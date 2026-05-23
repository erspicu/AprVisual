using System;
using System.Collections.Generic;
using System.Text;
using AprVisual.Sim;

namespace AprVisual.Codegen
{
    /// <summary>
    /// AOT compiler — analyzes the netlist structure around a target output node and emits an
    /// equivalent C# expression (or a compiled delegate) that computes the node's value as a
    /// pure function of its input nodes. First-MVP: inverter pattern only (single pull-down +
    /// pull-up). Future expansion: NAND/NOR ladders, multi-driver wired-OR, latches.
    ///
    /// Approach:
    ///   1. Inspect target node's transistor list (Node.C1c2s).
    ///   2. Classify each channel transistor by role:
    ///        - PullDown: gate=non-supply, other_end = Ngnd. Pulls output low when gate high.
    ///        - PullToBus: gate=non-supply, other_end = another non-supply node. Pass transistor.
    ///        - PullToSupply: gate=non-supply, other_end = Npwr. Pulls output high when gate high.
    ///   3. Pattern match:
    ///        - Single PullDown + node has Pullups + no other-active channels → INVERTER pattern.
    ///        - Multiple PullDowns + node has Pullups + same configuration → NOR pattern.
    ///        - Other patterns: not yet supported; return null.
    ///
    /// All emitted code assumes byte* nodeStates is the live S1 NodeStates array (same shape as
    /// the hand-coded AotBlocks functions).
    /// </summary>
    public static unsafe class AotEmitter
    {
        public sealed class EmitResult
        {
            public string? CSharpExpr;             // e.g. "(nodeStates[9086] == 0) ? (byte)1 : (byte)0"
            public Func<IntPtr, byte>? Compiled;   // pre-built delegate (IntPtr = byte*; cast inside)
            public string Pattern = "";            // "inverter" / "nor" / "unsupported(...)"
            public int OutputId;
            public int[] InputIds = Array.Empty<int>();
        }

        /// <summary>Try to emit AOT code for a single output node. Returns EmitResult with
        /// CSharpExpr + Compiled when pattern matches; otherwise EmitResult.Pattern starts with
        /// "unsupported".</summary>
        public static EmitResult EmitForNode(int outputId)
        {
            var result = new EmitResult { OutputId = outputId };
            if ((uint)outputId >= (uint)WireCore.Nodes.Count) { result.Pattern = "unsupported(out-of-range)"; return result; }
            var node = WireCore.Nodes[outputId];
            if (node == null) { result.Pattern = "unsupported(null-node)"; return result; }

            // Inverter requires a pull-up on the output (default-high without driver)
            if (node.Pullups == 0) { result.Pattern = "unsupported(no-pullup)"; return result; }

            // Classify channel transistors
            var pullDownGates = new List<int>();
            int passToBusCount = 0;
            foreach (int tid in node.C1c2s)
            {
                var t = WireCore.Transistors[tid];
                int otherEnd = (t.C1 == outputId) ? t.C2 : t.C1;
                int gate = t.Gate;
                if (gate == WireCore.Npwr || gate == WireCore.Ngnd) continue;  // always-on/off, lowered out usually
                if (otherEnd == WireCore.Ngnd) pullDownGates.Add(gate);
                else if (otherEnd == WireCore.Npwr) { /* pull-to-power: unusual in NMOS, skip */ }
                else passToBusCount++;
            }

            if (pullDownGates.Count == 1 && passToBusCount <= 1)
            {
                // INVERTER pattern: NOT(gate). The optional pass-to-bus is the latch write path
                // which is dormant in steady state; verified empirically against S1 for IR inverters.
                int gateId = pullDownGates[0];
                result.Pattern = passToBusCount == 0 ? "inverter" : "inverter+latch-write";
                result.InputIds = new[] { gateId };
                result.CSharpExpr = $"(nodeStates[{gateId}] == 0) ? (byte)1 : (byte)0";
                int capturedOutput = outputId, capturedGate = gateId;   // capture for closure
                result.Compiled = (IntPtr pNs) =>
                {
                    byte* ns = (byte*)pNs;
                    return (byte)(ns[capturedGate] == 0 ? 1 : 0);
                };
                return result;
            }

            if (pullDownGates.Count > 1 && passToBusCount == 0)
            {
                // NOR pattern: NOT(g0 | g1 | ... | gN)
                result.Pattern = "nor";
                result.InputIds = pullDownGates.ToArray();
                var sb = new StringBuilder();
                sb.Append("(");
                for (int i = 0; i < pullDownGates.Count; i++)
                {
                    if (i > 0) sb.Append(" | ");
                    sb.Append($"nodeStates[{pullDownGates[i]}]");
                }
                sb.Append(") == 0 ? (byte)1 : (byte)0");
                result.CSharpExpr = sb.ToString();
                int[] gates = pullDownGates.ToArray();
                result.Compiled = (IntPtr pNs) =>
                {
                    byte* ns = (byte*)pNs;
                    byte any = 0;
                    foreach (int g in gates) any |= ns[g];
                    return (byte)(any == 0 ? 1 : 0);
                };
                return result;
            }

            result.Pattern = $"unsupported(pulldowns={pullDownGates.Count}, passToBus={passToBusCount})";
            return result;
        }
    }
}
