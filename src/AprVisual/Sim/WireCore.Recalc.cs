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

        // math-algos #1 (Observability-Don't-Care merge pruning, the CORRECTED form): when a gate
        // goes HIGH (transistor turns ON → its two channel endpoints MERGE into one group) and the
        // two endpoints already hold the *same* value, the merge is provably value-preserving
        // (group resolution priority GND>VCC>SetHigh>SetLow>PullUp>hold can't flip when both
        // sub-groups already resolve to the same value), so skip the re-evaluation enqueue. ANY
        // later driver / topology change on either side independently re-enqueues that endpoint,
        // and the group walk catches it. The gate-going-LOW (split / disconnection) case is NEVER
        // pruned — at a split the two endpoints are always equal (they were one group) yet the
        // post-split groups can diverge (a dynamic node that held its value only via the now-broken
        // connection), so that path keeps S1's unconditional both-endpoint enqueue. Gated behind
        // --prune-merge; default off (A/B). Verified per-node-identical, not just blargg-PASS.
        public static bool EnablePruneMerge = false;

        // Diagnostics (opt-in via --count-events; gated so the timing path stays uncontaminated):
        // total EnqueueNode hits and RecalcNode calls over a run — to measure how much #1 shrinks D.
        public static bool CountEvents = false;
        public static long EnqueueCount, RecalcNodeCount;

        /// <summary>FNV-1a 64-bit hash over the whole NodeStates array — a cheap fingerprint of the
        /// chip's complete state, for rigorous A/B equivalence checking (two runs that match here at
        /// the same Time are bit-identical per node). NOTE: hashed by node id, so only comparable
        /// between runs with the SAME node numbering (i.e. same --rcm setting).</summary>
        public static ulong NodeStatesChecksum()
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < NodeCount; i++) { h ^= NodeStates[i]; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>Mark a node dirty and propagate to quiescence.</summary>
        public static void RecalcNodeList(int nn) { EnqueueNode(nn); ProcessQueue(); }

        /// <summary>Mark several nodes dirty and propagate to quiescence.</summary>
        public static void RecalcNodeList(ReadOnlySpan<int> list)
        {
            foreach (int nn in list) EnqueueNode(nn);
            ProcessQueue();
        }

        /// <summary>Re-evaluate every (non-supply) node — used at power-on after Reset(). Port of Wires::recomputeAllNodes.</summary>
        public static void RecomputeAllNodes()
        {
            for (int nn = 0; nn < NodeCount; nn++)
                if (nn != Npwr && nn != Ngnd && Nodes[nn] != null) EnqueueNode(nn);
            ProcessQueue();
        }

        private static void EnqueueNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            if (CountEvents) EnqueueCount++;   // diagnostic only; well-predicted false on the timing path
            // (--levelize uses the same FIFO double-buffer; it only reorders each wave by level in ProcessQueueLevelized)
            if (RecalcHashNext[nn] == 0)
            {
                RecalcListNext[RecalcListNextCount++] = nn;
                RecalcHashNext[nn] = 1;
            }
        }

        // Hard cap on settle passes. MetalNES's JS chipsim uses 100; the C++ has none (just a warning).
        // We keep a generous hard cap so a non-converging region can't hang the whole simulation —
        // the state is a heuristic anyway (see MD/struct/01 §11.2). If this trips routinely it's a bug.
        private const int MaxSettlePasses = 1000;

        private static void ProcessQueue()
        {
            if (EnableOblivious) { ProcessAllOblivious(); return; }   // math-algos X: replace BFS with all-node sweep until fixpoint
            if (EnableLevelize)  { ProcessQueueLevelized(); return; } // math-algos 策略三: level-ordered settle (priority = gate-only level; fixpoint preserved)
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
                        RecalcNode(nn);
                        RecalcHash[nn] = 0;
                    }
                }
                RecalcListCount = 0;
            }
            InvokeCallbacks();   // WireCore.Handlers.cs — memory accesses etc. fire once the dust settles
        }

        private static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            if (CountEvents)
            {
                RecalcNodeCount++;
                // math-algos 策略三 glitch diagnostic: count the first RecalcNode of nn this half-cycle
                if (_lastRecalcHc != null && _lastRecalcHc[nn] != Time) { _lastRecalcHc[nn] = Time; DistinctRecalcCount++; }
            }
            // Phase 2 P2.3: event-driven IR — an extracted (combinational) node evaluates its Expr
            // instead of walking a conducting group. Its "group" is {nn}; SetNodeState propagates.
            // HasCallback nodes fall through to the group walk (which fires callbacks).
            if (EnableIrInterp && IrRoot != null && IrRoot[nn] >= 0 && (NodeInfos[nn].Flags & NodeFlags.HasCallback) == 0)
            {
                SetNodeState(nn, EvalExpr(IrRoot[nn]));
                return;
            }
            // math-algos 策略二: pure-logic-gnd nodes resolve in O(1), bypassing the group DFS entirely.
            if (EnableFastPath && IsPureLogic != null && IsPureLogic[nn] != 0) { RecalcNodeFast(nn); return; }
            byte newState = EnableSimdQueue ? ComputeNodeGroupSimd(nn) : ComputeNodeGroup(nn);   // math-algos Y: SIMD-unrolled inner walk (behaviour-identical)
            for (int i = 0; i < _groupCount; i++) SetNodeState(_groupBuf[i], newState);

            if ((_groupFlags & NodeFlags.HasCallback) != 0)
                for (int i = 0; i < _groupCount; i++)
                {
                    var node = Nodes[_groupBuf[i]];
                    if (node?.Callback != null) EnqueueCallback(node.Callback);
                }
        }

        private static void SetNodeState(int nn, byte newState)
        {
            if (NodeStates[nn] == newState) return;
            NodeStates[nn] = newState;
            ref NodeInfo ns = ref NodeInfos[nn];
            if (ns.TlistGates != 0)
            {
                int* p = TransistorList + ns.TlistGates;
                if (EnablePruneMerge && newState != 0 && !EnableIrInterp)
                {
                    // math-algos #1: gate went HIGH → transistor turns ON → c1,c2 MERGE. Re-evaluating
                    // is only needed when the endpoints currently differ; an equal-value merge can't
                    // change either node's resolved value (and any later divergence re-enqueues the
                    // endpoint itself). (c2 is normalised to be the supply when one side is vcc/vss, so
                    // comparing NodeStates[c1] vs NodeStates[c2] also correctly handles pull-up/down:
                    // a pull-up turning on when the node is already 1 is a no-op, etc.)
                    while (*p != 0)
                    {
                        int c1 = *p++;
                        int c2 = *p++;
                        if (NodeStates[c1] != NodeStates[c2]) EnqueueNode(c1);
                    }
                }
                else
                {
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
            // Phase 2 P2.3: in interp mode, also wake the IR nodes whose Expr reads nn (the gate-fanout
            // above only reliably reaches hybrid consumers; revDep covers the extracted-Expr consumers).
            if (EnableIrInterp && _revDepStart != null)
            {
                EnqueueIrConsumers(nn);
                // An IR node resolves via its Expr, NOT via a conducting group — so a node it DRIVES
                // through a pass transistor (a dynamic bus: no pull-up, hybrid) is never pulled into a
                // shared group and would go stale. Wake those pass-channel neighbours so their hybrid
                // group walk re-reads nn's new value. (Only IR nodes need this; hybrid nodes keep S1's
                // group mechanism intact.)
                if (IrBoundaryDriver && IrRoot[nn] >= 0 && ns.TlistC1c2s != 0)
                {
                    int* q = TransistorList + ns.TlistC1c2s;
                    while (*q != 0) { q++; int other = *q++; EnqueueNode(other); }
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
            if (IrBruteForce) ReEvalAllIr();   // Phase 2 debug: oblivious re-eval to isolate triggering bugs

            if (TraceLevel != 0) CaptureTraceLine();   // WireCore.Trace.cs
            Time++;
        }
    }
}
