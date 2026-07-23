using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── S1.5 "lowering" — collapse the parsed switch-level netlist into a canonical, compacted
        //    intermediate form. Runs at the end of ComposeSystem (after AddInstance, before the
        //    behavioral handlers and Reset()); later it's also the input S2 extracts logic from.
        //
        //    Behaviour-preserving transformations:
        //      1. static-group merge — two nodes joined ONLY by an always-on, non-weak transistor
        //         (gate == Npwr: AddConnection's "= a transistor with gate Npwr", plus any literal
        //         gate==vcc device in the netlist, with both ends being normal nodes) are
        //         unconditionally shorted ⇒ the same node forever. Union-find; fold each class onto
        //         one representative. The merged node's "capacitance" (the floating-group tie-break
        //         weight, NodeInfo.Connections) is kept = max over the class, which is exactly what
        //         the tie-break would have picked among the class members — so even that corner case
        //         is bit-identical to the un-lowered model.
        //      2. dead transistors — gate == Ngnd can never conduct ⇒ drop. (Reset() already skipped
        //         these when flattening; doing it here keeps the canonical netlist itself clean.)
        //      3. compaction — renumber the survivors into a dense 0..N range (0 reserved, 1==Npwr,
        //         2==Ngnd kept fixed); this drops the consumed connections (post-merge self-loops,
        //         c1==c2) and re-dedupes (gate, c1, c2). Rebuilds the name maps / forceCompute list.
        //
        //    Not (yet) merged: a normal node tied to a supply by an always-on device (a "connection
        //    to vcc/vss") — left as-is with its TlistC1pwr / TlistC1gnd entry; only normal↔normal
        //    shorts are collapsed in this version.
        //
        //    Disable for an A/B comparison with WireCore.EnableLowering = false (CLI: --no-lower).

        public static bool EnableLowering = true;
        public static string LastLowerStats = "(lowering not run)";

        public static void LowerNetlist()
        {
            int nOld = _nodes.Count;
            int tOld = _transistors.Count;
            int oldNonNull = 0; for (int i = 0; i < nOld; i++) if (_nodes[i] != null) oldNonNull++;

            // ── 1. union-find over always-on, non-weak, normal↔normal transistors ──
            var uf = new int[nOld];
            for (int i = 0; i < nOld; i++) uf[i] = i;
            int Find(int x)
            {
                int r = x; while (uf[r] != r) r = uf[r];
                while (uf[x] != r) { int n = uf[x]; uf[x] = r; x = n; }
                return r;
            }
            void Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return;
                if (a < b) uf[b] = a; else uf[a] = b;   // smaller id is the representative (keeps 1/2 as roots, deterministic)
            }
            foreach (var t in _transistors)
            {
                if (t.Gate != Npwr || t.IsWeak) continue;          // not an unconditional short
                if (t.C1 <= Ngnd || t.C2 <= Ngnd) continue;        // touches a supply / reserved id — leave it
                if (_nodes[t.C1] == null || _nodes[t.C2] == null) continue;
                Union(t.C1, t.C2);
            }

            // ── 2. dense new ids: 0 reserved, 1==Npwr, 2==Ngnd; reps get 3.. in ascending old-id order ──
            var remap = new int[nOld];
            for (int i = 0; i < nOld; i++) remap[i] = EmptyNode;
            remap[0] = 0; remap[Npwr] = Npwr; remap[Ngnd] = Ngnd;
            int next = 3;
            for (int old = 3; old < nOld; old++)
            {
                if (_nodes[old] == null) continue;
                if (Find(old) == old) remap[old] = next++;          // representative
            }
            for (int old = 3; old < nOld; old++)
            {
                if (_nodes[old] == null) continue;
                if (Find(old) != old) remap[old] = remap[Find(old)]; // class member → rep's new id
            }
            int nNew = next;

            // ── new node table: merge pull-ups / callbacks; CapacityOverride = max class capacitance ──
            var newNodes = new List<Node?>(nNew);
            for (int i = 0; i < nNew; i++) newNodes.Add(null);
            newNodes[Npwr] = new Node { Id = Npwr, Name = "vcc" };
            newNodes[Ngnd] = new Node { Id = Ngnd, Name = "vss" };
            for (int old = 3; old < nOld; old++)
            {
                var on = _nodes[old];
                if (on == null) continue;
                int nid = remap[old];
                var nn = newNodes[nid] ??= new Node { Id = nid, Name = on.Name };
                nn.Pullups += on.Pullups;
                nn.IsFlipFlop |= on.IsFlipFlop;
                if (on.Callback != null) nn.Callback = on.Callback;       // (no callbacks attached at this stage, but be correct)
                int cap = on.C1c2s.Count + on.Gates.Count;               // pre-lowering "capacitance" = what the old tie-break used
                if (cap > nn.CapacityOverride) nn.CapacityOverride = cap;
                if (string.IsNullOrEmpty(nn.Name)) nn.Name = on.Name;
            }

            // ── 3. rebuild transistors: remap, drop self-loops (consumed connections) & gate==Ngnd, re-dedupe ──
            var newTrans = new List<Transistor>(tOld);
            var seen = new HashSet<(int Gate, int C1, int C2, bool ActiveLow)>();
            foreach (var t in _transistors)
            {
                int g = remap[t.Gate], c1 = remap[t.C1], c2 = remap[t.C2];
                if (g == EmptyNode || c1 == EmptyNode || c2 == EmptyNode) continue;   // (shouldn't happen)
                if (g == Ngnd) continue;                                              // dead — gate tied to GND, never conducts
                if (c1 == c2) continue;                                               // self-loop — a consumed always-on connection
                if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);                                // normalise supply onto c2 (matches AddTransistor)
                if (!seen.Add((g, c1, c2, t.ActiveLow))) continue;                    // dedupe (gate, c1, c2, polarity)
                int idx = newTrans.Count;
                newTrans.Add(new Transistor { Gate = g, C1 = c1, C2 = c2, IsWeak = t.IsWeak, ActiveLow = t.ActiveLow, Name = t.Name });
                newNodes[g]!.Gates.Add(idx);
                newNodes[c1]!.C1c2s.Add(idx);
                newNodes[c2]!.C1c2s.Add(idx);
            }

            // ── swap everything in ──
            _nodes.Clear(); _nodes.AddRange(newNodes);
            _transistors.Clear(); _transistors.AddRange(newTrans);
            _transistorSet.Clear(); _transistorSet.UnionWith(seen);

            var fcOld = new List<int>(_forceComputeList);
            _forceComputeList.Clear();
            var fcSeen = new HashSet<int>();
            foreach (int n in fcOld) { int m = n == EmptyNode ? EmptyNode : remap[n]; if (m != EmptyNode && fcSeen.Add(m)) _forceComputeList.Add(m); }

            var rebuiltByName = new Dictionary<string, int>(_nodeByName.Count, StringComparer.Ordinal);
            foreach (var kv in _nodeByName) { int m = kv.Value == EmptyNode ? EmptyNode : remap[kv.Value]; if (m != EmptyNode) rebuiltByName[kv.Key] = m; }
            _nodeByName.Clear(); foreach (var kv in rebuiltByName) _nodeByName[kv.Key] = kv.Value;

            _nameByNode.Clear();
            _nameByNode[Npwr] = "vcc"; _nameByNode[Ngnd] = "vss";
            for (int i = 3; i < _nodes.Count; i++) if (_nodes[i] != null && !string.IsNullOrEmpty(_nodes[i]!.Name)) _nameByNode[i] = _nodes[i]!.Name;
            foreach (var kv in _nodeByName) if (!_nameByNode.ContainsKey(kv.Value)) _nameByNode[kv.Value] = kv.Key;   // any id that ended up nameless

            _maxNodeId = _nodes.Count - 1;
            if (ClockNode != EmptyNode) ClockNode = remap[ClockNode];   // normally EmptyNode at this stage

            int newNonNull = nNew - 1;   // slots 1,2 + 3..nNew-1 are non-null; slot 0 is null
            LastLowerStats =
                $"lowering: nodes {oldNonNull} -> {newNonNull} (merged {oldNonNull - newNonNull}); " +
                $"transistors {tOld} -> {newTrans.Count} (dropped {tOld - newTrans.Count}: connections + dead gate==vss + dups)";
        }

    }
}
