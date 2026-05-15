using System;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── X (math-algos Phase 3): Oblivious eval — the algorithmic experiment.
        //
        // Gemini's full X was: bipartite split (86% pure-logic + 14% pass-transistor bus) +
        // topo-sort the logic subgraph + bit-sliced SIMD batch eval ("AVX2 一條指令同時模擬 256
        // 個 logic gate") + S1 BFS for bus. Predicted ~2× over S1.
        //
        // The catch with the full design: true bit-sliced SIMD across nodes requires per-node
        // structural classification (which gates feed which pull-up/down stack, in what
        // combination) -- i.e. you need an extracted IR per node (S2's DriveAnalysis +
        // NextStateBuilder), which doesn't exist on this branch. Without that, SIMD batching is
        // pointless (each node's eval is irregular). You CAN bit-slice ACROSS instances (main's
        // S4.4a bit-sliced emit, 64 chips per ulong) but the user explicitly cancelled multi-
        // instance, and per-instance the AVX2 lanes are idle.
        //
        // So this file implements the *algorithmic* half of X without the SIMD half: drop the
        // dirty-set entirely and Oblivious-iterate over all nodes until fixpoint per half-cycle.
        // It uses S1's existing ComputeNodeGroup / SetNodeState per node (so it's "S1 minus the
        // event queue, plus a settle loop"). The point isn't to win on speed — without SIMD
        // width, this should LOSE to S1 by a large factor (the same algorithmic redundancy main
        // branch's AOT codegen lost on). The point is to *quantify* how much the dirty-set
        // matters in isolation, with the same per-node compute cost as S1.
        //
        // Gated behind --oblivious / WireCore.EnableOblivious. Combinable with --rcm and
        // --simd-queue; replaces the BFS body of ProcessQueue when on.

        public static bool EnableOblivious = false;
        public static int LastObliviousIters;

        internal static void ProcessAllOblivious()
        {
            // Drain whatever's in the dirty queue first into NodeStates -- the handlers (clk toggle)
            // and any pre-Oblivious SetHigh/SetLow may have enqueued nodes. Then ignore the queue.
            // We re-derive everything from scratch by sweeping all normal nodes.

            const int MaxSweeps = 32;
            int n = NodeCount;
            int sweeps = 0;
            bool changed;
            do
            {
                changed = false;
                sweeps++;
                for (int nn = 3; nn < n; nn++)
                {
                    // Skip dead/empty slots
                    if (Nodes[nn] == null) continue;
                    if (nn == Npwr || nn == Ngnd) continue;

                    byte old = NodeStates[nn];
                    // ComputeNodeGroup walks nn's conducting group, fills _groupBuf / _groupFlags,
                    // returns the resolved value. SetNodeState then writes it (with early-return
                    // when unchanged). This is S1's per-node compute, just driven obliviously.
                    byte newState = EnableSimdQueue ? ComputeNodeGroupSimd(nn) : ComputeNodeGroup(nn);
                    for (int i = 0; i < _groupCount; i++)
                    {
                        int m = _groupBuf[i];
                        if (NodeStates[m] != newState) { NodeStates[m] = newState; changed = true; }
                    }
                    if (NodeStates[nn] != old) changed = true;
                }
                if (sweeps >= MaxSweeps)
                {
                    Console.Error.WriteLine($"WireCore.ProcessAllOblivious: hit MaxSweeps={MaxSweeps}; state may not be quiescent");
                    break;
                }
            } while (changed);

            LastObliviousIters = sweeps;

            // Clear the leftover queue & dedup hashes -- we bypassed them.
            for (int i = 0; i < RecalcListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
            for (int i = 0; i < RecalcListCount; i++) RecalcHash[RecalcList[i]] = 0;
            RecalcListNextCount = 0;
            RecalcListCount = 0;

            // Drain callbacks (memory writes, video frame writes) -- the callbacks queue may have
            // been populated by AddNodeToGroup hits on HasCallback group members; let them fire.
            InvokeCallbacks();
        }
    }
}
