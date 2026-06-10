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
                // S2-A: inline the channel payload into the 32-byte NodeInfo for small-fanout nodes
                // (~96%), so the hot path (cls==2 singleton check / RecalcNodeFast / BFS) reads ONE
                // cache line with no dependent chase into TransistorList. InlinePayload layout:
                //   [c1c2 (gate,other) pairs: 2*C1c2Count][gnd gates: GndCount][pwr gates: PwrCount].
                // S2-A2: for inline nodes we DON'T emit their c1c2/gnd/pwr sublists into TransistorList
                // at all (the inline path never reads them) — leaving TlistC1c2s/gnd/pwr = 0. This drops
                // the dead duplicate channel data from TransistorList, tightening the working set the hot
                // SetNodeState gates-writeback (NodeTlistGates -> TransistorList) walks. The gates sublist
                // is still emitted for ALL nodes (above). High-fanout nodes (~4%: buses/clk) keep
                // Inline==0 and the legacy TransistorList sublists. NodeConnections (capacitance, from the
                // managed graph counts) is independent of this -> bit-exact. Classification uses C1c2Count
                // for inline nodes (TlistC1c2s no longer set) — see ClassifyPureLogicNodes.
                int payLen = c1c2.Count + c1gnd.Count + c1pwr.Count;
                if (payLen <= NodeInfo.InlineCap)
                {
                    ns.Inline = 1;
                    ns.C1c2Count = (byte)(c1c2.Count >> 1);   // (gate,other) pairs
                    ns.GndCount = (byte)c1gnd.Count;
                    ns.PwrCount = (byte)c1pwr.Count;
                    ushort* pay = (NodeInfos + nn)->InlinePayload;
                    int w = 0;
                    for (int k = 0; k < c1c2.Count; k++) pay[w++] = (ushort)c1c2[k];
                    for (int k = 0; k < c1gnd.Count; k++) pay[w++] = (ushort)c1gnd[k];
                    for (int k = 0; k < c1pwr.Count; k++) pay[w++] = (ushort)c1pwr[k];
                    // TlistC1c2s/gnd/pwr stay 0 (unused on the inline path; not emitted into TransistorList).
                }
                else
                {
                    ns.TlistC1c2s = AddSubList(c1c2);
                    ns.TlistC1gnd = AddSubList(c1gnd);
                    ns.TlistC1pwr = AddSubList(c1pwr);
                }
            }
            // >=4 trailing pad zeros so SetNodeState's 8-byte (4-ushort) ulong reads at the last sublist
            // can't fault past the array end (pad zeros read as terminators = harmless). See SetNodeState.
            tl.Add(0); tl.Add(0); tl.Add(0); tl.Add(0);
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
            ClassifyPruneTaint();   // safety mask for the same-state turn-on prune (no-PullUp / ForceCompute)
            ClassifyTurnOffSkip();  // P-2: safety mask for the turn-off enqueue prune (isolated-on-disconnect float-hold)

            // ── Callback-by-node direct lookup table (suggest #F4): RecalcNode's HasCallback branch
            //    reads _callbackByNode[nn] instead of going through Nodes[nn].Callback (managed graph).
            _callbackByNode = new CallbackInfo?[NodeCount];   // [A6] direct array (was Dictionary)
            for (int i = 0; i < NodeCount; i++)
            {
                var cb = Nodes[i]?.Callback;
                if (cb != null) _callbackByNode[i] = cb;
            }
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

    // Per-node hot record. S2-A: 32 bytes (2 per 64B cache line). Holds the mutable Flags + the
    // channel payload INLINE for small-fanout nodes (Inline==1, ~96%), so the hot path reads ONE
    // cache line with NO dependent chase into TransistorList. High-fanout nodes (~4%: buses/clk)
    // keep Inline==0 and use the Tlist* indices into TransistorList (legacy path, unchanged +
    // bit-exact). Tlist* indices are kept for ALL nodes (fast-path classify reads TlistC1c2s != 0).
    // Cold fields (Connections, TlistGates/writeback) remain in WireCore.NodeConnections /
    // NodeTlistGates (different access pattern: tie-break + SetNodeState writeback, not the BFS scan).
    // 16 bytes (4 per 64B cache line; was 32). The inline channel payload and the overflow
    // TransistorList indices are MUTUALLY EXCLUSIVE (every hot access is gated by Inline), so they
    // share one 12-byte region via explicit layout (a union). InlineCap drops 7->6 to make the payload
    // 12 bytes (= the 3 Tlist ints); GndCount/PwrCount pack into one byte (each <= 6, a nibble). This
    // halves NodeInfos (460KB -> 230KB) so more of the BFS working set stays in L2 — the only field
    // that needs >16 bits is the Tlist index (TransistorList ~115K), which keeps its int and lives in
    // the union slot. All field NAMES are unchanged so the hot sites don't change.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 16)]
    internal unsafe struct NodeInfo
    {
        public const int InlineCap = 6;   // InlinePayload slots; node is inline iff 2*C1c2Count + GndCount + PwrCount <= InlineCap
        [System.Runtime.InteropServices.FieldOffset(0)] public WireCore.NodeFlags Flags;  // mutable (SetHigh/SetLow at runtime)
        [System.Runtime.InteropServices.FieldOffset(1)] public byte Inline;               // 1 = use InlinePayload; 0 = use Tlist*
        [System.Runtime.InteropServices.FieldOffset(2)] public byte C1c2Count;            // # (gate,other) PAIRS inline
        [System.Runtime.InteropServices.FieldOffset(3)] public byte GndPwr;               // GndCount = low nibble, PwrCount = high nibble (each <= 6)
        // ── 12-byte union @ offset 4 ── inline nodes use InlinePayload; overflow nodes use the 3 Tlist ints.
        [System.Runtime.InteropServices.FieldOffset(4)]  public fixed ushort InlinePayload[InlineCap];  // [c1c2 pairs][gnd gates][pwr gates]
        [System.Runtime.InteropServices.FieldOffset(4)]  public int TlistC1c2s;   // index into WireCore.TransistorList: (gate,other, ..., 0)
        [System.Runtime.InteropServices.FieldOffset(8)]  public int TlistC1gnd;   // ...: (gate, ..., 0) — channels whose far end is GND
        [System.Runtime.InteropServices.FieldOffset(12)] public int TlistC1pwr;   // ...: (gate, ..., 0) — channels whose far end is VCC

        // GndCount/PwrCount packed into GndPwr (nibbles) — same read/write surface as the old byte fields.
        public byte GndCount { readonly get => (byte)(GndPwr & 0x0F); set => GndPwr = (byte)((GndPwr & 0xF0) | (value & 0x0F)); }
        public byte PwrCount { readonly get => (byte)(GndPwr >> 4);   set => GndPwr = (byte)((GndPwr & 0x0F) | ((value & 0x0F) << 4)); }
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
