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

        public static string EmitIR() => BuildModule().PrintToString();

        public static void Compile()
        {
            LlvmSpike.EnsureJitInitialized();
            var m = BuildModule();
            // (-O3 via the new-PM `LLVMRunPasses` pipeline is a TODO — the LLVMSharp.Interop 20.x surface for it
            //  is fiddly; for now MCJIT's default opt level (≈-O2) handles it, which on already-SSA-ish IR is most
            //  of the win. `Optimize` is a placeholder flag for that.)
            _ = Optimize;
            _engine = m.CreateMCJITCompiler();
            var addr = _engine.GetFunctionAddress("step");
            if (addr == 0) throw new InvalidOperationException("MCJIT failed to resolve 'step'.");
            StepFn = (delegate* unmanaged[Cdecl]<byte*, byte*, void>)addr;
            StepAddr = (nint)StepFn;
            Compiled = true;
        }
    }
}
