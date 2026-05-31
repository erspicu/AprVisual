using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Dead-end node diagnostic — find nodes that:
        //      1. Get added to BFS groups (S1 spends work computing their resolved state), AND
        //      2. Have NO downstream consumer (Nodes[nn].Gates.Count == 0 — no transistor uses
        //         their state as a gate), AND
        //      3. Are NOT callback watchpoints (Nodes[nn].Callback == null — not read by our
        //         behavioral handlers via the AddCallback fake-transistor watch mechanism).
        //
        //    These are nodes whose BFS work is potentially WASTED — their resolved state
        //    propagates nowhere in the silicon model AND we never read it in software. Candidates
        //    for a BFS-block optimization (per MD/note/bitset-bfs-experiment-results.md the
        //    architectural challenge is real, but identifying the upper-bound work savings is
        //    the prerequisite step).
        //
        //    Caveats:
        //      - This is an UPPER BOUND. Some nodes may be read by direct NodeStates[i] in
        //        non-callback paths (Trace, blargg signature poll, video handler hpos/vpos read).
        //        Those false positives need manual filtering by name (e.g. "ppu.hpos*" excluded).
        //      - We can't easily distinguish "node is in a closed silicon subnet that drives
        //        a chip pin" (real silicon output) from "node is genuinely useless" — both look
        //        identical at our switch level. The article's vid_* analysis is one example;
        //        we treat them all as "dead-end candidates" and let the user decide per case.

        public static bool EnableDeadEndDiag = false;
        public static long* NodeVisitCount;   // per-node count of AddNodeToGroup hits

        // ── --dead-end-skip implementation: mark leaves we can safely skip in BFS.
        //    Filter (must satisfy ALL):
        //      Gates.Count == 0           (no transistor uses this node as a gate)
        //      C1c2s.Count == 1           (single channel — leaf, not a bus junction)
        //      Pullups == 0               (no pull-up — Flags has no PullUp contribution)
        //      Callback == null           (not a watchpoint target)
        //      name not in handler-read whitelist
        //    For such nodes, AddNodeToGroup early-returns: no _groupBuf write, no flag OR,
        //    no channel walk. The "lost" channel walk goes to at most 1 neighbor; that
        //    neighbor was already reachable from the source side of this leaf, so the BFS
        //    topology is preserved (in theory — bit-identical verify is mandatory).
        public static bool EnableDeadEndSkip = false;
        public static byte* DeadEndSkippable;     // 1 = skip, 0 = walk normally
        public static int DeadEndSkippableCount;  // diagnostic: how many nodes are marked

        internal static void BuildDeadEndSkipMap()
        {
            DeadEndSkippable = AllocArray<byte>(NodeCount);
            var whitelist = BuildHandlerReadWhitelist();
            int marked = 0;
            for (int nn = 3; nn < NodeCount; nn++)
            {
                Node? node = Nodes[nn];
                if (node == null) continue;
                if (node.Gates.Count != 0) continue;
                if (node.C1c2s.Count != 1) continue;
                if (node.Pullups != 0) continue;
                if (node.Callback != null) continue;
                string name = node.Name ?? GetNodeName(nn);
                if (whitelist.Contains(name)) continue;
                DeadEndSkippable[nn] = 1;
                marked++;
            }
            DeadEndSkippableCount = marked;
            Console.WriteLine($"# --dead-end-skip: marked {marked:N0} leaves as skippable ({(double)marked * 100 / NodeCount:F1}% of nodes)");
            // dump first 30 names for inspection
            Console.WriteLine($"# --dead-end-skip first 30 marked nodes:");
            int shown = 0;
            for (int nn = 3; nn < NodeCount && shown < 30; nn++)
            {
                if (DeadEndSkippable[nn] == 0) continue;
                Node? n = Nodes[nn];
                string nm = string.IsNullOrEmpty(n?.Name) ? "(unnamed)" : n!.Name;
                Console.WriteLine($"#   nn={nn,5} name='{nm}'  C1c2s={n?.C1c2s.Count}");
                shown++;
            }
        }

        // Centralised whitelist used by both --dead-end-diag and --dead-end-skip.
        private static HashSet<string> BuildHandlerReadWhitelist()
        {
            var w = new HashSet<string> { "ppu.pclk1" };
            for (int i = 0; i <= 8; i++) { w.Add($"ppu.hpos{i}"); w.Add($"ppu.vpos{i}"); }
            for (int i = 0; i <= 4; i++) w.Add($"ppu.pal_ptr{i}");
            for (int slot = 0; slot < 32; slot++)
                for (int b = 0; b < 6; b++)
                    w.Add($"ppu.pal_ram_{slot:X2}_b{b}");
            foreach (string reg in new[] { "cpu.a", "cpu.x", "cpu.y", "cpu.s", "cpu.p" })
                for (int b = 0; b < 8; b++) w.Add($"{reg}{b}");
            for (int b = 0; b < 8; b++) { w.Add($"cpu.pcl{b}"); w.Add($"cpu.pch{b}"); }
            return w;
        }

        internal static void InitDeadEndDiag()
        {
            NodeVisitCount = AllocArray<long>(NodeCount);
        }

        public static void ReportDeadEndDiag()
        {
            if (!EnableDeadEndDiag || NodeVisitCount == null) return;

            var readByHandler = BuildHandlerReadWhitelist();

            // Collect candidates
            var rows = new List<(int nn, long visits, string name, int channels)>();
            long totalVisits = 0;
            long deadVisits = 0;
            long leafVisits = 0;   // dead-end with channels <= 2 (true leaf — skip-safe)
            long hubVisits = 0;    // dead-end with channels >= 3 (junction — risky to skip)
            for (int nn = 3; nn < NodeCount; nn++)
            {
                long v = NodeVisitCount[nn];
                totalVisits += v;
                if (v == 0) continue;
                Node? node = Nodes[nn];
                if (node == null) continue;
                if (node.Gates.Count > 0) continue;            // condition (1): no downstream as gate
                if (node.Callback != null) continue;            // condition (2): not a watchpoint
                string name = node.Name ?? GetNodeName(nn);
                if (readByHandler.Contains(name)) continue;    // condition (3): not in manual whitelist
                int ch = node.C1c2s.Count;
                rows.Add((nn, v, name, ch));
                deadVisits += v;
                if (ch <= 2) leafVisits += v; else hubVisits += v;
            }

            rows.Sort((a, b) => b.visits.CompareTo(a.visits));

            Console.WriteLine();
            Console.WriteLine("# === dead-end-diag report ===");
            Console.WriteLine($"# total BFS node-visits:           {totalVisits:N0}");
            Console.WriteLine($"# dead-end candidate visits:       {deadVisits:N0}  ({(double)deadVisits * 100 / totalVisits:F2}% of all)");
            Console.WriteLine($"#   ├ leaf (≤2 channels, skip-safe):  {leafVisits:N0}  ({(double)leafVisits * 100 / totalVisits:F2}%)");
            Console.WriteLine($"#   └ hub  (≥3 channels, risky):       {hubVisits:N0}  ({(double)hubVisits * 100 / totalVisits:F2}%)");
            Console.WriteLine($"# distinct dead-end nodes:         {rows.Count:N0}");
            Console.WriteLine($"# (criterion: Gates.Count==0, no Callback, name not in handler-read whitelist)");
            Console.WriteLine();
            Console.WriteLine("# rank | visits      | %      | ch  | node-id | name");
            Console.WriteLine("# -----|-------------|--------|-----|---------|----------------------");
            int shown = 0;
            foreach (var r in rows)
            {
                if (shown >= 50) break;
                double pct = (double)r.visits * 100 / totalVisits;
                string kind = r.channels <= 2 ? "leaf" : "HUB ";
                Console.WriteLine($"# {shown + 1,4} | {r.visits,11:N0} | {pct,6:F3} | {kind} {r.channels,2} | {r.nn,7} | {r.name}");
                shown++;
            }

            // Bucket by name prefix (PPU / CPU / OTHER instances) to highlight hot subsystems
            var byPrefix = new Dictionary<string, (int count, long visits)>();
            foreach (var r in rows)
            {
                string pfx = PrefixBucket(r.name);
                if (!byPrefix.TryGetValue(pfx, out var v)) v = (0, 0);
                byPrefix[pfx] = (v.count + 1, v.visits + r.visits);
            }
            var sortedPfx = new List<KeyValuePair<string, (int count, long visits)>>(byPrefix);
            sortedPfx.Sort((a, b) => b.Value.visits.CompareTo(a.Value.visits));
            Console.WriteLine();
            Console.WriteLine("# dead-end visits bucketed by prefix:");
            foreach (var kv in sortedPfx)
            {
                if (kv.Value.visits < totalVisits / 1000) break;   // skip <0.1%
                Console.WriteLine($"#   {kv.Key,-30}  {kv.Value.count,4} nodes, {kv.Value.visits,12:N0} visits  ({(double)kv.Value.visits * 100 / totalVisits:F2}%)");
            }
        }

        // Bucket a node name by its instance prefix for the by-prefix summary.
        // "ppu.scan.foo" -> "ppu.scan", "cpu.a0" -> "cpu", "u3.1/Y0" -> "u3", "vss" -> "(global)"
        private static string PrefixBucket(string name)
        {
            int dot = name.IndexOf('.');
            if (dot < 0) return name.StartsWith("u", StringComparison.Ordinal) ? "(ttl-or-global)" : "(global)";
            int secondDot = name.IndexOf('.', dot + 1);
            if (secondDot < 0) return name.Substring(0, dot);
            return name.Substring(0, secondDot);
        }
    }
}
