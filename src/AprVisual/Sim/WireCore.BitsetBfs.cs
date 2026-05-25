using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Bit-parallel BFS experiment (branch: bitset-bfs-experiment).
        //
        //    Day 1: ActiveTransistors bitset + maintenance hook in SetNodeState. No consumers yet —
        //           pure infrastructure plus a verify path. Goal: overhead < 2% on bench-hc.
        //    Day 2: PPU-region bit-parallel BFS expansion (chip-id aware dispatch — only PPU
        //           starting nodes route through bitset BFS; CPU/OTHER stay scalar).
        //    Day 3: Ligra-dense linear scan over PPU transistors when frontier > threshold;
        //           tzcnt-based bit→node-id extraction; scalar flag accumulation + tie-break.
        //
        //    See MD/note/bitset-bfs-experiment-plan.md for the design + pitfalls.
        //    Gemini r5 analysis: temp/gemini_bitset_bfs_response.md.

        // Master switch. Off by default — experiment branch only.
        public static bool EnableBitsetBfs = false;
        // Day 3: route PPU starting nodes through the Ligra-dense BFS path (requires --bitset-bfs + --chip-diag).
        public static bool EnableBitsetBfsDense = false;
        // Day 3 diagnostic counters
        public static long DenseWalkCount;
        public static long DenseWalkPasses;       // total iterations across all dense walks
        public static long DenseWalkScalarFallback;  // walks that escaped PPU and were redone scalar
        public static long DenseWalkBypassSmall;     // walks that didn't enter dense (PPU but starting BFS visited<=K)

        // Assertion path: after each ProcessQueue settle, walk every transistor and verify
        // ActiveTransistors[i] matches (NodeStates[transistors[i].Gate] != 0). Costs O(T)
        // per settle, so use only for correctness verification (--verify-active-trans).
        public static bool VerifyActiveTransistors = false;
        public static long ActiveTransistorsVerifyCount;
        public static long ActiveTransistorsVerifyMismatches;

        // ── unmanaged hot data ──

        // ActiveTransistors[tid bit] = 1 iff transistor tid's gate currently conducts
        // (NodeStates[transistors[tid].Gate] != 0). One bit per surviving transistor
        // (post-lowering, dense 0..TransistorCount-1).
        public static ulong* ActiveTransistors;
        public static int ActiveTransistorsUlongCount;

        // Per-transistor flat records (post-lowering, dense 0..TransistorCount-1).
        // Mirrors the managed Transistors[] list but in unmanaged contiguous memory
        // so Day 3's Ligra-dense scan can iterate without managed-array bounds checks.
        public static int* TransistorGateNode;
        public static int* TransistorC1Node;
        public static int* TransistorC2Node;

        // Per-node "transistor IDs I gate" — flat int array, LENGTH-PREFIXED sub-lists
        // (can't use 0-terminator like TlistGates does because tid=0 is a valid transistor!).
        // Format: NodeGateTidsList[start] = count, then count tids follow.
        // NodeGateTidsStart[nn] = 0 means "no gates"; else points at the count int.
        public static int* NodeGateTidsList;
        public static int NodeGateTidsListLength;
        public static int* NodeGateTidsStart;

        // ── Day 2: PPU-region subset infrastructure (for Day 3's Ligra-dense scan). ──
        //
        // PPU node "local index": dense renumbering of NodeChip==CHIP_PPU nodes into 0..PpuNodeCount-1.
        //   PpuLocalIdx[global_node_id] = -1 if non-PPU, else local 0-based index
        //   PpuNodeList[local_idx]      = global_node_id (inverse map)
        //
        // PPU transistor list: subset of tids where gate AND c1 AND c2 are all PPU (or supplies — VCC/GND
        //   are accepted as "non-divergent" endpoints; channels to supplies just contribute Pwr/Gnd to the
        //   group flags, they don't cross to a different chip). Excludes any transistor touching CPU/OTHER
        //   nodes so dense scan stays inside the PPU graph; cross-chip walks fall back to scalar BFS.
        public static int PpuNodeCount;
        public static int* PpuLocalIdx;      // size NodeCount; -1 if not PPU
        public static int* PpuNodeList;      // size PpuNodeCount; global node id by local idx

        public static int PpuTransistorCount;
        public static int* PpuTransistorIds;   // size PpuTransistorCount; global tids of "PPU-internal" transistors

        // ── Day 3 scratch: frontier/visited/next bitmaps in PPU-local indices (sized PpuNodeCount/64 ulongs). ──
        public static int PpuBitmapUlongCount;
        public static ulong* DenseFrontier;
        public static ulong* DenseVisited;
        public static ulong* DenseNext;

        /// <summary>Allocate + populate the bitset structures. Called from Reset() when
        /// EnableBitsetBfs. MUST run AFTER NodeStates is initialised (so the initial gate-on
        /// states get reflected into ActiveTransistors).</summary>
        public static void InitBitsetBfs()
        {
            int tCount = TransistorCount;
            if (tCount <= 0) throw new InvalidOperationException("InitBitsetBfs: TransistorCount must be > 0 (set in Reset before calling)");

            BuildNodeGateTidsList();

            ActiveTransistorsUlongCount = (tCount + 63) / 64;
            ActiveTransistors = AllocArray<ulong>(ActiveTransistorsUlongCount);
            TransistorGateNode = AllocArray<int>(tCount);
            TransistorC1Node = AllocArray<int>(tCount);
            TransistorC2Node = AllocArray<int>(tCount);

            for (int tid = 0; tid < tCount; tid++)
            {
                var t = Transistors[tid];
                TransistorGateNode[tid] = t.Gate;
                TransistorC1Node[tid] = t.C1;
                TransistorC2Node[tid] = t.C2;
                if (NodeStates[t.Gate] != 0)
                    ActiveTransistors[tid >> 6] |= 1ul << (tid & 63);
            }

            ActiveTransistorsVerifyCount = 0;
            ActiveTransistorsVerifyMismatches = 0;

            // sanity: initial bitset must match initial NodeStates exactly
            if (!CheckActiveTransistorsConsistency(out int initBad))
            {
                int gate = TransistorGateNode[initBad];
                bool bit = (ActiveTransistors[initBad >> 6] & (1ul << (initBad & 63))) != 0;
                bool on = NodeStates[gate] != 0;
                Console.Error.WriteLine($"[bitset-bfs] INIT mismatch @ tid={initBad} gate={gate} bit={bit} gateOn={on}");
            }

            // Day 2: build PPU subset infrastructure if chip classification is available.
            // (Day 2/3 needs NodeChip[]; nudge the user to enable chip-diag if missing.)
            if (NodeChip != null) BuildPpuSubset();
            else Console.WriteLine("# bitset-bfs: NodeChip[] not populated (pass --chip-diag); Day 2 PPU subset skipped");
        }

        // ── Day 2: identify PPU-internal nodes + transistors. Requires chip-diag to have run
        //    (i.e. NodeChip is populated). PPU "internal" transistor = all three endpoints
        //    (gate, c1, c2) are either PPU nodes or supplies (Npwr/Ngnd). ──
        private static void BuildPpuSubset()
        {
            int n = NodeCount;
            PpuLocalIdx = AllocArray<int>(n);
            for (int i = 0; i < n; i++) PpuLocalIdx[i] = -1;
            int local = 0;
            for (int nn = 0; nn < n; nn++)
                if (NodeChip[nn] == CHIP_PPU) PpuLocalIdx[nn] = local++;
            PpuNodeCount = local;
            PpuNodeList = AllocArray<int>(PpuNodeCount);
            for (int nn = 0; nn < n; nn++)
                if (PpuLocalIdx[nn] >= 0) PpuNodeList[PpuLocalIdx[nn]] = nn;

            // First pass: count PPU-internal transistors
            int tCount = TransistorCount;
            int ppuT = 0;
            for (int tid = 0; tid < tCount; tid++)
            {
                int g = TransistorGateNode[tid], c1 = TransistorC1Node[tid], c2 = TransistorC2Node[tid];
                if (IsPpuOrSupply(g) && IsPpuOrSupply(c1) && IsPpuOrSupply(c2)) ppuT++;
            }
            PpuTransistorCount = ppuT;
            PpuTransistorIds = AllocArray<int>(ppuT);
            int cursor = 0;
            for (int tid = 0; tid < tCount; tid++)
            {
                int g = TransistorGateNode[tid], c1 = TransistorC1Node[tid], c2 = TransistorC2Node[tid];
                if (IsPpuOrSupply(g) && IsPpuOrSupply(c1) && IsPpuOrSupply(c2)) PpuTransistorIds[cursor++] = tid;
            }

            Console.WriteLine($"# bitset-bfs Day 2: PPU subset — {PpuNodeCount:N0} nodes ({(double)PpuNodeCount * 100 / n:F1}% of all), {PpuTransistorCount:N0} transistors ({(double)PpuTransistorCount * 100 / tCount:F1}% of all)");

            // Day 3 scratch bitmaps (PPU-local indices)
            PpuBitmapUlongCount = (PpuNodeCount + 63) / 64;
            DenseFrontier = AllocArray<ulong>(PpuBitmapUlongCount);
            DenseVisited  = AllocArray<ulong>(PpuBitmapUlongCount);
            DenseNext     = AllocArray<ulong>(PpuBitmapUlongCount);
            DenseWalkCount = 0;
            DenseWalkPasses = 0;
            DenseWalkScalarFallback = 0;
            DenseWalkBypassSmall = 0;
        }

        private static bool IsPpuOrSupply(int nodeId)
        {
            if (nodeId == Npwr || nodeId == Ngnd) return true;
            return nodeId >= 0 && nodeId < NodeCount && NodeChip[nodeId] == CHIP_PPU;
        }

        // ── Day 3: Ligra-dense BFS over PPU subgraph ──
        //
        // Replaces ComputeNodeGroup / AddNodeToGroup for walks STARTING at a PPU node when
        // --bitset-bfs-dense is on. Inner BFS step iterates PpuTransistorCount transistors
        // linearly, propagating frontier bits via Active-transistor mask. Final extraction
        // is scalar (tzcnt loop) + scalar flag accumulation (Q2 of Gemini r5: don't
        // bit-parallel the flag/tie-break — too many corner cases).
        //
        // Cross-chip safety: if any visited node has a conducting non-PPU channel, we
        // detected an "escape" — discard the dense result and re-run scalar BFS. The
        // chip-diag stats show this fires for ~0.2% of all walks (the only walks that
        // genuinely cross chip boundaries).
        //
        // Touched-range tracking: avoids zeroing the full ~140-ulong bitmaps for every
        // walk (most walks visit <10 nodes, touching maybe 1-2 chunks). The min/max
        // ulong index touched in this walk's frontier/visited/next bitmaps is recorded
        // and used both for the iteration sweep and for the cleanup pass at walk end.

        // Per-walk scratch
        private static int _denseTouchedMin;
        private static int _denseTouchedMax;

        private static byte ComputeNodeGroupDense(int startNn)
        {
            // clear previous walk's dedup flags (matches the scalar ComputeNodeGroup contract)
            for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;
            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            _maxState = 0;
            _maxConnections = 0;

            int startLocal = PpuLocalIdx[startNn];
            if (startLocal < 0)
            {
                // Caller bug: should have routed to scalar path. Fall back gracefully.
                AddNodeToGroup(startNn);
                return GetNodeValue();
            }

            // mark start in frontier + visited
            int startChunk = startLocal >> 6;
            ulong startBit = 1ul << (startLocal & 63);
            DenseFrontier[startChunk] = startBit;
            DenseVisited[startChunk] = startBit;
            _denseTouchedMin = startChunk;
            _denseTouchedMax = startChunk;

            // BFS loop — Ligra-dense scan over all PPU transistors per pass
            while (true)
            {
                int passMin = _denseTouchedMin;
                int passMax = _denseTouchedMax;
                // clear next over current touched range
                for (int i = passMin; i <= passMax; i++) DenseNext[i] = 0;

                int pCount = PpuTransistorCount;
                int* tlist = PpuTransistorIds;
                int* c1ns = TransistorC1Node;
                int* c2ns = TransistorC2Node;
                int* localIdx = PpuLocalIdx;
                ulong* active = ActiveTransistors;
                ulong* front = DenseFrontier;
                ulong* next = DenseNext;

                for (int i = 0; i < pCount; i++)
                {
                    int tid = tlist[i];
                    if ((active[tid >> 6] & (1ul << (tid & 63))) == 0) continue;
                    int c1 = c1ns[tid];
                    int c2 = c2ns[tid];
                    // Skip supply-touched transistors in BFS — they don't propagate (supply isn't
                    // a frontier node; the flag attribution happens in the scalar extraction below).
                    if (c1 == Npwr || c1 == Ngnd || c2 == Npwr || c2 == Ngnd) continue;
                    int l1 = localIdx[c1];
                    int l2 = localIdx[c2];
                    if (l1 < 0 || l2 < 0) continue;   // shouldn't happen — both endpoints are PPU by construction
                    bool in1 = (front[l1 >> 6] & (1ul << (l1 & 63))) != 0;
                    bool in2 = (front[l2 >> 6] & (1ul << (l2 & 63))) != 0;
                    if (in1 == in2) continue;
                    if (in1)
                    {
                        int c2Chunk = l2 >> 6;
                        next[c2Chunk] |= 1ul << (l2 & 63);
                        if (c2Chunk < _denseTouchedMin) _denseTouchedMin = c2Chunk;
                        if (c2Chunk > _denseTouchedMax) _denseTouchedMax = c2Chunk;
                    }
                    else
                    {
                        int c1Chunk = l1 >> 6;
                        next[c1Chunk] |= 1ul << (l1 & 63);
                        if (c1Chunk < _denseTouchedMin) _denseTouchedMin = c1Chunk;
                        if (c1Chunk > _denseTouchedMax) _denseTouchedMax = c1Chunk;
                    }
                }

                // next &= ~visited; visited |= next; frontier = next; detect empty
                bool any = false;
                for (int i = _denseTouchedMin; i <= _denseTouchedMax; i++)
                {
                    ulong n = next[i] & ~DenseVisited[i];
                    DenseVisited[i] |= n;
                    DenseFrontier[i] = n;
                    if (n != 0) any = true;
                }
                DenseWalkPasses++;
                if (!any) break;
            }

            // ── extraction: walk DenseVisited bits, build _groupBuf, accumulate flags ──
            // also detect cross-chip escape (a conducting transistor leading out of PPU).
            bool crossChip = false;
            for (int u = _denseTouchedMin; u <= _denseTouchedMax; u++)
            {
                ulong bits = DenseVisited[u];
                while (bits != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    int localIdxBit = (u << 6) + bit;
                    if (localIdxBit >= PpuNodeCount) continue;   // padding bits in last chunk
                    int nn = PpuNodeList[localIdxBit];

                    _inGroup[nn] = 1;
                    _groupBuf[_groupCount++] = nn;
                    ref NodeInfo ns = ref NodeInfos[nn];
                    if (ns.Connections > _maxConnections) { _maxState = NodeStates[nn]; _maxConnections = ns.Connections; }
                    RecalcHash[nn] = 0;
                    _groupFlags |= ns.Flags;

                    if (ns.TlistC1gnd != 0)
                    {
                        int* pp = TransistorList + ns.TlistC1gnd;
                        while (*pp != 0) { int gate = *pp++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Gnd; break; } }
                    }
                    if (ns.TlistC1pwr != 0)
                    {
                        int* pp = TransistorList + ns.TlistC1pwr;
                        while (*pp != 0) { int gate = *pp++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Pwr; break; } }
                    }
                    // cross-chip escape check: any conducting transistor in TlistC1c2s that leads outside PPU?
                    if (!crossChip && ns.TlistC1c2s != 0)
                    {
                        int* pp = TransistorList + ns.TlistC1c2s;
                        while (*pp != 0)
                        {
                            int gate = *pp++;
                            int other = *pp++;
                            if (NodeStates[gate] != 0 && other != Npwr && other != Ngnd && PpuLocalIdx[other] < 0)
                            { crossChip = true; break; }
                        }
                    }
                }
            }

            // cleanup: clear DenseVisited touched range for the next walk
            for (int i = _denseTouchedMin; i <= _denseTouchedMax; i++)
            {
                DenseVisited[i] = 0;
                DenseFrontier[i] = 0;
                DenseNext[i] = 0;
            }

            if (crossChip)
            {
                // discard dense result + retry scalar
                for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;
                _groupCount = 0;
                _groupFlags = NodeFlags.None;
                _maxState = 0;
                _maxConnections = 0;
                DenseWalkScalarFallback++;
                AddNodeToGroup(startNn);
            }
            else
            {
                DenseWalkCount++;
            }

            return GetNodeValue();
        }

        // ── Build NodeGateTidsList: length-prefixed flat int array, with NodeGateTidsStart[nn]
        //    pointing to the count int (followed by `count` tids). Length-prefix is needed
        //    because tid=0 is a valid transistor — a 0-terminator would falsely abort the walk. ──
        private static void BuildNodeGateTidsList()
        {
            NodeGateTidsStart = AllocArray<int>(NodeCount);
            int total = 1;   // index 0 reserved as sentinel "no list"
            for (int nn = 0; nn < NodeCount; nn++)
            {
                Node? node = Nodes[nn];
                if (node == null || node.Gates.Count == 0) continue;
                total += node.Gates.Count + 1;   // count int + that many tids
            }
            NodeGateTidsList = AllocArray<int>(total);
            NodeGateTidsListLength = total;

            int cursor = 1;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                Node? node = Nodes[nn];
                if (node == null || node.Gates.Count == 0) { NodeGateTidsStart[nn] = 0; continue; }
                NodeGateTidsStart[nn] = cursor;
                NodeGateTidsList[cursor++] = node.Gates.Count;
                foreach (int tid in node.Gates) NodeGateTidsList[cursor++] = tid;
            }
        }

        // ── Maintenance hook from SetNodeState — called when NodeStates[nn] flips. ──
        //    Toggles every ActiveTransistors bit for transistors gated by nn. Cost is
        //    O(gate fanout of nn); typical fanout is small. Caller MUST be inside the
        //    SetNodeState-detected change branch (we don't recheck old==new here).
        public static void UpdateActiveTransistorsOnFlip(int nn, byte newState)
        {
            int start = NodeGateTidsStart[nn];
            if (start == 0) return;
            int* p = NodeGateTidsList + start;
            int n = *p++;
            if (newState != 0)
            {
                for (int i = 0; i < n; i++)
                {
                    int tid = *p++;
                    ActiveTransistors[tid >> 6] |= 1ul << (tid & 63);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    int tid = *p++;
                    ActiveTransistors[tid >> 6] &= ~(1ul << (tid & 63));
                }
            }
        }

        /// <summary>Assertion: ActiveTransistors[i] must match (NodeStates[gate] != 0) for every i.
        /// Linear O(TransistorCount) scan. Intended for --verify-active-trans only.
        /// Returns true if consistent; on mismatch, returns false and sets out param to first failing tid.</summary>
        public static bool CheckActiveTransistorsConsistency(out int firstMismatchTid)
        {
            firstMismatchTid = -1;
            int tCount = TransistorCount;
            for (int tid = 0; tid < tCount; tid++)
            {
                bool bitSet = (ActiveTransistors[tid >> 6] & (1ul << (tid & 63))) != 0;
                bool gateOn = NodeStates[TransistorGateNode[tid]] != 0;
                if (bitSet != gateOn) { firstMismatchTid = tid; return false; }
            }
            return true;
        }

        /// <summary>Verify hook — called from ProcessQueueInterp end when VerifyActiveTransistors is on.
        /// Counts mismatches across the run; logs the first one immediately.</summary>
        public static void VerifyActiveTransistorsNow()
        {
            ActiveTransistorsVerifyCount++;
            if (!CheckActiveTransistorsConsistency(out int badTid))
            {
                ActiveTransistorsVerifyMismatches++;
                if (ActiveTransistorsVerifyMismatches == 1)
                {
                    int gate = TransistorGateNode[badTid];
                    bool bit = (ActiveTransistors[badTid >> 6] & (1ul << (badTid & 63))) != 0;
                    bool on = NodeStates[gate] != 0;
                    string gateName = gate >= 0 && gate < NodeCount && Nodes[gate] != null ? Nodes[gate]!.Name ?? "?" : "?";
                    Console.Error.WriteLine($"[bitset-bfs] FIRST mismatch @ verify#{ActiveTransistorsVerifyCount} time={Time}");
                    Console.Error.WriteLine($"  tid={badTid} gate={gate} ({gateName}) bit={bit} gateOn={on}");
                    Console.Error.WriteLine($"  Nodes[{gate}].Gates.Count = {Nodes[gate]?.Gates.Count ?? -1}");
                    if (Nodes[gate] != null)
                    {
                        bool found = false;
                        foreach (int tid in Nodes[gate]!.Gates)
                            if (tid == badTid) { found = true; break; }
                        Console.Error.WriteLine($"  Nodes[{gate}].Gates contains tid={badTid}: {found}");
                    }
                    Console.Error.WriteLine($"  NodeGateTidsStart[{gate}] = {NodeGateTidsStart[gate]}");
                    if (NodeGateTidsStart[gate] != 0)
                    {
                        int* p = NodeGateTidsList + NodeGateTidsStart[gate];
                        int cnt = *p++;
                        bool foundList = false;
                        for (int i = 0; i < cnt; i++) if (*p++ == badTid) foundList = true;
                        Console.Error.WriteLine($"  NodeGateTidsList[{gate}] count={cnt} contains badTid: {foundList}");
                    }
                }
            }
        }

        /// <summary>Diagnostic helper — print bitset stats after a run.</summary>
        public static void PrintBitsetBfsStats()
        {
            if (!EnableBitsetBfs) return;
            // Count set bits across ActiveTransistors
            long activeCount = 0;
            for (int i = 0; i < ActiveTransistorsUlongCount; i++)
                activeCount += System.Numerics.BitOperations.PopCount(ActiveTransistors[i]);
            Console.WriteLine($"# bitset-bfs stats");
            Console.WriteLine($"#   transistors:         {TransistorCount}");
            Console.WriteLine($"#   ActiveT ulongs:      {ActiveTransistorsUlongCount} ({ActiveTransistorsUlongCount * 8} bytes)");
            Console.WriteLine($"#   currently conducting:{activeCount} ({(double)activeCount * 100 / TransistorCount:F1}%)");
            Console.WriteLine($"#   NodeGateTidsList:    {NodeGateTidsListLength} ints ({NodeGateTidsListLength * 4} bytes)");
            if (VerifyActiveTransistors)
            {
                Console.WriteLine($"#   verify calls:        {ActiveTransistorsVerifyCount}");
                Console.WriteLine($"#   verify mismatches:   {ActiveTransistorsVerifyMismatches}");
            }
        }
    }
}
