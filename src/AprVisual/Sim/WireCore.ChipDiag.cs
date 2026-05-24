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

        /// <summary>Classify every node by chip prefix (cpu., ppu., else "other"). Called from Reset
        /// when EnableChipDiag. Stays null otherwise.</summary>
        internal static void ClassifyChips()
        {
            NodeChip = AllocArray<byte>(NodeCount);
            for (int i = 0; i < NodeCount; i++) NodeChip[i] = CHIP_OTHER;
            int cpuCount = 0, ppuCount = 0;
            for (int i = 0; i < NodeCount; i++)
            {
                string nm = GetNodeName(i);
                if (nm.StartsWith("cpu.", StringComparison.Ordinal)) { NodeChip[i] = CHIP_CPU; cpuCount++; }
                else if (nm.StartsWith("ppu.", StringComparison.Ordinal)) { NodeChip[i] = CHIP_PPU; ppuCount++; }
                else NodeChip[i] = CHIP_OTHER;
            }
            ChipDiagTotalWalks = 0;
            ChipDiagCrossChipWalks = 0;
            Array.Clear(ChipDiagWalksByChip);
            Array.Clear(ChipDiagNodesByChip);
            int otherCount = NodeCount - cpuCount - ppuCount;
            Console.WriteLine($"# chip-diag: classified {cpuCount:N0} CPU + {ppuCount:N0} PPU + {otherCount:N0} other nodes");
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
