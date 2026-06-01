using System;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Escape-1, step 3: the oblivious LOGIC ENGINE + Dynamic Miter.
    //
    //  Given the extracted model (ExtractModel persists: which nodes are extracted combinational,
    //  their complete truth tables, and a topological/level order), this evaluates them OBLIVIOUSLY
    //  in level order — straight-line, no event queue: LogicState[nn] = TT[ packed input states ].
    //  Inputs that are themselves extracted read LogicState (the model's own value); boundary inputs
    //  (state elements / analog islands / clocks / unextracted) read the GOLDEN NodeStates — a hybrid.
    //
    //  Dynamic Miter: after each half-cycle the golden switch-level engine has run, sweep the logic
    //  model and compare LogicState vs NodeStates for the extracted nodes. A match means the levelized
    //  boolean abstraction reproduces the switch-level behaviour for those nodes — the core Escape-1
    //  correctness claim. Mismatches localize exactly which extracted node's logic diverged.
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        // model (populated by ExtractModel)
        internal static byte* _logicIsExtracted;   // 1 = this node is an extracted combinational node
        internal static int* _logicOrder;          // candidate node ids: feed-forward core (topo) first, then cyclic
        internal static int _logicOrderCount;
        internal static int _logicFeedForward;     // first N of _logicOrder are the acyclic feed-forward core
        public static int LogicOrderCount => _logicOrderCount;

        public static int MaxRelaxIters = 40;      // relaxation cap per half-cycle (datapath SCC convergence)
        public static long RelaxIterTotal;         // sum of iterations used (for avg)
        public static long RelaxNonConverged;      // half-cycles that hit the iter cap without converging
        internal static int* _logicTTBase;         // per node: index into _logicTT (dense 2^k byte truth table)
        internal static byte* _logicTT;
        internal static byte* _logicSparse;        // 1 = K>16: value looked up in the sparse _covMap, not the dense TT
        internal static bool LogicModelBuilt;

        internal static byte* LogicState;          // the logic model's per-node value (parallel to NodeStates)

        // miter counters
        public static long MiterSweeps;
        public static long MiterNodeChecks;
        public static long MiterNodeMismatch;      // total (node,half-cycle) mismatches
        internal static long* _miterNodeBad;       // per-node mismatch count (localization)
        public static double MiterSweepSec;
        public static long MiterUncovered;         // (node,hc) evals that hit an unlearned combo (coverage hole)
        public static bool MiterActivity;          // count golden state-changes partitioned by oblivious membership
        public static long ActTotal, ActObliv, ActState;   // total / on oblivious nodes / on cut state-element nodes
        internal static byte* _logicStateCut;      // 1 = demoted to the state/register boundary (was a candidate)
        internal static long* _golActivity;        // per-node golden state-change count during the activity window

        // self-statefulness: TT evaluated on GOLDEN inputs (not LogicState) every half-cycle. If it ever
        // mismatches golden, the node is NOT a pure function of its current inputs -> it holds state (NMOS
        // dynamic latch / deeper sequential) -> a genuine STATE ELEMENT that must become a register, not a
        // contamination victim. Separates the real cut-set from downstream divergence.
        public static bool MiterCollectSelf;
        public static bool MiterLearnOnly;         // fast learning pass: golden-input learn + self-stateful only, no relaxation
        internal static byte* _selfStateful;

        public static void AllocLogicEval()
        {
            LogicState = AllocArray<byte>(NodeCount);
            _miterNodeBad = AllocArray<long>(NodeCount);
            _selfStateful = AllocArray<byte>(NodeCount);
            _logicStateCut = AllocArray<byte>(NodeCount);
            _golActivity = AllocArray<long>(NodeCount);
            for (int nn = 0; nn < NodeCount; nn++) LogicState[nn] = NodeStates[nn];   // seed from golden power-on state
            MiterSweeps = MiterNodeChecks = MiterNodeMismatch = MiterUncovered = 0; MiterSweepSec = 0;
            RelaxIterTotal = RelaxNonConverged = 0;
        }

        public static void ReseedLogicState()
        {
            for (int nn = 0; nn < NodeCount; nn++) LogicState[nn] = NodeStates[nn];
        }

        public static void ResetMiterCounters()
        {
            MiterSweeps = MiterNodeChecks = MiterNodeMismatch = MiterUncovered = 0; MiterSweepSec = 0;
            RelaxIterTotal = RelaxNonConverged = 0;
            for (int nn = 0; nn < NodeCount; nn++) _miterNodeBad[nn] = 0;
        }

        // Demote every self-stateful node out of the oblivious set (it becomes a boundary read from golden /
        // a register in a standalone sim). Filtering the existing order preserves the topological property.
        // Returns the number demoted. The remaining set is provably clean *by construction* on golden inputs.
        // Verify-then-enable for the relaxation itself: demote every node that diverged from golden in the last
        // validate window (state not caught by the golden-input test, relaxation non-convergence, or a wrong
        // held-prior on a coverage hole). Iterating to a fixpoint yields the maximal set that reproduces golden
        // EXACTLY under relaxation — correctness by construction. Returns the number demoted.
        public static int RefineDivergers()
        {
            int w = 0, demoted = 0;
            for (int i = 0; i < _logicOrderCount; i++)
            {
                int nn = _logicOrder[i];
                if (_miterNodeBad[nn] > 0) { _logicIsExtracted[nn] = 0; _logicStateCut[nn] = 1; demoted++; }
                else _logicOrder[w++] = nn;
            }
            _logicOrderCount = w;
            return demoted;
        }

        public static int RefineToSelfClean()
        {
            int w = 0, demoted = 0;
            for (int i = 0; i < _logicOrderCount; i++)
            {
                int nn = _logicOrder[i];
                if (_selfStateful[nn] != 0) { _logicIsExtracted[nn] = 0; _logicStateCut[nn] = 1; demoted++; }
                else _logicOrder[w++] = nn;
            }
            _logicOrderCount = w;
            return demoted;
        }

        // Iterative RELAXATION over the candidate set (feed-forward core first, then cyclic pass network).
        // Sweeps repeatedly until LogicState stops changing (fixed point) or the iteration cap is hit. The
        // datapath's bidirectional pass 2-cycles converge here (A=B only while the gate conducts); genuine
        // bistable latches never converge -> they show up as self-stateful and get cut. Returns iters used.
        internal static int EvalObliviousRelax()
        {
            int* order = _logicOrder;
            int cnt = _logicOrderCount;
            int iters = 0;
            bool changed = true;
            while (changed && iters < MaxRelaxIters)
            {
                changed = false; iters++;
                for (int i = 0; i < cnt; i++)
                {
                    int nn = order[i];
                    ushort* p = _covInputs + _covBase[nn];
                    int k = *p++;
                    ulong key = 0;
                    for (int j = 0; j < k; j++)
                    {
                        int inp = p[j];
                        ulong bit = _logicIsExtracted[inp] != 0 ? LogicState[inp] : NodeStates[inp];   // hybrid boundary
                        key |= bit << j;
                    }
                    byte v = _logicSparse[nn] != 0
                        ? (_covMap![nn].TryGetValue(key, out byte sv) ? sv : TtUnseen)
                        : _logicTT[_logicTTBase[nn] + (int)key];
                    if (v == TtUnseen) { if (iters == 1) MiterUncovered++; continue; }   // unlearned combo -> hold prior
                    if (LogicState[nn] != v) { LogicState[nn] = v; changed = true; }
                }
            }
            RelaxIterTotal += iters;
            if (changed) RelaxNonConverged++;
            return iters;
        }

        // After the golden engine advanced one half-cycle: sweep + compare extracted nodes.
        public static void MiterStep()
        {
            bool learnOnly = MiterLearnOnly;
            if (!learnOnly)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                EvalObliviousRelax();
                sw.Stop();
                MiterSweepSec += sw.Elapsed.TotalSeconds;
            }
            MiterSweeps++;
            int* order = _logicOrder; int cnt = _logicOrderCount;
            bool self = MiterCollectSelf;
            for (int i = 0; i < cnt; i++)
            {
                int nn = order[i];
                if (!learnOnly)
                {
                    MiterNodeChecks++;
                    if (LogicState[nn] != NodeStates[nn]) { MiterNodeMismatch++; _miterNodeBad[nn]++; }
                }
                if (self && _selfStateful[nn] == 0)
                {
                    ushort* p = _covInputs + _covBase[nn];
                    int k = *p++;
                    ulong gkey = 0;
                    for (int j = 0; j < k; j++) gkey |= (ulong)NodeStates[p[j]] << j;   // pack from GOLDEN inputs
                    byte gv = NodeStates[nn];
                    if (_logicSparse[nn] != 0)
                    {
                        var map = _covMap![nn];
                        if (!map.TryGetValue(gkey, out byte cur)) map[gkey] = gv;        // LEARN
                        else if (cur != gv) _selfStateful[nn] = 1;                       // CONTRADICTION
                    }
                    else
                    {
                        int slot = _logicTTBase[nn] + (int)gkey;
                        byte cur = _logicTT[slot];
                        if (cur == TtUnseen) _logicTT[slot] = gv;                        // LEARN
                        else if (cur != gv) _selfStateful[nn] = 1;                       // CONTRADICTION
                    }
                }
            }
        }

        // Break the truly-residual switch-level activity down by WHY each node is uncovered and by subsystem,
        // to decide whether the residual is a reducible coverage gap (-> structural extraction raises the
        // ceiling) or the irreducible analog/datapath core (-> the ceiling is near what we measured).
        public static void ReportResidualBreakdown()
        {
            long wide = 0, stateful = 0, unobsClean = 0, noChannel = 0, total = 0;
            long cpu = 0, ppu = 0, oth = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                long a = _golActivity[nn];
                if (a == 0) continue;
                if ((_logicIsExtracted != null && _logicIsExtracted[nn] != 0) || (_logicStateCut != null && _logicStateCut[nn] != 0)) continue; // covered
                total += a;
                if (_covWide[nn] != 0) wide += a;
                else if (_covStateful[nn] != 0) stateful += a;
                else if (_covSeen[nn] != 0) unobsClean += a;     // clean radius-1 but never made a candidate (K>16, or never in a map)
                else noChannel += a;                              // no channels / supply-anchored / not boolean
                string name = GetNodeName(nn);
                int dot = name.IndexOf('.');
                string sub = dot > 0 ? name.Substring(0, dot) : "other";
                if (sub.StartsWith("cpu")) cpu += a; else if (sub.StartsWith("ppu")) ppu += a; else oth += a;
            }
            long t = Math.Max(1, total);
            Console.WriteLine($"# ---- residual switch-level activity, by cause ----");
            Console.WriteLine($"#   wide (>{MaxDenseK} inputs):        {wide:N0}  ({Pct(wide, t):F1}%)  reducible: wider TT / structural");
            Console.WriteLine($"#   stateful @ radius-1:           {stateful:N0}  ({Pct(stateful, t):F1}%)  reducible: deeper-radius / register");
            Console.WriteLine($"#   clean but not a candidate:     {unobsClean:N0}  ({Pct(unobsClean, t):F1}%)  reducible: structural extract");
            Console.WriteLine($"#   no-channel / supply / analog:  {noChannel:N0}  ({Pct(noChannel, t):F1}%)  likely irreducible");
            Console.WriteLine($"#   by subsystem: cpu {Pct(cpu, t):F1}%  ppu {Pct(ppu, t):F1}%  other {Pct(oth, t):F1}%");
            Console.WriteLine($"# --------------------------------------------------");
        }

        public static void ReportMiter()
        {
            int badNodes = 0;
            for (int nn = 0; nn < NodeCount; nn++) if (_miterNodeBad[nn] > 0) badNodes++;
            double matchRate = MiterNodeChecks > 0 ? 100.0 * (MiterNodeChecks - MiterNodeMismatch) / MiterNodeChecks : 0;
            Console.WriteLine("# ============ DYNAMIC MITER (oblivious logic vs golden) ============");
            Console.WriteLine($"#  extracted nodes evaluated: {_logicOrderCount:N0}  (in level order)");
            Console.WriteLine($"#  half-cycles mitered: {MiterSweeps:N0}");
            Console.WriteLine($"#  per-(node,hc) checks: {MiterNodeChecks:N0}");
            Console.WriteLine($"#    MATCH golden: {matchRate:F3}%   ({MiterNodeMismatch:N0} mismatches)");
            Console.WriteLine($"#    extracted nodes that EVER diverged: {badNodes:N0} / {_logicOrderCount:N0}");
            Console.WriteLine($"#    coverage holes (combo not learned, held prior): {MiterUncovered:N0}");
            if (MiterSweeps > 0)
            {
                Console.WriteLine($"#  relax: avg {(double)RelaxIterTotal / MiterSweeps:F2} iters/half-cycle, non-converged {RelaxNonConverged:N0} hc");
                Console.WriteLine($"#  oblivious sweep: {MiterSweepSec / MiterSweeps * 1e9:F0} ns/half-cycle for {_logicOrderCount:N0} nodes  ({(double)RelaxIterTotal * _logicOrderCount / MiterSweepSec / 1e6:F0} M node-evals/s)");
            }
            // top divergent nodes (localization)
            if (badNodes > 0)
            {
                var top = new System.Collections.Generic.List<(int nn, long c)>();
                for (int nn = 0; nn < NodeCount; nn++) if (_miterNodeBad[nn] > 0) top.Add((nn, _miterNodeBad[nn]));
                top.Sort((a, b) => b.c.CompareTo(a.c));
                Console.WriteLine($"#  top diverging nodes (localization):");
                for (int i = 0; i < Math.Min(12, top.Count); i++)
                    Console.WriteLine($"#    {GetNodeName(top[i].nn),-22} {top[i].c:N0} bad half-cycles");
            }
            Console.WriteLine("# ===================================================================");
        }
    }
}
