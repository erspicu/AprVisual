using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S4 γ.4 — hybrid multi-driver bus lowering ────────────────────────────────────────────────────────
    //
    // The ~1379 "hybrid bus" nodes (NextExpr == null — DriveAnalysis couldn't extract a clean function because
    // they have multiple pass-transistor drivers / bidirectional bus links: BD0-7, io_db, exp_in, …) get a
    // *pseudo-NextExpr* modelling the NMOS wired resolution (GND wins; per Gemini's S4.0 review):
    //
    //   has_pd = OR( di[v].PullDown,  for each pass port i: Cond_i & !Node(Other_i) )    // anyone pulls it to 0
    //   has_pu = OR( <di[v].PullUp>,  for each pass port i: Cond_i &  Node(Other_i) )    // anyone pulls it to 1
    //   NextExpr[v] = Mux(has_pd, 0, Mux(has_pu, 1, Hold(v)))   // = !has_pd & (has_pu | Hold(v))  via the smart ctor
    //
    // where <di[v].PullUp> is True for StaticLoad/StrongVcc, di[v].PullUpCond for Conditional, nothing for None.
    // If the pull-down, the conditional pull-up, or any pass gate is Complex → bail (leave NextExpr[v] null → S1).
    //
    // Pass ports whose Other is itself a lowered bus turn into NodeRef edges; cycles among buses (io_db ↔ palette …)
    // end up in residual SCCs, which γ.1 (size-2 solver) / S4.3 (fixed-K micro-block) handle. Run after
    // SccModel.Build (so the already-modelled nodes have their NextExpr) and before NodeAlias.Apply / BuildEvalOrder.
    internal static class BusLowering
    {
        /// <summary>Give every hybrid pass-transistor-bus node a wired-resolution pseudo-NextExpr (in place).
        /// Returns the number of nodes lowered.</summary>
        public static int Apply(Expr?[] nextExpr, DriveInfo?[] drive)
        {
            int lowered = 0, n = nextExpr.Length;
            for (int v = 0; v < n; v++)
            {
                if (nextExpr[v] != null) continue;                                  // already modelled
                if (v >= drive.Length || drive[v] is not { } d) continue;
                if (!d.Hybrid || d.Passes.Count == 0) continue;                      // not a pass-transistor bus
                if (d.PullDown is ComplexExpr) continue;                             // can't model — leave to S1
                if (d.PullUp == PullUpKind.Conditional && d.PullUpCond is null or ComplexExpr) continue;
                bool bad = false; foreach (var pl in d.Passes) if (pl.Cond is ComplexExpr) { bad = true; break; }
                if (bad) continue;

                var pdParts = new List<Expr>();
                if (d.PullDown != null) pdParts.Add(d.PullDown);
                foreach (var pl in d.Passes) pdParts.Add(Expr.And(pl.Cond, Expr.Not(Expr.Node(pl.Other))));

                var puParts = new List<Expr>();
                if (d.PullUp is PullUpKind.StaticLoad or PullUpKind.StrongVcc) puParts.Add(Expr.True);
                else if (d.PullUp == PullUpKind.Conditional && d.PullUpCond is { } pc) puParts.Add(pc);
                foreach (var pl in d.Passes) puParts.Add(Expr.And(pl.Cond, Expr.Node(pl.Other)));

                nextExpr[v] = Expr.Mux(Expr.OrAll(pdParts), Expr.False, Expr.Mux(Expr.OrAll(puParts), Expr.True, Expr.Hold(v)));
                lowered++;
            }
            return lowered;
        }
    }
}
