using System;
using System.Collections.Generic;
using System.Text;

namespace AprVisual.Sim
{
    // ── S2 gate-abstraction node-reduction ESTIMATE (load-time, diagnostic only) ──
    //
    // Answers the go/no-go question for the S2 gate-abstraction plan (2026-06-10): how many nodes could a
    // transistor→gate extraction actually ELIMINATE? Per the EDA reality check (Gemini consult), the only
    // real win is node-COUNT reduction (the fast-path already resolves logic nodes O(1), so cheaper per-node
    // eval is swallowed by memory latency). And node reduction only comes from INTERNAL nodes of recognized
    // gates — which in NMOS means series pull-down stacks (NAND/AOI), because parallel-pull-down NOR gates
    // have ZERO internal nodes. Memory/dynamic nodes (no pull-up, hold charge) MUST be kept switch-level.
    //
    // This pass counts the candidate sets so we can compare against the abandon threshold (< ~1,500 = 10%).
    // It does NOT change the engine — invoked via TestRunner --gate-abs-estimate <rom>.
    internal static unsafe partial class WireCore
    {
        public static string GateAbsEstimate()
        {
            int n = NodeCount;

            // handler-driven pins (RAM/ROM/bus DataOut etc.) — externally driven, NOT free internal wires.
            var driven = new HashSet<int>();
            foreach (var cb in _callbacks)
                if (cb.DataOut != null)
                    for (int i = 0; i < cb.DLen; i++) driven.Add(cb.DataOut[i]);

            // gate-fanout per node = how many transistors this node is the GATE of (its NodeTlistGates list,
            // a 0-terminated (c1,c2) pair list in TransistorList). Used for the "aggressive cell-fold" ceiling.
            int Fanout(int nn)
            {
                int off = NodeTlistGates[nn];
                if (off == 0) return 0;
                ushort* p = TransistorList + off;
                int c = 0;
                while (*p != 0) { c++; p += 2; }   // (c1,c2) pairs, 0-terminated
                return c;
            }

            int live = 0, pullUp = 0, noPullUp = 0, gatesNothing = 0;
            int foldFanout1 = 0, foldFanout2 = 0;   // gate-output (pull-up) nodes that gate exactly 1 / 2 transistors, not special/driven, no pass channel ⇒ simple inverter/buffer feeding a few consumers ⇒ aggressive-fold candidates
            int foldableIntoLogic = 0;              // of the fanout-1 ones: its single gated transistor is a PULL-DOWN of a CONSUMER that is itself a pure-logic gate ⇒ cleanly foldable into an AOI, eliminating this node. The realistic aggressive-fold count.
            int pureLogicNOR = 0;          // static pure-logic = pull-up + only direct-to-GND pulls, NO pass channel ⇒ parallel NOR ⇒ 0 internal nodes
            int elimCeiling = 0;           // no pull-up ∩ gates-nothing ∩ not FC/callback ∩ not handler-driven ∩ has a channel  (UPPER bound on eliminable internal/intermediate wires)
            int elimWithPass = 0;          //   of those, how many have a pass (c1c2) channel (series-stack / pass-chain intermediates)
            int gateOutSeriesStack = 0;    // gate-output nodes (pull-up) that ALSO have a pass channel ⇒ their pull-down has a series stack ⇒ internal nodes exist behind them

            const NodeFlags fcCb = NodeFlags.ForceCompute | NodeFlags.HasCallback;

            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                live++;
                NodeInfo* ns = NodeInfos + nn;
                bool hasPullUp = (ns->Flags & NodeFlags.PullUp) != 0;
                bool hasPass   = ns->Inline != 0 ? (ns->C1c2Count != 0) : (ns->TlistC1c2s != 0);
                bool hasSupply = ns->Inline != 0 ? (ns->GndCount != 0 || ns->PwrCount != 0) : (ns->TlistC1gnd != 0 || ns->TlistC1pwr != 0);
                bool gatesAny  = NodeTlistGates[nn] != 0;     // node is the GATE of ≥1 transistor (a real logic signal)
                bool special   = (ns->Flags & fcCb) != 0;
                bool isDriven  = driven.Contains(nn);

                if (hasPullUp) pullUp++; else noPullUp++;
                if (!gatesAny) gatesNothing++;

                // static pure-logic NOR: pull-up, pulls straight to GND, no pass channel ⇒ no internal node.
                if (hasPullUp && hasSupply && !hasPass && !special) pureLogicNOR++;

                // gate output with a series stack behind it (pull-up + a pass channel = the pull-down isn't all direct-to-GND).
                if (hasPullUp && hasPass) gateOutSeriesStack++;

                // aggressive cell-fold candidates: a pull-up gate output with NO pass channel (pure NOR/inverter)
                // that gates only 1 or 2 transistors, not handler-driven/special — i.e. a small gate whose output
                // feeds very few consumers, so folding it into its consumer(s) would eliminate this node too.
                if (hasPullUp && !hasPass && !special && !isDriven)
                {
                    int fo = Fanout(nn);
                    if (fo == 1)
                    {
                        foldFanout1++;
                        // inspect the single transistor nn gates: (c1,c2) pair at NodeTlistGates[nn].
                        ushort* tp = TransistorList + NodeTlistGates[nn];
                        int tc1 = tp[0], tc2 = tp[1];
                        // is it a supply transistor (pull-down/up)? then the consumer X is the non-supply endpoint.
                        int x = (tc2 == Ngnd || tc2 == Npwr) ? tc1 : (tc1 == Ngnd || tc1 == Npwr) ? tc2 : -1;
                        if (x > 0 && x != Ngnd && x != Npwr && Nodes[x] != null)
                        {
                            NodeInfo* xs = NodeInfos + x;
                            bool xPullUp = (xs->Flags & NodeFlags.PullUp) != 0;
                            bool xPass   = xs->Inline != 0 ? (xs->C1c2Count != 0) : (xs->TlistC1c2s != 0);
                            bool xSpecial = (xs->Flags & fcCb) != 0;
                            // X is a pure-logic gate ⇒ nn folds into X's pull-down network (AOI), nn's node goes away.
                            if (xPullUp && !xPass && !xSpecial) foldableIntoLogic++;
                        }
                    }
                    else if (fo == 2) foldFanout2++;
                }

                // elimination ceiling: a wire that carries NO logic signal (gates nothing), has NO own driver
                // (no pull-up), isn't special/handler-driven, and is an actual channel node. These are the only
                // nodes a gate extraction could fold away. (NB: most of these are dynamic/charge-holding in NMOS
                // — i.e. exactly the memory we must PRESERVE — so this is an over-count, an upper bound.)
                if (!hasPullUp && !gatesAny && !special && !isDriven && (hasPass || hasSupply))
                {
                    elimCeiling++;
                    if (hasPass) elimWithPass++;
                }
            }

