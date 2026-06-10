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

            // [Estimator 1 — sequential/memory storage cells] a dynamic stored bit = no pull-up + no supply
            // (GndCount==PwrCount==0, so it can't be pulled to a rail) + written via a pass channel + READ by
            // gating ≥1 transistor + not handler/FC. It holds charge on its gate capacitance = a latch/register/
            // RAM bit. Collapsing these into array-backed state primitives is the big lever (Gemini).
            int storageBit = 0;
            // also bucket the no-pull-up nodes for the breakdown:
            int noPullUpPureWaypoint = 0;   // no-pull-up ∩ gates-nothing ∩ has-pass (pass fabric internal)
            int noPullUpSupplyDriven = 0;   // no-pull-up but has a gnd/pwr channel (driven, not a held bit)
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

                // [Estimator 1] bucket the no-pull-up mass into: held STORAGE BIT vs pure pass WAYPOINT vs
                // SUPPLY-DRIVEN. A dynamic stored bit = no rail of its own (no pull-up, no gnd/pwr channel) +
                // written through a pass channel + READ (gates ≥1 transistor) + not handler/FC. It holds its
                // value as charge on gate-capacitance ⇒ a latch / register / RAM bit. Collapsing each such
                // cell into one array-backed event-driven state primitive is the big lever (Gemini).
                if (!hasPullUp)
                {
                    if (hasSupply) noPullUpSupplyDriven++;
                    else if (!gatesAny && hasPass) noPullUpPureWaypoint++;
                    if (!hasSupply && hasPass && gatesAny && !special && !isDriven) storageBit++;
                }

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

            // ── [Estimator 2] bus / decoder fabric — channel-connected components over c1c2 ──────────────
            // Union-find over c1c2 channels = the maximal conducting groups (all channels ON). The biggest
            // CCCs are the data/address buses + register-file read/write pass-networks + OAM eval bus — where
            // ONE toggle can fire a BFS touching up-to-component-size nodes. Abstracting a CCC as a structured
            // mux / tristate-bus primitive eliminates its "pure fabric" internal nodes = nodes that gate NOTHING
            // and have NO pull-up (pure pass source/drain — they only route, never compute or hold a read bit).
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
            var compSize = new Dictionary<int, int>();
            var compFabric = new Dictionary<int, int>();
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                bool hasPass = ns->Inline != 0 ? (ns->C1c2Count != 0) : (ns->TlistC1c2s != 0);
                if (!hasPass) continue;   // only channel-graph members form a CCC; pure logic/supply nodes are trivial singletons
                int r = FcuFind(parent, nn);
                compSize.TryGetValue(r, out int c); compSize[r] = c + 1;
                bool fabric = (ns->Flags & NodeFlags.PullUp) == 0 && NodeTlistGates[nn] == 0
                              && (ns->Flags & fcCb) == 0 && !driven.Contains(nn);
                if (fabric) { compFabric.TryGetValue(r, out int f); compFabric[r] = f + 1; }
            }
            int cccCount = compSize.Count, biggestCcc = 0, biggestCccFabric = 0, biggestRoot = -1;
            int busCccCount = 0, busFabricTotal = 0, busNodeTotal = 0;   // aggregate over "large" components (≥32 nodes)
            const int BusThreshold = 32;
            foreach (var kv in compSize)
            {
                compFabric.TryGetValue(kv.Key, out int fab);
                if (kv.Value > biggestCcc) { biggestCcc = kv.Value; biggestCccFabric = fab; biggestRoot = kv.Key; }
                if (kv.Value >= BusThreshold) { busCccCount++; busNodeTotal += kv.Value; busFabricTotal += fab; }
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
            sb.Append("# =======================================================\n");

            // ── Estimator 1: sequential / memory storage cells ──────────────────────────────────────────
            sb.Append("# ----- ESTIMATOR 1: sequential / memory storage cells (the big lever) -----\n");
            sb.Append($"#  no-pull-up nodes total:        {noPullUp:N0}  ({P(noPullUp):F1}%)\n");
            sb.Append($"#    ↳ STORAGE BITS (no rail + written-via-pass + READ = latch/register/RAM bit):\n");
            sb.Append($"#         {storageBit:N0}  ({P(storageBit):F1}%)  ← collapse each into 1 array-backed clocked-state primitive\n");
            sb.Append($"#    ↳ pure pass WAYPOINTS (no rail + gates-nothing + has-pass = bus/route internal): {noPullUpPureWaypoint:N0} ({P(noPullUpPureWaypoint):F1}%)\n");
            sb.Append($"#    ↳ supply-driven (no pull-up but has a gnd/pwr channel — driven, not held):       {noPullUpSupplyDriven:N0} ({P(noPullUpSupplyDriven):F1}%)\n");
            sb.Append($"#  → memory-cell node-reduction ceiling ≈ storage bits + waypoints = {storageBit + noPullUpPureWaypoint:N0} ({P(storageBit + noPullUpPureWaypoint):F1}%)\n");
            sb.Append($"#    (Gemini estimate: ~30-40% via OAM/palette-RAM/CPU-register collapse. Event-driven, keeps sparsity\n");
            sb.Append($"#     — unlike the failed AOT macro-block; behavioral, not bit-exact.)\n");
            sb.Append("# ----------------------------------------------------------------------------\n");

            // ── Estimator 2: bus / decoder fabric ───────────────────────────────────────────────────────
            sb.Append("# ----- ESTIMATOR 2: bus / decoder fabric (channel-connected components) -----\n");
            sb.Append($"#  channel components (have a c1c2 pass channel): {cccCount:N0}\n");
            sb.Append($"#  BIGGEST component: {biggestCcc:N0} nodes  ({P(biggestCcc):F1}% of live)  — pure fabric in it: {biggestCccFabric:N0}\n");
            sb.Append($"#    (each toggle inside it can fire a BFS touching up to ~{biggestCcc:N0} nodes — the data/addr bus / reg-file / OAM eval net)\n");
            sb.Append($"#  large components (≥{BusThreshold} nodes): {busCccCount:N0}  spanning {busNodeTotal:N0} nodes ({P(busNodeTotal):F1}%)\n");
            sb.Append($"#  → bus-fabric node-reduction ceiling (extractable pure-fabric in large CCCs) ≈ {busFabricTotal:N0} ({P(busFabricTotal):F1}%)\n");
            sb.Append($"#    (Gemini estimate: ~7% nodes, but the real win is killing the giant-BFS time-complexity, not the count.)\n");
            sb.Append("# ----------------------------------------------------------------------------\n");

            // ── Estimator 3 pointer (run-time) ──────────────────────────────────────────────────────────
            sb.Append("# ----- ESTIMATOR 3: clock-phase wasted events (RUN-TIME — see DEBUG bench) -----\n");
            sb.Append("#  Node-count is a static proxy; the REAL cost is EVENTS. Run the DEBUG benchmark and read the\n");
            sb.Append("#  [phase-waste] line: it reports the share of RecalcNode pops that land on a node isolated this\n");
            sb.Append("#  phase (all its pass-channels gated OFF) and reading nothing ⇒ the event went nowhere = the\n");
            sb.Append("#  clock-phase-gating ceiling; plus [mem-event-share] = pops landing on the Estimator-1/2 mass.\n");
            sb.Append("#    dotnet run -c Debug --project src/AprVisual.S2 -- --benchmark <rom> --bench-hc 200000 --extra-ram --system-def-dir <dir>\n");
            sb.Append("# ===========================================================================");
            return sb.ToString();
        }
    }
}
