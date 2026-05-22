using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Phase 1 (CPU/event-driven, pre-IR) — structural analysis for levelized event-driven
        //    scheduling. This is the S2.1–2.3 work (static graph + SCC/loop detection) used here ONLY
        //    to drive a glitch-free schedule of the existing switch-level engine — NOT to extract any
        //    Expr IR (that is Phase 2). See memory math-algos-branch-charter / MD/impl/math-algos.
        //
        //    Increment 1 (this file): MEASURE the structure (no behaviour change). Build the directed
        //    dependency graph, find SCCs (Tarjan), and report whether the netlist is a mostly-small-SCC
        //    DAG (levelization will pay) or one giant pass-transistor SCC (it won't). Invoked by
        //    --dump-levels.
        //
        //    Dependency edges (static, conduction-agnostic):
        //      • gate-control:  t.Gate -> t.C1  and  t.Gate -> t.C2   (the gate decides whether the
        //        endpoints connect, so both endpoints' resolved values depend on the gate)
        //      • channel:       t.C1 <-> t.C2   (bidirectional) ONLY when both ends are normal nodes
        //        (a conducting pass transistor couples the two endpoints). A channel to GND/VCC is a
        //        fixed source -> no channel edge, only the gate edge to the non-supply endpoint.
        //    We build the graph BOTH with and without the channel edges: the full graph is what a
        //    correct static schedule must respect; the gate-only graph is a diagnostic that isolates
        //    real logic feedback from the (conservative) bidirectional pass-transistor coupling.
        //
        //    Increment 2 (--levelize): SOFT levelized event-driven settle. --dump-levels proved the
        //    FULL graph is one giant SCC (pass-transistor over-approximation), so a STRICT topological
        //    schedule is impossible; but the GATE-ONLY graph is mostly acyclic (largest SCC 44). So we
        //    use the gate-only condensation level as a *priority* for the event-driven queue: process
        //    dirty nodes lowest-level-first, but KEEP iterating to fixpoint — correctness is therefore
        //    independent of the order (the switch-level settle still runs to quiescence), the level
        //    order only reduces the within-half-cycle re-evaluations (the ~12% glitch tax 策略三 found).
        //    Measurement-grade (managed buckets) first: the win shows up in D / glitch factor; only if
        //    that pays do we rewrite the queue unmanaged for the hc/s.

        public static bool EnableLevelize = false;
        internal static int* NodeLevel;       // gate-only condensation level per node (priority key); unmanaged, freed in FreeUnmanagedMemory
        internal static int MaxNodeLevel;
        private static int* _lvlSorted;       // scratch: this wave's dirty nodes, counting-sorted by level (unmanaged, sized NodeCount)
        private static int* _lvlCount;        // scratch: counting-sort offsets (unmanaged, sized MaxNodeLevel+2)
        public static string LastLevelizeStats = "(levelize disabled — default; --levelize to enable)";

        /// <summary>Compute the gate-only condensation level for every node (priority key for the
        /// levelized settle) and size the per-level buckets. Run at the end of Reset() when --levelize.</summary>
        internal static void ComputeNodeLevels()
        {
            int n = NodeArrayCount;
            var adj = BuildDepAdj(n, includeChannel: false);   // gate-only (the mostly-acyclic graph)
            int[] comp = TarjanScc(adj, n, out int compCount);

            int[] level = new int[compCount];
            var seen = new HashSet<long>();
            var cadj = new List<int>[compCount];
            for (int c = 0; c < compCount; c++) cadj[c] = new List<int>();
            for (int u = 0; u < n; u++)
            {
                if (comp[u] < 0) continue;
                foreach (int v in adj[u])
                {
                    int cu = comp[u], cv = comp[v];
                    if (cu == cv) continue;
                    long key = ((long)cu << 32) | (uint)cv;
                    if (seen.Add(key)) cadj[cu].Add(cv);
                }
            }
            for (int cu = compCount - 1; cu >= 0; cu--)
                foreach (int cv in cadj[cu]) if (level[cu] + 1 > level[cv]) level[cv] = level[cu] + 1;

            NodeLevel = AllocArray<int>(n);   // tracked + freed in FreeUnmanagedMemory
            int maxL = 0;
            for (int i = 0; i < n; i++) { int lv = comp[i] >= 0 ? level[comp[i]] : 0; NodeLevel[i] = lv; if (lv > maxL) maxL = lv; }
            MaxNodeLevel = maxL;

            _lvlSorted = AllocArray<int>(n);
            _lvlCount = AllocArray<int>(maxL + 2);
            LastLevelizeStats = $"levelize: gate-only condensation levels 0..{maxL} computed for {n:N0} nodes (intra-wave priority for the FIFO settle; convergence preserved)";
        }

        /// <summary>Levelized settle = the SAME convergent double-buffered FIFO as ProcessQueue, except
        /// each wave's dirty list is counting-sorted by gate-only level (ascending) before processing.
        /// A node thus sees its lower-level inputs already updated THIS wave (not next) → fewer waves →
        /// fewer re-evaluations (the glitch tax). Convergence is unchanged from the FIFO: newly-dirtied
        /// nodes go to the next wave exactly as before; only the within-wave order differs. (A STRICT
        /// drain-by-level order does NOT converge here — pass-transistor groups span gate-only levels,
        /// so no valid static topological order exists; see --dump-levels' giant SCC.)</summary>
        private static void ProcessQueueLevelized()
        {
            int iteration = 0;
            while (RecalcListNextCount != 0)
            {
                ++iteration;
                if (iteration > MaxSettlePasses)
                {
                    Console.Error.WriteLine($"WireCore.ProcessQueueLevelized: aborting after {MaxSettlePasses} settle passes ({RecalcListNextCount} pending) — leaving state as-is");
                    for (int i = 0; i < RecalcListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
                    RecalcListNextCount = 0;
                    break;
                }

                // swap "next" ↔ "current"
                int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
                int* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
                RecalcListCount = RecalcListNextCount;
                RecalcListNextCount = 0;

                // counting-sort this wave's nodes by gate-only level (ascending, stable)
                int cnt = RecalcListCount;
                int top = MaxNodeLevel + 1;
                for (int l = 0; l <= top; l++) _lvlCount[l] = 0;
                for (int i = 0; i < cnt; i++) _lvlCount[NodeLevel[RecalcList[i]]]++;
                int run = 0;
                for (int l = 0; l <= top; l++) { int c = _lvlCount[l]; _lvlCount[l] = run; run += c; }
                for (int i = 0; i < cnt; i++) { int nn = RecalcList[i]; _lvlSorted[_lvlCount[NodeLevel[nn]]++] = nn; }

                for (int i = 0; i < cnt; i++)
                {
                    int nn = _lvlSorted[i];
                    if (RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; }
                }
                RecalcListCount = 0;
            }
            InvokeCallbacks();
        }

        /// <summary>Build the directed dependency adjacency over the post-lowering build-time netlist.
        /// includeChannel: add bidirectional C1<->C2 edges for normal-normal pass transistors.</summary>
        private static List<int>[] BuildDepAdj(int n, bool includeChannel)
        {
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            void AddEdge(int u, int v)
            {
                if (u == v) return;
                if (v == Npwr || v == Ngnd) return;            // supply depends on nothing
                if ((uint)u >= (uint)n || (uint)v >= (uint)n) return;
                adj[u].Add(v);
            }
            foreach (var t in Transistors)
            {
                if (t.Gate == Ngnd) continue;                  // never conducts (matches Reset())
                bool c1sup = t.C1 == Npwr || t.C1 == Ngnd;
                bool c2sup = t.C2 == Npwr || t.C2 == Ngnd;
                AddEdge(t.Gate, t.C1);
                AddEdge(t.Gate, t.C2);
                if (includeChannel && !c1sup && !c2sup) { AddEdge(t.C1, t.C2); AddEdge(t.C2, t.C1); }
            }
            return adj;
        }

        /// <summary>Iterative Tarjan SCC (survives deep chains over ~15k nodes). Returns comp[node] =
        /// SCC id (-1 for null/supply), and compCount. SCCs are emitted in reverse-topological order.</summary>
        private static int[] TarjanScc(List<int>[] adj, int n, out int compCount)
        {
            int[] index = new int[n]; Array.Fill(index, -1);
            int[] low = new int[n];
            bool[] onStk = new bool[n];
            int[] comp = new int[n]; Array.Fill(comp, -1);
            int idx = 0, cc = 0;
            var tj = new Stack<int>();
            var call = new Stack<(int node, int ci)>();
            for (int s = 0; s < n; s++)
            {
                if (index[s] != -1 || Nodes[s] == null || s == Npwr || s == Ngnd) continue;
                call.Push((s, 0));
                while (call.Count > 0)
                {
                    var (u, ci) = call.Peek();
                    if (ci == 0) { index[u] = low[u] = idx++; tj.Push(u); onStk[u] = true; }
                    if (ci < adj[u].Count)
                    {
                        call.Pop(); call.Push((u, ci + 1));
                        int v = adj[u][ci];
                        if (index[v] == -1) call.Push((v, 0));
                        else if (onStk[v] && index[v] < low[u]) low[u] = index[v];
                    }
                    else
                    {
                        call.Pop();
                        if (call.Count > 0) { int p = call.Peek().node; if (low[u] < low[p]) low[p] = low[u]; }
                        if (low[u] == index[u])
                        {
                            while (true) { int w = tj.Pop(); onStk[w] = false; comp[w] = cc; if (w == u) break; }
                            cc++;
                        }
                    }
                }
            }
            compCount = cc;
            return comp;
        }

        /// <summary>Report SCC size distribution + condensation max-level for one edge set.</summary>
        private static void ReportScc(string label, List<int>[] adj, int n, bool pureLogicCrosscheck)
        {
            long edges = 0; for (int i = 0; i < n; i++) edges += adj[i].Count;
            int[] comp = TarjanScc(adj, n, out int compCount);

            int[] size = new int[compCount];
            int considered = 0;
            for (int i = 0; i < n; i++) if (comp[i] >= 0) { size[comp[i]]++; considered++; }
            int singletons = 0, multi = 0, largest = 0, nodesInMulti = 0;
            for (int c = 0; c < compCount; c++)
                if (size[c] == 1) singletons++;
                else { multi++; nodesInMulti += size[c]; if (size[c] > largest) largest = size[c]; }

            // condensation longest-path level (descending comp id is topo order)
            int[] level = new int[compCount];
            var seen = new HashSet<long>();
            var cadj = new List<int>[compCount];
            for (int c = 0; c < compCount; c++) cadj[c] = new List<int>();
            for (int u = 0; u < n; u++)
            {
                if (comp[u] < 0) continue;
                foreach (int v in adj[u])
                {
                    int cu = comp[u], cv = comp[v];
                    if (cu == cv) continue;
                    long key = ((long)cu << 32) | (uint)cv;
                    if (seen.Add(key)) cadj[cu].Add(cv);
                }
            }
            for (int cu = compCount - 1; cu >= 0; cu--)
                foreach (int cv in cadj[cu]) if (level[cu] + 1 > level[cv]) level[cv] = level[cu] + 1;
            int maxLevel = 0; for (int c = 0; c < compCount; c++) if (level[c] > maxLevel) maxLevel = level[c];

            Console.WriteLine($"#   [{label}]  {edges:N0} edges");
            Console.WriteLine($"#     SCCs: {compCount:N0} ({singletons:N0} singleton + {multi:N0} multi)   largest: {largest:N0} nodes   in-multi: {nodesInMulti:N0} ({(considered > 0 ? 100.0 * nodesInMulti / considered : 0):F1}%)   max level: {maxLevel:N0}");

            if (pureLogicCrosscheck && IsPureLogic != null)
            {
                int pl = 0, plSingle = 0;
                for (int i = 0; i < n && i < NodeCount; i++)
                    if (IsPureLogic[i] != 0) { pl++; if (comp[i] >= 0 && size[comp[i]] == 1) plSingle++; }
                Console.WriteLine($"#     cross-check vs 策略二: {plSingle:N0}/{pl:N0} pure-logic nodes are singleton SCCs");
            }
        }

        /// <summary>Build the dep graph and report the SCC / level structure (diagnostic; no state change).</summary>
        internal static void AnalyzeLevels()
        {
            int n = NodeArrayCount;
            int live = 0; for (int i = 0; i < n; i++) if (Nodes[i] != null && i != Npwr && i != Ngnd) live++;
            Console.WriteLine($"# levelize structure (post-lowering, {live:N0} live non-supply nodes):");
            ReportScc("full: gate-control + bidirectional pass coupling (a correct static schedule must respect this)", BuildDepAdj(n, includeChannel: true), n, pureLogicCrosscheck: true);
            ReportScc("gate-only: drop pass coupling (diagnostic — isolates real logic feedback)", BuildDepAdj(n, includeChannel: false), n, pureLogicCrosscheck: true);
            Console.WriteLine("#   reading: if gate-only is far more acyclic than full, the giant SCC is conservative");
            Console.WriteLine("#   pass-transistor over-approximation (the real per-phase conducting graph is sparser);");
            Console.WriteLine("#   that coupling is exactly what a directed per-node next-state IR (Phase 2) dissolves.");
            ReportIrClasses(n);
        }

        // ── Phase 2, P2.1 — full-netlist IR routing classification (no state change). Classify EVERY
        //    node by its structure into the bucket that decides how Phase 2 will represent it. Uses the
        //    gate-only SCC (real logic feedback) + the per-node channel structure. This is the IR
        //    coverage map: how much of the WHOLE netlist is cleanly Expr-able vs needs drive-resolution
        //    vs sequential vs dynamic. "Process all netlist-level nodes together" (user, 2026-05-23):
        //    every node is routed here, not a hand-picked subset.
        //
        //      SUPPLY        VCC / GND
        //      SEQ           in a gate-only multi-node SCC -> cross-coupled latch / dynamic storage:
        //                    explicit sequential update (NodeRefExpr to the previous round)
        //      COMB_LOGIC    gate-only singleton, NO pass channel (TlistC1c2s empty), has a pull-up or a
        //                    VCC channel -> clean combinational gate: value = boolean fn of the gate
        //                    nodes of its GND/VCC channels (NOR/NAND/AOI). Directly Expr-able.
        //      COMB_PASS     gate-only singleton WITH pass channels (TlistC1c2s) -> combinational but its
        //                    value routes through pass transistors: needs drive-direction analysis
        //                    (Expr+Mux) or per-node hybrid.
        //      DYNAMIC       gate-only singleton, no pass channel, no pull-up/VCC -> holds via parasitic
        //                    capacitance (HoldExpr) / pure input.
        private static void ReportIrClasses(int n)
        {
            int[] comp = TarjanScc(BuildDepAdj(n, includeChannel: false), n, out int cc);   // gate-only = real logic feedback
            int[] size = new int[cc];
            for (int i = 0; i < n; i++) if (comp[i] >= 0) size[comp[i]]++;

            int supply = 0, seq = 0, combLogic = 0, combPass = 0, dynamic = 0, considered = 0;
            // COMB_PASS shape breakdown: is every pass-channel neighbour an "internal" stack node (no
            // pull-up of its own → part of THIS gate's series/parallel pull-down → Expr-able as a
            // boolean network) or a separately-driven node (has a pull-up → a real gate output → the
            // pass channel is a bus/routing connection → needs drive-direction resolution / hybrid)?
            int cpStack = 0, cpBus = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null) continue;
                if (nn == Npwr || nn == Ngnd) { supply++; continue; }
                considered++;
                if (comp[nn] >= 0 && size[comp[nn]] > 1) { seq++; continue; }   // gate-only SCC -> sequential
                ref NodeInfo ns = ref NodeInfos[nn];
                bool hasPass = ns.TlistC1c2s != 0;
                bool hasPullOrPwr = (ns.Flags & NodeFlags.PullUp) != 0 || ns.TlistC1pwr != 0;
                if (hasPass)
                {
                    combPass++;
                    bool anyDrivenNeighbour = false;
                    int* p = TransistorList + ns.TlistC1c2s;
                    while (*p != 0)
                    {
                        p++;                       // skip gate
                        int other = *p++;          // the channel's other end (a normal node)
                        if ((uint)other < (uint)NodeCount && (NodeInfos[other].Flags & NodeFlags.PullUp) != 0) { anyDrivenNeighbour = true; break; }
                    }
                    if (anyDrivenNeighbour) cpBus++; else cpStack++;
                }
                else if (hasPullOrPwr) combLogic++;
                else dynamic++;
            }

            double P(int x) => considered > 0 ? 100.0 * x / considered : 0;
            Console.WriteLine($"# IR routing classification (Phase 2 P2.1 — whole netlist, {considered:N0} live non-supply nodes):");
            Console.WriteLine($"#   COMB_LOGIC : {combLogic,6:N0} ({P(combLogic),5:F1}%)  clean combinational gate -> directly Expr-able");
            Console.WriteLine($"#   COMB_PASS  : {combPass,6:N0} ({P(combPass),5:F1}%)  combinational via pass transistors -> needs drive-direction analysis");
            Console.WriteLine($"#      ├ stack : {cpStack,6:N0} ({P(cpStack),5:F1}%)  all pass-neighbours internal (no pull-up) -> series/parallel pull-down -> Expr-able as a boolean network");
            Console.WriteLine($"#      └ bus   : {cpBus,6:N0} ({P(cpBus),5:F1}%)  >=1 pass-neighbour is a driven gate output -> shared bus/routing -> drive-direction or hybrid");
            Console.WriteLine($"#   SEQ        : {seq,6:N0} ({P(seq),5:F1}%)  gate-only SCC -> sequential / latch (explicit next-state)");
            Console.WriteLine($"#   DYNAMIC    : {dynamic,6:N0} ({P(dynamic),5:F1}%)  floating / charge-hold (HoldExpr) or pure input");
            Console.WriteLine($"#   (supply: {supply})   reading: COMB_LOGIC is the free win; COMB_PASS is the drive-analysis workload that dissolves the full-graph SCC.");
        }
    }
}
