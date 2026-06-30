using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        public static bool EnableAggressivePulldownMacroLowering;
        public static string LastAggressivePulldownMacroStats = "(aggressive pulldown macro disabled)";

        private static int AggressivePulldownMacroMapLen;
        private static int* AggressivePulldownMacroInputOffsetByOutput;
        private static ushort* AggressivePulldownMacroInputCountByOutput;
        private static ushort* AggressivePulldownMacroInputGates;
        private static int* AggressivePulldownMacroOutputOffsetByGate;
        private static ushort* AggressivePulldownMacroOutputCountByGate;
        private static ushort* AggressivePulldownMacroOutputNodes;

        internal static void ResetAggressivePulldownMacros()
        {
            AggressivePulldownMacroMapLen = 0;
            AggressivePulldownMacroInputOffsetByOutput = null;
            AggressivePulldownMacroInputCountByOutput = null;
            AggressivePulldownMacroInputGates = null;
            AggressivePulldownMacroOutputOffsetByGate = null;
            AggressivePulldownMacroOutputCountByGate = null;
            AggressivePulldownMacroOutputNodes = null;
            LastAggressivePulldownMacroStats = "(aggressive pulldown macro disabled)";
        }

        public static void AggressivePulldownMacroLowerNetlist()
        {
            int n = _nodes.Count;
            int oldTrans = _transistors.Count;
            var force = new HashSet<int>(_forceComputeList);
            var skipTrans = new bool[oldTrans];
            var inputsByOutput = new int[n][];
            var outputsByGateLists = new List<int>?[n];

            int macros = 0, rowsRemoved = 0, named = 0, maxInputs = 0, totalInputRefs = 0;
            for (int nn = 3; nn < n; nn++)
            {
                var node = _nodes[nn];
                if (node == null) continue;
                if (node.Pullups <= 0 || node.Callback != null || force.Contains(nn)) continue;
                if (node.C1c2s.Count == 0) continue;

                bool ok = true;
                var rows = new List<int>(node.C1c2s.Count);
                var gates = new HashSet<int>();
                foreach (int tid in node.C1c2s)
                {
                    if ((uint)tid >= (uint)oldTrans) { ok = false; break; }
                    var t = _transistors[tid];
                    int other = GetOtherEndpoint(t, nn);
                    if (other != Ngnd) { ok = false; break; }
                    rows.Add(tid);
                    if (t.Gate != Ngnd) gates.Add(t.Gate);
                }
                if (!ok || rows.Count == 0 || gates.Count == 0) continue;

                var gateArray = new int[gates.Count];
                gates.CopyTo(gateArray);
                inputsByOutput[nn] = gateArray;
                totalInputRefs += gateArray.Length;
                foreach (int gate in gateArray)
                {
                    if ((uint)gate >= (uint)n) continue;
                    (outputsByGateLists[gate] ??= new List<int>()).Add(nn);
                }
                foreach (int tid in rows) skipTrans[tid] = true;

                macros++;
                rowsRemoved += rows.Count;
                if (HasBuildName(nn)) named++;
                if (gateArray.Length > maxInputs) maxInputs = gateArray.Length;
            }

            if (macros == 0)
            {
                ResetAggressivePulldownMacros();
                LastAggressivePulldownMacroStats = "aggressive-pulldown-macro: no candidates";
                return;
            }

            int watchedGates = 0, totalOutputRefs = 0, maxOutputsPerGate = 0;
            for (int gate = 0; gate < outputsByGateLists.Length; gate++)
            {
                var list = outputsByGateLists[gate];
                if (list == null) continue;
                watchedGates++;
                totalOutputRefs += list.Count;
                if (list.Count > maxOutputsPerGate) maxOutputsPerGate = list.Count;
            }

            RebuildTransistorsSkipping(skipTrans);

            BuildAggressivePulldownMacroUnmanagedMaps(inputsByOutput, outputsByGateLists, n, totalInputRefs, totalOutputRefs);
            LastAggressivePulldownMacroStats =
                $"aggressive-pulldown-macro: {macros:N0} outputs ({named:N0} named), " +
                $"removed {rowsRemoved:N0}/{oldTrans:N0} pulldown rows; watched gates {watchedGates:N0}; " +
                $"input refs {totalInputRefs:N0}, output refs {totalOutputRefs:N0}; max inputs {maxInputs:N0}, max outputs/gate {maxOutputsPerGate:N0}; " +
                $"transistors {oldTrans:N0} -> {_transistors.Count:N0}";
        }

        private static void BuildAggressivePulldownMacroUnmanagedMaps(
            int[][] inputsByOutput,
            List<int>?[] outputsByGateLists,
            int n,
            int totalInputRefs,
            int totalOutputRefs)
        {
            AggressivePulldownMacroMapLen = n;
            AggressivePulldownMacroInputOffsetByOutput = AllocHandlerArray<int>(n);
            AggressivePulldownMacroInputCountByOutput = AllocHandlerArray<ushort>(n);
            AggressivePulldownMacroInputGates = AllocHandlerArray<ushort>(totalInputRefs > 0 ? totalInputRefs : 1);
            AggressivePulldownMacroOutputOffsetByGate = AllocHandlerArray<int>(n);
            AggressivePulldownMacroOutputCountByGate = AllocHandlerArray<ushort>(n);
            AggressivePulldownMacroOutputNodes = AllocHandlerArray<ushort>(totalOutputRefs > 0 ? totalOutputRefs : 1);

            int w = 0;
            for (int nn = 0; nn < inputsByOutput.Length; nn++)
            {
                var gates = inputsByOutput[nn];
                if (gates == null) continue;
                AggressivePulldownMacroInputOffsetByOutput[nn] = w;
                AggressivePulldownMacroInputCountByOutput[nn] = (ushort)gates.Length;
                for (int i = 0; i < gates.Length; i++) AggressivePulldownMacroInputGates[w++] = (ushort)gates[i];
            }

            w = 0;
            for (int gate = 0; gate < outputsByGateLists.Length; gate++)
            {
                var outputs = outputsByGateLists[gate];
                if (outputs == null) continue;
                AggressivePulldownMacroOutputOffsetByGate[gate] = w;
                AggressivePulldownMacroOutputCountByGate[gate] = (ushort)outputs.Count;
                for (int i = 0; i < outputs.Count; i++) AggressivePulldownMacroOutputNodes[w++] = (ushort)outputs[i];
            }
        }

        private static void RebuildTransistorsSkipping(bool[] skipTrans)
        {
            var old = new List<Transistor>(_transistors);
            foreach (var node in _nodes)
            {
                if (node == null) continue;
                node.Gates.Clear();
                node.C1c2s.Clear();
            }
            _transistors.Clear();
            _transistorSet.Clear();

            for (int i = 0; i < old.Count; i++)
            {
                if (i < skipTrans.Length && skipTrans[i]) continue;
                var t = old[i];
                AddTransistor(t.Name, t.Gate, t.C1, t.C2, t.IsWeak);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int GetAggressivePulldownMacroInputCount(int nn, out ushort* gates)
        {
            ushort* countByOutput = AggressivePulldownMacroInputCountByOutput;
            if (countByOutput == null || (uint)nn >= (uint)AggressivePulldownMacroMapLen)
            {
                gates = null;
                return 0;
            }
            int count = countByOutput[nn];
            gates = count == 0 ? null : AggressivePulldownMacroInputGates + AggressivePulldownMacroInputOffsetByOutput[nn];
            return count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RecalcAggressivePulldownMacro(int nn, ushort* gates, int count)
        {
            byte* nodeStates = NodeStates;
            int any = 0;
            for (int i = 0; i < count; i++) any |= nodeStates[gates[i]];
            SetNodeState(nn, any != 0 ? (byte)0 : (byte)1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnqueueAggressivePulldownMacroDependents(int nn)
        {
            ushort* countByGate = AggressivePulldownMacroOutputCountByGate;
            if (countByGate == null || (uint)nn >= (uint)AggressivePulldownMacroMapLen) return;
            int count = countByGate[nn];
            if (count == 0) return;
            ushort* outputs = AggressivePulldownMacroOutputNodes + AggressivePulldownMacroOutputOffsetByGate[nn];
            for (int i = 0; i < count; i++) EnqueueNode(outputs[i]);
        }
    }
}
