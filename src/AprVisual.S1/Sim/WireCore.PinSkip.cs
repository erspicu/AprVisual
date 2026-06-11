using System;

namespace AprVisual.Sim
{
    // ── P-5z: ZERO-MAINTENANCE dominant-driver turn-off skip (the P-5 revival, 2026-06-11) ──────────
    //
    // HISTORY. P-5 ("dominant-bypass", branch dominant-bypass, WebSite/dominant-bypass.html) proved the
    // prize is real — a 1-bit "pinned by my own driver" flag suppresses ~40% of all node re-evaluations
    // (+13.76% gross) — but MAINTAINING that runtime bit (recompute + store at every resolution) costs a
    // ~7% floor on this latency-bound engine, so the best net was −6.84% and it was shelved as a dead end.
    //
    // THE FLIP. Part of the pinned predicate decomposes into (static structure) × (1 NodeStates byte),
    // and NodeStates is already maintained by the engine. So for the GND LEG the bit can be DERIVED AT
    // THE SKIP SITE instead of cached — deleting the entire maintenance + write-stream cost, not
    // cheapening it. ONE class, one test:
    //
    //   the single-gnd-probe class: EXACTLY 1 own gnd channel, 0 own pwr, has pass channel(s), clean
    //   component (exclusions below). PullUp irrelevant to the test — it only decides which renumber
    //   block the node sits in (M = PullUp ⊂ block 2, D = no-PullUp ⊂ block 3, adjacent across B).
    //       skip a turn-off endpoint c ⇔ NodeStates[probe] != 0, probe = that gnd channel's GATE node:
    //       · probe ON ∧ c==0: c is held low by its OWN conducting gnd channel; Gnd is the top LUT
    //         priority, so no path removal can change the resolution = no-op. This argument is
    //         TRANSIENT-PROOF — it doesn't need c's group to be settled.
    //       · probe ON ∧ c==1: impossible settled (gnd reachable ⇒ 0); transiently c is QUEUED (the
    //         probe's turn-on always enqueues c — class M is same-state-pruned only when states match,
    //         and 1 ≠ states[Ngnd]; class D is turn-on-UNSAFE, always enqueued) so the nextHash dedup
    //         blocks the skip, or the suppressed re-eval duplicates a pending one (no-op either way).
    //
    //   ⚠ NEGATIVE RESULT (measured, bisect 2026-06-11): the c==1 "PullUp pins high" leg — the BULK of
    //   the original P-5's prize (E0: 64% incl. this leg) — is NOT implementable maintenance-free.
    //   Its soundness ("value 1 ⇒ no gnd reachable in the group") holds for the value AT c's LAST
    //   RESOLUTION; reading live NodeStates[c] at the skip site picks up mid-settle transients where
    //   the group is value-inconsistent, and suppressing the re-eval then loses fixes to OTHER residual
    //   members → checksum diverged at 100k hc. The maintained pinned bit is exactly what made that leg
    //   sound on the dominant-bypass branch — and the maintenance is the ~7% floor. Do not re-add the
    //   states[c]-based leg.
    //
    // SELF-GUARDING INVALIDATION (why no maintenance is needed): the only events that change the derived
    // bit are (a) c's own state change — only happens inside a resolution, whose enqueues re-read fresh
    // states; (b) the probe gate's state change — but that gate's turn-off IS a (c1=c, c2=supply) walk
    // entry, which the (2-c2)>>31 supply mask always lets through (and SetNodeState writes the gate's new
    // state BEFORE walking, so the probe read sees it). The within-resolution staleness window (a group
    // member read mid-member-loop before its state write) is closed STRUCTURALLY: components containing a
    // channel transistor gated from INSIDE the same component (latch/bootstrap gate feedback) are
    // excluded from both classes, so a skip-tested endpoint is never a co-member of the group whose
    // resolution is doing the testing.
    //
    // SOUNDNESS EXCLUSIONS (adversarial review, 2026-06-11 — all at channel-COMPONENT level):
    //   · ForceCompute components: the Gnd+Pwr cancel breaks "value 1 ⇒ no gnd reachable" (the 2C02
    //     spr_d/OAM bus — full_palette barely exercises it, so goldens alone can NOT certify this).
    //   · HasCallback components: hygiene (callback targets are structurally immune, but free to exclude).
    //   · Externally drivable components (handler DataOut pins, clk, res): a latched SetLow outranks
    //     PullUp in the LUT, breaking the class-M c==1 leg.
    //   · Gate-feedback components: see above.
    //   These cost only ~2.6% of the suppressible population ([E0] class 7).
    //
    // ENCODING. A full-size read-only table: ProbeGate[id] = the gnd channel's gate for class members,
    // 0 for everyone else — and NodeStates[0] (the reserved node) is always 0, so the single skip term
    // `NodeStates[ProbeGate[c]]` is self-disabling for non-members with NO range compare and NO masking
    // ALU (a first range-encoded variant measured −0.51% median: ~13 ALU × ~50M endpoint tests/100k hc
    // ate the prize; the table form cuts the c2-side test to 2 branch-fed loads + 1 OR). The test is
    // folded into the existing turn-off enqueue condition (see WireCore.Recalc.cs) — zero new branches,
    // zero writes, zero per-resolution work, and NO renumber sub-key (the first variant's sub-key also
    // perturbed the self-captured locality order). Numbering-independent: nothing to verify, no
    // safe-degenerate mode — the table is rebuilt from structure (the ground truth) at every Reset and
    // is correct on any netlist, including selftest's hand-built ones.
    //
    // Bit-exactness gates: Debug+Release golden checksums (100k/300k/400k/1M), selftest, SMB1 10M hc
    // (sprite-heavy — exercises the FC/OAM region full_palette doesn't).
    internal static unsafe partial class WireCore
    {
        // ProbeGate[id] = the probe gate node id (the single own-gnd channel's GATE) for class members,
        // 0 for every other node — NodeStates[0] is the reserved node and always 0, so the skip term
        // `NodeStates[ProbeGate[c]]` degenerates to 0 (no skip) for non-members with NO range test and
        // NO masking ALU. Indexed by RAW node id (any numbering — needs no renumber, no verification,
        // no safe-degenerate: the table IS the ground truth, rebuilt from structure at every Reset).
        // Read-only after Reset; full NodeCount ushorts (~29KB), hot rows dense under the locality key.
        // Reset-pool allocation (AllocArray) — freed + nulled by the next Reset's FreeUnmanagedMemory.
        internal static ushort* ProbeGate;

