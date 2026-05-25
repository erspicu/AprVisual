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
