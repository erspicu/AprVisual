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

            // ── Stage A: 2-node cross-coupled latch + ratioed write pass ──
            for (int q = 0; q < n; q++)
            {
                var dq = di[q];
                if (dq is not { Hybrid: true }) continue;
                if (dq.HybridReason is not string reason || !reason.Contains("bidirectional pass between two driven nodes")) continue;

                int r = SoleNodeRef(dq.PullDown);                       // Q's pull-down must be exactly NodeRef(R)
                if (r < 0 || r >= n || di[r] == null) continue;
                if (SoleNodeRef(di[r]!.PullDown) != q) continue;        // … and R's pull-down must be exactly NodeRef(Q) — confirms the 2-node cross-couple
                if (dq.PullUp is not (PullUpKind.StaticLoad or PullUpKind.StrongVcc)) continue;   // static pull-up ⇒ a static cross-couple (not a dynamic latch — deferred)

                Expr next = Expr.Prev(q);
                bool hasWritePort = false;
                for (int i = dq.Passes.Count - 1; i >= 0; i--)          // fold the write port(s) — assumes one-hot write-enables
                {
                    var p = dq.Passes[i];
                    if (p.OwnerDrives != null) continue;                // a directed pass, not a ratioed write port
                    if (p.Other < 0 || p.Other >= n || !HasDriver(di[p.Other])) continue;
                    next = Expr.Mux(p.Cond, Expr.Node(p.Other), next);
                    hasWritePort = true;
                }
                if (!hasWritePort) continue;                            // no usable write port → leave it hybrid

                m.NextExpr[q] = next;
                m.IsSequential[q] = true;
                m.Hybrid[q] = false;
                m.RecoveredLatches++;
                // R: single-rail case → R isn't hybrid, S2.2 already gave it Not(Node(Q)); dual-rail (R also hybrid)
                //    is left alone here (a more complex pattern — deferred).
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
