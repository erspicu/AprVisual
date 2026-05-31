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
        public int LastChangesPerSettle;     // nodes that changed value in the last settle (sum over iters)
        public int LastChangesFirstIter;     // nodes that changed in iter 0 specifically

        // Phase E-3 — external inputs the AotRuntime manages explicitly (clock + reset)
        // These are toggled/held by Step() instead of being computed by EvalAll.
        public int ResetNodeId = -1;         // /res node (active low during power-on)
        public bool ResetAsserted;           // when true, NodeStates[ResetNodeId] forced to 0
        public int ResetHoldHc = 192;        // hc count to hold /res low at start
        public int HcSinceStart;             // counter

        // Phase E-4a — cart ROM memory handler. When AB is in $8000-$FFFF range, look up byte
        // in PrgRom + write to DB. Lets CPU fetch instructions and propagate state changes.
        public byte[]? PrgRom;               // 16/32 KB cart PRG ROM
        public int[] AbIds = Array.Empty<int>();   // 16 node IDs for cpu.ab0..ab15 (NOT consecutive)
        public int[] DbIds = Array.Empty<int>();   // 8 node IDs for cpu.db0..db7  (NOT consecutive)
        public long RomReadCount;            // diagnostic: how many step-level ROM reads happened

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

            // Phase E-3: reset assertion / deassertion based on ResetAsserted + hc counter
            if (ResetNodeId >= 0)
            {
                if (ResetAsserted) NodeStates[ResetNodeId] = 0;
                if (HcSinceStart < ResetHoldHc) NodeStates[ResetNodeId] = 0;   // hold /res low during power-on
            }

            // Settle: iterate EvalAll until NodeStates stops changing (fixed-point)
            LastChangesPerSettle = 0;
            LastChangesFirstIter = 0;
            int iter = 0;
            byte[] prev = new byte[NodeStates.Length];
            for (; iter < MaxSettleIterations; iter++)
            {
                Array.Copy(NodeStates, prev, NodeStates.Length);
                fixed (byte* p = NodeStates) { EvalAll(p); }
                int changedThisIter = 0;
                for (int i = 0; i < NodeStates.Length; i++)
                    if (NodeStates[i] != prev[i]) changedThisIter++;
                LastChangesPerSettle += changedThisIter;
                if (iter == 0) LastChangesFirstIter = changedThisIter;
                if (changedThisIter == 0) break;
            }
            LastSettleIterations = iter + 1;
            HcSinceStart++;

            // Phase E-4a: after AOT settle, simulate cart ROM read. If AB in $8000-$FFFF,
            // fetch byte from PrgRom + write to DB. Then re-settle so CPU sees the new DB.
            if (PrgRom != null && AbIds.Length == 16 && DbIds.Length == 8)
            {
                int addr = 0;
                for (int i = 0; i < 16; i++) addr |= NodeStates[AbIds[i]] << i;
                if ((addr & 0x8000) != 0)
                {
                    int romOffset = (addr - 0x8000) % PrgRom.Length;
                    byte b = PrgRom[romOffset];
                    for (int i = 0; i < 8; i++) NodeStates[DbIds[i]] = (byte)((b >> i) & 1);
                    RomReadCount++;
                    // Re-settle so the CPU sees the data bus update
                    fixed (byte* p = NodeStates) { EvalAll(p); }
                }
            }
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
