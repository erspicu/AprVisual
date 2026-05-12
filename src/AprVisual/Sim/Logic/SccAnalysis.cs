using System;
using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S2.3: recover the cross-coupled register-bit latches from "hybrid" → sequential.
    //    Stage A (this cut — pattern-based static recovery, no graph / no Tarjan needed):
    //      a hybrid node Q whose HybridReason is "bidirectional pass between two driven nodes", whose
    //      pull-down is *exactly* NodeRef(R), where R's pull-down is *exactly* NodeRef(Q) (a 2-node
    //      cross-coupled inverter pair), and Q has a static pull-up (the depletion load) and ≥1
    //      bidirectional pass (PassLink.OwnerDrives == null) to a node `data` that actually has a driver
    //      — that pass is a ratioed write port gated by `we`. Synthesise
    //        nextExpr[Q] = Mux(we1, Node(data1), Mux(we2, Node(data2), … Prev(Q)))   (ratioed write —
    //      when a write-enable is on, data overpowers the weak feedback; else the pair holds its value).
    //      Q becomes sequential and leaves the hybrid set; R's nextExpr ( = Not(Node(Q)) ) is already
    //      what S2.2 gave it (R isn't hybrid in the simple single-rail case). Stages B–E (conditional
    //      dependency graph + Tarjan + cross-phase-cycle break + unsolved-SCC → hybrid) are deferred
    //      unless Stage A alone doesn't get the IR coverage past ~85%. Does NOT build the IR interpreter
    //      and does NOT change the simulation. See MD/impl/S2/04_S2.3_SCC分析_設計.md (incl. the Gemini review).

    internal sealed class SccModel
    {
        public Expr?[] NextExpr = [];        // [nodeId]; recovered/rewritten vs the S2.2 model; null = still hybrid
        public bool[] IsSequential = [];
        public bool[] Hybrid = [];            // updated hybrid set (the recovered cross-coupled latches removed)
        public int RecoveredLatches;          // # of 2-node cross-coupled SRAM-cell latches recovered (Stage A)
        public int RecoveredLatches2;         // # of cross-coupled-NOR-with-load latches rewritten sequential (Stage A2 — breaks the q↔/q dependency cycle)

        public static SccModel Build(NetlistGraph g, DriveInfo?[] di, NextStateModel s2)
        {
            int n = di.Length;
            var m = new SccModel
            {
                NextExpr = (Expr?[])s2.NextExpr.Clone(),
                IsSequential = (bool[])s2.IsSequential.Clone(),
                Hybrid = new bool[n],
            };
            for (int v = 0; v < n; v++) m.Hybrid[v] = di[v] is { Hybrid: true };

            static int SoleNodeRef(Expr? e) => e is NodeRefExpr nr ? nr.Id : -1;   // the id if e is exactly NodeRef(id), else -1
            static bool HasDriver(DriveInfo? d) =>
                d != null && ((d.PullDown != null && !d.PullDown.IsComplex) || d.PullUp != PullUpKind.None || d.Passes.Exists(p => p.OwnerDrives == false));

            // ── Stage A: 2-node cross-coupled latch + write port (structure-based — independent of how the
            //    pass-direction heuristic classified the write pass). A pair {a, b}: di[a].PullDown is exactly
            //    NodeRef(b) and di[b].PullDown is exactly NodeRef(a) (a cross-coupled inverter pair). The
            //    "state node" S = whichever of {a,b} has a Pass to a node ≠ partner (a write port) — if both,
            //    pick a; if neither, S = a (a non-writable, power-up-held latch). Synthesise
            //      nextExpr[S] = Mux(we1, Node(data1), Mux(…, Prev(S)))   (ratioed write — data overpowers the
            //                                                              weak feedback; else the pair holds)
            //      nextExpr[C(omplement)] = Not(Node(S))
            //    Both leave the hybrid set. For each write-source `data` whose pass to S the direction
            //    heuristic mis-pointed as "S drives data" (so `data` currently follows S) — flip that link in
            //    di[data].Passes (data drives S) and re-synth data so it no longer follows the latch.
            var dirtyData = new HashSet<int>();
            for (int a = 0; a < n; a++)
            {
                var da = di[a];
                if (da == null) continue;
                int b = SoleNodeRef(da.PullDown);
                if (b <= a || b >= n || di[b] == null) continue;        // process each pair once, with a < b
                if (SoleNodeRef(di[b]!.PullDown) != a) continue;        // confirm the 2-node cross-couple
                if (da.PullUp is not (PullUpKind.StaticLoad or PullUpKind.StrongVcc)) continue;   // a static cross-couple (dynamic-latch variant deferred)

                // each side's write ports = its Pass transistors to a node ≠ the partner
                static List<PassLink> WritePorts(DriveInfo d, int partner) => d.Passes.FindAll(p => p.Other != partner);
                var wa = WritePorts(da, b);
                var wb = WritePorts(di[b]!, a);
                int sNode, cNode; List<PassLink> wports;
                if (wa.Count > 0) { sNode = a; cNode = b; wports = wa; }       // a is the state node (has a write port)
                else if (wb.Count > 0) { sNode = b; cNode = a; wports = wb; }  // b is
                else { sNode = a; cNode = b; wports = wa; }                    // neither — non-writable; S = a, nextExpr = Prev(a)

                Expr next = Expr.Prev(sNode);
                foreach (var p in wports)                                       // fold right-to-left (assumes one-hot write-enables)
                {
                    if (p.Other < 0 || p.Other >= n || !HasDriver(di[p.Other])) continue;
                    next = Expr.Mux(p.Cond, Expr.Node(p.Other), next);
                    if (di[p.Other] is { Hybrid: false }) dirtyData.Add(p.Other);   // its DriveInfo.Passes may need the S-direction flip + re-synth
                }
                m.NextExpr[sNode] = next; m.IsSequential[sNode] = true; m.Hybrid[sNode] = false;
                m.NextExpr[cNode] = Expr.Not(Expr.Node(sNode)); m.IsSequential[cNode] = false; m.Hybrid[cNode] = false;
                m.RecoveredLatches++;

                // fix the write sources: in their DriveInfo.Passes, the link to the state node should be
                // "data drives S" (OwnerDrives = true from data's side) — the direction heuristic may have
                // pointed it the other way; flip it so the re-synth below doesn't make `data` follow the latch.
                foreach (var p in wports)
                    if (p.Other >= 0 && p.Other < n && di[p.Other] is { } dd)
                        for (int i = 0; i < dd.Passes.Count; i++)
                            if (dd.Passes[i].Other == sNode) { var pl = dd.Passes[i]; pl.OwnerDrives = true; dd.Passes[i] = pl; }
            }
            foreach (int data in dirtyData)
            {
                if (m.Hybrid[data]) continue;
                m.NextExpr[data] = NextStateModel.SynthOne(di, data);
                m.IsSequential[data] = m.NextExpr[data] is { } e && NextStateModel.ReferencesHoldSelf(e, data);
            }

            // ── Stage A2: cross-coupled-NOR-with-load latch (e.g. the board's 74LS373 bits: q = !(/q | load_q),
            //    /q = !(q | load_/q) — the load path is *mixed into* the pull-down, so Stage A's "PullDown is
            //    exactly NodeRef(partner)" miss it; instead one of the pull-down's OR-terms is NodeRef(partner)).
            //    Such a pair is a combinational q↔/q cycle in the current-value dependency graph (q's nextExpr =
            //    !(Node(/q) | rest_q), /q's = !(Node(q) | rest_/q)) — driving mode can't topo-sort it. Rewrite it
            //    sequential, symmetrically (so a `rest`-contention ⇒ both 0, matching S1's GND-wins):
            //      nextExpr[a] = Mux(rest_a, 0, Mux(rest_b, 1, Prev(a))),  nextExpr[b] = Mux(rest_b, 0, Mux(rest_a, 1, Prev(b)))
            //    where rest_x = x's pull-down with the NodeRef(partner) OR-term removed. Bail if a `rest` itself
            //    references a or b (would re-introduce a cycle) — leave that pair for IrEngine's Stage D.
            static void CollectOrTerms(Expr? e, List<Expr> terms) { if (e is OrExpr o) { CollectOrTerms(o.L, terms); CollectOrTerms(o.R, terms); } else if (e != null) terms.Add(e); }
            static bool ReferencesNodeRef(Expr e, int id) => e switch
            {
                NodeRefExpr nr => nr.Id == id,
                NotExpr x => ReferencesNodeRef(x.Operand, id),
                AndExpr a => ReferencesNodeRef(a.L, id) || ReferencesNodeRef(a.R, id),
                OrExpr o => ReferencesNodeRef(o.L, id) || ReferencesNodeRef(o.R, id),
                MuxExpr mx => ReferencesNodeRef(mx.Cond, id) || ReferencesNodeRef(mx.A, id) || ReferencesNodeRef(mx.B, id),
                _ => false,
            };
            for (int a = 0; a < n; a++)
            {
                if (di[a] is not { } da || da.PullDown is null or ComplexExpr) continue;
                if (da.PullUp is not (PullUpKind.StaticLoad or PullUpKind.StrongVcc)) continue;
                var aTerms = new List<Expr>(); CollectOrTerms(da.PullDown, aTerms);
                int b = -1;                                                    // partner = the node b such that NodeRef(b) is one of a's pull-down OR-terms and b's pull-down has NodeRef(a) symmetrically
                foreach (var t in aTerms)
                    if (t is NodeRefExpr nr && nr.Id > a && nr.Id < n && di[nr.Id] is { PullUp: PullUpKind.StaticLoad or PullUpKind.StrongVcc } db2 && db2.PullDown is not (null or ComplexExpr))
                    {
                        var bT = new List<Expr>(); CollectOrTerms(db2.PullDown, bT);
                        if (bT.Exists(x => x is NodeRefExpr bnr && bnr.Id == a)) { b = nr.Id; break; }
                    }
                if (b < 0) continue;
                // process only pairs that are still a live q↔/q cycle in m (not already handled by Stage A, not hybrid).
                if (m.NextExpr[a] is not { } na || m.NextExpr[b] is not { } nb || !ReferencesNodeRef(na, b) || !ReferencesNodeRef(nb, a)) continue;
                var restAterms = aTerms.FindAll(x => !(x is NodeRefExpr nr2 && nr2.Id == b));
                var restBterms = new List<Expr>(); CollectOrTerms(di[b]!.PullDown, restBterms); restBterms = restBterms.FindAll(x => !(x is NodeRefExpr nr3 && nr3.Id == a));
                Expr restA = restAterms.Count == 0 ? Expr.False : Expr.OrAll(restAterms);
                Expr restB = restBterms.Count == 0 ? Expr.False : Expr.OrAll(restBterms);
                if (ReferencesNodeRef(restA, a) || ReferencesNodeRef(restA, b) || ReferencesNodeRef(restB, a) || ReferencesNodeRef(restB, b)) continue;   // would re-introduce a cycle — leave for Stage D
                m.NextExpr[a] = Expr.Mux(restA, Expr.False, Expr.Mux(restB, Expr.True, Expr.Prev(a))); m.IsSequential[a] = true; m.Hybrid[a] = false;
                m.NextExpr[b] = Expr.Mux(restB, Expr.False, Expr.Mux(restA, Expr.True, Expr.Prev(b))); m.IsSequential[b] = true; m.Hybrid[b] = false;
                m.RecoveredLatches2++;
            }
            return m;
        }

        public (int comb, int seq, int hybrid, int total) Stats(DriveInfo?[] di)
        {
            int comb = 0, seq = 0, hyb = 0, total = 0;
            for (int v = 0; v < NextExpr.Length; v++)
            {
                if (v >= di.Length || di[v] == null) continue;
                total++;
                if (Hybrid[v] || NextExpr[v] == null) hyb++;
                else if (IsSequential[v]) seq++;
                else comb++;
            }
            return (comb, seq, hyb, total);
        }

        public string Describe(int nodeId)
        {
            if (nodeId < 0 || nodeId >= NextExpr.Length) return $"#{nodeId} (out of range)";
            bool hy = nodeId < Hybrid.Length && Hybrid[nodeId];
            var e = NextExpr[nodeId];
            return $"{WireCore.GetNodeName(nodeId)}#{nodeId}  nextExpr = {(hy || e == null ? "<hybrid>" : e.Pretty())}{(!hy && nodeId < IsSequential.Length && IsSequential[nodeId] ? "  (sequential)" : "")}";
        }
    }
}
