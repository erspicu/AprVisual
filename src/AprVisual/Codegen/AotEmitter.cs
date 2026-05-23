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
            var passTransistors = new List<(int gate, int other)>();   // for mux_bus
            foreach (int tid in node.C1c2s)
            {
                var t = WireCore.Transistors[tid];
                int otherEnd = (t.C1 == outputId) ? t.C2 : t.C1;
                int gate = t.Gate;
                if (gate == WireCore.Npwr || gate == WireCore.Ngnd) continue;  // always-on/off, lowered out usually
                if (otherEnd == WireCore.Ngnd) pullDownGates.Add(gate);
                else if (otherEnd == WireCore.Npwr) { /* pull-to-power: unusual in NMOS, skip */ }
                else { passToBusCount++; passTransistors.Add((gate, otherEnd)); }
            }

            if (pullDownGates.Count == 1 && passToBusCount <= 1)
            {
                // INVERTER pattern: NOT(gate). The optional pass-to-bus is the latch-write path
                // which is dormant in steady state; verified empirically against S1 for IR inverters.
                // C-3: tried passToBus≤2 but it cannibalised mux_bus+pulldown (which was 100%
                // PERFECT) and introduced 2.3% misses on the formerly-mux_bus subset → reverted.
                // Nodes with passToBus≥2 fall through to mux_bus pattern (correct model).
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

            // ── Generalised NOR (with optional dormant latch-write pass) ──
            //    output has N pull-downs (N >= 2) + 0..1 pass-to-bus (latch-write style;
            //    inert in steady state per the inverter+latch-write empirical result).
            //    Emit: NOT(g0 | g1 | ... | gN).
            //    C-3 narrowed from passToBus≤2 to ≤1: pass=2 nodes have 0.5%-5.6% misses (verified
            //    nor6+pass was 5.58% wrong), suggesting the second pass isn't dormant — it's
            //    likely a real driver. Those nodes fall through to mux_bus (when applicable).
            if (pullDownGates.Count >= 2 && passToBusCount <= 1)
            {
                string suffix = passToBusCount == 0 ? "" : "+pass";
                result.Pattern = $"nor{pullDownGates.Count}{suffix}";
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

            // ── NAND ladder (Phase C): output O has pull-up; ONE pass-to-mid transistor (gate=A,
            //    other_end=M); M has no pull-up and ONE pull-down (gate=B, other=Gnd). Topology:
            //         O --[A:T1]-- M --[B:T2]-- Gnd
            //    Function: O = NOT(A AND B). If A=B=1 both transistors conduct, M→O pulled to Gnd
            //    through the series chain; else O floats up via pull-up.
            //    This only matches if there are NO other pull-downs on O (would convert to AOI).
            if (pullDownGates.Count == 0 && passToBusCount >= 1)
            {
                // Look for exactly one pass-to-mid candidate where mid has the NAND shape
                int? gateA = null, gateB = null;
                int candidatesFound = 0;
                foreach (int tid in node.C1c2s)
                {
                    var t1 = WireCore.Transistors[tid];
                    int gate1 = t1.Gate;
                    int mid = (t1.C1 == outputId) ? t1.C2 : t1.C1;
                    if (gate1 == WireCore.Npwr || gate1 == WireCore.Ngnd) continue;
                    if (mid == WireCore.Npwr || mid == WireCore.Ngnd) continue;
                    var midNode = WireCore.Nodes[mid]; if (midNode == null) continue;
                    if (midNode.Pullups > 0) continue;        // mid must be dynamic (no pull-up)
                    // mid must have exactly one pull-down (to Gnd) + no other channels of significance
                    int midPullDownGate = -1; int midPullDownCount = 0; int midOtherChannelCount = 0;
                    foreach (int mtid in midNode.C1c2s)
                    {
                        if (mtid == tid) continue;             // skip the link back to O
                        var t2 = WireCore.Transistors[mtid];
                        int g2 = t2.Gate;
                        int o2 = (t2.C1 == mid) ? t2.C2 : t2.C1;
                        if (g2 == WireCore.Npwr || g2 == WireCore.Ngnd) continue;
                        if (o2 == WireCore.Ngnd) { midPullDownGate = g2; midPullDownCount++; }
                        else if (o2 != WireCore.Npwr) midOtherChannelCount++;
                    }
                    if (midPullDownCount == 1 && midOtherChannelCount == 0)
                    {
                        if (candidatesFound == 0) { gateA = gate1; gateB = midPullDownGate; }
                        candidatesFound++;
                    }
                }
                if (candidatesFound == 1 && gateA.HasValue && gateB.HasValue)
                {
                    result.Pattern = "nand";
                    result.InputIds = new[] { gateA.Value, gateB.Value };
                    result.CSharpExpr = $"((nodeStates[{gateA}] & nodeStates[{gateB}]) == 0) ? (byte)1 : (byte)0";
                    int gA = gateA.Value, gB = gateB.Value;
                    result.Compiled = (IntPtr pNs) =>
                    {
                        byte* ns = (byte*)pNs;
                        return (byte)((ns[gA] & ns[gB]) == 0 ? 1 : 0);
                    };
                    return result;
                }
            }

            // ── mux_bus (Phase C-2): multi-driver wired-OR bus. Topology:
            //      output -- [pull-up]
            //      output -- [pulldown_gate:T] -- Gnd   (0 or 1 of these)
            //      output -- [sel_i:T_i] -- src_i      (2+ pass transistors with non-supply other-end)
            //   NMOS semantics: output is high (pull-up) UNLESS pull-down conducts OR any active
            //   pass connects to a low source. Eval:
            //      result = (pullDown_gate active ? 0 : 1)
            //      for each (sel,src): if sel high AND src low → result = 0
            //   This is "wired-OR with GND-wins" model — verified empirically against S1 in Phase A.
            //   Assumption: src_i NodeStates is up-to-date at sample time (i.e., src is driven by
            //   another already-resolved node — typically a register or inverter output).
            if (pullDownGates.Count <= 1 && passToBusCount >= 2)
            {
                int pullDownGate = pullDownGates.Count == 1 ? pullDownGates[0] : -1;
                var passes = passTransistors.ToArray();
                int[] allInputs = new int[passes.Length * 2 + (pullDownGate >= 0 ? 1 : 0)];
                int k = 0; if (pullDownGate >= 0) allInputs[k++] = pullDownGate;
                foreach (var (g, s) in passes) { allInputs[k++] = g; allInputs[k++] = s; }
                result.Pattern = pullDownGates.Count == 1 ? "mux_bus+pulldown" : "mux_bus";
                result.InputIds = allInputs;
                var sb = new StringBuilder();
                if (pullDownGate >= 0) sb.Append($"(nodeStates[{pullDownGate}] != 0) ? (byte)0 : ");
                sb.Append("(byte)(");
                for (int i = 0; i < passes.Length; i++)
                {
                    if (i > 0) sb.Append(" & ");
                    sb.Append($"((nodeStates[{passes[i].gate}] == 0) ? 1 : nodeStates[{passes[i].other}])");
                }
                sb.Append(")");
                result.CSharpExpr = sb.ToString();
                int pdGate = pullDownGate;
                result.Compiled = (IntPtr pNs) =>
                {
                    byte* ns = (byte*)pNs;
                    if (pdGate >= 0 && ns[pdGate] != 0) return 0;
                    byte acc = 1;
                    foreach (var (g, s) in passes)
                        if (ns[g] != 0) acc &= ns[s];   // active pass: AND-in the source
                    return acc;
                };
                return result;
            }

            result.Pattern = $"unsupported(pulldowns={pullDownGates.Count}, passToBus={passToBusCount})";
            return result;
        }

        /// <summary>Scan every node in the loaded netlist; tally pattern types. Returns a
        /// dictionary of pattern → count. Used by --aot-coverage to gauge emitter completeness.</summary>
        public static Dictionary<string, int> ScanCoverage()
        {
            var histo = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int nn = 0; nn < WireCore.NodeCount; nn++)
            {
                if (nn == WireCore.Npwr || nn == WireCore.Ngnd) continue;
                var n = WireCore.Nodes[nn]; if (n == null) continue;
                var er = EmitForNode(nn);
                string key = er.Pattern;
                // Bucket the unsupported(*) family by its first detail to keep histogram compact
                if (key.StartsWith("unsupported("))
                {
                    int colon = key.IndexOf(',');
                    if (colon > 0) key = key.Substring(0, colon) + ",...)";
                }
                histo[key] = histo.TryGetValue(key, out int c) ? c + 1 : 1;
            }
            return histo;
        }
    }
}
