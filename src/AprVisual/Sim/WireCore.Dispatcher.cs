using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AprVisual.Native;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Phase 2.5 codegen Step 2 — bitmask-polling macro-block dispatcher (Gemini r2 §2.8).
        //
        // The codegen path's runtime kernel. Replaces ProcessQueue when --codegen-dispatcher is on.
        //
        //   uint64 dirty_mask                ; bit i = block i needs evaluation
        //   while (dirty_mask != 0):
        //       int next = TrailingZeroCount(dirty_mask)   ; BSF/TZCNT, 1 cycle
        //       dirty_mask &= ~(1UL << next)
        //       switch (next): case 0: Eval_AluBlock(); break;
        //                       case 63: ProcessQueueInterp(); break;   // hybrid fallback
        //
        // Why this is the right dispatch — not function-pointer Queue (main's S4 trap):
        //   - TZCNT is one hardware cycle (BSF/TZCNT op).
        //   - .NET JIT compiles a switch over a dense small integer range (0..63) into a JUMP TABLE.
        //     CPU's BTB predicts jump-table targets WELL — unlike indirect function-pointer calls
        //     which are unpredictable indirect branches (main's S4 stall trap).
        //   - Total dispatch overhead per block ≈ 3-5 CPU cycles (vs 15-30 for function-ptr queue).
        //
        // Step 2 scope: ONE real codegen block (the ALU) + the existing interpreter as bit 63.
        // After this validates, Step 3 (graph partitioner) will produce more blocks; Step 4 will
        // replace each block's evaluator with LLVMSharp-emitted native code instead of the hand-coded
        // AluBlock.dll currently used.

        public static bool EnableCodegenDispatcher = false;
        // Step 2 default: DRY-RUN. The dispatcher framework runs (TZCNT, block eval, jump-table),
        // and Eval_AluBlock reads inputs + calls native — but does NOT write outputs back. That
        // validates the framework cost without committing to a particular op-selector mapping
        // (which needs deeper netlist research before it can be trusted byte-for-byte equal to S1).
        // Step 2.5 will flip this on and trace-diff against S1.
        public static bool EnableCodegenAluWriteback = false;
        public static string LastDispatcherStats = "(codegen dispatcher disabled)";

        // Block IDs (sparse OK; just must fit in 0..63).
        private const int BLOCK_ALU    = 0;
        private const int BLOCK_INTERP = 63;

        // Diagnostic counters (gated by CountEvents, like the other diagnostics).
        public static long DispBlockEvalCount;     // total block evaluations
        public static long DispAluEvalCount;       // bit-0 specifically
        public static long DispInterpEvalCount;    // bit-63 specifically

        // Hot state: the dirty mask. Touched on every SetNodeState that hits a watched input + on
        // every block dispatch. Aligned on its own cache line (the C# runtime gives us 8-byte alignment;
        // for a single ulong field that's good enough).
        private static ulong _dirtyBlockMask;

        // Per-node ownership / watch flags. byte* for zero-bounds-check hot reads.
        internal static byte* CodegenOwned;          // 1 = this node is computed by a codegen block (skip RecalcNode)
        internal static byte* CodegenInputWatched;   // 1 = this node is in some block's input set; SetNodeState should re-arm the block

        // ALU block specifics.
        private static int[] _aluInputNodes  = Array.Empty<int>();    // 17 ids (alua x8 + alub x8 + cin) — for input read
        private static int[] _aluOpNodes     = Array.Empty<int>();    // ops (SUMS / AND / OR / EOR) — read each call
        private static int[] _aluOutputNodes = Array.Empty<int>();    // 18 ids (alu x8 + notalu x8 + alucout + notalucout)
        private static AluBlockBindings.AluCtx _aluCtx;               // persistent (no per-call alloc)

        public static int CodegenAluInputCount  => _aluInputNodes.Length + _aluOpNodes.Length;
        public static int CodegenAluOutputCount => _aluOutputNodes.Length;

        /// <summary>Set up the codegen dispatcher: resolve ALU block boundary nodes, mark CodegenOwned
        /// + CodegenInputWatched. Called from LoadSystem when EnableCodegenDispatcher.</summary>
        internal static void CodegenDispatcherSetup()
        {
            CodegenOwned        = AllocArray<byte>(NodeCount);
            CodegenInputWatched = AllocArray<byte>(NodeCount);

            // Resolve ALU boundary nodes (post-LoadSystem; the name table is populated by ComposeSystem)
            int[] Resolve(string expr) { var l = new List<int>(); ResolveNodes(expr, l, quiet: true); return l.ToArray(); }
            // Inputs: 8 alua + 8 alub + 1 cin = 17 ids, in this exact bit-packing order
            var alua    = Resolve("cpu.alua[7:0]");
            var alub    = Resolve("cpu.alub[7:0]");
            var alucinA = Resolve("cpu.alucin");
            // op selectors — we use op-SUMS, and the rest are placeholders that hardware would
            // also drive but we treat conservatively (best-effort match). For Step 2 we lump the
            // PLA states that feed ADD/AND/OR/EOR into a single op selector via NodeStates reads.
            var opSums  = Resolve("cpu.op-SUMS");
            var opAndOrEor = Resolve("cpu.op-T+-ora/and/eor/adc");   // not the perfect break-out but enough to set a bit
            // Outputs: alu0..7, notalu0..7, alucout, notalucout
            var alu     = Resolve("cpu.alu[7:0]");
            var notalu  = Resolve("cpu.notalu[7:0]");
            var alucout = Resolve("cpu.alucout");
            var notcout = Resolve("cpu.notalucout");

            _aluInputNodes  = new List<int>(alua).Concat(alub).Concat(alucinA).ToArray();
            _aluOpNodes     = new List<int>(opSums).Concat(opAndOrEor).ToArray();
            _aluOutputNodes = new List<int>(alu).Concat(notalu).Concat(alucout).Concat(notcout).ToArray();

            // Mark CodegenOwned for outputs (RecalcNode skips them; the dispatcher writes them).
            // GATED by EnableCodegenAluWriteback — in dry-run (Step 2 default), S1 still owns ALU
            // outputs, the dispatcher just measures the framework cost without mutating outputs.
            if (EnableCodegenAluWriteback)
                foreach (int nn in _aluOutputNodes)
                    if ((uint)nn < (uint)NodeCount) CodegenOwned[nn] = 1;
            // Mark CodegenInputWatched for inputs + op selectors (SetNodeState sets bit 0 on change).
            foreach (int nn in _aluInputNodes)
                if ((uint)nn < (uint)NodeCount) CodegenInputWatched[nn] = 1;
            foreach (int nn in _aluOpNodes)
                if ((uint)nn < (uint)NodeCount) CodegenInputWatched[nn] = 1;

            _dirtyBlockMask = 0;
            DispBlockEvalCount = DispAluEvalCount = DispInterpEvalCount = 0;
            string mode = EnableCodegenAluWriteback ? "WRITEBACK" : "dry-run (no output mutation)";
            LastDispatcherStats = $"codegen-dispatcher [{mode}]: ALU block 0 ({_aluInputNodes.Length} inputs + {_aluOpNodes.Length} ops watched, {_aluOutputNodes.Length} outputs); interp = block 63";
        }

        /// <summary>Replaces ProcessQueue when EnableCodegenDispatcher. Drains dirty blocks (bitmask
        /// polling + TZCNT + jump-table switch) until no block is dirty. ALU and interpreter alternate
        /// naturally: ALU outputs propagate via SetNodeState → re-arms interp; interp settles may flip
        /// ALU inputs → re-arms ALU.</summary>
        internal static void DispatcherRun()
        {
            // The interpreter ALWAYS starts dirty if anything was enqueued — kick it first.
            if (RecalcListNextCount != 0) _dirtyBlockMask |= 1UL << BLOCK_INTERP;

            while (_dirtyBlockMask != 0)
            {
                int next = BitOperations.TrailingZeroCount(_dirtyBlockMask);
                _dirtyBlockMask &= ~(1UL << next);
                if (CountEvents) DispBlockEvalCount++;

                // Dense small switch → JIT emits a jump table (BTB-friendly).
                switch (next)
                {
                    case BLOCK_ALU:    Eval_AluBlock();      if (CountEvents) DispAluEvalCount++;    break;
                    case BLOCK_INTERP: ProcessQueueInterp(); if (CountEvents) DispInterpEvalCount++; break;
                    default: /* future block IDs */ break;
                }
                // After interp drained, if its SetNodeStates re-armed the interp block (more nodes
                // enqueued during settle), re-set bit 63 so the dispatcher loop picks it up again.
                if (RecalcListNextCount != 0) _dirtyBlockMask |= 1UL << BLOCK_INTERP;
            }
            InvokeCallbacks();   // WireCore.Handlers.cs — fire memory accesses etc. once everything settles
        }

        /// <summary>The one currently-real codegen block: evaluate the ALU. Reads inputs from
        /// NodeStates → calls the native AluBlock.dll → writes outputs via SetNodeState (which
        /// propagates downstream + re-arms the interpreter for the next settle).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Eval_AluBlock()
        {
            // Pack the 17 input bits into the AluCtx struct (bit i of byte = NodeStates[alu_input[i]]).
            byte a = 0, b = 0;
            for (int i = 0; i < 8; i++) { a |= (byte)(NodeStates[_aluInputNodes[i]]      << i); }
            for (int i = 0; i < 8; i++) { b |= (byte)(NodeStates[_aluInputNodes[8 + i]]  << i); }
            byte cin = NodeStates[_aluInputNodes[16]];
            byte opSums = (_aluOpNodes.Length > 0) ? NodeStates[_aluOpNodes[0]] : (byte)0;
            byte opAOE  = (_aluOpNodes.Length > 1) ? NodeStates[_aluOpNodes[1]] : (byte)0;
            // For Step 2 we conservatively treat the "ora/and/eor/adc" lumped PLA state as picking
            // adc when op-SUMS is also high, and AND otherwise — best-effort until we wire per-op PLAs.
            _aluCtx.alua    = a;
            _aluCtx.alub    = b;
            _aluCtx.cin     = cin;
            _aluCtx.op_sums = opSums;
            _aluCtx.op_and  = (byte)(opAOE  != 0 && opSums == 0 ? 1 : 0);
            _aluCtx.op_or   = 0;
            _aluCtx.op_eor  = 0;
            _aluCtx._pad    = 0;
            _aluCtx.alu     = 0;
            _aluCtx.cout    = 0;
            fixed (AluBlockBindings.AluCtx* p = &_aluCtx) AluBlockBindings.Eval_Alu(p);

            if (!EnableCodegenAluWriteback) return;   // Step 2 dry-run: framework cost measured, S1 still owns outputs

            // Write 18 outputs back via SetNodeState. Each write that actually changes the value
            // triggers gate-fanout + revDep + (importantly) re-arms interp = bit 63 dirty.
            for (int i = 0; i < 8 && i < _aluOutputNodes.Length; i++)
            {
                byte bit = (byte)((_aluCtx.alu >> i) & 1);
                SetNodeState(_aluOutputNodes[i],     bit);
                if (8 + i < _aluOutputNodes.Length)
                    SetNodeState(_aluOutputNodes[8 + i], (byte)(1 - bit));   // notalu
            }
            if (16 < _aluOutputNodes.Length) SetNodeState(_aluOutputNodes[16], _aluCtx.cout);             // alucout
            if (17 < _aluOutputNodes.Length) SetNodeState(_aluOutputNodes[17], (byte)(1 - _aluCtx.cout)); // notalucout
        }

        /// <summary>Hook for SetNodeState (hot path) — when nn changes, set the right block bit. Currently
        /// only the ALU (bit 0) watches; future blocks would each register their watched node sets and
        /// this would or-in their bits. Kept inline-friendly (one byte read + a conditional).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CodegenInputChanged(int nn)
        {
            if (CodegenInputWatched != null && CodegenInputWatched[nn] != 0)
                _dirtyBlockMask |= 1UL << BLOCK_ALU;
        }
    }
}
