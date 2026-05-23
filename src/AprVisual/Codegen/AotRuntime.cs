using System;
using System.Linq;
using AprVisual.Sim;

namespace AprVisual.Codegen
{
    /// <summary>
    /// Phase E-2 (Route B): AotRuntime — a 100%-AOT simulation engine that takes a loaded
    /// AotEngine delegate (from AotRoslynLoader) + own NodeStates buffer, and Steps the
    /// simulation WITHOUT calling WireCore's S1 runtime.
    ///
    /// MVP scope:
    ///   - Own NodeStates buffer (byte[NodeCount], managed memory not unmanaged)
    ///   - Init from a snapshot of S1's settled state
    ///   - Step(): toggle clock node + iterate EvalAll until fixed-point (or max iter)
    ///   - Comparison helper: vs S1's NodeStates byte-by-byte
    ///
    /// Out of scope for E-2:
    ///   - Memory handlers (RAM/ROM I/O) → Phase E-4
    ///   - Callback mechanism (PPU video, APU audio) → Phase E-5+
    ///   - Power-on reset sequence → Phase E-3
    /// </summary>
    public sealed unsafe class AotRuntime
    {
        public byte[] NodeStates;            // own buffer, managed
        public AotRoslynLoader.EvalAllDelegate EvalAll;
        public int ClockNodeId;
        public int MaxSettleIterations = 32;
        public int LastSettleIterations;     // diagnostic

        public AotRuntime(byte[] initialNodeStates, AotRoslynLoader.EvalAllDelegate evalAll, int clockNodeId)
        {
            NodeStates = (byte[])initialNodeStates.Clone();
            EvalAll = evalAll;
            ClockNodeId = clockNodeId;
        }

        /// <summary>One half-cycle step: toggle clock, settle via EvalAll fixed-point.</summary>
        public void Step()
        {
            // Toggle the clock node (master half-cycle = one phi flip)
            NodeStates[ClockNodeId] = (byte)(1 - NodeStates[ClockNodeId]);

            // Settle: iterate EvalAll until NodeStates stops changing (fixed-point)
            int iter = 0;
            byte[] prev = new byte[NodeStates.Length];
            for (; iter < MaxSettleIterations; iter++)
            {
                Array.Copy(NodeStates, prev, NodeStates.Length);
                fixed (byte* p = NodeStates) { EvalAll(p); }
                bool same = true;
                for (int i = 0; i < NodeStates.Length; i++)
                    if (NodeStates[i] != prev[i]) { same = false; break; }
                if (same) break;
            }
            LastSettleIterations = iter + 1;
        }

        /// <summary>Compare own NodeStates to a reference (typically S1's settled state).
        /// Returns (matchCount, mismatchCount, firstMismatchNodeId)</summary>
        public (int match, int mismatch, int firstMissId) CompareWith(byte[] reference)
        {
            int match = 0, mismatch = 0, firstMiss = -1;
            int n = Math.Min(NodeStates.Length, reference.Length);
            for (int i = 0; i < n; i++)
            {
                if (NodeStates[i] == reference[i]) match++;
                else { mismatch++; if (firstMiss < 0) firstMiss = i; }
            }
            return (match, mismatch, firstMiss);
        }
    }
}
