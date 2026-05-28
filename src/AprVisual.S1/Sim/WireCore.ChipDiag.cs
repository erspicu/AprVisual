using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Per-chip BFS walk diagnostic — quantify how many ComputeNodeGroup walks stay within
        //    a single chip (CPU / PPU / other) vs cross a chip boundary. Tells us the upper bound
        //    on within-sim parallelism: walks that stay in one chip can be dispatched to
        //    chip-specific worker threads; cross-chip walks must serialize.
        //
        //    Counters reset at each Reset(); reported via LastChipDiagStats after bench-hc.
        //    Enabled by --chip-diag (sets EnableChipDiag = true) — gated so the hot path is clean.

        public const byte CHIP_CPU = 0;
        public const byte CHIP_PPU = 1;
        public const byte CHIP_OTHER = 2;

        internal static byte* NodeChip;
        public static bool EnableChipDiag = false;
        public static long ChipDiagTotalWalks;
        public static long[] ChipDiagWalksByChip = new long[3];     // walks that stayed in [chip]
        public static long ChipDiagCrossChipWalks;
        public static long[] ChipDiagNodesByChip = new long[3];     // total nodes added to groups, by chip
        public static string LastChipDiagStats = "(chip-diag not run)";

        // Per-walk scratch — set in ComputeNodeGroup, updated in AddNodeToGroup.
        internal static byte _chipDiagCurrent;     // chip of the walk's *first* node
        internal static bool _chipDiagMultiSeen;   // walk has touched >1 distinct chip

        // Breakdown of "other" by sub-prefix (TTL chips / cart / controllers / globals)
        public static System.Collections.Generic.Dictionary<string, int> OtherPrefixNodeCount = new();

        /// <summary>Classify every node by its OWNING MODULE INSTANCE (recorded in InstanceRanges
        /// during parse-time AddInstance). Unnamed internal transistor nodes get correctly attributed
        /// to their cpu/ppu/u1/cart.edge/etc. instance — name-prefix matching missed these because
        /// only "important" nodes have entries in each module's nodenames.txt.</summary>
        internal static void ClassifyChips()
        {
            NodeChip = AllocArray<byte>(NodeCount);
            for (int i = 0; i < NodeCount; i++) NodeChip[i] = CHIP_OTHER;
            OtherPrefixNodeCount.Clear();

            // InstanceRanges holds PRE-LOWERING id ranges. After lowering, ids are renumbered
            // densely; we use LastLowerRemap (pre → post) to translate. For each pre-id we know
            // its owner instance prefix; we then tag the corresponding post-id.
            var ranges = new System.Collections.Generic.List<(int Start, int End, string Prefix)>(InstanceRanges);
            ranges.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));

            int[]? remap = LastLowerRemap;
            int preMax = 0;
            foreach (var r in ranges) if (r.End > preMax) preMax = r.End;
            if (remap == null) { Console.WriteLine("# chip-diag: WARNING — LastLowerRemap is null; lowering may have been skipped"); }
            else if (remap.Length < preMax) preMax = remap.Length;

            int cpuCount = 0, ppuCount = 0;
            // First, classify based on PRE-lowering ids translated via remap.
            for (int oldId = 0; oldId < preMax; oldId++)
            {
                int newId = remap != null ? remap[oldId] : oldId;
                if (newId == EmptyNode || newId < 0 || newId >= NodeCount) continue;

                string owner = "";
                foreach (var r in ranges)
                {
                    if (oldId >= r.Start && oldId < r.End) { owner = r.Prefix; break; }
                }

                byte chip = CHIP_OTHER;
                if (owner == "cpu" || owner.StartsWith("cpu.", StringComparison.Ordinal)) chip = CHIP_CPU;
                else if (owner == "ppu" || owner.StartsWith("ppu.", StringComparison.Ordinal)) chip = CHIP_PPU;

                // Multi-old-to-one-new merging: keep the FIRST tag that's non-default; the merged
                // set was unioned via always-on connection so its members logically share owner.
                // Use OTHER as a "not yet decided" marker (default value from AllocArray).
                if (NodeChip[newId] == CHIP_OTHER && chip != CHIP_OTHER) NodeChip[newId] = chip;

                // For "other" tag (TTL / cart / port / globals), keep owner string for breakdown.
                if (chip == CHIP_OTHER && !string.IsNullOrEmpty(owner))
                {
                    OtherPrefixNodeCount.TryGetValue(owner, out int c);
                    OtherPrefixNodeCount[owner] = c + 1;
                }
            }
            for (int i = 0; i < NodeCount; i++)
            {
                if (NodeChip[i] == CHIP_CPU) cpuCount++;
                else if (NodeChip[i] == CHIP_PPU) ppuCount++;
            }
            ChipDiagTotalWalks = 0;
            ChipDiagCrossChipWalks = 0;
            Array.Clear(ChipDiagWalksByChip);
            Array.Clear(ChipDiagNodesByChip);
            int otherCount = NodeCount - cpuCount - ppuCount;
            Console.WriteLine($"# chip-diag: classified {cpuCount:N0} CPU + {ppuCount:N0} PPU + {otherCount:N0} other nodes (via instance-range table)");

            var sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(OtherPrefixNodeCount);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
            Console.Write("# chip-diag: 'other' breakdown — ");
            int shown = 0;
            foreach (var kv in sorted)
            {
                if (kv.Value < 20) break;
                Console.Write($"{kv.Key}={kv.Value:N0}  ");
                if (++shown >= 16) break;
            }
            Console.WriteLine();
        }

        internal static void ChipDiagAfterReport()
        {
            long t = ChipDiagTotalWalks;
            if (t == 0) { LastChipDiagStats = "(chip-diag enabled but 0 walks)"; return; }
            double crossPct = 100.0 * ChipDiagCrossChipWalks / t;
            long pureCpu = ChipDiagWalksByChip[CHIP_CPU];
            long purePpu = ChipDiagWalksByChip[CHIP_PPU];
            long pureOther = ChipDiagWalksByChip[CHIP_OTHER];
            LastChipDiagStats = $"chip-diag: {t:N0} walks total | pure-CPU {pureCpu:N0} ({100.0 * pureCpu / t:F1}%) | pure-PPU {purePpu:N0} ({100.0 * purePpu / t:F1}%) | pure-other {pureOther:N0} ({100.0 * pureOther / t:F1}%) | cross-chip {ChipDiagCrossChipWalks:N0} ({crossPct:F1}%)";

            long nT = ChipDiagNodesByChip[0] + ChipDiagNodesByChip[1] + ChipDiagNodesByChip[2];
            if (nT > 0)
            {
                LastChipDiagStats += $"\n# chip-diag: group-member counts — CPU {ChipDiagNodesByChip[CHIP_CPU]:N0} ({100.0 * ChipDiagNodesByChip[CHIP_CPU] / nT:F1}%) | PPU {ChipDiagNodesByChip[CHIP_PPU]:N0} ({100.0 * ChipDiagNodesByChip[CHIP_PPU] / nT:F1}%) | other {ChipDiagNodesByChip[CHIP_OTHER]:N0} ({100.0 * ChipDiagNodesByChip[CHIP_OTHER] / nT:F1}%)";
            }
        }
    }
}
