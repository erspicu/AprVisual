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
        public static void ExtractModel()
        {
            int n = NodeCount;
            var extracted = new bool[n];          // clean + complete truth table => directly compilable
            var k = new int[n];                   // input count
            int cleanSeen = 0, complete = 0, incompleteK = 0, tooWide = 0;

            for (int nn = 0; nn < n; nn++)
            {
                if (nn == Npwr || nn == Ngnd || Nodes[nn] == null) continue;
                int b = _covBase[nn];
                if (b == 0) continue;                              // no channels tracked
                if (_covSeen[nn] == 0 || _covStateful[nn] != 0) continue;   // not observed, or stateful/analog
                cleanSeen++;
                int kk = _covInputs[b];
                k[nn] = kk;
                var map = _covMap![nn];
                if (map == null) continue;
                if (kk > 24) { tooWide++; continue; }             // 2^K too big to ever fully observe empirically
                if (map.Count == (1 << kk)) { extracted[nn] = true; complete++; }
                else incompleteK++;                                // clean but not all input combos seen yet
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
            int maxLevel = 0, sorted = 0;
            for (int nn = 0; nn < n; nn++) if (extracted[nn] && indeg[nn] == 0) q.Enqueue(nn);
            while (q.Count > 0)
            {
                int u = q.Dequeue(); sorted++;
                if (succ[u] != null)
                    foreach (int v in succ[u])
                    {
                        if (level[u] + 1 > level[v]) level[v] = level[u] + 1;
                        if (--indeg[v] == 0) q.Enqueue(v);
                    }
                if (level[u] > maxLevel) maxLevel = level[u];
            }
            int cyclic = complete - sorted;   // extracted but stuck in a cycle (uncut state element)

            double live = NonNullNodeCount;
            Console.WriteLine("# ============ EXTRACTOR / LEVELIZER ============");
            Console.WriteLine($"#  clean+observed nodes:        {cleanSeen:N0}");
            Console.WriteLine($"#    COMPLETE truth table:      {complete:N0}  ({Pct(complete, (long)live):F1}% of live)   <- directly compilable");
            Console.WriteLine($"#    clean but combos not all seen yet: {incompleteK:N0}   (longer/varied run, or structural extract)");
            Console.WriteLine($"#    clean but K>24 (too wide for empirical TT): {tooWide:N0}");
            Console.WriteLine($"#  dependency DAG (among extracted): {edges:N0} edges");
            Console.WriteLine($"#    LEVELIZABLE (acyclic, oblivious-emittable): {sorted:N0}  in {maxLevel + 1} levels");
            Console.WriteLine($"#    in combinational CYCLES (uncut state elements): {cyclic:N0}");
            Console.WriteLine("# ===============================================");
        }
    }
}
