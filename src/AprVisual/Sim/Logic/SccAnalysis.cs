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
        public int RecoveredLatches;          // # of cross-coupled latches recovered (hybrid → sequential)

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
