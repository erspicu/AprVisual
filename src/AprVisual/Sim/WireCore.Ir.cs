using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Phase 2 (CPU/event-driven IR) — the Expr IR.
        //
        //    Goal: each node -> a *directed* next-state boolean expression over its driver nodes, so the
        //    engine can re-evaluate a cheap precompiled Expr (event-driven, on input change) instead of
        //    walking a conducting group. This is what dissolves the full-graph 94.5% pass-coupling SCC
        //    (--dump-levels): the IR picks a drive direction per node. EVENT-DRIVEN, not main's batch.
        //
        //    P2.2 iteration 1 (this file, for now): build + VERIFY the Expr infrastructure on the subset
        //    that is already PROVEN correct as a boolean function — the pure-logic-gnd nodes (策略二's
        //    IsPureLogic: pull-up + no pass channel + no VCC channel + only GND channels). For those,
        //    ComputeNodeGroup is provably == NOT(OR of the GND-channel gate nodes) (the --fast-path
        //    bit-identical result is exactly this), so extracting Not(Or(NodeRef gi)) is guaranteed
        //    right. Verifying EvalExpr == NodeStates over a run therefore validates the POOL + EVALUATOR
        //    (not the model). Later iterations extend extraction to COMB_PASS (drive-direction analysis)
        //    where the verify actually tests the model.
        //
        //    Representation note: a managed pool + recursive eval is used here for clarity during
        //    extraction/verification. The P2.3 event-driven interpreter will lower this to a flat
        //    unmanaged form for speed; correctness first.

        public enum ExprOp : byte { Const0, Const1, NodeRef, Not, Or, And, Mux, Hold }

        public struct IrNode
        {
            public ExprOp Op;
            public int A;   // NodeRef: node id; Not: child; Or/And: left child; Mux: select; Hold: node id
            public int B;   // Or/And: right child; Mux: then-child
            public int C;   // Mux: else-child
        }

        private static List<IrNode>? _ir;     // expr pool (managed; verification representation)
        internal static int[]? IrRoot;        // per node: root expr index, or -1 if not extracted
        public static int IrExtractedCount;
        public static string LastIrStats = "(IR not built)";
        private static int _irConst1;

        private static int AddIr(ExprOp op, int a, int b = 0, int c = 0)
        {
            _ir!.Add(new IrNode { Op = op, A = a, B = b, C = c });
            return _ir.Count - 1;
        }

        /// <summary>P2.2 it.1: extract Not(Or(GND-gate NodeRefs)) for every proven-pure-logic node
        /// (requires --fast-path / IsPureLogic populated). Const1 if a node has no GND channel (pull-up
        /// only → always 1). This subset's Expr is guaranteed == S1's group resolution.</summary>
        internal static void BuildPureLogicIr()
        {
            _ir = new List<IrNode>(1 << 16);
            IrRoot = new int[NodeCount];
            Array.Fill(IrRoot, -1);
            _irConst1 = AddIr(ExprOp.Const1, 0);

            if (IsPureLogic == null)
            {
                LastIrStats = "IR: IsPureLogic not populated (need --fast-path) — extracted 0";
                IrExtractedCount = 0;
                return;
            }

            int extracted = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (IsPureLogic[nn] == 0) continue;
                ref NodeInfo ns = ref NodeInfos[nn];

                int orRoot = -1;
                if (ns.TlistC1gnd != 0)
                {
                    int* p = TransistorList + ns.TlistC1gnd;
                    while (*p != 0)
                    {
                        int g = *p++;
                        int leaf = AddIr(ExprOp.NodeRef, g);
                        orRoot = orRoot < 0 ? leaf : AddIr(ExprOp.Or, orRoot, leaf);
                    }
                }
                IrRoot[nn] = orRoot < 0 ? _irConst1 : AddIr(ExprOp.Not, orRoot);
                extracted++;
            }
            IrExtractedCount = extracted;
            LastIrStats = $"IR (P2.2 it.1, pure-logic only): {extracted:N0} nodes extracted, {_ir.Count:N0} expr nodes in pool";
        }

        /// <summary>Evaluate an expr (reads current settled NodeStates for NodeRef leaves).</summary>
        internal static byte EvalExpr(int idx)
        {
            IrNode e = _ir![idx];
            switch (e.Op)
            {
                case ExprOp.Const0: return 0;
                case ExprOp.Const1: return 1;
                case ExprOp.NodeRef: return NodeStates[e.A];
                case ExprOp.Not: return (byte)(EvalExpr(e.A) == 0 ? 1 : 0);
                case ExprOp.Or: return (byte)((EvalExpr(e.A) | EvalExpr(e.B)) != 0 ? 1 : 0);
                case ExprOp.And: return (byte)((EvalExpr(e.A) & EvalExpr(e.B)) != 0 ? 1 : 0);
                case ExprOp.Mux: return EvalExpr(e.A) != 0 ? EvalExpr(e.B) : EvalExpr(e.C);
                case ExprOp.Hold: return NodeStates[e.A];   // placeholder (dynamic hold) — refined later
                default: return 0;
            }
        }

        /// <summary>Check every extracted node's Expr against the current (settled) NodeStates.
        /// Returns (#checks, #mismatches). For the pure-logic subset this must be 0 mismatches.</summary>
        internal static (long checks, long mism) VerifyIrOnce()
        {
            long c = 0, m = 0;
            if (IrRoot == null) return (0, 0);
            for (int nn = 0; nn < NodeCount; nn++)
            {
                int r = IrRoot[nn];
                if (r < 0) continue;
                c++;
                if (EvalExpr(r) != NodeStates[nn]) m++;
            }
            return (c, m);
        }
    }
}
