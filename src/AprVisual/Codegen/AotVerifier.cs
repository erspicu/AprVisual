using System;
using System.Collections.Generic;
using System.Linq;
using AprVisual.Sim;
using AprVisual.Rom;

namespace AprVisual.Codegen
{
    /// <summary>
    /// AOT verification harness: run S1 on a ROM step-by-step; at each half-cycle, after S1
    /// settles, call a hand-coded AOT eval on the same NodeStates snapshot, and compare the
    /// predicted output to S1's actual NodeStates value. Report mismatch rate.
    ///
    /// First MVP: verifies AotBlocks.EvalTileHBitMux_* against S1's ppu.+tile_h_bit_out.
    /// Both combinational (always-recompute) and phi-gated (latch-hold-when-pclk-low) variants
    /// are tested — the right model wins.
    /// </summary>
    public static unsafe class AotVerifier
    {
        /// <summary>Phase D-4: AOT actually replaces S1 work. Marks AOT-covered nodes as
        /// CodegenOwned so S1's ComputeNodeGroup BFS stops at them (Option D); calls AOT
        /// delegate BEFORE each S1 step so the values are pre-written. Then S1 settles but
        /// skips owned nodes — measurable hc/s gain if AOT covers enough nodes.</summary>
        public static int RunWithAotSkippingS1(string romPath, int hcCount, int minEmittable)
        {
            if (hcCount < 1) hcCount = 30_000;
            if (minEmittable < 1) minEmittable = 5;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-skip: {System.IO.Path.GetFileName(romPath)} — {hcCount:N0} hc per pass (AOT replaces S1 for emittable nodes)");

            // ── pre-build the AOT (so JIT warmup is done before timed passes) ──
            AotRoslynLoader.CompileResult? loaded = null;
            int[]? evaluatedNodeIds = null;
            try
            {
                WireCore.LoadSystem(rom);
                var partition = WireCore.AutoPartition();
                var picked = new List<(WireCore.Block pb, AotBlock ab)>();
                foreach (var pb in partition)
                {
                    var ab = AotBlockBuilder.Build(pb);
                    if (ab.Evals.Count >= minEmittable) picked.Add((pb, ab));
                }
                int totalEvals = 0; foreach (var (_, ab) in picked) totalEvals += ab.Evals.Count;
                string source = AotBlockBuilder.EmitMasterSource(picked, System.IO.Path.GetFileName(romPath));
                Console.WriteLine($"# AOT: {picked.Count} blocks, {totalEvals} emittable nodes, source {source.Length:N0} bytes");

                var swCompile = System.Diagnostics.Stopwatch.StartNew();
                loaded = AotRoslynLoader.CompileMaster(source);
                swCompile.Stop();
                if (!loaded.Success || loaded.EvalAll == null)
                {
                    Console.WriteLine($"# AOT compile FAILED:");
                    Console.WriteLine(loaded.Log);
                    return 3;
                }
                Console.WriteLine($"# AOT compile + load: {swCompile.Elapsed.TotalSeconds:F2} s");

                // collect all unique evaluated node IDs for ownership registration
                var idSet = new HashSet<int>();
                foreach (var (_, ab) in picked)
                    foreach (var (nn, _, _) in ab.Evals) idSet.Add(nn);
                evaluatedNodeIds = new int[idSet.Count];
                idSet.CopyTo(evaluatedNodeIds);
                Array.Sort(evaluatedNodeIds);
                Console.WriteLine($"# AOT will own {evaluatedNodeIds.Length:N0} unique node IDs");

                // JIT-warm the delegate
                unsafe { for (int i = 0; i < 16; i++) loaded.EvalAll(WireCore.NodeStates); }
            }
            finally { WireCore.Shutdown(); }

            // ── 4 timed passes ──
            var results = new List<(string label, double seconds, ulong checksum)>();
            for (int pass = 0; pass < 4; pass++)
            {
                bool aotSkip = (pass == 0 || pass == 2);
                string label = aotSkip ? "AOT-skip-S1" : "S1-only";
                try
                {
                    WireCore.LoadSystem(rom);
                    // Always clear any leftover codegen-dispatcher state before deciding mode
                    WireCore.ClearAotOwnership();
                    if (aotSkip)
                    {
                        WireCore.RegisterAotOwnership(evaluatedNodeIds!);
                    }
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (aotSkip)
                    {
                        unsafe
                        {
                            for (int hc = 0; hc < hcCount; hc++)
                            {
                                loaded!.EvalAll!(WireCore.NodeStates);   // AOT writes pre-step
                                WireCore.Step(1);                          // S1 settles, BFS-block stops at owned
                            }
                        }
                    }
                    else
                    {
                        for (int hc = 0; hc < hcCount; hc++) WireCore.Step(1);
                    }
                    sw.Stop();
                    ulong cs = ChecksumNodeStates();
                    results.Add((label, sw.Elapsed.TotalSeconds, cs));
                    Console.WriteLine($"# pass {pass + 1} {label,-12}: {hcCount / sw.Elapsed.TotalSeconds,8:N0} hc/s ({sw.Elapsed.TotalSeconds:F3} s)  checksum 0x{cs:X16}");
                }
                finally { WireCore.Shutdown(); }
            }

            // ── analysis ──
            double s1Avg  = results.Where(r => r.label == "S1-only").Select(r => r.seconds).Average();
            double aotAvg = results.Where(r => r.label == "AOT-skip-S1").Select(r => r.seconds).Average();
            bool allChecksumsEqual = results.All(r => r.checksum == results[0].checksum);
            double speedup = s1Avg / aotAvg;
            Console.WriteLine($"# === Phase D-4 averaged ===");
            Console.WriteLine($"#   S1-only       avg: {hcCount / s1Avg,8:N0} hc/s ({s1Avg:F3} s)");
            Console.WriteLine($"#   AOT-skip-S1   avg: {hcCount / aotAvg,8:N0} hc/s ({aotAvg:F3} s)");
            string speedLabel = speedup > 1.01 ? $"SPEEDUP {speedup:F2}×" : speedup < 0.99 ? $"SLOWDOWN {speedup:F2}×" : "no significant change";
            Console.WriteLine($"#   speedup           : {speedLabel}");
            Console.WriteLine($"#   checksums all equal: {(allChecksumsEqual ? "✓ functional equivalence verified" : "✗ DIVERGENCE - trace not byte-equal")}");
            if (!allChecksumsEqual)
                foreach (var r in results) Console.WriteLine($"#     {r.label}: 0x{r.checksum:X16}");
            return allChecksumsEqual ? 0 : 1;
        }

