using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  S2 IR — Phase A foundation (CPU-first, event-driven, hybrid).
    //
    //  Design: MD/S2/design/04_s2_ir_design_cpu_first.md. Honest ceiling (Gemini + the
    //  math-algos break-even result): a per-node IR INTERPRETER cannot BEAT the ~80K S1
    //  (S1's singleton fast-path is already near the instruction floor). The real speedup
    //  needs Phase-B macro-block CODEGEN. Phase A builds the IR machinery + a hybrid dispatch,
    //  validated bit-exact, perf-neutral (break-even) — the foundation the codegen will reuse.
    //
    //  COVERAGE (provably-correct safe subset): static pure-logic nodes (IsPureLogic==1:
    //  provably-singleton, no normal c1c2 channel) that ALSO have a pull-up and are pulled
    //  down ONLY through gates to GND (no PWR channel, no normal channel, no special flags).
    //  For such a node S1 resolves: any GND-gate ON -> 0, else pull-up -> 1. That is exactly
    //  NOT(OR gnd-gates) — a pure boolean of the gate states -> a precomputed truth-table LUT.
    //  This is bit-exact to S1's RecalcNodeFast BY CONSTRUCTION for this subset; the whole-NES
    //  NodeStates checksum is the end-to-end gate (must stay 0x794A43ABDF169ADA).
    //
    //  (Same numeric result as RecalcNodeFast for these nodes — the deliverable is the verified
    //  IR REPRESENTATION + dispatch infra, not a speedup. The speedup is Phase-B codegen.)
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        // IR dispatch class — folded into IsPureLogic[] so RecalcNode routes it via the existing
        // dispatch with no extra per-recalc branch for non-IR nodes. IrCls => evaluate via EvalIr.
        internal const byte IrCls = 3;

        public static bool EnableIr = false;        // --ir

        // Per-IR-node truth table over its GND-gates. IrGateBase[nn] -> IrGateList[..] (K gate node
        // ids then a 0 terminator). IrLutBase[nn] -> IrLut[.. 2^K bytes]. K capped at IrMaxK.
        internal const int IrMaxK = 12;            // 2^12 = 4 KB max table; pure-logic K is tiny (avg <2)
        internal static int* IrGateBase;           // index into IrGateList (0 = none)
        internal static ushort* IrGateList;        // flat: (gate, gate, ..., 0) per IR node
        internal static int* IrLutBase;            // index into IrLut
        internal static byte* IrLut;               // flat truth tables, 2^K bytes per IR node

        public static int IrNodeCount;
        public static string LastIrStats = "(IR disabled)";

        /// <summary>Build + enable IR for the clean pure-logic subset. Called at the end of Reset
        /// (after ClassifyPureLogicNodes, while the managed Node graph is alive). Marks survivors
        /// IsPureLogic[nn]=IrCls so RecalcNode dispatches them to EvalIr. Bit-exactness is verified
        /// end-to-end by the whole-NES checksum.</summary>
        internal static void BuildIr()
        {
            IrNodeCount = 0;
            if (!EnableIr) { LastIrStats = "(IR disabled)"; return; }

            var gateList = new List<ushort> { 0 };   // index 0 = sentinel/empty
            var lut = new List<byte> { 0 };
            IrGateBase = AllocArray<int>(NodeCount);
            IrLutBase  = AllocArray<int>(NodeCount);

            var gnd = new List<int>();
            int cand = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (nn == Npwr || nn == Ngnd) continue;
                Node? node = Nodes[nn];
                if (node == null) continue;
                ref NodeInfo ns = ref NodeInfos[nn];
                if (IsPureLogic[nn] != 1) continue;                       // only static pure-logic (provably-singleton)
                if ((ns.Flags & NodeFlags.PullUp) == 0) continue;         // need a pull-up (so f is never None -> no hold-prev)
                if ((ns.Flags & (NodeFlags.HasCallback | NodeFlags.ForceCompute | NodeFlags.Pwr | NodeFlags.Gnd)) != 0) continue;

                // Pull-down GND gates only; reject any PWR / normal channel (not the clean subset).
                gnd.Clear();
                bool ok = true;
                foreach (int tid in node.C1c2s)
                {
                    var t = Transistors[tid];
                    if (t.Gate == Ngnd) continue;                          // never conducts
                    int other = t.C1 == nn ? t.C2 : t.C1;
                    if (other == Ngnd) gnd.Add(t.Gate);
                    else { ok = false; break; }                            // PWR or normal channel -> skip
                }
                if (!ok) continue;
                int k = gnd.Count;
                if (k == 0 || k > IrMaxK) continue;                        // k==0: constant, not worth; too big: skip

                int gbase = gateList.Count;
                for (int i = 0; i < k; i++) gateList.Add((ushort)gnd[i]);
                gateList.Add(0);
                int lbase = lut.Count;
                int n = 1 << k;
                for (int idx = 0; idx < n; idx++) lut.Add((byte)(idx == 0 ? 1 : 0));   // NOT(any gnd-gate ON)

                IrGateBase[nn] = gbase;
                IrLutBase[nn]  = lbase;
                IsPureLogic[nn] = IrCls;                                    // enable IR dispatch for this node
                cand++;
            }

            IrGateList = AllocArray<ushort>(gateList.Count);
            for (int i = 0; i < gateList.Count; i++) IrGateList[i] = gateList[i];
            IrLut = AllocArray<byte>(lut.Count);
            for (int i = 0; i < lut.Count; i++) IrLut[i] = lut[i];

            IrNodeCount = cand;
            double pct = NonNullNodeCount > 0 ? 100.0 * cand / NonNullNodeCount : 0;
            LastIrStats = $"IR: {cand:N0} pure-logic nodes -> truth-table LUT ({pct:F1}% of live), lut bytes {lut.Count:N0}";
        }

        /// <summary>Evaluate an IR node's value via its truth-table LUT: read its GND-gate states,
        /// pack the index, table lookup. Bit-exact to NOT(any gnd-gate ON) for the clean subset.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte EvalIr(int nn)
        {
            ushort* g = IrGateList + IrGateBase[nn];
            int idx = 0, bit = 0;
            while (*g != 0) { idx |= NodeStates[*g++] << bit; bit++; }
            return IrLut[IrLutBase[nn] + idx];
        }
    }
}
