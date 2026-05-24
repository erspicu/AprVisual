using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── math-algos #1 (--prune-merge) topological-group-ID fix (2026-05-24, per Gemini consultation).
        //
        // The original --prune-merge optimization skipped EnqueueNode(c1) when a transistor's gate went
        // HIGH and its endpoints already held the same DIGITAL value: "an equal-value merge can't change
        // either node's resolved value". This is true for pure combinational static logic, but **WRONG**
        // for cross-coupled feedback (D-FF / latches / 6T SRAM): a "1" driven by a pull-up and a "1"
        // held by gate capacitance are digitally equal but topologically distinct. Skipping the merge
        // re-resolution leaves the solver with stale topological boundaries; subsequent walks may
        // capacitance-tie-break to an INVERTED stable state. Symptom: full_palette.nes @ frame 48
        // renders all-black under --prune-merge (PPU vpos/hpos counters lock wrong → vblank state
        // machine desyncs forever, CPU can't write internal counters).
        //
        // Initial fix attempt — "direct 2-cycle" detection (X gates trans with Y as endpoint AND
        // Y gates trans with X as endpoint) — marked the 6T SRAM cells (pal_ram, oam_ram) correctly
        // but missed the D-FF outputs (vpos2, hpos2 — Q is driven by master-slave internal latches,
        // not in the direct 2-cycle itself). State still diverged.
        //
        // The correct fix (Gemini r3): replace the digital-equality skip condition with a
        // TOPOLOGICAL EQUIVALENCE check. During ComputeNodeGroup's BFS, every node walked into the
        // current group receives the same fresh GroupID. In the prune-merge skip check, compare
        // GroupIDs instead of NodeStates: if c1 and c2 are already in the same group (same GroupID),
        // they are PHYSICALLY tied together — adding a parallel transistor between them genuinely
        // cannot change any resolved value. If they have different GroupIDs (or one hasn't been
        // walked yet), they are in distinct groups and the merge MUST be re-resolved.
        //
        // Bookkeeping:
        //   - NodeGroupIDs: per-node long, persistent across the run; initial value = nn (each
        //     node is its own singleton group until first walked, so two distinct unwalked nodes
        //     compare unequal as expected).
        //   - _nextGroupID: monotonic counter (starts at NodeCount so it never collides with the
        //     initial per-node values). Incremented once per ComputeNodeGroup call.
        //
        // Cost: one extra _groupCount-sized loop in RecalcNode (negligible), and the skip check now
        // does 2 long reads instead of 2 byte reads. The skip frequency drops vs the old version
        // (because some "same digital value, different group" pairs now correctly enqueue), but on
        // observably-benign chip regions (bus merging, combinational fanout) the skip still fires.

        internal static long* NodeGroupIDs;
        internal static long _nextGroupID;

        /// <summary>Allocate + initialize NodeGroupIDs (each node = its own singleton group).</summary>
        internal static void InitGroupIDs()
        {
            NodeGroupIDs = AllocArray<long>(NodeCount);   // tracked + zeroed; freed in FreeUnmanagedMemory
            for (int i = 0; i < NodeCount; i++) NodeGroupIDs[i] = i;
            _nextGroupID = NodeCount;
        }
    }
}
