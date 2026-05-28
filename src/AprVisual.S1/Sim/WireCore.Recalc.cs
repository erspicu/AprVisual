using System;
using System.Runtime.CompilerServices;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
            // S1 fork: single fixed settle path. Oblivious/Levelize/Codegen dispatchers removed.
            ProcessQueueInterp();
        }

        // Per-node FIFO double-buffer settle. The hot loop of the engine.
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
                byte* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
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
            InvokeCallbacks();   // WireCore.Handlers.cs
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            if (CountEvents)
            {
                RecalcNodeCount++;
                if (_lastRecalcHc != null && _lastRecalcHc[nn] != Time) { _lastRecalcHc[nn] = Time; DistinctRecalcCount++; }
            }
            // Fast-path: pure-logic-gnd nodes resolve in O(1). IsPureLogic populated at Reset.
            if (IsPureLogic[nn] != 0) { RecalcNodeFast(nn); return; }
            byte newState = ComputeNodeGroup(nn);
            for (int i = 0; i < _groupCount; i++) SetNodeState(_groupBuf[i], newState);

            if ((_groupFlags & NodeFlags.HasCallback) != 0)
                for (int i = 0; i < _groupCount; i++)
                {
                    var node = Nodes[_groupBuf[i]];
                    if (node?.Callback != null) EnqueueCallback(node.Callback);
                }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetNodeState(int nn, byte newState)
        {
            if (NodeStates[nn] == newState) return;
            NodeStates[nn] = newState;
            int tlistGates = NodeTlistGates[nn];
            if (tlistGates != 0)
            {
                ushort* p = TransistorList + tlistGates;
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
        // _queued variants: only enqueue if flag actually changed; return true if changed.
        // Public SetHigh/etc.: settle only if changed. Used by handler batch loops
        // (WriteBits etc.) to amortize the per-settle cost over an N-pin update.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SetHighQueued(int nn) {
            ref NodeInfo ns = ref NodeInfos[nn];
            var nf = (ns.Flags & ~NodeFlags.SetLow) | NodeFlags.SetHigh;
            if (nf == ns.Flags) return false;
            ns.Flags = nf; EnqueueNode(nn); return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SetLowQueued(int nn) {
            ref NodeInfo ns = ref NodeInfos[nn];
            var nf = (ns.Flags & ~NodeFlags.SetHigh) | NodeFlags.SetLow;
            if (nf == ns.Flags) return false;
            ns.Flags = nf; EnqueueNode(nn); return true;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SetFloatQueued(int nn) {
            ref NodeInfo ns = ref NodeInfos[nn];
            var nf = ns.Flags & ~(NodeFlags.SetLow | NodeFlags.SetHigh);
            if (nf == ns.Flags) return false;
            ns.Flags = nf; EnqueueNode(nn); return true;
        }
        public static void SetHigh(int nn)  { if (SetHighQueued(nn))  ProcessQueue(); }
        public static void SetLow (int nn)  { if (SetLowQueued(nn))   ProcessQueue(); }
        public static void SetFloat(int nn) { if (SetFloatQueued(nn)) ProcessQueue(); }

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
