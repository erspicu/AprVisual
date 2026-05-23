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
        private static int* _groupBuf;        // node ids in the current group (alloc'd in Reset, sized NodeCount)
        private static int* _inGroup;         // O(1) dedup flag per node (1 = currently in _groupBuf); cleared each ComputeNodeGroup
        private static byte _maxState;        // state of the highest-connection node seen
        private static int _maxConnections;

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
        private static byte ComputeNodeGroup(int nn)
        {
            // clear the previous group's dedup flags (only those entries — keeps _inGroup all-zero between calls)
            for (int i = 0; i < _groupCount; i++) _inGroup[_groupBuf[i]] = 0;

            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            _maxState = 0;
            _maxConnections = 0;
            AddNodeToGroup(nn);
            return GetNodeValue();
        }

        // Port of wire_module.cpp addNodeToGroup (~L1931-2001). Recursive (matches MetalNES); the
        // groups are bounded by the conduction structure so depth stays modest in practice. (If a
        // pathological bus group ever overflows the stack, convert to an explicit work list.)
        private static void AddNodeToGroup(int nn)
        {
            if (_inGroup[nn] != 0) return;          // O(1) dedup (MetalNES uses a linear scan; same effect)

            // Phase 2 P2.3: an IR node resolves via its Expr, so to a hybrid group walk it is a *directed
            // driver*, not a walked member: contribute its value (Gnd if 0 — its pull-down is active —
            // else PullUp, exactly what S1's group would accumulate for it) and stop. Not added to
            // _groupBuf, so the group's resolved value never overwrites the IR node. (_inGroup left 0:
            // re-reaching it just re-ORs the same flag — idempotent.)
            if (EnableIrInterp && IrAbsorbed != null && IrAbsorbed[nn] != 0) return;   // P2.3 B: absorbed mid is not a group member (its island is closed; nothing should reach it)
            if (EnableIrInterp && IrBoundaryDriver && IrRoot != null && IrRoot[nn] >= 0)
            {
                _groupFlags |= NodeStates[nn] == 0 ? NodeFlags.Gnd : NodeFlags.PullUp;
                return;
            }

            // Phase 2.5 Step 3.5 Option D — codegen-owned BFS block (per Gemini r2 §Q3):
            // The dispatcher has written the correct value to NodeStates[nn] before settle.
            // To prevent S1's BFS from re-resolving the owned region (which would either
            // overwrite the dispatcher's value or do redundant work walking owned internals),
            // we treat the owned node as an "infinite-strength driver" — OR-in PullUp/Gnd based
            // on its current value and STOP the BFS here. _inGroup left 0 (idempotent on re-visit).
            // This is the only mechanism that actually SKIPS S1 work on owned regions; without
            // this, CodegenOwned only skips the RecalcNode entry, not the BFS group walk
            // (see MD/impl/math-algos/10_step35_architecture_finding.md).
            if (EnableCodegenDispatcher && CodegenOwned != null && CodegenOwned[nn] != 0)
            {
                _groupFlags |= NodeStates[nn] == 0 ? NodeFlags.Gnd : NodeFlags.PullUp;
                return;
            }

            _inGroup[nn] = 1;

            ref NodeInfo ns = ref NodeInfos[nn];
            _groupBuf[_groupCount++] = nn;

            // track the highest-connection ("largest capacitance") node's state for the all-floating case
            if (ns.Connections > _maxConnections) { _maxState = NodeStates[nn]; _maxConnections = ns.Connections; }

            // this node is now part of a group being resolved — it won't need separate re-processing this pass
            RecalcHash[nn] = 0;

            _groupFlags |= ns.Flags;

            // walk channels to normal nodes: (gate, other) pairs, 0-terminated
            if (ns.TlistC1c2s != 0)
            {
                int* p = TransistorList + ns.TlistC1c2s;
                while (*p != 0)
                {
                    int gate = *p++;
                    int other = *p++;
                    if (NodeStates[gate] != 0) AddNodeToGroup(other);
                }
            }
            // any ON channel to GND ⇒ the whole group is pulled low
            if (ns.TlistC1gnd != 0)
            {
                int* p = TransistorList + ns.TlistC1gnd;
                while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Gnd; break; } }
            }
            // any ON channel to VCC ⇒ pulled high
            if (ns.TlistC1pwr != 0)
            {
                int* p = TransistorList + ns.TlistC1pwr;
                while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { _groupFlags |= NodeFlags.Pwr; break; } }
            }
        }

        private static byte GetNodeValue()
        {
            if ((_groupFlags & NodeFlags.ForceCompute) != 0 &&
                (_groupFlags & NodeFlags.Gnd) != 0 && (_groupFlags & NodeFlags.Pwr) != 0)
            {
                _groupFlags &= ~NodeFlags.Gnd;
                _groupFlags &= ~NodeFlags.Pwr;
            }
            if (_groupFlags == NodeFlags.None) return _maxState;   // purely floating: largest-cap node wins
            return FlagsToState[(int)_groupFlags];
        }
    }
}
