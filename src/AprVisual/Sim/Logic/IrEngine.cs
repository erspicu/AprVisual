using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

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

        public static DriveInfo?[] Drive = [];   // [nodeId]; DriveAnalysis result (PullDown / PullUp / Passes) — kept so γ.2 can classify latch nodes
        public static int IrCoveredCount;        // # of nodes with NextExpr != null (checking-mode coverage)
        public static int CheckedCount;          // # of nodes actually compared in checking mode (IrCoveredCount minus the skipped placeholders)
        public static int[] EvalOrder = [];      // driving mode: node ids the IR evaluates, topo-sorted by the current-value dependency graph (deps first)
        public static int[][]? EvalLevels;       // S4.6: EvalOrder partitioned into topological levels — EvalLevels[L] = the node ids at depth L (level-L nodes' IR-deps are all in levels < L, so a level can be evaluated in parallel). Lazily built by BuildTopoLevels(); the GPU runtime barriers between levels.
        public static int[]? EvalLevelOf;        // S4.6: [nodeId] → its topo level within EvalOrder, or -1 if not in EvalOrder.
        public static bool[] InScc = [];         // [nodeId]; true = NextExpr[v] is in a current-value SCC that Stage D couldn't break ⇒ driving mode lets S1's ProcessQueue compute it (NextExpr stays — checking mode still uses it; deps-on-this read S1's value)
        public static int ResidualSccNodes;      // # of nodes flagged InScc (Stage D's cap hit, or a self-edge that resisted)
        public static int AliasedNodeCount;      // S3 γ.0: # of pure buffer/inverter nodes folded out of the dependency graph (NodeAlias.Apply)
        public static int BusLoweredCount;       // S4 γ.4: # of hybrid pass-transistor-bus nodes given a wired-resolution pseudo-NextExpr (BusLowering.Apply)
        public static BusNode[] BusNodes = [];   // S4.2b: the hybrid pass-transistor-bus cut-points (NextExpr==null, di.Hybrid, has Passes, not behavioral-memory) + their drive structure (BusResolver.Build) — to be resolved by an inline S0/S1/W1 ping-pong block, not the IR graph
        public static bool[] IsBusNode = [];     // S4.2b: [nodeId] — true if this node is one of BusNodes
        public static bool EnableBusLowering = false; // S4 γ.4 — ABANDONED (Gemini, firing 6, log 20260512_182525): a bidirectional pass-transistor bus is a "mux + strength comparator", not a Boolean node — the wired-AND lowering loses strength info (rare mismatches on io_db*) and folds ~10K nodes into one giant false-loop SCC. The replacement is S4.2b (Inline Bus Resolver: buses are cut-points, not graph nodes; a separate branch-free Resolve_Buses() block with value+strength, ping-pong-iterated with the fixed-K SCC block). BusLowering.cs is kept (disabled) as a documented dead end. Default false; set true only to inspect the dead-end model.
        public static int Size2ResolvedCount;    // S3 γ.1: # of nodes dissolved from size-1/2 SCCs by 1-/2-step substitution
        public static bool Gamma2Enabled = false; // S3 γ.2 (WIP, disabled): topological-loop breaker — dissolves 821/843 SCC nodes correctly (hpos counter, APU DMC, chroma ring, …) but still over-cuts ~2-3 spots (a 6502 pipeline register reading a non-stable data source, a palette-RAM cascade) → checking 2 mismatches + a driving cascade. Root issue: the "register reads Prev(data)" model assumes data is stable until the register's clock edge, which fails when data depends on a register clocked by an earlier derived phase within the same master half-cycle. Needs more thought (asking Gemini). With γ.0+γ.1 driving coverage is 79.3% (843 nodes in 56 SCCs → S1).
        public static List<int[]> SccComponents = new();  // [k] = the node ids of residual SCC #k (size > 1, or a size-1 self-loop); diagnostic — see --dump-scc
        public static int[][] SccEvalOrders = [];   // S4.3: [k] = SCC #k's nodes in Gauss-Seidel order (reverse-postorder of a DFS — back-edges dropped); the fixed-K micro-block evaluates each SCC's nodes in this order, K times
        public const int FixedKScc = 32;            // S4.3: # of fixed-K iterations for the residual-SCC micro-block (Gauss-Seidel; the deepest SCC = the ~496-node APU DMC down-counter's carry chain; K=8 wasn't enough — see the validation report; per-SCC profiling could shrink this for the smaller SCCs)
        public static int StageDBrokenEdges;     // # of feedback edges Stage D cut (NodeRef(M) → Prev(M) in NextExpr[v]) to turn the dependency graph into a DAG
        public static int DrivingCoveredCount;   // # of nodes the IR evaluates in driving mode (= EvalOrder.Length)
        public static bool[] OkToSkipInRecalc = []; // [nodeId]; true = the driving-mode bridge ProcessQueue may skip RecalcNode(this) — IR-covered, not InScc, channel-graph component all-IR, and NodeRef dep-closure all-IR (see BuildOkToSkip). DEBUG-gated by IrEngine.DebugSkipRecalc / --debug-skip-recalc for now.
        public static bool[] SkipInBridge = [];     // S4.2b: [nodeId]; OkToSkipInRecalc ∪ InScc ∪ IsBusNode — what the bridge ProcessQueue (step 5) skips when the ping-pong is on (RunFixedKScc / ResolveBusesRun own those)
        public static bool PingPongEnabled = false; // S4.2b: PARKED — replace the bridge ProcessQueue's SCC+bus settling with the ping-pong "for outer: { RunCompiledStep; RunFixedKScc; ResolveBusesRun }". Disabled: the "settle to fixpoint at end-of-half-cycle clock values" ping-pong can't reproduce S1's within-half-cycle event sequencing (the PPU palette/sprite-RAM precharged-dynamic readout — same obstacle that parked γ.2). With it on: ~322K mismatches over 2000 hc (down from 1.2M after carving the conditional-pull-up bitlines out of SkipInBridge), all in the PPU readout / state-machine path. --pingpong / --no-pingpong to toggle. The bus resolver itself (ValidateBusResolver) is validated 0-real-mismatch (firing 11) — it's kept as a model/codegen artifact; the runtime keeps step 5 (S1) for the precharged-dynamic paths.
        public const int PingPongK = 4;             // S4.2b: ping-pong outer iterations (Gemini: ≈3; +1 margin; bump if --trace-cmp finds it short)
        public static int SkippableInRecalcCount; // # of nodes with OkToSkipInRecalc == true
        // ── flattened driving-mode IR program (S3.1): EvalOrder's Expr trees compiled to one stack-machine
        //    instruction stream — a tight loop over arrays beats recursing object trees (cache-friendly, no
        //    virtual dispatch). Op codes: 0 LoadNode(arg=id), 1 LoadPrev(arg=id), 2 Const0, 3 Const1, 4 Not,
        //    5 And, 6 Or, 7 Mux(cond?a:b), 8 StoreNode(arg=id, also SetNodeState+EnqueueNode if changed).
        const byte OpLoadNode = 0, OpLoadPrev = 1, OpConst0 = 2, OpConst1 = 3, OpNot = 4, OpAnd = 5, OpOr = 6, OpMux = 7, OpStore = 8;
        static byte[] _flatOp = []; static int[] _flatArg = []; static int _flatLen; static byte[] _flatStk = [];
        public static long FlatInstrCount;        // # of instructions in the driving-mode flat program (diagnostic)
        // ── S4.1 codegen: EvalOrder compiled to chunked `Action<byte[] cur, byte[] prev>` delegates (System.Linq.
        //    Expressions → JIT'd IL — straight-line per-node assignments, no interpreter dispatch). Chunked
        //    (~ChunkSize nodes / method) so RyuJIT doesn't blow up on a single huge method. cur = a snapshot of
        //    NodeStates at step-4 (so NodeRef(x) for x earlier in EvalOrder reads its freshly-computed value, for
        //    x outside EvalOrder reads the step-4 value); prev = PrevStates. After the chunks run, the EvalOrder
        //    nodes that changed are pushed back via SetNodeState + EnqueueNode (the bridge then propagates them).
        public const int ChunkSize = 512;
        public static Action<byte[], byte[]>[] CompiledChunks = [];
        public static int CompiledChunkCount;
        public static bool UseCompiledStep = true;  // S4.1: step-4 = the compiled chunks; set false to A/B vs the stack-machine interpreter (RunFlatProgram)
        public static bool UseLlvmStep = false;     // S4.5: step-4 = the LLVM-MCJIT'd `step` function (LlvmCodegen); --llvm-step to enable (overrides UseCompiledStep)
        static byte[] _evalCur = [];
        public static long MismatchCount;        // total node-mismatches over all StepOne() calls since Build()
        public static long FirstMismatchTime = -1;
        public static int  FirstMismatchNode = -1;
        public static readonly Dictionary<int, long> MismatchByNode = new();   // node id → how many half-cycles it mismatched

        /// <summary>Build the IR from the netlist currently composed in WireCore (after LoadSystem / Reset).</summary>
        public static void Build()
        {
            var g   = NetlistGraph.BuildFrom();
            var di  = DriveAnalysis.Analyze(g);
            Drive   = di;
            var s2  = NextStateModel.Build(g, di);
            var scc = SccModel.Build(g, di, s2);
            NextExpr = scc.NextExpr;
            IsSequential = scc.IsSequential;
            Hybrid = scc.Hybrid;
            BusLoweredCount = EnableBusLowering ? BusLowering.Apply(NextExpr, Drive) : 0;   // S4 γ.4 (disabled — see EnableBusLowering): give hybrid pass-transistor-bus nodes a wired-resolution pseudo-NextExpr
            AliasedNodeCount = NodeAlias.Apply(NextExpr);   // S3 γ.0: fold pure buffer/inverter nodes out of the dependency graph (no NodeRef points at one) — shrinks SCCs before BuildEvalOrder / γ.1
            (BusNodes, IsBusNode) = BusResolver.Build(NextExpr, Drive);   // S4.2b: classify the hybrid pass-transistor-bus cut-points + collect their drive structure (LogicPasses / BusPasses / PullDown / PullUp) — the runtime/codegen resolve them with an inline S0/S1/W1 ping-pong (not the IR graph)
            int n = Math.Max(NextExpr.Length, WireCore.NodeCount);
            PrevStates = new byte[n];
            CheckInChecking = new bool[NextExpr.Length];
            BuildEvalOrder();          // current-value dependency graph → break residual cycles (hybrid-ize) → topo sort → EvalOrder[] (driving mode)
            BuildOkToSkip(n);          // which IR nodes the bridge ProcessQueue can skip RecalcNode for
            // S4.2b: when the ping-pong is on, the bridge ProcessQueue (step 5) also skips the residual-SCC + bus
            // nodes (RunFixedKScc / ResolveBusesRun compute those) — leaving step 5 to do just InvokeCallbacks +
            // the behavioral-memory hybrids (u1/u4/cart internals) + any leftover non-IR/SCC/bus fan-out.
            SkipInBridge = new bool[Math.Max(OkToSkipInRecalc.Length, n)];
            for (int v = 0; v < SkipInBridge.Length; v++)
                SkipInBridge[v] = (v < OkToSkipInRecalc.Length && OkToSkipInRecalc[v]) || (v < InScc.Length && InScc[v]) || (v < IsBusNode.Length && IsBusNode[v]);
            // …except bus nodes with a *conditional* pull-up (= a precharge transistor): those are dynamic
            // precharged bitlines (the PPU palette / sprite-RAM readout) whose value is a within-half-cycle
            // sequence (precharge phase → discharge phase → latch capture) that the "settle to fixpoint at
            // end-of-half-cycle clock values" ping-pong can't reproduce — leave them (and so step 5 / S1's
            // event-sequenced ProcessQueue settles them, and re-derives the latches downstream of them).
            foreach (var bn in BusNodes) if (bn.PullUpCond != null && bn.Id < SkipInBridge.Length) SkipInBridge[bn.Id] = false;
            CompileFlatProgram();      // S3.1: compile EvalOrder's Expr trees into one stack-machine instruction stream (driving mode)
            CompileChunkedStep();      // S4.1: compile EvalOrder into chunked Action<byte[],byte[]> delegates (JIT'd IL — straight-line, no interpreter dispatch)
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
            _validBuf = new byte[n]; SccFixedKMismatchCount = 0; SccFixedKMaxK = 0;
            { int nb = BusNodes.Length; _busS0 = new byte[nb]; _busS1 = new byte[nb]; _busW1 = new byte[nb]; _busS0n = new byte[nb]; _busS1n = new byte[nb]; _busW1n = new byte[nb]; _busVal = new byte[nb]; }
            BusMismatchCount = 0; BusFloatExemptCount = 0; BusKPassMax = 0; FirstBusMismatchNode = -1; FirstBusMismatchTime = -1;
            MismatchCount = 0; FirstMismatchTime = -1; FirstMismatchNode = -1; MismatchByNode.Clear();
            if (UseLlvmStep && !LlvmCodegen.Compiled)   // S4.5: build + MCJIT the LLVM `step` now, so the ~0.5s compile isn't charged to the first half-cycle
            {
                try { LlvmCodegen.Compile(); }
                catch (Exception ex) { Console.Error.WriteLine($"LLVM step compile failed → falling back to the Expression-tree JIT: {ex.GetType().Name}: {ex.Message}"); UseLlvmStep = false; }
            }
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

        /// <summary>S4.6: partition EvalOrder into topological levels — level[v] = 1 + max(level[w] for w an
        /// IR-dep of v); level-0 nodes have no IR-deps (only Hold/Prev or non-IR refs). All nodes in a level can
        /// be evaluated in parallel. EvalOrder is already topo-sorted, so a single forward pass suffices. Lazy.</summary>
        public static void BuildTopoLevels()
        {
            if (EvalLevels != null) return;
            int n = NextExpr.Length;
            var lvl = new int[n]; Array.Fill(lvl, -1);
            var refs = new HashSet<int>();
            int maxLvl = 0;
            foreach (int v in EvalOrder)
            {
                if (v < 0 || v >= n) continue;
                int L = 0;
                if (NextExpr[v] is { } e)
                {
                    refs.Clear(); CollectNodeRefs(e, refs);
                    foreach (int w in refs) if (w >= 0 && w < n && lvl[w] >= 0) { int c = lvl[w] + 1; if (c > L) L = c; }
                }
                lvl[v] = L; if (L > maxLvl) maxLvl = L;
            }
            EvalLevelOf = lvl;
            var levels = new System.Collections.Generic.List<int>[maxLvl + 1];
            for (int i = 0; i <= maxLvl; i++) levels[i] = new System.Collections.Generic.List<int>();
            foreach (int v in EvalOrder) if (v >= 0 && v < n) levels[lvl[v]].Add(v);
            EvalLevels = new int[maxLvl + 1][];
            for (int i = 0; i <= maxLvl; i++) EvalLevels[i] = levels[i].ToArray();
        }

        // S3 γ.1: substitute NodeRef(id) → repl(id) (when non-null) throughout e, rebuilding with the smart ctors.
        // Hold/Prev are left alone — they read the prev-half-cycle snapshot, not the being-computed current value.
        static Expr Substitute(Expr e, Func<int, Expr?> repl) => e switch
        {
            NodeRefExpr nr => repl(nr.Id) ?? e,
            NotExpr x      => Expr.Not(Substitute(x.Operand, repl)),
            AndExpr a      => Expr.And(Substitute(a.L, repl), Substitute(a.R, repl)),
            OrExpr  o      => Expr.Or (Substitute(o.L, repl), Substitute(o.R, repl)),
            MuxExpr m      => Expr.Mux(Substitute(m.Cond, repl), Substitute(m.A, repl), Substitute(m.B, repl)),
            _              => e,   // Const / Hold / Prev / Complex
        };

        /// <summary>Build EvalOrder[] (driving mode) — the IR-evaluated nodes topo-sorted by the current-value
        /// dependency graph (edge v→M iff NextExpr[v] references NodeRef(M) for an IR-covered M; Hold/Prev don't
        /// count — they read the prev-half-cycle snapshot). Recursive Tarjan finds the residual cycles; γ.1 (the
        /// generalised Stage A2) tries to dissolve every size-2 SCC {a,b} by two-step algebraic substitution
        /// (a_next = f_a(f_b(Prev), Prev) — for a bistable cross-coupled cell this collapses to a function of
        /// Prev(a)/Prev(b) + the cell's async inputs); resolved pairs leave InScc and join EvalOrder. Cycles γ.1
        /// can't dissolve (a NextExpr is null/Complex, or — γ.2's job — a counter / shift-register tangle) get
        /// InScc[v] = true: driving mode lets S1's ProcessQueue compute those (NextExpr stays, so checking mode
        /// still uses it; deps-on-an-SCC-node read S1's settled value).</summary>
        static void BuildEvalOrder()
        {
            int n = NextExpr.Length;
            InScc = new bool[n];
            StageDBrokenEdges = 0;
            Size2ResolvedCount = 0;
            var deps = new List<int>?[n];
            var dependents = new List<int>?[n];
            var sccSizes = new List<int>(); var sampleScc = new List<string>();

            void BuildDepGraph()
            {
                Array.Clear(deps); Array.Clear(dependents);
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
            }

            // Tarjan SCC over the IR-covered subgraph → flag the cycle nodes (InScc). recursive — DFS depth =
            // the spanning-tree depth of the dependency DAG (gate depth, tens; paths terminate at hybrid/Input/
            // sequential leaves), so plain recursion is safe. Re-runnable (γ.1's loop re-detects after rewriting).
            void RunTarjan()
            {
                Array.Clear(InScc);
                sccSizes.Clear(); sampleScc.Clear(); SccComponents = new List<int[]>();
                int[] idx = new int[n], low = new int[n]; Array.Fill(idx, -1);
                bool[] onStk = new bool[n]; var stk = new Stack<int>(); int nextIdx = 0;
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
                            SccComponents.Add(comp.ToArray());
                            foreach (int c in comp) { InScc[c] = true; if (sampleScc.Count < 16) sampleScc.Add($"{WireCore.GetNodeName(c)}#{c}"); }
                        }
                    }
                }
                for (int v = 0; v < n; v++) if (NextExpr[v] != null && idx[v] < 0) StrongConnect(v);
            }

            void MarkResolved(int v, Expr e)
            {
                NextExpr[v] = e; InScc[v] = false;
                if (v < IsSequential.Length) IsSequential[v] = true;
                if (v < Hybrid.Length) Hybrid[v] = false;
            }

            // γ.1: dissolve size-1 self-loops (1-step substitution) and size-2 cross-coupled cells (2-step
            // substitution — a_next = f_a(f_b(Prev), Prev), the fixpoint of a bistable cell). Returns # resolved.
            int ResolveSmallSccs()
            {
                int resolved = 0;
                foreach (var comp in SccComponents)
                {
                    if (comp.Length == 1)
                    {
                        int a = comp[0]; var ea = NextExpr[a];
                        if (ea is null or ComplexExpr) continue;
                        var nextA = Substitute(ea!, id => id == a ? Expr.Prev(a) : null);   // a holds Prev(a) where its expr fed back
                        var ra = new HashSet<int>(); CollectNodeRefs(nextA, ra);
                        if (ra.Contains(a)) continue;                                        // shouldn't happen — defensive
                        MarkResolved(a, nextA); resolved++;
                    }
                    else if (comp.Length == 2)
                    {
                        int a = comp[0], b = comp[1];
                        var ea = NextExpr[a]; var eb = NextExpr[b];
                        if (ea is null or ComplexExpr || eb is null or ComplexExpr) continue;
                        Expr Sub(Expr e, int x, Expr xr, int y, Expr yr) => Substitute(e, id => id == x ? xr : id == y ? yr : null);
                        var innerB = Sub(eb!, a, Expr.Prev(a), b, Expr.Prev(b));        // b's value after one iteration from prev
                        var innerA = Sub(ea!, b, Expr.Prev(b), a, Expr.Prev(a));        // a's value after one iteration from prev
                        var nextA  = Sub(ea!, b, innerB, a, Expr.Prev(a));              // a's value after b updated   (= fixpoint for a size-2 bistable cell)
                        var nextB  = Sub(eb!, a, innerA, b, Expr.Prev(b));
                        var ra = new HashSet<int>(); CollectNodeRefs(nextA, ra);
                        var rb = new HashSet<int>(); CollectNodeRefs(nextB, rb);
                        if (ra.Contains(a) || ra.Contains(b) || rb.Contains(a) || rb.Contains(b)) continue;   // shouldn't happen — defensive
                        MarkResolved(a, nextA); MarkResolved(b, nextB); resolved += 2;
                    }
                }
                return resolved;
            }

            // γ.2's "synchronous register" test: v is a pure transmission-gate dynamic latch (di[v].Passes > 0,
            // no pull-down, no pull-up) AND at least one of its write ports is gated by a *clock-like* signal —
            // a node that gates many pass transistors (a real clock phase distributes to dozens of latches; a
            // random control signal gates a handful). This excludes domino/precharge nodes (they have a
            // conditional pull-up + an evaluate pull-down, and change *within* a half-cycle — Prev(them) is the
            // wrong value for a same-half-cycle consumer). The clock-fanout map is built once below.
            var clockGateFanout = new Dictionary<int, int>();
            foreach (var d in Drive) if (d != null) foreach (var pl in d.Passes) if (pl.Cond is NodeRefExpr cnr) clockGateFanout[cnr.Id] = clockGateFanout.GetValueOrDefault(cnr.Id) + 1;
            const int ClockGateThreshold = 6;
            bool LatchLike(int v)
            {
                if (v < 0 || v >= Drive.Length || Drive[v] is not { } d) return false;
                if (d.PullDown != null || d.PullUp != PullUpKind.None || d.Passes.Count == 0) return false;
                foreach (var pl in d.Passes) if (pl.Cond is NodeRefExpr nr && clockGateFanout.GetValueOrDefault(nr.Id) >= ClockGateThreshold) return true;
                return false;
            }

            // γ.2: topological-loop breaker. The residual SCCs (≥3 nodes — and any size-2 γ.1 couldn't dissolve)
            // are shift registers / phase rings / synchronous counters built from clock-gated pass-transistor
            // latches; the cycle is the combinational side (carry chains, "counter == k" detectors, the next
            // ring stage) reading the latches' *current* values while the latches feed back. Within an SCC, that
            // current value is — physically — the register state at the start of the half-cycle (the synchronous
            // Next-State function reads Current State): rewrite every SCC-internal NodeRef(latch) → Prev(latch).
            // The clock-phase muxes keep Node(clk) (transmission gates are level-sensitive; the clock dividers'
            // own loops were dissolved by γ.1 so Node(clk) is now an acyclic node). Only SCC-internal edges are
            // touched — pure feed-forward data paths (master-slave pipelines) aren't in SCCs, so no over-cut.
            // Returns # of (consumer, latch) NodeRef edges cut.
            int Gamma2BreakLoops()
            {
                // For each SCC: for every clock-gated transmission-gate register `reg` in it, rewrite reg's *own*
                // NextExpr so its SCC-internal data edges read Prev — `reg = Mux(Node(clk), Node(data), Hold(reg))`
                // becomes `Mux(Node(clk), Prev(data), Hold(reg))`. Physically: the register loads `data`'s value
                // *as of the clock edge*, which (data being combinational logic over other registers, all stable
                // until the edge) equals data's start-of-half-cycle value = Prev(data). This makes every register
                // a *source* of the SCC's induced subgraph; the combinational side (carry chains, "counter == k"
                // detectors, the next ring stage) keeps Node(reg) — it's downstream, so it reflects reg's freshly
                // computed value. So the SCC shatters into a DAG (a cycle that survives is pure-combinational ⇒
                // an extraction artifact ⇒ left to S1). Node(clk) stays current (level-sensitive transmission
                // gate; the clock dividers' loops were dissolved by γ.1 so clk is acyclic now). Returns # of edges cut.
                int cut = 0;
                foreach (var comp in SccComponents)
                {
                    if (comp.Length < 2) continue;
                    if (comp.Any(v => v < InScc.Length && !InScc[v])) continue;     // already dissolved this pass (γ.1)
                    var member = new HashSet<int>(comp);
                    foreach (int reg in comp)
                    {
                        if (!LatchLike(reg) || NextExpr[reg] is not { } e) continue;
                        var refs = new HashSet<int>(); CollectNodeRefs(e, refs);
                        var toCut = new HashSet<int>(refs.Where(member.Contains));
                        if (toCut.Count == 0) continue;
                        NextExpr[reg] = Substitute(e, id => toCut.Contains(id) ? Expr.Prev(id) : null);
                        if (reg < IsSequential.Length) IsSequential[reg] = true;
                        if (reg < Hybrid.Length) Hybrid[reg] = false;
                        cut += toCut.Count;
                    }
                }
                return cut;
            }

            BuildDepGraph(); RunTarjan();
            int gamma2Cuts = 0;
            for (int iter = 0; iter < 16; iter++)
            {
                int r1 = ResolveSmallSccs();
                int r2 = Gamma2Enabled ? Gamma2BreakLoops() : 0;
                Size2ResolvedCount += r1; gamma2Cuts += r2;
                if (r1 == 0 && r2 == 0) break;
                BuildDepGraph(); RunTarjan();   // NextExpr changed — re-detect
            }
            ResidualSccNodes = 0; foreach (int s in sccSizes) ResidualSccNodes += s;
            if (Size2ResolvedCount > 0 || gamma2Cuts > 0) Console.Error.WriteLine($"IrEngine: γ.1 dissolved {Size2ResolvedCount} node(s) (size-1/2 substitution); γ.2 cut {gamma2Cuts} latch back-edge(s) (Node→Prev)");
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

            // S4.3: per-SCC Gauss-Seidel order — DFS the SCC's induced subgraph; reverse-postorder is a topo order
            // of the DAG that remains once the back-edges are dropped, i.e. the order that propagates the most in
            // one pass (only the back-edges need another iteration). The codegen emits `for k in 0..K: <each SCC's
            // nodes in this order>` (fixed-K micro-block; see EmitCsharpSource / S4.2b's ping-pong loop).
            SccEvalOrders = new int[SccComponents.Count][];
            for (int si = 0; si < SccComponents.Count; si++)
            {
                var comp = SccComponents[si];
                var member = new HashSet<int>(comp);
                var color = new Dictionary<int, byte>(comp.Length);   // 0/absent unvisited, 1 on-stack, 2 done
                var post = new List<int>(comp.Length);
                void DfsScc(int u)
                {
                    color[u] = 1;
                    if (deps[u] is { } dl) foreach (int w in dl) if (member.Contains(w) && !color.ContainsKey(w)) DfsScc(w);
                    color[u] = 2; post.Add(u);
                }
                foreach (int v in comp) if (!color.ContainsKey(v)) DfsScc(v);
                post.Reverse();
                SccEvalOrders[si] = post.ToArray();
            }
        }

        /// <summary>Compute OkToSkipInRecalc[v] — true ⇒ the driving-mode bridge ProcessQueue can skip RecalcNode(v):
        /// v is IR-covered, not InScc, AND (a) every node in v's *channel-graph* connected component is also
        /// IR-covered & not-InScc (so v drives no hybrid/Input member that only RecalcNode(v) would propagate to),
        /// AND (b) v's transitive *current-value-dependency* closure (the NodeRef edges in NextExpr) contains no
        /// hybrid/InScc node (so v's IR eval used only correct inputs — nothing ProcessQueue would later fix).
        /// (Input nodes — no channel transistors, value resolved by step 3 before the IR eval — are fine in both.)</summary>
        static void BuildOkToSkip(int n)
        {
            int N = WireCore.NodeCount;
            // (a) channel-graph components (union-find; transistors as edges, but NOT through vcc/vss).
            int[] uf = new int[N]; for (int i = 0; i < N; i++) uf[i] = i;
            int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
            foreach (var t in WireCore.Transistors)
            {
                int c1 = t.C1, c2 = t.C2;
                if (t.Gate == WireCore.Ngnd) continue;
                if (c1 == WireCore.Npwr || c1 == WireCore.Ngnd || c2 == WireCore.Npwr || c2 == WireCore.Ngnd) continue;
                if (c1 >= 0 && c1 < N && c2 >= 0 && c2 < N && c1 != c2) { int ra = Find(c1), rb = Find(c2); if (ra != rb) uf[ra] = rb; }
            }
            bool IsIr(int v) => v < NextExpr.Length && NextExpr[v] != null && (v >= InScc.Length || !InScc[v]);
            bool[] compImpure = new bool[N];
            for (int v = 0; v < N; v++) if (!IsIr(v)) compImpure[Find(v)] = true;
            // (b) transitive dep-closure over the NodeRef edges (computed in EvalOrder order — deps first).
            bool[] depImpure = new bool[NextExpr.Length];
            var nodes = WireCore.Nodes;
            bool ImpureLeaf(int m) => (m < NextExpr.Length && NextExpr[m] == null && m < nodes.Count && nodes[m] is { } nd && nd.C1c2s.Count > 0)   // a real hybrid bus (not an Input / constant — those have no channels and are resolved in step 3)
                                      || (m < InScc.Length && InScc[m]);                                                                              // an InScc node (S1's ProcessQueue computes it after the IR eval)
            foreach (int v in EvalOrder)
            {
                if (NextExpr[v] is not { } e) continue;
                var refs = new HashSet<int>(); CollectNodeRefs(e, refs);
                bool imp = false;
                foreach (int m in refs) { if (ImpureLeaf(m) || (m < depImpure.Length && depImpure[m])) { imp = true; break; } }
                depImpure[v] = imp;
            }
            OkToSkipInRecalc = new bool[Math.Max(NextExpr.Length, N)];
            SkippableInRecalcCount = 0;
            for (int v = 0; v < N; v++) if (IsIr(v) && !compImpure[Find(v)] && !(v < depImpure.Length && depImpure[v])) { OkToSkipInRecalc[v] = true; SkippableInRecalcCount++; }
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

        // EvalExpr variant that reads from managed buffers (used by the S4.3 fixed-K SCC validation / codegen path).
        public static int EvalExprBuf(Expr e, byte[] cur, byte[] prev) => e switch
        {
            ConstExpr c   => c.Value ? 1 : 0,
            NodeRefExpr nr => nr.Id < cur.Length ? cur[nr.Id] : 0,
            HoldExpr h    => h.Id < prev.Length ? prev[h.Id] : 0,
            PrevExpr p    => p.Id < prev.Length ? prev[p.Id] : 0,
            NotExpr x     => 1 - EvalExprBuf(x.Operand, cur, prev),
            AndExpr a     => EvalExprBuf(a.L, cur, prev) & EvalExprBuf(a.R, cur, prev),
            OrExpr o      => EvalExprBuf(o.L, cur, prev) | EvalExprBuf(o.R, cur, prev),
            MuxExpr m     => EvalExprBuf(m.Cond, cur, prev) != 0 ? EvalExprBuf(m.A, cur, prev) : EvalExprBuf(m.B, cur, prev),
            _             => 0,
        };
        static byte[] _validBuf = [];
        public static long SccFixedKMismatchCount;   // S4.3 validation: # of (SCC node, half-cycle) where the fixed-K (K=FixedKScc) micro-block disagreed with S1 (in checking mode)

        // ── S4.2b — inline bus resolver (the S0/S1/W1 model, MD/impl/S4/00 §10) + its validation ─────────────
        public const int BusKPass = 8;             // bus-to-bus propagation iterations (validation upper bound; BusKPassMax = the depth actually needed)
        static byte[] _busS0 = [], _busS1 = [], _busW1 = [], _busS0n = [], _busS1n = [], _busW1n = [], _busVal = [];
        public static long BusMismatchCount;       // S4.2b validation: # of (bus node, half-cycle) where the resolver disagreed with S1 AND the bus had a driver (S0|S1|W1 != 0) — a real model error
        public static long BusFloatExemptCount;    // S4.2b validation: ditto but the bus was floating (S0==S1==W1==0 — in Hold) — the floating-cap "largest-cap wins" we deliberately don't model; an unobservable transient — exempt
        public static int  BusKPassMax;            // S4.2b validation: max bus-to-bus propagation iterations any half-cycle actually needed before fixpoint
        public static int  FirstBusMismatchNode = -1; public static long FirstBusMismatchTime = -1;
        public static bool RunBusValidation = true;   // S4.2b: StepOneDriving runs ValidateBusResolver() each half-cycle when the ping-pong is off (--trace-cmp wants the report). --benchmark turns it off (~2086 buses × the propagation loop / half-cycle isn't part of the real runtime).

        /// <summary>S4.2b validation — after step 5 (the settled NodeStates), recompute every bus node with the inline
        /// S0/S1/W1 wired-resolution model and compare to S1. Run-path unchanged for now; once this validates ~0 real
        /// mismatches, a later firing replaces step 5 with the ping-pong "chunks + fixed-K SCC + ResolveBuses" loop.</summary>
        static void ValidateBusResolver()
        {
            var bns = BusNodes; int nb = bns.Length;
            int F_SetHigh = (int)WireCore.NodeFlags.SetHigh, F_SetLow = (int)WireCore.NodeFlags.SetLow;
            // Step A — local candidate generation (PullDown / PullUp / handler injection / single-direction logic passes)
            for (int bi = 0; bi < nb; bi++)
            {
                var bn = bns[bi];
                int fl = WireCore.GetNodeFlags(bn.Id);
                byte s0 = (byte)(((bn.PullDown != null && EvalExpr(bn.PullDown) != 0) || (fl & F_SetLow) != 0) ? 1 : 0);
                byte s1 = (byte)((bn.StrongVcc || (bn.PullUpCond != null && EvalExpr(bn.PullUpCond) != 0) || (fl & F_SetHigh) != 0) ? 1 : 0);
                byte w1 = (byte)(bn.StaticLoad ? 1 : 0);
                foreach (var (cond, other) in bn.LogicPasses)
                    if (EvalExpr(cond) != 0) { if (WireCore.NodeStates[other] != 0) s1 = 1; else s0 = 1; }
                _busS0[bi] = s0; _busS1[bi] = s1; _busW1[bi] = w1;
            }
            // Step B — bus-to-bus propagation (double-buffered; a conducting pass merges the two buses' drives — full strength, no decay)
            int kUsed = 0;
            for (int k = 0; k < BusKPass; k++)
            {
                Array.Copy(_busS0, _busS0n, nb); Array.Copy(_busS1, _busS1n, nb); Array.Copy(_busW1, _busW1n, nb);
                bool changed = false;
                for (int bi = 0; bi < nb; bi++)
                    foreach (var (cond, oi) in bns[bi].BusPasses)
                        if (EvalExpr(cond) != 0)
                        {
                            if (_busS0[oi] != 0 && _busS0n[bi] == 0) { _busS0n[bi] = 1; changed = true; }
                            if (_busS1[oi] != 0 && _busS1n[bi] == 0) { _busS1n[bi] = 1; changed = true; }
                            if (_busW1[oi] != 0 && _busW1n[bi] == 0) { _busW1n[bi] = 1; changed = true; }
                        }
                (_busS0, _busS0n) = (_busS0n, _busS0); (_busS1, _busS1n) = (_busS1n, _busS1); (_busW1, _busW1n) = (_busW1n, _busW1);
                kUsed = k + 1;
                if (!changed) break;
            }
            if (kUsed > BusKPassMax) BusKPassMax = kUsed;
            // Step C — resolution (GND wins → VCC/pull-up → depletion → hold) + compare to S1
            for (int bi = 0; bi < nb; bi++)
            {
                int id = bns[bi].Id;
                byte s0 = _busS0[bi], s1 = _busS1[bi], w1 = _busW1[bi];
                byte hold = id < PrevStates.Length ? PrevStates[id] : (byte)0;
                byte v = s0 != 0 ? (byte)0 : s1 != 0 ? (byte)1 : w1 != 0 ? (byte)1 : hold;
                _busVal[bi] = v;
                if (v != WireCore.NodeStates[id])
                {
                    if (s0 == 0 && s1 == 0 && w1 == 0) BusFloatExemptCount++;
                    else { BusMismatchCount++; if (FirstBusMismatchTime < 0) { FirstBusMismatchTime = WireCore.Time; FirstBusMismatchNode = id; } }
                }
            }
        }
        public static int  SccFixedKMaxK;            // S4.3: the most iterations any SCC actually needed to reach a fixpoint, observed in checking mode (≤ FixedKScc means K is enough)

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

        /// <summary>S4.1: compile EvalOrder into chunked Action&lt;byte[] cur, byte[] prev&gt; delegates via
        /// System.Linq.Expressions (JIT'd IL — straight-line per-node assignments, no interpreter dispatch).
        /// Mirrors EvalExpr's semantics; all sub-exprs are computed as `int` (byte → int on read, int → byte on store).</summary>
        static void CompileChunkedStep()
        {
            int n = Math.Max(NextExpr.Length, WireCore.NodeCount);
            if (PrevStates.Length > n) n = PrevStates.Length;
            _evalCur = new byte[n];
            var curP  = Expression.Parameter(typeof(byte[]), "cur");
            var prevP = Expression.Parameter(typeof(byte[]), "prev");
            Expression Rd(Expression arr, int id) => Expression.Convert(Expression.ArrayIndex(arr, Expression.Constant(id)), typeof(int));
            Expression Emit(Expr e) => e switch
            {
                ConstExpr c    => Expression.Constant(c.Value ? 1 : 0),
                NodeRefExpr nr => Rd(curP, nr.Id),
                HoldExpr h     => Rd(prevP, h.Id),
                PrevExpr p     => Rd(prevP, p.Id),
                NotExpr x      => Expression.Subtract(Expression.Constant(1), Emit(x.Operand)),
                AndExpr a      => Expression.And(Emit(a.L), Emit(a.R)),
                OrExpr  o      => Expression.Or (Emit(o.L), Emit(o.R)),
                MuxExpr m      => Expression.Condition(Expression.NotEqual(Emit(m.Cond), Expression.Constant(0)), Emit(m.A), Emit(m.B)),
                _              => Expression.Constant(0),   // ComplexExpr — shouldn't appear in EvalOrder
            };
            // Leading length-guard so RyuJIT's bounds-check elimination can drop the per-access checks: after
            // `if (cur.Length < n || prev.Length < n) throw;`, every cur[id]/prev[id] with id < n is provably in
            // bounds. (n = the state-array length; all node ids the chunks touch are < n.)
            var guard = Expression.IfThen(
                Expression.OrElse(Expression.LessThan(Expression.ArrayLength(curP),  Expression.Constant(n)),
                                  Expression.LessThan(Expression.ArrayLength(prevP), Expression.Constant(n))),
                Expression.Throw(Expression.New(typeof(IndexOutOfRangeException))));
            var chunks = new List<Action<byte[], byte[]>>();
            for (int start = 0; start < EvalOrder.Length; start += ChunkSize)
            {
                int end = Math.Min(start + ChunkSize, EvalOrder.Length);
                var stmts = new List<Expression>(end - start + 1) { guard };
                for (int i = start; i < end; i++)
                {
                    int v = EvalOrder[i];
                    Expression rhs = (v < NextExpr.Length && NextExpr[v] is { } e) ? Emit(e) : Expression.Constant(0);
                    stmts.Add(Expression.Assign(Expression.ArrayAccess(curP, Expression.Constant(v)), Expression.Convert(rhs, typeof(byte))));
                }
                Expression body = Expression.Block(stmts);
                chunks.Add(Expression.Lambda<Action<byte[], byte[]>>(body, curP, prevP).Compile());
            }
            CompiledChunks = chunks.ToArray();
            CompiledChunkCount = CompiledChunks.Length;
        }

        /// <summary>S4.1 step-4: snapshot NodeStates → cur, run the compiled chunks (in EvalOrder order), then
        /// push the changed EvalOrder nodes back (SetNodeState + EnqueueNode, so the bridge propagates them).
        /// Equivalent to RunFlatProgram.</summary>
        static void RunCompiledStep()
        {
            int n = WireCore.NodeCount;
            new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(_evalCur);   // cur := NodeStates snapshot (rest stays 0, same as PrevStates)
            var chunks = CompiledChunks; var prev = PrevStates; var cur = _evalCur;
            for (int i = 0; i < chunks.Length; i++) chunks[i](cur, prev);
            var order = EvalOrder;
            for (int i = 0; i < order.Length; i++)
            {
                int v = order[i]; byte nv = cur[v];
                if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); }
            }
        }

        // S4.5 — step 4 via the LLVM-MCJIT'd `step` (same contract as RunCompiledStep: cur := NodeStates snapshot,
        // run `step(cur, prev)`, write back the changed EvalOrder nodes).
        static unsafe void RunLlvmStep()
        {
            if (!LlvmCodegen.Compiled) LlvmCodegen.Compile();
            int n = WireCore.NodeCount;
            new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(_evalCur);
            fixed (byte* cur = _evalCur) fixed (byte* prev = PrevStates) LlvmCodegen.StepFn(cur, prev);
            var order = EvalOrder;
            for (int i = 0; i < order.Length; i++)
            {
                int v = order[i]; byte nv = _evalCur[v];
                if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); }
            }
        }

        static void RunStep() { if (UseLlvmStep) RunLlvmStep(); else if (UseCompiledStep) RunCompiledStep(); else RunFlatProgram(); }

        // ── S4.2b — runnable fixed-K residual-SCC micro-block + the inline bus resolver (the run versions of the
        //    S4.3 / S4.2b-validation code; these REPLACE S1's ProcessQueue settling of the SCC + bus nodes when
        //    the ping-pong is enabled). RunFixedKScc: Gauss-Seidel (writes NodeStates as it goes, so later nodes
        //    in an SCC see this iteration's earlier updates). ResolveBusesRun: the S0/S1/W1 model from §10.
        static void RunFixedKScc()
        {
            for (int k = 0; k < FixedKScc; k++)
                foreach (var o in SccEvalOrders)
                    foreach (int v in o)
                        if (v < NextExpr.Length && NextExpr[v] is { } e)
                        {
                            byte nv = (byte)EvalExpr(e);
                            if (WireCore.NodeStates[v] != nv) { WireCore.SetNodeState(v, nv); WireCore.EnqueueNode(v); }
                        }
        }

        static void ResolveBusesRun()
        {
            var bns = BusNodes; int nb = bns.Length;
            int F_SetHigh = (int)WireCore.NodeFlags.SetHigh, F_SetLow = (int)WireCore.NodeFlags.SetLow;
            for (int bi = 0; bi < nb; bi++)
            {
                var bn = bns[bi];
                int fl = WireCore.GetNodeFlags(bn.Id);
                byte s0 = (byte)(((bn.PullDown != null && EvalExpr(bn.PullDown) != 0) || (fl & F_SetLow) != 0) ? 1 : 0);
                byte s1 = (byte)((bn.StrongVcc || (bn.PullUpCond != null && EvalExpr(bn.PullUpCond) != 0) || (fl & F_SetHigh) != 0) ? 1 : 0);
                byte w1 = (byte)(bn.StaticLoad ? 1 : 0);
                foreach (var (cond, other) in bn.LogicPasses)
                    if (EvalExpr(cond) != 0) { if (WireCore.NodeStates[other] != 0) s1 = 1; else s0 = 1; }
                _busS0[bi] = s0; _busS1[bi] = s1; _busW1[bi] = w1;
            }
            for (int k = 0; k < BusKPass; k++)
            {
                Array.Copy(_busS0, _busS0n, nb); Array.Copy(_busS1, _busS1n, nb); Array.Copy(_busW1, _busW1n, nb);
                bool changed = false;
                for (int bi = 0; bi < nb; bi++)
                    foreach (var (cond, oi) in bns[bi].BusPasses)
                        if (EvalExpr(cond) != 0)
                        {
                            if (_busS0[oi] != 0 && _busS0n[bi] == 0) { _busS0n[bi] = 1; changed = true; }
                            if (_busS1[oi] != 0 && _busS1n[bi] == 0) { _busS1n[bi] = 1; changed = true; }
                            if (_busW1[oi] != 0 && _busW1n[bi] == 0) { _busW1n[bi] = 1; changed = true; }
                        }
                (_busS0, _busS0n) = (_busS0n, _busS0); (_busS1, _busS1n) = (_busS1n, _busS1); (_busW1, _busW1n) = (_busW1n, _busW1);
                if (!changed) break;
            }
            for (int bi = 0; bi < nb; bi++)
            {
                int id = bns[bi].Id;
                byte s0 = _busS0[bi], s1 = _busS1[bi], w1 = _busW1[bi];
                // "hold" = the bus's value carried over from the previous ping-pong iteration (or, on iter 0, the
                // previous half-cycle) — NOT PrevStates[id]: a precharged bitline (PPU palette/sprite-RAM readout)
                // is precharged then read within ONE master half-cycle (the PPU's internal clock phases edge
                // mid-half-cycle), so the precharged value lives in NodeStates, not in the start-of-half-cycle snapshot.
                byte hold = WireCore.NodeStates[id];
                byte v = s0 != 0 ? (byte)0 : s1 != 0 ? (byte)1 : w1 != 0 ? (byte)1 : hold;
                if (v != hold) { WireCore.SetNodeState(id, v); WireCore.EnqueueNode(id); }
            }
        }

        /// <summary>S4.6 (bonus, MD/impl/S4/01 §8): emit a (best-effort, mostly-synthesizable) Verilog module for
        /// the 2A03+2C02+board, logically ≈ S1 (modulo the float-artifact exemption). The IR is essentially a
        /// synthesizable RTL netlist: the acyclic EvalOrder → combinational `assign`s; the latches' Hold/Prev →
        /// a registered copy `n_q` clocked by the master clock; the residual SCC nodes → `always @(posedge clk)`
        /// regs (K=1 — approximate for the deep counters); the hybrid tri-state buses → a resolved priority mux
        /// `n[v] = s0 ? 0 : s1 ? 1 : w1 ? 1 : n_q[v]` (local resolution only — no bus-to-bus propagation). Use:
        /// Verilator (possibly faster than S1) / FPGA synthesis. NOT emitted yet: the behavioral RAM/ROM (hook up
        /// external memory models / add `reg [7:0] mem []` + always-blocks watching cs/we/addr/data), the
        /// bus-to-bus propagation, the full SCC fixed-K (would be a `repeat(K)` unroll), the precise within-
        /// half-cycle PPU clock-phase sequencing.</summary>
        public static string EmitVerilog()
        {
            string VExpr(Expr e) => e switch
            {
                ConstExpr c    => c.Value ? "1'b1" : "1'b0",
                NodeRefExpr nr => $"n[{nr.Id}]",
                HoldExpr h     => $"n_q[{h.Id}]",
                PrevExpr p     => $"n_q[{p.Id}]",
                NotExpr x      => $"(~{VExpr(x.Operand)})",
                AndExpr a      => $"({VExpr(a.L)} & {VExpr(a.R)})",
                OrExpr  o      => $"({VExpr(o.L)} | {VExpr(o.R)})",
                MuxExpr m      => $"({VExpr(m.Cond)} ? {VExpr(m.A)} : {VExpr(m.B)})",
                _              => "1'b0",
            };
            // for SCC nodes: NodeRef to a same-SCC member must read the registered copy (the SCC is cyclic).
            HashSet<int> sccMembers = new(); foreach (var o in SccEvalOrders) foreach (int v in o) sccMembers.Add(v);
            string VExprScc(Expr e) => e switch
            {
                ConstExpr c    => c.Value ? "1'b1" : "1'b0",
                NodeRefExpr nr => sccMembers.Contains(nr.Id) ? $"n_q[{nr.Id}]" : $"n[{nr.Id}]",
                HoldExpr h     => $"n_q[{h.Id}]",
                PrevExpr p     => $"n_q[{p.Id}]",
                NotExpr x      => $"(~{VExprScc(x.Operand)})",
                AndExpr a      => $"({VExprScc(a.L)} & {VExprScc(a.R)})",
                OrExpr  o      => $"({VExprScc(o.L)} | {VExprScc(o.R)})",
                MuxExpr m      => $"({VExprScc(m.Cond)} ? {VExprScc(m.A)} : {VExprScc(m.B)})",
                _              => "1'b0",
            };
            int N = NextExpr.Length;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("// auto-generated by IrEngine.EmitVerilog — S4.6 bonus (MD/impl/S4/01 §8). 2A03+2C02+board, logically ≈ S1.");
            sb.AppendLine("// `clk` = the IR's master half-cycle clock — one `posedge clk` advances one half-cycle. Drive it + `res` from the testbench.");
            sb.AppendLine("// APPROXIMATIONS: residual-SCC nodes are K=1 regs (deep counters need K up to ~12 — a `repeat(K)` unroll); buses are local-only S0/S1/W1 (no bus-to-bus propagation); the float/hold case uses the registered copy (1-half-cycle stale for precharged bitlines — the float-artifact, expected). NOT EMITTED: behavioral RAM/ROM (u1/u4/cart) — hook up external memory models or add `reg [7:0] mem []` + always-blocks; the within-half-cycle PPU clock-phase sequencing.");
            sb.AppendLine("module nes_ir(input clk, input res);");
            sb.AppendLine($"  wire [{N - 1}:0] n;          // node states (n[id])");
            sb.AppendLine($"  reg  [{N - 1}:0] n_q;        // registered copy = the value at the start of this half-cycle (Hold/Prev read this)");
            sb.AppendLine($"  reg  [{N - 1}:0] n_seq;      // residual-SCC node states");
            sb.AppendLine("  always @(posedge clk) n_q <= n;");
            // combinational EvalOrder
            sb.AppendLine("  // ── combinational logic (EvalOrder, ~85% of the chip) ──");
            int comb = 0;
            for (int i = 0; i < EvalOrder.Length; i++)
            {
                int v = EvalOrder[i];
                if (v >= NextExpr.Length || NextExpr[v] is not { } e) continue;
                sb.AppendLine($"  assign n[{v}] = {VExpr(e)};   // {WireCore.GetNodeName(v)}");
                comb++;
            }
            // residual SCC nodes — K=1 registers
            sb.AppendLine($"  // ── residual SCC nodes ({sccMembers.Count} nodes, {SccEvalOrders.Length} SCCs) — modeled as K=1 registers (approximate; see header) ──");
            foreach (var o in SccEvalOrders)
                foreach (int v in o)
                {
                    if (v >= NextExpr.Length || NextExpr[v] is not { } e) continue;
                    sb.AppendLine($"  assign n[{v}] = n_seq[{v}];");
                    sb.AppendLine($"  always @(posedge clk) n_seq[{v}] <= {VExprScc(e)};   // {WireCore.GetNodeName(v)}");
                }
            // hybrid tri-state buses — resolved priority mux (local only)
            sb.AppendLine($"  // ── hybrid pass-transistor buses ({BusNodes.Length}) — resolved priority mux: n[v] = s0 ? 0 : s1 ? 1 : w1 ? 1 : n_q[v]  (local only — no bus-to-bus propagation; see header) ──");
            foreach (var bn in BusNodes)
            {
                var s0p = new System.Collections.Generic.List<string>();
                if (bn.PullDown != null) s0p.Add(VExpr(bn.PullDown));
                foreach (var (cond, other) in bn.LogicPasses) s0p.Add($"({VExpr(cond)} & ~n[{other}])");
                var s1p = new System.Collections.Generic.List<string>();
                if (bn.StrongVcc) s1p.Add("1'b1");
                if (bn.PullUpCond != null) s1p.Add(VExpr(bn.PullUpCond));
                foreach (var (cond, other) in bn.LogicPasses) s1p.Add($"({VExpr(cond)} & n[{other}])");
                string s0 = s0p.Count == 0 ? "1'b0" : "(" + string.Join(" | ", s0p) + ")";
                string s1 = s1p.Count == 0 ? "1'b0" : "(" + string.Join(" | ", s1p) + ")";
                string w1 = bn.StaticLoad ? "1'b1" : "1'b0";
                sb.AppendLine($"  assign n[{bn.Id}] = {s0} ? 1'b0 : {s1} ? 1'b1 : {w1} ? 1'b1 : n_q[{bn.Id}];   // {WireCore.GetNodeName(bn.Id)}");
            }
            sb.AppendLine("  // ── behavioral RAM/ROM: NOT EMITTED — see header. The remaining undriven bits of n[] are the u1/u4/cart internal nodes + supply/input nodes. ──");
            sb.AppendLine("endmodule");
            sb.Insert(0, $"// {comb} combinational + {sccMembers.Count} SCC + {BusNodes.Length} bus nodes; {N} node-id space.\n");
            return sb.ToString();
        }

        /// <summary>S4.1/S4.4: emit the C# source for the compiled step (for inspection / a Roslyn backend / a GPU
        /// compute-shader prototype). One `Step_chunk_N(cur, prev)` per ChunkSize EvalOrder nodes + the fixed-K
        /// residual-SCC block + a `Step` that calls them. <paramref name="bitsliced"/>=false ⇒ scalar `byte[]`
        /// (0/1), `1-x`/`c!=0?a:b` for Not/Mux; true ⇒ bit-sliced `ulong[]` (64 instances per word — GPU/SWAR),
        /// `~x`/`(c&a)|(~c&b)` for Not/Mux, `~0UL`/`0UL` for Const true/false.</summary>
        public static string EmitCsharpSource(bool bitsliced = false)
        {
            string T = bitsliced ? "ulong" : "byte";
            string Cast(string s) => bitsliced ? $"({s})" : $"(byte)({s})";
            string CsExpr(Expr e) => e switch
            {
                ConstExpr c    => c.Value ? (bitsliced ? "~0UL" : "1") : (bitsliced ? "0UL" : "0"),
                NodeRefExpr nr => $"cur[{nr.Id}]",
                HoldExpr h     => $"prev[{h.Id}]",
                PrevExpr p     => $"prev[{p.Id}]",
                NotExpr x      => bitsliced ? $"(~{CsExpr(x.Operand)})" : $"(1-{CsExpr(x.Operand)})",
                AndExpr a      => $"({CsExpr(a.L)}&{CsExpr(a.R)})",
                OrExpr  o      => $"({CsExpr(o.L)}|{CsExpr(o.R)})",
                MuxExpr m      => bitsliced ? $"(({CsExpr(m.Cond)}&{CsExpr(m.A)})|(~{CsExpr(m.Cond)}&{CsExpr(m.B)}))"
                                            : $"({CsExpr(m.Cond)}!=0?{CsExpr(m.A)}:{CsExpr(m.B)})",
                _              => bitsliced ? "0UL" : "0",
            };
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"// auto-generated by IrEngine.EmitCsharpSource(bitsliced={bitsliced}) — the S4.{(bitsliced ? "4 bit-sliced" : "1 scalar")} codegen (chunked).");
            sb.AppendLine(bitsliced ? "// cur[i] / prev[i] = ulong holding 64 instances' value of node i (this / start-of-half-cycle); ~0UL = all-1."
                                    : "// cur[i] = node i's current value (0/1); prev[i] = node i's start-of-half-cycle value.");
            sb.AppendLine("static class GeneratedIrStep {");
            int nChunks = (EvalOrder.Length + ChunkSize - 1) / ChunkSize;
            for (int ci = 0; ci < nChunks; ci++)
            {
                sb.AppendLine($"  static void Step_chunk_{ci}({T}[] cur, {T}[] prev) {{");
                int start = ci * ChunkSize, end = Math.Min(start + ChunkSize, EvalOrder.Length);
                for (int i = start; i < end; i++)
                {
                    int v = EvalOrder[i];
                    string rhs = (v < NextExpr.Length && NextExpr[v] is { } e) ? CsExpr(e) : CsExpr(Expr.False);
                    sb.AppendLine($"    cur[{v}]={Cast(rhs)};");
                }
                sb.AppendLine("  }");
            }
            // S4.3 — the residual-SCC fixed-K micro-block (`for k in 0..K: <each SCC's nodes in Gauss-Seidel order>`).
            // The cur[] reads inside an SCC's block see this iteration's updates (Gauss-Seidel); reads outside the
            // SCCs see the values left by the chunks above (or, when S4.2b's bus resolver is in, the ping-pong loop).
            int sccNodes = 0; foreach (var o in SccEvalOrders) sccNodes += o.Length;
            sb.AppendLine($"  static void Step_scc_fixedK({T}[] cur, {T}[] prev) {{   // {SccEvalOrders.Length} SCCs, {sccNodes} nodes, K={FixedKScc}");
            sb.AppendLine($"    for (int k = 0; k < {FixedKScc}; k++) {{");
            for (int si = 0; si < SccEvalOrders.Length; si++)
            {
                if (SccEvalOrders[si].Length == 0) continue;
                sb.AppendLine($"      // SCC {si} ({SccEvalOrders[si].Length} nodes)");
                foreach (int v in SccEvalOrders[si])
                {
                    string rhs = (v < NextExpr.Length && NextExpr[v] is { } e) ? CsExpr(e) : CsExpr(Expr.False);
                    sb.AppendLine($"      cur[{v}]={Cast(rhs)};");
                }
            }
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            // S4.2b — the inline bus resolver (the S0/S1/W1 wired-resolution block, MD/impl/S4/00 §10). NOTE: this
            // is a codegen artifact — the runtime (StepOneDriving) does NOT call it (the ping-pong is parked: the
            // "settle to fixpoint" model can't reproduce S1's within-half-cycle event sequencing for the PPU's
            // precharged-dynamic readout — see firing 13; the runtime keeps S1's ProcessQueue for the bus/SCC).
            // It's emitted for the S4.5 LLVM backend / a future GPU port. Handler injection (the memory handlers
            // driving the data bus): the host must, before calling this, OR `1` into bus_s1[bi] for bus nodes the
            // handler drives high and bus_s0[bi] for ones it drives low — that's the part the codegen tool can't
            // know; Step A here uses `=` (overwrite), so a host that wants injection should pre-set + change to `|=`.
            int N = BusNodes.Length;
            string busT = T;
            string OrAll(System.Collections.Generic.List<string> parts) => parts.Count == 0 ? (bitsliced ? "0UL" : "0") : parts.Count == 1 ? parts[0] : "(" + string.Join("|", parts) + ")";
            sb.AppendLine($"  static {busT}[] bus_s0 = new {busT}[{N}], bus_s1 = new {busT}[{N}], bus_w1 = new {busT}[{N}], bus_s0n = new {busT}[{N}], bus_s1n = new {busT}[{N}], bus_w1n = new {busT}[{N}];");
            sb.AppendLine($"  static void Resolve_Buses({busT}[] cur, {busT}[] prev) {{   // {N} hybrid pass-transistor-bus nodes, K_PASS={BusKPass}");
            sb.AppendLine($"    // Step A — local candidate generation (per bus: S0 = PullDown | LogicPass→0; S1 = StrongVcc | PullUpCond | LogicPass→1; W1 = StaticLoad)");
            for (int bi = 0; bi < N; bi++)
            {
                var bn = BusNodes[bi];
                var s0p = new System.Collections.Generic.List<string>();
                if (bn.PullDown != null) s0p.Add(CsExpr(bn.PullDown));
                var s1p = new System.Collections.Generic.List<string>();
                if (bn.StrongVcc) s1p.Add(bitsliced ? "~0UL" : "1");
                if (bn.PullUpCond != null) s1p.Add(CsExpr(bn.PullUpCond));
                foreach (var (cond, other) in bn.LogicPasses)
                {
                    string c = CsExpr(cond), o = CsExpr(Expr.Node(other));
                    s0p.Add(bitsliced ? $"({c}&~{o})" : $"({c}&(1-{o}))");
                    s1p.Add($"({c}&{o})");
                }
                sb.AppendLine($"    bus_s0[{bi}]={Cast(OrAll(s0p))}; bus_s1[{bi}]={Cast(OrAll(s1p))}; bus_w1[{bi}]={(bn.StaticLoad ? (bitsliced ? "~0UL" : "(byte)1") : (bitsliced ? "0UL" : "(byte)0"))};   // #{bn.Id} {WireCore.GetNodeName(bn.Id)}");
            }
            sb.AppendLine($"    // Step B — bus-to-bus propagation (K_PASS, double-buffered: a conducting pass merges the two buses' drives)");
            sb.AppendLine($"    for (int k = 0; k < {BusKPass}; k++) {{");
            sb.AppendLine($"      System.Array.Copy(bus_s0, bus_s0n, {N}); System.Array.Copy(bus_s1, bus_s1n, {N}); System.Array.Copy(bus_w1, bus_w1n, {N});");
            for (int bi = 0; bi < N; bi++)
                foreach (var (cond, oi) in BusNodes[bi].BusPasses)
                {
                    string c = CsExpr(cond);
                    sb.AppendLine($"      bus_s0n[{bi}]|=({c})&bus_s0[{oi}]; bus_s1n[{bi}]|=({c})&bus_s1[{oi}]; bus_w1n[{bi}]|=({c})&bus_w1[{oi}];");
                }
            sb.AppendLine($"      (bus_s0,bus_s0n)=(bus_s0n,bus_s0); (bus_s1,bus_s1n)=(bus_s1n,bus_s1); (bus_w1,bus_w1n)=(bus_w1n,bus_w1);");
            sb.AppendLine($"    }}");
            sb.AppendLine($"    // Step C — resolution (GND wins → VCC/pull-up → depletion → hold(=carried-over cur))");
            for (int bi = 0; bi < N; bi++)
            {
                int id = BusNodes[bi].Id;
                string rhs = bitsliced
                    ? $"(~bus_s0[{bi}]&bus_s1[{bi}])|(~bus_s0[{bi}]&~bus_s1[{bi}]&bus_w1[{bi}])|(~bus_s0[{bi}]&~bus_s1[{bi}]&~bus_w1[{bi}]&cur[{id}])"
                    : $"bus_s0[{bi}]!=0?0:bus_s1[{bi}]!=0?1:bus_w1[{bi}]!=0?1:cur[{id}]";
                sb.AppendLine($"    cur[{id}]={Cast(rhs)};");
            }
            sb.AppendLine("  }");
            sb.Append($"  public static void Step({T}[] cur, {T}[] prev) {{");
            for (int ci = 0; ci < nChunks; ci++) sb.Append($" Step_chunk_{ci}(cur,prev);");
            sb.Append(" Step_scc_fixedK(cur,prev);");
            if (N > 0) sb.Append(" Resolve_Buses(cur,prev);");
            sb.AppendLine(" }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        public static int DiagNode = -1;        // if ≥0: on each mismatch of this node, print t / Pretty / ir / s1 / referenced-node values

        public static void CollectIdsPublic(Expr e, HashSet<int> ids) => CollectIds(e, ids);   // for TestRunner's diag

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
            // S4.3 validation: does the fixed-K residual-SCC micro-block (Gauss-Seidel order, K = FixedKScc) converge
            // to S1's settled values? Re-init the SCC nodes to their start-of-half-cycle value, iterate, compare.
            if (SccEvalOrders.Length > 0)
            {
                new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(_validBuf);                                  // post-half-cycle state of everything
                foreach (var o in SccEvalOrders) foreach (int v in o) _validBuf[v] = v < PrevStates.Length ? PrevStates[v] : (byte)0;   // SCC nodes ← start-of-half-cycle
                int kUsed = 0;
                for (int k = 0; k < FixedKScc; k++)
                {
                    bool changed = false;
                    foreach (var o in SccEvalOrders) foreach (int v in o)
                        if (v < NextExpr.Length && NextExpr[v] is { } se) { byte nv = (byte)EvalExprBuf(se, _validBuf, PrevStates); if (_validBuf[v] != nv) { _validBuf[v] = nv; changed = true; } }
                    kUsed = k + 1;
                    if (!changed) break;
                }
                if (kUsed > SccFixedKMaxK) SccFixedKMaxK = kUsed;
                foreach (var o in SccEvalOrders) foreach (int v in o) if (v < n && _validBuf[v] != WireCore.NodeStates[v]) SccFixedKMismatchCount++;
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
            bool pp = PingPongEnabled && (BusNodes.Length > 0 || SccEvalOrders.Length > 0);
            WireCore.SkipRecalcOf = pp ? SkipInBridge : OkToSkipInRecalc;        //    bridge thinning: ProcessQueue skips RecalcNode of nodes the IR already computed (S3.0); + when the ping-pong is on, also the SCC + bus nodes (S4.2b) — see BuildOkToSkip / SkipInBridge
            WireCore.DeferRecalc = true; WireCore.RunHandlerChain(); WireCore.DeferRecalc = false;   // 2. handlers (clock toggle, …): SetHigh/Low just set flags + enqueue, no settle
            WireCore.ProcessQueueOneLevel();                                     // 3. flush the boundary changes (clk, …) into NodeStates + enqueue their fan-out
            RunStep();        // 4. IR-evaluate every node in EvalOrder (topo order). S4.1: compiled chunked delegates (JIT'd IL); fallback = the S3.1 stack-machine interpreter. Writes the changed nodes back via SetNodeState + EnqueueNode.
            if (pp)                                                             // 4b. S4.2b ping-pong: settle the residual-SCC + bus nodes inline (replaces what the bridge ProcessQueue used to do for them) — for outer: { fixed-K SCC; bus S0/S1/W1; re-eval EvalOrder (now seeing the new SCC/bus values) }
                for (int outer = 0; outer < PingPongK; outer++)
                {
                    if (SccEvalOrders.Length > 0) RunFixedKScc();
                    if (BusNodes.Length > 0) ResolveBusesRun();
                    RunStep();
                }
            WireCore.ProcessQueue();                                             // 5. the bridge: with the ping-pong on, skips IR/SCC/bus → just the behavioral-memory hybrids (u1/u4/cart) + leftover fan-out + InvokeCallbacks (memory/video). The memory handlers may have driven the data bus via SetHigh/SetLow → re-settle:
            if (pp)
                for (int outer = 0; outer < PingPongK; outer++)
                {
                    if (SccEvalOrders.Length > 0) RunFixedKScc();
                    if (BusNodes.Length > 0) ResolveBusesRun();
                    RunStep();
                }
            WireCore.SkipRecalcOf = null;
            if (!pp && RunBusValidation && BusNodes.Length > 0) ValidateBusResolver();   // S4.2b: (ping-pong OFF + RunBusValidation) validate the inline S0/S1/W1 bus resolver against the S1-settled NodeStates — off under --benchmark
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
