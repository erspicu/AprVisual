using System.Runtime.CompilerServices;

namespace AprVisual.Sim
{
    // ── P-5: the dominant-driver turn-off bypass (RESEARCH — opt-in via --dominant-bypass) ──
    //
    // Background: this idea was prototyped once before and REVERTED — it was bit-exact and genuinely
    // pruned (+3.4%), but maintaining the runtime state it needs cost ~−15.4% ⇒ net ~−12% (0/16 paired
    // rounds). Write-up: WebSite/dominant-bypass.html. The prior prototype was never committed, so this
    // is a fresh, from-spec reconstruction on the `dominant-bypass` branch to (a) re-measure on the
    // current P-4 + .NET 11 baseline and (b) study whether the maintenance can be made cheap enough to
    // flip it positive. DEFAULT OFF so the golden baseline + leaderboard are untouched.
    //
    // THE IDEA. The engine's biggest residual cost is re-evaluating a node after one of its incident
    // gates opens, only to find it unchanged because another driver still holds it. Per node `c` store
    // DominantGate[c] = the gate node of the single SUPPLY transistor that currently determines c's
    // value (0 if there isn't exactly one). Then in SetNodeState's turn-off walk, an endpoint `c` of a
    // transistor gated by the node `g` that just went low can be skipped when
    //     DominantGate[c] != 0 && DominantGate[c] != g
    // — c is pinned by a different single driver, so g opening cannot change it. One uint16 compare
    // deletes a whole re-evaluation.
    //
    // SOUNDNESS of the capture (why this stays bit-exact — golden checksum is the final judge):
    // DominantGate[c] = s is set ONLY when c has EXACTLY ONE conducting supply transistor (gate s)
    // whose drive equals c's just-resolved value:
    //   • value 0 (low): c has exactly one ON gnd-channel (gate s). GND is the top LUT priority, so c
    //     is low regardless of any pass channel; opening a different gate g can't raise it. If a SECOND
    //     gnd is on, or the low came from a *neighbour's* gnd (c has none of its own), we store 0.
    //   • value 1 (high): c has exactly one ON pwr-channel (gate s) AND — implied by value==1 — no gnd
    //     is reachable in c's group (else GND would win and value would be 0). Opening a gate only
    //     REMOVES paths, so no gnd can appear; c stays high on its own pwr.
    // Pull-up-only / external-drive / floating / ForceCompute / callback resolutions store 0 (no single
    // transistor gate to name, or special resolution) — conservative but always safe.
    //
    // SOUNDNESS of the refresh: DominantGate[c] is rewritten on EVERY resolution of c (RecalcNodeFast for
    // singletons, the per-member loop for BFS groups), so it can never go stale. Skipping c (the whole
    // point) is safe precisely because a sound skip means c's value AND its dominant driver are both
    // unchanged by g opening, so the un-refreshed entry stays correct.
    //
    // THE COST (what the research is about): keeping DominantGate means every resolution must (a) do a
    // count-and-capture supply scan and (b) write a fresh ~NodeCount-ushort (~29 KB) random-access
    // stream on a memory-latency-bound loop. That bookkeeping is what sank it before. This reconstruction
    // keeps the capture in a clearly-isolated helper so the maintenance can be profiled and attacked.
    internal static unsafe partial class WireCore
    {
        // [P-5 is HARDCODED ON on this branch] — the capture + skip run unconditionally (no flag branch in
        // the hot loop). A/B is now this branch's binary vs a baseline binary, not a runtime toggle.

        // [P-5 final form — 1-bit pinned, packed into NodeStates] The dominant gate ID (16 bits, separate
        // array) is GONE — its random write stream was the irreducible cost. Instead each NodeStates byte
        // now carries: bit 0 = the logic state (StateBit), bit 1 = "this node is pinned by its OWN driver"
        // (PinnedBit). The pinned bit rides the mandatory state write (no new array, no new write stream,
        // zero cache bloat). EVERY NodeStates read that wants the state must mask `& StateBit` (bit 1 is
        // metadata). A node is "pinned" iff its current value is held by its own supply/pull-up regardless
        // of pass connections: (state==0 && it has ≥1 ON gnd channel) || (state==1 && (≥1 ON pwr channel ||
        // PullUp)). Turn-off skip: a PASS-transistor endpoint c with PinnedBit set can't change when this
        // (non-supply) gate opens — skip it (a supply-transistor endpoint is NOT skipped: that gate may BE
        // c's pin; the same walk's supply entry re-enqueues c, so it stays sound).
        internal const byte StateBit  = 1;
        internal const byte PinnedBit = 2;

        // [Escape B — domain shrink] 1 = this node can EVER trigger a P-5 skip, so its DominantGate is
        // worth maintaining; 0 = it can't, so we never compute/write it (it stays 0 ⇒ never skipped ⇒
        // identical behaviour). A node is a candidate iff it has BOTH a pass (c1c2) channel — so a
        // turn-off walk can reach it — AND its own supply (gnd/pwr) channel — so it can have a single
        // dominant driver; and it carries none of Gnd/Pwr/ForceCompute/HasCallback (special resolution).
        // Every NON-candidate's DominantGate is provably always 0 (ComputeDominantGate would return 0:
        // no supply ⇒ count 0; no pass channel ⇒ only its supply gate ever turns off at it ⇒ id==nn ⇒
        // no skip), so dropping their maintenance is BIT-EXACT, not an approximation. Static (load-time).
        internal static byte* IsBypassCandidate;

