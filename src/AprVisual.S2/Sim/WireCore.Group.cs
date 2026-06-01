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

        // Iterative BFS version: reuses _groupBuf as both the current-group list and the
        // pending-work queue. No recursive calls — readIndex advances through queued nodes,
        // _groupCount is the write cursor. Drained when readIndex == _groupCount.
        // AddNodeOrApplyDriver handles the dedup + IR/Codegen intercepts that were inline in
        // the recursive version.
        //
        // Non-recursive now → JIT can inline. Combined with ComputeNodeGroup's [AggressiveInlining],
        // the whole BFS chain becomes inlined into RecalcNode's caller (ProcessQueueInterp inner loop).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void AddNodeToGroup(int seed)
        {
            int readIndex = 0;
            AddNodeOrApplyDriver(seed);

            while (readIndex < _groupCount)
            {
                int nn = _groupBuf[readIndex++];
                NodeInfo* ns = NodeInfos + nn;

                // S2-A: walk channels from the inline payload (one cache line, no chase) when available.
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    // channels to normal nodes: (gate, other) pairs
                    for (int k = 0; k < n2; k += 2) { if (NodeStates[pay[k]] != 0) AddNodeOrApplyDriver(pay[k + 1]); }
                    // any ON channel to GND ⇒ pulled low
                    int gndEnd = n2 + ns->GndCount;
                    for (int k = n2; k < gndEnd; k++) { if (NodeStates[pay[k]] != 0) { _groupFlags |= NodeFlags.Gnd; break; } }
                    // any ON channel to VCC ⇒ pulled high
                    int pwrEnd = gndEnd + ns->PwrCount;
                    for (int k = gndEnd; k < pwrEnd; k++) { if (NodeStates[pay[k]] != 0) { _groupFlags |= NodeFlags.Pwr; break; } }
                }
                else
                {
                    // high-fanout fallback: TransistorList 0-terminated sub-lists (unchanged).
                    // walk channels to normal nodes: (gate, other) pairs, 0-terminated
                    if (ns->TlistC1c2s != 0)
                    {
                        ushort* p = TransistorList + ns->TlistC1c2s;
                        while (*p != 0)
                        {
                            int gate = *p++;
                            int other = *p++;
                            if (NodeStates[gate] != 0) AddNodeOrApplyDriver(other);
                        }
                    }
                    // any ON channel to GND ⇒ the whole group is pulled low
                    if (ns->TlistC1gnd != 0)
                    {
                        ushort* p = TransistorList + ns->TlistC1gnd;
                        while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Gnd; break; } }
                    }
                    // any ON channel to VCC ⇒ pulled high
                    if (ns->TlistC1pwr != 0)
                    {
                        ushort* p = TransistorList + ns->TlistC1pwr;
                        while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Pwr; break; } }
                    }
                }
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static void AddNodeOrApplyDriver(int nn)
        {
            if (_inGroup[nn] != 0) return;

            _inGroup[nn] = 1;
            if (Profiling) { ProfVisit[nn]++; ProfTotalVisits++; }
            ref NodeInfo ns = ref NodeInfos[nn];
            _groupBuf[_groupCount++] = (ushort)nn;
            RecalcHash[nn] = 0;
            _groupFlags |= ns.Flags;
            // NodeConnections deferred to GetNodeValue's floating branch (suggest #02):
            // only needed when _groupFlags ends up None, which is <1% of walks.
        }

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
