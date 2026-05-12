using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Change propagation + one-half-cycle step — port of ref/metalnes-main wire_module.cpp:
        //      recalcNodeList / processQueue / recalcNode / setNodeState / enqueueNode (~L1519-1928)
        //      and step_cycle (~L730-751). See MD/note/01_模擬核心演算法.md §2.2-2.6.

        // The master clock node ("clk") is toggled by the clock handler (WireCore.Handlers.AttachClockHandler),
        // not by StepCycle directly — see the note there. Kept here only for reference / diagnostics.
        public static int ClockNode = EmptyNode;

        // When true, RecalcNodeList / SetHigh / SetLow / SetFloat only *enqueue* the dirty node(s) —
        // they do NOT run ProcessQueue. The caller (S2.4 IrEngine.StepOne driving mode) owns the settle:
        // it lets the handler chain enqueue the boundary changes, then evaluates the IR, then runs
        // ProcessQueue itself (the hybrid bridge). Default false ⇒ S1's behaviour is exactly unchanged.
        public static bool DeferRecalc;

        // When non-null (driving mode's bridge, DEBUG): ProcessQueue skips RecalcNode(nn) for nn with
        // SkipRecalcOf[nn]==true. This optimisation broke the memory path in firings 14/15 and is currently
        // only wired up via IrEngine.DebugSkipRecalc + the --debug-skip-recalc flag for investigation.
        // Default null ⇒ S1's behaviour is exactly unchanged.
        public static bool[]? SkipRecalcOf;

        /// <summary>Mark a node dirty and propagate to quiescence (unless DeferRecalc, then just enqueue).</summary>
        public static void RecalcNodeList(int nn) { EnqueueNode(nn); if (!DeferRecalc) ProcessQueue(); }

        /// <summary>Mark several nodes dirty and propagate to quiescence (unless DeferRecalc, then just enqueue).</summary>
        public static void RecalcNodeList(ReadOnlySpan<int> list)
        {
            foreach (int nn in list) EnqueueNode(nn);
            if (!DeferRecalc) ProcessQueue();
        }

        /// <summary>Re-evaluate every (non-supply) node — used at power-on after Reset(). Port of Wires::recomputeAllNodes.</summary>
        public static void RecomputeAllNodes()
        {
            for (int nn = 0; nn < NodeCount; nn++)
                if (nn != Npwr && nn != Ngnd && Nodes[nn] != null) EnqueueNode(nn);
            ProcessQueue();
        }

        internal static void EnqueueNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            if (RecalcHashNext[nn] == 0)
            {
                RecalcListNext[RecalcListNextCount++] = nn;
                RecalcHashNext[nn] = 1;
            }
        }

        /// <summary>Process exactly the nodes currently enqueued (one BFS level) — re-derives each, enqueueing
        /// its fan-out into the *next* level (which is left for a later ProcessQueue). Used by S2.4 driving mode
        /// to flush the handler-chain's boundary changes (clk, …) into NodeStates before the IR evaluation.</summary>
        internal static void ProcessQueueOneLevel()
        {
            if (RecalcListNextCount == 0) return;
            int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
            int* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
            RecalcListCount = RecalcListNextCount;
            RecalcListNextCount = 0;
            for (int i = 0; i < RecalcListCount; i++)
            {
                int nn = RecalcList[i];
                if (RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; }
            }
            RecalcListCount = 0;
        }

        // Hard cap on settle passes. MetalNES's JS chipsim uses 100; the C++ has none (just a warning).
        // We keep a generous hard cap so a non-converging region can't hang the whole simulation —
        // the state is a heuristic anyway (see MD/struct/01 §11.2). If this trips routinely it's a bug.
        private const int MaxSettlePasses = 1000;

        internal static void ProcessQueue()
        {
            int iteration = 0;
            while (RecalcListNextCount != 0)
            {
                ++iteration;
                if (iteration == 100)
                    Console.Error.WriteLine($"WireCore.ProcessQueue: settle pass {iteration} (still propagating; not necessarily a bug — see MD/struct/01 §11.2)");
                if (iteration > MaxSettlePasses)
                {
                    Console.Error.WriteLine($"WireCore.ProcessQueue: aborting after {MaxSettlePasses} settle passes ({RecalcListNextCount} nodes still pending) — leaving state as-is");
                    for (int i = 0; i < RecalcListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
                    RecalcListNextCount = 0;
                    break;
                }

                // swap "next" ↔ "current" (can't tuple-swap pointers — use temps)
                int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
                int* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
                RecalcListCount = RecalcListNextCount;
                RecalcListNextCount = 0;

                for (int i = 0; i < RecalcListCount; i++)
                {
                    int nn = RecalcList[i];
                    if (RecalcHash[nn] != 0)        // may have been cleared by AddNodeToGroup if it joined a group
                    {
                        RecalcHash[nn] = 0;
                        if (SkipRecalcOf is { } skip && nn < skip.Length && skip[nn]) continue;   // driving-mode bridge (DEBUG): already computed by the IR eval
                        RecalcNode(nn);
                    }
                }
                RecalcListCount = 0;
            }
            InvokeCallbacks();   // WireCore.Handlers.cs — memory accesses etc. fire once the dust settles
        }

        /// <summary>Like <see cref="ProcessQueue"/>, but each enqueued node is re-derived via <paramref name="recalcOne"/>
        /// instead of <see cref="RecalcNode"/> — used by IrEngine's event-driven runtime ("β"): IR-covered nodes are
        /// evaluated via their static NextExpr boolean tree, the rest fall back to RecalcNode (S1's group walk).
        /// S1's own ProcessQueue / hot path is untouched. Same queue state, same callback servicing.</summary>
        internal static void ProcessQueueWith(Action<int> recalcOne)
        {
            int iteration = 0;
            while (RecalcListNextCount != 0)
            {
                ++iteration;
                if (iteration > MaxSettlePasses)
                {
                    Console.Error.WriteLine($"WireCore.ProcessQueueWith: aborting after {MaxSettlePasses} settle passes ({RecalcListNextCount} nodes still pending) — leaving state as-is");
                    for (int i = 0; i < RecalcListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
                    RecalcListNextCount = 0;
                    break;
                }
                int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
                int* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
                RecalcListCount = RecalcListNextCount;
                RecalcListNextCount = 0;
                for (int i = 0; i < RecalcListCount; i++)
                {
                    int nn = RecalcList[i];
                    if (RecalcHash[nn] != 0)
                    {
                        RecalcHash[nn] = 0;
                        recalcOne(nn);
                    }
                }
                RecalcListCount = 0;
            }
            InvokeCallbacks();
        }

        internal static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            byte newState = ComputeNodeGroup(nn);   // WireCore.Group.cs — fills _groupBuf / _groupCount / _groupFlags
            for (int i = 0; i < _groupCount; i++) SetNodeState(_groupBuf[i], newState);

            if ((_groupFlags & NodeFlags.HasCallback) != 0)
                for (int i = 0; i < _groupCount; i++)
                {
                    var node = Nodes[_groupBuf[i]];
                    if (node?.Callback != null) EnqueueCallback(node.Callback);
                }
        }

        internal static void SetNodeState(int nn, byte newState)
        {
            if (NodeStates[nn] == newState) return;
            NodeStates[nn] = newState;
            ref NodeInfo ns = ref NodeInfos[nn];
            if (ns.TlistGates != 0)
            {
                int* p = TransistorList + ns.TlistGates;
                while (*p != 0)
                {
                    int c1 = *p++;
                    int c2 = *p++;
                    EnqueueNode(c1);
                    // when a gate goes low some channels may *disconnect*, so the far end needs re-evaluation too
                    if (newState == 0 && c2 != Npwr && c2 != Ngnd) EnqueueNode(c2);
                }
            }
        }

        // ── external pin drive / float (port of setHigh/setLow/setFloat) ──
        public static void SetHigh(int nn) { ref NodeInfo ns = ref NodeInfos[nn]; ns.Flags &= ~NodeFlags.SetLow;  ns.Flags |= NodeFlags.SetHigh; RecalcNodeList(nn); }
        public static void SetLow (int nn) { ref NodeInfo ns = ref NodeInfos[nn]; ns.Flags &= ~NodeFlags.SetHigh; ns.Flags |= NodeFlags.SetLow;  RecalcNodeList(nn); }
        public static void SetFloat(int nn){ ref NodeInfo ns = ref NodeInfos[nn]; ns.Flags &= ~(NodeFlags.SetLow | NodeFlags.SetHigh);          RecalcNodeList(nn); }

        public static void SetHigh(string name)  => SetHigh(RequireNode(name));
        public static void SetLow (string name)  => SetLow (RequireNode(name));
        public static void SetFloat(string name) => SetFloat(RequireNode(name));

        public static bool IsNodeHigh(int nn) => NodeStates[nn] != 0;
        public static bool IsNodeHigh(string name) => NodeStates[RequireNode(name)] != 0;
        public static int GetNodeFlags(int nn) => (int)NodeInfos[nn].Flags;

        private static int RequireNode(string name)
        {
            int nn = LookupNode(name);
            if (nn == EmptyNode) throw new ArgumentException($"unknown node '{name}'");
            return nn;
        }

        // ── one half-cycle: toggle the master clock node, run the per-cycle handler chain, advance time ──
        public static void Step(int count) { for (int i = 0; i < count; i++) StepCycle(); }

        private static void StepCycle()
        {
            RunHandlerChain();          // WireCore.Handlers.cs (clock handler toggles "clk", nes-system handler, …)
            if (TraceLevel != 0) CaptureTraceLine();   // WireCore.Trace.cs
            Time++;
        }
    }
}