        public static string LastDominantBypassStats = "(dominant-bypass off)";

#if DEBUG
        // ── P-5 cost/benefit profiler (DEBUG ONLY; event counts identical to Release) ──
        // Decomposes the maintenance cost vs the skip benefit:
        //   FastCalls  = RecalcNodeFastDom resolutions (the gnd/pwr scan there is SHARED with the value
        //                resolution — P-5's only extra cost on this path is the pinned bool + the bit RMW).
        //   BfsCalls   = UpdatePinnedBit calls (BFS members) — this scan is EXTRA (not shared) = the real
        //                maintenance cost; BfsScans = gates it scanned (inline nodes; overflow uncounted).
        //   Set/Clear  = pinned-bit transitions written.
        //   SkipC1/C2  = enqueues P-5 actually suppressed in the turn-off walk (the BENEFIT, beyond P-2).
        internal static long DiagPinFastCalls, DiagPinBfsCalls, DiagPinBfsScans, DiagPinSet, DiagPinClear, DiagPinSkipC1, DiagPinSkipC2;
#endif

        // [P-5 — fused singleton resolve + pinned-bit maintenance] the count-and-capture twin of
        // RecalcNodeFast, taken only for skip-CANDIDATE nodes. Resolves the value AND sets/clears the pinned
        // bit in NodeStates in one pass over the gnd/pwr channels (anyG/anyP). The pinned rule (sound + value-
        // matched): low ⇒ pinned iff it has ≥1 OWN ON gnd channel; high ⇒ pinned iff ≥1 OWN ON pwr channel OR
        // it has a PullUp (value==1 implies no gnd is reachable, so an own pull-up/pwr holds it high when any
        // pass channel opens). The pinned bit rides the SetNodeState write — no separate array/stream.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecalcNodeFastDom(int nn)
        {
            NodeInfo* ns = NodeInfos + nn;
            int flags = (int)ns->Flags;   // Pwr/Gnd excluded at classify time; anyG<<5==Gnd, anyP<<4==Pwr
            int anyG = 0, anyP = 0;
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload;
                int gndStart = ns->C1c2Count << 1;
                int gndEnd = gndStart + ns->GndCount;
                for (int k = gndStart; k < gndEnd; k++) anyG |= NodeStates[pay[k]] & StateBit;
                int pwrEnd = gndEnd + ns->PwrCount;
                for (int k = gndEnd; k < pwrEnd; k++) anyP |= NodeStates[pay[k]] & StateBit;
            }
            else
            {
                if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; while (*p != 0) anyG |= NodeStates[*p++] & StateBit; }
                if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; while (*p != 0) anyP |= NodeStates[*p++] & StateBit; }
            }
            flags |= anyG << 5;   // Gnd
            flags |= anyP << 4;   // Pwr

            byte resolved = flags != 0 ? FlagsToState[flags] : (byte)(NodeStates[nn] & StateBit);
            if (flags != 0) SetNodeState(nn, resolved);

            // pinned ⇔ the resolved value is held by nn's OWN driver, independent of pass connections.
            bool pinned = resolved == 0 ? anyG != 0
                                        : (anyP != 0 || (flags & (int)NodeFlags.PullUp) != 0);
            // set/clear bit 1 on nn's NodeStates byte (state bit already written by SetNodeState above; if
            // the state was unchanged SetNodeState returned early, but bit 0 is intact either way).
            byte cur = NodeStates[nn];
            NodeStates[nn] = pinned ? (byte)(cur | PinnedBit) : (byte)(cur & ~PinnedBit);
#if DEBUG
            DiagPinFastCalls++;   // scan here is SHARED with value resolution; extra cost is just this RMW
            if (pinned) DiagPinSet++; else DiagPinClear++;
#endif
        }

        // [P-5] BFS-path twin: a candidate member `nn` of a just-resolved group got value `value`; set/clear
        // its pinned bit from its OWN gnd/pwr channels (same rule as RecalcNodeFastDom). Separate scan here
        // (group members aren't individually supply-scanned in AddNodeToGroup). Reads mask `& StateBit`.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdatePinnedBit(int nn, byte value)
        {
            NodeInfo* ns = NodeInfos + nn;
            bool pinned;
            if (value == 0)   // pinned-low iff ≥1 OWN ON gnd channel
            {
                int anyG = 0;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int s = ns->C1c2Count << 1, e = s + ns->GndCount;
                    for (int k = s; k < e; k++) anyG |= NodeStates[pay[k]] & StateBit;
#if DEBUG
                    DiagPinBfsScans += ns->GndCount;
#endif
                }
                else if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; while (*p != 0) anyG |= NodeStates[*p++] & StateBit; }
                pinned = anyG != 0;
            }
            else              // pinned-high iff ≥1 OWN ON pwr channel OR PullUp (value==1 ⇒ no gnd reachable)
            {
                if ((ns->Flags & NodeFlags.PullUp) != 0) pinned = true;
                else
                {
                    int anyP = 0;
                    if (ns->Inline != 0)
                    {
                        ushort* pay = ns->InlinePayload;
                        int s = (ns->C1c2Count << 1) + ns->GndCount, e = s + ns->PwrCount;
                        for (int k = s; k < e; k++) anyP |= NodeStates[pay[k]] & StateBit;
#if DEBUG
                        DiagPinBfsScans += ns->PwrCount;
#endif
                    }
                    else if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; while (*p != 0) anyP |= NodeStates[*p++] & StateBit; }
                    pinned = anyP != 0;
                }
            }
            byte cur = NodeStates[nn];
            NodeStates[nn] = pinned ? (byte)(cur | PinnedBit) : (byte)(cur & ~PinnedBit);
#if DEBUG
            DiagPinBfsCalls++;
            if (pinned) DiagPinSet++; else DiagPinClear++;
#endif
        }
    }
}
