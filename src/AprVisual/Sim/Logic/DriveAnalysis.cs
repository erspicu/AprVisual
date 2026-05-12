using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim.Logic
{
    // ── S2.1: per-node "drive structure" — given the (S1.5-lowered) netlist + S2.0's NetlistGraph
    //    classification, compute for each non-Supply/Input node:
    //      PullDown  : the boolean condition "this node has a conducting path to GND" — OR over the
    //                  simple paths node→vss (each path = AND of its transistor gate conditions),
    //                  where a path may hop through *pull-down-chain interior* nodes (Gemini's heuristic:
    //                  a stack interior node never drives any transistor gate, so we recurse through a
    //                  Pass transistor to M iff Role[M] != Bus && Nodes[M].Gates.Count == 0). >MaxPaths
    //                  simple paths ⇒ ComplexExpr + Hybrid (almost certainly a mis-classified pass net).
    //      PullUp    : StaticLoad (node.Pullups > 0, the segdef '+' depletion-load marker) / StrongVcc
    //                  (a PullUpStrong transistor) / Conditional (PullUpActive transistor(s) → OR of
    //                  their gates — precharge / push-pull) / None.   StaticLoad/StrongVcc win over Conditional.
    //      Passes    : the Pass transistors that are NOT consumed into a pull-down chain (= transmission
    //                  gates / muxes between two real signal nodes), each with a direction (OwnerDrives).
    //      Hybrid    : this node's drive structure is too tangled (bus / bidirectional pass / charge
    //                  sharing / Complex pull-down) ⇒ keep it switch-level in the IR engine.
    //    Does NOT build next-state Expr (S2.2), does NOT touch SCC (S2.3), does NOT change the simulation.
    //    See MD/impl/S2/02_S2.1_導通分析_設計.md (incl. the Gemini review).

    internal enum PullUpKind { None, StaticLoad, StrongVcc, Conditional }

    internal struct PassLink
    {
        public int Other;          // the node on the other side of the Pass transistor
        public Expr Cond;          // the Pass transistor's gate condition (NodeRef(gate), or True if gate==vcc)
        public bool? OwnerDrives;  // true = the node owning this link drives Other; false = Other drives it; null = bidirectional / unknown
    }

    internal sealed class DriveInfo
    {
        public Expr? PullDown;             // null = no pull-down path; ComplexExpr = couldn't extract
        public PullUpKind PullUp;
        public Expr? PullUpCond;           // meaningful only when PullUp == Conditional
        public List<PassLink> Passes = new();
        public bool Hybrid;
        public string? HybridReason;
    }

    internal static class DriveAnalysis
    {
        public const int MaxPaths = 32;   // >32 simple paths node→vss ⇒ Complex (likely a mis-classified pass network)
        private const int Npwr = WireCore.Npwr, Ngnd = WireCore.Ngnd;

        /// <summary>The number of node roles RefineBuses() promoted to Bus on the last Analyze() call (for --dump-drive).</summary>
        public static int LastBusesRefined;

        // BusResolver: a behavioral RAM/ROM handler drives the chip's `_d[7:0]` pins (on a read); those pins
        // are also driven by the netlist (the CPU on a write, via the chip's I/O pass transistors) → they're
        // genuine bidirectional bus nodes. Find them (without needing the handlers attached — resolve the
        // `*func<ram>` / `*func<rom>` hooks ourselves) and re-mark them Bus, so the drive analysis treats
        // them (and any pass touching them) as hybrid. Called at the start of Analyze().
        public static int RefineBuses(NetlistGraph g)
        {
            int added = 0;
            static string PrefixOf(string nm) { int d = nm.LastIndexOf('.'); return d < 0 ? "" : nm.Substring(0, d + 1); }
            void MarkDpins(string hookPattern)
            {
                var hooks = new List<int>();
                WireCore.ResolveNodes(hookPattern, hooks, quiet: true);
                foreach (int hook in hooks)
                {
                    var dpins = new List<int>();
                    WireCore.ResolveNodes(WireCore.CombinePrefix(PrefixOf(WireCore.GetNodeName(hook)), "_d[7:0]"), dpins, quiet: true);
                    foreach (int id in dpins)
                        if (id >= 0 && id < g.Role.Length && g.Role[id] == NodeRole.Internal) { g.Role[id] = NodeRole.Bus; added++; }
                }
            }
            MarkDpins("*func<ram>");
            MarkDpins("*func<rom>");
            return added;
        }

        /// <summary>Analyze the netlist (after NetlistGraph.BuildFrom). Returns DriveInfo per node id; null for Supply/Input.</summary>
        public static DriveInfo?[] Analyze(NetlistGraph g)
        {
            LastBusesRefined = RefineBuses(g);   // promote handler-driven `_d[]` pins to Bus before we use g.Role

            var nodes = WireCore.Nodes;
            var trans = WireCore.Transistors;
            int n = nodes.Count;
            var di = new DriveInfo?[n];

            // hasAnyPullUp[m] = m has a static load (pullups>0) or a pull-up transistor of any kind (it can be
            // *driven high* — used below to decide which side of a pass to a chain-interior node "owns" it).
            var hasAnyPullUp = new bool[n];
            for (int m = Ngnd + 1; m < n; m++)
            {
                var nm = nodes[m];
                if (nm == null) continue;
                if (nm.Pullups > 0) { hasAnyPullUp[m] = true; continue; }
                foreach (int tid in nm.C1c2s)
                    if (g.Kind[tid] is TransistorKind.PullUpStrong or TransistorKind.PullUpActive or TransistorKind.PullUpLoad) { hasAnyPullUp[m] = true; break; }
            }

            // A "stack interior" node = a transparent series-junction *inside a pull-down chain*: it never drives
            // any transistor gate (Gemini's layout heuristic — robust against precharge/dynamic), isn't a bus,
            // isn't a supply, has no pull-up of any kind, AND has at least one direct channel to vss (a PullDown
            // transistor) — i.e. it genuinely sits on a path to ground. The "direct PullDown" condition is the fix
            // for the bug where a *dynamic mux/bus* node (Gates.Count==0, no pull-up, but all its channel
            // transistors are Passes to control-gated signal sources — e.g. cpu.#aluresult0, the ALU result bus)
            // was wrongly treated as a transparent stack node ⇒ its mux passes got skipped ⇒ nextExpr = Hold(self).
            // Such a junction's value still gets a write port back from any *pulled-up* pass-neighbor o (o can pull
            // it up/down through the pass — see pass 2), so e.g. NAND/NOR/AOI stack junctions and cpu node 8867 ↔
            // 9484 get the right Mux(passgate, Node(o), …) instead of a bare clear-and-hold. Precomputed once.
            var chainInterior = new bool[n];
            for (int m = Ngnd + 1; m < n; m++)
            {
                var nm = nodes[m];
                if (nm == null || nm.Gates.Count != 0 || nm.Pullups != 0) continue;
                if (m < g.Role.Length && g.Role[m] == NodeRole.Bus) continue;
                bool hasPullUpTransistor = false, hasPullDownTransistor = false;
                foreach (int tid in nm.C1c2s)
                {
                    if (g.Kind[tid] is TransistorKind.PullUpStrong or TransistorKind.PullUpActive) hasPullUpTransistor = true;
                    if (g.Kind[tid] == TransistorKind.PullDown) hasPullDownTransistor = true;
                }
                if (hasPullUpTransistor || !hasPullDownTransistor) continue;
                chainInterior[m] = true;
            }
            bool IsChainInterior(int m) => m >= 0 && m < n && chainInterior[m];

            // A "clearable dynamic node": Pullups == 0, has ≥1 direct channel to vss (a PullDown — a conditional
            // *clear*), and *every* other channel transistor is a Pass (a transmission-gate write port). Such a
            // node is a dynamic latch (holds on parasitic cap) whose only active drive is the clear(s) — so a Pass
            // to it from a *non*-clearable node is always a write *into* the latch, regardless of which side ranks
            // higher on the strength tier. Without this, the latch's conditional pull-down makes it Tier 2 and the
            // tier rule wrongly sends the load pass outbound, dropping the load from nextExpr (e.g. cpu.alua[7:0],
            // the ALU A input — clear=0ADD, load=SBADD from cpu.sb[7:0]). A node with a *static* pull-up (pullups>0)
            // or a pull-up *transistor* of any kind is excluded — it's a bus / driven node, not a pure latch.
            var clearableDynamic = new bool[n];
            for (int m = Ngnd + 1; m < n; m++)
            {
                var nm = nodes[m];
                if (nm == null || nm.Pullups != 0) continue;
                if (m < g.Role.Length && g.Role[m] != NodeRole.Internal) continue;   // not a bus / supply / input
                bool hasPd = false, ok = true;
                foreach (int tid in nm.C1c2s)
                    switch (g.Kind[tid])
                    {
                        case TransistorKind.PullDown: hasPd = true; break;
                        case TransistorKind.Pass: case TransistorKind.Dead: break;
                        default: ok = false; break;            // PullUpStrong / PullUpActive / PullUpLoad ⇒ a driven node, not a pure latch
                    }
                clearableDynamic[m] = hasPd && ok;
            }
            bool IsClearableDynamic(int m) => m >= 0 && m < n && clearableDynamic[m];

            Expr GateCond(int gate) => gate == Npwr ? Expr.True : Expr.Node(gate);

            // ── pass 1: PullDown (path enumeration) + PullUp ──
            for (int v = 0; v < n; v++)
            {
                var node = nodes[v];
                if (node == null) continue;
                var role = v < g.Role.Length ? g.Role[v] : NodeRole.Internal;
                if (role == NodeRole.Supply || role == NodeRole.Input) continue;
                var info = new DriveInfo();
                di[v] = info;
                if (role == NodeRole.Bus) { info.Hybrid = true; info.HybridReason = "bidirectional bus node"; }

                // --- PullDown: OR over simple paths v→vss; each path = AND of the gate conditions on it ---
                var paths = new List<Expr>();
                bool tooMany = false;
                var onPath = new HashSet<int> { v };
                void Dfs(int cur, Expr acc)
                {
                    if (tooMany) return;
                    foreach (int tid in nodes[cur]!.C1c2s)
                    {
                        if (g.Kind[tid] == TransistorKind.Dead) continue;
                        var t = trans[tid];
                        if (g.Kind[tid] == TransistorKind.PullDown)
                        {
                            paths.Add(Expr.And(acc, GateCond(t.Gate)));
                            if (paths.Count > MaxPaths) { tooMany = true; return; }
                            continue;
                        }
                        if (g.Kind[tid] != TransistorKind.Pass) continue;       // PullUp* transistors aren't part of the pull-down DFS
                        int other = t.C1 == cur ? t.C2 : t.C1;
                        if (other == cur || !IsChainInterior(other) || onPath.Contains(other)) continue;
                        onPath.Add(other);
                        Dfs(other, Expr.And(acc, GateCond(t.Gate)));
                        onPath.Remove(other);
                        if (tooMany) return;
                    }
                }
                Dfs(v, Expr.True);
                if (tooMany)
                {
                    info.PullDown = Expr.Complex;
                    if (!info.Hybrid) { info.Hybrid = true; info.HybridReason = $"pull-down net not series-parallel and >{MaxPaths} paths (likely a mis-classified pass network)"; }
                }
                else info.PullDown = paths.Count == 0 ? null : Expr.OrAll(paths);

                // --- PullUp ---
                bool hasStaticLoad = node.Pullups > 0, hasStrongVcc = false;
                var activeGates = new List<Expr>();
                foreach (int tid in node.C1c2s)
                    switch (g.Kind[tid])
                    {
                        case TransistorKind.PullUpStrong: hasStrongVcc = true; break;
                        case TransistorKind.PullUpLoad:   hasStaticLoad = true; break;   // diode-connected depletion load on this node
                        case TransistorKind.PullUpActive: activeGates.Add(GateCond(trans[tid].Gate)); break;
                    }
                if      (hasStaticLoad)        info.PullUp = PullUpKind.StaticLoad;
                else if (hasStrongVcc)         info.PullUp = PullUpKind.StrongVcc;
                else if (activeGates.Count > 0) { info.PullUp = PullUpKind.Conditional; info.PullUpCond = Expr.OrAll(activeGates); }
                else                           info.PullUp = PullUpKind.None;
            }

            // ── pass 2: Passes (each Pass transistor processed once, at v == C1; recorded on both ends) ──
            // Drive-strength tiers for the direction heuristic: 2 = "strong" (a pull-down conduction
            // path = a real conditional 0-driver, or a hard VCC tie) — NMOS passes a strong 0/1 with no
            // loss, so a strong driver overpowers the other side; 1 = "weak" (only a static depletion
            // load / a precharge pull-up — loses to a strong path through the pass); 0 = none (pure
            // dynamic, holds on parasitic cap). A pass is *directed* when the two sides differ in tier
            // (the stronger drives the weaker); same tier ⇒ a contention / charge-share ⇒ hybrid.
            int Tier(DriveInfo d) =>
                (d.PullDown != null && !d.PullDown.IsComplex) || d.PullUp == PullUpKind.StrongVcc ? 2 :
                d.PullUp is PullUpKind.StaticLoad or PullUpKind.Conditional ? 1 : 0;
            void MarkHybrid(DriveInfo d, string reason) { if (!d.Hybrid) { d.Hybrid = true; d.HybridReason = reason; } }
            for (int tid = 0; tid < trans.Count; tid++)
            {
                if (g.Kind[tid] != TransistorKind.Pass) continue;
                var t = trans[tid];
                int a = t.C1, b = t.C2;
                if (a == b) continue;
                bool aInt = IsChainInterior(a), bInt = IsChainInterior(b);
                if (aInt || bInt)
                {
                    // A pass with a chain-interior end is "transparent" — counted in the parent's pull-down DFS,
                    // not a write port — *except* that a chain-interior junction m sitting next to a pulled-up
                    // node o gets an inbound write port o→m: when this pass conducts, m and o are one group and o
                    // (which can drive high) carries the value, so m's nextExpr should follow Node(o). (o needs
                    // no link back: o.PullDown already includes the path o—pass—m—…—vss from the DFS, so Node(o)
                    // is exactly the settled group value.) Fixes NAND/NOR/AOI stack junctions floating high while
                    // hold==0, the cart-glue latch node 14200 ↔ 13045, etc.
                    g.PassDirection[tid] = PassDir.Unknown;
                    var c0 = GateCond(t.Gate);
                    if (aInt && !bInt && b < n && hasAnyPullUp[b] && di[a] is { } dia) { dia.Passes.Add(new PassLink { Other = b, Cond = c0, OwnerDrives = false }); g.PassDirection[tid] = PassDir.BtoA; }
                    if (bInt && !aInt && a < n && hasAnyPullUp[a] && di[b] is { } dib) { dib.Passes.Add(new PassLink { Other = a, Cond = c0, OwnerDrives = false }); g.PassDirection[tid] = PassDir.AtoB; }
                    continue;
                }
                var da = a < di.Length ? di[a] : null;
                var db = b < di.Length ? di[b] : null;
                if (da == null || db == null) { g.PassDirection[tid] = PassDir.Unknown; continue; }   // (shouldn't happen — Pass ends are signal nodes)

                bool aBus = a < g.Role.Length && g.Role[a] == NodeRole.Bus, bBus = b < g.Role.Length && g.Role[b] == NodeRole.Bus;
                bool aCl = IsClearableDynamic(a), bCl = IsClearableDynamic(b);
                int ta = Tier(da), tb = Tier(db);
                bool? aDrivesB; string? hyReason = null;
                // The clearable-dynamic rule only applies when the *other* side isn't itself strongly driven
                // (tier 2): a Pass between a clearable latch and another tier-2 node is a genuine merge /
                // contention (e.g. cpu.##alucout ↔ cpu.short-circuit-branch-add) — fall to the tier rule, which
                // marks it hybrid. The latch's "load port" sources are buses / precharged nodes — tier ≤ 1.
                if (aBus || bBus)              { aDrivesB = null; hyReason = "bidirectional pass touching a bus node"; }
                else if (aCl && !bCl && tb < 2){ aDrivesB = false; }   // a is a clearable dynamic latch ← this pass is a write into it from b
                else if (bCl && !aCl && ta < 2){ aDrivesB = true;  }
                else if (ta > tb)             { aDrivesB = true;  }    // a's stronger driver overpowers b through the pass
                else if (tb > ta)             { aDrivesB = false; }
                else if (ta == 2)             { aDrivesB = null; hyReason = "bidirectional pass between two strongly-driven nodes (latch / contention)"; }
                else if (ta == 1)             { aDrivesB = null; hyReason = "pass between two weakly-pulled-up nodes"; }
                else                          { aDrivesB = null; hyReason = "pass between two floating dynamic nodes (charge sharing)"; }

                g.PassDirection[tid] = aDrivesB == true ? PassDir.AtoB : aDrivesB == false ? PassDir.BtoA : PassDir.Bidirectional;
                Expr cond = GateCond(t.Gate);
                da.Passes.Add(new PassLink { Other = b, Cond = cond, OwnerDrives = aDrivesB });
                db.Passes.Add(new PassLink { Other = a, Cond = cond, OwnerDrives = aDrivesB.HasValue ? !aDrivesB.Value : (bool?)null });
                if (hyReason != null) { MarkHybrid(da, hyReason); MarkHybrid(db, hyReason); }
            }

            // ── pass 3: a precharge/domino node (PullUpKind.Conditional — clocked pull-up, no static load) that is
            //    *also* pass-connected to other signal nodes is a multi-driver structure (clear / set / precharge /
            //    the eval network / several pass writes, all conditional) whose resolution priority is timing-
            //    sensitive — the pass-direction heuristic can't pick the right priority chain (e.g. the 2C02
            //    sprite/OAM `*_int` nodes). Hand it to S1's switch-level group resolution (the hybrid fallback). ──
            for (int v = 0; v < n; v++)
                if (di[v] is { PullUp: PullUpKind.Conditional, Hybrid: false } d2 && d2.Passes.Count > 0)
                { d2.Hybrid = true; d2.HybridReason = "precharge/domino node with pass connections (multi-driver — switch-level handles the priority)"; }

            // ── pass 4: a pure dynamic node (no own pull-down, no pull-up) with ≥3 inbound write ports is a
            //    multi-port bus bridge (e.g. cpu.dl[7:0] bridging the data latch to the ADH / ADL / IDB buses +
            //    the IDL hold). A priority Mux(cond_a, A, Mux(cond_b, B, …)) can't model "≥2 ports conduct ⇒ the
            //    merged group resolves by drive strength, not by Mux order" — hand it to S1's switch-level. ──
            for (int v = 0; v < n; v++)
                if (di[v] is { Hybrid: false, PullDown: null, PullUp: PullUpKind.None } d3 && d3.Passes.Count(p => p.OwnerDrives == false) >= 3)
                { d3.Hybrid = true; d3.HybridReason = "dynamic multi-port bus bridge (≥3 write ports — switch-level resolves the multi-driver)"; }

            return di;
        }

        /// <summary>Coverage / stats for --dump-drive.</summary>
        public static (int total, int complexPd, int hybrid, int pdNull, int pdSome, int[] pullUpByKind, int passLinks, int bidirPass) Stats(DriveInfo?[] di, NetlistGraph g)
        {
            int total = 0, complexPd = 0, hybrid = 0, pdNull = 0, pdSome = 0, passLinks = 0;
            var pu = new int[Enum.GetValues<PullUpKind>().Length];
            foreach (var d in di)
            {
                if (d == null) continue;
                total++;
                if (d.PullDown is ComplexExpr) complexPd++;
                else if (d.PullDown == null) pdNull++;
                else pdSome++;
                if (d.Hybrid) hybrid++;
                pu[(int)d.PullUp]++;
                passLinks += d.Passes.Count;
            }
            int bidir = g.PassDirection.Count(p => p == PassDir.Bidirectional);
            return (total, complexPd, hybrid, pdNull, pdSome, pu, passLinks / 2, bidir);   // passLinks/2 — each link counted on both ends
        }

        public static string Describe(DriveInfo? d, int nodeId)
        {
            if (d == null) return $"{WireCore.GetNodeName(nodeId)}#{nodeId}  (Supply/Input — no DriveInfo)";
            string pd = d.PullDown is ComplexExpr ? "<complex>" : d.PullDown == null ? "(none)" : d.PullDown.Pretty();
            string pu = d.PullUp == PullUpKind.Conditional ? $"Conditional[{d.PullUpCond?.Pretty()}]" : d.PullUp.ToString();
            string passes = d.Passes.Count == 0 ? "" : "  passes=[" + string.Join(", ", d.Passes.Select(p =>
                $"{(p.OwnerDrives == true ? "→" : p.OwnerDrives == false ? "←" : "↔")}{WireCore.GetNodeName(p.Other)}#{p.Other} when {p.Cond.Pretty()}")) + "]";
            return $"{WireCore.GetNodeName(nodeId)}#{nodeId}  pullDown={pd}  pullUp={pu}{passes}{(d.Hybrid ? $"  HYBRID({d.HybridReason})" : "")}";
        }
    }
}
