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
            int count = 0, dynCount = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (nn == Npwr || nn == Ngnd || Nodes[nn] == null) continue;
                ref NodeInfo ns = ref NodeInfos[nn];
                if ((ns.Flags & exclude) != 0) continue;            // class 0 — must go through the BFS (callback / forceCompute resolution)
                // S2-A2: inline nodes no longer set TlistC1c2s (their channel sublist isn't emitted into
                // TransistorList), so "has a normal-node channel?" is read from C1c2Count for them.
                bool hasC1c2 = ns.Inline != 0 ? ns.C1c2Count != 0 : ns.TlistC1c2s != 0;
                if (hasC1c2)
                {
                    // R-1: class 2 — "dynamic-singleton candidate". Has normal-node channel(s) so the
                    // group CAN grow, but if all those channels happen to be OFF this half-cycle the
                    // group is exactly {nn} and RecalcNode can resolve it via the O(1) RecalcNodeFast
                    // path (same resolution as a static pure-logic node — this node carries none of the
                    // excluded flags). RecalcNode does the runtime "are all c1c2s gates off?" check.
                    IsPureLogic[nn] = 2;
                    dynCount++;
                    continue;
                }
                // class 1 — static pure-logic: group is provably {nn} (no normal channel at all).
                // PullUp NOT required: a no-pullup singleton resolves to GND/PWR if a channel
                // conducts, else floats → "hold previous" (RecalcNodeFast handles the empty-flags
                // case as a no-op, exactly like ComputeNodeGroup's single-node floating tie-break).
                IsPureLogic[nn] = 1;
                count++;
            }
            PureLogicNodeCount = count;
            double pct = NonNullNodeCount > 0 ? 100.0 * count / NonNullNodeCount : 0;
            double dpct = NonNullNodeCount > 0 ? 100.0 * dynCount / NonNullNodeCount : 0;
            LastFastPathStats = $"fast-path: {count:N0} static pure-logic ({pct:F1}%) + {dynCount:N0} dyn-singleton candidates ({dpct:F1}%) of {NonNullNodeCount:N0} live nodes";
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
            NodeInfo* ns = NodeInfos + nn;
            // [T-A] keep flags as int throughout — drops the (NodeFlags)((uint)..) casts; anyG<<5==Gnd, anyP<<4==Pwr.
            int flags = (int)ns->Flags;   // PullUp and/or runtime SetHigh/SetLow, or 0 (floating); Pwr/Gnd excluded at classify time

            // OR-all (branchless, R4): NodeStates is 0/1, so `any` ∈ {0,1}. Same result as early-break, no per-gate branch.
            // S2-A: read gnd/pwr gates from the inline payload (one cache line) when available.
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload;
                int gndStart = ns->C1c2Count << 1;
                int gndEnd = gndStart + ns->GndCount;
                int anyG = 0;
                for (int k = gndStart; k < gndEnd; k++) anyG |= NodeStates[pay[k]];   // any ON path to GND ⇒ pulled low
                flags |= anyG << 5;
                int pwrEnd = gndEnd + ns->PwrCount;
                int anyP = 0;
                for (int k = gndEnd; k < pwrEnd; k++) anyP |= NodeStates[pay[k]];      // any ON path to VCC ⇒ pulled high
                flags |= anyP << 4;
            }
            else
            {
                if (ns->TlistC1gnd != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1gnd;
                    int any = 0;
                    while (*p != 0) any |= NodeStates[*p++];                  // any ON path to GND ⇒ pulled low
                    flags |= any << 5;
                }
                if (ns->TlistC1pwr != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1pwr;
                    int any = 0;
                    while (*p != 0) any |= NodeStates[*p++];                  // any ON path to VCC ⇒ pulled high
                    flags |= any << 4;
                }
            }

            // flags == 0 ⇒ floating singleton ⇒ hold previous state (matches GetNodeValue's single-node tie-break).
            SetNodeState(nn, flags != 0 ? FlagsToState[flags] : NodeStates[nn]);
        }
    }
}
