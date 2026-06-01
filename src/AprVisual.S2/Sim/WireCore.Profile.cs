using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Whole-NES (CPU 2A03 + PPU 2C02) work profiler — analysis only, does NOT change
    //  engine behaviour (counters are passive; checksum stays bit-exact). Behind the
    //  `Profiling` flag so the default engine is untouched when off.
    //
    //  Answers, for a real run: (1) the runtime fast-path vs group-BFS split; (2) WHERE the
    //  BFS work concentrates (which nodes / which subsystem cpu.* vs ppu.* / which bus); and
    //  (3) the CONCENTRATION (top-K coverage) — the data that decides whether macro-block
    //  codegen can beat S1 without dying to i-cache (diffuse dirty set = i-cache risk).
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        public static bool Profiling = false;

        public static long ProfTotalRecalc;     // RecalcNode calls (non-supply)
        public static long ProfBfsRecalc;       // recalcs that fell to ComputeNodeGroup (multi-node group walk)
        public static long ProfTotalVisits;     // AddNodeOrApplyDriver calls (group-member visits)
        internal static long* ProfVisit;        // per-node BFS-visit count (alloc'd by AllocProfile)

        public static void AllocProfile()
        {
            ProfVisit = AllocArray<long>(NodeCount);
            ProfTotalRecalc = ProfBfsRecalc = ProfTotalVisits = 0;
            Profiling = true;
        }

        /// <summary>Print the whole-NES work profile. Uses the node-name map (kept after load) for
        /// per-node + per-subsystem attribution.</summary>
        public static void ReportProfile(int topN = 30)
        {
            Profiling = false;
            long total = ProfTotalRecalc, bfs = ProfBfsRecalc, singles = total - bfs;
            long visits = ProfTotalVisits;
            Console.WriteLine("# ============ WHOLE-NES PROFILE (CPU 2A03 + PPU 2C02) ============");
            Console.WriteLine($"#  recalcs: total {total:N0}");
            Console.WriteLine($"#    fast-path (singleton, O(1)):  {singles:N0}  ({Pct(singles, total):F1}%)   <- S1 already optimal here");
            Console.WriteLine($"#    group-BFS (multi-node):       {bfs:N0}  ({Pct(bfs, total):F1}%)   <- the codegen target");
            Console.WriteLine($"#  BFS group visits: {visits:N0}  (avg group size {(bfs > 0 ? (double)visits / bfs : 0):F2} nodes)");

            // rank nodes by BFS-visit count
            var ranked = new List<(int nn, long v)>();
            for (int nn = 0; nn < NodeCount; nn++) if (ProfVisit[nn] > 0) ranked.Add((nn, ProfVisit[nn]));
            ranked.Sort((a, b) => b.v.CompareTo(a.v));

            // concentration
            long cum = 0; int n10 = 0, n50 = 0, n200 = 0; long c10 = 0, c50 = 0, c200 = 0;
            for (int i = 0; i < ranked.Count; i++)
            {
                cum += ranked[i].v;
                if (i < 10) c10 = cum; if (i < 50) c50 = cum; if (i < 200) c200 = cum;
            }
            Console.WriteLine($"#  CONCENTRATION (of {ranked.Count:N0} BFS-touched nodes):");
            Console.WriteLine($"#    top 10  = {Pct(c10, visits):F1}% of BFS work");
            Console.WriteLine($"#    top 50  = {Pct(c50, visits):F1}%");
            Console.WriteLine($"#    top 200 = {Pct(c200, visits):F1}%");

            // subsystem breakdown by name prefix (cpu / ppu / other)
            var sub = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (nn, v) in ranked)
            {
                string name = GetNodeName(nn);
                int dot = name.IndexOf('.');
                string key = dot > 0 ? name.Substring(0, dot) : (name == "vcc" || name == "vss" ? "supply" : "(top)");
                sub[key] = sub.TryGetValue(key, out long cur) ? cur + v : v;
            }
            Console.WriteLine("#  SUBSYSTEM (by name prefix, share of BFS visits):");
            foreach (var kv in sub.OrderByDescending(k => k.Value).Take(12))
                Console.WriteLine($"#    {kv.Key,-16} {Pct(kv.Value, visits),5:F1}%  ({kv.Value:N0})");

            Console.WriteLine($"#  TOP {topN} HOTTEST BFS NODES (where the group-resolution time goes):");
            cum = 0;
            for (int i = 0; i < Math.Min(topN, ranked.Count); i++)
            {
                cum += ranked[i].v;
                Console.WriteLine($"#    {i + 1,3}. {GetNodeName(ranked[i].nn),-22} {ranked[i].v,12:N0}  {Pct(ranked[i].v, visits),5:F2}%  cum {Pct(cum, visits),5:F1}%");
            }
            Console.WriteLine("# =================================================================");
        }

        private static double Pct(long a, long b) => b > 0 ? 100.0 * a / b : 0;
    }
}
