using System;
using System.Collections.Generic;
using System.Text;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        public static string AggressiveLoweringCensusReport(string label)
        {
            int live = 0, normal = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i] == null) continue;
                live++;
                if (i > Ngnd) normal++;
            }

            var constMask = BuildAggressiveConstMask();
            var railKeepConflicts = SimulateConstFoldRebuild(constMask, foldConflicts: false);
            var railFoldConflicts = SimulateConstFoldRebuild(constMask, foldConflicts: true);
            var series = CensusSeriesAndPhase();
            var dynamic = CensusDynamicMacro();
            var islands = CensusUnobservableIslands();

            var nodeUnion = new HashSet<int>();
            var transTouched = new HashSet<int>();

            for (int nn = 3; nn < constMask.Length; nn++)
            {
                if (_nodes[nn] == null) continue;
                if ((constMask[nn] & 3) != 0) nodeUnion.Add(nn);
            }
            foreach (int nn in series.PureMidNodes) nodeUnion.Add(nn);
            foreach (int nn in islands.AggressiveNodes) nodeUnion.Add(nn);
            foreach (int tid in dynamic.BroadMacroTrans) transTouched.Add(tid);
            foreach (int tid in islands.AggressiveTrans) transTouched.Add(tid);
            for (int tid = 0; tid < _transistors.Count; tid++)
            {
                var t = _transistors[tid];
                if (nodeUnion.Contains(t.Gate) || nodeUnion.Contains(t.C1) || nodeUnion.Contains(t.C2))
                    transTouched.Add(tid);
            }

            double P(int part, int total) => total == 0 ? 0.0 : 100.0 * part / total;

            var sb = new StringBuilder();
            sb.AppendLine($"# aggressive-lower-census: {label}");
            sb.AppendLine($"# baseline after current safe lowering + handlers: live nodes {live:N0} (normal {normal:N0}), transistors {_transistors.Count:N0}");
            sb.AppendLine($"# {LastLowerStats}");
            sb.AppendLine();
            sb.AppendLine("1) rail clamp / constant fold");
            sb.AppendLine($"   const nodes: low {railKeepConflicts.ConstLow:N0}, high {railKeepConflicts.ConstHigh:N0}, conflict {railKeepConflicts.ConstConflict:N0}");
            sb.AppendLine($"   non-conflict fold: nodes {live:N0} -> {railKeepConflicts.NodesAfter:N0} (delete/merge {railKeepConflicts.NodesDeleted:N0}, {P(railKeepConflicts.NodesDeleted, live):F2}%), trans {_transistors.Count:N0} -> {railKeepConflicts.TransAfter:N0} (drop {railKeepConflicts.TransDropped:N0}, {P(railKeepConflicts.TransDropped, _transistors.Count):F2}%)");
            sb.AppendLine($"   arbitrary conflict fold upper bound: nodes {live:N0} -> {railFoldConflicts.NodesAfter:N0} (delete/merge {railFoldConflicts.NodesDeleted:N0}, {P(railFoldConflicts.NodesDeleted, live):F2}%), trans {_transistors.Count:N0} -> {railFoldConflicts.TransAfter:N0} (drop {railFoldConflicts.TransDropped:N0}, {P(railFoldConflicts.TransDropped, _transistors.Count):F2}%)");
            sb.AppendLine();
            sb.AppendLine("2) series stack compression");
            sb.AppendLine($"   pure degree-2 midpoints: {series.PureMidNodes.Count:N0} nodes ({P(series.PureMidNodes.Count, normal):F2}% of normal); unnamed {series.PureUnnamed:N0}, supply-touch {series.PureSupplyTouch:N0}");
            sb.AppendLine($"   same-gate directly fusable subset: {series.SameGate:N0}; generic macro row saving upper bound ~= {series.PureMidNodes.Count:N0} rows (replace 2 channel rows with 1 macro row per midpoint)");
            sb.AppendLine();
            sb.AppendLine("3) mutually-exclusive gate pruning");
            sb.AppendLine($"   phase-exclusive pure bridges: {series.PhaseExclusive:N0} midpoints; incident rows touched {series.PhaseExclusive * 2:N0}");
            sb.AppendLine($"   heuristic only: gate names matched clock/phase complements such as clk0/clk1, pclk0/pclk1, phi1/phi2");
            sb.AppendLine();
            sb.AppendLine("4) precharge/evaluate or pulldown-network macro");
            sb.AppendLine($"   strict pullup + GND-only singleton outputs: {dynamic.StrictNodes:N0} nodes; pulldown rows replaceable {dynamic.StrictTrans.Count:N0} ({P(dynamic.StrictTrans.Count, _transistors.Count):F2}%)");
            sb.AppendLine($"   broad pullup nodes with any GND evaluate path: {dynamic.BroadNodes:N0} nodes; touched rows {dynamic.BroadMacroTrans.Count:N0} ({P(dynamic.BroadMacroTrans.Count, _transistors.Count):F2}%)");
            sb.AppendLine($"   clocked precharge/evaluate-looking nodes (has VCC path and GND path): {dynamic.PrechargeEvalNodes:N0}; touched rows {dynamic.PrechargeEvalTrans.Count:N0}");
            sb.AppendLine();
            sb.AppendLine("5) unobservable island elimination");
            sb.AppendLine($"   strict islands (unnamed/no-gate/no-callback/no-pullup/no-rail/no-force): {islands.StrictComponents:N0} comps, {islands.StrictNodeCount:N0} nodes, incident rows {islands.StrictTrans.Count:N0}");
            sb.AppendLine($"   aggressive state-only islands (unnamed/no-gate/no-callback/no-force; allows pull/rail): {islands.AggressiveComponents:N0} comps, {islands.AggressiveNodes.Count:N0} nodes ({P(islands.AggressiveNodes.Count, normal):F2}%), incident rows {islands.AggressiveTrans.Count:N0} ({P(islands.AggressiveTrans.Count, _transistors.Count):F2}%)");
            sb.AppendLine();
            sb.AppendLine("rough union upper bound");
            sb.AppendLine($"   candidate nodes covered by 1/2/5: {nodeUnion.Count:N0} of {normal:N0} normal nodes ({P(nodeUnion.Count, normal):F2}%)");
            sb.AppendLine($"   transistor rows touched/replaced by 1/2/4/5: {transTouched.Count:N0} of {_transistors.Count:N0} ({P(transTouched.Count, _transistors.Count):F2}%)");
            sb.AppendLine("   This is a destructive potential census, not an equivalence claim.");
            return sb.ToString();
        }

        private static byte[] BuildAggressiveConstMask()
        {
            int n = _nodes.Count;
            var mask = new byte[n];
            if (n > Npwr) mask[Npwr] = 2;
            if (n > Ngnd) mask[Ngnd] = 1;

            bool Add(int nn, byte m)
            {
                if (nn <= Ngnd || nn >= n || _nodes[nn] == null) return false;
                byte old = mask[nn];
                byte now = (byte)(old | (m & 3));
                if (now == old) return false;
                mask[nn] = now;
                return true;
            }

            bool changed;
            do
            {
                changed = false;
                foreach (var t in _transistors)
                {
                    if (t.Gate < 0 || t.Gate >= n) continue;
                    if ((mask[t.Gate] & 2) == 0) continue; // gate can be high
                    if (t.C1 >= 0 && t.C1 < n && mask[t.C1] != 0) changed |= Add(t.C2, mask[t.C1]);
                    if (t.C2 >= 0 && t.C2 < n && mask[t.C2] != 0) changed |= Add(t.C1, mask[t.C2]);
                }
            } while (changed);

            return mask;
        }

        private readonly struct RebuildStats
        {
            public readonly int ConstLow, ConstHigh, ConstConflict, NodesAfter, NodesDeleted, TransAfter, TransDropped;
            public RebuildStats(int constLow, int constHigh, int constConflict, int nodesAfter, int nodesDeleted, int transAfter, int transDropped)
            {
                ConstLow = constLow; ConstHigh = constHigh; ConstConflict = constConflict;
                NodesAfter = nodesAfter; NodesDeleted = nodesDeleted; TransAfter = transAfter; TransDropped = transDropped;
            }
        }

        private static RebuildStats SimulateConstFoldRebuild(byte[] constMask, bool foldConflicts)
        {
            int n = _nodes.Count;
            int live = 0, low = 0, high = 0, conflict = 0;
            for (int i = 0; i < n; i++)
            {
                if (_nodes[i] == null) continue;
                live++;
                if (i <= Ngnd) continue;
                switch (constMask[i] & 3)
                {
                    case 1: low++; break;
                    case 2: high++; break;
                    case 3: conflict++; break;
                }
            }

            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Fold(int x)
            {
                if (x < 0 || x >= n || _nodes[x] == null) return EmptyNode;
                if (x <= Ngnd) return x;
                byte m = (byte)(constMask[x] & 3);
                if (m == 1) return Ngnd;
                if (m == 2) return Npwr;
                if (m == 3 && foldConflicts) return Ngnd;
                return x;
            }

            int Find(int x)
            {
                int r = x;
                while (parent[r] != r) r = parent[r];
                while (parent[x] != r) { int p = parent[x]; parent[x] = r; x = p; }
                return r;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (a < b) parent[b] = a; else parent[a] = b;
            }

            foreach (var t in _transistors)
            {
                int g = Fold(t.Gate), c1 = Fold(t.C1), c2 = Fold(t.C2);
                if (g == Npwr && !t.IsWeak && c1 > Ngnd && c2 > Ngnd) Union(c1, c2);
            }

            int Final(int x)
            {
                int y = Fold(x);
                return y > Ngnd ? Find(y) : y;
            }

            var roots = new HashSet<int>();
            for (int i = 3; i < n; i++)
            {
                if (_nodes[i] == null) continue;
                int f = Fold(i);
                if (f > Ngnd) roots.Add(Find(f));
            }
            int nodesAfter = 2 + roots.Count;

            var seen = new HashSet<(int, int, int)>();
            foreach (var t in _transistors)
            {
                int g = Final(t.Gate), c1 = Final(t.C1), c2 = Final(t.C2);
                if (g == EmptyNode || c1 == EmptyNode || c2 == EmptyNode) continue;
                if (g == Ngnd) continue;
                if (c1 == c2) continue;
                if (IsPwrGnd(c1) && IsPwrGnd(c2)) continue;
                if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);
                seen.Add((g, c1, c2));
            }

            return new RebuildStats(low, high, conflict, nodesAfter, live - nodesAfter, seen.Count, _transistors.Count - seen.Count);
        }

        private sealed class SeriesStats
        {
            public readonly HashSet<int> PureMidNodes = new();
            public int PureUnnamed, PureSupplyTouch, SameGate, PhaseExclusive;
        }

        private static SeriesStats CensusSeriesAndPhase()
        {
            var s = new SeriesStats();
            for (int nn = 3; nn < _nodes.Count; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                if (node.Pullups != 0 || node.Callback != null || node.Gates.Count != 0 || node.C1c2s.Count != 2) continue;
                int t0id = node.C1c2s[0], t1id = node.C1c2s[1];
                if ((uint)t0id >= (uint)_transistors.Count || (uint)t1id >= (uint)_transistors.Count) continue;
                var t0 = _transistors[t0id];
                var t1 = _transistors[t1id];
                s.PureMidNodes.Add(nn);
                if (!HasBuildName(nn)) s.PureUnnamed++;
                if (GetOtherEndpoint(t0, nn) <= Ngnd || GetOtherEndpoint(t1, nn) <= Ngnd) s.PureSupplyTouch++;
                if (t0.Gate == t1.Gate) s.SameGate++;
                if (ArePhaseExclusiveGates(t0.Gate, t1.Gate)) s.PhaseExclusive++;
            }
            return s;
        }

        private sealed class DynamicMacroStats
        {
            public int StrictNodes, BroadNodes, PrechargeEvalNodes;
            public readonly HashSet<int> StrictTrans = new();
            public readonly HashSet<int> BroadMacroTrans = new();
            public readonly HashSet<int> PrechargeEvalTrans = new();
        }

        private static DynamicMacroStats CensusDynamicMacro()
        {
            var d = new DynamicMacroStats();
            for (int nn = 3; nn < _nodes.Count; nn++)
            {
                var node = _nodes[nn];
                if (node == null || node.Callback != null) continue;

                int gnd = 0, pwr = 0, normal = 0;
                var gndTrans = new List<int>();
                var pwrTrans = new List<int>();
                foreach (int tid in node.C1c2s)
                {
                    if ((uint)tid >= (uint)_transistors.Count) continue;
                    var t = _transistors[tid];
                    int other = GetOtherEndpoint(t, nn);
                    if (other == Ngnd) { gnd++; gndTrans.Add(tid); }
                    else if (other == Npwr) { pwr++; pwrTrans.Add(tid); }
                    else if (other > Ngnd) normal++;
                }

                if (node.Pullups > 0 && gnd > 0)
                {
                    d.BroadNodes++;
                    foreach (int tid in gndTrans) d.BroadMacroTrans.Add(tid);
                    if (normal == 0 && pwr == 0)
                    {
                        d.StrictNodes++;
                        foreach (int tid in gndTrans) d.StrictTrans.Add(tid);
                    }
                }

                if (pwr > 0 && gnd > 0)
                {
                    d.PrechargeEvalNodes++;
                    foreach (int tid in gndTrans) d.PrechargeEvalTrans.Add(tid);
                    foreach (int tid in pwrTrans) d.PrechargeEvalTrans.Add(tid);
                }
            }
            return d;
        }

        private sealed class IslandStats
        {
            public int StrictComponents, StrictNodeCount, AggressiveComponents;
            public readonly HashSet<int> StrictTrans = new();
            public readonly HashSet<int> AggressiveNodes = new();
            public readonly HashSet<int> AggressiveTrans = new();
        }

        private sealed class CompInfo
        {
            public int Nodes;
            public bool Named, GateUse, Callback, Pullup, Force, RailTouch;
            public readonly HashSet<int> Trans = new();
            public readonly List<int> Members = new();
        }

        private static IslandStats CensusUnobservableIslands()
        {
            int n = _nodes.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            int Find(int x)
            {
                int r = x;
                while (parent[r] != r) r = parent[r];
                while (parent[x] != r) { int p = parent[x]; parent[x] = r; x = p; }
                return r;
            }

            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (a < b) parent[b] = a; else parent[a] = b;
            }

            foreach (var t in _transistors)
                if (t.C1 > Ngnd && t.C2 > Ngnd && _nodes[t.C1] != null && _nodes[t.C2] != null)
                    Union(t.C1, t.C2);

            var force = new HashSet<int>(_forceComputeList);
            var comps = new Dictionary<int, CompInfo>();
            CompInfo GetComp(int nn)
            {
                int r = Find(nn);
                if (!comps.TryGetValue(r, out var c)) comps[r] = c = new CompInfo();
                return c;
            }

            for (int nn = 3; nn < n; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                var c = GetComp(nn);
                c.Nodes++;
                c.Members.Add(nn);
                c.Named |= HasBuildName(nn);
                c.GateUse |= node.Gates.Count != 0;
                c.Callback |= node.Callback != null;
                c.Pullup |= node.Pullups != 0;
                c.Force |= force.Contains(nn);
            }

            for (int tid = 0; tid < _transistors.Count; tid++)
            {
                var t = _transistors[tid];
                int r1 = t.C1 > Ngnd && t.C1 < n && _nodes[t.C1] != null ? Find(t.C1) : EmptyNode;
                int r2 = t.C2 > Ngnd && t.C2 < n && _nodes[t.C2] != null ? Find(t.C2) : EmptyNode;
                if (r1 != EmptyNode && comps.TryGetValue(r1, out var c1)) c1.Trans.Add(tid);
                if (r2 != EmptyNode && r2 != r1 && comps.TryGetValue(r2, out var c2)) c2.Trans.Add(tid);
                if (r1 != EmptyNode && (t.C2 == Npwr || t.C2 == Ngnd) && comps.TryGetValue(r1, out c1)) c1.RailTouch = true;
                if (r2 != EmptyNode && (t.C1 == Npwr || t.C1 == Ngnd) && comps.TryGetValue(r2, out c2)) c2.RailTouch = true;
            }

            var stats = new IslandStats();
            foreach (var c in comps.Values)
            {
                bool strict = !c.Named && !c.GateUse && !c.Callback && !c.Pullup && !c.Force && !c.RailTouch;
                bool aggressive = !c.Named && !c.GateUse && !c.Callback && !c.Force;
                if (strict)
                {
                    stats.StrictComponents++;
                    stats.StrictNodeCount += c.Nodes;
                    foreach (int tid in c.Trans) stats.StrictTrans.Add(tid);
                }
                if (aggressive)
                {
                    stats.AggressiveComponents++;
                    foreach (int nn in c.Members) stats.AggressiveNodes.Add(nn);
                    foreach (int tid in c.Trans) stats.AggressiveTrans.Add(tid);
                }
            }
            return stats;
        }

        private static int GetOtherEndpoint(Transistor t, int nn) => t.C1 == nn ? t.C2 : t.C1;

        private static bool HasBuildName(int nn)
            => _nameByNode.ContainsKey(nn) || (_nodes[nn] != null && !string.IsNullOrEmpty(_nodes[nn]!.Name));

        private static bool ArePhaseExclusiveGates(int a, int b)
        {
            if (a <= Ngnd || b <= Ngnd || a >= _nodes.Count || b >= _nodes.Count) return false;
            string ka = PhaseKey(GetBuildName(a), out int pa);
            string kb = PhaseKey(GetBuildName(b), out int pb);
            return pa >= 0 && pb >= 0 && pa != pb && ka.Length != 0 && ka == kb;
        }

        private static string GetBuildName(int nn)
        {
            if (_nameByNode.TryGetValue(nn, out string? n)) return n;
            return nn > 0 && nn < _nodes.Count && _nodes[nn] != null ? _nodes[nn]!.Name : "";
        }

        private static string PhaseKey(string name, out int phase)
        {
            phase = -1;
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.ToLowerInvariant();
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot + 1 < s.Length) s = s[(dot + 1)..];
            s = s.TrimStart('#', '~', '/', '_', '+');

            if (s.Contains("pclk0")) { phase = 0; return s.Replace("pclk0", "pclk"); }
            if (s.Contains("pclk1")) { phase = 1; return s.Replace("pclk1", "pclk"); }
            if (s.Contains("clk0")) { phase = 0; return s.Replace("clk0", "clk"); }
            if (s.Contains("clk1")) { phase = 1; return s.Replace("clk1", "clk"); }
            if (s.Contains("phi1")) { phase = 0; return s.Replace("phi1", "phi"); }
            if (s.Contains("phi2")) { phase = 1; return s.Replace("phi2", "phi"); }
            return "";
        }
    }
}
