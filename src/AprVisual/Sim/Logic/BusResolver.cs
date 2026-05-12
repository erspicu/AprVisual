using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S4.2b — Inline Bus Resolver (build-time data) ────────────────────────────────────────────────────
    //
    // The ~1379 hybrid multi-driver tri-state bus nodes (di[v].Hybrid && di[v].Passes.Count > 0, excluding the
    // behavioral-memory modules u1/u4/cart) are NOT given a NextExpr — they stay leaves of the IR graph (so they
    // don't fold the graph into a giant false-loop SCC; see MD/impl/S3/02 §9). Instead, BusResolver collects each
    // bus's drive structure so the runtime / codegen can compute it with the S0/S1/W1 wired-resolution model
    // (MD/impl/S4/00 §10):
    //   S0 (strong 0)  <-  di[v].PullDown  |  a LogicPass with Other == 0  |  a handler injecting 0
    //   S1 (strong 1)  <-  di[v].StrongVcc |  di[v].PullUpCond (precharge)  |  a LogicPass with Other == 1  |  a handler injecting 1
    //   W1 (weak 1)    <-  di[v].StaticLoad (depletion load)
    //   ... then bus-to-bus propagation over BusPasses (bidirectional, full strength) ...
    //   BusVal = (~S0 & S1) | (~S0 & ~S1 & W1) | (~S0 & ~S1 & ~W1 & Hold(v))     // GND wins → VCC/pull-up → depletion → hold
    // wrapped in a ping-pong outer loop:  for outer in 0..K_OUTER: { Eval_DAG(); Eval_SCCs(); FireHandlers(); Eval_Buses(); }.
    internal sealed class BusNode
    {
        public int Id;
        public Expr? PullDown;          // path-to-0 (non-Complex; null = none) ⇒ S0 when true
        public bool StrongVcc;          // a direct VCC connection ⇒ S1 unconditionally
        public bool StaticLoad;         // a depletion load ⇒ W1 unconditionally
        public Expr? PullUpCond;        // a conditional pull-up (precharge transistor) ⇒ S1 when this is true (non-Complex; null = none)
        public List<(Expr Cond, int Other)> LogicPasses = new();      // pass ports whose Other is a non-bus node (driven by logic): cond & ~Node(Other) → S0, cond & Node(Other) → S1
        public List<(Expr Cond, int OtherBusIdx)> BusPasses = new();  // pass ports whose Other is another bus (index into BusNodes[]): bidirectional / full-strength / no decay
    }

    internal static class BusResolver
    {
        /// <summary>Classify the hybrid pass-transistor-bus nodes and collect their drive structure.
        /// Returns (the bus-node array, an isBus[nodeId] flag array).</summary>
        public static (BusNode[] busNodes, bool[] isBusNode) Build(Expr?[] nextExpr, DriveInfo?[] drive)
        {
            int n = nextExpr.Length;
            bool[] isBus = new bool[n];
            for (int v = 0; v < n; v++)
            {
                if (nextExpr[v] != null) continue;                                  // already an IR node — not a bus cut-point
                if (v >= drive.Length || drive[v] is not { } d) continue;
                if (!d.Hybrid || d.Passes.Count == 0) continue;                      // not a pass-transistor bus
                var name = WireCore.GetNodeName(v);
                if (name.StartsWith("u1.") || name.StartsWith("u4.") || name.StartsWith("cart.")) continue;   // behavioral-memory module — handler territory
                isBus[v] = true;
            }
            var idxOf = new Dictionary<int, int>();
            var ids = new List<int>();
            for (int v = 0; v < n; v++) if (isBus[v]) { idxOf[v] = ids.Count; ids.Add(v); }
            var busNodes = new BusNode[ids.Count];
            for (int bi = 0; bi < ids.Count; bi++)
            {
                int v = ids[bi]; var d = drive[v]!;
                var bn = new BusNode { Id = v };
                if (d.PullDown is not null and not ComplexExpr) bn.PullDown = d.PullDown;
                if      (d.PullUp == PullUpKind.StrongVcc)  bn.StrongVcc = true;
                else if (d.PullUp == PullUpKind.StaticLoad) bn.StaticLoad = true;
                else if (d.PullUp == PullUpKind.Conditional && d.PullUpCond is not null and not ComplexExpr) bn.PullUpCond = d.PullUpCond;
                foreach (var pl in d.Passes)
                {
                    if (pl.Cond is ComplexExpr) continue;                            // can't model this port — drop it (the gate / float-exemption catches any consequence)
                    if (pl.Other >= 0 && pl.Other < n && isBus[pl.Other]) bn.BusPasses.Add((pl.Cond, idxOf[pl.Other]));
                    else                                                  bn.LogicPasses.Add((pl.Cond, pl.Other));
                }
                busNodes[bi] = bn;
            }
            return (busNodes, isBus);
        }
    }
}
