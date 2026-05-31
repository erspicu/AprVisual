using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Per-ProcessQueue settle-iteration diagnostic.
        //    Counts how many wave passes each ProcessQueue invocation takes; bucketed into a
        //    log-ish histogram. Use to pick a safer cap than MaxSettlePasses = 1000 and to spot
        //    when an optimization or new module pushes settle into a long tail (= latch /
        //    register region not converging cleanly).
        //
        //    Enabled by --settle-stats. Off by default — adds a tiny counter increment per
        //    ProcessQueue call, gated so the hot path stays clean.

        public static bool EnableSettleStats = false;

        // Histogram buckets: 1, 2, 3, 4, 5-8, 9-16, 17-32, 33-64, 65-128, 129-256, 257-512, 513+
        public static readonly long[] SettleHistogram = new long[12];
        public static readonly string[] SettleBucketLabels =
            { "1", "2", "3", "4", "5-8", "9-16", "17-32", "33-64", "65-128", "129-256", "257-512", "513+" };

        public static long SettleCallCount;
        public static long SettleIterTotal;
        public static int SettleMaxIter;
        public static string LastSettleStats = "(settle-stats not run)";

        public static void ResetSettleStats()
        {
            Array.Clear(SettleHistogram);
            SettleCallCount = 0;
            SettleIterTotal = 0;
            SettleMaxIter = 0;
        }

        /// <summary>Record one ProcessQueue completion. Called from ProcessQueueInterp's loop tail.</summary>
        internal static void RecordSettle(int iter)
        {
            SettleCallCount++;
            SettleIterTotal += iter;
            if (iter > SettleMaxIter) SettleMaxIter = iter;
            int b;
            if      (iter <=   4) b = iter - 1;   // 1, 2, 3, 4
            else if (iter <=   8) b = 4;
            else if (iter <=  16) b = 5;
            else if (iter <=  32) b = 6;
            else if (iter <=  64) b = 7;
            else if (iter <= 128) b = 8;
            else if (iter <= 256) b = 9;
            else if (iter <= 512) b = 10;
            else                  b = 11;
            SettleHistogram[b]++;
        }

        public static void BuildSettleStatsString()
        {
            if (SettleCallCount == 0) { LastSettleStats = "(settle-stats: 0 calls)"; return; }
            double avg = (double)SettleIterTotal / SettleCallCount;
            // cumulative p99: find smallest bucket-upper such that cumulative count >= 0.99 × total
            long target = (long)Math.Ceiling(SettleCallCount * 0.99);
            long cum = 0;
            int p99Bucket = SettleHistogram.Length - 1;
            for (int i = 0; i < SettleHistogram.Length; i++)
            {
                cum += SettleHistogram[i];
                if (cum >= target) { p99Bucket = i; break; }
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"settle-stats: {SettleCallCount:N0} ProcessQueue calls, avg {avg:F2} iter/call, max {SettleMaxIter} iter, p99 in [{SettleBucketLabels[p99Bucket]}]");
            sb.Append("# settle-stats histogram:");
            for (int i = 0; i < SettleHistogram.Length; i++)
            {
                if (SettleHistogram[i] == 0) continue;
                double pct = 100.0 * SettleHistogram[i] / SettleCallCount;
                sb.Append($"\n#   {SettleBucketLabels[i],8}: {SettleHistogram[i],9:N0} ({pct,5:F1}%)");
            }
            LastSettleStats = sb.ToString();
        }
    }
}
