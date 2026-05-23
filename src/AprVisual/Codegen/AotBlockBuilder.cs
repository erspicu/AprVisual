using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AprVisual.Sim;

namespace AprVisual.Codegen
{
    /// <summary>
    /// Phase C-5 — block-level AOT emit. Takes a Partition.Block (from WireCore.Partition.cs Step 3)
    /// and builds an in-memory "AOT block": the set of (nodeId, compiled-delegate, pattern-name)
    /// triples for every node in the block that AotEmitter can pattern-match.
    ///
    /// Use case:
    ///   1. Partitioner finds macro-block (e.g. block #16 = ppu.finex1, 188 internal nodes).
    ///   2. AotBlockBuilder builds the eval list — typically 80-100% of internal nodes have a
    ///      delegate; rest are no-pullup mids that nothing reads from outside.
    ///   3. AotVerifier.VerifyBlock runs S1, snapshots NodeStates each hc, calls the block's
    ///      Eval on the snapshot, compares predicted vs actual for the block's owned nodes.
    ///   4. If byte-equal across N hc → the block is correctly AOT-able.
    ///
    /// Phase C-5 NEXT: emit the actual `.cs` source code for the block (today this is just an
    /// in-memory delegate list; the .cs emission is task #74).
    /// </summary>
    public sealed class AotBlock
    {
        public int BlockId;
        public string Label = "";
        public List<(int nodeId, string pattern, Func<IntPtr, byte> compiled)> Evals = new();
        public int TotalInternalNodes;
        public int UnsupportedNodes;
        public Dictionary<string, int> PatternHisto = new();

