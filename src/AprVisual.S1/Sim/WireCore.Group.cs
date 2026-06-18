using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Group evaluation — the heart of the switch-level engine.
        //    Port of ref/metalnes-main wire_module.cpp:
        //      computeNodeGroup / addNodeToGroup / getNodeValue (~L1583-2030)
        //    plus flagsToState (~L1358-1396). See MD/note/01_模擬核心演算法.md §2.4-2.5.
        //
        //    NMOS resolution priority (same as MetalNES / chipsim.js, see MD/struct/01 §8):
        //      1. ForceCompute special: if a group has both Gnd and Pwr, clear both
        //      2. Gnd     -> 0   (GND wins)
        //      3. Pwr     -> 1
        //      4. SetHigh -> 1   (external drive)
        //      5. SetLow  -> 0
        //      6. PullUp  -> 1
        //      7. State   -> previous value
        //      8. else    -> 0
        //    Plus: if the OR-ed flags are *empty* (purely floating group), return the state of
        //          the node in the group with the most connections (largest "capacitance" wins).
        //
        //    S1 improvement TODO: have a variant that returns NodeValue.HoldPrevious explicitly
        //    for the floating case, so callers can tell "driven high" from "held high".

        // Scratch state for the current addNodeToGroup walk (single-threaded).
        private static NodeFlags _groupFlags;
        private static int _groupCount;       // number of nodes currently in _groupBuf
        private static ushort* _groupBuf;     // node ids in the current group (alloc'd in Reset, sized NodeCount). ushort* (was int*) — node count <65K so 16 bit suffices, 29KB vs 58KB
        private static byte* _inGroup;        // O(1) dedup flag per node (1 = currently in _groupBuf); cleared each ComputeNodeGroup. byte* (was int*) — 0/1 only, 14KB vs 58KB fits L1d alongside NodeStates

        /// <summary>Build the 256-entry FlagsToState lookup table from FlagsToStateOf().</summary>
        public static void BuildFlagsToStateTable()
        {
            // FlagsToState is unmanaged byte[256] — alloc'd in Reset() before calling this.
            for (int i = 0; i < 256; i++)
                FlagsToState[i] = FlagsToStateOf((NodeFlags)i) ? (byte)1 : (byte)0;
        }

        // The pure resolution function (run 256 times to fill the LUT).
        private static bool FlagsToStateOf(NodeFlags flags)
        {
            if ((flags & NodeFlags.ForceCompute) != 0 &&
                (flags & NodeFlags.Gnd) != 0 && (flags & NodeFlags.Pwr) != 0)
            {
                flags &= ~NodeFlags.Gnd;
                flags &= ~NodeFlags.Pwr;
            }
            if ((flags & NodeFlags.Gnd) != 0) return false;
            if ((flags & NodeFlags.Pwr) != 0) return true;
            if ((flags & NodeFlags.SetHigh) != 0) return true;
            if ((flags & NodeFlags.SetLow) != 0) return false;
            if ((flags & NodeFlags.PullUp) != 0) return true;
            if ((flags & NodeFlags.State) != 0) return true;
            return false;
        }

        /// <summary>
        /// Build the connected group containing <paramref name="nn"/> (BFS/DFS over ON transistors),
        /// accumulating _groupFlags and _maxState, then resolve the group's value.
        /// TODO: port computeNodeGroup + addNodeToGroup.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte ComputeNodeGroup(int nn)
        {
            // clear the previous group's dedup flags (only those entries — keeps _inGroup all-zero between calls)
            for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;

            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            AddNodeToGroup(nn);
            return GetNodeValue();
        }

        // Iterative BFS over ON transistors. _groupBuf is both the group list and the work queue
        // (readIndex reads, gc writes); drained when readIndex == gc. Non-recursive → JIT inlines it,
        // and with ComputeNodeGroup's [AggressiveInlining] the whole BFS chain folds into the
        // ProcessQueue inner loop.
        //
        // Perf shape (measured, interleaved-paired, bit-exact):
        //   • hoist the static SoA pointers + group state into LOCALS and manual-inline the per-node
        //     add (dedup + push + RecalcHash clear + flags OR): keeps gc/gf in registers across the
        //     whole walk instead of hitting static-field memory each step — **+3.2%**.
        //   • high-fanout (overflow) c1c2 walk reads two (gate,other) pairs per 64-bit load — **+1.4%**
        //     (only pays here because high-fanout walks are long; the same trick LOST on short walks).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void AddNodeToGroup(int seed)
        {
            // hoisted locals (see method comment) — write back gc/gf at the end.
            byte* inGroup = _inGroup;
            ushort* groupBuf = _groupBuf;
            byte* nodeStates = NodeStates;
            byte* recalcHash = RecalcHash;
            NodeInfo* nodeInfos = NodeInfos;
            ushort* transList = TransistorList;
            int gc = _groupCount;
            NodeFlags gf = _groupFlags;

            if (inGroup[seed] == 0) { inGroup[seed] = 1; groupBuf[gc++] = (ushort)seed; gf |= nodeInfos[seed].Flags; }

#if DEBUG
            int dbgLevelEnd = gc;   // end of BFS level 0 (the seed) — for the depth profiler
            int dbgDepth = 0;
#endif
            int readIndex = 0;
            while (readIndex < gc)
            {
#if DEBUG
                if (readIndex == dbgLevelEnd) { dbgDepth++; dbgLevelEnd = gc; }   // crossed into the next BFS level
#endif
                int nn = groupBuf[readIndex++];
                NodeInfo* ns = nodeInfos + nn;

                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2)
                        if (nodeStates[pay[k]] != 0) { int o = pay[k + 1]; if (inGroup[o] == 0) { inGroup[o] = 1; groupBuf[gc++] = (ushort)o; recalcHash[o] = 0; gf |= nodeInfos[o].Flags; } }
                    int gndEnd = n2 + ns->GndCount;
                    for (int k = n2; k < gndEnd; k++) { if (nodeStates[pay[k]] != 0) { gf |= NodeFlags.Gnd; break; } }
                    int pwrEnd = gndEnd + ns->PwrCount;
                    for (int k = gndEnd; k < pwrEnd; k++) { if (nodeStates[pay[k]] != 0) { gf |= NodeFlags.Pwr; break; } }
                }
                else
                {
                    if (ns->TlistC1c2s != 0)   // high-fanout: ulong dual-pair (2 (gate,other) pairs / 64-bit load)
                    {
                        ushort* p = transList + ns->TlistC1c2s;
                        while (true)
                        {
                            ulong quad = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<ulong>(p);
                            int g1 = (ushort)quad; if (g1 == 0) break;
                            if (nodeStates[g1] != 0) { int o = (ushort)(quad >> 16); if (inGroup[o] == 0) { inGroup[o] = 1; groupBuf[gc++] = (ushort)o; recalcHash[o] = 0; gf |= nodeInfos[o].Flags; } }
                            int g2 = (ushort)(quad >> 32); if (g2 == 0) break;
                            if (nodeStates[g2] != 0) { int o = (ushort)(quad >> 48); if (inGroup[o] == 0) { inGroup[o] = 1; groupBuf[gc++] = (ushort)o; recalcHash[o] = 0; gf |= nodeInfos[o].Flags; } }
                            p += 4;
                        }
                    }
                    if (ns->TlistC1gnd != 0)
                    {
                        ushort* p = transList + ns->TlistC1gnd;
                        while (*p != 0) { int gate = *p++; if (nodeStates[gate] != 0) { gf |= NodeFlags.Gnd; break; } }
                    }
                    if (ns->TlistC1pwr != 0)
                    {
                        ushort* p = transList + ns->TlistC1pwr;
                        while (*p != 0) { int gate = *p++; if (nodeStates[gate] != 0) { gf |= NodeFlags.Pwr; break; } }
                    }
                }
            }

#if DEBUG
            BfsDepthTally(dbgDepth);   // BFS-depth distribution profiler (DEBUG only)
#endif
            _groupCount = gc;
            _groupFlags = gf;
        }
        // (the old AddNodeOrApplyDriver helper is now manual-inlined at each add site above — the
        //  dedup + push + RecalcHash clear + flags OR. NodeConnections stays deferred to GetNodeValue's
        //  floating branch, suggest #02 — only needed when the group ends up all-floating, <1% of walks.)

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static byte GetNodeValue()
        {
            // ForceCompute|Gnd|Pwr mask is pre-computed into the 256-entry FlagsToState LUT
            // (see BuildFlagsToStateTable/FlagsToStateOf above) — no need to mask at runtime.
            if (_groupFlags != NodeFlags.None) return FlagsToState[(int)_groupFlags];

            // purely floating: largest-cap node wins. Deferred from BFS — only <1% of walks hit this.
            int maxConn = -1;
            byte maxState = 0;
            for (int i = 0; i < _groupCount; i++)
            {
                int nn = _groupBuf[i];
                int conn = NodeConnections[nn];
                if (conn > maxConn) { maxState = NodeStates[nn]; maxConn = conn; }
            }
            return maxState;
        }
    }
}
