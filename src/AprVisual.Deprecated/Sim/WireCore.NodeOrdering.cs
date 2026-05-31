using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── G (math-algos Phase 1): Reverse Cuthill-McKee node-id reordering.
        //
        // Runs *after* LowerNetlist() in ComposeSystem(). Builds the undirected adjacency
        // graph "u ~ v iff ∃ transistor with both u and v among {gate, c1, c2}" — i.e. any
        // pair that ever appears together in the same recalc/group/fanout walk — then
        // permutes node ids so that adjacent nodes get adjacent ids. The downstream effect:
        // the hot-loop accesses (NodeStates[gate]/[other], NodeInfos[v], the bytes inside
        // _groupBuf / TransistorList that come from the permuted ids) hit more L1/L2 lines.
        //
        // Per Gemini's evaluation: "唯一必贏的純優化" — expected ~1.2-1.5× from cache.
        // Verification is via existing blargg tests (functional equivalence) plus the
        // before/after bandwidth statistic reported in LastRcmStats.
        //
        // Reserved ids (0, Npwr=1, Ngnd=2) are kept fixed; only normal nodes (id >= 3) are
        // permuted. Disable for A/B with WireCore.EnableRcm = false (CLI: --rcm to enable).
        public static bool EnableRcm = false;
        public static string LastRcmStats = "(RCM ordering not run)";

        public static void ApplyRcmOrdering()
        {
            int n = _nodes.Count;
            if (n < 4) { LastRcmStats = "RCM skipped (too few nodes)"; return; }

            // ── 1. undirected adjacency over normal nodes (gate-c1, gate-c2, c1-c2 each contribute) ──
            var adj = new HashSet<int>?[n];
            for (int i = 3; i < n; i++) if (_nodes[i] != null) adj[i] = new HashSet<int>();
            void AddEdge(int u, int v)
            {
                if (u < 3 || v < 3 || u == v) return;
                if ((uint)u >= (uint)n || (uint)v >= (uint)n) return;
                if (_nodes[u] == null || _nodes[v] == null) return;
                adj[u]!.Add(v); adj[v]!.Add(u);
            }
            foreach (var t in _transistors)
            {
                AddEdge(t.Gate, t.C1);
                AddEdge(t.Gate, t.C2);
                AddEdge(t.C1, t.C2);
            }

            // Original-layout bandwidth: max |C1 - C2| over channels (a proxy for cache stride)
            long bwOldSum = 0, bwOldMax = 0; int bwOldEdges = 0;
            foreach (var t in _transistors)
            {
                if (t.C1 < 3 || t.C2 < 3) continue;
                long d = Math.Abs((long)t.C1 - (long)t.C2);
                bwOldSum += d; if (d > bwOldMax) bwOldMax = d; bwOldEdges++;
            }
            double bwOldAvg = bwOldEdges > 0 ? (double)bwOldSum / bwOldEdges : 0;

            // ── 2. RCM: BFS over degree-ascending neighbours; restart on disconnected components; reverse ──
            var deg = new int[n];
            int normalNodeCount = 0;
            for (int i = 3; i < n; i++) if (adj[i] != null) { deg[i] = adj[i]!.Count; normalNodeCount++; }
            var visited = new bool[n];
            var order = new List<int>(normalNodeCount);

            while (true)
            {
                // Pick the unvisited node with minimum degree as the start of the next component
                int start = -1; int bestDeg = int.MaxValue;
                for (int i = 3; i < n; i++)
                {
                    if (visited[i] || _nodes[i] == null) continue;
                    int d = deg[i];
                    if (d < bestDeg) { bestDeg = d; start = i; if (d == 0) break; }
                }
                if (start < 0) break;

                var q = new Queue<int>();
                visited[start] = true; q.Enqueue(start); order.Add(start);
                while (q.Count > 0)
                {
                    int v = q.Dequeue();
                    if (adj[v] == null) continue;
                    // Collect unvisited neighbours, sort by ascending degree (RCM convention)
                    var nbrs = new List<int>(adj[v]!.Count);
                    foreach (int u in adj[v]!) if (!visited[u]) nbrs.Add(u);
                    nbrs.Sort((a, b) => deg[a].CompareTo(deg[b]));
                    foreach (int u in nbrs)
                    {
                        if (visited[u]) continue;
                        visited[u] = true;
                        order.Add(u);
                        q.Enqueue(u);
                    }
                }
            }
            order.Reverse();   // the "R" in "RCM"

            // ── 3. perm[oldId] = newId; supplies (0/Npwr/Ngnd) keep their ids ──
            var perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = EmptyNode;
            perm[0] = 0; perm[Npwr] = Npwr; perm[Ngnd] = Ngnd;
            int next = 3;
            foreach (int oldId in order) perm[oldId] = next++;
            int totalNew = next;

            // ── 4. apply: rebuild _nodes, _transistors, name maps, _forceComputeList, _transistorSet ──
            var newNodes = new List<Node?>(totalNew);
            for (int i = 0; i < totalNew; i++) newNodes.Add(null);
            newNodes[Npwr] = _nodes[Npwr];
            newNodes[Ngnd] = _nodes[Ngnd];
            for (int oldId = 3; oldId < n; oldId++)
            {
                var on = _nodes[oldId]; if (on == null) continue;
                int nid = perm[oldId]; if (nid == EmptyNode) continue;
                on.Id = nid;
                on.Gates.Clear(); on.C1c2s.Clear();   // rebuilt below as transistor list is replayed
                newNodes[nid] = on;
            }
            // Npwr / Ngnd's Gates/C1c2s also need rebuilding from the permuted transistor list
            if (newNodes[Npwr] != null) { newNodes[Npwr]!.Gates.Clear(); newNodes[Npwr]!.C1c2s.Clear(); }
            if (newNodes[Ngnd] != null) { newNodes[Ngnd]!.Gates.Clear(); newNodes[Ngnd]!.C1c2s.Clear(); }

            var newTrans = new List<Transistor>(_transistors.Count);
            foreach (var t in _transistors)
            {
                int g = perm[t.Gate], c1 = perm[t.C1], c2 = perm[t.C2];
                int idx = newTrans.Count;
                newTrans.Add(new Transistor { Gate = g, C1 = c1, C2 = c2, IsWeak = t.IsWeak, Name = t.Name });
                if (g != EmptyNode && newNodes[g] != null) newNodes[g]!.Gates.Add(idx);
                if (c1 != EmptyNode && newNodes[c1] != null) newNodes[c1]!.C1c2s.Add(idx);
                if (c2 != EmptyNode && newNodes[c2] != null) newNodes[c2]!.C1c2s.Add(idx);
            }

            _nodes.Clear(); _nodes.AddRange(newNodes);
            _transistors.Clear(); _transistors.AddRange(newTrans);
            _transistorSet.Clear();
            foreach (var t in newTrans) _transistorSet.Add((t.Gate, t.C1, t.C2));

            // forceComputeList: remap + dedupe
            var fcOld = new List<int>(_forceComputeList); _forceComputeList.Clear();
            var fcSeen = new HashSet<int>();
            foreach (int oldId in fcOld) { int m = oldId == EmptyNode ? EmptyNode : perm[oldId]; if (m != EmptyNode && fcSeen.Add(m)) _forceComputeList.Add(m); }

            // name maps: remap value side, then rebuild _nameByNode from _nodes for completeness
            var rebuiltByName = new Dictionary<string, int>(_nodeByName.Count, StringComparer.Ordinal);
            foreach (var kv in _nodeByName) { int m = kv.Value == EmptyNode ? EmptyNode : perm[kv.Value]; if (m != EmptyNode) rebuiltByName[kv.Key] = m; }
            _nodeByName.Clear(); foreach (var kv in rebuiltByName) _nodeByName[kv.Key] = kv.Value;

            _nameByNode.Clear();
            _nameByNode[Npwr] = "vcc"; _nameByNode[Ngnd] = "vss";
            for (int i = 3; i < _nodes.Count; i++) if (_nodes[i] != null && !string.IsNullOrEmpty(_nodes[i]!.Name)) _nameByNode[i] = _nodes[i]!.Name;
            foreach (var kv in _nodeByName) if (!_nameByNode.ContainsKey(kv.Value)) _nameByNode[kv.Value] = kv.Key;

            _maxNodeId = _nodes.Count - 1;
            if (ClockNode != EmptyNode) ClockNode = perm[ClockNode];

            // ── post-RCM bandwidth (same metric as bwOld) ──
            long bwNewSum = 0, bwNewMax = 0; int bwNewEdges = 0;
            foreach (var t in newTrans)
            {
                if (t.C1 < 3 || t.C2 < 3) continue;
                long d = Math.Abs((long)t.C1 - (long)t.C2);
                bwNewSum += d; if (d > bwNewMax) bwNewMax = d; bwNewEdges++;
            }
            double bwNewAvg = bwNewEdges > 0 ? (double)bwNewSum / bwNewEdges : 0;

            LastRcmStats =
                $"RCM ordering: {order.Count} normal nodes reordered; " +
                $"channel-edge bandwidth |C1-C2|: avg {bwOldAvg:N0} -> {bwNewAvg:N0} ({100.0 * (bwOldAvg - bwNewAvg) / Math.Max(1, bwOldAvg):F1}% reduction), " +
                $"max {bwOldMax:N0} -> {bwNewMax:N0}";
        }
    }
}
