using System;
using System.Collections.Generic;

namespace AprVisual.Sim.Logic
{
    // ── S2.4: run the extracted IR (nextExpr[N] from S2.0–S2.3) alongside S1's switch-level engine.
    //    First cut = "checking mode" (the S2.6 equivalence gate): each StepOne() snapshots NodeStates
    //    (the previous half-cycle's settled state — what Hold/Prev read), runs WireCore.Step(1) — which,
    //    since S1's handlers are "Case B" (SetHigh/SetLow call RecalcNodeList → ProcessQueue immediately),
    //    toggles the clock + services memory + settles the *whole* chip for this half-cycle — then for
    //    every IR-covered node v verifies  EvalExpr(nextExpr[v]) == NodeStates[v]  (NodeRef reads
    //    NodeStates = S1's this-half-cycle settled values; Hold/Prev read the snapshot). Mismatches are
    //    recorded, not overwritten — this directly proves the IR's logic agrees with S1's group-BFS, per
    //    node, per half-cycle. The topo-sort / Tarjan-residual-SCC / hybrid-bridge ("driving mode" — the IR
    //    actually drives the netlist, for the speed-up) is deferred (S3 / a later S2.4 firing). Does NOT
    //    change S1's behaviour. See MD/impl/S2/05_S2.4_IR直譯器_設計.md (incl. the Gemini review).

    internal static unsafe class IrEngine
    {
        public static Expr?[] NextExpr = [];     // [nodeId]; null = hybrid / Supply / Input — not IR-covered (S1 owns it)
        public static bool[] IsSequential = [];
        public static bool[] Hybrid = [];
        public static bool[] CheckInChecking = []; // [nodeId]; false = skip the IR-vs-S1 check (an un-modelled internal placeholder — nextExpr == Hold(self), Gates.Count == 0, unnamed)
        public static byte[] PrevStates = [];    // [nodeId]; snapshot of NodeStates at the start of the current half-cycle
        public static bool Built;

        public static int IrCoveredCount;        // # of nodes with NextExpr != null
        public static int CheckedCount;          // # of nodes actually compared in checking mode (IrCoveredCount minus the skipped placeholders)
        public static long MismatchCount;        // total node-mismatches over all StepOne() calls since Build()
        public static long FirstMismatchTime = -1;
        public static int  FirstMismatchNode = -1;
        public static readonly Dictionary<int, long> MismatchByNode = new();   // node id → how many half-cycles it mismatched

        /// <summary>Build the IR from the netlist currently composed in WireCore (after LoadSystem / Reset).</summary>
        public static void Build()
        {
            var g   = NetlistGraph.BuildFrom();
            var di  = DriveAnalysis.Analyze(g);
            var s2  = NextStateModel.Build(g, di);
            var scc = SccModel.Build(g, di, s2);
            NextExpr = scc.NextExpr;
            IsSequential = scc.IsSequential;
            Hybrid = scc.Hybrid;
            int n = Math.Max(NextExpr.Length, WireCore.NodeCount);
            PrevStates = new byte[n];
            CheckInChecking = new bool[NextExpr.Length];
            IrCoveredCount = 0; CheckedCount = 0;
            var nodes = WireCore.Nodes;
            for (int v = 0; v < NextExpr.Length; v++)
            {
                var e = NextExpr[v];
                if (e == null) continue;
                IrCoveredCount++;
                // skip un-modelled internal placeholders: nextExpr == Hold(self), nothing reads this node (Gates.Count == 0),
                // and it's unnamed (a named node — a pin / observable signal — we DO want to check, even if its model is wrong).
                bool placeholder = e is HoldExpr h && h.Id == v
                                   && v < nodes.Count && nodes[v] is { Gates.Count: 0 }
                                   && WireCore.GetNodeName(v) == v.ToString();
                CheckInChecking[v] = !placeholder;
                if (!placeholder) CheckedCount++;
            }
            MismatchCount = 0; FirstMismatchTime = -1; FirstMismatchNode = -1; MismatchByNode.Clear();
            Built = true;
        }

        public static int EvalExpr(Expr e) => e switch
        {
            ConstExpr c   => c.Value ? 1 : 0,
            NodeRefExpr nr => WireCore.NodeStates[nr.Id],                       // current value (S1's this-half-cycle settled state)
            HoldExpr h    => h.Id < PrevStates.Length ? PrevStates[h.Id] : 0,  // value at the start of this half-cycle (parasitic-cap hold)
            PrevExpr p    => p.Id < PrevStates.Length ? PrevStates[p.Id] : 0,  //   …same, but used to break a sequential loop
            NotExpr x     => 1 - EvalExpr(x.Operand),
            AndExpr a     => EvalExpr(a.L) & EvalExpr(a.R),
            OrExpr o      => EvalExpr(o.L) | EvalExpr(o.R),
            MuxExpr m     => EvalExpr(m.Cond) != 0 ? EvalExpr(m.A) : EvalExpr(m.B),
            ComplexExpr   => 0,                                                // shouldn't appear (Complex ⇒ hybrid ⇒ NextExpr is null)
            _             => 0,
        };

        /// <summary>One half-cycle, checking mode: snapshot prev → WireCore.Step(1) (S1 settles) → verify IR vs S1.</summary>
        public static void StepOne()
        {
            if (!Built) Build();
            int n = WireCore.NodeCount;
            new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(PrevStates);   // prevStates = NodeStates (start of this half-cycle)
            WireCore.Step(1);                                                    // S1: clock toggle + memory callbacks + settle (Case B) + Time++
            for (int v = 0; v < NextExpr.Length && v < n; v++)
            {
                var e = NextExpr[v];
                if (e == null || !CheckInChecking[v]) continue;                   // hybrid / not IR-covered / un-modelled placeholder — S1 owns it
                if (EvalExpr(e) != WireCore.NodeStates[v])
                {
                    MismatchCount++;
                    MismatchByNode[v] = MismatchByNode.GetValueOrDefault(v) + 1;
                    if (FirstMismatchTime < 0) { FirstMismatchTime = WireCore.Time; FirstMismatchNode = v; }
                }
            }
        }

        public static void Step(int count) { for (int i = 0; i < count; i++) StepOne(); }

        /// <summary>Step until the PPU's in-vblank flag rises (one frame), or maxHalfCycles. Mirrors WireCore.RunFrame.</summary>
        public static long RunFrame(long maxHalfCycles = 1_200_000)
        {
            long start = WireCore.Time;
            int vbl = WireCore.N_PpuInVblank;
            if (vbl == WireCore.EmptyNode) { Step((int)Math.Min(maxHalfCycles, 714_736)); return WireCore.Time - start; }
            bool prev = WireCore.NodeStates[vbl] != 0;
            for (long i = 0; i < maxHalfCycles; i++)
            {
                StepOne();
                bool now = WireCore.NodeStates[vbl] != 0;
                if (!prev && now) break;
                prev = now;
            }
            return WireCore.Time - start;
        }
    }
}
