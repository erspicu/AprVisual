using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Escape-1, step 2: the EXTRACTOR. Turns the coverage probe's per-node observations
    //  into an actual logic model and answers the next de-risk question after coverage:
    //  of the clean nodes, how many have a COMPLETE truth table (all 2^K input combos seen)
    //  AND can be LEVELIZED (the combinational dependency DAG among them is acyclic — combinational
    //  loops = undetected state elements that need cutting). The levelizable+complete set is what
    //  an oblivious straight-line compiler can actually emit.
    //
    //  Runs AFTER a coverage run (reuses _covMap = per-node inputVector->value, _covInputs = the
    //  radius-1 input node ids, _covStateful/_covSeen/_covWide). Analysis only.
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        internal const int MaxDenseK = 16;     // 2^16 = 64K dense TT entries; clean nodes have small radius-1 K
        internal const int MaxSparseK = CovMaxInputs;   // 17..60 inputs: sparse map lookup (key packs into a ulong)
        internal const byte TtUnseen = 2;      // truth-table sentinel: this input combo not yet observed/learned

        public static void ExtractModel() => ExtractModel(true);

        public static void ExtractModel(bool buildModel)
        {
            int n = NodeCount;
            var extracted = new bool[n];          // clean + small-K => oblivious candidate (TT learned online, verified)
            int cleanSeen = 0, complete = 0, incompleteK = 0, tooWide = 0, extractedCount = 0;

            // Include EVERY clean (non-contradicting) node with a tractable input count. We no longer require a
            // COMPLETE 2^K truth table: the oblivious sim only needs values for combos that actually OCCUR, the
            // TT is learned online (sentinel-filled) during the miter, and verify-then-enable (self-stateful
            // refine) demotes any node that ever contradicts golden. This scales the set from complete-only
            // (~10%) toward the full clean set (~96%).
            for (int nn = 0; nn < n; nn++)
            {
                if (nn == Npwr || nn == Ngnd || Nodes[nn] == null) continue;
                int b = _covBase[nn];
                if (b == 0) continue;                              // no channels tracked
                if (_covSeen[nn] == 0 || _covStateful[nn] != 0) continue;   // not observed, or stateful/analog
                cleanSeen++;
                int kk = _covInputs[b];
                var map = _covMap![nn];
                if (map == null) continue;
                if (kk > MaxSparseK) { tooWide++; continue; }       // > sparse cap; leave at boundary (bus model later)
                if (kk <= MaxDenseK && map.Count == (1 << kk)) complete++; else incompleteK++;
                extracted[nn] = true; extractedCount++;            // clean + K<=MaxSparseK => oblivious candidate (dense or sparse)
            }

            // Dependency DAG among extracted nodes: edge (input -> node) when an input is itself extracted.
            var indeg = new int[n];
            var succ = new List<int>[n];
            int edges = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (!extracted[nn]) continue;
                ushort* p = _covInputs + _covBase[nn];
                int kk = *p++;
                for (int i = 0; i < kk; i++)
                {
                    int inp = p[i];
                    if ((uint)inp < (uint)n && extracted[inp])
                    {
                        (succ[inp] ??= new List<int>()).Add(nn);
                        indeg[nn]++; edges++;
                    }
                }
            }

            // Kahn topological sort -> levels. Un-processed extracted nodes are in combinational cycles.
            var q = new Queue<int>();
            var level = new int[n];
            var orderList = new List<int>();    // levelizable nodes, in topological (eval) order
            int maxLevel = 0, sorted = 0;
            for (int nn = 0; nn < n; nn++) if (extracted[nn] && indeg[nn] == 0) q.Enqueue(nn);
            while (q.Count > 0)
            {
                int u = q.Dequeue(); sorted++; orderList.Add(u);
                if (succ[u] != null)
                    foreach (int v in succ[u])
                    {
                        if (level[u] + 1 > level[v]) level[v] = level[u] + 1;
                        if (--indeg[v] == 0) q.Enqueue(v);
                    }
                if (level[u] > maxLevel) maxLevel = level[u];
            }
            int cyclic = extractedCount - sorted;   // extracted but stuck in a cycle (uncut state element)

            // For RELAXATION eval we evaluate ALL candidates, not just the acyclic ones: every pass transistor
            // makes a bidirectional 2-cycle (A is B's far-end and vice-versa), so the datapath is one big SCC and
            // strict levelization keeps almost nothing. Iterative relaxation converges these conditional cycles
            // (A=B only while the gate is on); genuine bistable latches don't converge -> caught as self-stateful.
            // Order: levelizable nodes first (topological, fast-converging feed-forward core), then the rest.
            var inOrder = new bool[n];
            foreach (int u in orderList) inOrder[u] = true;
            var allCandidates = new List<int>(orderList);
            for (int nn = 0; nn < n; nn++) if (extracted[nn] && !inOrder[nn]) allCandidates.Add(nn);

            double live = NonNullNodeCount;
            Console.WriteLine("# ============ EXTRACTOR / LEVELIZER ============");
            Console.WriteLine($"#  clean+observed nodes:        {cleanSeen:N0}");
            Console.WriteLine($"#  oblivious CANDIDATES (clean, K<={MaxDenseK}): {extractedCount:N0}  ({Pct(extractedCount, (long)live):F1}% of live)");
            Console.WriteLine($"#    of which COMPLETE 2^K truth table: {complete:N0}   (rest learned online: {incompleteK:N0})");
            Console.WriteLine($"#    clean but K>{MaxDenseK} (too wide for dense TT): {tooWide:N0}");
            Console.WriteLine($"#  dependency DAG (among candidates): {edges:N0} edges");
            Console.WriteLine($"#    LEVELIZABLE (acyclic, oblivious-emittable): {sorted:N0}  in {maxLevel + 1} levels");
            Console.WriteLine($"#    in combinational CYCLES (uncut state elements): {cyclic:N0}");
            Console.WriteLine("# ===============================================");

            Console.WriteLine($"#  RELAXATION candidate set (levelizable core + cyclic pass net): {allCandidates.Count:N0}  ({sorted:N0} feed-forward first)");
            if (buildModel) { _logicFeedForward = sorted; BuildLogicModel(allCandidates); }
        }

        // Persist the extracted model into the unmanaged arrays the oblivious eval (WireCore.Logic.cs) reads:
        //   _logicIsExtracted, _logicOrder (topological), _logicTTBase + _logicTT (dense 2^k truth tables).
        // Truth tables are sentinel-initialised (TtUnseen) and only the already-observed combos pre-filled;
        // the rest are LEARNED online during the miter's identify window. A combo that is later contradicted
        // (same inputs, different golden value) flags the node self-stateful -> demoted at refine.
        private static void BuildLogicModel(List<int> orderList)
        {
            _logicIsExtracted = AllocArray<byte>(NodeCount);
            _logicTTBase      = AllocArray<int>(NodeCount);
            _logicSparse      = AllocArray<byte>(NodeCount);

            long ttSize = 1;              // entry 0 reserved (TTBase 0 == "none")
            int dense = 0, sparse = 0;
            foreach (int nn in orderList)
            {
                _logicIsExtracted[nn] = 1;
                int kk = _covInputs[_covBase[nn]];
                if (kk <= MaxDenseK) { _logicTTBase[nn] = (int)ttSize; ttSize += (1 << kk); dense++; }
                else { _logicSparse[nn] = 1; sparse++; }            // K 17..60: sparse map lookup (_covMap), no dense table
            }
            _logicTT = AllocArray<byte>((int)ttSize);
            for (long i = 0; i < ttSize; i++) _logicTT[i] = TtUnseen;     // sentinel: unobserved
            int preFilled = 0;
            foreach (int nn in orderList)
            {
                if (_logicSparse[nn] != 0) continue;                 // sparse nodes keep using _covMap directly
                int baseIdx = _logicTTBase[nn];
                var map = _covMap![nn];
                if (map == null) continue;
                foreach (var kv in map) { _logicTT[baseIdx + (int)kv.Key] = kv.Value; preFilled++; }
            }
            _logicOrder = AllocArray<int>(orderList.Count);
            for (int i = 0; i < orderList.Count; i++) _logicOrder[i] = orderList[i];
            _logicOrderCount = orderList.Count;
            LogicModelBuilt = true;
            Console.WriteLine($"#  [logic model built] {_logicOrderCount:N0} oblivious nodes ({dense:N0} dense + {sparse:N0} sparse), TT bytes {ttSize:N0}, pre-filled {preFilled:N0}");
        }
    }
}
