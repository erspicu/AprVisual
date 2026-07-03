using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Module composition / instantiation — port of ref/metalnes-main wire_module.cpp:
        //      Wires::addInstance / setupNodes / setupPins / setupMemory / setupSegments /
        //      setupTransistors / addNode / addTransistor / addConnection (~L892-1340)
        //      + wire_node_resolver.cpp (name → id + array / wildcard / | expansion).
        //    See MD/note/02_模組化網表系統.md §3.
        //
        //    Key tricks (kept from MetalNES):
        //      - "connection" between two nodes = a transistor with gate = Npwr (always ON), c1/c2 =
        //        the two nodes. Reuses the group machinery; no destructive node merge.
        //      - each *new* instance prefix gets a contiguous, aligned slice of the global node-id
        //        space; local ids are offset (vss/vcc fold to Ngnd/Npwr); names registered as
        //        "prefix.localName". Re-instantiating the same prefix reuses it (only re-runs the
        //        sub-module / connection recursion) — that's how the cartridge layers compose.
        //      - "func<clock>" / "func<rom>" / "func<ram>" / "func<video_out>" / "func<audio_out>"
        //        hook nodes — handlers find them via "*func<...>" wildcard (Step 6).

        // ── build-time per-node record (the hot path uses the flattened WireCore.TransistorList) ──
        internal sealed class Node
        {
            public int Id;
            public string Name = "";
            public int Pullups;                 // segdef '+' / pullups:[] count
            public readonly List<int> Gates  = new();   // transistor indices this node gates
            public readonly List<int> C1c2s  = new();   // transistor indices this node is a channel end of
            public CallbackInfo? Callback;      // set by AddCallback (Step 6)
            public int CapacityOverride = -1;   // -1 = use C1c2s.Count+Gates.Count; ≥0 = explicit "capacitance" (set by LowerNetlist on merged nodes)
        }

        // build-time tables (consumed by WireCore.Reset() in Step 3 to fill the unmanaged hot arrays)
        private static readonly List<Node?> _nodes = new();             // indexed by global node id
        private static readonly List<Transistor> _transistors = new();
        private static readonly HashSet<(int, int, int)> _transistorSet = new();   // dedup by (gate, c1, c2)
        private static readonly List<int> _forceComputeList = new();
        private static readonly HashSet<string> _instancesSetUp = new(StringComparer.Ordinal);

        internal static IReadOnlyList<Node?> Nodes => _nodes;
        internal static IReadOnlyList<Transistor> Transistors => _transistors;
        internal static IReadOnlyList<int> ForceComputeList => _forceComputeList;
        internal static int NodeArrayCount => _nodes.Count;
        internal static int TransistorBuildCount => _transistors.Count;
        internal static int NonNullNodeCount { get { int c = 0; foreach (var n in _nodes) if (n != null) c++; return c; } }
        internal static int PullUpNodeCount  { get { int c = 0; foreach (var n in _nodes) if (n != null && n.Pullups > 0) c++; return c; } }
        internal static int ConnectionTransistorCount { get { int c = 0; foreach (var t in _transistors) if (t.Gate == Npwr) c++; return c; } }

        // name <-> id (name is unique; id may have several names — aliases, like #<pin>)
        private static readonly Dictionary<string, int> _nodeByName = new(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> _nameByNode = new();
        private static int _maxNodeId = Ngnd;

        public static int LookupNode(string name) => _nodeByName.TryGetValue(name, out int id) ? id : EmptyNode;
        public static string GetNodeName(int id) => _nameByNode.TryGetValue(id, out string? n) ? n : (id == Npwr ? "vcc" : id == Ngnd ? "vss" : id.ToString());
        public static bool IsPwrGnd(int nn) => nn == Npwr || nn == Ngnd;

        /// <summary>When set BEFORE LoadSystem, AddInstance also registers "&lt;inst&gt;.#&lt;rawId&gt;"
        /// name aliases for every raw netlist node id (probe access to unnamed nodes). Diagnostic
        /// only — default off; does not create nodes the build wouldn't create.</summary>
        public static bool RegisterRawIdAliases = false;

        /// <summary>Drop build-time data that the simulation hot path doesn't read. Called from
        /// LoadSystem() after Reset() has populated the unmanaged arrays. Keeps the _nodeByName
        /// / _nameByNode maps and the Node[] shell (for LookupNode / probe-style diags), but
        /// frees the large per-node Gates / C1c2s lists, the full transistor list, the build
        /// dedup hash, and the parsed JSON ModuleDefs.</summary>
        public static void ClearPostLoadBuildState()
        {
            // Cleared because: already flattened into TransistorList + NodeInfos + NodeTlistGates.
            for (int i = 0; i < _nodes.Count; i++)
            {
                var n = _nodes[i];
                if (n == null) continue;
                n.Gates.Clear(); n.Gates.TrimExcess();
                n.C1c2s.Clear(); n.C1c2s.TrimExcess();
            }
            _transistors.Clear(); _transistors.TrimExcess();
            _transistorSet.Clear(); _transistorSet.TrimExcess();
            _forceComputeList.Clear(); _forceComputeList.TrimExcess();
            // Parsed JSON module defs (biggest single allocator, ~20-50 MB).
            ClearLoadedDefs();
            // Hint a Gen2 collection so the cleared memory actually returns to the OS;
            // happens once at LoadSystem, has no bench-time cost.
            System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }

        /// <summary>Bench-only final sweep: release the LAST residual build-time managed data the hot
        /// loop never reads — the name↔id maps (LookupNode/GetNodeName are setup/diag-only) and the
        /// gutted Node shells. ClearPostLoadBuildState (in LoadSystem) already freed the big lists/JSON;
        /// this trims the remainder so the timed loop starts with the minimum managed heap. NOT called
        /// on the --test/--frames/trace paths, which may still resolve node names after load. The hot
        /// path reads only the unmanaged arrays, so this is timing hygiene, not a throughput change.</summary>
        public static void ReleaseBenchResidualState()
        {
            _nodeByName.Clear(); _nodeByName.TrimExcess();
            _nameByNode.Clear(); _nameByNode.TrimExcess();
            for (int i = 0; i < _nodes.Count; i++) _nodes[i] = null;   // drop Node shells (RecomputeAllNodes already ran at power-on)
            _nodes.Clear(); _nodes.TrimExcess();
        }

        /// <summary>Allocate <paramref name="count"/> fresh node ids, aligned to <paramref name="alignment"/>. Port of node_resolver::allocNodes.</summary>
        public static int AllocNodes(int alignment, int count)
        {
            int start = _maxNodeId + 1;
            while (alignment > 1 && (start % alignment) != 0) start++;
            _maxNodeId = start + count - 1;
            return start;
        }

        // ── reset all build-time state (called at the start of ComposeSystem / any fresh build) ──
        public static void ResetBuild()
        {
            ClearLoadedDefs();
            InstanceRanges.Clear();   // append-only in AddInstance — without this it accumulates across every compose (2x per two-phase load, more across --test-dir)
            _nodes.Clear();
            _transistors.Clear();
            _transistorSet.Clear();
            _forceComputeList.Clear();
            _instancesSetUp.Clear();
            _nodeByName.Clear();
            _nameByNode.Clear();
            _memories.Clear();   // declared in WireCore.Handlers.cs
            ResetHandlers();     // clears _callbacks / handler arrays (WireCore.Handlers.cs)
            ClockNode = EmptyNode;
            _maxNodeId = Ngnd;
            AddNode(Npwr, "vcc");
            AddNode(Ngnd, "vss");
        }

        private static Node? GetOrCreateNode(int nn)
        {
            if (nn == EmptyNode) return null;
            while (_nodes.Count <= nn) _nodes.Add(null);
            return _nodes[nn] ??= new Node { Id = nn };
        }

        /// <summary>Register a (name → id) mapping (and id → name, first wins) and ensure the node exists.</summary>
        public static void AddNode(int nn, string name)
        {
            var node = GetOrCreateNode(nn);
            if (node == null) return;
            _maxNodeId = Math.Max(_maxNodeId, nn);

            if (_nodeByName.TryGetValue(name, out int existing))
            {
                if (existing != nn) Console.Error.WriteLine($"node name '{name}' already maps to {existing}, not {nn} — keeping {existing}");
            }
            else _nodeByName[name] = nn;

            if (string.IsNullOrEmpty(node.Name)) node.Name = name;
            if (!_nameByNode.ContainsKey(nn)) _nameByNode[nn] = name;
        }

        /// <summary>Allocate a fresh node with the given name (for callback target nodes etc.). Port of Wires::addNode(name).</summary>
        public static int AddNamedNode(string name)
        {
            int nn = AllocNodes(1, 1);
            AddNode(nn, name);
            return nn;
        }

        public static void AddTransistor(string name, int gate, int c1, int c2, bool isWeak = false)
        {
            if (gate == EmptyNode || c1 == EmptyNode || c2 == EmptyNode) return;
            if (c1 == c2) return;
            if (IsPwrGnd(c1)) (c1, c2) = (c2, c1);   // normalise supply onto c2

            var key = (gate, c1, c2);
            if (!_transistorSet.Add(key)) return;     // dedup by (gate, c1, c2)

            int i = _transistors.Count;
            _transistors.Add(new Transistor { Gate = gate, C1 = c1, C2 = c2, IsWeak = isWeak, Name = name });
            GetOrCreateNode(gate)!.Gates.Add(i);
            GetOrCreateNode(c1)!.C1c2s.Add(i);
            GetOrCreateNode(c2)!.C1c2s.Add(i);
        }

        /// <summary>Connect two nodes = an always-ON transistor (gate = Npwr). Port of Wires::addConnection(id,id).</summary>
        public static void AddConnection(int from, int to)
        {
            if (from == to) return;
            if (IsPwrGnd(from)) (from, to) = (to, from);
            AddTransistor($"{GetNodeName(from)}<>{GetNodeName(to)}", Npwr, from, to);
        }

        /// <summary>Connect two node *expressions* (one-to-many if the left resolves to one node). Port of Wires::addConnection(str,str).</summary>
        public static void AddConnection(string fromExpr, string toExpr)
        {
            var fromList = new List<int>();
            var toList = new List<int>();
            ResolveNodes(fromExpr, fromList);
            ResolveNodes(toExpr, toList);

            if (fromList.Count == 1 && toList.Count > 0)
            {
                foreach (int t in toList) AddConnection(fromList[0], t);
            }
            else if (fromList.Count == toList.Count && fromList.Count > 0)
            {
                for (int i = 0; i < fromList.Count; i++) AddConnection(fromList[i], toList[i]);
            }
            else
            {
                Console.Error.WriteLine($"connection failed: '{fromExpr}' -> '{toExpr}'  ({fromList.Count} vs {toList.Count})");
            }
        }

        /// <summary>Combine an instance prefix with a (possibly empty) local name. Port of combinePrefix.</summary>
        public static string CombinePrefix(string prefix, string name)
        {
            if (prefix.Length == 0) return name;
            if (name.Length == 0) return prefix;
            if (prefix[^1] == '.') return prefix + name;
            return prefix + "." + name;
        }

        private static int MaxNodeIdOf(ModuleDef def)
        {
            int m = 0;
            foreach (int id in def.NodeNames.Values) if (id > m) m = id;
            foreach (var sd in def.Segs) if (!sd.Node.IsName && sd.Node.Id > m) m = sd.Node.Id;
            foreach (var td in def.Trans)
            {
                if (!td.Gate.IsName && td.Gate.Id > m) m = td.Gate.Id;
                if (!td.C1.IsName && td.C1.Id > m) m = td.C1.Id;
                if (!td.C2.IsName && td.C2.Id > m) m = td.C2.Id;
            }
            return m;
        }

        /// <summary>
        /// Instantiate <paramref name="def"/> under <paramref name="prefix"/>: a *new* prefix allocates a
        /// node-id slice and sets up its nodes / pins / memory / segments / transistors; then (always)
        /// recurse into sub-modules, apply connections, and collect forceCompute nodes.
        /// Port of Wires::addInstance.
        /// </summary>
        // Per-instance node-id range — recorded so the chip-diag (and any future per-chip strategy)
        // can correctly attribute UNNAMED internal nodes (most of a transistor netlist) to their
        // owning module instance. Populated as a side-effect of the AllocNodes call below.
        public static System.Collections.Generic.List<(int Start, int End, string Prefix)> InstanceRanges = new();

        public static void AddInstance(ModuleDef def, string prefix)
        {
            if (_instancesSetUp.Add(prefix))
            {
                int maxNode = MaxNodeIdOf(def);
                int alignment = maxNode < 100 ? 100 : 1000;
                int nodeStart = AllocNodes(alignment, maxNode + 1);
                InstanceRanges.Add((nodeStart, nodeStart + maxNode + 1, prefix));
                int nodeGnd = def.NodeNames.GetValueOrDefault("vss", EmptyNode);
                int nodePwr = def.NodeNames.GetValueOrDefault("vcc", EmptyNode);

                int Remap(int local) =>
                    local == EmptyNode ? EmptyNode :
                    local == nodeGnd   ? Ngnd :
                    local == nodePwr   ? Npwr :
                    nodeStart + local;
                int ResolveRef(NodeRef r) => r.IsName ? LookupNode(CombinePrefix(prefix, r.Name!)) : Remap(r.Id);

                // setupNodes
                foreach (var (name, id) in def.NodeNames) AddNode(Remap(id), CombinePrefix(prefix, name));

                // Optional diagnostic aliases: register "<inst>.#<rawId>" for every raw netlist node id
                // so probe instruments (--bus-trace) can watch UNNAMED nodes. Only ids the normal build
                // would create anyway (guards mirror AddTransistor's), so NodeCount / checksum are
                // untouched. Off by default; enabled by TestRunner before LoadSystem.
                if (RegisterRawIdAliases)
                {
                    var rawIds = new HashSet<int>(def.NodeNames.Values);
                    foreach (var sd in def.Segs) if (!sd.Node.IsName) rawIds.Add(sd.Node.Id);
                    foreach (var td in def.Trans)
                    {
                        if (td.Gate.IsName || td.C1.IsName || td.C2.IsName) continue;
                        if (td.Gate.Id == EmptyNode || td.C1.Id == EmptyNode || td.C2.Id == EmptyNode) continue;
                        if (td.C1.Id == td.C2.Id) continue;
                        rawIds.Add(td.Gate.Id); rawIds.Add(td.C1.Id); rawIds.Add(td.C2.Id);
                    }
                    foreach (int id in rawIds)
                    {
                        int rn = Remap(id);
                        if (rn == EmptyNode || rn == Ngnd || rn == Npwr) continue;
                        AddNode(rn, CombinePrefix(prefix, "#" + id));
                    }
                }

                // setupPins — add a "#<pin>" alias for each pin's node (documentation / layout; not load-bearing)
                foreach (var pd in def.Pins)
                {
                    int pinNode = LookupNode(CombinePrefix(prefix, pd.Name));
                    if (pinNode != EmptyNode) AddNode(pinNode, CombinePrefix(prefix, "#" + pd.Pin));
                    else Console.Error.WriteLine($"missing pin '{pd.Name}' (#{pd.Pin}) in instance '{prefix}' ({def.Name})");
                }

                // setupMemory — behavioral RAM/ROM regions (not transistors)
                foreach (var (mname, msize) in def.Memories)
                {
                    string full = CombinePrefix(prefix, mname);
                    // Data is unmanaged (handler-lifetime): freed by FreeHandlerArrays() at the next rebuild
                    // (ResetHandlers runs right after _memories.Clear()). AllocHandlerArray zeroes it.
                    _memories[full] = new Memory { Name = full, Data = AllocHandlerArray<byte>(msize), Length = msize };
                }

                // setupSegments — pull-ups (we don't keep the polygons)
                foreach (var sd in def.Segs)
                {
                    int nid = ResolveRef(sd.Node);
                    if (nid == EmptyNode || IsPwrGnd(nid)) continue;
                    var n = GetOrCreateNode(nid);
                    if (n != null && sd.Pull == '+') n.Pullups++;
                }
                foreach (var pu in def.Pullups)
                {
                    var list = new List<int>();
                    ResolveNodes(CombinePrefix(prefix, pu), list);
                    foreach (int nid in list) { var n = GetOrCreateNode(nid); if (n != null) n.Pullups++; }
                }

                // setupTransistors
                foreach (var td in def.Trans)
                    AddTransistor(CombinePrefix(prefix, td.Name), ResolveRef(td.Gate), ResolveRef(td.C1), ResolveRef(td.C2), td.IsWeak);
            }

            // recurse into sub-modules
            foreach (var sub in def.SubModules)
            {
                if (LoadedDefs.TryGetValue(sub.Type, out var subDef)) AddInstance(subDef, CombinePrefix(prefix, sub.Prefix));
                else Console.Error.WriteLine($"sub-module type '{sub.Type}' not loaded (under prefix '{prefix}')");
            }

            // connections
            foreach (var (cf, ct) in def.Connections) AddConnection(CombinePrefix(prefix, cf), CombinePrefix(prefix, ct));

            // forceCompute
            foreach (var fc in def.ForceCompute) ResolveNodes(CombinePrefix(prefix, fc), _forceComputeList);
        }

        /// <summary>
        /// Resolve a node *expression* to one or more global node ids. Port of node_resolver::resolveNodes:
        ///   "name"          single node (missing → Ngnd)
        ///   "a[7:0]"        [a0, a1, ..., a7]   (the index order matches bit order: bit i = a&lt;i&gt;)
        ///   "a[]"           [a0, a1, ...] until lookup fails
        ///   "x|y|z"         [x, y, z]
        ///   "*func&lt;rom&gt;"   every node whose name starts with the part before '*' and ends with the part after
        /// Missing array elements fall back to Ngnd (so wrong widths don't crash), matching MetalNES.
        /// <paramref name="quiet"/> suppresses the "unknown node" warnings (for optional trace probes).
        /// </summary>
        public static void ResolveNodes(string expr, List<int> outIds, bool quiet = false)
        {
            if (expr.IndexOf('|') >= 0)
            {
                foreach (var part in expr.Split('|')) ResolveNodes(part, outIds, quiet);
                return;
            }

            int star = expr.IndexOf('*');
            if (star >= 0)
            {
                string left = expr.Substring(0, star);
                string right = expr.Substring(star + 1);
                foreach (var kv in _nodeByName)
                {
                    string name = kv.Key;
                    if (name.Length >= left.Length && name.Length >= right.Length &&
                        name.StartsWith(left, StringComparison.Ordinal) && name.EndsWith(right, StringComparison.Ordinal))
                        outIds.Add(kv.Value);
                }
                return;
            }

            int lb = expr.IndexOf('[');
            if (lb >= 0)
            {
                int rb = expr.IndexOf(']', lb);
                if (rb >= 0)
                {
                    string range = expr.Substring(lb + 1, rb - lb - 1);
                    string nameLeft = expr.Substring(0, lb);
                    string nameRight = expr.Substring(rb + 1);

                    if (range.Length == 0)
                    {
                        for (int i = 0; ; i++)
                        {
                            int nn = LookupNode(nameLeft + i + nameRight);
                            if (nn == EmptyNode) break;
                            outIds.Add(nn);
                        }
                    }
                    else
                    {
                        int colon = range.IndexOf(':');
                        if (colon >= 0
                            && int.TryParse(range.AsSpan(0, colon), out int rangeEnd)            // before ':'
                            && int.TryParse(range.AsSpan(colon + 1), out int rangeStart))        // after ':'
                        {
                            int delta = rangeStart < rangeEnd ? 1 : -1;
                            for (int i = rangeStart; delta > 0 ? i <= rangeEnd : i >= rangeEnd; i += delta)
                            {
                                int nn = LookupNode(nameLeft + i + nameRight);
                                outIds.Add(nn == EmptyNode ? Ngnd : nn);
                            }
                        }
                        else if (!quiet)
                        {
                            Console.Error.WriteLine($"bad node range expression: '{expr}'");
                        }
                    }
                    return;
                }
            }

            int single = LookupNode(expr);
            if (single == EmptyNode && !quiet) Console.Error.WriteLine($"unknown node: '{expr}' — using vss");
            outIds.Add(single == EmptyNode ? Ngnd : single);
        }
    }
}
