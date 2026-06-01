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
        internal static int* _logicOrder;          // extracted node ids in topological (level) order
        internal static int _logicOrderCount;
        public static int LogicOrderCount => _logicOrderCount;
        internal static int* _logicTTBase;         // per node: index into _logicTT (dense 2^k byte truth table)
        internal static byte* _logicTT;
        internal static bool LogicModelBuilt;

        internal static byte* LogicState;          // the logic model's per-node value (parallel to NodeStates)

        // miter counters
        public static long MiterSweeps;
        public static long MiterNodeChecks;
        public static long MiterNodeMismatch;      // total (node,half-cycle) mismatches
        internal static long* _miterNodeBad;       // per-node mismatch count (localization)
        public static double MiterSweepSec;

        // self-statefulness: TT evaluated on GOLDEN inputs (not LogicState) every half-cycle. If it ever
        // mismatches golden, the node is NOT a pure function of its current inputs -> it holds state (NMOS
        // dynamic latch / deeper sequential) -> a genuine STATE ELEMENT that must become a register, not a
        // contamination victim. Separates the real cut-set from downstream divergence.
        public static bool MiterCollectSelf;
        internal static byte* _selfStateful;

        public static void AllocLogicEval()
        {
            LogicState = AllocArray<byte>(NodeCount);
            _miterNodeBad = AllocArray<long>(NodeCount);
            _selfStateful = AllocArray<byte>(NodeCount);
            for (int nn = 0; nn < NodeCount; nn++) LogicState[nn] = NodeStates[nn];   // seed from golden power-on state
            MiterSweeps = MiterNodeChecks = MiterNodeMismatch = 0; MiterSweepSec = 0;
        }

        public static void ReseedLogicState()
        {
            for (int nn = 0; nn < NodeCount; nn++) LogicState[nn] = NodeStates[nn];
        }

        public static void ResetMiterCounters()
        {
            MiterSweeps = MiterNodeChecks = MiterNodeMismatch = 0; MiterSweepSec = 0;
            for (int nn = 0; nn < NodeCount; nn++) _miterNodeBad[nn] = 0;
        }

        // Demote every self-stateful node out of the oblivious set (it becomes a boundary read from golden /
        // a register in a standalone sim). Filtering the existing order preserves the topological property.
        // Returns the number demoted. The remaining set is provably clean *by construction* on golden inputs.
        public static int RefineToSelfClean()
        {
            int w = 0, demoted = 0;
            for (int i = 0; i < _logicOrderCount; i++)
            {
                int nn = _logicOrder[i];
                if (_selfStateful[nn] != 0) { _logicIsExtracted[nn] = 0; demoted++; }
                else _logicOrder[w++] = nn;
            }
            _logicOrderCount = w;
            return demoted;
        }

        // One oblivious pass over the extracted combinational nodes in level order.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static void EvalObliviousSweep()
        {
            int* order = _logicOrder;
            int cnt = _logicOrderCount;
            for (int i = 0; i < cnt; i++)
            {
                int nn = order[i];
                ushort* p = _covInputs + _covBase[nn];
                int k = *p++;
                int idx = 0;
                for (int j = 0; j < k; j++)
                {
                    int inp = p[j];
                    int bit = _logicIsExtracted[inp] != 0 ? LogicState[inp] : NodeStates[inp];   // hybrid boundary
                    idx |= bit << j;
                }
                LogicState[nn] = _logicTT[_logicTTBase[nn] + idx];
            }
        }

        // After the golden engine advanced one half-cycle: sweep + compare extracted nodes.
        public static void MiterStep()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            EvalObliviousSweep();
            sw.Stop();
            MiterSweepSec += sw.Elapsed.TotalSeconds;
            MiterSweeps++;
            int* order = _logicOrder; int cnt = _logicOrderCount;
            bool self = MiterCollectSelf;
            for (int i = 0; i < cnt; i++)
            {
                int nn = order[i];
                MiterNodeChecks++;
                if (LogicState[nn] != NodeStates[nn]) { MiterNodeMismatch++; _miterNodeBad[nn]++; }
                if (self && _selfStateful[nn] == 0)
                {
                    ushort* p = _covInputs + _covBase[nn];
                    int k = *p++;
                    int gidx = 0;
                    for (int j = 0; j < k; j++) gidx |= NodeStates[p[j]] << j;   // pack from GOLDEN inputs
                    if (_logicTT[_logicTTBase[nn] + gidx] != NodeStates[nn]) _selfStateful[nn] = 1;
                }
            }
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
            if (MiterSweeps > 0)
                Console.WriteLine($"#  oblivious sweep: {MiterSweepSec / MiterSweeps * 1e9:F0} ns/half-cycle for {_logicOrderCount:N0} nodes  ({_logicOrderCount / (MiterSweepSec / MiterSweeps) / 1e6:F0} M node-evals/s)");
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
