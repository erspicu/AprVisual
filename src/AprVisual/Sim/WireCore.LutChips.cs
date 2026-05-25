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
        public static bool LutEnable74LS139 = false;   // currently broken — renders black; debug TODO
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
        private static void Setup74LS139(string prefix)
        {
            string Full(string n) => CombinePrefix(prefix, n);
            SetupHalfDecoder139(Full("1/E"), Full("1A0"), Full("1A1"),
                                new[] { Full("1/Y0"), Full("1/Y1"), Full("1/Y2"), Full("1/Y3") }, prefix, "1");
            SetupHalfDecoder139(Full("2/E"), Full("2A0"), Full("2A1"),
                                new[] { Full("2/Y0"), Full("2/Y1"), Full("2/Y2"), Full("2/Y3") }, prefix, "2");
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
