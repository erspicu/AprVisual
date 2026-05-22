using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── math-algos 策略二 (Pure-logic Fast-Path): an O(1) RecalcNode for "pure logic" nodes.
        //
        // A large fraction of an NMOS chip's nodes are depletion-load gate outputs: they carry a
        // pull-up and are pulled down only through transistors straight to GND — they are NEVER an
        // endpoint of a pass-transistor bus. For such a node nn the conducting group is *provably*
        // always exactly {nn} (because nn has no channel to any normal node: TlistC1c2s is empty,
        // so AddNodeToGroup can neither recurse out of nn nor pull nn into anyone else's group), so
        // the whole DFS machinery (clear _inGroup, push _groupBuf, recurse, track _maxConnections)
        // is wasted. Its value is just GetNodeValue() of a one-node group:
        //
        //     resolved = FlagsToState[ ns.Flags | (Gnd if any GND-channel conducts)
        //                                        | (Pwr if any VCC-channel conducts) ]
        //
        // We read ns.Flags *fresh* each call (not a precomputed "default 1"), so this stays
        // byte-for-byte identical to ComputeNodeGroup({nn}) + GetNodeValue() even when the node is
        // externally driven at runtime (SetHigh/SetLow flags) — those just ride the LUT priority
        // (Gnd>Pwr>SetHigh>SetLow>PullUp). Requiring PullUp guarantees the OR-ed flags are never
        // empty, so GetNodeValue's "purely-floating → largest-cap node holds" branch is never the
        // answer (which the LUT, returning 0 for empty flags, would NOT reproduce) — i.e. PullUp is
        // exactly the condition that makes the LUT path equivalent.
        //
        // This does NOT shrink D (the dirty-set size); it shrinks the *constant* per dirty node.
        // Gated behind --fast-path / EnableFastPath; default off (A/B). Verified bit-identical
        // (NodeStatesChecksum), not merely blargg-PASS. Combinable with --prune-merge (#1).

        public static bool EnableFastPath = false;

        // Per-node classification, 1 = take RecalcNodeFast. Unmanaged, sized NodeCount, (re)built by
        // ClassifyPureLogicNodes() at the end of Reset() when EnableFastPath; freed/null'd with the
        // rest in FreeUnmanagedMemory(). null when fast-path is off — RecalcNode null-checks it.
        internal static byte* IsPureLogic;

        public static int PureLogicNodeCount;
        public static string LastFastPathStats = "(fast-path disabled — default; --fast-path to enable)";

        /// <summary>
        /// Flag the nodes eligible for the O(1) RecalcNodeFast path. Must run after Reset() has built
        /// the per-node Tlist* sub-lists and set the static flags (pull-up / forceCompute / supply).
        /// Eligible ⇔ has PullUp, has NO channel to a normal node (TlistC1c2s empty ⇒ group is {nn}),
        /// and carries none of HasCallback / ForceCompute / Pwr / Gnd (callbacks must fire through the
        /// normal path; forceCompute/supply have special resolution). SetHigh/SetLow are runtime-only
        /// and handled by the LUT, so they are NOT excluded here.
        /// </summary>
        internal static void ClassifyPureLogicNodes()
        {
            IsPureLogic = AllocArray<byte>(NodeCount);   // tracked + zeroed; freed in FreeUnmanagedMemory
            const NodeFlags exclude = NodeFlags.HasCallback | NodeFlags.ForceCompute | NodeFlags.Pwr | NodeFlags.Gnd;
            int count = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (nn == Npwr || nn == Ngnd || Nodes[nn] == null) continue;
                ref NodeInfo ns = ref NodeInfos[nn];
                if ((ns.Flags & NodeFlags.PullUp) == 0) continue;   // PullUp ⇒ LUT path is the equivalent one
                if ((ns.Flags & exclude) != 0) continue;
                if (ns.TlistC1c2s != 0) continue;                   // any normal-node channel ⇒ group can grow ⇒ not {nn}
                IsPureLogic[nn] = 1;
                count++;
            }
            PureLogicNodeCount = count;
            double pct = NonNullNodeCount > 0 ? 100.0 * count / NonNullNodeCount : 0;
            LastFastPathStats = $"fast-path: {count:N0} pure-logic-gnd nodes classified ({pct:F1}% of {NonNullNodeCount:N0} live nodes) take the O(1) RecalcNode";
        }

        /// <summary>
        /// O(1)-resolution RecalcNode for a classified pure-logic node (group is provably {nn}).
        /// Behaviourally identical to: ComputeNodeGroup(nn) → SetNodeState(nn, value) for the
        /// single-node-group case, minus the DFS/_groupBuf/_inGroup bookkeeping. The classification
        /// excludes HasCallback nodes, so there is no callback-enqueue step here.
        /// </summary>
        private static void RecalcNodeFast(int nn)
        {
            ref NodeInfo ns = ref NodeInfos[nn];
            NodeFlags f = ns.Flags;   // PullUp (+ any runtime SetHigh/SetLow); the others were excluded at classify time

            if (ns.TlistC1gnd != 0)
            {
                int* p = TransistorList + ns.TlistC1gnd;
                while (*p != 0) { if (NodeStates[*p++] != 0) { f |= NodeFlags.Gnd; break; } }   // any ON path to GND ⇒ pulled low
            }
            if (ns.TlistC1pwr != 0)
            {
                int* p = TransistorList + ns.TlistC1pwr;
                while (*p != 0) { if (NodeStates[*p++] != 0) { f |= NodeFlags.Pwr; break; } }   // any ON path to VCC ⇒ pulled high
            }

            SetNodeState(nn, FlagsToState[(int)f]);
        }

        // ── math-algos 策略三 (glitch diagnostic, NOT an optimization): how many times is the *same*
        //    node re-evaluated within one half-cycle? In an event-driven async settle a node can flip
        //    0→1→0 inside one half-cycle (race / glitch), spending D on transient passes that the
        //    steady state discards. We measure it instead of the (latch-breaking) delay-line the
        //    suggestion warned about: DistinctRecalcCount counts the *first* RecalcNode of each node
        //    in each half-cycle, so RecalcNodeCount / DistinctRecalcCount = avg recalcs per node per
        //    half-cycle. ~1.0 ⇒ no glitching, 策略三 is dead; >1.1 ⇒ glitches are eating D.
        //    Diagnostic only; allocated by a counted benchmark, gated behind CountEvents in RecalcNode.

        internal static long[]? _lastRecalcHc;   // per node, the Time of its last counted RecalcNode (-1 = never)
        internal static long DistinctRecalcCount;

        /// <summary>Arm the glitch diagnostic for the upcoming counted run (call after LoadSystem, before timing).</summary>
        internal static void InitGlitchDiag()
        {
            _lastRecalcHc = new long[NodeCount];
            Array.Fill(_lastRecalcHc, -1L);
            DistinctRecalcCount = 0;
        }
    }
}