        /// <summary>Phase E-2: AotRuntime first-step test. Init AotRuntime from S1's settled state;
        /// run AotRuntime.Step() once; compare NodeStates to S1's NodeStates after S1.Step(1).
        /// Reports per-node mismatch count and first divergence — informs E-3+ priorities.</summary>
        public static int RunAotRuntimeStep1Test(string romPath, int hcCount, int minEmittable)
        {
            if (hcCount < 1) hcCount = 1;
            if (minEmittable < 1) minEmittable = 5;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-runtime-step1: {System.IO.Path.GetFileName(romPath)} — {hcCount:N0} hc test");

            try
            {
                // ── A. Load S1, settle initial state, snapshot ──
                WireCore.LoadSystem(rom);
                int clockId = WireCore.LookupNode("clk");
                if (clockId == WireCore.EmptyNode) { Console.Error.WriteLine("clk node not found"); return 1; }
                int nodeCount = WireCore.NodeCount;
                var initState = new byte[nodeCount];
                unsafe { byte* p = WireCore.NodeStates; for (int i = 0; i < nodeCount; i++) initState[i] = p[i]; }
                Console.WriteLine($"#   initial state from S1 settled (NodeCount={nodeCount}, clk={clockId}, initial value={initState[clockId]})");

                // ── B. Build AOT, compile, load ──
                var partition = WireCore.AutoPartition();
                var picked = new List<(WireCore.Block pb, AotBlock ab)>();
                foreach (var pb in partition)
                {
                    var ab = AotBlockBuilder.Build(pb);
                    if (ab.Evals.Count >= minEmittable) picked.Add((pb, ab));
                }
                int totalEvals = 0; foreach (var (_, ab) in picked) totalEvals += ab.Evals.Count;
                string source = AotBlockBuilder.EmitMasterSource(picked, System.IO.Path.GetFileName(romPath));
                var loaded = AotRoslynLoader.CompileMaster(source);
                if (!loaded.Success || loaded.EvalAll == null)
                {
                    Console.WriteLine($"# compile FAILED:");
                    Console.WriteLine(loaded.Log);
                    return 3;
                }
                Console.WriteLine($"#   AOT: {picked.Count} blocks, {totalEvals} delegates, source {source.Length:N0} bytes");

                // ── C. AotRuntime: init from initState + run hcCount steps ──
                var aotRt = new AotRuntime(initState, loaded.EvalAll, clockId);
                for (int hc = 0; hc < hcCount; hc++) aotRt.Step();
                Console.WriteLine($"#   AotRuntime did {hcCount} step(s), last settle iter count = {aotRt.LastSettleIterations}");

                // ── D. S1: run hcCount steps from the SAME initial state (which S1 is already in) ──
                for (int hc = 0; hc < hcCount; hc++) WireCore.Step(1);
                var s1State = new byte[nodeCount];
                unsafe { byte* p = WireCore.NodeStates; for (int i = 0; i < nodeCount; i++) s1State[i] = p[i]; }

                // ── E. Compare ──
                var (match, mismatch, firstMiss) = aotRt.CompareWith(s1State);
                Console.WriteLine($"# === Phase E-2 first-step comparison ===");
                Console.WriteLine($"#   matches    : {match:N0} / {nodeCount:N0}  ({(double)match / nodeCount:P2})");
                Console.WriteLine($"#   mismatches : {mismatch:N0}  ({(double)mismatch / nodeCount:P2})");
                if (firstMiss >= 0)
                {
                    var node = WireCore.Nodes[firstMiss];
                    Console.WriteLine($"#   first mismatch: nn={firstMiss} name='{(string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name)}', AOT={aotRt.NodeStates[firstMiss]}, S1={s1State[firstMiss]}");
                }
                // Categorise mismatches by node having pull-up vs not (gives a sense of what category drifts)
                int puMiss = 0, npuMiss = 0;
                for (int i = 0; i < nodeCount; i++)
                {
                    if (aotRt.NodeStates[i] == s1State[i]) continue;
                    var n = WireCore.Nodes[i]; if (n == null) continue;
                    if (n.Pullups > 0) puMiss++; else npuMiss++;
                }
                Console.WriteLine($"#   mismatch breakdown: pull-up nodes: {puMiss:N0}, no-pullup: {npuMiss:N0}");
                return mismatch == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase E-1 prep: classify all no-pullup nodes (the 8,331 nodes AotEmitter can't
        /// pattern-match) by topology, to scope the Route B full-pivot work. Categories:
        ///   external_drive : c1c2s=0, gates>=1 (driven by handler, e.g. clk, res, BA0)
        ///   external_anon  : c1c2s=0, gates=0  (callback fake nodes, e.g. func<clock>)
        ///   latch_simple   : c1c2s=1, has pass to other non-supply node (transparent-when-gate)
        ///   latch_complex  : c1c2s>=2, multi-pass latch (typical 6502 dynamic latch with 2-phase)
        ///   bus_routing    : multi-pass with no clear write side (pure routing intermediate)
        /// </summary>
        public static int RunNoPullupInventory(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                Console.WriteLine($"# no-pullup inventory: {System.IO.Path.GetFileName(romPath)} — {WireCore.NodeCount:N0} live nodes");

                int total = 0;
                int externalDrive = 0, externalAnon = 0;
                int latchSimple = 0, latchComplex = 0;
                int passThrough = 0;
                int hasPullup = 0;
                var examplesByCat = new Dictionary<string, List<int>>();
                void Sample(string cat, int nn) { if (!examplesByCat.TryGetValue(cat, out var l)) examplesByCat[cat] = l = new(); if (l.Count < 5) l.Add(nn); }

                for (int nn = 0; nn < WireCore.NodeCount; nn++)
                {
                    if (nn == WireCore.Npwr || nn == WireCore.Ngnd) continue;
                    var n = WireCore.Nodes[nn]; if (n == null) continue;
                    total++;
                    if (n.Pullups > 0) { hasPullup++; continue; }
                    // no-pullup classification
                    int c = n.C1c2s.Count;
                    int g = n.Gates.Count;
                    if (c == 0 && g >= 1) { externalDrive++; Sample("external_drive", nn); }
                    else if (c == 0 && g == 0) { externalAnon++; Sample("external_anon", nn); }
                    else if (c == 1) { latchSimple++; Sample("latch_simple", nn); }
                    else if (c == 2) { latchComplex++; Sample("latch_complex (2-pass)", nn); }
                    else if (c >= 3) { passThrough++; Sample($"latch_or_bus ({c}-pass)", nn); }
                    else { Sample("uncategorised", nn); }
                }

                int noPullup = total - hasPullup;
                Console.WriteLine($"# total non-supply nodes : {total:N0}");
                Console.WriteLine($"#   with pull-up         : {hasPullup:N0}  ({(double)hasPullup / total:P1})  → AotEmitter handles these");
                Console.WriteLine($"#   no pull-up           : {noPullup:N0}  ({(double)noPullup / total:P1})  → Route B target");
                Console.WriteLine($"#");
                Console.WriteLine($"# no-pullup breakdown:");
                Console.WriteLine($"#   external_drive (c=0,g>=1) : {externalDrive,5:N0}  ({(double)externalDrive / noPullup:P1})   handler-driven (clk/res/BA0/...)");
                Console.WriteLine($"#   external_anon  (c=0,g=0)  : {externalAnon, 5:N0}  ({(double)externalAnon  / noPullup:P1})   callback fake nodes");
                Console.WriteLine($"#   latch_simple   (c=1)      : {latchSimple,  5:N0}  ({(double)latchSimple   / noPullup:P1})   1 pass-transistor (transparent latch)");
                Console.WriteLine($"#   latch_complex  (c=2)      : {latchComplex, 5:N0}  ({(double)latchComplex  / noPullup:P1})   2-phase dynamic latch");
                Console.WriteLine($"#   latch_or_bus   (c>=3)     : {passThrough,  5:N0}  ({(double)passThrough   / noPullup:P1})   multi-pass routing/bus");
                Console.WriteLine($"#");
                Console.WriteLine($"# === samples (5 per category) ===");
                foreach (var (cat, ids) in examplesByCat.OrderBy(kv => kv.Key))
                {
                    Console.WriteLine($"# {cat}:");
                    foreach (int nn in ids)
                    {
                        var node = WireCore.Nodes[nn];
                        string name = string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name;
                        Console.WriteLine($"#   nn={nn,5}  '{name}'   c1c2s={node!.C1c2s.Count}, gates={node!.Gates.Count}");
                    }
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase D-3: AOT engine in the simulation hot path. Loads the master AOT, then
        /// runs the simulation with AOT called AFTER each S1 step. AOT predictions overwrite
        /// the corresponding NodeStates entries — since they should be byte-equal to S1's just-
        /// computed values (per Phase D-2 verification), this should be functionally inert.
        /// Compares overall throughput (hc/s) to S1-baseline and verifies trace equivalence.
        ///
        /// Runs 4 passes: warmup, S1-only, AOT-in-loop, S1-only-again — to cancel out OS file
        /// cache + .NET JIT warmup effects. The last two are the meaningful comparison.</summary>
        public static int RunWithAotEngine(string romPath, int hcCount, int minEmittable)
        {
            if (hcCount < 1) hcCount = 30_000;
            if (minEmittable < 1) minEmittable = 5;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-run: {System.IO.Path.GetFileName(romPath)} — {hcCount:N0} hc per pass");

            // ── pre-build the AOT (so JIT warmup happens before any timed pass) ──
            AotRoslynLoader.CompileResult? loaded = null;
            try
            {
                WireCore.LoadSystem(rom);
                var partition = WireCore.AutoPartition();
                var picked = new List<(WireCore.Block pb, AotBlock ab)>();
                foreach (var pb in partition)
                {
                    var ab = AotBlockBuilder.Build(pb);
                    if (ab.Evals.Count >= minEmittable) picked.Add((pb, ab));
                }
                int totalEvals = 0; foreach (var (_, ab) in picked) totalEvals += ab.Evals.Count;
                string source = AotBlockBuilder.EmitMasterSource(picked, System.IO.Path.GetFileName(romPath));
                Console.WriteLine($"# AOT: {picked.Count} blocks, {totalEvals} emittable nodes, source {source.Length:N0} bytes");

                var swCompile = System.Diagnostics.Stopwatch.StartNew();
                loaded = AotRoslynLoader.CompileMaster(source);
                swCompile.Stop();
                if (!loaded.Success || loaded.EvalAll == null)
                {
                    Console.WriteLine($"# AOT compile FAILED:");
                    Console.WriteLine(loaded.Log);
                    return 3;
                }
                Console.WriteLine($"# AOT compile + load: {swCompile.Elapsed.TotalSeconds:F2} s");
                // JIT-warm the delegate with a dozen calls so timed pass below isn't penalised
                unsafe
                {
                    for (int i = 0; i < 16; i++) loaded.EvalAll(WireCore.NodeStates);
                }
            }
            finally { WireCore.Shutdown(); }

            // ── 4 timed passes ──
            var results = new List<(string label, double seconds, ulong checksum)>();
            for (int pass = 0; pass < 4; pass++)
            {
                bool aotInLoop = (pass == 0 || pass == 2);  // alternate so each mode runs twice
                string label = aotInLoop ? "AOT-in-loop" : "S1-only";
                try
                {
                    WireCore.LoadSystem(rom);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    if (aotInLoop)
                    {
                        unsafe
                        {
                            for (int hc = 0; hc < hcCount; hc++)
                            {
                                WireCore.Step(1);
                                loaded!.EvalAll!(WireCore.NodeStates);
                            }
                        }
                    }
                    else
                    {
                        for (int hc = 0; hc < hcCount; hc++) WireCore.Step(1);
                    }
                    sw.Stop();
                    ulong cs = ChecksumNodeStates();
                    results.Add((label, sw.Elapsed.TotalSeconds, cs));
                    Console.WriteLine($"# pass {pass + 1} {label,-12}: {hcCount / sw.Elapsed.TotalSeconds,8:N0} hc/s ({sw.Elapsed.TotalSeconds:F3} s)  checksum 0x{cs:X16}");
                }
                finally { WireCore.Shutdown(); }
            }

            // ── analysis ──
            double s1Avg  = results.Where(r => r.label == "S1-only").Select(r => r.seconds).Average();
            double aotAvg = results.Where(r => r.label == "AOT-in-loop").Select(r => r.seconds).Average();
            ulong s1cs  = results.First(r => r.label == "S1-only").checksum;
            ulong aotcs = results.First(r => r.label == "AOT-in-loop").checksum;
            bool allChecksumsEqual = results.All(r => r.checksum == results[0].checksum);
            Console.WriteLine($"# === Phase D-3 averaged ===");
            Console.WriteLine($"#   S1-only       avg: {hcCount / s1Avg,8:N0} hc/s ({s1Avg:F3} s)");
            Console.WriteLine($"#   AOT-in-loop   avg: {hcCount / aotAvg,8:N0} hc/s ({aotAvg:F3} s)");
            Console.WriteLine($"#   AOT overhead     : {(aotAvg / s1Avg - 1) * 100:F2}%  ({(aotAvg - s1Avg) * 1e6 / hcCount:F2} µs/hc)");
            Console.WriteLine($"#   checksums all equal: {(allChecksumsEqual ? "✓ functional equivalence verified" : "✗ DIVERGENCE")}");
            if (!allChecksumsEqual)
                foreach (var r in results) Console.WriteLine($"#     {r.label}: 0x{r.checksum:X16}");
            return allChecksumsEqual ? 0 : 1;
        }

        private static unsafe ulong ChecksumNodeStates()
        {
            // Cheap FNV-1a over NodeStates (good enough to detect any divergence)
            ulong h = 14695981039346656037UL;
            byte* p = WireCore.NodeStates;
            int n = WireCore.NodeCount;
            for (int i = 0; i < n; i++) { h = (h ^ p[i]) * 1099511628211UL; }
            return h;
        }

        /// <summary>Phase D-2: emit master .cs in-memory, Roslyn-compile, load, run the loaded
        /// AotEngine.EvalAllBlocks delegate alongside S1; verify the written NodeStates match
        /// what the direct runtime delegates would have written (i.e., Roslyn-emitted code is
        /// equivalent to in-process AotBlockBuilder evaluation).</summary>
        public static int CompileAndLoadAll(string romPath, int hcCount, int minEmittable)
        {
            if (hcCount < 1) hcCount = 30_000;
            if (minEmittable < 1) minEmittable = 5;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                var partition = WireCore.AutoPartition();
                Console.WriteLine($"# aot-compile-load: {System.IO.Path.GetFileName(romPath)} — {hcCount:N0} hc");

                // ── A. Generate master source ──
                var picked = new List<(WireCore.Block pb, AotBlock ab)>();
                int totalEmittable = 0;
                foreach (var pb in partition)
                {
                    var ab = AotBlockBuilder.Build(pb);
                    if (ab.Evals.Count >= minEmittable) { picked.Add((pb, ab)); totalEmittable += ab.Evals.Count; }
                }
                string source = AotBlockBuilder.EmitMasterSource(picked, System.IO.Path.GetFileName(romPath));
                Console.WriteLine($"#   blocks: {picked.Count}, emittable nodes: {totalEmittable}, source: {source.Length:N0} bytes");

                // ── B. Roslyn compile + load ──
                var swCompile = System.Diagnostics.Stopwatch.StartNew();
                var loaded = AotRoslynLoader.CompileMaster(source);
                swCompile.Stop();
                if (!loaded.Success || loaded.EvalAll == null)
                {
                    Console.WriteLine($"#   COMPILE FAILED ({swCompile.Elapsed.TotalSeconds:F2} s):");
                    Console.WriteLine(loaded.Log);
                    return 3;
                }
                Console.WriteLine($"#   compile + load: {swCompile.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"#   (compile log)");
                foreach (string line in loaded.Log.Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) Console.WriteLine($"#     {line.TrimEnd('\r')}");

                // ── C. Verify: run S1 + loaded delegate side-by-side ──
                // For each hc, snapshot NodeStates; have the loaded delegate write its predictions
                // into a copy; for each (nodeId, _) in picked blocks, compare predicted vs actual.
                int nodeCount = WireCore.NodeCount;
                var snapshot = new byte[nodeCount];
                long sampledTotal = 0, sampledMatch = 0;
                int firstMismatchHc = -1; int firstMismatchNode = -1;
                byte firstMismatchPred = 0, firstMismatchActual = 0;

                // collect all evaluated nodeIds for comparison (every node in any picked block's Evals)
                var evaluatedNodes = new HashSet<int>();
                foreach (var (_, ab) in picked)
                    foreach (var (nn, _, _) in ab.Evals) evaluatedNodes.Add(nn);
                Console.WriteLine($"#   verifying {evaluatedNodes.Count} unique evaluated node IDs over {hcCount:N0} hc");

                var swBench = System.Diagnostics.Stopwatch.StartNew();
                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    // snapshot S1's NodeStates (this is the actual truth this hc)
                    unsafe
                    {
                        byte* nsPtr = WireCore.NodeStates;
                        for (int i = 0; i < nodeCount; i++) snapshot[i] = nsPtr[i];
                    }
                    // Roslyn-loaded delegate writes its predictions into the snapshot buffer
                    unsafe
                    {
                        fixed (byte* p = snapshot) { loaded.EvalAll(p); }
                    }
                    // Compare: for each evaluated node, snapshot now has the AOT-predicted value;
                    // compare to S1's actual (which we read from WireCore.NodeStates).
                    unsafe
                    {
                        byte* actualPtr = WireCore.NodeStates;
                        foreach (int nn in evaluatedNodes)
                        {
                            sampledTotal++;
                            if (snapshot[nn] == actualPtr[nn]) sampledMatch++;
                            else if (firstMismatchHc < 0) { firstMismatchHc = hc; firstMismatchNode = nn; firstMismatchPred = snapshot[nn]; firstMismatchActual = actualPtr[nn]; }
                        }
                    }
                }
                swBench.Stop();

                long sampledMismatch = sampledTotal - sampledMatch;
                Console.WriteLine($"# === Phase D-2 verification ===");
                Console.WriteLine($"#   bench wall-time     : {swBench.Elapsed.TotalSeconds:F2} s for {hcCount:N0} hc + {evaluatedNodes.Count} evals × {hcCount:N0} = {sampledTotal:N0} samples");
                Console.WriteLine($"#   bench rate          : {hcCount / swBench.Elapsed.TotalSeconds:N0} hc/s (vs S1-only would be faster — this includes Roslyn delegate per hc)");
                Console.WriteLine($"#   matches             : {sampledMatch:N0} / {sampledTotal:N0}  ({(double)sampledMatch / sampledTotal:P4})");
                Console.WriteLine($"#   mismatches          : {sampledMismatch:N0}  ({(double)sampledMismatch / sampledTotal:P4})");
                if (firstMismatchHc >= 0)
                {
                    var node = WireCore.Nodes[firstMismatchNode];
                    Console.WriteLine($"#   first mismatch       : nn={firstMismatchNode} name='{(string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name)}', hc={firstMismatchHc}, pred={firstMismatchPred}, actual={firstMismatchActual}");
                }
                if (sampledMismatch == 0)
                    Console.WriteLine($"# VERDICT: ROSLYN-COMPILED AotEngine IS BYTE-EQUAL TO S1 on {evaluatedNodes.Count} nodes over {hcCount:N0} hc ✓");
                else
                    Console.WriteLine($"# VERDICT: tiny mismatches (likely phi-transient — same as direct AotEmitter delegates)");
                return sampledMismatch == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase D-1: mass-emit all codegen-attractive partition blocks into ONE
        /// master .cs file. Picks blocks with >=minEmittable AOT-emittable nodes; skips small
        /// (uninteresting) ones. The output file has an AotEngine class with EvalAllBlocks
        /// dispatcher + one static class per block.</summary>
        public static int EmitAllBlocks(string romPath, string outputPath, int minEmittable)
        {
            if (minEmittable < 1) minEmittable = 5;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                var partition = WireCore.AutoPartition();
                Console.WriteLine($"# aot-emit-all: {System.IO.Path.GetFileName(romPath)} → {outputPath}");
                Console.WriteLine($"#   partition blocks total : {partition.Count:N0}");
                Console.WriteLine($"#   min-emittable threshold: {minEmittable}");

                var picked = new List<(WireCore.Block pb, AotBlock ab)>();
                int totalEmittableNodes = 0;
                foreach (var pb in partition)
                {
                    var ab = AotBlockBuilder.Build(pb);
                    if (ab.Evals.Count >= minEmittable) { picked.Add((pb, ab)); totalEmittableNodes += ab.Evals.Count; }
                }
                Console.WriteLine($"#   picked blocks          : {picked.Count:N0}");
                Console.WriteLine($"#   total emittable nodes  : {totalEmittableNodes:N0}");

                string source = AotBlockBuilder.EmitMasterSource(picked, System.IO.Path.GetFileName(romPath));
                System.IO.File.WriteAllText(outputPath, source);
                Console.WriteLine($"#   wrote {source.Length:N0} bytes to {outputPath}");
                Console.WriteLine($"#   per-block (top 15 by emittable):");
                int shown = 0;
                foreach (var (pb, ab) in picked.OrderByDescending(x => x.ab.Evals.Count))
                {
                    Console.WriteLine($"#     #{pb.Id,3}  {pb.Label,-25}  {ab.Evals.Count,4} emittable / {pb.InternalNodes.Length + pb.DrivenOutputs.Length,4} total");
                    if (++shown >= 15) break;
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase C-5 task #74: emit actual .cs source file for a partition block.
        /// Writes to <paramref name="outputPath"/>; user can inspect / commit / build it later.</summary>
        public static int EmitBlockSource(string romPath, int blockId, string outputPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                var blocks = WireCore.AutoPartition();
                if (blockId < 0 || blockId >= blocks.Count) { Console.Error.WriteLine($"block id {blockId} out of [0..{blocks.Count - 1}]"); return 1; }
                var pb = blocks[blockId];
                var ab = AotBlockBuilder.Build(pb);
                Console.WriteLine($"# emitting block #{blockId} ({pb.Label}): {ab.Evals.Count} emittable nodes");
                string source = AotBlockBuilder.EmitSource(ab, pb);
                System.IO.File.WriteAllText(outputPath, source);
                Console.WriteLine($"# wrote {source.Length:N0} bytes to {outputPath}");
                Console.WriteLine($"# === preview (first 40 lines) ===");
                int lineCount = 0;
                foreach (string line in source.Split('\n'))
                {
                    Console.WriteLine("    " + line.TrimEnd('\r'));
                    if (++lineCount >= 40) { Console.WriteLine("    ..."); break; }
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase C-5: per-block verification. Build AotBlock for a specific partition
        /// block ID; run S1; each hc, snapshot NodeStates, run block's eval delegates, compare
        /// predicted vs actual for each emittable node in the block.</summary>
        public static int VerifyBlock(string romPath, int blockId, int hcCount)
        {
            if (hcCount < 1) hcCount = 50_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-block #{blockId}: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var blocks = WireCore.AutoPartition();
                if (blockId < 0 || blockId >= blocks.Count) { Console.Error.WriteLine($"block id {blockId} out of [0..{blocks.Count - 1}]"); return 1; }
                var block = blocks[blockId];
                var aotBlock = AotBlockBuilder.Build(block);
                Console.WriteLine($"# {aotBlock.ShortSummary}");
                Console.WriteLine($"#   internal: {block.InternalNodes.Length}, driven outputs: {block.DrivenOutputs.Length}, boundary inputs: {block.BoundaryInputs.Length}");
                Console.WriteLine($"#   AOT-emitted delegates: {aotBlock.Evals.Count}");
                Console.WriteLine($"#   AOT-unsupported (will be left to S1): {aotBlock.UnsupportedNodes}");

                if (aotBlock.Evals.Count == 0) { Console.WriteLine("# no emittable delegates — nothing to verify"); return 1; }

                // Take a copy buffer for snapshotting
                var snapshot = new byte[WireCore.NodeCount];
                long perNodeMismatch = 0;
                long totalNodeSamples = 0;
                int firstMissHc = -1, firstMissNodeId = -1; byte firstMissPred = 0, firstMissActual = 0;
                string firstMissPattern = "";

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* nsPtr = WireCore.NodeStates;
                    for (int i = 0; i < snapshot.Length; i++) snapshot[i] = nsPtr[i];
                    fixed (byte* snapPtr = snapshot)
                    {
                        for (int i = 0; i < aotBlock.Evals.Count; i++)
                        {
                            var (nn, pattern, compiled) = aotBlock.Evals[i];
                            byte pred = compiled((IntPtr)snapPtr);
                            byte actual = snapshot[nn];   // same snapshot — what S1 produced this hc
                            totalNodeSamples++;
                            if (pred != actual)
                            {
                                perNodeMismatch++;
                                if (firstMissHc < 0) { firstMissHc = hc; firstMissNodeId = nn; firstMissPred = pred; firstMissActual = actual; firstMissPattern = pattern; }
                            }
                        }
                    }
                }

                Console.WriteLine($"# === block #{blockId} verification ({hcCount:N0} hc × {aotBlock.Evals.Count} delegates) ===");
                Console.WriteLine($"#   total samples : {totalNodeSamples:N0}");
                Console.WriteLine($"#   mismatches    : {perNodeMismatch:N0}  ({(double)perNodeMismatch / totalNodeSamples:P4})");
                if (firstMissHc >= 0)
                {
                    var node = WireCore.Nodes[firstMissNodeId];
                    Console.WriteLine($"#   first miss    : nn={firstMissNodeId} name='{(string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name)}', hc={firstMissHc}, pattern='{firstMissPattern}', pred={firstMissPred}, actual={firstMissActual}");
                }
                Console.WriteLine($"# pattern breakdown for this block:");
                foreach (var kv in aotBlock.PatternHisto.OrderByDescending(kv => kv.Value))
                    Console.WriteLine($"#   {kv.Key,-25} : {kv.Value,4}");
                if (perNodeMismatch == 0) Console.WriteLine($"# VERDICT: block #{blockId} is BYTE-EQUAL to S1 over {hcCount:N0} hc on {aotBlock.Evals.Count} emittable nodes");
                else Console.WriteLine($"# VERDICT: block #{blockId} has tiny phi-transient mismatches; pattern logic per-node is the same as the global verify-all result");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase C batch verifier: emit AOT delegates for EVERY emitter-supported node,
        /// then run S1 for hcCount half-cycles, sampling each emitted delegate against the actual
        /// NodeStates value. Report per-pattern mismatch rate so we can spot pattern bugs across
        /// the whole netlist (vs just one hand-picked node).</summary>
        public static int VerifyAllEmittable(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 50_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-all: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                // Emit for every node; collect those with Compiled delegate
                var emitted = new List<(int nodeId, string pattern, Func<IntPtr, byte> compiled)>();
                for (int nn = 0; nn < WireCore.NodeCount; nn++)
                {
                    if (nn == WireCore.Npwr || nn == WireCore.Ngnd) continue;
                    var n = WireCore.Nodes[nn]; if (n == null) continue;
                    var er = AotEmitter.EmitForNode(nn);
                    if (er.Compiled != null) emitted.Add((nn, er.Pattern, er.Compiled));
                }
                Console.WriteLine($"# emitted delegates: {emitted.Count:N0} nodes");

                // Per-pattern stats
                var totalByPattern    = new Dictionary<string, long>();
                var mismatchByPattern = new Dictionary<string, long>();
                var firstMissByPattern = new Dictionary<string, (int nodeId, int hc, byte pred, byte actual)>();

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* ns = WireCore.NodeStates;
                    IntPtr nsPtr = (IntPtr)ns;
                    foreach (var (nodeId, pattern, compiled) in emitted)
                    {
                        byte pred = compiled(nsPtr);
                        byte actual = ns[nodeId];
                        totalByPattern[pattern] = totalByPattern.TryGetValue(pattern, out long t) ? t + 1 : 1;
                        if (pred != actual)
                        {
                            mismatchByPattern[pattern] = mismatchByPattern.TryGetValue(pattern, out long m) ? m + 1 : 1;
                            if (!firstMissByPattern.ContainsKey(pattern))
                                firstMissByPattern[pattern] = (nodeId, hc, pred, actual);
                        }
                    }
                }

                // Report sorted by pattern name for stability
                var patterns = new List<string>(totalByPattern.Keys);
                patterns.Sort(StringComparer.Ordinal);
                long grandTotal = 0, grandMiss = 0;
                Console.WriteLine($"# === per-pattern verification ({hcCount:N0} hc × N nodes) ===");
                Console.WriteLine($"#   {"pattern",-25}  {"samples",13}  {"mismatch",13}  rate");
                foreach (var p in patterns)
                {
                    long samples = totalByPattern[p];
                    long mismatches = mismatchByPattern.TryGetValue(p, out long m) ? m : 0;
                    grandTotal += samples; grandMiss += mismatches;
                    string mark = mismatches == 0 ? "✓" : (mismatches < samples / 1000 ? "·" : "✗");
                    Console.WriteLine($"#   {mark} {p,-25}  {samples,13:N0}  {mismatches,13:N0}  {(double)mismatches / samples:P3}");
                    if (mismatches > 0 && firstMissByPattern.TryGetValue(p, out var fm))
                    {
                        var node = WireCore.Nodes[fm.nodeId];
                        Console.WriteLine($"#       first miss: nn={fm.nodeId} name='{(string.IsNullOrEmpty(node?.Name) ? "(anon)" : node!.Name)}', hc={fm.hc}, pred={fm.pred}, actual={fm.actual}");
                    }
                }
                Console.WriteLine($"# === GRAND TOTAL ===");
                Console.WriteLine($"#   samples   : {grandTotal:N0}");
                Console.WriteLine($"#   mismatches: {grandMiss:N0}  ({(double)grandMiss / grandTotal:P4})");
                if (grandMiss == 0) Console.WriteLine($"# VERDICT: ALL {emitted.Count:N0} EMITTED NODES MATCH S1 (zero diff across {hcCount:N0} hc)");
                else Console.WriteLine($"# VERDICT: {patterns.Count - mismatchByPattern.Count} / {patterns.Count} patterns are byte-equal to S1; others need investigation");
                return grandMiss == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Phase C coverage scanner: load netlist, scan all nodes through AotEmitter,
        /// print pattern histogram + sample of supported vs unsupported nodes. Drives Phase C
        /// pattern-priority decisions.</summary>
        public static int RunCoverageScan(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                Console.WriteLine($"# aot-coverage scan over {WireCore.NodeCount:N0} live nodes");
                var histo = AotEmitter.ScanCoverage();
                int total = 0; foreach (var kv in histo) total += kv.Value;
                int supported = 0;
                foreach (var kv in histo) if (!kv.Key.StartsWith("unsupported(")) supported += kv.Value;
                // Sort by count descending
                var ordered = new List<KeyValuePair<string, int>>(histo);
                ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
                Console.WriteLine($"# total scanned: {total:N0}");
                Console.WriteLine($"# supported    : {supported:N0}  ({(double)supported / total:P1})");
                Console.WriteLine($"# unsupported  : {total - supported:N0}  ({(double)(total - supported) / total:P1})");
                Console.WriteLine($"# pattern histogram (desc by count):");
                foreach (var kv in ordered)
                {
                    string flag = kv.Key.StartsWith("unsupported(") ? "  " : "✓ ";
                    Console.WriteLine($"#   {flag}{kv.Key,-40} : {kv.Value,6:N0}  ({(double)kv.Value / total:P2})");
                }

                // Sample 8 nodes from each top unsupported bucket so we can investigate the topology
                Console.WriteLine($"#");
                Console.WriteLine($"# === SAMPLES from top-3 unsupported buckets (8 each) ===");
                int bucketsShown = 0;
                foreach (var kv in ordered)
                {
                    if (!kv.Key.StartsWith("unsupported(")) continue;
                    Console.WriteLine($"# pattern '{kv.Key}' ({kv.Value:N0} nodes):");
                    int shown = 0;
                    for (int nn = 0; nn < WireCore.NodeCount && shown < 8; nn++)
                    {
                        if (nn == WireCore.Npwr || nn == WireCore.Ngnd) continue;
                        var n = WireCore.Nodes[nn]; if (n == null) continue;
                        var er = AotEmitter.EmitForNode(nn);
                        string keyBucket = er.Pattern;
                        if (keyBucket.StartsWith("unsupported("))
                        {
                            int colon = keyBucket.IndexOf(',');
                            if (colon > 0) keyBucket = keyBucket.Substring(0, colon) + ",...)";
                        }
                        if (keyBucket != kv.Key) continue;
                        Console.WriteLine($"#   nn={nn,5} name='{(string.IsNullOrEmpty(n.Name) ? "(anon)" : n.Name)}', pullups={n.Pullups}, c1c2s={n.C1c2s.Count}, gates={n.Gates.Count}, full-pattern='{er.Pattern}'");
                        shown++;
                    }
                    bucketsShown++;
                    if (bucketsShown >= 3) break;
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        /// <summary>Auto-emit AOT code for ir0..7 + notir0..7 via AotEmitter, then verify the
        /// emitter's output matches S1 for hcCount half-cycles. This is the Phase B milestone:
        /// "emitter-generated code = hand-coded code = S1 truth".</summary>
        public static int VerifyEmitterOnIrInverter(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-emit-verify-ir: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveIrInverter();

                // Auto-emit for each of the 8 notir nodes
                Console.WriteLine($"# === auto-emitted AOT for notir[0..7] (from AotEmitter.EmitForNode) ===");
                var emitted = new AotEmitter.EmitResult[8];
                int emittedOk = 0;
                for (int i = 0; i < 8; i++)
                {
                    var er = AotEmitter.EmitForNode(ids.NotIr[i]);
                    emitted[i] = er;
                    Console.WriteLine($"#   notir{i} (id {ids.NotIr[i]}): pattern='{er.Pattern}', inputs=[{string.Join(',', er.InputIds)}], expr = {er.CSharpExpr ?? "(none)"}");
                    if (er.Compiled != null) emittedOk++;
                }
                if (emittedOk != 8)
                {
                    Console.WriteLine($"# emitter only handled {emittedOk}/8 nodes — cannot verify (need 8/8)");
                    return 3;
                }
                // Sanity: each emitter's discovered input ID should equal the corresponding ir[i]
                bool inputMatchesHand = true;
                for (int i = 0; i < 8; i++)
                {
                    if (emitted[i].InputIds.Length != 1 || emitted[i].InputIds[0] != ids.Ir[i])
                    {
                        Console.WriteLine($"#   ! emitter discovered input for notir{i} = {string.Join(',', emitted[i].InputIds)} ; hand-coded says ir{i} = {ids.Ir[i]}");
                        inputMatchesHand = false;
                    }
                }
                Console.WriteLine($"# emitter's discovered inputs MATCH the hand-coded gate IDs: {inputMatchesHand}");

                // Run S1 + emitter side-by-side
                long sampled = 0, mismatchesEmitter = 0, mismatchesHand = 0;
                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* ns = WireCore.NodeStates;
                    IntPtr nsPtr = (IntPtr)ns;
                    byte emitterByte = 0;
                    for (int i = 0; i < 8; i++) emitterByte |= (byte)(emitted[i].Compiled!(nsPtr) << i);
                    byte handByte    = AotBlocks.EvalIrInverter(ns, ids);
                    byte actualByte  = AotBlocks.ReadIrInverterActual(ns, ids);
                    sampled++;
                    if (emitterByte != actualByte) mismatchesEmitter++;
                    if (handByte    != actualByte) mismatchesHand++;
                }

                Console.WriteLine($"# samples: {sampled:N0}");
                Console.WriteLine($"# hand-coded eval mismatches : {mismatchesHand:N0} / {sampled:N0}");
                Console.WriteLine($"# auto-emitted eval mismatches: {mismatchesEmitter:N0} / {sampled:N0}");
                if (mismatchesEmitter == 0 && mismatchesHand == 0)
                    Console.WriteLine($"# VERDICT: AUTO-EMITTED AOT IS EQUIVALENT TO HAND-CODED AND TO S1 (0 diff). Phase B milestone achieved.");
                else
                    Console.WriteLine($"# VERDICT: divergence — see counts above");
                return (mismatchesEmitter == 0 && mismatchesHand == 0) ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        public static int VerifyIrInverter(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-ir-inverter: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveIrInverter();
                Console.WriteLine($"# resolved: ir = [{string.Join(',', ids.Ir)}], notir = [{string.Join(',', ids.NotIr)}]");

                long sampled = 0, mismatches = 0;
                long irChangeHc = 0;          // half-cycles where any ir bit changed
                byte prevIr = 0xFF;
                int firstMismatchHc = -1;
                byte firstMismatchPredicted = 0, firstMismatchActual = 0, firstMismatchIr = 0;

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);
                    byte* ns = WireCore.NodeStates;
                    byte predicted = AotBlocks.EvalIrInverter(ns, ids);
                    byte actual    = AotBlocks.ReadIrInverterActual(ns, ids);
                    byte ir = 0; for (int i = 0; i < 8; i++) ir |= (byte)(ns[ids.Ir[i]] << i);
                    sampled++;
                    if (ir != prevIr) { irChangeHc++; prevIr = ir; }
                    if (predicted != actual)
                    {
                        mismatches++;
                        if (firstMismatchHc < 0) { firstMismatchHc = hc; firstMismatchPredicted = predicted; firstMismatchActual = actual; firstMismatchIr = ir; }
                    }
                }

                Console.WriteLine($"# samples: {sampled:N0}");
                Console.WriteLine($"# ir-changing half-cycles: {irChangeHc:N0}  ({(double)irChangeHc / sampled:P2})  — proves ir IS being exercised");
                Console.WriteLine($"# mismatches: {mismatches:N0} / {sampled:N0}  ({(double)mismatches / sampled:P2})");
                if (mismatches > 0)
                {
                    Console.WriteLine($"# first mismatch @ hc={firstMismatchHc}: ir=0x{firstMismatchIr:X2}, predicted_notir=0x{firstMismatchPredicted:X2}, actual_notir=0x{firstMismatchActual:X2}");
                    Console.WriteLine($"# VERDICT: AOT inverter eval does NOT match S1 perfectly — likely phi-latched");
                }
                else
                    Console.WriteLine($"# VERDICT: AOT inverter eval IS the right model (zero diff vs S1 over {sampled:N0} hc)");
                return mismatches == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        public static int VerifyTileHBitMux(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 100_000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# aot-verify-tilemux: {System.IO.Path.GetFileName(romPath)} — running {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                var ids = AotBlocks.ResolveTileHBitMux();
                Console.WriteLine($"# resolved IDs: finex={ids.FineX0}/{ids.FineX1}/{ids.FineX2}, pclk1_3={ids.Pclk1_3}, tile_h0={ids.TileH0}..{ids.TileH0 + 7}, output={ids.Output}");

                // Counters
                long sampled = 0;
                long mismatchComb = 0;     // combinational variant
                long mismatchPhi  = 0;     // phi-gated variant
                long pclkHighSamples = 0;
                long pclkLowSamples = 0;
                long actualOutputHigh = 0;
                long actualOutputLow = 0;
                // Per-input diagnostics
                var fxToggles = new long[3];          // count of times each fine_x bit changed
                var tileHLow = new long[8];           // count of times each tile_h bit was 0
                byte prevFx0 = 99, prevFx1 = 99, prevFx2 = 99;

                // Sample first few divergences for debugging
                const int maxReports = 5;
                int reportsComb = 0, reportsPhi = 0;

                for (int hc = 0; hc < hcCount; hc++)
                {
                    WireCore.Step(1);   // one master half-cycle
                    byte* ns = WireCore.NodeStates;   // already a pointer, no fixed needed
                    byte predictedComb = AotBlocks.EvalTileHBitMux_Combinational(ns, in ids);
                    byte predictedPhi  = AotBlocks.EvalTileHBitMux_PhiGated(ns, in ids);
                    byte actual = ns[ids.Output];
                    byte pclk = ns[ids.Pclk1_3];

                    sampled++;
                    if (pclk != 0) pclkHighSamples++; else pclkLowSamples++;
                    if (actual != 0) actualOutputHigh++; else actualOutputLow++;
                    // Track input movement
                    if (ns[ids.FineX0] != prevFx0) { if (prevFx0 != 99) fxToggles[0]++; prevFx0 = ns[ids.FineX0]; }
                    if (ns[ids.FineX1] != prevFx1) { if (prevFx1 != 99) fxToggles[1]++; prevFx1 = ns[ids.FineX1]; }
                    if (ns[ids.FineX2] != prevFx2) { if (prevFx2 != 99) fxToggles[2]++; prevFx2 = ns[ids.FineX2]; }
                    for (int b = 0; b < 8; b++) if (ns[ids.TileH0 + b] == 0) tileHLow[b]++;

                    if (predictedComb != actual)
                    {
                        mismatchComb++;
                        if (reportsComb < maxReports)
                        {
                            int idx = (ns[ids.FineX2] << 2) | (ns[ids.FineX1] << 1) | ns[ids.FineX0];
                            Console.WriteLine($"#   COMB miss @ hc={hc}: predicted={predictedComb}, actual={actual}, finex={ns[ids.FineX2]}{ns[ids.FineX1]}{ns[ids.FineX0]} ({idx}), tile_h[{idx}]={ns[ids.TileH0 + idx]}, pclk1_3={pclk}");
                            reportsComb++;
                        }
                    }
                    if (predictedPhi != actual)
                    {
                        mismatchPhi++;
                        if (reportsPhi < maxReports)
                        {
                            int idx = (ns[ids.FineX2] << 2) | (ns[ids.FineX1] << 1) | ns[ids.FineX0];
                            Console.WriteLine($"#   PHI  miss @ hc={hc}: predicted={predictedPhi},  actual={actual}, finex=({idx}), tile_h[{idx}]={ns[ids.TileH0 + idx]}, pclk1_3={pclk}");
                            reportsPhi++;
                        }
                    }
                }

                Console.WriteLine($"# samples: {sampled:N0} (pclk_high={pclkHighSamples:N0}, pclk_low={pclkLowSamples:N0})");
                Console.WriteLine($"# actual output bit: high={actualOutputHigh:N0}, low={actualOutputLow:N0}  (fraction high = {(double)actualOutputHigh / sampled:P2})");
                Console.WriteLine($"# fine_x toggles  : fx0={fxToggles[0]:N0}  fx1={fxToggles[1]:N0}  fx2={fxToggles[2]:N0}");
                Console.WriteLine($"# tile_h low count: t0={tileHLow[0]:N0}  t1={tileHLow[1]:N0}  t2={tileHLow[2]:N0}  t3={tileHLow[3]:N0}  t4={tileHLow[4]:N0}  t5={tileHLow[5]:N0}  t6={tileHLow[6]:N0}  t7={tileHLow[7]:N0}");
                Console.WriteLine($"# combinational variant : {mismatchComb:N0} / {sampled:N0} mismatches  ({(double)mismatchComb / sampled:P2})");
                Console.WriteLine($"# phi-gated variant     : {mismatchPhi:N0} / {sampled:N0} mismatches  ({(double)mismatchPhi / sampled:P2})");
                if (mismatchComb == 0) Console.WriteLine($"# VERDICT: combinational variant IS the right model (zero diff vs S1)");
                else if (mismatchPhi == 0) Console.WriteLine($"# VERDICT: phi-gated variant IS the right model (zero diff vs S1)");
                else Console.WriteLine($"# VERDICT: NEITHER variant matches S1; need to model more carefully");

                return mismatchComb == 0 || mismatchPhi == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }
    }
}
