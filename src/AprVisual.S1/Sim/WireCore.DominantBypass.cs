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
        // Opt-in (TestRunner --dominant-bypass). When false, the capture + skip are short-circuited and
        // the engine is byte-for-byte the P-4 baseline.
        internal static bool EnableDominantBypass;

        // Per-node: gate node of c's single determining supply transistor, or 0 (= no single dominant
        // driver ⇒ never skip). ushort (node ids < 65536). Allocated in ClassifyPureLogicNodes, zeroed
        // (0 is safe — node 0 is the list sentinel, never a real node), freed in FreeUnmanagedMemory.
        internal static ushort* DominantGate;

        public static string LastDominantBypassStats = "(dominant-bypass off)";

        // Resolve c's dominant SUPPLY gate for a just-computed value, or 0 if there isn't exactly one.
        // Scans only c's OWN gnd/pwr channels (inline payload or the overflow Tlist sub-lists). Cheap-ish
        // (those lists are short) but it is the per-resolution maintenance the whole technique pays for.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ComputeDominantGate(int nn, byte value)
        {
            NodeInfo* ns = NodeInfos + nn;
            // Special-resolution nodes never name a single supply gate — stay conservative (0 = no skip).
            if ((ns->Flags & (NodeFlags.ForceCompute | NodeFlags.HasCallback)) != 0) return 0;

            int count = 0, last = 0;
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload;
                int c1c2x2 = ns->C1c2Count << 1;
                if (value == 0)   // low ⇒ determined by an ON gnd channel
                {
                    int s = c1c2x2, e = s + ns->GndCount;
                    for (int k = s; k < e; k++) { int g = pay[k]; if (NodeStates[g] != 0) { count++; last = g; } }
                }
                else              // high ⇒ determined by an ON pwr channel (no gnd reachable, else value'd be 0)
                {
                    int s = c1c2x2 + ns->GndCount, e = s + ns->PwrCount;
                    for (int k = s; k < e; k++) { int g = pay[k]; if (NodeStates[g] != 0) { count++; last = g; } }
                }
            }
            else
            {
                int off = (value == 0) ? ns->TlistC1gnd : ns->TlistC1pwr;
                if (off != 0)
                {
                    ushort* p = TransistorList + off;
                    int g;
                    while ((g = *p++) != 0) if (NodeStates[g] != 0) { count++; last = g; }
                }
            }
            return count == 1 ? (ushort)last : (ushort)0;
        }
    }
}