        public string ShortSummary =>
            $"AotBlock #{BlockId} ({Label}): {Evals.Count}/{TotalInternalNodes} nodes emittable ({(double)Evals.Count / Math.Max(1, TotalInternalNodes):P1}), patterns: {string.Join(",", PatternHisto.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}={kv.Value}"))}";
    }

    public static unsafe class AotBlockBuilder
    {
        /// <summary>Build the AOT eval list for a partition block. Skips nodes the emitter can't
        /// pattern-match (most commonly anonymous no-pullup mids that aren't read externally).</summary>
        internal static AotBlock Build(WireCore.Block block)
        {
            var ab = new AotBlock
            {
                BlockId = block.Id,
                Label = block.Label,
                TotalInternalNodes = block.InternalNodes.Length,
            };
            foreach (int nn in block.InternalNodes)
            {
                var er = AotEmitter.EmitForNode(nn);
                string p = er.Pattern;
                if (p.StartsWith("unsupported(")) { p = "unsupported"; ab.UnsupportedNodes++; }
                ab.PatternHisto[p] = ab.PatternHisto.TryGetValue(p, out int c) ? c + 1 : 1;
                if (er.Compiled != null) ab.Evals.Add((nn, er.Pattern, er.Compiled));
            }
            // Also emit for the driven outputs (they're boundary nodes by partitioner's view, but
            // we can still try to eval them as a "block output" verification target).
            foreach (int nn in block.DrivenOutputs)
            {
                if (nn == WireCore.Npwr || nn == WireCore.Ngnd) continue;
                if (ab.Evals.Any(e => e.nodeId == nn)) continue;     // already emitted
                var er = AotEmitter.EmitForNode(nn);
                if (er.Compiled != null) ab.Evals.Add((nn, er.Pattern + "[output]", er.Compiled));
            }
            return ab;
        }

        /// <summary>Evaluate the block: for each (nodeId, delegate), call delegate(nodeStates) and
        /// WRITE the result back to nodeStates[nodeId]. Order = registration order (Build order =
        /// partition.InternalNodes order). For verification we usually snapshot first so writes
        /// don't pollute the comparison.</summary>
        public static void EvalAndWrite(AotBlock block, byte* nodeStates)
        {
            IntPtr p = (IntPtr)nodeStates;
            foreach (var (nodeId, _, compiled) in block.Evals)
                nodeStates[nodeId] = compiled(p);
        }

        /// <summary>Evaluate the block against a SNAPSHOT (read-only) — used by the verifier so
        /// each prediction reads from the SAME baseline that S1 saw, without write-order effects.
        /// Fills `predicted[i] = compiled_i(snapshot)` for i in block.Evals.</summary>
        public static byte[] EvalToBuffer(AotBlock block, byte* snapshot)
        {
            IntPtr p = (IntPtr)snapshot;
            var output = new byte[block.Evals.Count];
            for (int i = 0; i < block.Evals.Count; i++)
                output[i] = block.Evals[i].compiled(p);
            return output;
        }

        /// <summary>Phase C-5 task #74: emit actual C# source code for one block. The generated
        /// .cs has a static class with an Eval(byte*) method containing the eval expression for
        /// every emittable node. This is the "AOT artifact" — what a future build step would write
        /// to disk + compile into the simulator at AOT time (vs runtime delegate today). </summary>
        internal static string EmitSource(AotBlock block, WireCore.Block partBlock)
        {
            var sb = new StringBuilder();
            string safeName = SafeIdent(block.Label);
            sb.AppendLine($"// Auto-generated by AotBlockBuilder.EmitSource");
            sb.AppendLine($"// Source partition block: #{block.BlockId} ({block.Label})");
            sb.AppendLine($"//   internal nodes : {partBlock.InternalNodes.Length}");
            sb.AppendLine($"//   driven outputs : {partBlock.DrivenOutputs.Length}");
            sb.AppendLine($"//   boundary inputs: {partBlock.BoundaryInputs.Length}");
            sb.AppendLine($"//   emittable      : {block.Evals.Count} / {block.TotalInternalNodes} internal + {partBlock.DrivenOutputs.Length} outputs");
            sb.AppendLine($"//   patterns       : {string.Join(", ", block.PatternHisto.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}={kv.Value}"))}");
            sb.AppendLine();
            sb.AppendLine($"namespace AprVisual.Codegen.Generated");
            sb.AppendLine($"{{");
            sb.AppendLine($"    public static unsafe class Block_{block.BlockId}_{safeName}");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        /// <summary>Evaluate block #{block.BlockId} ({block.Label}). Reads inputs from");
            sb.AppendLine($"        /// nodeStates, writes outputs to the same array.</summary>");
            sb.AppendLine($"        public static void Eval(byte* nodeStates)");
            sb.AppendLine($"        {{");
            foreach (var (nn, pattern, _) in block.Evals)
            {
                var er = AotEmitter.EmitForNode(nn);
                if (er.CSharpExpr == null) continue;
                var node = WireCore.Nodes[nn];
                string nodeName = string.IsNullOrEmpty(node?.Name) ? "(anonymous)" : node!.Name;
                sb.AppendLine($"            // {pattern,-20}  {nodeName} (id {nn}) ← inputs [{string.Join(",", er.InputIds)}]");
                sb.AppendLine($"            nodeStates[{nn}] = (byte)({er.CSharpExpr});");
            }
            sb.AppendLine($"        }}");
            sb.AppendLine($"    }}");
            sb.AppendLine($"}}");
            return sb.ToString();
        }

        internal static string SafeIdent(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            string r = sb.ToString();
            return string.IsNullOrEmpty(r) || char.IsDigit(r[0]) ? "_" + r : r;
        }

        /// <summary>Phase D-1: mass-emit every codegen-attractive block into ONE master .cs file
        /// containing multiple static classes. Used for the future AotEngine that will dispatch
        /// to these as the simulator's runtime (replacing S1's BFS).</summary>
        internal static string EmitMasterSource(List<(WireCore.Block pb, AotBlock ab)> blocks, string romName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"// Auto-generated by AotBlockBuilder.EmitMasterSource");
            sb.AppendLine($"// Source ROM      : {romName}");
            sb.AppendLine($"// Generated time  : {DateTime.UtcNow:O}");
            sb.AppendLine($"// Block count     : {blocks.Count}");
            int totalEvals = 0; foreach (var (_, ab) in blocks) totalEvals += ab.Evals.Count;
            sb.AppendLine($"// Total emitted   : {totalEvals:N0} node evaluations across {blocks.Count} blocks");
            sb.AppendLine();
            sb.AppendLine($"namespace AprVisual.Codegen.Generated");
            sb.AppendLine($"{{");

            // Dispatcher class — single Eval(byte*) that calls every block's Eval in order
            sb.AppendLine($"    /// <summary>Aggregate entry point: call every emitted block's Eval in registration order.");
            sb.AppendLine($"    /// In Phase D-3 this gets event-driven dispatching; here we just dispatch all.</summary>");
            sb.AppendLine($"    public static unsafe class AotEngine");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        public static void EvalAllBlocks(byte* nodeStates)");
            sb.AppendLine($"        {{");
            foreach (var (pb, ab) in blocks)
            {
                if (ab.Evals.Count == 0) continue;
                sb.AppendLine($"            Block_{pb.Id}_{SafeIdent(pb.Label)}.Eval(nodeStates);");
            }
            sb.AppendLine($"        }}");
            sb.AppendLine($"    }}");
            sb.AppendLine();

            // Per-block classes
            foreach (var (pb, ab) in blocks)
            {
                if (ab.Evals.Count == 0) continue;
                sb.AppendLine($"    // ─────────────────────────────────────────────────────────────────");
                sb.AppendLine($"    // Block #{pb.Id} — {pb.Label}");
                sb.AppendLine($"    //   internal: {pb.InternalNodes.Length},  driven outputs: {pb.DrivenOutputs.Length},  inputs: {pb.BoundaryInputs.Length}");
                sb.AppendLine($"    //   AOT-emittable: {ab.Evals.Count} nodes");
                sb.AppendLine($"    //   patterns: {string.Join(", ", ab.PatternHisto.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}={kv.Value}"))}");
                sb.AppendLine($"    public static unsafe class Block_{pb.Id}_{SafeIdent(pb.Label)}");
                sb.AppendLine($"    {{");
                sb.AppendLine($"        public static void Eval(byte* nodeStates)");
                sb.AppendLine($"        {{");
                foreach (var (nn, pattern, _) in ab.Evals)
                {
                    var er = AotEmitter.EmitForNode(nn);
                    if (er.CSharpExpr == null) continue;
                    var node = WireCore.Nodes[nn];
                    string nodeName = string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name;
                    sb.AppendLine($"            nodeStates[{nn}] = (byte)({er.CSharpExpr});  // {pattern,-22} {nodeName}");
                }
                sb.AppendLine($"        }}");
                sb.AppendLine($"    }}");
                sb.AppendLine();
            }

            sb.AppendLine($"}}");
            return sb.ToString();
        }
    }
}
