using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        public static bool EnableAggressiveLowering;
        public static string LastAggressiveLowerStats = "(aggressive lowering disabled)";

        public static void AggressiveLowerNetlist()
        {
            int oldLive = 0;
            for (int i = 0; i < _nodes.Count; i++) if (_nodes[i] != null) oldLive++;
            int oldTrans = _transistors.Count;

            var constMask = BuildAggressiveConstMask();
            var force = new HashSet<int>(_forceComputeList);
            var protectedNode = BuildMemoryLikeProtectionMask(force);
            var drop = new bool[_nodes.Count];
            var skipTrans = new bool[_transistors.Count];
            var replacements = new List<Transistor>();

            MarkAggressiveSeriesMidpoints(constMask, force, protectedNode, drop, skipTrans, replacements);
            MarkAggressiveIslands(force, protectedNode, drop);

            var remap = new int[_nodes.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = EmptyNode;
            remap[0] = 0;
            remap[Npwr] = Npwr;
            remap[Ngnd] = Ngnd;

            int foldedLow = 0, foldedHigh = 0, droppedNodes = 0;
            int next = 3;
            for (int old = 3; old < _nodes.Count; old++)
            {
                if (_nodes[old] == null) continue;
                byte cm = (byte)(constMask[old] & 3);
                if (!protectedNode[old])
                {
                    if (cm == 1) { remap[old] = Ngnd; foldedLow++; continue; }
                    if (cm == 2) { remap[old] = Npwr; foldedHigh++; continue; }
                }
                if (drop[old]) { droppedNodes++; continue; }
                remap[old] = next++;
            }
            int protectedCount = 0;
            for (int i = 3; i < protectedNode.Length; i++) if (protectedNode[i]) protectedCount++;

            var newNodes = new List<Node?>(next);
            for (int i = 0; i < next; i++) newNodes.Add(null);
            newNodes[Npwr] = new Node { Id = Npwr, Name = "vcc" };
            newNodes[Ngnd] = new Node { Id = Ngnd, Name = "vss" };
            for (int old = 3; old < _nodes.Count; old++)
            {
                var on = _nodes[old];
                if (on == null) continue;
                int nid = remap[old];
                if (nid <= Ngnd) continue;
                var nn = newNodes[nid] ??= new Node { Id = nid, Name = on.Name };
                nn.Pullups += on.Pullups;
                if (on.Callback != null) nn.Callback = on.Callback;
                int cap = on.CapacityOverride >= 0 ? on.CapacityOverride : on.C1c2s.Count + on.Gates.Count;
                if (cap > nn.CapacityOverride) nn.CapacityOverride = cap;
                if (string.IsNullOrEmpty(nn.Name)) nn.Name = on.Name;
            }

            var newTrans = new List<Transistor>(oldTrans);
            var seen = new HashSet<(int, int, int)>();

            void AddMappedTrans(Transistor t)
            {
                int g = MapNode(t.Gate), c1 = MapNode(t.C1), c2 = MapNode(t.C2);
                if (g == EmptyNode || c1 == EmptyNode || c2 == EmptyNode) return;
                if (g == Ngnd) return;
                if (c1 == c2) return;
                if (IsPwrGnd(c1) && IsPwrGnd(c2)) return;
                if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);
                if (!seen.Add((g, c1, c2))) return;
                int idx = newTrans.Count;
                newTrans.Add(new Transistor { Gate = g, C1 = c1, C2 = c2, IsWeak = t.IsWeak, Name = t.Name });
                newNodes[g]!.Gates.Add(idx);
                newNodes[c1]!.C1c2s.Add(idx);
                newNodes[c2]!.C1c2s.Add(idx);
            }

            int MapNode(int n) => n >= 0 && n < remap.Length ? remap[n] : EmptyNode;

            for (int i = 0; i < _transistors.Count; i++)
            {
                if (skipTrans[i]) continue;
                AddMappedTrans(_transistors[i]);
            }
            foreach (var t in replacements) AddMappedTrans(t);

            _nodes.Clear(); _nodes.AddRange(newNodes);
            _transistors.Clear(); _transistors.AddRange(newTrans);
            _transistorSet.Clear(); _transistorSet.UnionWith(seen);

            var fcOld = new List<int>(_forceComputeList);
            _forceComputeList.Clear();
            var fcSeen = new HashSet<int>();
            foreach (int n in fcOld)
            {
                int m = n == EmptyNode ? EmptyNode : MapNode(n);
                if (m != EmptyNode && fcSeen.Add(m)) _forceComputeList.Add(m);
            }

            var rebuiltByName = new Dictionary<string, int>(_nodeByName.Count, StringComparer.Ordinal);
            foreach (var kv in _nodeByName)
            {
                int m = kv.Value == EmptyNode ? EmptyNode : MapNode(kv.Value);
                if (m != EmptyNode) rebuiltByName[kv.Key] = m;
            }
            _nodeByName.Clear();
            foreach (var kv in rebuiltByName) _nodeByName[kv.Key] = kv.Value;

            _nameByNode.Clear();
            _nameByNode[Npwr] = "vcc";
            _nameByNode[Ngnd] = "vss";
            for (int i = 3; i < _nodes.Count; i++)
                if (_nodes[i] != null && !string.IsNullOrEmpty(_nodes[i]!.Name))
                    _nameByNode[i] = _nodes[i]!.Name;
            foreach (var kv in _nodeByName)
                if (!_nameByNode.ContainsKey(kv.Value))
                    _nameByNode[kv.Value] = kv.Key;

            _maxNodeId = _nodes.Count - 1;
            if (ClockNode != EmptyNode) ClockNode = MapNode(ClockNode);

            int newLive = 0;
            for (int i = 0; i < _nodes.Count; i++) if (_nodes[i] != null) newLive++;
            LastAggressiveLowerStats =
                $"aggressive-lower(memory-protected): nodes {oldLive:N0} -> {newLive:N0} " +
                $"(fold low {foldedLow:N0}, high {foldedHigh:N0}, drop {droppedNodes:N0}); " +
                $"transistors {oldTrans:N0} -> {newTrans.Count:N0} " +
                $"(delta {oldTrans - newTrans.Count:N0}, series replacements {replacements.Count:N0}, protected {protectedCount:N0})";
        }

        private static bool[] BuildMemoryLikeProtectionMask(HashSet<int> force)
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

            var taintedRoot = new bool[n];
            for (int nn = 3; nn < n; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                bool memoryLike = node.Pullups == 0
                               || node.Callback != null
                               || force.Contains(nn)
                               || HasBuildName(nn);
                if (memoryLike) taintedRoot[Find(nn)] = true;
            }

            var protectedNode = new bool[n];
            for (int nn = 3; nn < n; nn++)
                if (_nodes[nn] != null && taintedRoot[Find(nn)])
                    protectedNode[nn] = true;
            return protectedNode;
        }

        private static void MarkAggressiveSeriesMidpoints(
            byte[] constMask,
            HashSet<int> force,
            bool[] protectedNode,
            bool[] drop,
            bool[] skipTrans,
            List<Transistor> replacements)
        {
            for (int nn = 3; nn < _nodes.Count; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                if ((constMask[nn] & 3) != 0) continue;
                if (protectedNode[nn]) continue;
                if (force.Contains(nn)) continue;
                if (HasBuildName(nn)) continue;
                if (node.Pullups != 0 || node.Callback != null || node.Gates.Count != 0 || node.C1c2s.Count != 2) continue;

                int t0id = node.C1c2s[0], t1id = node.C1c2s[1];
                if ((uint)t0id >= (uint)_transistors.Count || (uint)t1id >= (uint)_transistors.Count) continue;
                var t0 = _transistors[t0id];
                var t1 = _transistors[t1id];
                int e0 = GetOtherEndpoint(t0, nn);
                int e1 = GetOtherEndpoint(t1, nn);
                if (e0 == e1) continue;

                int gate = ChooseAggressiveSeriesGate(t0.Gate, t1.Gate);
                if (gate == EmptyNode) continue;

                drop[nn] = true;
                skipTrans[t0id] = true;
                skipTrans[t1id] = true;
                replacements.Add(new Transistor { Gate = gate, C1 = e0, C2 = e1, IsWeak = t0.IsWeak || t1.IsWeak, Name = "aggr-series" });
            }
        }

        private static int ChooseAggressiveSeriesGate(int g0, int g1)
        {
            if (g0 == g1) return g0;
            if (g0 == Npwr) return g1;
            if (g1 == Npwr) return g0;
            if (g0 == Ngnd || g1 == Ngnd) return Ngnd;
            return g0; // destructive over-approximation: use one side of the original AND stack.
        }

        private static void MarkAggressiveIslands(HashSet<int> force, bool[] protectedNode, bool[] drop)
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

            var roots = new Dictionary<int, List<int>>();
            var bad = new HashSet<int>();
            for (int nn = 3; nn < n; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                int r = Find(nn);
                if (!roots.TryGetValue(r, out var list)) roots[r] = list = new List<int>();
                list.Add(nn);
                if (HasBuildName(nn) || node.Gates.Count != 0 || node.Callback != null || force.Contains(nn))
                    bad.Add(r);
                if (protectedNode[nn])
                    bad.Add(r);
            }

            foreach (var kv in roots)
            {
                if (bad.Contains(kv.Key)) continue;
                foreach (int nn in kv.Value) drop[nn] = true;
            }
        }
    }
}