        public static string LastPinSkipStats = "(pin-skip not classified)";

#if DEBUG
        internal static long DiagPinSkips;   // suppressed enqueues (fired P-5z skips), c1+c2 sides
#endif

        /// <summary>
        /// Recompute the pin-skip classes from netlist structure (ground truth), then verify the
        /// renumber-implied ranges and build the probe table — or disable on any mismatch. Runs at the
        /// end of ClassifyTurnOffSkip (needs the per-node flags/payloads and the handler `driven` set).
        /// </summary>
        internal static unsafe void ClassifyPinSkip(System.Collections.Generic.HashSet<int> driven)
        {
            int n = NodeCount;
            var cls = new byte[n];
            var probeOf = new int[n];

            // union-find over c1c2 (pass) channels — the connected components a conducting group can span
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

            // component-level exclusions (see the file header)
            bool[] badRoot = new bool[n];
            for (int nn = 0; nn < n; nn++)
                if (Nodes[nn] != null && (NodeInfos[nn].Flags & (NodeFlags.ForceCompute | NodeFlags.HasCallback)) != 0)
                    badRoot[FcuFind(parent, nn)] = true;
            foreach (int d in driven) badRoot[FcuFind(parent, d)] = true;
            int clkN = LookupNode("clk"); if (clkN != EmptyNode) badRoot[FcuFind(parent, clkN)] = true;
            int resN = LookupNode("res"); if (resN != EmptyNode) badRoot[FcuFind(parent, resN)] = true;
            // gate-feedback: a channel transistor whose GATE is inside its own component
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                int root = FcuFind(parent, nn);
                if (badRoot[root]) continue;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2)
                        if (FcuFind(parent, pay[k]) == root) { badRoot[root] = true; break; }
                }
                else if (ns->TlistC1c2s != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;
                    while (*p != 0) { if (FcuFind(parent, *p) == root) { badRoot[root] = true; break; } p += 2; }
                }
            }

            // per-node class
            int mCount = 0, dCount = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                bool hasPass = ns->Inline != 0 ? ns->C1c2Count != 0 : ns->TlistC1c2s != 0;
                if (!hasPass) continue;   // can only be reached through the supply-masked walk entries
                if ((ns->Flags & (NodeFlags.ForceCompute | NodeFlags.HasCallback | NodeFlags.Pwr | NodeFlags.Gnd)) != 0) continue;
                if (badRoot[FcuFind(parent, nn)]) continue;
                int g = 0, p2 = 0, sg = 0;
                if (ns->Inline != 0)
                {
                    g = ns->GndCount; p2 = ns->PwrCount;
                    if (g == 1) sg = ns->InlinePayload[ns->C1c2Count << 1];   // the single gnd channel's gate
                }
                else
                {
                    if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; while (*p != 0) { if (g == 0) sg = *p; g++; p++; } }
                    if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; while (*p != 0) { p2++; p++; } }
                }
                bool pu = (ns->Flags & NodeFlags.PullUp) != 0;
                if (p2 == 0 && g == 1)
                {
                    // single own-gnd probe class — PullUp irrelevant to the test (gnd leg only)
                    cls[nn] = 1;
                    probeOf[nn] = sg;
                    if (pu) mCount++; else dCount++;
                }
            }

            // table form: ProbeGate[id] = probe gate for members, 0 (→ NodeStates[0] == 0, never skips)
            // for everyone else — numbering-independent, nothing to verify
            ProbeGate = AllocArray<ushort>(n);   // tracked + zeroed
            for (int nn = 3; nn < n; nn++)
                if (cls[nn] != 0) ProbeGate[nn] = (ushort)probeOf[nn];
            double pct = NonNullNodeCount > 0 ? 100.0 * (mCount + dCount) / NonNullNodeCount : 0;
            LastPinSkipStats = $"pin-skip (P-5z): {mCount + dCount:N0} single-gnd-probe nodes ({pct:F1}%; {mCount:N0} PullUp + {dCount:N0} plain) — zero-maintenance turn-off skip";
        }
    }
}
