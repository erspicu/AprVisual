using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace EtlAnalyze
{
    // Headless PMC-per-method analyzer for a PerfView CpuCounters capture (.etl / .etl.zip).
    // Aggregates PMCCounterProfTraceData samples by ProfileSource (counter) and resolved
    // top-of-stack method, for a process whose name contains the given substring.
    // Managed JIT'd methods resolve from the CLR rundown PerfView captured (no PDB needed).
    internal static class Program
    {
        static readonly Dictionary<int, string> SrcNames = new()
        {
            { 8, "DcacheMisses" }, { 9, "IcacheMisses" }, { 11, "BranchMispredictions" },
            { 19, "TotalCycles" }, { 6, "BranchInstructions" }, { 25, "InstructionRetired" },
        };

        static int Main(string[] argv)
        {
            if (argv.Length < 1)
            {
                Console.Error.WriteLine("usage: etlanalyze <trace.etl|.etl.zip> [processSubstr=AprVisual]");
                return 2;
            }
            string input = argv[0];
            string procFilter = argv.Length > 1 ? argv[1] : "AprVisual";

            string etl = input;
            if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                etl = input.Substring(0, input.Length - 4);
                Console.Error.WriteLine($"unzipping {input} -> {etl} ...");
                var z = new ZippedETLReader(input);
                z.EtlFileName = etl;
                z.UnpackArchive();
            }
            Console.Error.WriteLine("converting to etlx (resolving managed symbols from rundown)...");
            string etlx = TraceLog.CreateFromEventTraceLogFile(etl);
            using var traceLog = new TraceLog(etlx);

            var perSrcTotal = new Dictionary<int, long>();   // every process
            var perSrcOurs = new Dictionary<int, long>();    // our process
            var perMethod = new Dictionary<int, Dictionary<string, long>>(); // src -> label -> count (our process)
            long noStack = 0;

            foreach (var data in traceLog.Events)
            {
                if (data is not PMCCounterProfTraceData pmc) continue;
                int src = pmc.ProfileSource;
                perSrcTotal[src] = perSrcTotal.GetValueOrDefault(src) + 1;

                string pname = pmc.ProcessName ?? "";
                if (pname.IndexOf(procFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                perSrcOurs[src] = perSrcOurs.GetValueOrDefault(src) + 1;

                string label;
                var cs = pmc.CallStack();
                if (cs != null)
                {
                    var ca = cs.CodeAddress;
                    string m = ca.FullMethodName;
                    label = string.IsNullOrEmpty(m) ? (ca.ModuleName + "!0x" + ca.Address.ToString("x")) : (ca.ModuleName + "!" + m);
                }
                else { label = "(no stack)"; noStack++; }

                if (!perMethod.TryGetValue(src, out var d)) { d = new(); perMethod[src] = d; }
                d[label] = d.GetValueOrDefault(label) + 1;
            }

            Console.WriteLine("### samples per counter  (all processes / process matching '" + procFilter + "')");
            foreach (var kv in perSrcTotal.OrderByDescending(k => k.Value))
            {
                string nm = SrcNames.GetValueOrDefault(kv.Key, "src#" + kv.Key);
                long ours = perSrcOurs.GetValueOrDefault(kv.Key);
                Console.WriteLine($"  {nm,-22} all={kv.Value,9}   ours={ours,9}");
            }
            if (noStack > 0) Console.WriteLine($"  ({noStack} of our samples had no resolved stack)");

            foreach (var src in perMethod.Keys.OrderBy(k => k))
            {
                string nm = SrcNames.GetValueOrDefault(src, "src#" + src);
                long ours = perSrcOurs.GetValueOrDefault(src);
                Console.WriteLine($"\n### {nm}: top methods in '{procFilter}' process (of {ours} samples)");
                foreach (var mk in perMethod[src].OrderByDescending(k => k.Value).Take(20))
                {
                    double pct = ours > 0 ? 100.0 * mk.Value / ours : 0;
                    Console.WriteLine($"  {mk.Value,7}  {pct,5:F1}%  {mk.Key}");
                }
            }
            return 0;
        }
    }
}
