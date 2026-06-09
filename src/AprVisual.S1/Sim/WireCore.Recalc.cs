using System;
using System.Runtime.CompilerServices;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Change propagation + one-half-cycle step — port of ref/metalnes-main wire_module.cpp:
        //      recalcNodeList / processQueue / recalcNode / setNodeState / enqueueNode (~L1519-1928)
        //      and step_cycle (~L730-751). See MD/note/01_模擬核心演算法.md §2.2-2.6.

        // The master clock node ("clk"), resolved by AttachClockHandler. [H1] StepCycle toggles it INLINE
        // every half-cycle (was a handler-chain delegate). EmptyNode if there's no clk node (toggle skipped).
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

#if DEBUG
        // ── Wasted-pop profiler (DEBUG ONLY — compiled out of Release; zero hot-path cost there) ──
        // A "no-change pop" = a node popped from RecalcList whose RecalcNode caused ZERO state change =
        // wasted work that an enqueue-prune could eliminate (this is how P-2/P-3/P-4 were found). Run:
        //   dotnet run -c Debug --project src/AprVisual.S1 -- --benchmark <rom> --bench-hc N --extra-ram \
        //              --system-def-dir <dir>
        // and read the [waste-profile] line. Event COUNTS are identical in Debug and Release (same
        // algorithm) — only wall-clock differs — so the categorisation is valid for Release tuning.
        // Categories of no-change pop: FloatSingle/Multi (no-driver float-hold), PullUp/Supply (driven,
        // recompute-same — the structural turn-off "hidden-driver" residue), Other.
        internal static long DiagPops, DiagNoChange, DiagStateChanges,
                             DiagNCFloatSingle, DiagNCFloatMulti, DiagNCFloatMultiCapLT,
                             DiagNCPullUp, DiagNCSupply, DiagNCOther;

        private static unsafe void WasteProfileTally(int nn, bool noChange)
        {
            DiagPops++;
            if (!noChange) return;
            DiagNoChange++;
            NodeInfo* d = NodeInfos + nn;
            bool floatOnly = (d->Flags & (NodeFlags.PullUp | NodeFlags.ForceCompute | NodeFlags.HasCallback)) == 0
                          && (d->Inline != 0 ? (d->GndCount == 0 && d->PwrCount == 0) : (d->TlistC1gnd == 0 && d->TlistC1pwr == 0));
            if (floatOnly && d->Inline != 0 && d->C1c2Count == 1) DiagNCFloatSingle++;
            else if (floatOnly && d->Inline != 0 && d->C1c2Count > 1)
            {
                DiagNCFloatMulti++;
                ushort* pay = d->InlinePayload; int nc = d->C1c2Count; bool capLt = true;
                for (int k = 0; k < nc; k++) if (NodeConnections[nn] >= NodeConnections[pay[k * 2 + 1]]) { capLt = false; break; }
                if (capLt) DiagNCFloatMultiCapLT++;
            }
            else if (floatOnly) DiagNCOther++;
            else if ((d->Flags & NodeFlags.PullUp) != 0) DiagNCPullUp++;
            else if (d->Inline != 0 ? (d->GndCount != 0 || d->PwrCount != 0) : (d->TlistC1gnd != 0 || d->TlistC1pwr != 0)) DiagNCSupply++;
            else DiagNCOther++;
        }

        // ── SetNodeState compound-`if` condition profiler (DEBUG ONLY) ──
        // Measures, for each of the 3 enqueue `if` templates, the INDEPENDENT true-rate of every &&-clause
        // (evaluated unconditionally here — defeats short-circuit, so each rate is unconditional, not the
        // biased "only-when-earlier-passed" rate the live && would show). Goal: reorder the && clauses so the
        // cheapest + most-often-FALSE clause runs first (more short-circuits ⇒ fewer ops in the hot loop).
        // Counts are deterministic ⇒ identical to Release. a/b unroll copies are aggregated per template.
        // Reordering pure (side-effect-free) && clauses is bit-exact; still gate the chosen order on checksum.
        internal static long
            CpOff1_N, CpOff1_NextHash0, CpOff1_MaskOff, CpOff1_Whole,                          // TurnOff c1: nextHash0 && maskOff
            CpOff2_N, CpOff2_NotPwr, CpOff2_NotGnd, CpOff2_NextHash0, CpOff2_MaskOff, CpOff2_Whole, // TurnOff c2: !pwr && !gnd && nextHash0 && maskOff
            CpOn_N, CpOn_NextHash0, CpOn_MaskUnsafe, CpOn_Xor, CpOn_Combined, CpOn_Whole;       // TurnOn c1: nextHash0 && (maskUnsafe | xor)

        private static unsafe void CondTallyOff1(int c)
        {
            CpOff1_N++;
            bool h = RecalcHashNext[c] == 0;
            bool m = (PruneMask[c] & PruneTurnOffSkip) == 0;
            if (h) CpOff1_NextHash0++;
            if (m) CpOff1_MaskOff++;
            if (h && m) CpOff1_Whole++;
        }
        private static unsafe void CondTallyOff2(int c2, int npwr, int ngnd)
        {
            CpOff2_N++;
            bool p = c2 != npwr, g = c2 != ngnd;
            bool h = RecalcHashNext[c2] == 0;
            bool m = (PruneMask[c2] & PruneTurnOffSkip) == 0;
            if (p) CpOff2_NotPwr++;
            if (g) CpOff2_NotGnd++;
            if (h) CpOff2_NextHash0++;
            if (m) CpOff2_MaskOff++;
            if (p && g && h && m) CpOff2_Whole++;
        }
        private static unsafe void CondTallyOn(int c1, int c2)
        {
            CpOn_N++;
            bool h = RecalcHashNext[c1] == 0;
            bool mu = (PruneMask[c1] & PruneTurnOnUnsafe) != 0;
            bool x = (NodeStates[c1] ^ NodeStates[c2]) != 0;
            bool comb = mu || x;
            if (h) CpOn_NextHash0++;
            if (mu) CpOn_MaskUnsafe++;
            if (x) CpOn_Xor++;
            if (comb) CpOn_Combined++;
            if (h && comb) CpOn_Whole++;
        }

        // ── settle-pass distribution profiler (DEBUG ONLY) ──
        // Histogram of how many settle waves each ProcessQueue() call takes to reach quiescence
        // (the `iteration` count). Counts ALL ProcessQueue calls — the per-half-cycle clk settle
        // plus the smaller handler-triggered settles (memory writes via WriteBits). Bucketed by exact
        // pass count [0..255]; 255 = overflow. Deterministic ⇒ identical to Release. Use it to revisit
        // the MaxSettlePasses safety cap (Release omits the cap; this shows the real tail).
        internal static readonly long[] SettlePassHist = new long[256];
        internal static long SettleCalls;

        private static void SettlePassTally(int iter)
        {
            SettleCalls++;
            if (iter < 0) iter = 0; else if (iter > 255) iter = 255;
            SettlePassHist[iter]++;
        }

        // ── BFS group-walk DEPTH distribution profiler (DEBUG ONLY) ──
        // Histogram of the max BFS level reached by each AddNodeToGroup walk = how many hops, through
        // currently-ON transistors, from the seed to the farthest conducting member. Depth 0 = singleton
        // group (no conducting neighbour). Only counts walks that actually reach the BFS (cls 0 + the
        // cls-2 nodes that grow); fast-path singletons never enter here. Answers "what's the deepest the
        // conducting BFS ever goes?" Deterministic ⇒ identical to Release.
        internal static readonly long[] BfsDepthHist = new long[256];
        internal static long BfsWalks;

        private static void BfsDepthTally(int depth)
        {
            BfsWalks++;
            if (depth < 0) depth = 0; else if (depth > 255) depth = 255;
            BfsDepthHist[depth]++;
        }
