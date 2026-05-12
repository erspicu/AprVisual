using System;
using System.Collections.Generic;
using LLVMSharp.Interop;

namespace AprVisual.Sim.Logic
{
    // ── S4.5 — the LLVM backend ──────────────────────────────────────────────────────────────────────────
    //
    // Translates the IR's EvalOrder (the ~11680 acyclic nodes' Expr trees) into an LLVM `void step(i8* cur,
    // i8* prev)` function — the same thing CompileChunkedStep emits as JIT'd C# Expression-tree IL and
    // EmitCsharpSource emits as C# source, but here as LLVM IR → MCJIT → native. (The residual SCC + bus
    // nodes stay with S1's ProcessQueue, same as the C# runtime — so the LLVM `step` does just the chunks.)
    //
    // Per EvalOrder node v (in topo order):  store i8 lower(NextExpr[v]), getelementptr i8, i8* cur, i64 v
    // lower (working in i8, 0/1):
    //   NodeRef(id)  → load i8 from cur[id]   (within a chunk, just-stored values are forwarded directly)
    //   Hold/Prev(id)→ load i8 from prev[id]  (prev doesn't change inside step → each loaded once)
    //   Const        → i8 0/1
    //   Not(x)       → xor i8 lower(x), 1
    //   And/Or       → and/or i8
    //   Mux(c,a,b)   → select i1 (icmp ne i8 lower(c), 0), i8 lower(a), i8 lower(b)
    // Chunked (ChunkSize nodes per `step_chunk_N`, a `step` calling them) — matches CompileChunkedStep, so a
    // huge single function doesn't blow up LLVM's reg-alloc.
    public static unsafe class LlvmCodegen
    {
        public const int ChunkSize = 512;
        public static bool Optimize = true;   // run `default<O3>` (new-PM) on the module before MCJIT; if the API
                                              // path isn't available, falls back to MCJIT's default opt (~-O2). --llvm-no-opt to skip.

        public static delegate* unmanaged[Cdecl]<byte*, byte*, void> StepFn;
        static LLVMExecutionEngineRef _engine;
        public static int ChunkCount, InstrCount;
        public static bool Compiled;          // StepFn != null (a non-pointer flag, so non-unsafe callers can check)
        public static nint StepAddr;          // (nint)StepFn — the JIT'd code's address

        static LLVMModuleRef BuildModule()
        {
            var i8 = LLVMTypeRef.Int8;
            var i8p = LLVMTypeRef.CreatePointer(i8, 0);
            var i64 = LLVMTypeRef.Int64;
            var fnT = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, new[] { i8p, i8p });

            var m = LLVMModuleRef.CreateWithName("AprVisual_IrStep");
            var b = m.Context.CreateBuilder();
            var order = IrEngine.EvalOrder;
            var nextExpr = IrEngine.NextExpr;
            int nChunks = (order.Length + ChunkSize - 1) / ChunkSize;
            ChunkCount = nChunks;
            var chunkFns = new LLVMValueRef[nChunks];
            int instrs = 0;

            LLVMValueRef C8(ulong v) => LLVMValueRef.CreateConstInt(i8, v, false);

