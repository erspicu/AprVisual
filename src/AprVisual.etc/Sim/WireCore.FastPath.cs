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

        // [enqueue-prune classification — one byte per node, bit-packed]
        //   bit 0 (PruneTurnOnUnsafe = 1): node is UNSAFE for the same-state turn-ON prune (P-1). Set for
        //       no-PullUp / ForceCompute nodes (their merge can resolve non-monotonically via the floating
        //       capacitance tie-break); CLEARED again by P-3/P-4 for the cap<all-neighbours subset that
        //       provably can't win a tie-break.
        //   bit 1 (PruneTurnOffSkip = 2): node is a single-channel no-driver leaf that ISOLATES → float-holds
        //       when its channel opens (P-2); skip enqueuing it on turn-OFF.
        // Built at Reset by ClassifyPruneTaint() (bit 0) then ClassifyTurnOffSkip() (bit 1 + the P-3/4
        // bit-0 clears). NOTE [range-prune, 2026-06-10]: the HOT PATH no longer reads this array —
        // the class-major auto-renumber (WireCore.Renumber.cs) makes each prune class one contiguous
        // id block, and SetNodeState tests the RangePrune* boundaries instead. The mask's remaining
        // role is the GROUND TRUTH the ranges are verified against at every Reset (and the DEBUG
        // [waste-profile] tallies); in Release it is freed right after a
        // successful verification.
        internal const byte PruneTurnOnUnsafe = 1;
        internal const byte PruneTurnOffSkip  = 2;
        internal static byte* PruneMask;
        public static string LastPruneTaintStats = "(prune-taint not classified)";

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
        /// Build the enqueue-prune safety mask <see cref="PruneMask"/> (allocates it) and set bit 0
        /// (PruneTurnOnUnsafe): taint a node iff it has no PullUp (can float → hold-previous tie-break) OR
        /// its c1c2 channel-component contains a ForceCompute node (Gnd+Pwr cancel → non-monotone). Runs
        /// before ClassifyTurnOffSkip (which adds bit 1 and clears bit 0 for the P-3/4 subset). Not hot path.
        /// </summary>
        internal static unsafe void ClassifyPruneTaint()
        {
            int n = NodeCount;
            PruneMask = AllocArray<byte>(n);   // tracked + zeroed; freed in FreeUnmanagedMemory
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) FcuUnion(parent, nn, pay[k + 1]);
                }
                else if (ns->TlistC1c2s != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;
                    while (*p != 0) { FcuUnion(parent, nn, *(p + 1)); p += 2; }
                }
            }
            bool[] taintedRoot = new bool[n];
            int fcNodes = 0;
            for (int nn = 0; nn < n; nn++)
                if (Nodes[nn] != null && (NodeInfos[nn].Flags & NodeFlags.ForceCompute) != 0) { fcNodes++; taintedRoot[FcuFind(parent, nn)] = true; }
            int live = 0, tainted = 0, noPull = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                live++;
                bool fc = taintedRoot[FcuFind(parent, nn)];
                // [hold-previous guard] a node WITHOUT a PullUp can sit in a purely-floating group whose
                // value is the charge-held / capacitance tie-break (dynamic + storage cells: OAM/palette RAM,
                // dynamic CPU nodes). The same-state prune is only proven for driven (monotone-LUT) groups,
                // so prune ONLY on PullUp nodes; taint the rest.
                bool dynamic = (NodeInfos[nn].Flags & NodeFlags.PullUp) == 0;
                if (dynamic) noPull++;
                if (fc || dynamic) { PruneMask[nn] |= PruneTurnOnUnsafe; tainted++; }
            }
            double pct = live > 0 ? 100.0 * tainted / live : 0;
            LastPruneTaintStats = $"prune-taint: {tainted:N0} unsafe ({pct:F1}%) of {live:N0} live  [{fcNodes} ForceCompute, {noPull:N0} no-PullUp/dynamic] — same-state turn-on prune active on the rest";
        }

        // [P-2 turn-off enqueue prune — safety mask] 1 = skip enqueuing this node when the gate of its channel
        // goes LOW (turn-off). Eligible ⇔ the node is provably ISOLATED the instant its single channel opens,
        // so its group becomes {nn} with no driver ⇒ it floats and HOLDS its previous value ⇒ re-evaluating it
        // is a guaranteed no-op (RecalcNodeFast's flags==0 branch). Condition (all static, set once at Reset):
        //   • C1c2Count == 1   — exactly one channel transistor, so turning it off leaves NO conducting channel
        //   • GndCount == PwrCount == 0 — no supply path ⇒ nothing drives it once isolated
        //   • no PullUp / ForceCompute / HasCallback — would give it a non-floating resolution or a side effect
        //   • Inline — needed to read the counts reliably (overflow nodes are high-fanout, never C1c2Count==1)
        // The turn-ON path is unaffected (single-sided enqueue + P-1 already handle it). Measured: this safe
        // subset is ~25.9% of ALL RecalcNode pops (no-change, full_palette 300k). Bit-exact — golden checksum.
        // Sets bit 1 (PruneTurnOffSkip) of the shared PruneMask; PruneMask is allocated by ClassifyPruneTaint.
        public static string LastTurnOffSkipStats = "(turn-off-skip not classified)";

        internal static unsafe void ClassifyTurnOffSkip()
        {
            int n = NodeCount;
            // PruneMask already allocated + bit-0-populated by ClassifyPruneTaint (runs first). We OR in bit 1
            // here, and below clear bit 0 for the P-3/4 turn-on-safe subset.

            // [exclude handler-DRIVEN nodes] the RAM/ROM data-bus pins (e.g. u1._d*) are driven by the memory
            // handlers via WriteBits → SetHigh/SetLow, so once their channel opens they resolve to the DRIVEN
            // value (SetHigh/SetLow flag), NOT a pure float-hold — skipping their turn-off re-eval diverges
            // (measured: u1._d0/_d2/_d5 broke at full_palette 20k). Handlers are attached before Reset(), so
            // _callbacks is populated here; DataOut is exactly the set of externally-driven pins. Static table.
            var driven = new System.Collections.Generic.HashSet<int>();
            foreach (var cb in _callbacks)
                if (cb.DataOut != null)
                    for (int i = 0; i < cb.DLen; i++) driven.Add(cb.DataOut[i]);

            const NodeFlags exclude = NodeFlags.PullUp | NodeFlags.ForceCompute | NodeFlags.HasCallback | NodeFlags.Pwr | NodeFlags.Gnd;
            int count = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                if (driven.Contains(nn)) continue;                  // externally driven (SetHigh/SetLow) — not float-hold
                NodeInfo* ns = NodeInfos + nn;
                if ((ns->Flags & exclude) != 0) continue;
                if (ns->Inline == 0) continue;
                if (ns->C1c2Count != 1 || ns->GndCount != 0 || ns->PwrCount != 0) continue;
                PruneMask[nn] |= PruneTurnOffSkip;
                count++;
            }

            // [P-3 / P-4 — extend P-1's same-state turn-ON prune to nodes that can never win a floating
            //  tie-break] P-1 blanket-taints every no-PullUp node because a turn-ON MERGE can pick a
            //  different largest-cap winner / charge-share value the endpoint-only same-state check can't
            //  see. But a no-driver node X whose capacitance is STRICTLY LESS than EVERY one of its c1c2
            //  neighbours can never be the merged group's largest-cap member, and carries no driven flag,
            //  so merging an equal-state X into a neighbour's group is a provable no-op (and P-1's endpoint
            //  same-state check already blocks cross-state bridging). Un-taint that subset. Auto-excludes
            //  the heavy dynamic register / bus nodes (large capacitance keep P-1's taint) — no hand-list.
            //  P-3 = single-channel; P-4 = the multi-channel generalisation (cap < ALL neighbours).
            int p34 = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd || driven.Contains(nn)) continue;
                NodeInfo* ns = NodeInfos + nn;
                if ((ns->Flags & exclude) != 0 || ns->Inline == 0) continue;
                if (ns->GndCount != 0 || ns->PwrCount != 0) continue;   // has a supply driver ⇒ not a pure float node
                int nc = ns->C1c2Count;
                if (nc == 0) continue;
                ushort* pay = ns->InlinePayload;                        // [gate,other, gate,other, ...]
                int myCap = NodeConnections[nn];
                bool capLtAll = true;
                for (int k = 0; k < nc; k++) if (myCap >= NodeConnections[pay[k * 2 + 1]]) { capLtAll = false; break; }
                if (capLtAll) { PruneMask[nn] &= unchecked((byte)~PruneTurnOnUnsafe); p34++; }   // clear bit 0
            }
            // [supply-skip fold, 2026-06-09] mark supply as turn-off-skip: supply is never recomputed /
            // enqueued, so skip=1 on it is correct + bit-exact (this is what historically let the c2 enqueue
            // drop its explicit `c2 != ngnd && c2 != npwr` guard, +~1.5-2%). Under the range form the same
            // fact is expressed positionally — supply ids 1,2 < 3 ≤ S satisfy the `c < S` skip compare — but
            // the bits stay set here so the mask remains the complete ground truth the ranges are verified
            // against. Only bit 1; turn-on never sees supply as c1 (AddTransistor normalises supply onto c2).
            PruneMask[Npwr] |= PruneTurnOffSkip;
            PruneMask[Ngnd] |= PruneTurnOffSkip;
            double pct = NonNullNodeCount > 0 ? 100.0 * count / NonNullNodeCount : 0;
            LastTurnOffSkipStats = $"turn-off-skip (P-2): {count:N0} nodes ({pct:F1}%, excl {driven.Count} driven); P-3/4 turn-on un-taint: {p34:N0} (cap<all-neighbours) — bit-exact enqueue prunes";

            // ── [range-prune verify] the class-major renumber (WireCore.Renumber.cs) claims each prune
            // class occupies one contiguous id block. The mask just computed IS the ground truth: verify
            // EVERY id's bits equal the range-implied bits before any compare-based path may be trusted.
            // (Supply 1,2 carry TurnOffSkip and sit below 3 ≤ S, covered by the `c < S` skip-test form,
            // so the loop starts at 3. Fake handler nodes ≥ perm length extend the last block — covered.)
            if (RangePruneActive)
            {
                bool ok = 3 <= RangePruneA && RangePruneA <= RangePruneS && RangePruneS <= RangePruneB;
                for (int nn = 3; ok && nn < n; nn++)
                {
                    int bits = PruneMask[nn] & (PruneTurnOnUnsafe | PruneTurnOffSkip);
                    int implied = nn < RangePruneA ? (PruneTurnOnUnsafe | PruneTurnOffSkip)
                               : nn < RangePruneS ? PruneTurnOffSkip
                               : nn < RangePruneB ? 0
                               : PruneTurnOnUnsafe;
                    if (bits != implied) ok = false;
                }
                RangePruneOk = ok;
                if (!ok)
                {
                    // wrong ranges would MIS-prune — fall back to the safe-degenerate boundaries
                    // (prunes off, supply still guarded, bit-exact-correct on any numbering).
                    RangePruneA = RangeSafeA; RangePruneS = RangeSafeS; RangePruneB = RangeSafeB;
                    Console.Error.WriteLine("WireCore: range-prune layout MISMATCH — falling back to safe-degenerate ranges (prunes disabled)");
                }
                LastTurnOffSkipStats += ok ? " [range-prune VERIFIED]" : " [range-prune MISMATCH — safe-degenerate fallback]";
            }
            // (no layout applied ⇒ the RangeSafe* defaults are in force: prunes disabled, supply guarded,
            // correct on any numbering — what selftest / hand-built netlists run under in a RANGE build.)
