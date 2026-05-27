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
        // We keep a hard cap so a non-converging region can't hang the whole simulation — the state
        // is a heuristic anyway (see MD/struct/01 §11.2). If this trips routinely it's a bug.
        //
        // 2026-05-25: lowered 1000 → 128 after settle-stats measurement across two real workloads:
        //   full_palette.nes / 50K hc:   max 45 iter, p99 in [33-64]
        //   Super Mario Bros. / 71M hc:  max 41 iter, p99 in [17-32]
        // 128 = ~2.8× safety margin over observed max. Soft warning at 100 still triggers earlier.
        private const int MaxSettlePasses = 128;

        private static void ProcessQueue()
        {
            if (EnableOblivious) { ProcessAllOblivious(); return; }   // math-algos X: replace BFS with all-node sweep until fixpoint
            if (EnableLevelize)  { ProcessQueueLevelized(); return; } // math-algos 策略三: level-ordered settle (priority = gate-only level; fixpoint preserved)
            if (EnableCodegenDispatcher) { DispatcherRun(); return; } // Phase 2.5 Step 2: bitmask-polling macro-block dispatcher (delegates to ProcessQueueInterp for hybrid fallback)
            ProcessQueueInterp();
        }

        // Original ProcessQueue body — the per-node FIFO double-buffer settle. Reachable as block 63
        // of the codegen dispatcher (Phase 2.5 Step 2) AND as the default ProcessQueue when no codegen
        // mode is active. The InvokeCallbacks at the end fires only when called from the no-codegen
        // path; the dispatcher arranges its own InvokeCallbacks after the full dispatcher loop drains.
        private static void ProcessQueueInterp()
        {
            int iteration = 0;
            while (RecalcListNextCount != 0)
            {
                ++iteration;
                if (iteration == 64)
                    Console.Error.WriteLine($"WireCore.ProcessQueue: settle pass {iteration} (still propagating, past p99 — see MD/struct/01 §11.2)");
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
            if (EnableSettleStats) RecordSettle(iteration);
            if (!EnableCodegenDispatcher) InvokeCallbacks();   // WireCore.Handlers.cs — fired by dispatcher after the full block loop drains when codegen mode is on
        }

        private static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            // Phase 2.5 Step 2: nodes claimed by a codegen block skip the interpreter walk; the
            // block dispatcher will compute them. Cheap byte read; the bulk of nodes have it 0.
            if (EnableCodegenDispatcher && CodegenOwned != null && CodegenOwned[nn] != 0) return;
            if (CountEvents)
            {
                RecalcNodeCount++;
                // math-algos 策略三 glitch diagnostic: count the first RecalcNode of nn this half-cycle
                if (_lastRecalcHc != null && _lastRecalcHc[nn] != Time) { _lastRecalcHc[nn] = Time; DistinctRecalcCount++; }
            }
            // Phase 2 P2.4: collapsed IR dispatch — a single byte read + switch instead of
            // IrAbsorbed/IrRoot/IrUseLut chain. 0=hybrid (fall through), 1=absorbed (return),
            // 2=LUT eval, 3=EvalExpr fallback. MUST be gated by EnableIrInterp — verify-ir builds the
            // IR tables but runs the sim under S1, where absorbed mids would otherwise be skipped.
            if (EnableIrInterp && IrClass != null)
            {
                byte cls = IrClass[nn];
                if (cls != IrCls_Hybrid)
                {
                    if (cls == IrCls_Absorbed) { if (CountEvents) RecalcAbsorbedCount++; return; }
                    if (CountEvents) RecalcIrCount++;
                    SetNodeState(nn, cls == IrCls_Lut ? EvalLut(nn) : EvalExpr(IrRoot![nn]));
                    return;
                }
                if (CountEvents) RecalcHybridCount++;
            }
            // math-algos 策略二: pure-logic-gnd nodes resolve in O(1), bypassing the group DFS entirely.
            if (EnableFastPath && IsPureLogic != null && IsPureLogic[nn] != 0) { RecalcNodeFast(nn); return; }
            byte newState = EnableSimdQueue ? ComputeNodeGroupSimd(nn) : ComputeNodeGroup(nn);   // math-algos Y: SIMD-unrolled inner walk (behaviour-identical)
            // math-algos #1 topology-group-ID: ratify the walked group with a fresh GroupID before
            // SetNodeState runs. Subsequent prune-merge skip checks compare GroupIDs (topological
            // equivalence) instead of NodeStates (digital equality) — see WireCore.PruneMerge.cs.
            if (NodeGroupIDs != null)
            {
                long gid = _nextGroupID++;
                for (int i = 0; i < _groupCount; i++) NodeGroupIDs[_groupBuf[i]] = gid;
            }
            // --dead-end-skip: skip SetNodeState writeback for marked leaves (their state goes
            // nowhere — no transistor reads them as gate, no callback watches them, no handler
            // whitelist hit). Saves the SetNodeState body + downstream TlistGates walk for them.
            // BFS itself is unchanged — group resolution still includes the leaf's flag/state.
            if (EnableDeadEndSkip && DeadEndSkippable != null)
            {
                for (int i = 0; i < _groupCount; i++)
                {
                    int m = _groupBuf[i];
                    if (DeadEndSkippable[m] != 0) continue;
                    SetNodeState(m, newState);
                }
            }
            else
            {
                for (int i = 0; i < _groupCount; i++) SetNodeState(_groupBuf[i], newState);
            }

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
                ushort* p = TransistorList + ns.TlistGates;
                if (EnablePruneMerge && newState != 0 && !EnableIrInterp)
                {
                    // math-algos #1 (Gemini r3 fix): gate went HIGH → transistor turns ON → c1,c2 MERGE.
                    // Skip enqueue ONLY when c1 and c2 are already in the same conducting group
                    // (NodeGroupIDs assigned by the last ComputeNodeGroup walk of each endpoint —
                    // see WireCore.PruneMerge.cs). Topological equivalence is the only mathematically
                    // safe skip condition in a switch-level sim: a "1" driven by pull-up and a "1"
                    // held by gate capacitance are digitally equal but topologically distinct, and
                    // merging them DOES change cross-coupled feedback stable states. If c1 and c2 are
                    // already physically tied, adding a parallel transistor between them only changes
                    // resistance, never any resolved value → safe to skip.
                    while (*p != 0)
                    {
                        int c1 = *p++;
                        int c2 = *p++;
                        if (NodeGroupIDs[c1] != NodeGroupIDs[c2]) EnqueueNode(c1);
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
            // Phase 2 P2.3: wake IR nodes whose Expr reads nn (gate-fanout above only reliably reaches
            // hybrid consumers; revDep covers the extracted-Expr consumers). Skipped on nodes with no
            // IR consumer — the IrHasConsumers byte avoids the function call entirely for the bulk of
            // hybrid nodes (most have 0 IR consumers). IrBoundaryDriver path is off-by-default (option
            // B / closed islands made it unnecessary), so removed from the hot path.
            if (EnableIrInterp && IrHasConsumers != null && IrHasConsumers[nn] != 0) EnqueueIrConsumers(nn);
            // Phase 2.5 Step 2: re-arm any codegen block whose input set contains nn. One byte read
            // + a branchless or-into-mask; for the bulk of (un-watched) nodes the byte is 0 and the
            // branch is well predicted.
            if (EnableCodegenDispatcher) CodegenInputChanged(nn);
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
