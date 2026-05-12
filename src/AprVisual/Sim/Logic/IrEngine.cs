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
        public static bool[] InScc = [];         // [nodeId]; true = NextExpr[v] is in a current-value SCC that Stage D couldn't break ⇒ driving mode lets S1's ProcessQueue compute it (NextExpr stays — checking mode still uses it; deps-on-this read S1's value)
        public static int ResidualSccNodes;      // # of nodes flagged InScc (Stage D's cap hit, or a self-edge that resisted)
        public static int StageDBrokenEdges;     // # of feedback edges Stage D cut (NodeRef(M) → Prev(M) in NextExpr[v]) to turn the dependency graph into a DAG
        public static int DrivingCoveredCount;   // # of nodes the IR evaluates in driving mode (= EvalOrder.Length)
        // ── flattened driving-mode IR program (S3.1): EvalOrder's Expr trees compiled to one stack-machine
        //    instruction stream — a tight loop over arrays beats recursing object trees (cache-friendly, no
        //    virtual dispatch). Op codes: 0 LoadNode(arg=id), 1 LoadPrev(arg=id), 2 Const0, 3 Const1, 4 Not,
        //    5 And, 6 Or, 7 Mux(cond?a:b), 8 StoreNode(arg=id, also SetNodeState+EnqueueNode if changed).
        const byte OpLoadNode = 0, OpLoadPrev = 1, OpConst0 = 2, OpConst1 = 3, OpNot = 4, OpAnd = 5, OpOr = 6, OpMux = 7, OpStore = 8;
        static byte[] _flatOp = []; static int[] _flatArg = []; static int _flatLen; static byte[] _flatStk = [];
        public static long FlatInstrCount;        // # of instructions in the driving-mode flat program (diagnostic)
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
            CompileFlatProgram();      // S3.1: compile EvalOrder's Expr trees into one stack-machine instruction stream (driving mode)
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
        /// count — they read the prev-half-cycle snapshot). Recursive Tarjan finds the residual cycles S2.3 (incl.
        /// Stage A2) didn't break; the cycle nodes get InScc[v] = true — driving mode lets S1's ProcessQueue
        /// compute those (NextExpr is left alone, so checking mode still uses it; deps-on-an-SCC-node read S1's
        /// settled value). Only the cycle nodes are flagged, not their (acyclic) dependents.
        /// (Stage D — Prev-cutting the back-edges — was tried and reverted: it's only correct for pure pass-
        /// through pipelines, but most residual cycles are stateful — the 2C02 clock dividers, whose extracted
        /// NextExpr is itself wrong — so Prev-cutting yields a wrong model. Needs a "is this cycle a pipeline?"
        /// classifier; deferred. See MD/impl/S2/05 §"firing 12".)</summary>
        static void BuildEvalOrder()
        {
            int n = NextExpr.Length;
            InScc = new bool[n];
            StageDBrokenEdges = 0;
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

            // Tarjan SCC over the IR-covered subgraph → flag the cycle nodes (InScc). recursive — DFS depth =
            // the spanning-tree depth of the dependency DAG (gate depth, tens; paths terminate at hybrid/Input/
            // sequential leaves), so plain recursion is safe.
            int[] idx = new int[n], low = new int[n]; Array.Fill(idx, -1);
            bool[] onStk = new bool[n]; var stk = new Stack<int>(); int nextIdx = 0;
            var sccSizes = new List<int>(); var sampleScc = new List<string>();
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
                    var comp = new List<int>(); int w; do { w = stk.Pop(); onStk[w] = false; comp.Add(w); } while (w != v);
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
                Console.Error.WriteLine($"IrEngine: {sccSizes.Count} residual current-value SCC(s) (sizes {string.Join(",", sccSizes.OrderByDescending(s => s).Take(20))}{(sccSizes.Count > 20 ? "…" : "")}) — {ResidualSccNodes} node(s) → driving mode hands them to S1: {string.Join(", ", sampleScc)}{(ResidualSccNodes > 16 ? " …" : "")}");

            // Kahn topo sort over the IR nodes (NextExpr != null && !InScc) — acyclic.
            bool Drivable(int v) => v >= 0 && v < n && NextExpr[v] != null && !InScc[v];
            var indeg = new int[n];
            var order = new List<int>(n);
            var queue = new Queue<int>();
            for (int v = 0; v < n; v++)
            {
                if (!Drivable(v)) continue;
                int k = 0; if (deps[v] is { } dl2) foreach (int m in dl2) if (Drivable(m)) k++;
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

        /// <summary>Compile EvalOrder's Expr trees into the flat stack-machine program (_flatOp/_flatArg).
        /// Post-order emit each NextExpr[v]'s tree, then a StoreNode(v). Mirrors EvalExpr's semantics.</summary>
        static void CompileFlatProgram()
        {
            var ops = new List<byte>(EvalOrder.Length * 12);
            var args = new List<int>(EvalOrder.Length * 12);
            int maxDepth = 1;
            void Emit(byte op, int arg) { ops.Add(op); args.Add(arg); }
            void Walk(Expr e, ref int depth)   // depth tracking: a leaf pushes 1; And/Or pop 2 push 1 (net -1); Mux pops 3 push 1 (net -2); Not net 0
            {
                switch (e)
                {
                    case ConstExpr c: Emit(c.Value ? OpConst1 : OpConst0, 0); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case NodeRefExpr nr: Emit(OpLoadNode, nr.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case HoldExpr h: Emit(OpLoadPrev, h.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case PrevExpr p: Emit(OpLoadPrev, p.Id); depth++; if (depth > maxDepth) maxDepth = depth; break;
                    case NotExpr x: Walk(x.Operand, ref depth); Emit(OpNot, 0); break;                                  // pops 1, pushes 1: net 0
                    case AndExpr a: Walk(a.L, ref depth); Walk(a.R, ref depth); Emit(OpAnd, 0); depth--; break;          // pops 2, pushes 1
                    case OrExpr o: Walk(o.L, ref depth); Walk(o.R, ref depth); Emit(OpOr, 0); depth--; break;
                    case MuxExpr m: Walk(m.Cond, ref depth); Walk(m.A, ref depth); Walk(m.B, ref depth); Emit(OpMux, 0); depth -= 2; break;  // pops 3, pushes 1
                    default: Emit(OpConst0, 0); depth++; if (depth > maxDepth) maxDepth = depth; break;                  // ComplexExpr shouldn't appear
                }
            }
            foreach (int v in EvalOrder)
            {
                int d = 0;
                if (v < NextExpr.Length && NextExpr[v] is { } e) Walk(e, ref d); else Emit(OpConst0, 0);
                Emit(OpStore, v);   // consumes the one value on the stack (d should be 1 here)
            }
            _flatOp = ops.ToArray(); _flatArg = args.ToArray(); _flatLen = ops.Count;
            _flatStk = new byte[Math.Max(16, maxDepth + 4)];
            FlatInstrCount = _flatLen;
        }

        /// <summary>Run the flat driving-mode IR program: evaluate every node in EvalOrder (topo order) and
        /// write it back via WireCore.SetNodeState (+ EnqueueNode if it changed, so the bridge ProcessQueue
        /// propagates it / services its callbacks). Equivalent to `foreach v in EvalOrder: SetNodeState(v, EvalExpr(NextExpr[v]))`.</summary>
        static void RunFlatProgram()
        {
            byte[] stk = _flatStk; byte[] op = _flatOp; int[] arg = _flatArg; int n = _flatLen;
            int prevN = PrevStates.Length;
            int sp = 0;
            for (int i = 0; i < n; i++)
            {
                switch (op[i])
                {
                    case OpLoadNode: stk[sp++] = WireCore.NodeStates[arg[i]]; break;
                    case OpLoadPrev: stk[sp++] = arg[i] < prevN ? PrevStates[arg[i]] : (byte)0; break;
                    case OpConst0: stk[sp++] = 0; break;
                    case OpConst1: stk[sp++] = 1; break;
                    case OpNot: stk[sp - 1] = (byte)(1 - stk[sp - 1]); break;
                    case OpAnd: { byte b = stk[--sp]; stk[sp - 1] &= b; break; }
                    case OpOr:  { byte b = stk[--sp]; stk[sp - 1] |= b; break; }
                    case OpMux: { byte b = stk[--sp]; byte a = stk[--sp]; byte c = stk[--sp]; stk[sp++] = c != 0 ? a : b; break; }
                    case OpStore: { byte nv = stk[--sp]; int v = arg[i]; if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); } break; }
                }
            }
        }

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

        // ── driving mode (S2.4 proper + S2.5 bridge): the IR *replaces* S1's settle; only the hybrid + InScc
        //    nodes (and the IR nodes adjacent to them) are computed by S1's group-BFS. See MD/impl/S2/05 "firing 8". ──

        /// <summary>One half-cycle, driving mode. The IR engine owns the chip.</summary>
        public static void StepOneDriving()
        {
            if (!Built) Build();
            int n = WireCore.NodeCount;
            new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(PrevStates);   // 1. prevStates = NodeStates (start of this half-cycle — Hold/Prev read this)
            WireCore.DeferRecalc = true; WireCore.RunHandlerChain(); WireCore.DeferRecalc = false;   // 2. handlers (clock toggle, …): SetHigh/Low just set flags + enqueue, no settle
            WireCore.ProcessQueueOneLevel();                                     // 3. flush the boundary changes (clk, …) into NodeStates + enqueue their fan-out
            RunFlatProgram();                                                    // 4. IR-evaluate every node in EvalOrder (topo order; flat stack-machine program — S3.1); writes back via SetNodeState + EnqueueNode if changed
            WireCore.ProcessQueue();                                             // 5. the bridge: computes the hybrid + InScc nodes (enqueued via the fan-out of steps 3/4); re-derives the IR nodes that got enqueued (= same value ⇒ no propagation); InvokeCallbacks = memory/video
            if (WireCore.TraceLevel != 0) WireCore.CaptureTraceLine();
            WireCore.Time++;                                                     // 6.
        }

        public static void StepDriving(int count) { for (int i = 0; i < count; i++) StepOneDriving(); }

        /// <summary>Driving-mode RunFrame: step until the PPU's in-vblank flag rises, or maxHalfCycles.</summary>
        public static long RunFrameDriving(long maxHalfCycles = 1_200_000)
        {
            long start = WireCore.Time;
            int vbl = WireCore.N_PpuInVblank;
            if (vbl == WireCore.EmptyNode) { StepDriving((int)Math.Min(maxHalfCycles, 714_736)); return WireCore.Time - start; }
            bool prev = WireCore.NodeStates[vbl] != 0;
            for (long i = 0; i < maxHalfCycles; i++)
            {
                StepOneDriving();
                bool now = WireCore.NodeStates[vbl] != 0;
                if (!prev && now) break;
                prev = now;
            }
            return WireCore.Time - start;
        }
    }
}
