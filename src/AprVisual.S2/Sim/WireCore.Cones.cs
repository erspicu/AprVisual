using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Escape-1, macro-event de-risk: the CONE COMPRESSION RATIO.
    //
    //  The macro-event design (Gemini) reclaims event-driven sparsity by making the atomic unit a
    //  latch/bus-bounded combinational CONE instead of a node. Its honest 4-6x estimate assumes only
    //  ~tens of distinct cones fire per half-cycle. This measures that directly, with ZERO engine build:
    //  condense the oblivious combinational nodes into cones (union-find over radius-1 adjacency, CUT at
    //  bus nodes and at state/latch nodes), then over a golden run count, per half-cycle, the number of
    //  DISTINCT macro-units (cones + bus super-nodes + latches) that contain a changed node — vs golden's
    //  ~600 node-events. High compression (few macro-units, modest cone sizes) => macro-event is worth
    //  building; low compression or giant cones => it would only break even, abort.
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        public static bool MiterConeCount;
        internal static int* _coneOf;          // node -> macro-unit id (combinational cone root, or a singleton for bus/latch/residual)
        internal static byte* _coneIsComb;     // per macro-unit: 1 = a real combinational cone (not a singleton)
        internal static int _coneCount;
        internal static byte* _coneDirty;      // per macro-unit: touched this half-cycle
        internal static int* _coneDirtyList;   // the touched macro-unit ids (to clear cheaply)
        internal static int _coneDirtyCount;
        internal static long _coneNodeEvents;  // node-changes this half-cycle (golden's event count)

        // accumulators over the cone window
        private static long _coneHcCount, _coneSumUnits, _coneSumCombCones, _coneSumNodeEv, _coneMaxUnits;
        private static long _coneCombDirtyThisHc;

        // Condense combinational nodes into cones. Combinational = oblivious & !bus & !state-cut.
        // Edge nn—inp when inp is also combinational (union); bus/latch inputs are NOT unioned => cone cuts.
        public static void BuildCones()
        {
            int n = NodeCount;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }
            bool IsComb(int nn) => _logicIsExtracted != null && _logicIsExtracted[nn] != 0 && _logicBus[nn] == 0 && _logicStateCut[nn] == 0 && _covBase[nn] != 0;

            // Union through CHANNEL far-ends only (the bidirectional electrical connectivity = the atomic
            // settle-unit), NOT through gates (a gate is a unidirectional control wire between cones, merging
            // through it over-merges). Cut at bus/latch (not combinational). Read channels from NodeInfos.
            for (int nn = 0; nn < n; nn++)
            {
                if (!IsComb(nn)) continue;
                NodeInfo* ns = NodeInfos + nn;
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) { int f = pay[k + 1]; if (IsComb(f)) Union(nn, f); }   // far-end only
                }
                else if (ns->TlistC1c2s != 0)
                {
                    ushort* p = TransistorList + ns->TlistC1c2s;
                    while (*p != 0) { p++; int f = *p++; if (IsComb(f)) Union(nn, f); }                      // skip gate, far-end only
                }
            }

            _coneOf = AllocArray<int>(n);
            var idOf = new Dictionary<int, int>();
            var sizes = new List<int>();
            int next = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null) { _coneOf[nn] = -1; continue; }
                int key = IsComb(nn) ? Find(nn) : (n + nn);   // comb -> cone root; else unique singleton
                if (!idOf.TryGetValue(key, out int id)) { id = next++; idOf[key] = id; }
                _coneOf[nn] = id;
            }
            _coneCount = next;
            _coneIsComb = AllocArray<byte>(_coneCount);
            _coneDirty = AllocArray<byte>(_coneCount);
            _coneDirtyList = AllocArray<int>(_coneCount);

            // sizes per macro-unit + comb flag
            var unitSize = new int[_coneCount];
            int combCones = 0;
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null) continue;
                int id = _coneOf[nn];
                unitSize[id]++;
                if (IsComb(nn)) _coneIsComb[id] = 1;
            }
            var combSizes = new List<int>();
            for (int id = 0; id < _coneCount; id++) if (_coneIsComb[id] != 0) { combCones++; combSizes.Add(unitSize[id]); }
            combSizes.Sort();
            int Pct(double p) => combSizes.Count == 0 ? 0 : combSizes[Math.Min(combSizes.Count - 1, (int)(p * combSizes.Count))];
            long combNodes = 0; foreach (int s in combSizes) combNodes += s;
            int big = 0; foreach (int s in combSizes) if (s > 50) big++;

            Console.WriteLine("# ============ CONE PARTITION (macro-units) ============");
            Console.WriteLine($"#  macro-units total: {_coneCount:N0}  (combinational cones {combCones:N0} + singletons {_coneCount - combCones:N0} = bus/latch/residual)");
            Console.WriteLine($"#  combinational cone sizes (nodes): mean {(combCones > 0 ? (double)combNodes / combCones : 0):F1}, median {Pct(0.5)}, p90 {Pct(0.9)}, max {(combSizes.Count > 0 ? combSizes[combSizes.Count - 1] : 0)}");
            Console.WriteLine($"#    cones with >50 nodes (giant): {big:N0}");
            Console.WriteLine("# =======================================================");
            _coneHcCount = _coneSumUnits = _coneSumCombCones = _coneSumNodeEv = _coneMaxUnits = 0;
        }

        // Called after each golden half-cycle (Step(1)): tally distinct macro-units touched, then clear.
        public static void ConeStepBoundary()
        {
            _coneHcCount++;
            _coneSumUnits += _coneDirtyCount;
            _coneSumCombCones += _coneCombDirtyThisHc;
            _coneSumNodeEv += _coneNodeEvents;
            if (_coneDirtyCount > _coneMaxUnits) _coneMaxUnits = _coneDirtyCount;
            for (int i = 0; i < _coneDirtyCount; i++) _coneDirty[_coneDirtyList[i]] = 0;
            _coneDirtyCount = 0; _coneNodeEvents = 0; _coneCombDirtyThisHc = 0;
        }

        public static void ReportCones()
        {
            double hc = Math.Max(1, _coneHcCount);
            double nodeEv = _coneSumNodeEv / hc, units = _coneSumUnits / hc, comb = _coneSumCombCones / hc;
            Console.WriteLine("# ======= CONE COMPRESSION RATIO (macro-event de-risk) =======");
            Console.WriteLine($"#  half-cycles measured: {_coneHcCount:N0}");
            Console.WriteLine($"#  golden node-events / hc:        {nodeEv:F0}");
            Console.WriteLine($"#  distinct MACRO-UNITS / hc:      {units:F0}  (max {_coneMaxUnits:N0})   <- the macro-event count");
            Console.WriteLine($"#    of which combinational cones: {comb:F0}   (rest = bus/latch/residual singletons)");
            Console.WriteLine($"#  COMPRESSION RATIO: {nodeEv / Math.Max(1, units):F1}x  (node-events -> macro-events)");
            Console.WriteLine($"#  -> Gemini's 4-6x math assumes ~50 macro-units/hc; compare against {units:F0}");
            Console.WriteLine("# ============================================================");
        }
    }
}
