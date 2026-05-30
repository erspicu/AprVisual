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
        // (Gnd>Pwr>SetHigh>SetLow>PullUp). PullUp is NOT required: the single-node group is {nn}
        // purely from TlistC1c2s being empty (no channel either way), independent of the pull. The
        // only case PullUp used to "guarantee away" is empty OR-ed flags — a purely-floating group,
        // which GetNodeValue resolves as "largest-cap member holds its previous state" (and for a
        // one-node group that member IS nn). RecalcNodeFast reproduces that branch explicitly
        // (f == None ⇒ keep NodeStates[nn]) instead of feeding 0 to the LUT, so dropping the PullUp
        // gate stays exact while widening coverage 23.1% → 26.7% (verified checksum-identical).
        //
        // This does NOT shrink D (the dirty-set size); it shrinks the *constant* per dirty node.
        // S1 fork: ALWAYS on (verified +2% peak 6d01abe; PullUp gate dropped for +1.6% more).

        // Per-node classification, 1 = take RecalcNodeFast. Unmanaged, sized NodeCount,
        // (re)built by ClassifyPureLogicNodes() at the end of Reset().
        internal static byte* IsPureLogic;

        public static int PureLogicNodeCount;
        public static string LastFastPathStats = "(fast-path disabled — default; --fast-path to enable)";

        /// <summary>
        /// Flag the nodes eligible for the O(1) RecalcNodeFast path. Must run after Reset() has built
        /// the per-node Tlist* sub-lists and set the static flags (pull-up / forceCompute / supply).
        /// Eligible ⇔ has NO channel to a normal node (TlistC1c2s empty ⇒ group is provably {nn}),
        /// and carries none of HasCallback / ForceCompute / Pwr / Gnd (callbacks must fire through the
        /// normal path; forceCompute/supply have special resolution). PullUp is NOT required —
        /// RecalcNodeFast holds-previous on the empty-flags (floating) case. SetHigh/SetLow are
        /// runtime-only and handled by the LUT, so they are NOT excluded here.
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
                if ((ns.Flags & exclude) != 0) continue;
                if (ns.TlistC1c2s != 0) continue;                   // any normal-node channel ⇒ group can grow ⇒ not {nn}
                // PullUp NOT required: a no-pullup singleton resolves to GND/PWR if a channel
                // conducts, else floats → "hold previous" (RecalcNodeFast handles the empty-flags
                // case as a no-op, exactly like ComputeNodeGroup's single-node floating tie-break).
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
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void RecalcNodeFast(int nn)
        {
            ref NodeInfo ns = ref NodeInfos[nn];
            NodeFlags f = ns.Flags;   // PullUp and/or runtime SetHigh/SetLow, or None (floating); Pwr/Gnd excluded at classify time

            if (ns.TlistC1gnd != 0)
            {
                ushort* p = TransistorList + ns.TlistC1gnd;
                while (*p != 0) { if (NodeStates[*p++] != 0) { f |= NodeFlags.Gnd; break; } }   // any ON path to GND ⇒ pulled low
            }
            if (ns.TlistC1pwr != 0)
            {
                ushort* p = TransistorList + ns.TlistC1pwr;
                while (*p != 0) { if (NodeStates[*p++] != 0) { f |= NodeFlags.Pwr; break; } }   // any ON path to VCC ⇒ pulled high
            }

            // f == None ⇒ singleton group with nothing driving it ⇒ floating: hold previous state
            // (matches GetNodeValue's single-node tie-break, where max-cap member == nn itself).
            // For PullUp nodes f always carries the PullUp bit, so this is the common (non-None) path.
            SetNodeState(nn, f != NodeFlags.None ? FlagsToState[(int)f] : NodeStates[nn]);
        }
    }
}
