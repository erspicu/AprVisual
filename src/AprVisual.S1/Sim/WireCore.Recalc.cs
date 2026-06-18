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
        /// the same Time are bit-identical per node). Always hashed in ORIGINAL (pre-renumber) id
        /// order — under the class-major auto-renumber the loop walks NodeStates through the
        /// permutation — so the value is numbering-independent and directly comparable with the
        /// historical goldens (0x794A43ABDF169ADA @300k etc.).</summary>
        public static ulong NodeStatesChecksum()
        {
            // Under the auto renumber, hash in ORIGINAL id order (via the permutation) so the checksum stays
            // directly comparable with the golden values of the identity numbering.
            ushort* perm = RenumberPerm;
            int permLen = RenumberPermLen;
            ulong h = 14695981039346656037UL;
            if (perm == null)
                for (int i = 0; i < NodeCount; i++) { h ^= NodeStates[i]; h *= 1099511628211UL; }
            else
                for (int i = 0; i < NodeCount; i++) { h ^= NodeStates[i < permLen ? perm[i] : i]; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>Re-evaluate every (non-supply) node — used at power-on after Reset(). Port of Wires::recomputeAllNodes.
        /// The ONLY id-order-dependent site in the engine (power-on settle order feeds the floating
        /// hold-previous tie-break) — under the auto renumber it iterates in ORIGINAL id order via the
        /// permutation, so a renumbered run is bit-exact with the identity run.</summary>
        public static void RecomputeAllNodes()
        {
            // Without a verified class layout the RangeSafe* degenerate boundaries are in force —
            // correct but with the P-1..P-4 prunes disabled. That state is INTENTIONAL for selftest /
            // hand-built netlists and for the auto-renumber's pass-1 warm-up (RenumberPerm == null);
            // it is only alarming when a permutation EXISTS but its verification failed.
            if (!RangePruneOk && RenumberPerm != null)
                Console.Error.WriteLine("WireCore: settling WITHOUT a verified range-prune layout — prunes disabled, expect ~2x slower (range verification failed)");
            ushort* perm = RenumberPerm;
            int permLen = RenumberPermLen;
            for (int i = 0; i < NodeCount; i++)
            {
                int nn = perm == null ? i : (i < permLen ? perm[i] : i);
                if (nn != Npwr && nn != Ngnd && Nodes[nn] != null) EnqueueNode(nn);
            }
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
        // [P-2b candidate diagnostic] the "single-channel pure-PullUp" class: PullUp + exactly one
        // c1c2 channel + ZERO own gnd/pwr gates (+ inline). When its only channel turns OFF it isolates
        // and provably resolves to 1 (PullUp) — so a turn-off enqueue is skippable iff state==1.
        // These counters size that prize: class pops, the no-change subset, and the skippable subset.
        internal static long DiagP2bPops, DiagNCP2b, DiagNCP2bState1;
        // [B1 pair path] pops resolved by the inline 2-node-group path (vs falling to the BFS)
        internal static long DiagPairPath;

        private static unsafe void WasteProfileTally(int nn, bool noChange)
        {
            DiagPops++;
            {   // P-2b class traffic (counted for ALL pops, change or not — sizes the candidate's reach)
                NodeInfo* d2 = NodeInfos + nn;
                if ((d2->Flags & NodeFlags.PullUp) != 0 && d2->Inline != 0 && d2->C1c2Count == 1
                    && d2->GndCount == 0 && d2->PwrCount == 0
                    && (d2->Flags & (NodeFlags.ForceCompute | NodeFlags.HasCallback)) == 0)
                    DiagP2bPops++;
            }
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
            else if ((d->Flags & NodeFlags.PullUp) != 0)
            {
                DiagNCPullUp++;
                // P-2b class membership (structural): single-channel pure-PullUp (see field comment).
                if (d->Inline != 0 && d->C1c2Count == 1 && d->GndCount == 0 && d->PwrCount == 0
                    && (d->Flags & (NodeFlags.ForceCompute | NodeFlags.HasCallback)) == 0)
                {
                    DiagNCP2b++;
                    if (NodeStates[nn] != 0) DiagNCP2bState1++;   // skippable: would resolve to 1 anyway
                }
            }
            else if (d->Inline != 0 ? (d->GndCount != 0 || d->PwrCount != 0) : (d->TlistC1gnd != 0 || d->TlistC1pwr != 0)) DiagNCSupply++;
            else DiagNCOther++;
        }

        // (the SetNodeState &&-clause [cond-profile] profiler was REMOVED 2026-06-11: its purpose —
        //  measuring per-clause true-rates to order the PruneMask tests — died with the mask reads;
        //  the range-prune compares have no reorderable clause left. See git history if ever needed.)

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

        // ── Co-activity / cache-line headroom profiler (DEBUG ONLY) ──
        // Decision gate for "co-activity node renumbering": per half-cycle window, how many DISTINCT
        // nodes pop, and how many DISTINCT NodeInfo cache lines (64B = 4 NodeInfos, line = nn>>2) they
        // touch under the CURRENT numbering — vs the ideal packing floor ceil(distinct/4). The ratio
        // current/ideal is the max line-compression a perfect co-activity permutation could deliver.
        // If it's near 1, the physical-layout numbering already co-locates co-fired nodes → renumber
        // can't gain. Also tracks the GLOBAL hot set (nodes that ever pop) and its line footprint.
        internal static long CoWindows, CoSumPops, CoSumDistinctNodes, CoSumDistinctLines;
        internal static int CoGlobalNodes, CoGlobalLines;
        private static long[]? _coLastNode, _coLastLine;   // lastSeen window id per node / per line
        private static long _coCurWindow = -1;
        private static int _coWinPops, _coWinNodes, _coWinLines;

        private static void CoActivityTally(int nn)
        {
            // steady-state only: the power-on RecomputeAllNodes settle is one giant window that pops ~ALL
            // nodes in id order — including it contaminates meanPos for rarely-popping nodes (scatters them
            // among the hot core) and dilutes the headroom stats. Skip until well past the reset sequence.
            if (Time < 384) return;
            if (_coLastNode == null) { _coLastNode = new long[NodeCount]; _coLastLine = new long[(NodeCount >> 2) + 1];
                for (int i = 0; i < _coLastNode.Length; i++) _coLastNode[i] = -1;
                for (int i = 0; i < _coLastLine!.Length; i++) _coLastLine[i] = -1; }
            if (Time != _coCurWindow)
            {
                if (_coCurWindow >= 0) { CoWindows++; CoSumPops += _coWinPops; CoSumDistinctNodes += _coWinNodes; CoSumDistinctLines += _coWinLines; }
                _coCurWindow = Time; _coWinPops = _coWinNodes = _coWinLines = 0;
            }
            _coWinPops++;
            if (_coLastNode[nn] == -1) CoGlobalNodes++;
            if (_coLastNode[nn] != Time) { _coLastNode[nn] = Time; _coWinNodes++; }
            int line = nn >> 2;
            if (_coLastLine![line] == -1) CoGlobalLines++;
            if (_coLastLine[line] != Time) { _coLastLine[line] = Time; _coWinLines++; }
        }

        // ── CPU/PPU boundary profiler (DEBUG ONLY) — feasibility test for the "split the two chips
        // onto two threads with coarse step-granularity sync" (PDES) idea. Two questions:
        //   (1) How often do the CPU<->PPU COUPLING WIRES change state? The nes-001 cut is exactly
        //       io_db[7:0]≡cpu.db (data), io_ab[2:0]≡cpu.ab (reg addr), io_ce (PPU select),
        //       cpu.nmi≡ppu.int, cpu.rw≡ppu.io_rw. db/ab/rw also carry CPU<->RAM traffic (change every
        //       CPU cycle) so they OVER-count; io_ce + nmi transitions are the TRUE CPU<->PPU exchange
        //       rate — if those are sparse per hc, a thread can run ahead and sync only at them.
        //   (2) Per hc, are BOTH chips active (a cpu.* AND a ppu.* node popped)? If most hc fire both,
        //       the chips are entangled hc-by-hc and time-decoupling buys nothing.
        // All deterministic ⇒ event counts identical to Release. Init via InitBoundaryDiag() before the
        // bench loop; read the [cpu-ppu-boundary] line in TestRunner.
        internal static int[]? CutNodeKind;   // [NodeCount] 0=none 1=db 2=ab 3=io_ce 4=nmi 5=rw
        internal static byte[]? NodeDomain;   // [NodeCount] 0=board 1=cpu 2=ppu
        internal static readonly long[] DiagCut = new long[6];      // state-changes per cut kind
        internal static readonly int[]  CutResolved = new int[6];   // how many node ids got each kind
        internal static long DiagPopCpu, DiagPopPpu, DiagPopBoard;
        internal static long DiagHcSeen, DiagHcBothChips, DiagHcCpuOnly, DiagHcPpuOnly;
        private static long _bndCurHc = -1; private static bool _bndCpu, _bndPpu;

        // per-module pop histogram (visibility) + per-side pop totals (the PDES work-balance / ceiling).
        internal static string[]? ModuleNames; private static int[]? NodeModule; internal static long[]? DiagModulePops;
        private static byte[]? NodeSide;   // 0 neutral, 1 cpu-side, 2 ppu-side
        internal static readonly long[] DiagSidePops = new long[3];

        private static string ModuleKey(string nm)
        {
            int dot = nm.IndexOf('.');
            if (dot > 0) return nm.Substring(0, dot);
            int e = nm.Length; while (e > 0 && char.IsDigit(nm[e - 1])) e--;   // BD0->BD, BA13->BA
            return e > 0 ? nm.Substring(0, e) : nm;
        }

        // Assign a board/chip module to a PDES side. CPU side: 2A03 + work-RAM + addr decoder +
        // controller buffers/ports + CIC. PPU side: 2C02 + addr latch + VRAM + a13 inverter + the
        // BD/BA video bus. Cartridge straddles (PRG=CPU, CHR=PPU) so it's split by pin name; pure
        // supply/clock/reset are neutral (the clock is broadcast — each thread ticks its own).
        private static byte SideOf(string nm)
        {
            if (nm.StartsWith("cpu.", StringComparison.Ordinal)) return 1;
            if (nm.StartsWith("ppu.", StringComparison.Ordinal)) return 2;
            if (nm.StartsWith("u1.", StringComparison.Ordinal) || nm.StartsWith("u3.", StringComparison.Ordinal)
             || nm.StartsWith("u7.", StringComparison.Ordinal) || nm.StartsWith("u8.", StringComparison.Ordinal)
             || nm.StartsWith("port0.", StringComparison.Ordinal) || nm.StartsWith("port1.", StringComparison.Ordinal)
             || nm.StartsWith("u10.", StringComparison.Ordinal)) return 1;
            if (nm.StartsWith("u2.", StringComparison.Ordinal) || nm.StartsWith("u4.", StringComparison.Ordinal)
             || nm.StartsWith("u9.", StringComparison.Ordinal)
             || nm.StartsWith("BD", StringComparison.Ordinal) || nm.StartsWith("BA", StringComparison.Ordinal)) return 2;
            if (nm.StartsWith("cart", StringComparison.Ordinal))
            {
                if (nm.Contains("ppu", StringComparison.OrdinalIgnoreCase) || nm.Contains("chr", StringComparison.OrdinalIgnoreCase)) return 2;
                if (nm.Contains("cpu", StringComparison.OrdinalIgnoreCase) || nm.Contains("prg", StringComparison.OrdinalIgnoreCase)) return 1;
                return 0;
            }
            return 0;   // vcc/vss/clk/res/func<...>
        }

        internal static void InitBoundaryDiag()
        {
            NodeDomain = new byte[NodeCount];
            CutNodeKind = new int[NodeCount];
            NodeModule = new int[NodeCount];
            NodeSide = new byte[NodeCount];
            var idx = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < NodeCount; i++)
            {
                string nm = GetNodeName(i);
                NodeDomain[i] = (byte)(nm.StartsWith("cpu.", StringComparison.Ordinal) ? 1
                                     : nm.StartsWith("ppu.", StringComparison.Ordinal) ? 2 : 0);
                NodeSide[i] = SideOf(nm);
                string key = ModuleKey(nm);
                if (!idx.TryGetValue(key, out int gi)) { gi = names.Count; idx[key] = gi; names.Add(key); }
                NodeModule[i] = gi;
            }
            ModuleNames = names.ToArray();
            DiagModulePops = new long[ModuleNames.Length];
            void Tag(string name, int kind) { int n = LookupNode(name); if (n != EmptyNode && n != Npwr && n != Ngnd) { CutNodeKind[n] = kind; CutResolved[kind]++; } }
            for (int b = 0; b <= 7; b++) Tag($"cpu.db{b}", 1);
            for (int b = 0; b <= 2; b++) Tag($"cpu.ab{b}", 2);
            Tag("ppu.io_ce", 3);
            Tag("cpu.nmi", 4);
            Tag("cpu.rw", 5);
        }

        private static void BoundaryPopTally(int nn)
        {
            if (NodeDomain == null) return;
            byte d = NodeDomain[nn];
            if (d == 1) DiagPopCpu++; else if (d == 2) DiagPopPpu++; else DiagPopBoard++;
            DiagModulePops![NodeModule![nn]]++;
            DiagSidePops[NodeSide![nn]]++;
            if (Time != _bndCurHc)
            {
                if (_bndCurHc >= 0)
                {
                    DiagHcSeen++;
                    if (_bndCpu && _bndPpu) DiagHcBothChips++;
                    else if (_bndCpu) DiagHcCpuOnly++;
                    else if (_bndPpu) DiagHcPpuOnly++;
                }
                _bndCurHc = Time; _bndCpu = false; _bndPpu = false;
            }
            if (d == 1) _bndCpu = true; else if (d == 2) _bndPpu = true;
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
        // DEBUG-only pure non-convergence tripwire — Release has neither the const nor the cap block.
        // (The old --settle-cap/-SettleCapSilent under-settle EXPERIMENT was removed 2026-06-11: its
        // study concluded — abandoning even the deepest 0.58% of settles diverges within <1000 hc, no
        // graceful degradation — so the knob had no remaining use. See git history / the memory note.)
        internal const int MaxSettlePasses = 128;
#endif

        // Per-node FIFO double-buffer settle. The hot loop of the engine. (Was a thin ProcessQueue()
        // wrapper around ProcessQueueInterp() for the old Oblivious/Levelize/Codegen dispatch; those
        // paths were removed in the S1 fork, so the wrapper is gone and the two are one method.)
        // NOT [AggressiveInlining]: this method is huge (RecalcNode/RecalcNodeFast/ComputeNodeGroup/
        // AddNodeToGroup/SetNodeState all inline INTO it) and is called from many sites (StepCycle,
        // SetHigh/Low/Float, handlers); forcing it inline measured -1.4% (code bloat). [exp 2026-06-08]
        // [hybrid-full-pasted] Full faithful build of the user-supplied "main-loop ultra-specialize + cold
        // isolation" proposal. cls==1 (77% static singleton) is FULLY inlined here (no RecalcNode/
        // RecalcNodeFast/SetNodeState call); cls==2 and BFS are forced [NoInlining]. Queue cursors are
        // hoisted to METHOD-level locals (kept in registers across all waves), swapped as locals, static
        // fields written back at the end. Two correctness fixes vs the raw proposal: (1) the static
        // RecalcHash is synced to curHash after each swap, because ComputeNodeGroup/AddNodeToGroup clear
        // member hashes through the STATIC RecalcHash; (2) the cold methods do their writeback through the
        // PASSED next-cursors (shared WritebackNode), NOT SetNodeState (which uses the static count) — else
        // cls1's local nextCount would desync from the cold paths. Bit-identical to main. RecalcNode (below)
        // is kept ONLY for the cold first-touch capture loop.
        private static unsafe void ProcessQueue()
        {
#if DEBUG
            int iteration = 0;
#endif
            int* curList = RecalcList;
            byte* curHash = RecalcHash;
            int* nextList = RecalcListNext;
            byte* nextHash = RecalcHashNext;
            byte* nodeStates = NodeStates;
            int rS = RangePruneS, rA = RangePruneA, rB = RangePruneB;

            while (RecalcListNextCount != 0)
            {
#if DEBUG
                if (++iteration == 64)
                    Console.Error.WriteLine($"WireCore.ProcessQueue: settle pass {iteration} (still propagating, past p99 — see MD/struct/01 §11.2)");
                if (iteration > MaxSettlePasses)
                {
                    Console.Error.WriteLine($"WireCore.ProcessQueue: aborting after {MaxSettlePasses} settle passes ({RecalcListNextCount} nodes still pending) — leaving state as-is");
                    for (int i = 0; i < RecalcListNextCount; i++) nextHash[nextList[i]] = 0;
                    RecalcListNextCount = 0;
                    break;
                }
#endif
                // swap cur ↔ next (locals)
                int* tmpList = curList; curList = nextList; nextList = tmpList;
                byte* tmpHash = curHash; curHash = nextHash; nextHash = tmpHash;
                int curCount = RecalcListNextCount;
                RecalcListCount = curCount;
                RecalcListNextCount = 0;
                int nextCount = 0;

                // sync the STATIC current-wave cursors so the BFS cold path (ComputeNodeGroup/AddNodeToGroup,
                // which clear member hashes via the static RecalcHash) operates on the right buffer.
                RecalcList = curList; RecalcHash = curHash;

                for (int i = 0; i < curCount; i++)
                {
                    int nn = curList[i];
                    if (curHash[nn] == 0) continue;
                    curHash[nn] = 0;
#if DEBUG
                    long _dchg = DiagStateChanges;
#endif
                    byte cls = IsPureLogic[nn];
                    if (cls == 1)
                    {
                        // ── fully-inline static singleton: resolve flags, then specialized writeback ──
                        NodeInfo* ns = NodeInfos + nn;
                        int flags = (int)ns->Flags;
                        if (ns->Inline != 0)
                        {
                            ushort* pay = ns->InlinePayload;
                            int gndStart = ns->C1c2Count << 1;
                            int gndEnd = gndStart + ns->GndCount;
                            int anyG = 0; for (int k = gndStart; k < gndEnd; k++) anyG |= nodeStates[pay[k]]; flags |= anyG << 5;
                            int pwrEnd = gndEnd + ns->PwrCount;
                            int anyP = 0; for (int k = gndEnd; k < pwrEnd; k++) anyP |= nodeStates[pay[k]]; flags |= anyP << 4;
                        }
                        else
                        {
                            if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; int any = 0; while (*p != 0) any |= nodeStates[*p++]; flags |= any << 5; }
                            if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; int any = 0; while (*p != 0) any |= nodeStates[*p++]; flags |= any << 4; }
                        }
                        if (flags != 0)
                        {
                            byte newState = FlagsToState[flags];
                            if (nodeStates[nn] != newState)
                                WritebackNode(nn, newState, nextList, nextHash, ref nextCount, nodeStates, rS, rA, rB);
                        }
                    }
                    else if (cls == 2)
                    {
                        if (!TryProcessCls2Inline(nn, nodeStates, nextList, nextHash, ref nextCount, curHash, rS, rA, rB))
                            nextCount = ProcessBFSFallback(nn, nodeStates, nextList, nextHash, nextCount, rS, rA, rB);
                    }
                    else
                    {
                        nextCount = ProcessBFSFallback(nn, nodeStates, nextList, nextHash, nextCount, rS, rA, rB);
                    }
#if DEBUG
                    WasteProfileTally(nn, DiagStateChanges == _dchg);
                    CoActivityTally(nn);
                    BoundaryPopTally(nn);
#endif
                }
                RecalcListNextCount = nextCount;
                RecalcListCount = 0;
            }
            // write back the swapped cursor statics for code that runs between ProcessQueue calls.
            RecalcList = curList; RecalcHash = curHash;
            RecalcListNext = nextList; RecalcHashNext = nextHash;
#if DEBUG
            SettlePassTally(iteration);
#endif
            InvokeCallbacks();   // WireCore.Handlers.cs
        }

        // Shared node writeback: write state + walk the gated fan-out, enqueuing into the PASSED next-wave
        // cursors (kept consistent with the caller's local nextCount). AggressiveInlining: folds into the
        // cls1 hot site directly; for the cold methods it inlines using their ref-threaded cursor.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WritebackNode(int nn, byte newState, int* nextList, byte* nextHash, ref int nextCount, byte* nodeStates, int rS, int rA, int rB)
        {
            nodeStates[nn] = newState;
#if DEBUG
            DiagStateChanges++;
            if (CutNodeKind != null) { int _ck = CutNodeKind[nn]; if (_ck != 0) DiagCut[_ck]++; }
#endif
            int tg = NodeTlistGates[nn];
            if (tg == 0) return;
            ushort* p = TransistorList + tg;
            if (newState == 0)
            {
                while (true)
                {
                    ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                    int c1a = (ushort)quad; if (c1a == 0) break;
                    int c2a = (ushort)(quad >> 16);
                    if (c1a >= rS && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                    if (c2a >= rS && nextHash[c2a] == 0) { nextList[nextCount++] = c2a; nextHash[c2a] = 1; }
                    int c1b = (ushort)(quad >> 32); if (c1b == 0) break;
                    int c2b = (ushort)(quad >> 48);
                    if (c1b >= rS && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
                    if (c2b >= rS && nextHash[c2b] == 0) { nextList[nextCount++] = c2b; nextHash[c2b] = 1; }
                    p += 4;
                }
            }
            else
            {
                while (true)
                {
                    ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                    int c1a = (ushort)quad; if (c1a == 0) break;
                    int c2a = (ushort)(quad >> 16);
                    if ((c1a < rA || c1a >= rB || nodeStates[c1a] != nodeStates[c2a]) && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                    int c1b = (ushort)(quad >> 32); if (c1b == 0) break;
                    int c2b = (ushort)(quad >> 48);
                    if ((c1b < rA || c1b >= rB || nodeStates[c1b] != nodeStates[c2b]) && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
                    p += 4;
                }
            }
        }

        // [NoInlining cold] cls==2: B1-pair detect / dynamic-singleton. Returns false (no mutation) to bail
        // to BFS; true if resolved here (pair or singleton). Writeback via passed cursors (NOT SetNodeState).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryProcessCls2Inline(int nn, byte* nodeStates, int* nextList, byte* nextHash, ref int nextCount, byte* curHash, int rS, int rA, int rB)
        {
            NodeInfo* ns = NodeInfos + nn;
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload;
                int n2 = ns->C1c2Count << 1;
                for (int k = 0; k < n2; k += 2)
                {
                    if (nodeStates[pay[k]] != 0)
                    {
                        int o = pay[k + 1];
                        if (o == nn) return false;
                        for (int k2 = k + 2; k2 < n2; k2 += 2) if (nodeStates[pay[k2]] != 0) return false;
                        NodeInfo* os = NodeInfos + o;
                        if (os->Inline == 0 || (os->Flags & (NodeFlags.HasCallback | NodeFlags.ForceCompute)) != 0) return false;
                        ushort* opay = os->InlinePayload;
                        int on2 = os->C1c2Count << 1;
                        for (int k2 = 0; k2 < on2; k2 += 2) if (nodeStates[opay[k2]] != 0 && opay[k2 + 1] != nn) return false;
                        curHash[o] = 0;
                        int flags = (int)ns->Flags | (int)os->Flags;
                        int anyG = 0, anyP = 0;
                        int sGe = n2 + ns->GndCount, sPe = sGe + ns->PwrCount;
                        for (int j = n2; j < sGe; j++) anyG |= nodeStates[pay[j]];
                        for (int j = sGe; j < sPe; j++) anyP |= nodeStates[pay[j]];
                        int oGe = on2 + os->GndCount, oPe = oGe + os->PwrCount;
                        for (int j = on2; j < oGe; j++) anyG |= nodeStates[opay[j]];
                        for (int j = oGe; j < oPe; j++) anyP |= nodeStates[opay[j]];
                        flags |= (anyG << 5) | (anyP << 4);
                        byte v = flags != 0 ? FlagsToState[flags]
                               : (NodeConnections[o] > NodeConnections[nn] ? nodeStates[o] : nodeStates[nn]);
                        if (nodeStates[nn] != v) WritebackNode(nn, v, nextList, nextHash, ref nextCount, nodeStates, rS, rA, rB);
                        if (nodeStates[o]  != v) WritebackNode(o,  v, nextList, nextHash, ref nextCount, nodeStates, rS, rA, rB);
#if DEBUG
                        DiagPairPath++;
#endif
                        return true;
                    }
                }
            }
            else
            {
                ushort* p = TransistorList + ns->TlistC1c2s;
                while (*p != 0) { if (nodeStates[*p] != 0) return false; p += 2; }
            }
            // no ON c1c2 gate ⇒ conducting group is {nn} ⇒ resolve as static singleton inline.
            {
                int flags = (int)ns->Flags;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int gndStart = ns->C1c2Count << 1;
                    int gndEnd = gndStart + ns->GndCount;
                    int anyG = 0; for (int k = gndStart; k < gndEnd; k++) anyG |= nodeStates[pay[k]]; flags |= anyG << 5;
                    int pwrEnd = gndEnd + ns->PwrCount;
                    int anyP = 0; for (int k = gndEnd; k < pwrEnd; k++) anyP |= nodeStates[pay[k]]; flags |= anyP << 4;
                }
                else
                {
                    if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; int any = 0; while (*p != 0) any |= nodeStates[*p++]; flags |= any << 5; }
                    if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; int any = 0; while (*p != 0) any |= nodeStates[*p++]; flags |= any << 4; }
                }
                if (flags != 0)
                {
                    byte newState = FlagsToState[flags];
                    if (nodeStates[nn] != newState) WritebackNode(nn, newState, nextList, nextHash, ref nextCount, nodeStates, rS, rA, rB);
                }
            }
            return true;
        }

        // [NoInlining cold] generic BFS group resolve + writeback + callback. ComputeNodeGroup reads the
        // STATIC RecalcHash (synced after the swap) for member-hash clearing; writeback via passed cursors.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ProcessBFSFallback(int nn, byte* nodeStates, int* nextList, byte* nextHash, int nextCount, int rS, int rA, int rB)
        {
            byte newState = ComputeNodeGroup(nn);
            ushort* gb = _groupBuf;
            int gc = _groupCount;
            for (int m = 0; m < gc; m++) { int gn = gb[m]; if (nodeStates[gn] != newState) WritebackNode(gn, newState, nextList, nextHash, ref nextCount, nodeStates, rS, rA, rB); }
            if ((_groupFlags & NodeFlags.HasCallback) != 0)
            {
                var cbByNode = _callbackByNode!;
                for (int m = 0; m < gc; m++) { var cb = cbByNode[gb[m]]; if (cb != null) EnqueueCallback(cb); }
            }
            return nextCount;
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
                    for (int k = 0; k < n2; k += 2)
                    {
                        if (NodeStates[pay[k]] != 0)
                        {
                            // [B1 pair path, 2026-06-12] size-2 groups are 77% of all BFS walks (30.5% of ALL
                            // pops) — when the group is provably exactly {nn, o}, resolve it inline without the
                            // _groupBuf/_inGroup machinery. Bit-exactness obligations (each mirrors the BFS):
                            //   · o == nn (self-channel) → BFS;  any SECOND ON seed gate → BFS (conservative —
                            //     even a parallel channel to the same o falls back);
                            //   · o overflow (Inline==0) or HasCallback/ForceCompute → BFS (callback enqueue +
                            //     FC resolution stay on the BFS path; the seed's cls2 classify already excludes
                            //     those flags on nn);
                            //   · any ON channel of o leading to a third node → BFS (group ≥ 3);
                            //   · ALL bails happen before any mutation, so the fallback is exact;
                            //   · RecalcHash[o] = 0 — the BFS member clear (cancels o's pending pop THIS wave);
                            //   · flags = nn.Flags | o.Flags | Gnd/Pwr OR-all over BOTH nodes' supply gates
                            //     (same OR-ed value as the walk's early-break accumulation, order-free);
                            //   · floating (flags==0): strict larger-cap wins, seed wins ties — exactly
                            //     GetNodeValue over _groupBuf = [nn, o];
                            //   · SetNodeState(nn) THEN SetNodeState(o) — the walk's writeback order, so the
                            //     next-wave enqueue-append order (= pop order = Gauss-Seidel semantics) matches.
                            int o = pay[k + 1];
                            if (o == nn) goto FallbackBFS;
                            for (int k2 = k + 2; k2 < n2; k2 += 2) if (NodeStates[pay[k2]] != 0) goto FallbackBFS;
                            NodeInfo* os = NodeInfos + o;
                            if (os->Inline == 0 || (os->Flags & (NodeFlags.HasCallback | NodeFlags.ForceCompute)) != 0) goto FallbackBFS;
                            ushort* opay = os->InlinePayload;
                            int on2 = os->C1c2Count << 1;
                            for (int k2 = 0; k2 < on2; k2 += 2) if (NodeStates[opay[k2]] != 0 && opay[k2 + 1] != nn) goto FallbackBFS;
                            // committed — group is exactly {nn, o}
                            RecalcHash[o] = 0;
                            int flags = (int)ns->Flags | (int)os->Flags;
                            int anyG = 0, anyP = 0;
                            int sGe = n2 + ns->GndCount, sPe = sGe + ns->PwrCount;
                            for (int j = n2; j < sGe; j++) anyG |= NodeStates[pay[j]];
                            for (int j = sGe; j < sPe; j++) anyP |= NodeStates[pay[j]];
                            int oGe = on2 + os->GndCount, oPe = oGe + os->PwrCount;
                            for (int j = on2; j < oGe; j++) anyG |= NodeStates[opay[j]];
                            for (int j = oGe; j < oPe; j++) anyP |= NodeStates[opay[j]];
                            flags |= (anyG << 5) | (anyP << 4);
                            byte v = flags != 0 ? FlagsToState[flags]
                                   : (NodeConnections[o] > NodeConnections[nn] ? NodeStates[o] : NodeStates[nn]);
                            SetNodeState(nn, v);
                            SetNodeState(o, v);
#if DEBUG
                            DiagPairPath++;
#endif
                            return;
                        }
                    }
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
#if DEBUG
            DiagStateChanges++;   // wasted-pop profiler (DEBUG only)
            if (CutNodeKind != null) { int _ck = CutNodeKind[nn]; if (_ck != 0) DiagCut[_ck]++; }   // cpu/ppu cut-wire transition (DEBUG only)
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
                    // [P-2 turn-off enqueue prune, range form (2026-06-10)] skip endpoints that become a
                    // driverless isolated singleton the instant this (their only) channel opens — they float
                    // and HOLD their previous value, so re-evaluating them is a guaranteed no-op (the static
                    // safety class: C1c2Count==1, no supply/PullUp/FC/callback — see ClassifyTurnOffSkip).
                    // The class-major auto-renumber (WireCore.Renumber.cs) makes that class one contiguous id
                    // block, so the test is a REGISTER COMPARE: skip ⇔ c < S. Supply (ngnd/npwr, ids 1,2 < 3
                    // ≤ S) rides the same compare — the historical explicit `c2!=ngnd && c2!=npwr` guard and
                    // its successor (the supply-skip mask fold) are both subsumed. Boundaries verified against
                    // the freshly computed PruneMask at every Reset. Bit-exact.
                    int rS = RangePruneS;
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        int c2a = (ushort)(quad >> 16);
                        if (c1a >= rS && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        // gate going low can *disconnect* the channel, so c2 needs re-eval too
                        if (c2a >= rS && nextHash[c2a] == 0) { nextList[nextCount++] = c2a; nextHash[c2a] = 1; }
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        int c2b = (ushort)(quad >> 48);
                        if (c1b >= rS && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
                        if (c2b >= rS && nextHash[c2b] == 0) { nextList[nextCount++] = c2b; nextHash[c2b] = 1; }
                        p += 4;
                    }
                }
                else
                {
                    // gate going high: the channel CONDUCTS, so c1 and c2 merge; single-sided enqueue of
                    // c1 suffices (BFS traverses the ON channel to c2).
                    // [same-state turn-on prune (P-1), range form (2026-06-10)] if c1 and c2 already hold
                    // the same state, merging two equal-state groups can't change any value — PROVIDED the
                    // merged group resolves through the monotone driven-priority LUT. The "unsafe" class
                    // that must always enqueue (no-PullUp floating/hold-previous, or ForceCompute Gnd+Pwr
                    // cancel; minus the P-3/4 cap<all-neighbours un-taint — see ClassifyPruneTaint) sits in
                    // the two OUTER id blocks of the class-major renumber, so the test is two REGISTER
                    // COMPARES: unsafe ⇔ c < A || c >= B (c1 is never supply). Boundaries verified against
                    // the computed PruneMask at every Reset. The keep-term stays FIRST, nextHash==0 LAST
                    // (the 2026-06-08 [cond-profile] showed nextHash==0 is ≈97.7% true — a near-useless lead
                    // gate; re-profile on a busier ROM if this ordering is ever revisited). The `if` stays —
                    // a pruned node must NOT have nextHash set, or it would look queued without being listed.
                    // Bit-exact (golden checksum); P-1 mask form was +11.85%, range form adds +3.6% on top.
                    byte* nodeStates = NodeStates;
                    int rA = RangePruneA, rB = RangePruneB;
                    while (true)
                    {
                        ulong quad = Unsafe.ReadUnaligned<ulong>(p);
                        int c1a = (ushort)quad;
                        if (c1a == 0) break;
                        int c2a = (ushort)(quad >> 16);
                        if ((c1a < rA || c1a >= rB || nodeStates[c1a] != nodeStates[c2a]) && nextHash[c1a] == 0) { nextList[nextCount++] = c1a; nextHash[c1a] = 1; }
                        int c1b = (ushort)(quad >> 32);
                        if (c1b == 0) break;
                        int c2b = (ushort)(quad >> 48);
                        if ((c1b < rA || c1b >= rB || nodeStates[c1b] != nodeStates[c2b]) && nextHash[c1b] == 0) { nextList[nextCount++] = c1b; nextHash[c1b] = 1; }
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
