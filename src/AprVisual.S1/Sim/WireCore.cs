using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  WireCore — the S1 switch-level simulation engine.
    //
    //  Monolithic `static unsafe partial class` (AprNes style — see
    //  MD/struct/09_S1實作風格_參考AprNes.md). Split across:
    //    WireCore.cs          — fields, NodeInfo/NodeValue/Transistor types, Init/Free
    //    WireCore.Native.cs   — NativeMemory.AlignedAlloc wrappers + one-shot Free
    //    WireCore.Parse.cs    — .js module-format parsing (segdefs/transdefs/nodenames)
    //    WireCore.Module.cs   — instance node-id allocation, connection = always-on transistor,
    //                           name resolution (a[7:0] / a[] / x|y|z / *wildcard)
    //    WireCore.Recalc.cs   — recalcNodeList / processQueue / recalcNode (double-buffered list + hash)
    //    WireCore.Group.cs    — computeNodeGroup / addNodeToGroup / getNodeValue (flags OR + 256-entry LUT)
    //    WireCore.Handlers.cs — clock / RAM / ROM behavioral handlers (callback = fake transistor)
    //    WireCore.System.cs   — load nes-001, attach handlers, real reset (setHigh res; step(192); setLow res)
    //    WireCore.Trace.cs    — trace ring buffer; dump cpu.a/x/y/p/s/pc etc.
    //
    //  Reference implementation: ref/metalnes-main/source/metalnes/wire_module.cpp
    //  (the `wire_compute` class, ~L1400-2030). See MD/note/01_模擬核心演算法.md.
    //
    //  Improvements over MetalNES, decided for S1:
    //    - NodeValue distinguishes "driven High" vs "HoldPrevious" (MetalNES conflates both
    //      into the node_state flag). See MD/note/01 §2.5.
    //    - Transistor.IsWeak (7th column of 2A03/2C02 transdefs) is actually used to
    //      separate strong pull-down vs weak/depletion pull-up. MetalNES reads it
    //      (transdef::unknown_2) but barely uses it.
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        // ── Special node ids (match MetalNES: npwr=1, ngnd=2; 0 reserved / EMPTY) ──
        public const int EmptyNode = -1;
        public const int Npwr = 1;
        public const int Ngnd = 2;

        // ── Per-node flags (8-bit, OR-ed across a connected group; index into FlagsToState) ──
        [Flags]
        public enum NodeFlags : byte
        {
            None         = 0,
            State        = 1 << 0, // current logical state (also stands in for "floating, held high")
            PullUp       = 1 << 1, // has a pull-up (segdef '+' or module pullups:[] or a weak transistor)
            SetHigh      = 1 << 2, // externally driven high
            SetLow       = 1 << 3, // externally driven low
            Pwr          = 1 << 4, // this node is VCC
            Gnd          = 1 << 5, // this node is GND
            ForceCompute = 1 << 6, // if a group has both Gnd and Pwr, they cancel (for certain bus nodes)
            HasCallback  = 1 << 7, // a callback is registered on this node (memory watch etc.)
        }

        // ── Result of evaluating a node group (improvement over MetalNES's bool) ──
        public enum NodeValue : byte
        {
            Low = 0,        // forced 0 (strong path to GND)
            High = 1,       // forced 1 (path to VCC / strong pull-up)
            HoldPrevious,   // floating; retained via parasitic capacitance
            Undefined,      // conflict / uninitialised
        }

        // ── Hot per-node data (Structure-of-Arrays; allocated by Reset() once node count is known) ──
        // nodeStates[nodeId] = 0/1 — read constantly during group BFS / recalc.
        public static byte* NodeStates;

        // nodeInfos[nodeId] — flags + 3× indices into TransistorList for the bucketed channel sub-lists
        // (channels-to-normal / channels-to-GND / channels-to-VCC). 16 bytes each — 4 per cache line.
        // The cold fields (Connections, TlistGates) were split out to NodeConnections / NodeTlistGates.
        public static NodeInfo* NodeInfos;

        // Cold per-node arrays — used much less often than NodeInfos.
        // NodeConnections[nn] = c1c2s.Count + gates.Count, the "capacitance" proxy for the floating-group
        //   tie-break (only fires when a group has zero flags — rare). Touched twice in AddNodeToGroup
        //   but the early-out branch is well-predicted-false in typical groups.
        // NodeTlistGates[nn] = (c1,c2, c1,c2, ..., 0) sub-list — read ONLY by SetNodeState to enqueue
        //   downstream nodes when a state flips. Called once per group member at writeback time, so 1
        //   cache miss per group member vs the BFS hot loop's per-visit cost.
        public static int* NodeConnections;
        public static int* NodeTlistGates;

        // FlagsToState[256] — precomputed by BuildFlagsToStateTable() in WireCore.Group.cs.
        // Indexed by (group's OR-ed NodeFlags); value = the group's resolved 0/1.
        public static byte* FlagsToState;

        // ── Flattened adjacency: one big ushort[] with null(0)-terminated sub-lists (cache-friendly) ──
        // Sub-lists referenced by NodeInfo.TlistGates / TlistC1c2s / TlistC1gnd / TlistC1pwr.
        // ushort* (was int*): node IDs < 65K, halves the working set (697KB → 350KB) — the
        // hottest array in BFS by far, so L2 pressure reduction is the lever here.
        public static ushort* TransistorList;

        // ── Double-buffered recalc queue (see WireCore.Recalc.cs) ──
        public static int* RecalcList;
        public static int* RecalcListNext;
        // byte* (was int*) — 0/1 only per node, 58 KB → 14 KB. Bitset variant (ulong*) was
        // tested but the shift+mask per-access cost erased the cache benefit. byte* keeps
        // straight-load semantics + same L1d footprint as _inGroup.
        public static byte* RecalcHash;
        public static byte* RecalcHashNext;
        public static int RecalcListCount;
        public static int RecalcListNextCount;

        // ── Counts (set during parse/build) ──
        public static int NodeCount;
        public static int TransistorCount;

        // ── Cycle counter (half-cycles), like MetalNES _time ──
        public static long Time;

        // ── Output framebuffer: unmanaged 256x240 ARGB, written by the video-out handler,
        //    blitted by Render.NativeGDI. Allocated in WireCore.System.cs. ──
        public const int ScreenW = 256;
        public const int ScreenH = 240;
        public static uint* FrameBuffer;

        // ── Tracing toggle ──
        public static int TraceLevel;

        // ───────────────────────────────────────────────────────────────────────
        //  Lifecycle
        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Power-on the netlist built by ComposeSystem/AddInstance: allocate the unmanaged hot arrays,
        /// build the FlagsToState LUT and the flattened TransistorList, set every node to power-on state
        /// (pull-ups → high, the rest 0/floating), mark Ngnd/Npwr and the forceCompute nodes.
        /// Port of ref/metalnes-main wire_module.cpp wire_compute::reset() (~L1400-1517). Re-runnable.
        /// </summary>
        public static void Reset()
        {
            NodeCount = NodeArrayCount;
            if (NodeCount < 3) throw new InvalidOperationException("WireCore.Reset: netlist not composed (call ComposeSystem first)");

            FreeUnmanagedMemory();   // release any prior allocations (re-runnable)

            NodeStates     = AllocArray<byte>(NodeCount);
            NodeInfos      = AllocArray<NodeInfo>(NodeCount);
            NodeConnections = AllocArray<int>(NodeCount);   // cold — only tie-break
            NodeTlistGates  = AllocArray<int>(NodeCount);   // cold — only SetNodeState writeback
            RecalcList     = AllocArray<int>(NodeCount);
            RecalcListNext = AllocArray<int>(NodeCount);
            RecalcHash     = AllocArray<byte>(NodeCount);
            RecalcHashNext = AllocArray<byte>(NodeCount);
            _groupBuf      = AllocArray<ushort>(NodeCount);
            _inGroup       = AllocArray<byte>(NodeCount);
            FlagsToState   = AllocArray<byte>(256);
            BuildFlagsToStateTable();   // WireCore.Group.cs

            // _inGroup must start all-zero (it does — AllocArray clears) and ComputeNodeGroup keeps it so;
            // but the *previous* group's _groupBuf/_groupCount are now stale, so reset them too.
            _groupCount = 0;

            RecalcListCount = RecalcListNextCount = 0;
            Time = 0;

            // ── per-node power-on state ──
            for (int nn = 0; nn < NodeCount; nn++)
            {
                Node? node = Nodes[nn];
                if (node == null) continue;   // AllocArray zeroed: state 0, flags 0, tlist* 0
                ref NodeInfo ns = ref NodeInfos[nn];
                NodeConnections[nn] = node.CapacityOverride >= 0 ? node.CapacityOverride : node.C1c2s.Count + node.Gates.Count;
                ns.Flags = NodeFlags.None;
                if (node.Pullups > 0) { ns.Flags |= NodeFlags.PullUp; NodeStates[nn] = 1; }
                if (node.Callback != null) ns.Flags |= NodeFlags.HasCallback;
            }

            // ── flattened transistor lists (cache-friendly; one big int[] of null(0)-terminated sub-lists) ──
            // Index 0 is a sentinel so an "empty" sub-list index of 0 points straight at a 0 terminator.
            var tl = new List<int> { 0 };
            int AddSubList(List<int> sub)
            {
                if (sub.Count == 0) return 0;
                int idx = tl.Count;
                tl.AddRange(sub);
                tl.Add(0);
                return idx;
            }
            var gates = new List<int>();
            var c1c2 = new List<int>();
            var c1gnd = new List<int>();
            var c1pwr = new List<int>();
            for (int nn = 0; nn < NodeCount; nn++)
            {
                Node? node = Nodes[nn];
                if (node == null) continue;
                ref NodeInfo ns = ref NodeInfos[nn];

                gates.Clear();
                foreach (int tid in node.Gates) { var t = Transistors[tid]; gates.Add(t.C1); gates.Add(t.C2); }
                NodeTlistGates[nn] = AddSubList(gates);

                c1c2.Clear(); c1gnd.Clear(); c1pwr.Clear();
                foreach (int tid in node.C1c2s)
                {
                    var t = Transistors[tid];
                    if (t.Gate == Ngnd) continue;             // gate tied to GND → transistor can never turn on
                    int other = t.C1 == nn ? t.C2 : t.C1;
                    if (other == Ngnd) c1gnd.Add(t.Gate);
                    else if (other == Npwr) c1pwr.Add(t.Gate);
                    else { c1c2.Add(t.Gate); c1c2.Add(other); }
                }
                ns.TlistC1c2s = AddSubList(c1c2);
                ns.TlistC1gnd = AddSubList(c1gnd);
                ns.TlistC1pwr = AddSubList(c1pwr);
            }
            TransistorList = AllocArray<ushort>(tl.Count);
            for (int i = 0; i < tl.Count; i++) TransistorList[i] = (ushort)tl[i];
            _transistorListLength = tl.Count;

            // ── supply nodes (override whatever the loop set) ──
            NodeStates[Ngnd] = 0; NodeInfos[Ngnd].Flags = NodeFlags.Gnd;
            NodeStates[Npwr] = 1; NodeInfos[Npwr].Flags = NodeFlags.Pwr;

            // ── forceCompute: if a group has both Gnd and Pwr, they cancel (certain bus nodes) ──
            foreach (int nn in ForceComputeList)
                if (nn >= 0 && nn < NodeCount) NodeInfos[nn].Flags |= NodeFlags.ForceCompute;

            // ── Fast-path classifier (always on in S1 — verified +2% peak in C# 6d01abe bench).
            //    Pure-logic-gnd nodes (pull-up + only GND channels + no normal channel + no callback)
            //    resolve in O(1) via RecalcNodeFast, bypassing the group DFS. See WireCore.FastPath.cs.
            ClassifyPureLogicNodes();
        }

        // length of TransistorList (for diagnostics)
        public static int TransistorListLength => _transistorListLength;
        private static int _transistorListLength;

        /// <summary>Free every unmanaged allocation owned by WireCore. Idempotent.</summary>
        public static void Shutdown()
        {
            FreeUnmanagedMemory();
        }
    }

    // Per-node hot record. 16 bytes exactly (1 byte flag + 3 byte padding + 3× 4-byte int) = 4 per cache line.
    // Cold fields (Connections, TlistGates) split out to WireCore.NodeConnections / NodeTlistGates so AddNodeToGroup's
    // hot-loop struct read fetches only the bits it needs (BFS visits much more often than tie-break or writeback).
    internal struct NodeInfo
    {
        public WireCore.NodeFlags Flags;
        public int TlistC1c2s;      // index into WireCore.TransistorList: (gate,other, gate,other, ..., 0) — channels to a normal node
        public int TlistC1gnd;      // ...: (gate, gate, ..., 0) — channels whose far end is GND
        public int TlistC1pwr;      // ...: (gate, gate, ..., 0) — channels whose far end is VCC
    }

    // Build-time transistor record (the hot path uses the flattened WireCore.TransistorList instead).
    internal struct Transistor
    {
        public int Gate;
        public int C1;
        public int C2;
        public bool IsWeak;   // 7th column of 2A03/2C02 transdefs — weak / depletion-load device
        public string Name;
    }
}