#endif

        // Hard cap on settle passes — DEBUG builds only (Release omits the cap entirely; see ProcessQueue:
        // the in-loop guard cost +2.77%). MetalNES's JS chipsim uses 100; the C++ has none (just a warning).
        // The cap catches a non-converging region during development; the state is a heuristic anyway
        // (see MD/struct/01 §11.2). If it ever trips it's a bug.
        //
        // 2026-05-25: lowered 1000 → 128 after settle-stats measurement across two real workloads:
        //   full_palette.nes / 50K hc:   max 45 iter, p99 in [33-64]
        //   Super Mario Bros. / 71M hc:  max 41 iter, p99 in [17-32]
        // 128 = ~2.8× safety margin over observed max — so in practice it never trips.
#if DEBUG
        // DEBUG-only. Normally 128 (pure non-convergence tripwire). The --settle-cap experiment
        // (TestRunner) lowers it to deliberately ABANDON settles past N passes, to study how an
        // under-settled (timing-violation) chip diverges. SettleCapSilent suppresses the per-trip
        // abort message (a low cap trips ~every settle). Both are DEBUG-only — Release has neither
        // the field nor the cap block (this whole region is #if DEBUG).
        internal static int MaxSettlePasses = 128;
        internal static bool SettleCapSilent = false;
