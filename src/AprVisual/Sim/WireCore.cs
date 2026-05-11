using System;

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

        // nodeInfos[nodeId] — flags + connection count + indices into TransistorList for the
        // three pre-bucketed transistor sub-lists (channels-to-normal / channels-to-GND / channels-to-VCC)
        // and the gates list. See WireCore.Group.cs / WireCore.Recalc.cs.
        public static NodeInfo* NodeInfos;

        // FlagsToState[256] — precomputed by BuildFlagsToStateTable() in WireCore.Group.cs.
        // Indexed by (group's OR-ed NodeFlags); value = the group's resolved 0/1.
        public static byte* FlagsToState;

        // ── Flattened adjacency: one big int[] with null(0)-terminated sub-lists (cache-friendly) ──
        // Sub-lists referenced by NodeInfo.TlistGates / TlistC1c2s / TlistC1gnd / TlistC1pwr.
        public static int* TransistorList;

        // ── Double-buffered recalc queue (see WireCore.Recalc.cs) ──
        public static int* RecalcList;
        public static int* RecalcListNext;
        public static int* RecalcHash;       // dedupe: nonzero = already queued this pass
        public static int* RecalcHashNext;
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
        /// Reset all per-node state to power-on (pull-ups -> high, the rest 0/floating),
        /// (re)build the flattened transistor lists and the FlagsToState LUT.
        /// Allocates the unmanaged arrays if not already done. Call after parse/build.
        /// TODO: port from ref/metalnes-main wire_module.cpp wire_compute::reset() (~L1400-1517).
        /// </summary>
        public static void Reset()
        {
            // TODO:
            //  1. allocate NodeStates / NodeInfos / RecalcList* / RecalcHash* sized to NodeCount
            //  2. BuildFlagsToStateTable()  (WireCore.Group.cs)
            //  3. for each node: connections = c1c2s.Count + gates.Count; SetFloat(); if pullups>0 -> PullUp + state=1
            //  4. build TransistorList: per node, flatten gates -> (c1,c2,...,0); channels split into
            //     normal (gate,other,...,0) / to-GND (gate,...,0) / to-VCC (gate,...,0)
            //  5. NodeStates[Ngnd]=0; NodeInfos[Ngnd] = Gnd; NodeStates[Npwr]=1; NodeInfos[Npwr] = Pwr
            //  6. apply forceCompute list
            throw new NotImplementedException("WireCore.Reset — port wire_compute::reset()");
        }

        /// <summary>Free every unmanaged allocation owned by WireCore. Idempotent.</summary>
        public static void Shutdown()
        {
            FreeUnmanagedMemory();
        }
    }

    // Per-node hot record. Kept as a 32-byte-ish struct so an array of these stays cache-dense.
    internal struct NodeInfo
    {
        public WireCore.NodeFlags Flags;
        public int Connections;     // c1c2s.Count + gates.Count — used as the "capacitance" proxy for the
                                    // floating-group tie-break (largest node wins). See WireCore.Group.cs.
        public int TlistGates;      // index into WireCore.TransistorList: (c1,c2, c1,c2, ..., 0)
        public int TlistC1c2s;      // ...: (gate,other, gate,other, ..., 0) — channels to a normal node
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
