using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Phase 2.5 codegen Step 3 — auto-partition the netlist into macro-blocks.
        //
        // The partition model (from --dump-block + Gemini r2 §2.4):
        //   Boundaries:  pull-up nodes (Pullups > 0) + Npwr/Ngnd. These are latched / named / bus
        //                  nodes — the natural cut points between functional blocks.
        //   Blocks:      maximally-connected sets of NON-boundary nodes reachable through transistor
        //                channels and gate-pins without crossing a boundary. Each non-boundary node
        //                belongs to exactly one block. (Union-find style — done in one BFS sweep.)
        //   Inputs:      boundary nodes touched by this block's transistors but not "driven" by it.
        //   Outputs:     boundary nodes that the block DRIVES (the block contains transistors whose
        //                channel pulls the boundary down — gates inside, channel-end outside).
        //
        // Use case:
        //   Each block becomes a candidate macro-block for codegen. If the block has a clean
        //   boundary (inputs all classified, outputs all named), an LLVM-emitted function can
        //   replace S1's group-walk for the entire region — and CodegenOwned can include all the
        //   internal nodes (not just the named outputs as Step 2.5 currently does).
        //
        //   --dump-partition prints the size histogram + per-block summary, used to validate the
        //   partition (e.g. the ALU should show up as ~133-node block, matching --dump-block).

        public sealed class Block
        {
            public int Id;
            public int[] InternalNodes  = Array.Empty<int>();   // non-boundary nodes
            public int[] BoundaryInputs = Array.Empty<int>();   // boundary nodes touched (could be in or out)
            public int[] DrivenOutputs  = Array.Empty<int>();   // boundary nodes this block actively drives
            public int[] TransistorIds  = Array.Empty<int>();
            // Heuristic name: pick the most prominent named output, or fall back to "block-<id>"
            public string Label = "";
        }

        /// <summary>Auto-partition the post-Reset netlist. Returns blocks in descending internal-size
        /// order. Requires NodeCount > 0 (call after Reset / LoadSystem).</summary>
        public static List<Block> AutoPartition()
        {
            if (NodeCount < 3) throw new InvalidOperationException("AutoPartition: netlist not powered (call after Reset)");

            // ── Step A — classify nodes as boundary or internal ──
            var isBoundary = new bool[NodeCount];
            for (int nn = 0; nn < NodeCount; nn++)
            {
                var n = Nodes[nn];
                if (n == null) continue;
                if (n.Pullups > 0) isBoundary[nn] = true;
            }
            if ((uint)Npwr < (uint)NodeCount) isBoundary[Npwr] = true;
            if ((uint)Ngnd < (uint)NodeCount) isBoundary[Ngnd] = true;

            // ── Step B — single BFS sweep, growing one block per non-boundary connected component ──
            var blockOf  = new int[NodeCount];
            Array.Fill(blockOf, -1);
            var blocks   = new List<List<int>>();
            var binputs  = new List<HashSet<int>>();
            var btrans   = new List<HashSet<int>>();
            // For "driven outputs": collect (block_id, boundary_node) pairs where a transistor in
            // the block has its CHANNEL touching the boundary AND its GATE inside the block. That's
            // the canonical "block drives this boundary" relation.
            var bouts    = new List<HashSet<int>>();
            var queue    = new Queue<int>();

            for (int seed = 0; seed < NodeCount; seed++)
            {
                if (isBoundary[seed]) continue;
                if (blockOf[seed] >= 0) continue;
                var n = Nodes[seed];
                if (n == null) continue;

                int blockId = blocks.Count;
                var bnodes = new List<int>();
                var bin    = new HashSet<int>();
                var btr    = new HashSet<int>();
                var bo     = new HashSet<int>();
                blocks.Add(bnodes); binputs.Add(bin); btrans.Add(btr); bouts.Add(bo);

                queue.Clear();
                queue.Enqueue(seed);
                blockOf[seed] = blockId;
                bnodes.Add(seed);

                // BFS strategy (Step 3 v2): only follow CHANNEL-CHANNEL edges (c1↔c2 of a transistor)
                // to grow the block; the GATE of any transistor we touch is recorded as a boundary
                // input (signal flowing IN to control this block), never followed. This gives blocks
                // = "data-flow connected components" — much closer to the macro-block notion than
                // following gates (which over-merges everything sharing a control signal).
                while (queue.Count > 0)
                {
                    int v = queue.Dequeue();
                    var vnode = Nodes[v]; if (vnode == null) continue;

                    // (1) Transistors where v is a channel endpoint — follow ONLY the other channel
                    // end (data-flow neighbor). The gate is recorded as a control input.
                    foreach (int tid in vnode.C1c2s)
                    {
                        btr.Add(tid);
                        var t = Transistors[tid];
                        int gate  = t.Gate;
                        int other = (t.C1 == v) ? t.C2 : t.C1;
                        VisitChannelNeighbor(other, gate);
                        RecordControlInput(gate);
                    }
                    // (2) Transistors where v is the gate — DO NOT cross into channel ends. v is
                    // controlling something external; the channel ends belong to another block.
                    // But we still record them as boundary fanout (this block CONTROLS those nodes
                    // via the gate signal). For simplicity we don't track fanout here.
                    // (Not iterating vnode.Gates intentionally.)

                    void VisitChannelNeighbor(int u, int gateNode)
                    {
                        if (u == v) return;
                        if ((uint)u >= (uint)NodeCount) return;
                        if (isBoundary[u])
                        {
                            bin.Add(u);
                            // If the gate that mediates this transistor is itself non-boundary
                            // (a combinational control signal from this block), treat the boundary
                            // as DRIVEN by this block.
                            if (gateNode != Npwr && gateNode != Ngnd && (uint)gateNode < (uint)NodeCount && !isBoundary[gateNode])
                                bo.Add(u);
                            return;
                        }
                        if (blockOf[u] >= 0) return;
                        blockOf[u] = blockId;
                        bnodes.Add(u);
                        queue.Enqueue(u);
                    }
                    void RecordControlInput(int g)
                    {
                        if (g == v) return;
                        if ((uint)g >= (uint)NodeCount) return;
                        if (g == Npwr || g == Ngnd) return;  // always-on / always-off — not a meaningful input
                        // Whether boundary or internal, the gate is an INPUT to this block (controls a channel).
                        // Only record if it's a boundary (named/latched signal); internal gates are derived in-block.
                        if (isBoundary[g]) bin.Add(g);
                    }
                }
            }

            // ── Step C — convert to Block list, sort by size desc, derive labels ──
            var result = new List<Block>(blocks.Count);
            for (int i = 0; i < blocks.Count; i++)
            {
                var b = new Block
                {
                    Id = i,
                    InternalNodes  = blocks[i].ToArray(),
                    BoundaryInputs = binputs[i].ToArray(),
                    DrivenOutputs  = bouts[i].ToArray(),
                    TransistorIds  = btrans[i].ToArray(),
                };
                b.Label = PickBlockLabel(b);
                result.Add(b);
            }
            result.Sort((a, b) => b.InternalNodes.Length.CompareTo(a.InternalNodes.Length));
            // Re-id by size order so block-0 is the largest.
            for (int i = 0; i < result.Count; i++) result[i].Id = i;
            return result;
        }

        // Pick a representative name for a block. Priority: best non-supply non-anonymous DrivenOutput
        // > best DrivenOutput > best non-supply BoundaryInput. Skip vcc/vss (they're labels every
        // block touches, not useful for identification).
        private static string PickBlockLabel(Block b)
        {
            string? best = null;
            int bestScore = -1;
            void Consider(int nn, int baseScore)
            {
                if ((uint)nn >= (uint)Nodes.Count) return;
                var n = Nodes[nn]; if (n == null) return;
                string name = n.Name; if (string.IsNullOrEmpty(name)) return;
                int score = baseScore;
                // Penalise generic supply / anonymous numeric names
                if (name == "vcc" || name == "vss" || name == "clk" || name == "clk0") score -= 100;
                if (int.TryParse(name, out _)) score -= 50;   // anonymous numeric node-id placeholder
                // Reward semantically meaningful subsystem-prefixed names
                if (name.StartsWith("cpu.")) score += 5;
                if (name.StartsWith("ppu.")) score += 5;
                if (name.StartsWith("apu.")) score += 5;
                // Reward shorter names (registers/buses tend to be short like "cpu.a0", "cpu.sb3")
                score -= Math.Min(20, name.Length / 4);
                if (score > bestScore) { bestScore = score; best = name; }
            }
            // DrivenOutputs are the most semantically meaningful (this block COMPUTES these)
            foreach (int nn in b.DrivenOutputs) Consider(nn, 50);
            foreach (int nn in b.BoundaryInputs) Consider(nn, 10);
            return best ?? $"block-{b.Id}";
        }

        /// <summary>Summary stats over the partition list.</summary>
        public static (int blockCount, int unassigned, int totalInternal, int boundaryNodes)
            SummarisePartition(List<Block> blocks)
        {
            int totalInt = 0; foreach (var b in blocks) totalInt += b.InternalNodes.Length;
            int bound = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                var n = Nodes[nn]; if (n == null) continue;
                if (n.Pullups > 0) bound++;
            }
            // unassigned = non-boundary nodes that ended up in no block (should be 0)
            int unassigned = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                var n = Nodes[nn]; if (n == null) continue;
                if (n.Pullups > 0) continue;
                if (nn == Npwr || nn == Ngnd) continue;
                bool inAnyBlock = false;
                foreach (var b in blocks) { if (Array.IndexOf(b.InternalNodes, nn) >= 0) { inAnyBlock = true; break; } }
                if (!inAnyBlock) unassigned++;
            }
            return (blocks.Count, unassigned, totalInt, bound);
        }
    }
}
