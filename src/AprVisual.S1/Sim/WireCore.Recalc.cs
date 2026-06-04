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
            if (RecalcHashNext[nn] == 0)
            {
                RecalcListNext[RecalcListNextCount++] = nn;
                RecalcHashNext[nn] = 1;
            }
        }

        // Hard cap on settle passes — DEBUG builds only (Release omits the cap entirely; see ProcessQueueInterp:
        // the in-loop guard cost +2.77%). MetalNES's JS chipsim uses 100; the C++ has none (just a warning).
        // The cap catches a non-converging region during development; the state is a heuristic anyway
        // (see MD/struct/01 §11.2). If it ever trips it's a bug.
        //
        // 2026-05-25: lowered 1000 → 128 after settle-stats measurement across two real workloads:
        //   full_palette.nes / 50K hc:   max 45 iter, p99 in [33-64]
        //   Super Mario Bros. / 71M hc:  max 41 iter, p99 in [17-32]
        // 128 = ~2.8× safety margin over observed max — so in practice it never trips.
#if DEBUG
        private const int MaxSettlePasses = 128;
#endif

        private static void ProcessQueue()
        {
            // S1 fork: single fixed settle path. Oblivious/Levelize/Codegen dispatchers removed.
            ProcessQueueInterp();
        }

        // Per-node FIFO double-buffer settle. The hot loop of the engine.
        private static void ProcessQueueInterp()
        {
#if DEBUG
            int iteration = 0;
#endif
            while (RecalcListNextCount != 0)
            {
#if DEBUG
                // Non-convergence safety + diagnostics — DEBUG ONLY. In Release this whole block (including the
                // break) is compiled out: measured +2.77% interleaved-paired (the cold string-interpolation IL
                // bloated this giant fully-inlined hot method, and the in-loop break inhibited its codegen).
                // NES settle maxes at ~45 passes « MaxSettlePasses so the cap never trips in practice anyway
                // (see the const's note); Debug keeps the catch+message for when someone breaks convergence.
                if (++iteration == 64)
                    Console.Error.WriteLine($"WireCore.ProcessQueue: settle pass {iteration} (still propagating, past p99 — see MD/struct/01 §11.2)");
                if (iteration > MaxSettlePasses)
                {
                    Console.Error.WriteLine($"WireCore.ProcessQueue: aborting after {MaxSettlePasses} settle passes ({RecalcListNextCount} nodes still pending) — leaving state as-is");
                    for (int i = 0; i < RecalcListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
                    RecalcListNextCount = 0;
                    break;
                }
#endif

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
            InvokeCallbacks();   // WireCore.Handlers.cs
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecalcNode(int nn)
        {
            if (nn == Npwr || nn == Ngnd) return;
            // Fast-path dispatch (IsPureLogic populated at Reset):
            //   1 = static pure-logic — group provably {nn}, O(1) RecalcNodeFast.
            //   2 = R-1 dynamic-singleton candidate — has c1c2s channels but no excluded flags; if
            //       every c1c2s gate is OFF this half-cycle the conducting group is exactly {nn}, so
            //       RecalcNodeFast is bit-identical to ComputeNodeGroup({nn}). One ON gate ⇒ fall to BFS.
            //   0 = must go through the BFS (callback / forceCompute / supply resolution).
            byte cls = IsPureLogic[nn];
            if (cls == 1) { RecalcNodeFast(nn); return; }
            if (cls == 2)
            {
                // S2-A: read the c1c2 gates from the inline payload (one cache line, no chase) when
                // available; high-fanout nodes (Inline==0) fall back to the TransistorList scan.
                // goto-flatten: no `bool grows` flag + merge — jump straight to the BFS on the first conducting
                // gate, else RecalcNodeFast. Measured +~0.5% (4 interleaved-paired batches, 47/72 wins, bit-exact):
                // dropping the flag/merge lets the JIT emit a cleaner dispatch exit. (The sibling ideas tested
                // alongside this — unrolling the tiny inline/group loops, ulong on the rare overflow path — all
                // measured neutral/negative; only the flatten helped.)
                NodeInfo* ns = NodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;         // [c1c2 pairs ...] — gates at even offsets
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) { if (NodeStates[pay[k]] != 0) goto FallbackBFS; }
                }
                else
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;   // (gate, other, …, 0)
                    while (*p != 0) { if (NodeStates[*p] != 0) goto FallbackBFS; p += 2; }
                }
                RecalcNodeFast(nn); return;
            }
        FallbackBFS:
            byte newState = ComputeNodeGroup(nn);
            for (int i = 0; i < _groupCount; i++) SetNodeState(_groupBuf[i], newState);

            if ((_groupFlags & NodeFlags.HasCallback) != 0)
            {
                // [A6] direct array index (was Dictionary.TryGetValue), bypass Nodes[] managed Node object graph
                var cbByNode = _callbackByNode!;
                for (int i = 0; i < _groupCount; i++)
                {
                    var cb = cbByNode[_groupBuf[i]];
                    if (cb != null) EnqueueCallback(cb);
                }
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
                // Inline enqueue (suggest #04): hoist queue state to locals; c1 is guaranteed
                // non-supply by AddTransistor (Module.cs:125 normalises supply onto c2), so we
                // can skip EnqueueNode's `nn == Npwr || nn == Ngnd` check for c1.
                // #G2 loop unswitch: newState is loop-invariant 0/1 — specialise the two cases
                // so the gate-low branch (which has 3 extra checks per transistor) compiles to
                // a tighter hot loop in the newState==1 case (no c2 enqueue at all).
                int* nextList = RecalcListNext;
                byte* nextHash = RecalcHashNext;
                int nextCount = RecalcListNextCount;
                ushort* p = TransistorList + tlistGates;
                // Read two (c1,c2) pairs per iteration as one 64-bit load (4 ushorts) — measured +~1.2%
                // (3 interleaved-paired batches, bit-exact). Halves this walk's loop branches + load count;
                // unlike the random NodeInfos/NodeStates gather, this sequential enqueue walk's overhead is
                // NOT fully hidden under the memory-latency stalls, so trimming it measurably helps.
                // 0-terminated list; TransistorList has >=4 trailing pad zeros (see Reset) so the 8-byte
                // read never faults past the array. x64 little-endian: low ushort of `quad` == *p.
                if (newState == 0)
                {
                    int npwr = Npwr, ngnd = Ngnd;
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        int c2a = (ushort)(quad >> 16);
                        if (nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        // gate going low can *disconnect* the channel, so c2 needs re-eval too
                        if (c2a != npwr && c2a != ngnd && nextHash[c2a] == 0) { nextList[nextCount++] = c2a; nextHash[c2a] = 1; }
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        int c2b = (ushort)(quad >> 48);
                        if (nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
                        if (c2b != npwr && c2b != ngnd && nextHash[c2b] == 0) { nextList[nextCount++] = c2b; nextHash[c2b] = 1; }
                        p += 4;
                    }
                }
                else
                {
                    // gate going high: c2 stays connected via the now-ON channel; only c1 needs enqueue
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        if (nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        if (nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
                        p += 4;
                    }
                }
                RecalcListNextCount = nextCount;
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
            Time++;
        }
    }
}
