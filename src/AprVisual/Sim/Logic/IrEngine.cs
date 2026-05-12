using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim.Logic
{
    // ── S2.4: run the extracted IR (nextExpr[N] from S2.0–S2.3) alongside S1's switch-level engine.
    //    First cut = "checking mode" (the S2.6 equivalence gate): each StepOne() snapshots NodeStates
    //    (the previous half-cycle's settled state — what Hold/Prev read), runs WireCore.Step(1) — which,
    //    since S1's handlers are "Case B" (SetHigh/SetLow call RecalcNodeList → ProcessQueue immediately),
    //    toggles the clock + services memory + settles the *whole* chip for this half-cycle — then for
    //    every IR-covered node v verifies  EvalExpr(nextExpr[v]) == NodeStates[v]  (NodeRef reads
    //    NodeStates = S1's this-half-cycle settled values; Hold/Prev read the snapshot). Mismatches are
    //    recorded, not overwritten — this directly proves the IR's logic agrees with S1's group-BFS, per
    //    node, per half-cycle. The topo-sort / Tarjan-residual-SCC / hybrid-bridge ("driving mode" — the IR
    //    actually drives the netlist, for the speed-up) is deferred (S3 / a later S2.4 firing). Does NOT
    //    change S1's behaviour. See MD/impl/S2/05_S2.4_IR直譯器_設計.md (incl. the Gemini review).

    internal static unsafe class IrEngine
    {
        public static Expr?[] NextExpr = [];     // [nodeId]; null = hybrid / Supply / Input — not IR-covered (S1 owns it)
        public static bool[] IsSequential = [];
        public static bool[] Hybrid = [];
        public static bool[] CheckInChecking = []; // [nodeId]; false = skip the IR-vs-S1 check (an un-modelled internal placeholder — nextExpr == Hold(self), Gates.Count == 0, unnamed)
        public static byte[] PrevStates = [];    // [nodeId]; snapshot of NodeStates at the start of the current half-cycle
        public static bool Built;

        public static int IrCoveredCount;        // # of nodes with NextExpr != null (checking-mode coverage)
        public static int CheckedCount;          // # of nodes actually compared in checking mode (IrCoveredCount minus the skipped placeholders)
        public static int[] EvalOrder = [];      // driving mode: node ids the IR evaluates, topo-sorted by the current-value dependency graph (deps first)
        public static bool[] InScc = [];         // [nodeId]; true = NextExpr[v] is in a residual current-value SCC ⇒ driving mode lets S1's ProcessQueue compute it (NextExpr stays — checking mode still uses it; deps-on-this read S1's value)
        public static int ResidualSccNodes;      // # of nodes flagged InScc (S2.3 didn't break the cycle)
        public static int DrivingCoveredCount;   // # of nodes the IR evaluates in driving mode (= EvalOrder.Length)
        public static long MismatchCount;        // total node-mismatches over all StepOne() calls since Build()
        public static long FirstMismatchTime = -1;
        public static int  FirstMismatchNode = -1;
        public static readonly Dictionary<int, long> MismatchByNode = new();   // node id → how many half-cycles it mismatched

        /// <summary>Build the IR from the netlist currently composed in WireCore (after LoadSystem / Reset).</summary>
        public static void Build()
        {
            var g   = NetlistGraph.BuildFrom();
            var di  = DriveAnalysis.Analyze(g);
            var s2  = NextStateModel.Build(g, di);
            var scc = SccModel.Build(g, di, s2);
            NextExpr = scc.NextExpr;
            IsSequential = scc.IsSequential;
            Hybrid = scc.Hybrid;
            int n = Math.Max(NextExpr.Length, WireCore.NodeCount);
            PrevStates = new byte[n];
            CheckInChecking = new bool[NextExpr.Length];
            BuildEvalOrder();          // current-value dependency graph → break residual cycles (hybrid-ize) → topo sort → EvalOrder[] (driving mode)
            IrCoveredCount = 0; CheckedCount = 0;
            var nodes = WireCore.Nodes;
            for (int v = 0; v < NextExpr.Length; v++)
            {
                var e = NextExpr[v];
                if (e == null) continue;
                IrCoveredCount++;
                // Verification boundary: check a node iff it's *observable* — it either drives some transistor gate
                // (Gates.Count > 0 → it has logic fan-out) or carries a semantic name (a pin / register / bus). An
                // *unnamed* node with *no* gate fan-out is a wire-ish junction *inside* a macro cell (a NAND/NOR/AOI
                // pull-down-stack interior node, a carry-chain intermediate, a precharge-stack node, …). Its value
                // is a parasitic-capacitor residue that gets overwritten the moment a real path conducts; whether
                // its transient hold matches S1's event-queue-order artifact is logically unobservable (if it
                // *did* propagate to a real logic node, that node would mismatch — and none do). Standard EDA
                // transistor→gate equivalence checking collapses these internal nodes too. We still extract a
                // model for them (IrCoveredCount), we just don't gate equivalence on them.
                bool observable = v < nodes.Count && nodes[v] is { } nd && (nd.Gates.Count > 0 || WireCore.GetNodeName(v) != v.ToString());
                CheckInChecking[v] = observable;
                if (observable) CheckedCount++;
            }
            MismatchCount = 0; FirstMismatchTime = -1; FirstMismatchNode = -1; MismatchByNode.Clear();
            Built = true;
        }

        static void CollectNodeRefs(Expr e, HashSet<int> ids)
        {
            switch (e)
            {
                case NodeRefExpr nr: ids.Add(nr.Id); break;          // HoldExpr / PrevExpr deliberately *not* collected — they read the prev-half-cycle snapshot, not a dependency edge
                case NotExpr x: CollectNodeRefs(x.Operand, ids); break;
                case AndExpr a: CollectNodeRefs(a.L, ids); CollectNodeRefs(a.R, ids); break;
                case OrExpr o: CollectNodeRefs(o.L, ids); CollectNodeRefs(o.R, ids); break;
                case MuxExpr m: CollectNodeRefs(m.Cond, ids); CollectNodeRefs(m.A, ids); CollectNodeRefs(m.B, ids); break;
            }
        }

        /// <summary>Build EvalOrder[] (driving mode) — the IR-evaluated nodes topo-sorted by the current-value
        /// dependency graph (edge v→M iff NextExpr[v] references NodeRef(M) for an IR-covered M; Hold/Prev don't
        /// count — they read the prev-half-cycle snapshot). Nodes that are genuinely in a cycle S2.3 didn't break
        /// (a Tarjan SCC of size > 1, or a self-loop) are flagged InScc — driving mode then lets S1's ProcessQueue
        /// compute them (NextExpr is left alone, so checking mode still uses it; deps-on-an-SCC-node read S1's
        /// settled value). Only the cycle nodes are flagged, not their (acyclic) dependents.</summary>
        static void BuildEvalOrder()
        {
            int n = NextExpr.Length;
            InScc = new bool[n];
            // deps[v] = the IR-covered NodeRef ids in NextExpr[v]; dependents[M] = the v's that depend on M.
            var deps = new List<int>?[n];
            var dependents = new List<int>[n];
            for (int v = 0; v < n; v++)
            {
                if (NextExpr[v] == null) continue;
                var refs = new HashSet<int>();
                CollectNodeRefs(NextExpr[v]!, refs);
                List<int>? d = null;
                foreach (int m in refs)
                    if (m >= 0 && m < n && NextExpr[m] != null) { (d ??= new()).Add(m); (dependents[m] ??= new()).Add(v); }
                deps[v] = d;
            }

            // Tarjan SCC over the IR-covered subgraph → flag the nodes that are in a real cycle (InScc).
            int[] idx = new int[n], low = new int[n]; Array.Fill(idx, -1);
            bool[] onStk = new bool[n]; var stk = new Stack<int>(); int nextIdx = 0;
            var sccSizes = new List<int>(); var sampleScc = new List<string>();
            // recursive Tarjan — DFS depth = the spanning-tree depth of the dependency DAG (gate depth, tens;
            // paths terminate at hybrid/Input/sequential leaves), so plain recursion is safe.
            void StrongConnect(int v)
            {
                idx[v] = low[v] = nextIdx++; stk.Push(v); onStk[v] = true;
                bool selfLoop = false;
                if (deps[v] is { } dl)
                    foreach (int w in dl)
                    {
                        if (w == v) { selfLoop = true; continue; }
                        if (idx[w] < 0) { StrongConnect(w); if (low[w] < low[v]) low[v] = low[w]; }
                        else if (onStk[w] && idx[w] < low[v]) low[v] = idx[w];
                    }
                if (low[v] == idx[v])
                {
                    var comp = new List<int>(); int w;
                    do { w = stk.Pop(); onStk[w] = false; comp.Add(w); } while (w != v);
                    if (comp.Count > 1 || selfLoop)
                    {
                        sccSizes.Add(comp.Count);
                        foreach (int c in comp) { InScc[c] = true; if (sampleScc.Count < 16) sampleScc.Add($"{WireCore.GetNodeName(c)}#{c}"); }
                    }
                }
            }
            for (int v = 0; v < n; v++) if (NextExpr[v] != null && idx[v] < 0) StrongConnect(v);
            ResidualSccNodes = 0; foreach (int s in sccSizes) ResidualSccNodes += s;
            if (ResidualSccNodes > 0)
                Console.Error.WriteLine($"IrEngine: {sccSizes.Count} residual current-value SCC(s) (sizes {string.Join(",", sccSizes.OrderByDescending(s => s).Take(20))}{(sccSizes.Count > 20 ? "…" : "")}) — {ResidualSccNodes} node(s), S2.3 didn't break → driving mode hands them to S1: {string.Join(", ", sampleScc)}{(ResidualSccNodes > 16 ? " …" : "")}");

            // Kahn topo sort over the remaining acyclic IR nodes (NextExpr != null && !InScc).
            bool Drivable(int v) => v >= 0 && v < n && NextExpr[v] != null && !InScc[v];
            var indeg = new int[n];
            var order = new List<int>(n);
            var queue = new Queue<int>();
            for (int v = 0; v < n; v++)
            {
                if (!Drivable(v)) continue;
                int k = 0; if (deps[v] is { } dl2) foreach (int m in dl2) if (Drivable(m)) k++;   // count only deps the IR will evaluate
                indeg[v] = k;
                if (k == 0) queue.Enqueue(v);
            }
            while (queue.Count > 0)
            {
                int m = queue.Dequeue();
                order.Add(m);
                if (dependents[m] is { } ds) foreach (int v in ds) if (Drivable(v) && --indeg[v] == 0) queue.Enqueue(v);
            }
            EvalOrder = order.ToArray();
            DrivingCoveredCount = EvalOrder.Length;
        }

        public static int EvalExpr(Expr e) => e switch
        {
            ConstExpr c   => c.Value ? 1 : 0,
            NodeRefExpr nr => WireCore.NodeStates[nr.Id],                       // current value (S1's this-half-cycle settled state)
            HoldExpr h    => h.Id < PrevStates.Length ? PrevStates[h.Id] : 0,  // value at the start of this half-cycle (parasitic-cap hold)
            PrevExpr p    => p.Id < PrevStates.Length ? PrevStates[p.Id] : 0,  //   …same, but used to break a sequential loop
            NotExpr x     => 1 - EvalExpr(x.Operand),
            AndExpr a     => EvalExpr(a.L) & EvalExpr(a.R),
            OrExpr o      => EvalExpr(o.L) | EvalExpr(o.R),
            MuxExpr m     => EvalExpr(m.Cond) != 0 ? EvalExpr(m.A) : EvalExpr(m.B),
            ComplexExpr   => 0,                                                // shouldn't appear (Complex ⇒ hybrid ⇒ NextExpr is null)
            _             => 0,
        };

        public static int DiagNode = -1;        // if ≥0: on each mismatch of this node, print t / Pretty / ir / s1 / referenced-node values

        static void CollectIds(Expr e, HashSet<int> ids)
        {
            switch (e)
            {
                case NodeRefExpr nr: ids.Add(nr.Id); break;
                case HoldExpr h: ids.Add(h.Id); break;
                case PrevExpr p: ids.Add(p.Id); break;
                case NotExpr x: CollectIds(x.Operand, ids); break;
                case AndExpr a: CollectIds(a.L, ids); CollectIds(a.R, ids); break;
                case OrExpr o: CollectIds(o.L, ids); CollectIds(o.R, ids); break;
                case MuxExpr m: CollectIds(m.Cond, ids); CollectIds(m.A, ids); CollectIds(m.B, ids); break;
            }
        }

        /// <summary>One half-cycle, checking mode: snapshot prev → WireCore.Step(1) (S1 settles) → verify IR vs S1.</summary>
        public static void StepOne()
        {
            if (!Built) Build();
            int n = WireCore.NodeCount;
            new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(PrevStates);   // prevStates = NodeStates (start of this half-cycle)
            WireCore.Step(1);                                                    // S1: clock toggle + memory callbacks + settle (Case B) + Time++
            for (int v = 0; v < NextExpr.Length && v < n; v++)
            {
                var e = NextExpr[v];
                if (e == null || !CheckInChecking[v]) continue;                   // hybrid / not IR-covered / un-modelled placeholder — S1 owns it
                if (EvalExpr(e) != WireCore.NodeStates[v])
                {
                    MismatchCount++;
                    MismatchByNode[v] = MismatchByNode.GetValueOrDefault(v) + 1;
                    if (FirstMismatchTime < 0) { FirstMismatchTime = WireCore.Time; FirstMismatchNode = v; }
                    if (v == DiagNode)
                    {
                        var ids = new HashSet<int>(); CollectIds(e, ids);
                        var parts = new List<string>();
                        foreach (int id in ids) parts.Add($"{WireCore.GetNodeName(id)}#{id}={(id < n ? WireCore.NodeStates[id] : -1)}{(id < PrevStates.Length ? $"(prev {PrevStates[id]})" : "")}");
                        Console.WriteLine($"  DIAG #{v}: t={WireCore.Time}  ir={EvalExpr(e)} s1={WireCore.NodeStates[v]} prevSelf={(v < PrevStates.Length ? PrevStates[v] : -1)}  | {string.Join("  ", parts)}");
                    }
                }
            }
        }

        public static void Step(int count) { for (int i = 0; i < count; i++) StepOne(); }

        /// <summary>Step until the PPU's in-vblank flag rises (one frame), or maxHalfCycles. Mirrors WireCore.RunFrame.</summary>
        public static long RunFrame(long maxHalfCycles = 1_200_000)
        {
            long start = WireCore.Time;
            int vbl = WireCore.N_PpuInVblank;
            if (vbl == WireCore.EmptyNode) { Step((int)Math.Min(maxHalfCycles, 714_736)); return WireCore.Time - start; }
            bool prev = WireCore.NodeStates[vbl] != 0;
            for (long i = 0; i < maxHalfCycles; i++)
            {
                StepOne();
                bool now = WireCore.NodeStates[vbl] != 0;
                if (!prev && now) break;
                prev = now;
            }
            return WireCore.Time - start;
        }
    }
}
