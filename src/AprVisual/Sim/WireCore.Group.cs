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
            _groupFlags = NodeFlags.None;
            _groupCount = 0;
            _maxState = 0;
            _maxConnections = 0;
            AddNodeToGroup(nn);
            return GetNodeValue();
        }

        private static void AddNodeToGroup(int nn)
        {
            // TODO: port wire_module.cpp addNodeToGroup (~L1931-2001):
            //  - skip if nn already in _groupBuf
            //  - push nn; if NodeInfos[nn].Connections > _maxConnections -> _maxState = NodeStates[nn]; _maxConnections = ...
            //  - _groupFlags |= NodeInfos[nn].Flags
            //  - walk TlistC1c2s: while *p: gate=*p++, c2=*p++; if NodeStates[gate]!=0 AddNodeToGroup(c2)
            //  - walk TlistC1gnd: while *p: gate=*p++; if NodeStates[gate]!=0 { _groupFlags |= Gnd; break; }
            //  - walk TlistC1pwr: similarly for Pwr
            throw new NotImplementedException("WireCore.AddNodeToGroup — port wire_module.cpp addNodeToGroup");
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