#if !DEBUG
            // [array hygiene] in Release the hot path reads only the verified id RANGES — the mask was
            // the ground truth for the verification above and is dead weight from here on (15KB RAM;
            // untouched memory costs no cache, so this is hygiene not speed). DEBUG keeps it for the
            // [waste-profile] tallies. The auto-renumber's pass 0/1
            // (RangePruneActive == false) must keep it too: CapturePruneClasses reads it after Reset.
            if (RangePruneActive && RangePruneOk) { FreeAligned(PruneMask); PruneMask = null; }
#endif
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
#if DEBUG
            {   // [fast-gate dist] DEBUG-only: tally gate counts of fast-path pops (Design-1 MLP sizing)
                int g = ns->GndCount, p = ns->PwrCount, c = ns->C1c2Count;
                DiagFastPops++;
                DiagFastGnd[g < 8 ? g : 7]++; DiagFastPwr[p < 8 ? p : 7]++; DiagFastC1c2[c < 8 ? c : 7]++;
                if (ns->Inline != 0) { DiagFastInline++; if (c <= 1 && g <= 2 && p <= 2) DiagFastFitsFixed++; }
            }
#endif
            byte* nodeStates = NodeStates;   // [trial] hoist static ptr (used in up to 2 gnd/pwr loops)
            // [T-A] keep flags as int throughout — drops the (NodeFlags)((uint)..) casts; anyG<<5==Gnd, anyP<<4==Pwr.
            int flags = (int)ns->Flags;   // PullUp and/or runtime SetHigh/SetLow, or 0 (floating); Pwr/Gnd excluded at classify time

            // OR-all (branchless, R4): NodeStates is 0/1, so `any` ∈ {0,1}. Same result as early-break, no per-gate branch.
            // S2-A: read gnd/pwr gates from the inline payload (one cache line) when available.
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload;
                int gndStart = ns->C1c2Count << 1;
                byte gp = ns->GndPwr;
                if (gp == 1)
                {
                    flags |= nodeStates[pay[gndStart]] << 5;   // one GND gate, no PWR gates
                }
                else if (gp == 2)
                {
                    flags |= (nodeStates[pay[gndStart]] | nodeStates[pay[gndStart + 1]]) << 5;   // two GND gates, no PWR gates
                }
                else if (gp != 0)
                {
                    int gndEnd = gndStart + (gp & 0x0F);
                    int anyG = 0;
                    for (int k = gndStart; k < gndEnd; k++) anyG |= nodeStates[pay[k]];   // any ON path to GND ⇒ pulled low
                    flags |= anyG << 5;
                    int pwrEnd = gndEnd + (gp >> 4);
                    int anyP = 0;
                    for (int k = gndEnd; k < pwrEnd; k++) anyP |= nodeStates[pay[k]];      // any ON path to VCC ⇒ pulled high
                    flags |= anyP << 4;
                }
            }
            else
            {
                if (ns->TlistC1gnd != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1gnd;
                    int any = 0;
                    while (*p != 0) any |= nodeStates[*p++];                  // any ON path to GND ⇒ pulled low
                    flags |= any << 5;
                }
                if (ns->TlistC1pwr != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1pwr;
                    int any = 0;
                    while (*p != 0) any |= nodeStates[*p++];                  // any ON path to VCC ⇒ pulled high
                    flags |= any << 4;
                }
            }

            // flags == 0 ⇒ floating singleton ⇒ hold previous (NodeStates[nn] unchanged) ⇒ SetNodeState would
            // be a pure no-op, so skip the call entirely (saves the operand read + the call). Bit-exact.
            if (flags != 0) SetNodeState(nn, FlagsToState[flags]);
        }

        // ── DIAGNOSTIC ONLY (NOT hot path — runs once from --fc-taint-stats, like --payload-hist) ──
        // "same-state turn-on prune" eligibility study. Union-find over c1c2 (normal-to-normal)
        // channels = the maximal possible conducting group (all channels ON). A node is SAFE for the
        // prune iff its channel-component contains NO ForceCompute node (then the FC Gnd+Pwr cancel can
        // never fire for any group it joins, so the resolver stays monotone). Reports safe vs tainted.
        internal static unsafe string FcTaintStats()
        {
            int n = NodeCount;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            // edges = c1c2 channels (the ones that grow a group's node membership)
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) FcuUnion(parent, nn, pay[k + 1]);
                }
                else if (ns->TlistC1c2s != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;
                    while (*p != 0) { FcuUnion(parent, nn, *(p + 1)); p += 2; }
                }
            }

            // FC nodes: read the flag off NodeInfos (the ForceComputeList is freed after compose).
            bool[] taintedRoot = new bool[n];
            int fcNodes = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null) continue;
                if ((NodeInfos[nn].Flags & NodeFlags.ForceCompute) != 0) { fcNodes++; taintedRoot[FcuFind(parent, nn)] = true; }
            }

            var compSize = new System.Collections.Generic.Dictionary<int, int>();
            int live = 0, safe = 0, tainted = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                live++;
                int r = FcuFind(parent, nn);
                compSize.TryGetValue(r, out int c); compSize[r] = c + 1;
                if (taintedRoot[r]) tainted++; else safe++;
            }

            int comps = compSize.Count, taintedComps = 0, biggest = 0; bool biggestTainted = false;
            foreach (var kv in compSize)
            {
                if (taintedRoot[kv.Key]) taintedComps++;
                if (kv.Value > biggest) { biggest = kv.Value; biggestTainted = taintedRoot[kv.Key]; }
            }

            double P(int x) => live == 0 ? 0 : 100.0 * x / live;
            return
                "# ===== same-state turn-on prune eligibility (FC-taint over c1c2 channel graph) =====\n" +
                $"#  live nodes:          {live:N0}\n" +
                $"#  ForceCompute nodes:  {fcNodes:N0}\n" +
                $"#  channel components:  {comps:N0}  (FC-tainted: {taintedComps:N0})\n" +
                $"#  biggest component:   {biggest:N0} nodes ({P(biggest):F1}% of live){(biggestTainted ? "  [FC-TAINTED]" : "")}\n" +
                $"#  SAFE to prune (FC-free component):    {safe:N0}  ({P(safe):F1}%)\n" +
                $"#  CANNOT prune (FC-tainted component):  {tainted:N0}  ({P(tainted):F1}%)\n" +
                "# ================================================================================";
        }
        private static int FcuFind(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        private static void FcuUnion(int[] p, int a, int b) { int ra = FcuFind(p, a), rb = FcuFind(p, b); if (ra != rb) p[ra] = rb; }
    }
}
