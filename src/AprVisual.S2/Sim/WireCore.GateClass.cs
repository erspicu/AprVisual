using System;
using System.Collections.Generic;
using System.Text;

namespace AprVisual.Sim
{
    // ── S2 gate-level CLASSIFICATION (load-time, diagnostic / abstraction foundation) ──────────────────
    //
    // The S2 charter wants every node tagged with (1) a gate-class and (2) a digital-vs-analog attribute,
    // and the switch-level netlist abstracted into recognizable logic gates. This pass is the foundation:
    // it walks the composed netlist once and assigns each live node a GateClass, then summarizes the
    // digital-ness breakdown. It does NOT change the engine — it is the gate-level VIEW + the input the
    // export (WireCore.GateExport.cs) and any fold step consume. Invoked via TestRunner --gate-classify.
    //
    // The three digital-ness buckets connect back to the project's Escape-1 headline (~98.9% of activity
    // reduces to logic + registers, ~1.1% genuine analog):
    //   • Static logic  — pull-up depletion-load gate outputs (NOR / inverter / series AOI). Clean booleans.
    //   • Dynamic digital — no own pull-up but behaviorally a 0/1: registers/latches (charge-held bits),
    //                       precharge/dynamic-logic nodes, handler-driven bus pins. Digital VALUE, dynamic HOLD.
    //   • Analog        — value decided by the switch-level strength / capacitance tie-break or a Gnd+Pwr
    //                     cancel (bus charge-sharing, ForceCompute) — the irreducible switch-level core.
    internal static unsafe partial class WireCore
    {
        public enum GateClass : byte
        {
            Unknown = 0,
            Supply,           // VCC / GND rails
            Inverter,         // pull-up + exactly ONE direct-GND pull-down, no pass channel (1-input NOR)
            NorGate,          // pull-up + >=2 direct-GND pull-downs, no pass channel (parallel NOR)
            SeriesGate,       // pull-up + a pass channel behind the pull-down (NAND / AOI; has internal nodes)
            DynamicStorage,   // no rail of its own, written via a pass channel, READ (gates >=1) = latch/register/RAM bit
            SupplyDrivenDyn,  // no pull-up but a direct gnd/pwr channel (precharge / dynamic-logic node)
            BusFabric,        // no pull-up, gates nothing, in a LARGE channel-connected component (bus / decoder net)
            PassWaypoint,     // no pull-up, gates nothing, small component (pure routing internal)
            Driven,           // handler-driven pin (RAM/ROM/bus DataOut) — external behavioral source
            Special,          // ForceCompute (Gnd+Pwr cancel) / HasCallback (watched) — special resolution
        }

        // Digital-ness bucket for a class (for the 3-way summary).
        private enum DigitalKind : byte { StaticLogic, DynamicDigital, Analog, Rail }

        private static DigitalKind KindOf(GateClass g) => g switch
        {
            GateClass.Inverter or GateClass.NorGate or GateClass.SeriesGate => DigitalKind.StaticLogic,
            GateClass.DynamicStorage or GateClass.SupplyDrivenDyn or GateClass.Driven => DigitalKind.DynamicDigital,
            GateClass.BusFabric or GateClass.PassWaypoint or GateClass.Special => DigitalKind.Analog,
            _ => DigitalKind.Rail,
        };

        private const int BigCccThreshold = 32;   // a "bus / decoder" net (matches Estimator 2's large-component threshold)

        // Shared classification context: handler-driven pins, channel-connected-component sizes (union-find
        // over c1c2). Built once, consumed by ClassifyGate. (Same construction as Estimator 2 / ClassifyPruneTaint.)
        internal sealed class GateCtx
        {
            public HashSet<int> Driven = new();
            public int[] Parent = Array.Empty<int>();
            public Dictionary<int, int> CompSize = new();
        }

