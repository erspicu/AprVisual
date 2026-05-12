using System;
using LLVMSharp.Interop;

namespace AprVisual.Sim.Logic
{
    // ── S4.5 — Phase 0 spike ─────────────────────────────────────────────────────────────────────────────
    //
    // Verifies the LLVMSharp.Interop + libLLVM toolchain is wired up: build a trivial `int add(int,int)` via
    // the LLVM builder API, JIT-compile it (MCJIT), and call it. Same pattern as AprGba's JitSpike — the S4.5
    // codegen (Expr trees → an `void step(i8* cur, i8* prev)` LLVM function, MCJIT -O3, run as the inner loop)
    // is built on this. `--llvm-spike` runs it.
    public static unsafe class LlvmSpike
    {
        private static bool _jitInit;
        private static readonly object _lock = new();

        public static void EnsureJitInitialized()
        {
            if (_jitInit) return;
            lock (_lock)
            {
                if (_jitInit) return;
                LLVM.InitializeAllTargetInfos();
                LLVM.InitializeAllTargets();
                LLVM.InitializeAllTargetMCs();
                LLVM.InitializeAllAsmPrinters();
                LLVM.InitializeAllAsmParsers();
                LLVM.LinkInMCJIT();
                _jitInit = true;
            }
        }

        public static LLVMModuleRef BuildAddModule()
        {
            var module = LLVMModuleRef.CreateWithName("AprVisual_Spike");
            var builder = module.Context.CreateBuilder();
            var funcType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, new[] { LLVMTypeRef.Int32, LLVMTypeRef.Int32 });
            var addFunc = module.AddFunction("add", funcType);
            var entry = addFunc.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            var sum = builder.BuildAdd(addFunc.GetParam(0), addFunc.GetParam(1), "sum");
            builder.BuildRet(sum);
            if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var error))
                throw new InvalidOperationException($"LLVM module verification failed: {error}");
            return module;
        }

        public static string EmitAddIR() => BuildAddModule().PrintToString();

        public static int JitAndRunAdd(int a, int b)
        {
            EnsureJitInitialized();
            var module = BuildAddModule();
            var engine = module.CreateMCJITCompiler();
            var addr = engine.GetFunctionAddress("add");
            if (addr == 0) throw new InvalidOperationException("MCJIT failed to resolve 'add'.");
            var fn = (delegate* unmanaged[Cdecl]<int, int, int>)addr;
            return fn(a, b);
        }
    }
}
