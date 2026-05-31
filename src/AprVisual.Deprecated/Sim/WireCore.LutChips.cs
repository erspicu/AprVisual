using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── LUT-replacement for pure-combinational TTL support chips on the NES motherboard.
        //    When --lut-ttl is set, the AddInstance path for these specific module types
        //    SKIPS the transistor instantiation (def.Trans) and instead registers a callback
        //    that computes outputs from inputs using a direct truth-table evaluation.
        //
        //    Eligible chips (all pure-combinational with no internal state):
        //      74HC04   ── hex inverter           Y = ~A
        //      74LS139  ── dual 2-to-4 decoder    when /E=0: /Yn = 0 iff (A1,A0)==n
        //      74LS368  ── hex inverter w/ tristate  /Y = ~A when /OE=0, else Z (SetFloat)
        //
        //    Pins, connections, segdefs, sub-modules — all kept as-is. Only the internal
        //    transistors are replaced. The pin nodes remain global IDs accessible to the
        //    rest of the system; downstream propagation goes through SetHigh/SetLow on
        //    the output pin nodes, fired by the callback when any input pin changes.
        //
        //    Correctness model: each LUT replacement must produce bit-identical NodeStates
        //    versus the transistor-level baseline. Verify via NodeStates checksum after
        //    bench-hc. If checksum differs, the LUT model is wrong (commonly: missing
        //    pull-up handling, or tristate/Z state not modeled correctly).

        public static bool EnableLutTtl = false;
        public static bool LutEnable74HC04 = true;
        // 74LS139 LUT is fundamentally INCOMPATIBLE with the AddCallback architecture for
        // CPU-bus-timing-critical paths — even monolithic (Gemini r4 fix), the callback fires
        // one wave AFTER the watched input change, so /ROMSEL settles a phase behind CPU clk0's
        // own cascade. Real CPUs latch the chip-select during the same propagation as clk0;
        // our event loop introduces a 1-wave delay that's invisible for HC04/LS368 (non-timing-
        // critical) but breaks the CPU's read window for the address decoder. Left off by default.
        public static bool LutEnable74LS139 = false;
        public static bool LutEnable74LS368 = true;
        public static int LutInstanceCount;
        public static long DiagDecoder139FireCount;
        public static long DiagDecoder139Half1Fires;
        public static long DiagDecoder139Half2Fires;

        // Per registered LUT sub-callback — needed to round-trip into the v4 snapshot
        // so the Rust port can dispatch the same behaviour without re-running the .js parser.
        public enum LutChipType : byte { Inverter = 0, Decoder2to4 = 1, TristateBuffer = 2 }
        public sealed class LutChipSpec
        {
            public LutChipType Type;
            public string Prefix = "";
            public string Tag = "";
            public int TargetNode;        // fake callback target node id (post-lowering)
            public int[] Inputs = Array.Empty<int>();
            public int[] Outputs = Array.Empty<int>();
            public int OeNode = -1;       // for TristateBuffer only
        }
        public static readonly List<LutChipSpec> RegisteredLutChips = new();

        internal static void ResetLutChips()
        {
            RegisteredLutChips.Clear();
            LutInstanceCount = 0;
        }

        internal static bool IsLutChip(string moduleName)
        {
            return (moduleName == "74HC04"  && LutEnable74HC04)
                || (moduleName == "74LS139" && LutEnable74LS139)
                || (moduleName == "74LS368" && LutEnable74LS368);
        }

        // Pending list — populated during AddInstance (before lowering renames node ids), drained
        // by AttachLutHandlers() called by LoadSystem AFTER lowering. Callback closures captured
        // here use the FINAL post-lowering node ids, same lifecycle as memory handlers.
        public static readonly List<(string ModuleName, string Prefix)> PendingLutInstances = new();

        // Called from AddInstance when EnableLutTtl && IsLutChip(def.Name). Defers actual callback
        // registration to AttachLutHandlers().
        internal static void DeferLutInstance(ModuleDef def, string prefix)
        {
            PendingLutInstances.Add((def.Name, prefix));
        }

        /// <summary>Drain PendingLutInstances and register the actual LUT callbacks. Must be called
        /// AFTER ComposeSystem (so lowering has finalised node ids), BEFORE ResetNes (so AddCallback's
        /// fake nodes/transistors are in place before Reset sizes the hot arrays).</summary>
        public static void AttachLutHandlers()
        {
            foreach (var (name, prefix) in PendingLutInstances)
            {
                switch (name)
                {
                    case "74HC04":   Setup74HC04(prefix);   break;
                    case "74LS139":  Setup74LS139(prefix);  break;
                    case "74LS368":  Setup74LS368(prefix);  break;
                    default: Console.Error.WriteLine($"LutChips: unknown module {name}"); continue;
                }
                LutInstanceCount++;
            }
            PendingLutInstances.Clear();
        }

        // ── 74HC04 hex inverter — 6 independent gates: nY = ~nA for n in 1..6 ──
        private static void Setup74HC04(string prefix)
        {
            string Full(string n) => CombinePrefix(prefix, n);
            for (int gi = 1; gi <= 6; gi++)
            {
                int a = LookupNode(Full($"{gi}A"));
                int y = LookupNode(Full($"{gi}Y"));
                if (a == EmptyNode || y == EmptyNode)
                {
                    Console.Error.WriteLine($"74HC04 {prefix}: missing pin {gi}A or {gi}Y");
                    continue;
                }
                int aCap = a, yCap = y;
                AddCallback(new[] { aCap }, () =>
                {
                    if (NodeStates[aCap] != 0) SetLow(yCap); else SetHigh(yCap);
                });
                // record for snapshot
                string cbName = "callback:" + GetNodeName(aCap);
                int tgt = FindCallbackTargetByName(cbName);
                RegisteredLutChips.Add(new LutChipSpec
                {
                    Type = LutChipType.Inverter, Prefix = prefix, Tag = $"{gi}",
                    TargetNode = tgt, Inputs = new[] { aCap }, Outputs = new[] { yCap },
                });
            }
        }

        // ── 74LS139 dual 2-to-4 decoder ──
        //    For each half: when /E=0, /Yn = 0 iff (A1,A0)==n; otherwise /Y0..3 = 1.
        //    When /E=1: /Y0..3 = 1 (chip disabled, all outputs high).
        //
        //    CASCADE DETECTION (per Gemini r4): if half-1's /E pin is electrically merged with
        //    one of half-2's /Y pins (via system-level always-on connection, observed after
        //    lowering as the same node id), the chip is in a CASCADED configuration and the two
        //    halves can't be handled by independent AddCallback's — the half-2 → half-1 cascade
        //    would take TWO callback phases (each costing one ProcessQueue), making half-1's
        //    outputs lag by 1-2 simulator phases. CPU then reads stale RAM/PPU CS during the
        //    same clk0 phase → black screen.
        //
        //    Fix: when cascade is detected, install ONE monolithic callback that computes
        //    half-2's sel, derives half-1's effective /E from that sel, computes half-1's sel,
        //    and writes ALL 8 output pins together via one RecalcNodeList. This guarantees
        //    /ROMSEL, WRAM_CS, PPU_CE settle in the same simulator phase as half-2's outputs.
        private static void Setup74LS139(string prefix)
        {
            string Full(string n) => CombinePrefix(prefix, n);
            int e1 = LookupNode(Full("1/E"));
            int y2_0 = LookupNode(Full("2/Y0"));
            int y2_1 = LookupNode(Full("2/Y1"));
            int y2_2 = LookupNode(Full("2/Y2"));
            int y2_3 = LookupNode(Full("2/Y3"));

            // post-lowering: if half-1's /E equals one of half-2's /Y pins, those nodes are
            // physically merged (same global id) → cascade configuration.
            int cascadeFromY = -1;
            if      (e1 == y2_0) cascadeFromY = 0;
            else if (e1 == y2_1) cascadeFromY = 1;
            else if (e1 == y2_2) cascadeFromY = 2;
            else if (e1 == y2_3) cascadeFromY = 3;

            if (cascadeFromY >= 0)
            {
                SetupMonolithicCascaded139(prefix, cascadeFromY);
            }
            else
            {
                SetupHalfDecoder139(Full("1/E"), Full("1A0"), Full("1A1"),
                                    new[] { Full("1/Y0"), Full("1/Y1"), Full("1/Y2"), Full("1/Y3") }, prefix, "1");
                SetupHalfDecoder139(Full("2/E"), Full("2A0"), Full("2A1"),
                                    new[] { Full("2/Y0"), Full("2/Y1"), Full("2/Y2"), Full("2/Y3") }, prefix, "2");
            }
        }

        // Monolithic decoder: one callback compute s both halves, knowing that half-1's /E
        // electrically follows half-2's /Y[cascadeFromY] (= 0 iff sel_2 == cascadeFromY).
        private static void SetupMonolithicCascaded139(string prefix, int cascadeFromY)
        {
            string Full(string n) => CombinePrefix(prefix, n);

            int e2 = LookupNode(Full("2/E"));
            int a0_2 = LookupNode(Full("2A0"));
            int a1_2 = LookupNode(Full("2A1"));
            int a0_1 = LookupNode(Full("1A0"));
            int a1_1 = LookupNode(Full("1A1"));

            var yHalf2 = new[] { LookupNode(Full("2/Y0")), LookupNode(Full("2/Y1")),
                                 LookupNode(Full("2/Y2")), LookupNode(Full("2/Y3")) };
            var yHalf1 = new[] { LookupNode(Full("1/Y0")), LookupNode(Full("1/Y1")),
                                 LookupNode(Full("1/Y2")), LookupNode(Full("1/Y3")) };

            // Watch all real inputs. Skip half-1's /E because it's electrically tied to a half-2
            // output (we derive its value, not read it).
            var watched = new System.Collections.Generic.List<int> { e2, a0_2, a1_2, a0_1, a1_1 };

            // allYs: 8 outputs, but half-1's /Y[cascadeFromY] coincides with half-1's /E (which is
            // the same merged node). Skip duplicate to avoid double-Recalc on one node.
            var allYsSet = new System.Collections.Generic.HashSet<int>();
            foreach (var n in yHalf2) allYsSet.Add(n);
            foreach (var n in yHalf1) allYsSet.Add(n);
            int[] allYs = new int[allYsSet.Count];
            allYsSet.CopyTo(allYs);

            int cascadeIdx = cascadeFromY;
            int e2Cap = e2, a0_2Cap = a0_2, a1_2Cap = a1_2;
            int a0_1Cap = a0_1, a1_1Cap = a1_1;
            var yH2 = yHalf2; var yH1 = yHalf1;

            AddCallback(watched, () =>
            {
                DiagDecoder139FireCount++;
                DiagDecoder139Half2Fires++;  // count once for monolithic

                // Half-2 (only enabled when 2/E = 0)
                int sel_2 = -1;
                if (NodeStates[e2Cap] == 0)
                {
                    sel_2 = (NodeStates[a1_2Cap] != 0 ? 2 : 0) | (NodeStates[a0_2Cap] != 0 ? 1 : 0);
                }

                // Half-1's effective /E = the new state of half-2's /Y[cascadeIdx]:
                //   0 (active) iff sel_2 == cascadeIdx, else 1 (disabled).
                int sel_1 = -1;
                if (sel_2 == cascadeIdx)
                {
                    sel_1 = (NodeStates[a1_1Cap] != 0 ? 2 : 0) | (NodeStates[a0_1Cap] != 0 ? 1 : 0);
                }

                // Set Flags atomically on all 8 outputs (with deduplication via allYsSet logic
                // already applied to allYs).
                for (int i = 0; i < 4; i++)
                {
                    ref NodeInfo ns = ref NodeInfos[yH2[i]];
                    if (i == sel_2) { ns.Flags &= ~NodeFlags.SetHigh; ns.Flags |= NodeFlags.SetLow; }
                    else            { ns.Flags &= ~NodeFlags.SetLow;  ns.Flags |= NodeFlags.SetHigh; }
                }
                for (int i = 0; i < 4; i++)
                {
                    ref NodeInfo ns = ref NodeInfos[yH1[i]];
                    if (i == sel_1) { ns.Flags &= ~NodeFlags.SetHigh; ns.Flags |= NodeFlags.SetLow; }
                    else            { ns.Flags &= ~NodeFlags.SetLow;  ns.Flags |= NodeFlags.SetHigh; }
                }
                RecalcNodeList(allYs);
            });

            // record for snapshot (Rust port). The "type" stays Decoder2to4 — Rust will need a
            // monolithic equivalent if we ever want cascaded LUT in Rust. For now mark with a
            // distinct Tag so Rust can detect + dispatch a monolithic compute.
            string cbName = "callback:" + string.Join(",",
                System.Linq.Enumerable.Select(watched, GetNodeName));
            int tgt = FindCallbackTargetByName(cbName);
            RegisteredLutChips.Add(new LutChipSpec
            {
                Type = LutChipType.Decoder2to4, Prefix = prefix, Tag = $"monolithic_cascade_y{cascadeIdx}",
                TargetNode = tgt,
                Inputs = new[] { e2, a0_2, a1_2, a0_1, a1_1, cascadeIdx },   // last field = cascade idx (encoded)
                Outputs = allYs,
            });
        }

        private static void SetupHalfDecoder139(string eName, string a0Name, string a1Name, string[] yNames,
                                                string prefix, string halfTag)
        {
            int e = LookupNode(eName);
            int a0 = LookupNode(a0Name);
            int a1 = LookupNode(a1Name);
            var y = new int[4];
            for (int i = 0; i < 4; i++) y[i] = LookupNode(yNames[i]);
            if (e == EmptyNode || a0 == EmptyNode || a1 == EmptyNode || Array.IndexOf(y, EmptyNode) >= 0)
            {
                Console.Error.WriteLine($"74LS139 {prefix}.{halfTag}: missing pin (e={e}, a0={a0}, a1={a1}, y={string.Join(",", y)})");
                return;
            }
            int eCap = e, a0Cap = a0, a1Cap = a1;
            int y0 = y[0], y1 = y[1], y2 = y[2], y3 = y[3];
            var watched = new[] { eCap, a0Cap, a1Cap };
            var allYs = new[] { y0, y1, y2, y3 };
            string halfTagCap = halfTag;
            AddCallback(watched, () =>
            {
                DiagDecoder139FireCount++;
                if (halfTagCap == "1") DiagDecoder139Half1Fires++;
                else DiagDecoder139Half2Fires++;
                // Compute the new state of all 4 outputs, then commit AS ONE BATCH so the cascade
                // (half-2 → half-1's E) doesn't see an intermediate "all high" transient.
                int selLow = -1;
                if (NodeStates[eCap] == 0)
                {
                    selLow = (NodeStates[a1Cap] != 0 ? 2 : 0) | (NodeStates[a0Cap] != 0 ? 1 : 0);
                }
                // Set Flags atomically on all 4, then RecalcNodeList once.
                for (int i = 0; i < 4; i++)
                {
                    ref NodeInfo ns = ref NodeInfos[allYs[i]];
                    if (i == selLow) { ns.Flags &= ~NodeFlags.SetHigh; ns.Flags |= NodeFlags.SetLow; }
                    else             { ns.Flags &= ~NodeFlags.SetLow;  ns.Flags |= NodeFlags.SetHigh; }
                }
                RecalcNodeList(allYs);
            });
            string cbName = "callback:" + string.Join(",", Array.ConvertAll(watched, GetNodeName));
            int tgt = FindCallbackTargetByName(cbName);
            RegisteredLutChips.Add(new LutChipSpec
            {
                Type = LutChipType.Decoder2to4, Prefix = prefix, Tag = halfTag,
                TargetNode = tgt,
                Inputs = new[] { eCap, a0Cap, a1Cap },   // [0]=E, [1]=A0, [2]=A1
                Outputs = new[] { y0, y1, y2, y3 },
            });
        }

        // ── 74LS368 hex inverter w/ tristate ──
        //    Group 1 (/1OE): 1A1..4 → /1Y1..4
        //    Group 2 (/2OE): 2A1..2 → /2Y1..2
        //    When /OE = 0: /Y = ~A (driving)
        //    When /OE = 1: /Y = Z (high impedance — SetFloat)
        private static void Setup74LS368(string prefix)
        {
            string Full(string n) => CombinePrefix(prefix, n);
            SetupTristateGroup368(Full("/1OE"),
                new[] { Full("1A1"), Full("1A2"), Full("1A3"), Full("1A4") },
                new[] { Full("/1Y1"), Full("/1Y2"), Full("/1Y3"), Full("/1Y4") },
                prefix, "1");
            SetupTristateGroup368(Full("/2OE"),
                new[] { Full("2A1"), Full("2A2") },
                new[] { Full("/2Y1"), Full("/2Y2") },
                prefix, "2");
        }

        private static void SetupTristateGroup368(string oeName, string[] aNames, string[] yNames,
                                                  string prefix, string groupTag)
        {
            int oe = LookupNode(oeName);
            int n = aNames.Length;
            var a = new int[n];
            var y = new int[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = LookupNode(aNames[i]);
                y[i] = LookupNode(yNames[i]);
            }
            if (oe == EmptyNode || Array.IndexOf(a, EmptyNode) >= 0 || Array.IndexOf(y, EmptyNode) >= 0)
            {
                Console.Error.WriteLine($"74LS368 {prefix}.group{groupTag}: missing pin");
                return;
            }
            int oeCap = oe;
            var aCap = (int[])a.Clone();
            var yCap = (int[])y.Clone();
            var watched = new List<int>(1 + n);
            watched.Add(oeCap);
            watched.AddRange(aCap);
            AddCallback(watched, () =>
            {
                if (NodeStates[oeCap] != 0)
                {
                    for (int i = 0; i < n; i++) SetFloat(yCap[i]);
                }
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (NodeStates[aCap[i]] != 0) SetLow(yCap[i]); else SetHigh(yCap[i]);
                    }
                }
            });
            string cbName = "callback:" + string.Join(",", watched.ConvertAll(GetNodeName));
            int tgt = FindCallbackTargetByName(cbName);
            RegisteredLutChips.Add(new LutChipSpec
            {
                Type = LutChipType.TristateBuffer, Prefix = prefix, Tag = groupTag,
                TargetNode = tgt, OeNode = oeCap,
                Inputs = aCap, Outputs = yCap,
            });
        }
    }
}
