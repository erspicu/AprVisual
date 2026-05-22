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
        internal static byte* IrAbsorbed;     // 1 = a pull-down mid folded into an IR island's Expr -> inert (P2.3 option B)
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

        // ── P2.2 it.2: unified combinational extraction (pure-logic + COMB_PASS-stack).
        //    A combinational node = gate-only singleton + pull-up + every pass-neighbour internal
        //    (no pull-up). Its value = NOT( exists conducting path to GND through the NMOS pull-down
        //    network ). PullDownCond builds that boolean by simple-path DFS: a direct GND channel gated
        //    by g contributes NodeRef(g); a pass channel gated by g to internal node mid contributes
        //    And(g, PullDownCond(mid)); all OR-ed. (Series = AND down the chain, parallel = OR.) Pass
        //    channels to a *driven* node (pull-up) are skipped — that's a bus/routing edge, not part of
        //    this gate's pull-down; if skipping it is wrong, --verify-ir flags the node for hybrid.
        private static int _irBudget;
        private static bool _irAbort;
        private static readonly HashSet<int> _irVisited = new();

        // A pass-neighbour is a genuine internal pull-down mid only if it (a) has no pull-up AND (b)
        // gates nothing — a real series pull-down element drives no logic. A node that GATES things
        // (e.g. a dynamic bus line ab14, read by the address decoders) is a driven signal, NOT this
        // gate's pull-down: folding it in would be a backwards drive direction (the stack node DRIVES
        // it). Excluding such nodes keeps them hybrid (correct) — this is the cheap drive-direction guard.
        private static bool IsInternalMid(int m) =>
            (uint)m < (uint)NodeCount && (NodeInfos[m].Flags & NodeFlags.PullUp) == 0 && NodeInfos[m].TlistGates == 0;

        private static bool AllPassInternal(int nn)
        {
            ref NodeInfo ns = ref NodeInfos[nn];
            if (ns.TlistC1c2s == 0) return true;
            int* p = TransistorList + ns.TlistC1c2s;
            while (*p != 0) { p++; int other = *p++; if (!IsInternalMid(other)) return false; }
            return true;
        }

        private static int Or2(int a, int b) => a < 0 ? b : AddIr(ExprOp.Or, a, b);

        // Returns expr idx for "v has a conducting path to GND", or -1 if none. _irAbort set if budget blown.
        private static int PullDownCond(int v)
        {
            if (_irAbort) return -1;
            if (--_irBudget < 0) { _irAbort = true; return -1; }
            ref NodeInfo ns = ref NodeInfos[v];
            int orRoot = -1;

            if (ns.TlistC1gnd != 0)   // direct channels to GND: transistor g conducting => path to GND
            {
                int* p = TransistorList + ns.TlistC1gnd;
                while (*p != 0) { int g = *p++; orRoot = Or2(orRoot, AddIr(ExprOp.NodeRef, g)); }
            }
            if (ns.TlistC1c2s != 0)   // pass channels to internal nodes: g AND (mid reaches GND)
            {
                int* p = TransistorList + ns.TlistC1c2s;
                while (*p != 0)
                {
                    int g = *p++;
                    int mid = *p++;
                    if (_irVisited.Contains(mid)) continue;                                  // simple paths only (cycle guard)
                    if (!IsInternalMid(mid)) continue;                                       // only recurse through genuine pull-down mids (no pull-up, gates nothing)
                    _irVisited.Add(mid);
                    int sub = PullDownCond(mid);
                    _irVisited.Remove(mid);
                    if (_irAbort) return -1;
                    if (sub >= 0) orRoot = Or2(orRoot, AddIr(ExprOp.And, AddIr(ExprOp.NodeRef, g), sub));
                }
            }
            return orRoot;
        }

        /// <summary>P2.3 (option B): extract Expr per pass-connected ISLAND. An island is a maximal set
        /// of nodes joined by pass transistors (TlistC1c2s) — i.e. exactly the nodes that can ever share
        /// a conducting group. We only IR an island that is a clean single combinational gate: exactly
        /// ONE pull-up node v (the output) + every other member a pure pull-down mid (no pull-up, gates
        /// nothing), and v is a gate-only singleton (no sequential feedback). Then v's value =
        /// NOT(PullDownCond over the island), and the mids are ABSORBED (marked inert: never
        /// RecalcNode'd, never group-walked). Because an island is a closed pass-component, an IR output
        /// is connected to the rest of the chip ONLY by gating (its value drives via fanout, never via a
        /// shared group) — so no IR node ever shares a group with a hybrid node. No IR/hybrid interop =
        /// the P2.3 freeze bug is structurally impossible.</summary>
        internal static void BuildCombinationalIr()
        {
            int n = NodeCount;
            _ir = new List<IrNode>(1 << 18);
            IrRoot = new int[n];
            Array.Fill(IrRoot, -1);
            _irConst1 = AddIr(ExprOp.Const1, 0);
            IrAbsorbed = AllocArray<byte>(n);   // zeroed; freed in FreeUnmanagedMemory

            // ── pass-connected components (union-find over conducting-capable normal-normal channels) ──
            int[] uf = new int[n];
            for (int i = 0; i < n; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) uf[ra] = rb; }
            foreach (var t in Transistors)
            {
                if (t.Gate == Ngnd) continue;
                bool c1s = t.C1 == Npwr || t.C1 == Ngnd, c2s = t.C2 == Npwr || t.C2 == Ngnd;
                if (!c1s && !c2s) Union(t.C1, t.C2);
            }
            var members = new Dictionary<int, List<int>>();
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                int r = Find(nn);
                (members.TryGetValue(r, out var l) ? l : (members[r] = new List<int>())).Add(nn);
            }

            // ── gate-only SCC (singleton = combinational, no sequential feedback) ──
            int[] comp = TarjanScc(BuildDepAdj(n, includeChannel: false), n, out int cc);
            int[] sccSize = new int[cc];
            for (int i = 0; i < n; i++) if (comp[i] >= 0) sccSize[comp[i]]++;
            const NodeFlags badOut = NodeFlags.HasCallback | NodeFlags.ForceCompute | NodeFlags.Pwr | NodeFlags.Gnd;

            int nLogic = 0, nStack = 0, nAbsorbed = 0, nComplex = 0, nReject = 0;
            foreach (var kv in members)
            {
                var list = kv.Value;
                // find the single pull-up output; every other member must be a pure pull-down mid
                int outV = -1; int pulls = 0; bool clean = true;
                foreach (int m in list)
                {
                    if ((NodeInfos[m].Flags & NodeFlags.PullUp) != 0) { pulls++; outV = m; }
                    else if (NodeInfos[m].TlistGates != 0) { clean = false; break; }   // a mid that drives logic => not a pure stack
                }
                if (!clean || pulls != 1) { nReject++; continue; }
                ref NodeInfo ns = ref NodeInfos[outV];
                if ((ns.Flags & badOut) != 0) { nReject++; continue; }
                if (!(comp[outV] >= 0 && sccSize[comp[outV]] == 1)) { nReject++; continue; }   // output must be combinational
                if (IrPureOnly && list.Count > 1) { nReject++; continue; }                     // debug: pure-logic islands only

                _irVisited.Clear(); _irVisited.Add(outV);
                _irBudget = 4000; _irAbort = false;
                int cond = PullDownCond(outV);
                if (_irAbort) { nComplex++; continue; }
                IrRoot[outV] = cond < 0 ? _irConst1 : AddIr(ExprOp.Not, cond);
                if (list.Count > 1) nStack++; else nLogic++;
                foreach (int m in list) if (m != outV) { IrAbsorbed[m] = 1; nAbsorbed++; }
            }
            IrExtractedCount = nLogic + nStack;
            LastIrStats = $"IR (P2.3 island): {IrExtractedCount:N0} extracted ({nLogic:N0} logic + {nStack:N0} multi-node islands), {nAbsorbed:N0} mids absorbed, {nComplex:N0} too-complex, {nReject:N0} islands rejected->hybrid, pool {_ir.Count:N0}";
        }

        // ── P2.3: event-driven IR interpreter. Keeps S1's dirty-queue/double-buffer; only RecalcNode
        //    (eval) and SetNodeState (propagation) change for IR nodes. revDep[g] = the IR nodes whose
        //    Expr references node g — so when g changes, exactly those IR nodes are re-enqueued. Hybrid
        //    nodes keep S1's group-walk + gate-fanout. EVENT-DRIVEN (dirty-set sparsity preserved).
        public static bool EnableIrInterp = false;
        public static bool IrPureOnly = false;       // debug: extract only pure-logic (no pass/stack) — isolate dispatch bugs
        public static bool IrBoundaryDriver = false; // experiment: treat IR nodes as group-walk driver boundaries (vs let walks resolve them in-group)
        public static bool IrBruteForce = false;     // debug: re-eval ALL nodes each half-cycle (oblivious) — isolate triggering bugs from eval bugs

        /// <summary>Debug: after the normal settle, force a full re-evaluation of every live node to
        /// fixpoint. If this makes --ir-interp match S1, the event-driven triggering (revDep) is
        /// incomplete (eval/extraction are fine — that's already proven by --verify-ir).</summary>
        internal static void ReEvalAllIr()
        {
            for (int pass = 0; pass < 12; pass++)
            {
                for (int nn = 0; nn < NodeCount; nn++)
                    if (nn != Npwr && nn != Ngnd && Nodes[nn] != null) EnqueueNode(nn);
                ProcessQueue();
            }
        }
        internal static int* _revDepStart;   // CSR: revDep consumers of node g are _revDepList[start[g]..start[g+1])
        internal static int* _revDepList;
        public static string LastRevDepStats = "(revDep not built)";

        private static void CollectRefs(int idx, HashSet<int> into)
        {
            IrNode e = _ir![idx];
            switch (e.Op)
            {
                case ExprOp.NodeRef: case ExprOp.Hold: into.Add(e.A); break;
                case ExprOp.Not: CollectRefs(e.A, into); break;
                case ExprOp.Or: case ExprOp.And: CollectRefs(e.A, into); CollectRefs(e.B, into); break;
                case ExprOp.Mux: CollectRefs(e.A, into); CollectRefs(e.B, into); CollectRefs(e.C, into); break;
                default: break;
            }
        }

        /// <summary>Build the reverse-dependency map (node -> IR nodes whose Expr references it), CSR
        /// form, for the interpreter's propagation. Run after BuildCombinationalIr.</summary>
        internal static void BuildRevDep()
        {
            int n = NodeCount;
            var tmp = new List<int>?[n];
            var refs = new HashSet<int>();
            long edges = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (IrRoot == null || IrRoot[nn] < 0) continue;
                refs.Clear();
                CollectRefs(IrRoot[nn], refs);
                foreach (int g in refs)
                {
                    if ((uint)g >= (uint)n) continue;
                    (tmp[g] ??= new List<int>()).Add(nn);
                    edges++;
                }
            }
            _revDepStart = AllocArray<int>(n + 1);
            _revDepList = AllocArray<int>((int)Math.Max(edges, 1));
            int pos = 0;
            for (int g = 0; g < n; g++)
            {
                _revDepStart[g] = pos;
                var l = tmp[g];
                if (l != null) foreach (int c in l) _revDepList[pos++] = c;
            }
            _revDepStart[n] = pos;
            LastRevDepStats = $"revDep: {edges:N0} (node -> IR-consumer) edges";
        }

        /// <summary>Enqueue the IR nodes that reference <paramref name="nn"/> (called from SetNodeState in
        /// interp mode). Inlined-friendly; no-op when nn drives no IR node.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static void EnqueueIrConsumers(int nn)
        {
            int s = _revDepStart[nn], e = _revDepStart[nn + 1];
            for (int k = s; k < e; k++) EnqueueNode(_revDepList[k]);
        }

        /// <summary>Debug: dump an IR node's Expr refs + verify it appears in each referenced node's
        /// revDep (is the event-driven trigger complete for this node?).</summary>
        internal static void DumpIrNodeInfo(int nn)
        {
            Console.WriteLine($"# node {nn} '{GetNodeName(nn)}' class={IrClassOf(nn)} root={(IrRoot != null ? IrRoot[nn] : -1)}");
            if (IrRoot == null || IrRoot[nn] < 0) { Console.WriteLine("#   (not extracted)"); return; }
            var refs = new HashSet<int>();
            CollectRefs(IrRoot[nn], refs);
            Console.WriteLine($"#   Expr references {refs.Count} nodes:");
            foreach (int g in refs)
            {
                bool inRev = false;
                if (_revDepStart != null) { for (int k = _revDepStart[g]; k < _revDepStart[g + 1]; k++) if (_revDepList[k] == nn) { inRev = true; break; } }
                Console.WriteLine($"#     gate {g} '{GetNodeName(g)}' state={NodeStates[g]} class={IrClassOf(g)} -> nn in revDep[gate]? {inRev}");
            }
        }

        /// <summary>Debug: "IR" if the node has an extracted Expr, else "hy" (hybrid/switch-level).</summary>
        internal static string IrClassOf(int nn) => (IrRoot != null && (uint)nn < (uint)NodeCount && IrRoot[nn] >= 0) ? "IR" : "hy";

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
        internal static (long checks, long mism) VerifyIrOnce(int[]? badPerNode = null)
        {
            long c = 0, m = 0;
            if (IrRoot == null) return (0, 0);
            for (int nn = 0; nn < NodeCount; nn++)
            {
                int r = IrRoot[nn];
                if (r < 0) continue;
                c++;
                if (EvalExpr(r) != NodeStates[nn]) { m++; if (badPerNode != null) badPerNode[nn]++; }
            }
            return (c, m);
        }
    }
}
