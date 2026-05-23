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
        // Step 3.5a: ALSO mark the ALU's whole 133-node reverse-closure as CodegenOwned (S1 skips
        // RecalcNode entry on them) and have dispatcher write the latched outputs (notalu, alucout,
        // notalucout) transparently. Whether trace stays identical depends on whether the
        // "transparent latch" approximation matches S1's phi-gated behaviour for this ROM.
        public static bool EnableCodegenAluOwnInternal = false;
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
        private static int[] _aluInputNodes   = Array.Empty<int>();   // 17 ids (alua x8 + alub x8 + cin) — for input read
        private static int[] _aluOpNodes      = Array.Empty<int>();   // 5 ids: op-SUMS / ANDS / ORS / EORS / SRS — read each call
        private static int[] _aluOutputNodes  = Array.Empty<int>();   // 8 ids (alu x8) — combinational output (always written)
        // Step 3.5a: latched-output nodes — when EnableCodegenOwnInternal is on, dispatcher writes
        // these too (transparently). May or may not match S1's phi-gated latch behaviour.
        private static int[] _aluNotaluNodes  = Array.Empty<int>();   // 8 ids (notalu x8)
        private static int[] _aluCoutNodes    = Array.Empty<int>();   // 2 ids (alucout + notalucout)
        // Step 3.5a: full 133-node closure (alu output + notalu + carry-save intermediates + mux mids)
        private static int[] _aluClosureNodes = Array.Empty<int>();
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
            // 6502 ALU's real op selectors (per ref/metalnes-main/data/system-def/2a03/nodenames.js
            // L606-L610). These are PLA outputs that activate the corresponding ALU result-bus
            // contribution. Exactly one is normally active per cycle; multi-active resolves as
            // wired-OR which AluBlock.cpp models. Step 2.5: 5 ops instead of the earlier 2-op
            // best-effort approximation.
            var opSums  = Resolve("cpu.op-SUMS");
            var opAnds  = Resolve("cpu.op-ANDS");
            var opOrs   = Resolve("cpu.op-ORS");
            var opEors  = Resolve("cpu.op-EORS");
            var opSrs   = Resolve("cpu.op-SRS");
            // Outputs we WILL drive (combinational ALU output bus): just alu0..7. The latched
            // storage nodes notalu/alucout/notalucout stay owned by S1's phi-gated transistor
            // chain (writing them from the dispatcher would corrupt the latch behaviour) —
            // UNLESS EnableCodegenAluOwnInternal is on (Step 3.5a experiment).
            var alu     = Resolve("cpu.alu[7:0]");
            var notalu  = Resolve("cpu.notalu[7:0]");
            var alucoutR  = Resolve("cpu.alucout");
            var notcoutR  = Resolve("cpu.notalucout");

            _aluInputNodes  = new List<int>(alua).Concat(alub).Concat(alucinA).ToArray();
            _aluOpNodes     = new List<int>(opSums).Concat(opAnds).Concat(opOrs).Concat(opEors).Concat(opSrs).ToArray();
            _aluOutputNodes = alu;
            _aluNotaluNodes = notalu;
            _aluCoutNodes   = new List<int>(alucoutR).Concat(notcoutR).ToArray();

            // Step 3.5a: compute the 133-node ALU reverse-closure (same algorithm as --dump-block:
            // BFS backwards along channel edges, stop at other pull-up nodes / declared inputs /
            // supply rails). The full closure naively LEAKS into critical CPU clock/bus nodes
            // (cpu.adh*, cpu.adl*, cpu.cclk, cpu.phi2, cpu.a0/a4) because those happen to have
            // pullups=0 — so we FILTER it to a safer "definitely ALU-internal" subset:
            //   - cpu.#aluresult[0..7]  (the 8 mid latches for the OR-mux result, clearly ALU-only)
            //   - anonymous nodes (no Name) — combinational mids without semantic meaning elsewhere
            // Named CPU registers / buses / clocks are EXCLUDED — they're shared with the rest of
            // the chip and owning them breaks correctness.
            int[] closureInputs = _aluInputNodes.Concat(_aluOpNodes).ToArray();
            int[] closureOutputs = _aluOutputNodes.Concat(_aluNotaluNodes).Concat(_aluCoutNodes).ToArray();
            var fullClosure = ComputeReverseClosure(closureOutputs, closureInputs);
            _aluClosureNodes = fullClosure.Where(nn =>
            {
                if ((uint)nn >= (uint)NodeCount) return false;
                var n = Nodes[nn]; if (n == null) return false;
                string name = n.Name;
                // Conservative: ONLY take nodes whose name is clearly ALU-internal. Anonymous
                // nodes turn out to include shared bus / clock mids that break correctness when
                // owned. Step 4 (LLVM emit) can do a more sophisticated reachability + dataflow
                // analysis; for now we play very safe.
                if (string.IsNullOrEmpty(name)) return false;
                if (name.StartsWith("cpu.#alu")) return true;           // #aluresult0..7
                return false;
            }).ToArray();

            // Mark CodegenOwned for outputs (RecalcNode skips them; the dispatcher writes them).
            // GATED by EnableCodegenAluWriteback — in dry-run (Step 2 default), S1 still owns ALU
            // outputs, the dispatcher just measures the framework cost without mutating outputs.
            if (EnableCodegenAluWriteback)
                foreach (int nn in _aluOutputNodes)
                    if ((uint)nn < (uint)NodeCount) CodegenOwned[nn] = 1;

            // Step 3.5a: ALSO own all 133 closure-internal nodes + latched outputs. Dispatcher
            // writes the named outputs (alu + notalu + alucout + notalucout); the carry-save
            // intermediates are owned but NOT written — S1 won't update them on RecalcNode entry,
            // but they may still get set via group walks from other blocks (limitation of CodegenOwned).
            if (EnableCodegenAluOwnInternal)
            {
                foreach (int nn in _aluClosureNodes) if ((uint)nn < (uint)NodeCount) CodegenOwned[nn] = 1;
                foreach (int nn in _aluNotaluNodes)  if ((uint)nn < (uint)NodeCount) CodegenOwned[nn] = 1;
                foreach (int nn in _aluCoutNodes)    if ((uint)nn < (uint)NodeCount) CodegenOwned[nn] = 1;
            }
            // Mark CodegenInputWatched for inputs + op selectors (SetNodeState sets bit 0 on change).
            foreach (int nn in _aluInputNodes)
                if ((uint)nn < (uint)NodeCount) CodegenInputWatched[nn] = 1;
            foreach (int nn in _aluOpNodes)
                if ((uint)nn < (uint)NodeCount) CodegenInputWatched[nn] = 1;

            _dirtyBlockMask = 0;
            DispBlockEvalCount = DispAluEvalCount = DispInterpEvalCount = 0;
            string mode = EnableCodegenAluOwnInternal ? "OWN-INTERNAL"
                        : EnableCodegenAluWriteback   ? "WRITEBACK"
                                                       : "dry-run (no output mutation)";
            int ownedCount = 0;
            for (int nn = 0; nn < NodeCount; nn++) if (CodegenOwned[nn] != 0) ownedCount++;
            LastDispatcherStats = $"codegen-dispatcher [{mode}]: ALU block 0 ({_aluInputNodes.Length} inputs + {_aluOpNodes.Length} ops watched, {_aluOutputNodes.Length} alu outputs, closure {_aluClosureNodes.Length} nodes, {ownedCount} total CodegenOwned); interp = block 63";

            // Step 3.5a debug: in own-internal mode, dump the closure node names + check if any
            // input watch was accidentally subsumed by the closure.
            if (EnableCodegenAluOwnInternal && Environment.GetEnvironmentVariable("APRVISUAL_DEBUG_CLOSURE") == "1")
            {
                Console.Error.WriteLine($"# closure inspection (first 40 by name):");
                int shown = 0;
                foreach (int nn in _aluClosureNodes.OrderBy(n => Nodes[n]?.Name ?? ""))
                {
                    var node = Nodes[nn];
                    Console.Error.WriteLine($"#   {nn,6}  {(string.IsNullOrEmpty(node?.Name) ? "(anonymous)" : node.Name)}  pullups={node?.Pullups ?? 0}");
                    if (++shown >= 40) break;
                }
                if (_aluClosureNodes.Length > 40) Console.Error.WriteLine($"#   ... ({_aluClosureNodes.Length - 40} more)");
                // Check overlap with watched inputs
                var watchSet = new HashSet<int>(_aluInputNodes); foreach (int n in _aluOpNodes) watchSet.Add(n);
                int overlap = _aluClosureNodes.Count(n => watchSet.Contains(n));
                Console.Error.WriteLine($"# overlap with watched inputs: {overlap} of {watchSet.Count} watched are in closure");
            }
        }

        /// <summary>Step 3.5a: BFS-backwards from <paramref name="outputs"/> along channel edges,
        /// stopping at <paramref name="inputs"/> / supply rails / other pull-up nodes. Mirrors the
        /// --dump-block algorithm in TestRunner. Returns the set of INTERNAL nodes (closure minus
        /// the original outputs).</summary>
        private static int[] ComputeReverseClosure(int[] outputs, int[] inputs)
        {
            var stops = new HashSet<int>(inputs) { Npwr, Ngnd };
            var outSet = new HashSet<int>(outputs);
            var closure = new HashSet<int>(outputs);
            var queue = new Queue<int>(outputs);
            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                if ((uint)v >= (uint)Nodes.Count) continue;
                var node = Nodes[v]; if (node == null) continue;
                foreach (int tid in node.C1c2s)
                {
                    var t = Transistors[tid];
                    int gate  = t.Gate;
                    int other = (t.C1 == v) ? t.C2 : t.C1;
                    void Consider(int cand)
                    {
                        if (cand == v || cand == Npwr || cand == Ngnd) return;
                        if (stops.Contains(cand)) return;
                        if (!outSet.Contains(cand))
                        {
                            var cnode = (uint)cand < (uint)Nodes.Count ? Nodes[cand] : null;
                            if (cnode != null && cnode.Pullups > 0) return;   // another block's output
                        }
                        if (closure.Add(cand)) queue.Enqueue(cand);
                    }
                    Consider(gate); Consider(other);
                }
            }
            var internalOnly = new HashSet<int>(closure); internalOnly.ExceptWith(outSet);
            return internalOnly.ToArray();
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
            // 5 op selectors — direct read of the actual 6502 PLA outputs (Step 2.5)
            _aluCtx.alua    = a;
            _aluCtx.alub    = b;
            _aluCtx.cin     = cin;
            _aluCtx.op_sums = NodeStates[_aluOpNodes[0]];
            _aluCtx.op_ands = NodeStates[_aluOpNodes[1]];
            _aluCtx.op_ors  = NodeStates[_aluOpNodes[2]];
            _aluCtx.op_eors = NodeStates[_aluOpNodes[3]];
            _aluCtx.op_srs  = NodeStates[_aluOpNodes[4]];
            _aluCtx.alu     = 0;
            _aluCtx.cout    = 0;
            fixed (AluBlockBindings.AluCtx* p = &_aluCtx) AluBlockBindings.Eval_Alu(p);

            if (!EnableCodegenAluWriteback) return;   // Step 2 dry-run: framework cost measured, S1 still owns outputs

            // Step 2.5: write 8 combinational ALU output bits back via SetNodeState. The latched
            // sibling nodes (notalu, alucout, notalucout) are NOT written by us — S1's phi-gated
            // transistor chain captures them naturally from alu[i] during the phi-transparent half.
            // Each SetNodeState that actually changes value triggers gate-fanout + revDep, which
            // re-arms interp (bit 63) for the next dispatcher iteration.
            for (int i = 0; i < 8; i++)
            {
                byte bit = (byte)((_aluCtx.alu >> i) & 1);
                SetNodeState(_aluOutputNodes[i], bit);
            }

            // Step 3.5a: ALSO write the latched outputs transparently (notalu = ~alu, alucout = cout).
            // This bypasses S1's phi-gated latch chain — the latches end up always-transparent. For
            // the NES 6502 microcode, alua/alub are also phi-latched upstream, so they don't change
            // during phi-closed phases → alu[i] doesn't change → notalu[i] effectively holds anyway.
            // Trace-diff will tell if this assumption breaks for any ROM.
            if (EnableCodegenAluOwnInternal)
            {
                for (int i = 0; i < 8 && i < _aluNotaluNodes.Length; i++)
                {
                    byte bit = (byte)((_aluCtx.alu >> i) & 1);
                    SetNodeState(_aluNotaluNodes[i], (byte)(1 - bit));
                }
                if (_aluCoutNodes.Length >= 1) SetNodeState(_aluCoutNodes[0], _aluCtx.cout);
                if (_aluCoutNodes.Length >= 2) SetNodeState(_aluCoutNodes[1], (byte)(1 - _aluCtx.cout));
            }
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