        internal static GateCtx BuildGateCtx()
        {
            int n = NodeCount;
            var ctx = new GateCtx();
            foreach (var cb in _callbacks)
                if (cb.DataOut != null)
                    for (int i = 0; i < cb.DLen; i++) ctx.Driven.Add(cb.DataOut[i]);

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) FcuUnion(parent, nn, pay[k + 1]);
                }
                else if (ns->TlistC1c2s != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;
                    while (*p != 0) { FcuUnion(parent, nn, *(p + 1)); p += 2; }
                }
            }
            ctx.Parent = parent;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                bool hasPass = ns->Inline != 0 ? (ns->C1c2Count != 0) : (ns->TlistC1c2s != 0);
                if (!hasPass) continue;
                int r = FcuFind(parent, nn);
                ctx.CompSize.TryGetValue(r, out int c); ctx.CompSize[r] = c + 1;
            }
            return ctx;
        }

        // Count GND pull-down transistors of a node (its fan-in, for NOR vs inverter).
        private static int GndFanin(NodeInfo* ns)
        {
            if (ns->Inline != 0) return ns->GndCount;
            if (ns->TlistC1gnd == 0) return 0;
            ushort* p = TransistorList + ns->TlistC1gnd; int c = 0;
            while (*p != 0) { c++; p++; }   // c1gnd list is a flat (gate,...,0) — one ushort per transistor
            return c;
        }

        // Classify one LIVE node (Nodes[nn] != null). Rails return Supply.
        internal static GateClass ClassifyGate(int nn, GateCtx ctx)
        {
            if (nn == Npwr || nn == Ngnd) return GateClass.Supply;
            NodeInfo* ns = NodeInfos + nn;
            const NodeFlags fcCb = NodeFlags.ForceCompute | NodeFlags.HasCallback;
            bool hasPullUp = (ns->Flags & NodeFlags.PullUp) != 0;
            bool hasPass   = ns->Inline != 0 ? (ns->C1c2Count != 0) : (ns->TlistC1c2s != 0);
            bool hasSupply = ns->Inline != 0 ? (ns->GndCount != 0 || ns->PwrCount != 0) : (ns->TlistC1gnd != 0 || ns->TlistC1pwr != 0);
            bool gatesAny  = NodeTlistGates[nn] != 0;
            bool special   = (ns->Flags & fcCb) != 0;

            if (special) return GateClass.Special;
            if (ctx.Driven.Contains(nn)) return GateClass.Driven;
            if (hasPullUp)
            {
                if (hasPass) return GateClass.SeriesGate;
                return GndFanin(ns) <= 1 ? GateClass.Inverter : GateClass.NorGate;
            }
            if (hasSupply) return GateClass.SupplyDrivenDyn;
            if (hasPass)
            {
                if (gatesAny) return GateClass.DynamicStorage;
                ctx.CompSize.TryGetValue(FcuFind(ctx.Parent, nn), out int sz);
                return sz >= BigCccThreshold ? GateClass.BusFabric : GateClass.PassWaypoint;
            }
            return GateClass.Unknown;   // no pull-up, no supply, no pass, no flags — isolated/floating stub
        }

        public static string GateClassify()
        {
            int n = NodeCount;
            var ctx = BuildGateCtx();
            var classCount = new long[Enum.GetValues<GateClass>().Length];
            var kindCount = new long[4];
            long faninSum = 0, norGates = 0; int maxFanin = 0;

            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null) continue;
                GateClass g = ClassifyGate(nn, ctx);
                classCount[(int)g]++;
                kindCount[(int)KindOf(g)]++;
                if (g == GateClass.NorGate || g == GateClass.Inverter)
                {
                    int fanin = GndFanin(NodeInfos + nn);
                    faninSum += fanin; norGates++; if (fanin > maxFanin) maxFanin = fanin;
                }
            }

            long live = 0; for (int nn = 0; nn < n; nn++) if (Nodes[nn] != null && nn != Npwr && nn != Ngnd) live++;
            double P(long x) => live == 0 ? 0 : 100.0 * x / live;
            var sb = new StringBuilder();
            sb.Append("# ===== S2 gate-level classification =====\n");
            sb.Append($"#  live nodes (excl. rails): {live:N0}\n");
            sb.Append("#  --- by gate class ---\n");
            foreach (GateClass g in Enum.GetValues<GateClass>())
            {
                if (g == GateClass.Supply) continue;   // rails aren't part of the live count
                long c = classCount[(int)g];
                if (c == 0) continue;
                sb.Append($"#    {g,-16} {c,7:N0}  ({P(c):F1}%)\n");
            }
            sb.Append("#  --- digital-ness (Escape-1 buckets) ---\n");
            sb.Append($"#    static logic  (NOR/INV/series gate outputs):        {kindCount[(int)DigitalKind.StaticLogic],7:N0}  ({P(kindCount[(int)DigitalKind.StaticLogic]):F1}%)\n");
            sb.Append($"#    dynamic digital (registers/latches/precharge/driven): {kindCount[(int)DigitalKind.DynamicDigital],6:N0}  ({P(kindCount[(int)DigitalKind.DynamicDigital]):F1}%)\n");
            sb.Append($"#    ANALOG (bus charge-share / ForceCompute / floating):  {kindCount[(int)DigitalKind.Analog],7:N0}  ({P(kindCount[(int)DigitalKind.Analog]):F1}%)  ← irreducible switch-level core\n");
            long digitalTotal = kindCount[(int)DigitalKind.StaticLogic] + kindCount[(int)DigitalKind.DynamicDigital];
            sb.Append($"#    => digital (logic + registers) = {P(digitalTotal):F1}%   analog = {P(kindCount[(int)DigitalKind.Analog]):F1}%\n");
            sb.Append($"#  --- NOR/inverter fan-in --- gates={norGates:N0}  mean fan-in={(norGates == 0 ? 0 : (double)faninSum / norGates):F2}  max={maxFanin}\n");
            sb.Append("# ========================================");
            return sb.ToString();
        }
    }
}