#endif

        // Per-node FIFO double-buffer settle. The hot loop of the engine. (Was a thin ProcessQueue()
        // wrapper around ProcessQueueInterp() for the old Oblivious/Levelize/Codegen dispatch; those
        // paths were removed in the S1 fork, so the wrapper is gone and the two are one method.)
        // NOT [AggressiveInlining]: this method is huge (RecalcNode/RecalcNodeFast/ComputeNodeGroup/
        // AddNodeToGroup/SetNodeState all inline INTO it) and is called from many sites (StepCycle,
        // SetHigh/Low/Float, handlers); forcing it inline measured -1.4% (code bloat). [exp 2026-06-08]
        private static void ProcessQueue()
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
                    if (!SettleCapSilent)
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
#if DEBUG
                        long _dchg = DiagStateChanges;   // wasted-pop profiler (DEBUG only)
#endif
                        RecalcNode(nn);
                        RecalcHash[nn] = 0;
#if DEBUG
                        WasteProfileTally(nn, DiagStateChanges == _dchg);
#endif
                    }
                }
                RecalcListCount = 0;
            }
#if DEBUG
            SettlePassTally(iteration);   // settle-pass distribution profiler (DEBUG only)
#endif
            InvokeCallbacks();   // WireCore.Handlers.cs
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecalcNode(int nn)
        {
            // Supply (Npwr/Ngnd) is NEVER enqueued: EnqueueNode filters it, and SetNodeState's inline enqueue
            // keeps c1 non-supply (Module normalises supply onto c2) + filters c2. So this guard never triggers
            // in Release — kept only as a DEBUG tripwire (and even if it did slip through, SetNodeState's
            // unchanged-state early-out makes it a no-op). Bit-exact with/without it.
#if DEBUG
            System.Diagnostics.Debug.Assert(nn != Npwr && nn != Ngnd, "RecalcNode: a supply node was enqueued — invariant broken");
            if (nn == Npwr || nn == Ngnd) return;
#endif
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

            // [P-5] maintain the dominant-driver record for every member of the just-resolved group
            // (opt-in). Each member's id is from its OWN single supply matching the group value, or 0 —
            // so a member pinned by its own gnd/pwr can be skipped when an unrelated incident gate opens.
            if (EnableDominantBypass)
                for (int i = 0; i < _groupCount; i++) { int m = _groupBuf[i]; DominantGate[m] = ComputeDominantGate(m, newState); }

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
#if DEBUG
            DiagStateChanges++;   // wasted-pop profiler (DEBUG only)
#endif
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
#if DEBUG
                    int npwr = Npwr, ngnd = Ngnd;   // supply-skip is folded into pruneMask now — only the [cond-profile] needs these
#endif
                    // [P-2 turn-off enqueue prune] skip endpoints that become a driverless isolated singleton
                    // the instant this (their only) channel opens — they float and HOLD their previous value, so
                    // re-evaluating them is a guaranteed no-op. Bit 1 (PruneTurnOffSkip) of the shared PruneMask
                    // is the precomputed static safety mask (C1c2Count==1, no supply/PullUp/FC/callback — see
                    // ClassifyTurnOffSkip). Bit-exact. [exp supply-skip fold] supply (ngnd/npwr) is ALSO marked
                    // skip in ClassifyTurnOffSkip, so c2 needs NO explicit `c2!=ngnd && c2!=npwr` guard — it is
                    // now identical to c1. Clause order: more-selective maskOff (≈20% false) before nextHash==0.
                    byte* pruneMask = PruneMask;
                    // [P-5 dominant-driver bypass] opt-in extra skip: an endpoint c held by a single supply
                    // driver whose gate is NOT this gate (nn) can't change when nn opens. domOn==false (default)
                    // short-circuits before any domGate read ⇒ byte-for-byte the P-4 baseline. See WireCore.DominantBypass.cs.
                    bool domOn = EnableDominantBypass;
                    ushort* domGate = DominantGate;
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        int c2a = (ushort)(quad >> 16);
#if DEBUG
                        CondTallyOff1(c1a);
#endif
                        if ((pruneMask[c1a] & PruneTurnOffSkip) == 0 && !(domOn && domGate[c1a] != 0 && domGate[c1a] != nn) && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        // gate going low can *disconnect* the channel, so c2 needs re-eval too
#if DEBUG
                        CondTallyOff2(c2a, npwr, ngnd);
#endif
                        if ((pruneMask[c2a] & PruneTurnOffSkip) == 0 && !(domOn && domGate[c2a] != 0 && domGate[c2a] != nn) && nextHash[c2a] == 0) { nextList[nextCount++] = c2a; nextHash[c2a] = 1; }   // identical to c1 (supply-skip folded into pruneMask)
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        int c2b = (ushort)(quad >> 48);
#if DEBUG
                        CondTallyOff1(c1b);
#endif
                        if ((pruneMask[c1b] & PruneTurnOffSkip) == 0 && !(domOn && domGate[c1b] != 0 && domGate[c1b] != nn) && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
#if DEBUG
                        CondTallyOff2(c2b, npwr, ngnd);
#endif
                        if ((pruneMask[c2b] & PruneTurnOffSkip) == 0 && !(domOn && domGate[c2b] != 0 && domGate[c2b] != nn) && nextHash[c2b] == 0) { nextList[nextCount++] = c2b; nextHash[c2b] = 1; }
                        p += 4;
                    }
                }
                else
                {
                    // gate going high: the channel CONDUCTS, so c1 and c2 merge; single-sided enqueue of
                    // c1 suffices (BFS traverses the ON channel to c2).
                    // [same-state turn-on prune] if c1 and c2 already hold the same state, merging two
                    // equal-state groups can't change any value — PROVIDED the merged group resolves through
                    // the monotone driven-priority LUT. Bit 0 (PruneTurnOnUnsafe) of the shared PruneMask forces
                    // the enqueue for nodes that can resolve non-monotonically (no-PullUp floating/hold-previous,
                    // or ForceCompute Gnd+Pwr cancel; cleared by P-3/4 for the cap<all-neighbours subset).
                    // Bit-exact (golden checksum); +11.85% (14/14 paired). See ClassifyPruneTaint.
                    // Micro-form: the keep-condition is folded branchlessly —
                    // (mask&bit0) | (state^state) != 0  ==  unsafe || state!=state. The `if` stays — a pruned
                    // node must NOT have nextHash set, or it would look queued without being in the list.
                    // Clause ORDER (2026-06-08): the combined keep-term is tested FIRST, nextHash==0 LAST.
                    // The DEBUG [cond-profile] (CondTallyOn) measured nextHash==0 ≈97.7% true here — a near-
                    // useless lead gate — while the combined term is ≈41.8% false, so leading with it short-
                    // circuits the (cheaper, single-load) nextHash check more often. +~1% (29/40 paired over
                    // two 20-round runs, bit-exact). The earlier "nextHash==0 first" assumed already-queued was
                    // common; the profile disproved that for full_palette. (Re-check on a busier ROM if ordering
                    // is ever revisited — already-queued rate is workload-dependent.)
                    byte* nodeStates = NodeStates;
                    byte* pruneMask = PruneMask;
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        int c2a = (ushort)(quad >> 16);
#if DEBUG
                        CondTallyOn(c1a, c2a);
#endif
                        if (((pruneMask[c1a] & PruneTurnOnUnsafe) | (nodeStates[c1a] ^ nodeStates[c2a])) != 0 && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        int c2b = (ushort)(quad >> 48);
#if DEBUG
                        CondTallyOn(c1b, c2b);
#endif
                        if (((pruneMask[c1b] & PruneTurnOnUnsafe) | (nodeStates[c1b] ^ nodeStates[c2b])) != 0 && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
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

        // ── one half-cycle: toggle the master clock node, advance time ──
        public static void Step(int count) { for (int i = 0; i < count; i++) StepCycle(); }

        private static void StepCycle()
        {
            // [H1] clock toggle inlined here (it was the SOLE entry of the old handler-chain delegate) —
            // drops the per-half-cycle delegate invoke + null-check and lets the toggle inline. Uses the
            // static ClockNode (set by AttachClockHandler) instead of a captured closure local.
            // [#1 test] branchless toggle: clock always flips, so the state-direction branch + the
            // SetXQueued changed-check are both removable. next = state^1; (8>>next) maps 1->SetHigh(4),
            // 0->SetLow(8); clear both drive flags then OR the new one — bit-identical to SetHigh/LowQueued.
            int clk = ClockNode;
            if (clk != EmptyNode)
            {
                ref NodeInfo ns = ref NodeInfos[clk];
                int next = NodeStates[clk] ^ 1;
                ns.Flags = (ns.Flags & ~(NodeFlags.SetHigh | NodeFlags.SetLow)) | (NodeFlags)(8 >> next);
                EnqueueNode(clk);
                ProcessQueue();
            }
            Time++;
        }
    }
}