            for (int ci = 0; ci < nChunks; ci++)
            {
                var f = m.AddFunction($"step_chunk_{ci}", fnT);
                chunkFns[ci] = f;
                var entry = f.AppendBasicBlock("e");
                b.PositionAtEnd(entry);
                var cur = f.GetParam(0);
                var prev = f.GetParam(1);
                var curVal = new Dictionary<int, LLVMValueRef>();
                var prevVal = new Dictionary<int, LLVMValueRef>();

                LLVMValueRef LoadCur(int id)
                {
                    if (curVal.TryGetValue(id, out var v)) return v;
                    var p = b.BuildGEP2(i8, cur, new[] { LLVMValueRef.CreateConstInt(i64, (ulong)id, false) });
                    var ld = b.BuildLoad2(i8, p);
                    curVal[id] = ld; instrs += 2; return ld;
                }
                LLVMValueRef LoadPrev(int id)
                {
                    if (prevVal.TryGetValue(id, out var v)) return v;
                    var p = b.BuildGEP2(i8, prev, new[] { LLVMValueRef.CreateConstInt(i64, (ulong)id, false) });
                    var ld = b.BuildLoad2(i8, p);
                    prevVal[id] = ld; instrs += 2; return ld;
                }
                LLVMValueRef Lower(Expr e)
                {
                    switch (e)
                    {
                        case ConstExpr c:    return C8(c.Value ? 1UL : 0UL);
                        case NodeRefExpr nr: return LoadCur(nr.Id);
                        case HoldExpr h:     return LoadPrev(h.Id);
                        case PrevExpr p:     return LoadPrev(p.Id);
                        case NotExpr x:      instrs++; return b.BuildXor(Lower(x.Operand), C8(1));
                        case AndExpr a:      { var l = Lower(a.L); var r = Lower(a.R); instrs++; return b.BuildAnd(l, r); }
                        case OrExpr o:       { var l = Lower(o.L); var r = Lower(o.R); instrs++; return b.BuildOr(l, r); }
                        case MuxExpr mx:     { var c = Lower(mx.Cond); var a = Lower(mx.A); var bb = Lower(mx.B); var cond = b.BuildICmp(LLVMIntPredicate.LLVMIntNE, c, C8(0)); instrs += 2; return b.BuildSelect(cond, a, bb); }
                        default:             return C8(0);
                    }
                }

                int start = ci * ChunkSize, end = Math.Min(start + ChunkSize, order.Length);
                for (int i = start; i < end; i++)
                {
                    int v = order[i];
                    var val = (v < nextExpr.Length && nextExpr[v] is { } e) ? Lower(e) : C8(0);
                    var p = b.BuildGEP2(i8, cur, new[] { LLVMValueRef.CreateConstInt(i64, (ulong)v, false) });
                    b.BuildStore(val, p);
                    curVal[v] = val; instrs += 2;
                }
                b.BuildRetVoid();
            }

            var step = m.AddFunction("step", fnT);
            b.PositionAtEnd(step.AppendBasicBlock("e"));
            var sc = step.GetParam(0); var sp = step.GetParam(1);
            for (int ci = 0; ci < nChunks; ci++) b.BuildCall2(fnT, chunkFns[ci], new[] { sc, sp });
            b.BuildRetVoid();
            InstrCount = instrs;

            if (!m.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var err))
                throw new InvalidOperationException($"LLVM module verification failed: {err}");
            return m;
        }

        public static string EmitIR() { var m = BuildModule(); if (Optimize) TryRunO3(m); return m.PrintToString(); }

        // The new-PM optimisation pass list run on the module before MCJIT. Curated for our workload (a big
        // straight-line boolean dataflow function — 23 chunk functions called by `step`, no loops, no recursion):
        // mem2reg/sroa (alloca→SSA — no-op here, harmless), early-cse + gvn (CSE, catches any loads/expr the
        // per-chunk caches missed), instcombine×2 (peephole — folds xor/and/or boolean chains, select(icmp ne x,0,..)
        // → x, etc.), dse (dead store elim), simplifycfg (cleanup). No inlining → the chunk functions stay separate
        // (so `step` doesn't become one huge function that blows up reg-alloc). Same RunPasses API the user's
        // AprGba uses. (`--llvm-no-opt` skips it → MCJIT's default opt ≈-O2.)
        const string OptPasses = "mem2reg,sroa,early-cse,instcombine<no-verify-fixpoint>,gvn,instcombine<no-verify-fixpoint>,dse,simplifycfg";

        static void TryRunO3(LLVMModuleRef m)
        {
            try
            {
                var opts = LLVMPassBuilderOptionsRef.Create();
                try
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes(OptPasses + "\0");
                    fixed (byte* p = bytes)
                    {
                        var err = LLVM.RunPasses(m, (sbyte*)p, default(LLVMTargetMachineRef), opts);
                        if (err != null)
                        {
                            var msgPtr = LLVM.GetErrorMessage(err);
                            var msg = msgPtr != null ? new string(msgPtr) : "(no message)";
                            if (msgPtr != null) LLVM.DisposeErrorMessage(msgPtr);
                            throw new InvalidOperationException($"LLVM RunPasses('{OptPasses}') failed: {msg}");
                        }
                    }
                }
                finally { opts.Dispose(); }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LLVM opt-pass pipeline skipped (using the unoptimized module + MCJIT default opt): {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void Compile()
        {
            LlvmSpike.EnsureJitInitialized();
            var m = BuildModule();
            if (Optimize) TryRunO3(m);
            _engine = m.CreateMCJITCompiler();
            var addr = _engine.GetFunctionAddress("step");
            if (addr == 0) throw new InvalidOperationException("MCJIT failed to resolve 'step'.");
            StepFn = (delegate* unmanaged[Cdecl]<byte*, byte*, void>)addr;
            StepAddr = (nint)StepFn;
            Compiled = true;
        }
    }
}
