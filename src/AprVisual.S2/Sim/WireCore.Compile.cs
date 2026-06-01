using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Let the runtime-generated sweep assembly (named below) reach WireCore's internal members.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AprVisualGenSweep")]

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Escape-1, step 7: OBLIVIOUS COMPILATION (simplest C# path, via Roslyn).
    //
    //  Emit the relaxation sweep as straight-line C#: one statement per oblivious node, inputs and
    //  truth-table base baked in as constants, dense nodes inlined as `tt[BASE + (ls[a]|ns[b]<<1|...)]`,
    //  bus/sparse nodes as a call back to EvalBusOrSparse. Compile in-memory (Roslyn) into a set of
    //  Sweep_k(ls,ns,tt) methods (chunked so no single method is JIT-hostile), and relax to a fixed point
    //  by calling them in order. The point: kill the interpreter's per-node pointer-chase + variable input
    //  loop, and prove a compiled straight-line sweep is much faster than EvalObliviousRelax — with the
    //  same 100% golden match. (A later LLVM-via-.NET backend can replace the Roslyn step.)
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        internal unsafe delegate int SweepPartDelegate(byte* ls, byte* ns, byte* tt);
        private static SweepPartDelegate[]? _sweepParts;
        public static long CompiledRelaxIters;
        public static bool MiterUseCompiled;

        // Value for a bus/sparse node, called from the generated code. Returns the value to write
        // (or the node's current LogicState = a no-op, for an unlearned sparse combo / a hold).
        public static byte EvalBusOrSparse(int nn)
        {
            if (_logicBus[nn] != 0) return BusResolve(nn);
            ushort* p = _covInputs + _covBase[nn];
            int k = *p++;
            ulong key = 0;
            for (int j = 0; j < k; j++) key |= (ulong)St(p[j]) << j;
            return _covMap![nn].TryGetValue(key, out byte v) ? v : LogicState[nn];
        }

        public static void CompileSweep()
        {
            const int ChunkSize = 400;
            int cnt = _logicOrderCount;
            int parts = (cnt + ChunkSize - 1) / ChunkSize;
            var sb = new StringBuilder();
            sb.AppendLine("namespace AprVisual.Gen {");
            sb.AppendLine("  public static unsafe class GeneratedSweep {");
            for (int part = 0; part < parts; part++)
            {
                sb.AppendLine($"    public static int Sweep{part}(byte* ls, byte* ns, byte* tt) {{");
                sb.AppendLine("      int ch = 0;");
                int lo = part * ChunkSize, hi = Math.Min(cnt, lo + ChunkSize);
                for (int i = lo; i < hi; i++)
                {
                    int nn = _logicOrder[i];
                    if (_logicBus[nn] != 0 || _logicSparse[nn] != 0)
                    {
                        sb.AppendLine($"      {{ byte v=AprVisual.Sim.WireCore.EvalBusOrSparse({nn}); if(ls[{nn}]!=v){{ls[{nn}]=v;ch++;}} }}");
                    }
                    else
                    {
                        ushort* pp = _covInputs + _covBase[nn];
                        int kk = *pp++;
                        var terms = new List<string>(kk);
                        for (int j = 0; j < kk; j++)
                        {
                            int inp = pp[j];
                            string arr = _logicIsExtracted[inp] != 0 ? "ls" : "ns";
                            terms.Add(j == 0 ? $"{arr}[{inp}]" : $"({arr}[{inp}]<<{j})");
                        }
                        string idx = terms.Count > 0 ? string.Join("|", terms) : "0";
                        sb.AppendLine($"      {{ int i={idx}; byte v=tt[{_logicTTBase[nn]}+i]; if(v!=2&&ls[{nn}]!=v){{ls[{nn}]=v;ch++;}} }}");
                    }
                }
                sb.AppendLine("      return ch;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");

            var tree = CSharpSyntaxTree.ParseText(sb.ToString());
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();
            var comp = CSharpCompilation.Create("AprVisualGenSweep", new[] { tree }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, allowUnsafe: true));
            var swc = System.Diagnostics.Stopwatch.StartNew();
            using var ms = new System.IO.MemoryStream();
            var res = comp.Emit(ms);
            swc.Stop();
            if (!res.Success)
            {
                foreach (var d in res.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(8))
                    Console.WriteLine("# codegen error: " + d.GetMessage());
                throw new Exception("oblivious codegen failed");
            }
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(ms);
            var type = asm.GetType("AprVisual.Gen.GeneratedSweep")!;
            _sweepParts = new SweepPartDelegate[parts];
            for (int part = 0; part < parts; part++)
                _sweepParts[part] = (SweepPartDelegate)type.GetMethod($"Sweep{part}")!.CreateDelegate(typeof(SweepPartDelegate));
            CompiledRelaxIters = 0;
            Console.WriteLine($"# [compiled] {parts} straight-line sweep methods, {cnt:N0} nodes, {sb.Length / 1024:N0} KB C#, Roslyn emit {swc.Elapsed.TotalSeconds:F2}s");
        }

        // Relaxation using the compiled straight-line sweep (replaces EvalObliviousRelax on the compiled path).
        public static int EvalObliviousCompiled()
        {
            byte* ls = LogicState, ns = NodeStates, tt = _logicTT;
            var parts = _sweepParts!;
            int iters = 0; bool changed = true;
            while (changed && iters < MaxRelaxIters)
            {
                changed = false; iters++;
                int ch = 0;
                for (int p = 0; p < parts.Length; p++) ch += parts[p](ls, ns, tt);
                if (ch > 0) changed = true;
            }
            RelaxIterTotal += iters; if (changed) RelaxNonConverged++;   // shared counters -> ReportMiter avg works
            CompiledRelaxIters += iters;
            return iters;
        }
    }
}
