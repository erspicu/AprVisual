using System;
using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S2.2: synthesise each node's next-state expression from S2.1's DriveInfo, following the NMOS
    //    group-resolution priority  GND wins  >  VCC/pull-up  >  external drive  >  hold previous  >  0:
    //      di.Hybrid           → nextExpr = null   (S2.5's bridge runs this node switch-level)
    //      else  nextExpr = pd==null ? passChain : Mux(pd, Const(false), passChain)
    //        passChain = the inbound passes (PassLink.OwnerDrives == false, i.e. Other drives this node),
    //                    folded right-to-left into nested Mux(passCond, Node(Other), …), fallback = the
    //                    pull-up value:  StaticLoad/StrongVcc → Const(true)
    //                                    Conditional         → Mux(precharge_cond, Const(true), Hold(self))   (precharge / domino)
    //                                    None                → Hold(self)                                    (pure dynamic)
    //        — assumes mutually-exclusive (one-hot) pass selects, so the fold order doesn't matter.
    //    Expr.Mux's smart ctor collapses Mux(pd, False, True) → Not(pd) etc., so the textbook
    //    inverter / NAND / NOR / AOI falls out of the general formula. A node is marked sequential here
    //    iff its nextExpr references Hold(itself) (a dynamic latch); cross-coupled SCC latches are
    //    reclassified by S2.3. Does NOT touch SCC, build the IR interpreter, or change the simulation.
    //    See MD/impl/S2/03_S2.2_NextState抽取_設計.md (incl. the Gemini review).

    internal sealed class NextStateModel
    {
        public Expr?[] NextExpr = [];      // [nodeId]; null = hybrid (switch-level) or Supply/Input (no DriveInfo)
        public bool[] IsSequential = [];   // [nodeId]; true iff NextExpr[id] references Hold(id) — S2.3 adds the SCC ones

        public static NextStateModel Build(NetlistGraph g, DriveInfo?[] di)
        {
            int n = di.Length;
            var m = new NextStateModel { NextExpr = new Expr?[n], IsSequential = new bool[n] };
            for (int v = 0; v < n; v++)
            {
                var d = di[v];
                if (d == null) continue;            // Supply / Input
                if (d.Hybrid) { m.NextExpr[v] = null; continue; }   // switch-level handles it

                Expr puFallback = d.PullUp switch
                {
                    PullUpKind.StaticLoad or PullUpKind.StrongVcc => Expr.True,
                    PullUpKind.Conditional => Expr.Mux(d.PullUpCond ?? Expr.True, Expr.True, Expr.Hold(v)),   // precharge: clock on → 1, else hold
                    _ => Expr.Hold(v),              // pure dynamic
                };

                Expr passChain = puFallback;
                for (int i = d.Passes.Count - 1; i >= 0; i--)        // assumes one-hot pass selects → fold order doesn't matter
                {
                    var p = d.Passes[i];
                    if (p.OwnerDrives != false) continue;            // only inbound (Other → v) passes drive v's value
                    passChain = Expr.Mux(p.Cond, Expr.Node(p.Other), passChain);
                }

                Expr next = d.PullDown == null ? passChain : Expr.Mux(d.PullDown, Expr.False, passChain);   // pull-down wins
                m.NextExpr[v] = next;
                m.IsSequential[v] = ReferencesHoldSelf(next, v);
            }
            return m;
        }

        private static bool ReferencesHoldSelf(Expr e, int self) => e switch
        {
            HoldExpr h => h.Id == self,
            NotExpr x  => ReferencesHoldSelf(x.Operand, self),
            AndExpr a  => ReferencesHoldSelf(a.L, self) || ReferencesHoldSelf(a.R, self),
            OrExpr o   => ReferencesHoldSelf(o.L, self) || ReferencesHoldSelf(o.R, self),
            MuxExpr x  => ReferencesHoldSelf(x.Cond, self) || ReferencesHoldSelf(x.A, self) || ReferencesHoldSelf(x.B, self),
            _ => false,
        };

        public (int comb, int seq, int hybrid, int constE, int notE, int gateE, int muxE, int holdE, int other) Stats(DriveInfo?[] di)
        {
            int comb = 0, seq = 0, hybrid = 0, c = 0, no = 0, ga = 0, mx = 0, ho = 0, ot = 0;
            for (int v = 0; v < NextExpr.Length; v++)
            {
                if (v >= di.Length || di[v] == null) continue;
                var e = NextExpr[v];
                if (e == null) { hybrid++; continue; }
                if (IsSequential[v]) seq++; else comb++;
                switch (e)
                {
                    case ConstExpr: c++; break;
                    case NotExpr: no++; break;
                    case MuxExpr: mx++; break;
                    case HoldExpr: ho++; break;
                    case AndExpr or OrExpr or NodeRefExpr: ga++; break;
                    default: ot++; break;
                }
            }
            return (comb, seq, hybrid, c, no, ga, mx, ho, ot);
        }

        public string Describe(int nodeId)
        {
            if (nodeId < 0 || nodeId >= NextExpr.Length) return $"#{nodeId} (out of range)";
            var e = NextExpr[nodeId];
            return $"{WireCore.GetNodeName(nodeId)}#{nodeId}  nextExpr = {(e == null ? "<hybrid / no-DriveInfo>" : e.Pretty())}{(nodeId < IsSequential.Length && IsSequential[nodeId] ? "  (sequential)" : "")}";
        }
    }
}
