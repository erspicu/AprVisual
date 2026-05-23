using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AprVisual.Codegen
{
    /// <summary>
    /// Phase D-2: take a generated .cs source string (from AotBlockBuilder.EmitMasterSource),
    /// compile it in-memory via Roslyn, load the resulting assembly, and expose the
    /// AotEngine.EvalAllBlocks method as a `unsafe void EvalAll(byte*)` delegate.
    ///
    /// Together with Phase D-1, this completes the "static source → compiled delegate" pipeline.
    /// Phase D-3 will wire this delegate into the runtime dispatcher (event-driven block eval).
    /// </summary>
    public static class AotRoslynLoader
    {
        public unsafe delegate void EvalAllDelegate(byte* nodeStates);

        public sealed class CompileResult
        {
            public bool Success;
            public Assembly? Assembly;
            public EvalAllDelegate? EvalAll;
            public string Log = "";
        }

        /// <summary>Compile a generated AOT .cs source into a runtime delegate. The source MUST
        /// have `public static unsafe class AotEngine { public static void EvalAllBlocks(byte*) }`
        /// in the namespace `AprVisual.Codegen.Generated`.</summary>
        public static CompileResult CompileMaster(string source)
        {
            var result = new CompileResult();
            var logSb = new System.Text.StringBuilder();

            var tree = CSharpSyntaxTree.ParseText(source);
            // Reference everything we need: runtime, System.* and the host AprVisual assembly
            // (so the loaded code can call into anything internal if it ever needs to; for now
            // the AOT code is self-contained and only does byte* arithmetic).
            var trustedAsms = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "";
            var refs = new List<MetadataReference>();
            foreach (string p in trustedAsms.Split(Path.PathSeparator))
                if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    refs.Add(MetadataReference.CreateFromFile(p));

            var options = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Release,
                concurrentBuild: true);

            var compilation = CSharpCompilation.Create(
                assemblyName: "AprVisual.AotEngine.Generated_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                syntaxTrees: new[] { tree },
                references: refs,
                options: options);

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
            {
                logSb.AppendLine("Roslyn compile FAILED:");
                int max = 0;
                foreach (var d in emit.Diagnostics)
                {
                    if (d.Severity != DiagnosticSeverity.Error) continue;
                    logSb.AppendLine($"  {d.Severity}: {d.GetMessage()}  @ {d.Location}");
                    if (++max >= 12) { logSb.AppendLine("  (... truncated)"); break; }
                }
                result.Success = false;
                result.Log = logSb.ToString();
                return result;
            }
            logSb.AppendLine($"Roslyn compile OK; emitted {ms.Length:N0} bytes IL");

            ms.Position = 0;
            var asm = Assembly.Load(ms.ToArray());
            result.Assembly = asm;
            logSb.AppendLine($"Loaded assembly: {asm.FullName}");

            var engineType = asm.GetType("AprVisual.Codegen.Generated.AotEngine");
            if (engineType == null) { result.Log = logSb + "AotEngine type not found in loaded assembly"; return result; }
            var evalMethod = engineType.GetMethod("EvalAllBlocks", BindingFlags.Public | BindingFlags.Static);
            if (evalMethod == null) { result.Log = logSb + "EvalAllBlocks method not found"; return result; }

            var del = (EvalAllDelegate)Delegate.CreateDelegate(typeof(EvalAllDelegate), evalMethod);
            result.EvalAll = del;
            result.Success = true;
            logSb.AppendLine($"Got EvalAllBlocks delegate: {del.Method.Name}");
            result.Log = logSb.ToString();
            return result;
        }
    }
}