            double P(int x) => live == 0 ? 0 : 100.0 * x / live;
            var sb = new StringBuilder();
            sb.Append("# ===== S2 gate-abstraction node-reduction estimate =====\n");
            sb.Append($"#  live nodes:                 {live:N0}   (transistors {TransistorBuildCount:N0})\n");
            sb.Append($"#  gate outputs (pull-up):     {pullUp:N0}  ({P(pullUp):F1}%)\n");
            sb.Append($"#    of which static pure-NOR: {pureLogicNOR:N0}  ({P(pureLogicNOR):F1}%)  ← 0 internal nodes (parallel pull-down) ⇒ extracting them eliminates NOTHING\n");
            sb.Append($"#    of which have a series stack (pull-up + pass channel): {gateOutSeriesStack:N0}  ({P(gateOutSeriesStack):F1}%)\n");
            sb.Append($"#  no-pull-up nodes:           {noPullUp:N0}  ({P(noPullUp):F1}%)  ← dynamic/storage/bus/internal (mostly MUST stay switch-level)\n");
            sb.Append($"#  gates-nothing nodes:        {gatesNothing:N0}  ({P(gatesNothing):F1}%)  ← carry no logic signal\n");
            sb.Append($"#  ---\n");
            sb.Append($"#  ELIMINATION CEILING (no-pull-up ∩ gates-nothing ∩ not-FC/cb ∩ not-driven ∩ has-channel):\n");
            sb.Append($"#      {elimCeiling:N0} nodes  ({P(elimCeiling):F1}% of live)   [of which {elimWithPass:N0} have a pass channel]\n");
            sb.Append($"#  go/no-go: abandon if this is < ~1,500 (10%). NB this is an UPPER bound — most no-pull-up\n");
            sb.Append($"#  intermediates are charge-holding dynamic nodes that MUST be preserved, so the true\n");
            sb.Append($"#  eliminable set is smaller. Max realistic speedup ≈ this % (Amdahl on event volume).\n");
            sb.Append($"#  ---\n");
            sb.Append($"#  AGGRESSIVE cell-fold candidates (pull-up + no-pass + not-driven gate outputs by gate-fanout):\n");
            sb.Append($"#      fanout==1: {foldFanout1:N0} ({P(foldFanout1):F1}%)  fanout==2: {foldFanout2:N0} ({P(foldFanout2):F1}%)\n");
            sb.Append($"#      ↳ of fanout-1, REALISTICALLY foldable-into-logic (single consumer is a pure-logic gate's pull-down):\n");
            sb.Append($"#          {foldableIntoLogic:N0} ({P(foldableIntoLogic):F1}%)  ← clean AOI fold, this node goes away\n");
            sb.Append($"#  REALISTIC aggressive ceiling ≈ elimCeiling ({elimCeiling:N0}) + foldableIntoLogic ({foldableIntoLogic:N0}) = {elimCeiling + foldableIntoLogic:N0} ({P(elimCeiling + foldableIntoLogic):F1}%).\n");
            sb.Append($"#  Caveat: the fold half is logic synthesis — behavioral-risk (timing/glitch); verify via ROMs/screenshots,\n");
            sb.Append($"#  not bit-exact. The elimCeiling half is also over-counted (dynamic-memory internals must stay).\n");
            sb.Append("# =======================================================");
            return sb.ToString();
        }
    }
}
