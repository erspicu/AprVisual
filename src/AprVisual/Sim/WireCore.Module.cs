using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Module composition / instantiation — port of ref/metalnes-main wire_module.cpp:
        //      Wires::addInstance / addConnection / addNode / addTransistor (~L892-1340)
        //      and wire_node_resolver.cpp (the name->id + array/wildcard/| expansion).
        //    See MD/note/02_模組化網表系統.md §3.
        //
        //    Key tricks to keep (decided for S1):
        //      - a "connection" between two nodes = a transistor with gate = Npwr (always ON),
        //        c1/c2 = the two nodes. Reuses the group machinery; no destructive node merge.
        //      - each instance gets a contiguous, aligned slice of the global node-id space;
        //        local ids are offset; full names registered as "prefix.localName".
        //      - special "func<clock>" / "func<rom>" / "func<ram>" / "func<video_out>" /
        //        "func<audio_out>" hook nodes — handlers find them via "*func<...>" wildcard.

        // name -> global node id, and id -> name(s). Built during instantiation.
        private static readonly Dictionary<string, int> _nodeByName = new();
        private static readonly Dictionary<int, string> _nameByNode = new();
        private static int _maxNodeId = Ngnd;   // npwr=1, ngnd=2 already taken

        public static int LookupNode(string name) => _nodeByName.TryGetValue(name, out int id) ? id : EmptyNode;

        public static string GetNodeName(int id) => _nameByNode.TryGetValue(id, out string? n) ? n : "";

        /// <summary>
        /// Allocate `count` fresh node ids, aligned to `alignment` (port of node_resolver::allocNodes).
        /// </summary>
        public static int AllocNodes(int alignment, int count)
        {
            int start = _maxNodeId + 1;
            while (alignment > 1 && (start % alignment) != 0) start++;
            _maxNodeId = start + count - 1;
            return start;
        }

        /// <summary>
        /// Instantiate <paramref name="def"/> (and recursively its sub-modules) under <paramref name="prefix"/>,
        /// adding its nodes / segments (pull-ups) / transistors / connections / forceCompute to the global tables.
        /// TODO: port Wires::addInstance.
        /// </summary>
        public static void AddInstance(ModuleDef def, string prefix)
        {
            throw new NotImplementedException("WireCore.AddInstance — port Wires::addInstance");
        }

        /// <summary>connection: from &lt;-&gt; to as an always-ON transistor (port of Wires::addConnection).</summary>
        public static void AddConnection(int from, int to)
        {
            if (from == to) return;
            // if (IsPwrGnd(from)) (from,to) = (to,from);
            // AddTransistor(name: $"{GetNodeName(from)}<>{GetNodeName(to)}", gate: Npwr, c1: from, c2: to);
            throw new NotImplementedException("WireCore.AddConnection — port Wires::addConnection");
        }

        public static void AddTransistor(string name, int gate, int c1, int c2, bool isWeak = false)
        {
            // TODO: build-time — append to a List<Transistor>; also register gate.gates / c1.c1c2s / c2.c1c2s;
            // if pull-up via weak transistor, bump the node's pullup count. The flattened TransistorList
            // is produced later in WireCore.Reset() (see WireCore.cs / .Group.cs).
            throw new NotImplementedException("WireCore.AddTransistor");
        }

        /// <summary>
        /// Resolve a node *expression* to one or more global node ids. Supports
        /// (port of node_resolver::resolveNodes):
        ///   "name"        single node
        ///   "a[7:0]"      [a7, a6, ..., a0]  (big-endian: first index = MSB)
        ///   "a[]"         [a0, a1, ...] until lookup fails
        ///   "x|y|z"       [x, y, z]
        ///   "*func&lt;rom&gt;"  every node whose name matches the wildcard
        /// Missing array elements fall back to Ngnd (so wrong widths don't crash), matching MetalNES.
        /// </summary>
        public static void ResolveNodes(string expr, List<int> outIds)
        {
            throw new NotImplementedException("WireCore.ResolveNodes — port node_resolver::resolveNodes");
        }

        public static bool IsPwrGnd(int nn) => nn == Npwr || nn == Ngnd;
    }
}
