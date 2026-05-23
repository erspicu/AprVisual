using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using AprVisual.Native;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    /// <summary>
    /// Headless CLI entry (no window). Mirrors ref/AprNes/TestRunner.cs's shape:
    ///   --rom &lt;path&gt;            load and... (S1: actually this pops up the window — see below)
    ///   --test &lt;path&gt;           run to the blargg $6000 signature; print "PASS|..." / "FAIL(n)|...", exit code
    ///   --test-dir &lt;dir&gt;        batch-run *.nes in a directory
    ///   --max-wait &lt;sec&gt;        timeout per test (default 15)
    ///   --region ntsc|pal|dendy
    ///   --benchmark <rom>       headless throughput: simulated FPS / MIPS / raw step rate
    ///   --dump-module &lt;name&gt;    parse data/system-def/&lt;name&gt;.js (+ sub-modules / external files) and print a summary
    ///   --system-def-dir &lt;dir&gt; where the .js module files live (default: data/system-def)
    ///
    /// Note: `--rom` is handled here too so a single arg works; for `--rom` we hand back to the
    /// GUI path (open MainForm) rather than running headless.
    /// </summary>
    internal static class TestRunner
    {
        public static int Run(string[] args)
        {
            string? romPath = null, testPath = null, testDir = null, dumpModule = null, tracePath = null, shotPath = null, ppuDumpPath = null, probePath = null, probeVblPath = null, dumpNodeName = null, benchPath = null, verifyIrPath = null, dumpStatesPath = null, dumpBlockOutputs = null, dumpBlockStops = null;
            bool dumpPartition = false;
            int dumpBlockId = -1;
            string? aotVerifyTileMux = null;
            string? aotVerifyIrInv = null;
            string? aotEmitVerifyIr = null;
            string? aotCoverage = null;
            string? aotVerifyAll = null;
            string? aotVerifyBlockRom = null; int aotVerifyBlockId = -1;
            string? aotEmitBlockRom = null; int aotEmitBlockId = -1; string? aotEmitBlockPath = null;
            string? aotEmitAllRom = null; string? aotEmitAllPath = null; int aotMinEmittable = 5;
            string? aotCompileLoad = null;
            string? aotRun = null;
            string? aotSkip = null;
            string? aotNoPullupScan = null;
            string? aotRuntimeStep = null;
            string systemDefDir = WireCore.SystemDefDir;
            string shotOut = "screenshot.png";
            int maxWait = 15;
            int traceCycles = 64;
            int shotFrames = 3;
            int benchHcCount = 0;
            string region = "ntsc";
            bool benchmark = false, dumpSystem = false, dumpLevels = false, aluBench = false;
            int aluBenchN = 1_000_000;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom":             if (i + 1 < args.Length) romPath      = args[++i]; break;
                    case "--test":            if (i + 1 < args.Length) testPath     = args[++i]; break;
                    case "--test-dir":        if (i + 1 < args.Length) testDir      = args[++i]; break;
                    case "--trace":           if (i + 1 < args.Length) tracePath    = args[++i]; break;
                    case "--cycles":          if (i + 1 < args.Length) int.TryParse(args[++i], out traceCycles); break;
                    case "--screenshot":      if (i + 1 < args.Length) shotPath     = args[++i]; break;
                    case "--ppu-dump":        if (i + 1 < args.Length) ppuDumpPath  = args[++i]; break;
                    case "--probe2002":       if (i + 1 < args.Length) probePath    = args[++i]; break;
                    case "--probe-vbl":       if (i + 1 < args.Length) probeVblPath = args[++i]; break;
                    case "--dump-node":       if (i + 1 < args.Length) dumpNodeName = args[++i]; break;
                    case "--frames":          if (i + 1 < args.Length) int.TryParse(args[++i], out shotFrames); break;
                    case "--out":             if (i + 1 < args.Length) shotOut      = args[++i]; break;
                    case "--dump-module":     if (i + 1 < args.Length) dumpModule   = args[++i]; break;
                    case "--dump-system":     dumpSystem = true; break;
                    case "--dump-levels":     dumpLevels = true; break;   // Phase 1: SCC/level structure of the netlist (levelization de-risk)
                    case "--verify-ir":       if (i + 1 < args.Length) verifyIrPath = args[++i]; break;   // Phase 2 P2.2: extract Expr for pure-logic subset, verify Expr eval == S1 per half-cycle
                    case "--dump-states":     if (i + 1 < args.Length) dumpStatesPath = args[++i]; break;  // Phase 2 debug: step N hc, dump high-node ids (diff S1 vs --ir-interp to find divergence)
                    case "--dump-block":      if (i + 1 < args.Length) dumpBlockOutputs = args[++i]; break;  // Phase 2.5 codegen: reverse-closure a block from output names (Gemini r1 macro-block prep)
                    case "--block-stop":      if (i + 1 < args.Length) dumpBlockStops = args[++i]; break;
                    case "--dump-partition":  dumpPartition = true; break;   // Phase 2.5 Step 3: auto-partition the whole netlist into macro-blocks + histogram
                    case "--dump-block-id":   if (i + 1 < args.Length) int.TryParse(args[++i], out dumpBlockId); break;   // Step 3: detail of one auto-partition block
                    case "--aot-verify-tilemux": if (i + 1 < args.Length) aotVerifyTileMux = args[++i]; break;   // aot-codegen: hand-coded AOT for PPU tile_h MUX vs S1
                    case "--aot-verify-ir-inv":  if (i + 1 < args.Length) aotVerifyIrInv = args[++i]; break;   // aot-codegen: hand-coded AOT for 6502 IR inverter ladder vs S1
                    case "--aot-emit-verify-ir": if (i + 1 < args.Length) aotEmitVerifyIr = args[++i]; break;   // aot-codegen Phase B: AotEmitter auto-generates IR inverter code, verify against S1
                    case "--aot-coverage":       if (i + 1 < args.Length) aotCoverage = args[++i]; break;   // aot-codegen Phase C: scan whole netlist, tally what % AotEmitter can handle
                    case "--aot-verify-all":     if (i + 1 < args.Length) aotVerifyAll = args[++i]; break;   // aot-codegen Phase C: verify ALL emitter-supported nodes vs S1 per pattern
                    case "--aot-verify-block":   if (i + 2 < args.Length) { aotVerifyBlockRom = args[++i]; int.TryParse(args[++i], out aotVerifyBlockId); } break;   // aot-codegen Phase C-5: <ROM> <blockId>, verify one Partition.Block's AOT eval vs S1
                    case "--aot-emit-block":     if (i + 3 < args.Length) { aotEmitBlockRom = args[++i]; int.TryParse(args[++i], out aotEmitBlockId); aotEmitBlockPath = args[++i]; } break;   // aot-codegen Phase C-5 task #74: <ROM> <blockId> <outputPath>.cs
                    case "--aot-emit-all":       if (i + 2 < args.Length) { aotEmitAllRom = args[++i]; aotEmitAllPath = args[++i]; } break;   // aot-codegen Phase D-1: <ROM> <out.cs>, mass emit all blocks (>= 5 emittable)
                    case "--min-emittable":      if (i + 1 < args.Length) int.TryParse(args[++i], out aotMinEmittable); break;
                    case "--aot-compile-load":   if (i + 1 < args.Length) aotCompileLoad = args[++i]; break;   // aot-codegen Phase D-2: Roslyn-compile master, load delegate, verify vs S1
                    case "--aot-run":            if (i + 1 < args.Length) aotRun = args[++i]; break;   // aot-codegen Phase D-3: run sim with AOT delegate in the loop, compare hc/s + checksum to S1-only
                    case "--aot-skip":           if (i + 1 < args.Length) aotSkip = args[++i]; break;   // aot-codegen Phase D-4: AOT replaces S1 work (CodegenOwned + Option D BFS-block)
                    case "--aot-nopullup-scan":  if (i + 1 < args.Length) aotNoPullupScan = args[++i]; break;   // aot-codegen Phase E-1 prep: classify the 8,331 no-pullup nodes by topology
                    case "--aot-runtime-step":   if (i + 1 < args.Length) aotRuntimeStep = args[++i]; break;   // aot-codegen Phase E-2: AotRuntime first-step test vs S1
                    case "--alu-bench":       aluBench = true; if (i + 1 < args.Length && int.TryParse(args[i + 1], out var nv)) { aluBenchN = nv; i++; } break;
                    case "--selftest":        return SelfTest();
                    case "--system-def-dir":  if (i + 1 < args.Length) systemDefDir = args[++i]; break;
                    case "--no-lower":        WireCore.EnableLowering = false; break;   // A/B: skip the S1.5 lowering pass
                    case "--rcm":             WireCore.EnableRcm = true; break;          // math-algos G: Reverse Cuthill-McKee node-id reorder for cache locality
                    case "--simd-queue":      WireCore.EnableSimdQueue = true; break;    // math-algos Y: unroll-4 + MLP inner walk in AddNodeToGroup (wide-list nodes)
                    case "--oblivious":       WireCore.EnableOblivious = true; break;    // math-algos X: replace BFS dirty-set with full-sweep until fixpoint (Oblivious eval)
                    case "--prune-merge":     WireCore.EnablePruneMerge = true; break;   // math-algos #1: skip ON-case (merge) enqueue when endpoints already equal
                    case "--fast-path":       WireCore.EnableFastPath = true; break;     // math-algos 策略二: O(1) RecalcNode for pure-logic-gnd nodes (bypass group DFS)
                    case "--levelize":        WireCore.EnableLevelize = true; break;     // math-algos 策略三: soft levelized event-driven settle (gate-only level priority; fixpoint preserved)
                    case "--ir-interp":       WireCore.EnableIrInterp = true; break;     // Phase 2 P2.3: event-driven IR interpreter (Expr eval for extracted nodes, hybrid switch-level for the rest)
                    case "--codegen-dispatcher": WireCore.EnableCodegenDispatcher = true; break;  // Phase 2.5 Step 2: bitmask-polling macro-block dispatcher (dry-run; ALU eval but no output writeback)
                    case "--codegen-writeback":  WireCore.EnableCodegenDispatcher = true; WireCore.EnableCodegenAluWriteback = true; break;  // Step 2.5: writeback enabled (functional ≡ S1)
                    case "--codegen-own":        WireCore.EnableCodegenDispatcher = true; WireCore.EnableCodegenAluWriteback = true; WireCore.EnableCodegenAluOwnInternal = true; break;   // Step 3.5a: own + write whole 133-node ALU closure (transparent latch approximation)
                    case "--ir-pure":         WireCore.EnableIrInterp = true; WireCore.IrPureOnly = true; break;   // debug: interp with pure-logic-only IR (isolate dispatch)
                    case "--ir-brute":        WireCore.IrBruteForce = true; break;       // debug: oblivious re-eval each half-cycle (isolate triggering vs eval bug)
                    case "--count-events":    WireCore.CountEvents = true; break;        // diagnostic: count EnqueueNode + RecalcNode hits (measures D)
                    case "--bench-hc":        if (i + 1 < args.Length) int.TryParse(args[++i], out benchHcCount); break;   // bench raw N half-cycles (use when --frames is too coarse, e.g. for slow variants)
                    case "--max-wait":        if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--region":          if (i + 1 < args.Length) region       = args[++i].ToLowerInvariant(); break;
                    case "--benchmark":
                        benchmark = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) benchPath = args[++i];
                        break;
                    case "--help": case "-h": case "/?": PrintUsage(); return 0;
                    default:
                        // bare path → treat as --rom
                        if (romPath is null && testPath is null && testDir is null && dumpModule is null && tracePath is null && shotPath is null && ppuDumpPath is null && probePath is null && probeVblPath is null && dumpNodeName is null && !dumpSystem && !args[i].StartsWith('-'))
                            romPath = args[i];
                        break;
                }
            }

            WireCore.SystemDefDir = systemDefDir;

            if (dumpModule != null) return DumpModule(systemDefDir, dumpModule);
            if (dumpSystem) return DumpSystem();
            if (dumpLevels) return DumpLevels();
            if (verifyIrPath != null) return VerifyIr(verifyIrPath, benchHcCount);
            if (dumpStatesPath != null) return DumpStates(dumpStatesPath, benchHcCount);
            if (dumpBlockOutputs != null) return DumpBlock(dumpBlockOutputs, dumpBlockStops);
            if (dumpPartition) return DumpPartition();
            if (dumpBlockId >= 0) return DumpBlockId(dumpBlockId);
            if (aotVerifyTileMux != null) return AprVisual.Codegen.AotVerifier.VerifyTileHBitMux(aotVerifyTileMux, benchHcCount > 0 ? benchHcCount : 100_000);
            if (aotVerifyIrInv   != null) return AprVisual.Codegen.AotVerifier.VerifyIrInverter (aotVerifyIrInv,   benchHcCount > 0 ? benchHcCount : 100_000);
            if (aotEmitVerifyIr  != null) return AprVisual.Codegen.AotVerifier.VerifyEmitterOnIrInverter(aotEmitVerifyIr, benchHcCount > 0 ? benchHcCount : 100_000);
            if (aotCoverage      != null) return AprVisual.Codegen.AotVerifier.RunCoverageScan(aotCoverage);
            if (aotVerifyAll     != null) return AprVisual.Codegen.AotVerifier.VerifyAllEmittable(aotVerifyAll, benchHcCount > 0 ? benchHcCount : 50_000);
            if (aotVerifyBlockRom != null && aotVerifyBlockId >= 0) return AprVisual.Codegen.AotVerifier.VerifyBlock(aotVerifyBlockRom, aotVerifyBlockId, benchHcCount > 0 ? benchHcCount : 50_000);
            if (aotEmitBlockRom != null && aotEmitBlockId >= 0 && aotEmitBlockPath != null) return AprVisual.Codegen.AotVerifier.EmitBlockSource(aotEmitBlockRom, aotEmitBlockId, aotEmitBlockPath);
            if (aotEmitAllRom != null && aotEmitAllPath != null) return AprVisual.Codegen.AotVerifier.EmitAllBlocks(aotEmitAllRom, aotEmitAllPath, aotMinEmittable);
            if (aotCompileLoad != null) return AprVisual.Codegen.AotVerifier.CompileAndLoadAll(aotCompileLoad, benchHcCount > 0 ? benchHcCount : 30_000, aotMinEmittable);
            if (aotRun != null) return AprVisual.Codegen.AotVerifier.RunWithAotEngine(aotRun, benchHcCount > 0 ? benchHcCount : 30_000, aotMinEmittable);
            if (aotSkip != null) return AprVisual.Codegen.AotVerifier.RunWithAotSkippingS1(aotSkip, benchHcCount > 0 ? benchHcCount : 30_000, aotMinEmittable);
            if (aotNoPullupScan != null) return AprVisual.Codegen.AotVerifier.RunNoPullupInventory(aotNoPullupScan);
            if (aotRuntimeStep != null) return AprVisual.Codegen.AotVerifier.RunAotRuntimeStep1Test(aotRuntimeStep, benchHcCount > 0 ? benchHcCount : 1, aotMinEmittable);
            if (aluBench) return AluBench(aluBenchN);
            if (tracePath != null) return Trace(tracePath, traceCycles);
            if (shotPath != null) return Screenshot(shotPath, shotFrames, shotOut);
            if (ppuDumpPath != null) return PpuDump(ppuDumpPath, shotFrames);
            if (probePath != null) return Probe2002(probePath);
            if (probeVblPath != null) return ProbeVbl(probeVblPath);
            if (dumpNodeName != null) return DumpNode(dumpNodeName);
            if (benchPath != null && benchHcCount > 0) return BenchmarkHalfCycles(benchPath, benchHcCount);
            if (benchPath != null) return Benchmark(benchPath, shotFrames);

            if (romPath != null)
            {
                // GUI path: show the window for this ROM.
                ApplicationConfiguration.Initialize();   // source-generated, global namespace
                System.Windows.Forms.Application.Run(new MainForm(romPath));
                return 0;
            }

            if (testDir != null)
            {
                if (!Directory.Exists(testDir)) { Console.Error.WriteLine($"no such dir: {testDir}"); return 2; }
                int fail = 0, total = 0;
                foreach (string f in Directory.EnumerateFiles(testDir, "*.nes", SearchOption.AllDirectories))
                {
                    total++;
                    if (RunOneTest(f, maxWait, region, benchmark) != 0) fail++;
                }
                Console.WriteLine($"\n{total - fail}/{total} passed");
                return fail == 0 ? 0 : 1;
            }

            if (testPath != null)
                return RunOneTest(testPath, maxWait, region, benchmark);

            PrintUsage();
            return 0;
        }

        // ── Step 1 acceptance harness: parse a .js module def and print a summary so we can
        //    eyeball that counts / sub-modules / external files came through correctly. ──
        private static int DumpModule(string dir, string name)
        {
            WireCore.ModuleDef def;
            try { def = WireCore.LoadModuleDef(dir, name); }
            catch (Exception ex) { Console.Error.WriteLine($"parse failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            int segPlus = 0, segMinus = 0, segNone = 0;
            foreach (var s in def.Segs) { if (s.Pull == '+') segPlus++; else if (s.Pull == '-') segMinus++; else segNone++; }
            int weak = 0; foreach (var t in def.Trans) if (t.IsWeak) weak++;

            Console.WriteLine($"module: {def.Name}  ({def.Description})");
            Console.WriteLine($"  file:          {def.Path}");
            Console.WriteLine($"  named nodes:   {def.NodeNames.Count}");
            Console.WriteLine($"  segdefs:       {def.Segs.Count}   (+: {segPlus}, -: {segMinus}, none: {segNone})");
            Console.WriteLine($"  transdefs:     {def.Trans.Count}   (weak: {weak})");
            Console.WriteLine($"  connections:   {def.Connections.Count}");
            Console.WriteLine($"  pins:          {def.Pins.Count}");
            Console.WriteLine($"  pullups:       {def.Pullups.Count}   forceCompute: {def.ForceCompute.Count}");
            if (def.Memories.Count > 0)
                Console.WriteLine($"  memory:        {string.Join(", ", System.Linq.Enumerable.Select(def.Memories, kv => $"{kv.Key}({kv.Value})"))}");
            if (def.NodeNameFiles.Count + def.TransDefFiles.Count + def.SegDefFiles.Count > 0)
                Console.WriteLine($"  external:      nodenames={string.Join(",", def.NodeNameFiles)} transdefs={string.Join(",", def.TransDefFiles)} segdefs={string.Join(",", def.SegDefFiles)}");
            Console.WriteLine($"  sub-modules:   {def.SubModules.Count}");
            foreach (var sm in def.SubModules) Console.WriteLine($"    {sm.Prefix,-12} -> {sm.Type}");

            if (WireCore.LoadedDefs.Count > 1)
            {
                Console.WriteLine($"\n  all defs loaded ({WireCore.LoadedDefs.Count}):");
                foreach (var kv in WireCore.LoadedDefs)
                    Console.WriteLine($"    {kv.Key,-16} nodes={kv.Value.NodeNames.Count,5}  trans={kv.Value.Trans.Count,6}  segs={kv.Value.Segs.Count,6}  conns={kv.Value.Connections.Count,4}");
            }

            // A few sample named nodes (sanity: the internal-register names should be present for 2a03).
            var sample = new System.Collections.Generic.List<string>();
            foreach (var probe in new[] { "vcc", "vss", "clk0", "res", "rw", "a0", "x0", "y0", "pcl0", "ab0", "db0", "func<ram>" })
                if (def.NodeNames.ContainsKey(probe)) sample.Add(probe);
            if (sample.Count > 0) Console.WriteLine($"\n  sample nodes present: {string.Join(", ", sample)}");
            return 0;
        }

        // ── Step 2 acceptance harness: compose the full nes-001 + cart netlist and print
        //    node/transistor/memory counts + a few LookupNode / ResolveNodes probes. ──
        private static int DumpSystem()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 2; }

            Console.WriteLine($"global node array:  {WireCore.NodeArrayCount}");
            Console.WriteLine($"  non-null nodes:   {WireCore.NonNullNodeCount}");
            Console.WriteLine($"  with pull-up:     {WireCore.PullUpNodeCount}");
            Console.WriteLine($"transistors:        {WireCore.TransistorBuildCount}  (incl. {WireCore.ConnectionTransistorCount} connection-transistors)");
            Console.WriteLine($"forceCompute nodes: {WireCore.ForceComputeList.Count}");
            Console.WriteLine($"memories:           {string.Join(", ", WireCore.MemoryNames)}");
            Console.WriteLine($"S1.5 {WireCore.LastLowerStats}");

            Console.WriteLine("\nlookups:");
            foreach (var p in new[] { "clk", "res", "vcc", "vss", "cpu.clk0", "cpu.a0", "cpu.ir0", "cpu.ab0", "cpu.db0",
                                      "ppu.clk0", "ppu.io_ce", "u1.cs", "u3.1/Y1", "cart.edge.cpu_a0", "cart.prg.a0", "port0.out" })
                Console.WriteLine($"  {p,-22} = {WireCore.LookupNode(p)}");

            Console.WriteLine("\nresolveNodes:");
            void Probe(string e) { var l = new System.Collections.Generic.List<int>(); WireCore.ResolveNodes(e, l); Console.WriteLine($"  {e,-22} -> {l.Count,4} nodes  [{string.Join(",", System.Linq.Enumerable.Take(l, 8))}{(l.Count > 8 ? ",…" : "")}]"); }
            Probe("cpu.ab[15:0]");
            Probe("cpu.db[7:0]");
            Probe("cpu.a[7:0]");
            Probe("cart.edge.cpu_a[14:0]");
            Probe("*.vss");   // should resolve entirely to Ngnd (=2)
            Probe("*.vcc");   // should resolve entirely to Npwr (=1)

            // ── Step 3: power on (allocate hot arrays, build LUT + flattened transistor lists) ──
            try { WireCore.Reset(); }
            catch (Exception ex) { Console.Error.WriteLine($"Reset() failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 2; }

            int stateHigh = 0;
            for (int i = 0; i < WireCore.NodeCount; i++) unsafe { if (WireCore.NodeStates[i] != 0) stateHigh++; }
            // spot-check: a known node's flattened c1c2 sub-list vs its build-time C1c2s
            int probeNode = WireCore.LookupNode("cpu.clk0");
            int buildC1c2 = probeNode > 0 && probeNode < WireCore.Nodes.Count && WireCore.Nodes[probeNode] != null ? WireCore.Nodes[probeNode]!.C1c2s.Count : -1;

            Console.WriteLine("\nReset():");
            Console.WriteLine($"  NodeCount:          {WireCore.NodeCount}");
            Console.WriteLine($"  TransistorList len: {WireCore.TransistorListLength}");
            Console.WriteLine($"  nodes at state 1:   {stateHigh}  (== {WireCore.PullUpNodeCount} pull-ups + 1 for vcc)");
            Console.WriteLine($"  forceCompute flags: {WireCore.ForceComputeList.Count}");
            Console.WriteLine($"  cpu.clk0 (#{probeNode}) build-time c1c2s count = {buildC1c2}");
            return 0;
        }

        // ── Phase 1 (CPU/event-driven, pre-IR): compose the full netlist, classify pure-logic (策略二),
        //    then report the SCC / topological-level structure — to decide whether levelized scheduling
        //    will pay (mostly small-SCC DAG) or not (one giant pass-transistor SCC). No state change. ──
        // ── Phase 2 P2.2 (it.1): extract Expr for the proven pure-logic subset and verify, at every
        //    settled half-cycle, that EvalExpr == NodeStates. 0 mismatches => the Expr pool + evaluator
        //    reproduce S1 for that subset (infrastructure validated before tackling COMB_PASS). ──
        private static int VerifyIr(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 5000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            WireCore.EnableFastPath = true;   // populate IsPureLogic (the proven-correct subset)
            Console.WriteLine($"# verify-ir: {Path.GetFileName(romPath)} — extract pure-logic Expr, check Expr==NodeStates over {hcCount:N0} half-cycles");
            try
            {
                WireCore.LoadSystem(rom);
                WireCore.BuildCombinationalIr();
                Console.WriteLine($"# {WireCore.LastIrStats}");
                int[] badPerNode = new int[WireCore.NodeCount];
                long totalChecks = 0, totalMism = 0; int badHc = 0; long firstBadT = -1;
                for (int i = 0; i < hcCount; i++)
                {
                    WireCore.Step(1);
                    var (c, m) = WireCore.VerifyIrOnce(badPerNode);
                    totalChecks += c; totalMism += m;
                    if (m > 0) { badHc++; if (firstBadT < 0) firstBadT = WireCore.Time; }
                }
                int distinctBad = 0; var sample = new List<string>();
                for (int nn = 0; nn < WireCore.NodeCount; nn++)
                    if (badPerNode[nn] > 0) { distinctBad++; if (sample.Count < 6) sample.Add($"{WireCore.GetNodeName(nn)}#{nn}"); }
                Console.WriteLine($"# checks: {totalChecks:N0}   mismatches: {totalMism:N0}   half-cycles with a mismatch: {badHc:N0}{(firstBadT >= 0 ? $" (first at t={firstBadT})" : "")}");
                Console.WriteLine($"# distinct nodes ever mismatching: {distinctBad:N0} / {WireCore.IrExtractedCount:N0} extracted  ({(WireCore.IrExtractedCount > 0 ? 100.0 * (WireCore.IrExtractedCount - distinctBad) / WireCore.IrExtractedCount : 0):F2}% clean){(sample.Count > 0 ? "  e.g. " + string.Join(", ", sample) : "")}");
                Console.WriteLine(totalMism == 0
                    ? "# VERDICT: PASS — extracted Expr reproduces S1 for ALL extracted combinational nodes"
                    : $"# VERDICT: {distinctBad} node(s) need hybrid (stack model wrong there); the rest match");
                return totalMism == 0 ? 0 : 1;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── Phase 2.5 codegen #46: P/Invoke benchmark of the hand-coded ALU in AluBlock.dll.
        //    Step 2.5: 5-op AluCtx (SUMS/ANDS/ORS/EORS/SRS — the real 6502 PLA selectors); shared
        //    bindings now in AprVisual.Native.AluBlockBindings. ──
        private static int AluBench(int n)
        {
            if (n < 1000) n = 1000;
            Console.WriteLine($"# AluBlock.dll native bench (n = {n:N0} calls)");

            // Generate random vectors covering all 5 op codes (SUMS/ANDS/ORS/EORS/SRS).
            var rng = new Random(12345);
            var ctxs = new AluBlockBindings.AluCtx[n];
            var op = new byte[n];
            for (int i = 0; i < n; i++)
            {
                op[i] = (byte)rng.Next(5);
                ctxs[i] = new AluBlockBindings.AluCtx
                {
                    alua    = (byte)rng.Next(256),
                    alub    = (byte)rng.Next(256),
                    cin     = (byte)rng.Next(2),
                    op_sums = (byte)(op[i] == 0 ? 1 : 0),
                    op_ands = (byte)(op[i] == 1 ? 1 : 0),
                    op_ors  = (byte)(op[i] == 2 ? 1 : 0),
                    op_eors = (byte)(op[i] == 3 ? 1 : 0),
                    op_srs  = (byte)(op[i] == 4 ? 1 : 0),
                };
            }

            // Warmup (JIT + I-cache prime)
            unsafe { fixed (AluBlockBindings.AluCtx* p = ctxs) { AluBlockBindings.Eval_AluN(p, Math.Min(n, 10_000)); } }

            // Bulk bench — single P/Invoke crossing, N evaluations inside native
            var swBulk = System.Diagnostics.Stopwatch.StartNew();
            unsafe { fixed (AluBlockBindings.AluCtx* p = ctxs) { AluBlockBindings.Eval_AluN(p, n); } }
            swBulk.Stop();
            double bulkNs = swBulk.Elapsed.TotalNanoseconds / n;

            // Per-call bench — one P/Invoke crossing per evaluation
            var swPer = System.Diagnostics.Stopwatch.StartNew();
            unsafe { fixed (AluBlockBindings.AluCtx* p = ctxs) { for (int i = 0; i < n; i++) AluBlockBindings.Eval_Alu(p + i); } }
            swPer.Stop();
            double perNs = swPer.Elapsed.TotalNanoseconds / n;

            // Correctness on a sample (compare against hand-computed for all 5 ops)
            int checks = Math.Min(n, 2000);
            int errs = 0;
            for (int i = 0; i < checks; i++)
            {
                var c = ctxs[i];
                byte expected = op[i] switch
                {
                    0 => (byte)((c.alua + c.alub + c.cin) & 0xFF),                  // SUMS
                    1 => (byte)(c.alua & c.alub),                                   // ANDS
                    2 => (byte)(c.alua | c.alub),                                   // ORS
                    3 => (byte)(c.alua ^ c.alub),                                   // EORS
                    4 => (byte)((c.alua >> 1) | (c.cin << 7)),                      // SRS
                    _ => (byte)0,
                };
                byte expectedCout = op[i] switch
                {
                    0 => (byte)(((c.alua + c.alub + c.cin) >> 8) & 1),              // SUMS carry-out
                    4 => (byte)(c.alua & 1),                                        // SRS carry-out (LSB)
                    _ => (byte)0,
                };
                if (c.alu != expected || c.cout != expectedCout) errs++;
            }

            // S1 baseline: from the latest tight measurement, baseline ~41.8 k hc/s with D ≈ 610
            // recalc/hc → ~39.2 ns/recalc on this machine. The ALU's internal closure is 133 nodes
            // and 477 transistors (--dump-block); a hypothetical fraction of D spent on those nodes
            // gives the codegen savings ceiling.
            const double s1NsPerRecalc = 39.2;

            Console.WriteLine($"# bulk Eval_AluN  : {swBulk.Elapsed.TotalMilliseconds:F2} ms total, {bulkNs:F2} ns/call ({1e9/bulkNs:N0} ops/sec)");
            Console.WriteLine($"# per-call Eval_Alu: {swPer.Elapsed.TotalMilliseconds:F2} ms total, {perNs:F2} ns/call ({1e9/perNs:N0} ops/sec)");
            Console.WriteLine($"# (P/Invoke crossing overhead per call = ~{(perNs - bulkNs):F2} ns)");
            Console.WriteLine($"# correctness     : {checks - errs}/{checks} match (alu + cout against hand-computed)");
            Console.WriteLine($"# ");
            Console.WriteLine($"# S1 baseline reference: ~{s1NsPerRecalc:F1} ns / recalc (from latest tight measurement)");
            Console.WriteLine($"# native bulk speedup : {s1NsPerRecalc/bulkNs:F1}x  (vs S1 recalc cost — bulk path, what a codegen'd block would approach)");
            Console.WriteLine($"# native per-call    : {s1NsPerRecalc/perNs:F1}x   (vs S1 recalc cost — per-call path, with P/Invoke overhead)");
            Console.WriteLine($"# ");
            if (bulkNs < 5)        Console.WriteLine($"# verdict: native ALU is FAST ENOUGH — codegen path is worth pursuing (per Gemini >3x threshold)");
            else if (bulkNs < 20)  Console.WriteLine($"# verdict: native ALU is OK but the per-block speedup is moderate — pursue carefully");
            else                   Console.WriteLine($"# verdict: native ALU is slow ({bulkNs:F1} ns/call); something's off with the compile or measurement");
            return errs == 0 ? 0 : 1;
        }

        // ── Phase 2.5 codegen prep: reverse-closure a "macro-block" from output node names. Walks the
        //    netlist BACKWARDS along channel edges (per transistor: gate + the-other-endpoint) until it
        //    hits declared --block-stop inputs (or supply Npwr/Ngnd). Reports outputs / touched inputs /
        //    declared-but-untouched / internal nodes / transistors involved. This is the boundary the
        //    P/Invoke ALU experiment + future macro-block extractor will use. (Gemini r1 §5.2)
        //
        //    Example: dotnet run -- --dump-block "alu[7:0] notalu[7:0] alucout notalucout"
        //                          --block-stop "alua[7:0] alub[7:0] alucin op-SUMS"
        //                          --system-def-dir <defs>
        private static int DumpBlock(string outputExpr, string? stopExpr)
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.Message}"); return 2; }

            HashSet<int> ResolveSet(string expr)
            {
                var set = new HashSet<int>();
                if (string.IsNullOrWhiteSpace(expr)) return set;
                foreach (var part in expr.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var list = new List<int>();
                    WireCore.ResolveNodes(part, list, quiet: true);
                    foreach (int n in list) if (n != WireCore.EmptyNode) set.Add(n);
                }
                return set;
            }

            var outputs = ResolveSet(outputExpr);
            var stops = ResolveSet(stopExpr ?? "");
            stops.Add(WireCore.Npwr); stops.Add(WireCore.Ngnd);
            if (outputs.Count == 0) { Console.Error.WriteLine("no valid output nodes resolved"); return 1; }

            // Reverse BFS closure. For each node v, walk every transistor where v is a channel endpoint
            // (TlistC1c2s + TlistC1gnd + TlistC1pwr — all in build-time node.C1c2s). Both the transistor's
            // gate AND the other channel endpoint may affect v, so both are candidates. Stops:
            //   (1) Supply (Npwr/Ngnd).
            //   (2) Declared --block-stop inputs.
            //   (3) ANY OTHER pull-up node (Node.Pullups > 0) — those are gate outputs of OTHER blocks,
            //       so naturally a block boundary. Without this, the data bus + shared bus structures
            //       drag in the entire chip (e.g. ALU output -> sb -> ACC -> all other drivers ...).
            //       Pull-up boundary nodes are auto-collected into `discoveredBoundary` for the report
            //       — user can move any into --block-stop explicitly if they care, but typically the
            //       auto-set IS the right block boundary.
            var closure = new HashSet<int>(outputs);
            var queue = new Queue<int>(outputs);
            var touchedStops = new HashSet<int>();
            var discoveredBoundary = new HashSet<int>();   // pull-up nodes hit but not declared
            var transistorsInvolved = new HashSet<int>();

            while (queue.Count > 0)
            {
                int v = queue.Dequeue();
                if ((uint)v >= (uint)WireCore.Nodes.Count) continue;
                var node = WireCore.Nodes[v];
                if (node == null) continue;
                foreach (int tid in node.C1c2s)
                {
                    transistorsInvolved.Add(tid);
                    var t = WireCore.Transistors[tid];
                    int gate = t.Gate;
                    int other = (t.C1 == v) ? t.C2 : t.C1;

                    void Consider(int cand)
                    {
                        if (cand == v || cand == WireCore.Npwr || cand == WireCore.Ngnd) return;
                        if (stops.Contains(cand)) { touchedStops.Add(cand); return; }
                        if (!outputs.Contains(cand))
                        {
                            var cnode = (uint)cand < (uint)WireCore.Nodes.Count ? WireCore.Nodes[cand] : null;
                            if (cnode != null && cnode.Pullups > 0)
                            {
                                discoveredBoundary.Add(cand);     // hit another gate output -> block boundary
                                return;
                            }
                        }
                        if (closure.Add(cand)) queue.Enqueue(cand);
                    }
                    Consider(gate);
                    Consider(other);
                }
            }

            var internalNodes = new HashSet<int>(closure); internalNodes.ExceptWith(outputs);
            var declaredInputs = new HashSet<int>(stops); declaredInputs.Remove(WireCore.Npwr); declaredInputs.Remove(WireCore.Ngnd);
            var untouched = new HashSet<int>(declaredInputs); untouched.ExceptWith(touchedStops);

            Console.WriteLine("# block dump (reverse-closure from outputs along channel edges):");
            Console.WriteLine($"#   outputs (declared):           {outputs.Count}");
            Console.WriteLine($"#   inputs (declared as --block-stop): {declaredInputs.Count}  (+ Npwr/Ngnd auto)");
            Console.WriteLine($"#   inputs actually touched:      {touchedStops.Count}");
            Console.WriteLine($"#   inputs declared but UNTOUCHED:{untouched.Count}  (declared but never reached → over-spec)");
            Console.WriteLine($"#   auto-discovered pull-up bdry: {discoveredBoundary.Count}  (other gate outputs hit → naturally another block's output; treated as inputs)");
            Console.WriteLine($"#   internal nodes:               {internalNodes.Count}");
            Console.WriteLine($"#   total closure:                {closure.Count}");
            Console.WriteLine($"#   transistors involved:         {transistorsInvolved.Count}");

            Console.WriteLine("# ");
            Console.WriteLine("# === outputs (resolved) ===");
            foreach (int v in outputs.OrderBy(x => x)) Console.WriteLine($"  {v,6}  {WireCore.GetNodeName(v)}");

            Console.WriteLine("# ");
            Console.WriteLine("# === inputs touched (good — these are the real block boundary) ===");
            foreach (int v in touchedStops.OrderBy(x => x)) Console.WriteLine($"  {v,6}  {WireCore.GetNodeName(v)}");

            if (untouched.Count > 0)
            {
                Console.WriteLine("# ");
                Console.WriteLine("# === inputs declared but NEVER REACHED (consider removing) ===");
                foreach (int v in untouched.OrderBy(x => x)) Console.WriteLine($"  {v,6}  {WireCore.GetNodeName(v)}");
            }

            if (discoveredBoundary.Count > 0)
            {
                int namedCount = discoveredBoundary.Count(v => !string.IsNullOrEmpty(WireCore.GetNodeName(v)) && WireCore.GetNodeName(v) != v.ToString());
                int anonCount = discoveredBoundary.Count - namedCount;
                Console.WriteLine("# ");
                Console.WriteLine($"# === auto-discovered pull-up boundary ({discoveredBoundary.Count}: {namedCount} named, {anonCount} anonymous) ===");
                Console.WriteLine("# (other gate outputs the block reaches via pass — the real block inputs)");
                Console.WriteLine("# (showing the NAMED ones first; anonymous = likely PLA / internal mid pull-ups, less directly meaningful)");
                foreach (int v in discoveredBoundary
                    .Where(v => { string n = WireCore.GetNodeName(v); return !string.IsNullOrEmpty(n) && n != v.ToString(); })
                    .OrderBy(v => WireCore.GetNodeName(v)))
                {
                    Console.WriteLine($"  {v,6}  {WireCore.GetNodeName(v)}");
                }
            }

            // Name-prefix histogram: helps spot what subsystems got dragged in (eg "ir*", "pla*", "pcl*",
            // "y*" register etc.) so we know what to add to --block-stop.
            var prefixHist = new Dictionary<string, int>();
            foreach (int v in internalNodes)
            {
                string name = WireCore.GetNodeName(v);
                if (string.IsNullOrEmpty(name)) name = "(anon)";
                // Extract the alphabetic prefix before any digit or non-letter
                int k = 0;
                while (k < name.Length && (char.IsLetter(name[k]) || name[k] == '.' || name[k] == '/' || name[k] == '_')) k++;
                string pfx = k > 0 ? name.Substring(0, k) : name;
                if (pfx.Length > 24) pfx = pfx.Substring(0, 24);
                prefixHist.TryGetValue(pfx, out int c);
                prefixHist[pfx] = c + 1;
            }
            Console.WriteLine("# ");
            Console.WriteLine($"# === internal-node name-prefix histogram (top 30) — what subsystems got pulled in ===");
            foreach (var kv in prefixHist.OrderByDescending(p => p.Value).Take(30))
                Console.WriteLine($"  {kv.Value,5}  {kv.Key}*");

            Console.WriteLine("# ");
            Console.WriteLine($"# === internal nodes (first 60 by name) ===");
            int shown = 0;
            foreach (int v in internalNodes.OrderBy(x => WireCore.GetNodeName(x)))
            {
                Console.WriteLine($"  {v,6}  {WireCore.GetNodeName(v)}");
                if (++shown >= 60) break;
            }
            if (internalNodes.Count > 60) Console.WriteLine($"  ... ({internalNodes.Count - 60} more)");

            // Sanity heuristic: closure size suggests how clean the boundary is.
            if (closure.Count > 500)
                Console.WriteLine($"# WARNING: closure size {closure.Count} is large — likely under-specified --block-stop (closure ate into other subsystems).");
            else if (closure.Count < 50)
                Console.WriteLine($"# NOTE: closure size {closure.Count} is small — verify it actually covers the intended block.");

            return 0;
        }

        // ── Phase 2.5 Step 3: auto-partition the netlist into macro-blocks and print a histogram +
        //    per-block summary. Boundaries are pull-up nodes (latched/named) + Npwr/Ngnd; each
        //    non-boundary node belongs to exactly one block. Used to validate the partitioner +
        //    drive subsequent Step 3.5 (wire each block into the dispatcher as a CodegenOwned region)
        //    and Step 4 (LLVM-emit per block). ──
        private static int DumpPartition()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.Message}"); return 2; }
            try { WireCore.Reset(); WireCore.RecomputeAllNodes(); }
            catch (Exception ex) { Console.Error.WriteLine($"reset failed: {ex.Message}"); return 2; }

            var blocks = WireCore.AutoPartition();
            var (count, unassigned, totalInt, bound) = WireCore.SummarisePartition(blocks);

            Console.WriteLine($"# auto-partition: netlist split into {count:N0} macro-blocks");
            Console.WriteLine($"#   boundary nodes (pull-up + supply): {bound:N0}");
            Console.WriteLine($"#   total internal nodes assigned:     {totalInt:N0}");
            Console.WriteLine($"#   unassigned internal (should be 0): {unassigned:N0}");
            Console.WriteLine($"#   total nodes (for sanity):          {WireCore.NodeCount:N0}");
            Console.WriteLine("#");

            // ── histogram by internal-node-count buckets ──
            var buckets = new (int lo, int hi, string label)[] {
                (1,    1,    "1            (singletons)"),
                (2,    4,    "2..4         (tiny)"),
                (5,    16,   "5..16        (small)"),
                (17,   64,   "17..64       (medium)"),
                (65,   256,  "65..256      (large — codegen candidates)"),
                (257,  1024, "257..1024    (very large — may need sub-cutting)"),
                (1025, int.MaxValue, ">=1025      (huge — likely under-cut)"),
            };
            var histo = new int[buckets.Length];
            foreach (var b in blocks)
            {
                int sz = b.InternalNodes.Length;
                for (int i = 0; i < buckets.Length; i++) if (sz >= buckets[i].lo && sz <= buckets[i].hi) { histo[i]++; break; }
            }
            Console.WriteLine("# size histogram (block count by internal-node bucket):");
            for (int i = 0; i < buckets.Length; i++) Console.WriteLine($"#   {buckets[i].label,-44} : {histo[i],6:N0}");
            Console.WriteLine("#");

            // ── top-N largest blocks ──
            int topN = Math.Min(30, blocks.Count);
            Console.WriteLine($"# top-{topN} largest blocks:");
            Console.WriteLine($"#   {"id",4}  {"intern",6}  {"transtor",8}  {"inputs",6}  {"driven",6}  {"label"}");
            for (int i = 0; i < topN; i++)
            {
                var b = blocks[i];
                Console.WriteLine($"#   {b.Id,4}  {b.InternalNodes.Length,6}  {b.TransistorIds.Length,8}  {b.BoundaryInputs.Length,6}  {b.DrivenOutputs.Length,6}  {b.Label}");
            }
            Console.WriteLine("#");
            // ── named-driven-outputs roll-up: show the top blocks where the label points at a known
            // CPU/PPU/APU subsystem, ranked by internal-node size ──
            Console.WriteLine($"# top-15 codegen-candidate blocks (size 17+ and non-supply label):");
            int shown = 0;
            foreach (var b in blocks)
            {
                if (b.InternalNodes.Length < 17) break;
                if (b.Label.StartsWith("block-")) continue;
                if (b.Label is "vcc" or "vss" or "clk" or "clk0") continue;
                Console.WriteLine($"#   {b.Id,4}  {b.InternalNodes.Length,6}  {b.TransistorIds.Length,8}  {b.BoundaryInputs.Length,6}  {b.DrivenOutputs.Length,6}  {b.Label}");
                if (++shown >= 15) break;
            }
            Console.WriteLine("#");

            // ── ALU block locator: find which block contains the alu0 node ──
            int alu0 = WireCore.LookupNode("cpu.alu0");
            if (alu0 != WireCore.EmptyNode)
            {
                int aluBlockIdx = -1;
                for (int i = 0; i < blocks.Count; i++) if (Array.IndexOf(blocks[i].InternalNodes, alu0) >= 0) { aluBlockIdx = i; break; }
                if (aluBlockIdx >= 0)
                {
                    var ab = blocks[aluBlockIdx];
                    Console.WriteLine($"# ALU LOCATOR (cpu.alu0 = node {alu0}):");
                    Console.WriteLine($"#   in block #{ab.Id}  ({ab.Label})");
                    Console.WriteLine($"#   internal {ab.InternalNodes.Length}, transistors {ab.TransistorIds.Length}, inputs {ab.BoundaryInputs.Length}, driven {ab.DrivenOutputs.Length}");
                    Console.WriteLine($"#   (compare: --dump-block ALU showed 133 internal + 477 transistors)");
                }
                else Console.WriteLine($"# ALU LOCATOR: cpu.alu0 (node {alu0}) is itself a boundary — has pull-up");
            }

            return 0;
        }

        // ── Phase 2.5 Step 3: print the details of one auto-partition block (internal node names,
        //    input/output boundary names, transistor count). Used together with --dump-partition to
        //    drill into a candidate macro-block before wiring it into the dispatcher.
        private static int DumpBlockId(int blockId)
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.Message}"); return 2; }
            try { WireCore.Reset(); WireCore.RecomputeAllNodes(); }
            catch (Exception ex) { Console.Error.WriteLine($"reset failed: {ex.Message}"); return 2; }

            var blocks = WireCore.AutoPartition();
            if (blockId < 0 || blockId >= blocks.Count) { Console.Error.WriteLine($"block id {blockId} out of range (0..{blocks.Count - 1})"); return 1; }
            var b = blocks[blockId];

            Console.WriteLine($"# block #{b.Id} — {b.Label}");
            Console.WriteLine($"#   internal nodes:    {b.InternalNodes.Length}");
            Console.WriteLine($"#   transistors:       {b.TransistorIds.Length}");
            Console.WriteLine($"#   boundary inputs:   {b.BoundaryInputs.Length}");
            Console.WriteLine($"#   driven outputs:    {b.DrivenOutputs.Length}");

            string NameOf(int nn)
            {
                if ((uint)nn >= (uint)WireCore.Nodes.Count) return "(?)";
                var n = WireCore.Nodes[nn]; return n == null ? "(null)" : (string.IsNullOrEmpty(n.Name) ? "(anonymous)" : n.Name);
            }

            Console.WriteLine("# === driven outputs ===");
            foreach (int v in b.DrivenOutputs.OrderBy(x => x)) Console.WriteLine($"  {v,6}  {NameOf(v)}");
            Console.WriteLine("# === boundary inputs (top 40 by name) ===");
            int shown = 0;
            foreach (int v in b.BoundaryInputs.OrderBy(NameOf))
            {
                Console.WriteLine($"  {v,6}  {NameOf(v)}");
                if (++shown >= 40) break;
            }
            if (b.BoundaryInputs.Length > 40) Console.WriteLine($"  ... ({b.BoundaryInputs.Length - 40} more)");

            Console.WriteLine("# === internal nodes (first 40 by name) ===");
            shown = 0;
            foreach (int v in b.InternalNodes.OrderBy(NameOf))
            {
                Console.WriteLine($"  {v,6}  {NameOf(v)}");
                if (++shown >= 40) break;
            }
            if (b.InternalNodes.Length > 40) Console.WriteLine($"  ... ({b.InternalNodes.Length - 40} more)");
            return 0;
        }

        // ── Phase 2 debug: step N half-cycles, print every live HIGH node's id (sorted). Diff the S1
        //    dump vs the --ir-interp dump (Compare-Object) to find the first node that diverges. ──
        private static int DumpStates(string romPath, int n)
        {
            if (n < 1) n = 48;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                if (WireCore.EnableIrInterp) { WireCore.BuildCombinationalIr(); WireCore.BuildRevDep(); }
                WireCore.Step(n);
                Console.WriteLine($"# dump-states t={WireCore.Time} ir-interp={WireCore.EnableIrInterp} pure-only={WireCore.IrPureOnly}");
                unsafe
                {
                    for (int nn = 0; nn < WireCore.NodeCount; nn++)
                        if (WireCore.Nodes[nn] != null && WireCore.NodeStates[nn] != 0)
                            Console.WriteLine($"{nn} {WireCore.IrClassOf(nn)} {WireCore.GetNodeName(nn)}");
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static int DumpLevels()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 2; }
            WireCore.EnableFastPath = true;   // populate IsPureLogic so AnalyzeLevels can cross-check the levelable set
            try { WireCore.Reset(); }
            catch (Exception ex) { Console.Error.WriteLine($"Reset() failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 2; }
            Console.WriteLine($"# {WireCore.LastLowerStats}");
            Console.WriteLine($"# {WireCore.LastFastPathStats}");
            WireCore.AnalyzeLevels();
            return 0;
        }

        // ── Step 4+5 acceptance harness: hand-built tiny netlists (inverter / NAND / pass transistor /
        //    dynamic hold) driven via SetHigh/SetLow, checked against their truth tables.
        //    (cf. MD/struct/04 §13.3 and ref/metalnes-main chip_tests.cpp's pslatch/4021.) ──
        private static int SelfTest()
        {
            int fails = 0;
            fails += TestInverter();
            fails += TestNand();
            fails += TestPassTransistor();
            fails += TestCallback();
            fails += TestStaticMerge();
            Console.WriteLine(fails == 0 ? "\nselftest: ALL PASS" : $"\nselftest: {fails} FAILURE(S)");
            return fails == 0 ? 0 : 1;
        }

        private static int Check(string what, bool ok)
        {
            Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
            return ok ? 0 : 1;
        }

        // NMOS inverter: gate 'a' pulls output 'y' down to vss; 'y' has a pull-up. y == !a.
        private static int TestInverter()
        {
            Console.WriteLine("inverter (y = !a):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a");
            WireCore.AddNode(11, "y");
            WireCore.AddTransistor("inv", gate: 10, c1: 11, c2: WireCore.Ngnd);
            WireCore.Nodes[11]!.Pullups = 1;          // segdef '+'
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            int f = 0;
            f += Check("a floating (0) -> y = 1 (pull-up)", WireCore.IsNodeHigh("y"));
            WireCore.SetHigh("a"); f += Check("a = 1 -> y = 0", !WireCore.IsNodeHigh("y"));
            WireCore.SetLow ("a"); f += Check("a = 0 -> y = 1", WireCore.IsNodeHigh("y"));
            WireCore.SetHigh("a"); f += Check("a = 1 -> y = 0 (again)", !WireCore.IsNodeHigh("y"));
            WireCore.Shutdown();
            return f;
        }

        // NMOS NAND: 'a' and 'b' in series pulling 'y' down to vss; 'y' has a pull-up. y == !(a && b).
        private static int TestNand()
        {
            Console.WriteLine("NAND (y = !(a & b)):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "b"); WireCore.AddNode(12, "y"); WireCore.AddNode(13, "mid");
            WireCore.AddTransistor("t1", gate: 10, c1: 12, c2: 13);              // a: y <-> mid
            WireCore.AddTransistor("t2", gate: 11, c1: 13, c2: WireCore.Ngnd);  // b: mid <-> vss
            WireCore.Nodes[12]!.Pullups = 1;
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            int f = 0;
            foreach (var (a, b, expectHigh) in new[] { (false, false, true), (true, false, true), (false, true, true), (true, true, false) })
            {
                if (a) WireCore.SetHigh("a"); else WireCore.SetLow("a");
                if (b) WireCore.SetHigh("b"); else WireCore.SetLow("b");
                f += Check($"a={(a ? 1 : 0)} b={(b ? 1 : 0)} -> y = {(expectHigh ? 1 : 0)}", WireCore.IsNodeHigh("y") == expectHigh);
            }
            WireCore.Shutdown();
            return f;
        }

        // Pass transistor + dynamic hold: gate 'en' connects 'in' <-> 'out'; 'out' has no pull-up.
        // en=1 -> out follows in; en=0 -> out holds its last value (parasitic capacitance abstraction).
        private static int TestPassTransistor()
        {
            Console.WriteLine("pass transistor / dynamic hold:");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "in"); WireCore.AddNode(11, "out"); WireCore.AddNode(12, "en");
            WireCore.AddTransistor("pass", gate: 12, c1: 10, c2: 11);
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            int f = 0;
            WireCore.SetHigh("en");
            WireCore.SetHigh("in");  f += Check("en=1 in=1 -> out = 1", WireCore.IsNodeHigh("out"));
            WireCore.SetLow ("in");  f += Check("en=1 in=0 -> out = 0", !WireCore.IsNodeHigh("out"));
            WireCore.SetHigh("in");  f += Check("en=1 in=1 -> out = 1 (again)", WireCore.IsNodeHigh("out"));
            WireCore.SetLow ("en");                 // disconnect
            WireCore.SetLow ("in");                 // change the (now disconnected) input
            f += Check("en=0 -> out holds previous value (1)", WireCore.IsNodeHigh("out"));
            WireCore.SetHigh("en");                 // reconnect; out should now follow in (=0)
            f += Check("en=1 again -> out tracks in (0)", !WireCore.IsNodeHigh("out"));
            WireCore.Shutdown();
            return f;
        }

        // Callback / fake-transistor mechanism: a callback watching node 'w' must fire after 'w' changes.
        private static int TestCallback()
        {
            Console.WriteLine("callback (fake-transistor watch):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "w");                 // a node we'll drive
            int fires = 0;
            // AddCallback must happen *before* Reset() (it adds a fake node + transistors).
            WireCore.AddCallback(new[] { WireCore.LookupNode("w") }, () => fires++);
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            int f = 0;
            int before = fires;
            WireCore.SetHigh("w");
            f += Check("callback fires after w: 0 -> 1", fires > before);
            before = fires;
            WireCore.SetHigh("w");                     // no change ⇒ no fire
            f += Check("callback does NOT fire when w unchanged", fires == before);
            before = fires;
            WireCore.SetLow("w");
            f += Check("callback fires after w: 1 -> 0", fires > before);
            WireCore.Shutdown();
            return f;
        }

        // S1.5 lowering: an always-on connection between 'mid' and 'out' must collapse them into one node,
        // pull-up and behaviour preserved (out == !a, same as if the connection were left as a transistor).
        private static int TestStaticMerge()
        {
            Console.WriteLine("static-group merge (LowerNetlist):");
            bool savedLower = WireCore.EnableLowering;
            WireCore.EnableLowering = true;
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "mid"); WireCore.AddNode(12, "out");
            WireCore.AddTransistor("inv", gate: 10, c1: 11, c2: WireCore.Ngnd);   // a pulls 'mid' down to vss
            WireCore.AddConnection(11, 12);                                        // mid <> out  (gate = Npwr → always on)
            WireCore.Nodes[12]!.Pullups = 1;                                       // the pull-up sits on 'out'
            WireCore.LowerNetlist();
            int f = 0;
            int m = WireCore.LookupNode("mid"), o = WireCore.LookupNode("out");
            f += Check("'mid' and 'out' merged to one node", m != WireCore.EmptyNode && m == o);
            f += Check("merged node kept the pull-up", o != WireCore.EmptyNode && WireCore.Nodes[o]!.Pullups >= 1);
            f += Check("the always-on connection was dropped (only 'inv' left)", WireCore.TransistorBuildCount == 1);
            f += Check("'a' still resolves", WireCore.LookupNode("a") != WireCore.EmptyNode);
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            f += Check("a floating (0) -> out = 1 (pull-up), mid == out", WireCore.IsNodeHigh("out") && WireCore.IsNodeHigh("mid"));
            WireCore.SetHigh("a"); f += Check("a = 1 -> out = 0, mid == out", !WireCore.IsNodeHigh("out") && !WireCore.IsNodeHigh("mid"));
            WireCore.SetLow ("a"); f += Check("a = 0 -> out = 1 (again)", WireCore.IsNodeHigh("out"));
            WireCore.Shutdown();
            WireCore.EnableLowering = savedLower;
            return f;
        }

        // ── Run N frames headless and dump the FrameBuffer to a PNG (visual verification of the
        //    video handler / the rendered picture). Wraps the unmanaged FrameBuffer in a Bitmap (no copy). ──
        private static int Screenshot(string romPath, int frames, string outPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, CHR {rom.ChrRom.Length / 1024} KB, mapper {rom.Mapper}) — running {frames} frame(s)");
            try
            {
                WireCore.LoadSystem(rom);
                for (int f = 0; f < frames; f++)
                {
                    long hc = WireCore.RunFrame();
                    Console.WriteLine($"#  frame {f + 1}/{frames}: {hc} half-cycles  |  {WireCore.DumpCpuState()}");
                }
                unsafe
                {
                    if (WireCore.FrameBuffer == null) { Console.Error.WriteLine("no FrameBuffer"); return 2; }
                    using var bmp = new System.Drawing.Bitmap(
                        WireCore.ScreenW, WireCore.ScreenH, WireCore.ScreenW * 4,
                        System.Drawing.Imaging.PixelFormat.Format32bppRgb, (IntPtr)WireCore.FrameBuffer);
                    bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine($"# wrote {outPath}  ({WireCore.ScreenW}x{WireCore.ScreenH}, {WireCore.Time} half-cycles total)");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── Headless throughput benchmark: how many simulated NES frames / 6502 cycles per real second.
        //    (S3 baseline — the switch-level interpreter's speed, before the IR backend exists to beat it.)
        //    Constants: 2A03 clk0 toggles every 24 master half-cycles ⇒ 1 simulated CPU cycle = 24 steps;
        //    one NTSC frame ≈ 357366 master clocks = 714732 half-cycles; CPU 1.789773 MHz; 60.0988 Hz.
        // ── BenchmarkHalfCycles: time N raw master-half-cycles directly via WireCore.Step(N).
        //    For when --frames is too coarse (e.g. measuring a variant that's 100x slower than S1,
        //    where waiting for a whole frame would take an hour). Reports hc/s and the load+power-on
        //    time separately.
        public static int BenchmarkHalfCycles(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 1000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# bench-hc: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {hcCount:N0} master half-cycles");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);                              // S1 reset (IrRoot null => switch-level used here)
                if (WireCore.EnableIrInterp) { WireCore.BuildCombinationalIr(); WireCore.BuildRevDep(); }   // Phase 2 P2.3: build IR after reset, then the timed run uses it
                swLoad.Stop();
                WireCore.EnqueueCount = 0; WireCore.RecalcNodeCount = 0;
                WireCore.DispBlockEvalCount = WireCore.DispAluEvalCount = WireCore.DispInterpEvalCount = 0;   // Phase 2.5 Step 2 reset
                if (WireCore.CountEvents) WireCore.InitGlitchDiag();   // 策略三: arm per-half-cycle re-recalc counting over the timed window only
                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                WireCore.Step(hcCount);
                sw.Stop();
                long halfCycles = WireCore.Time - t0;
                double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
                double stepsHz = halfCycles / secs;
                ulong stateHash = WireCore.NodeStatesChecksum();   // FNV-1a over NodeStates — for rigorous A/B per-node equivalence (after timing)
                Console.WriteLine($"# {WireCore.LastLowerStats}");
                Console.WriteLine($"# {WireCore.LastRcmStats}");
                Console.WriteLine($"# {WireCore.LastFastPathStats}");
                Console.WriteLine($"# {WireCore.LastLevelizeStats}");
                if (WireCore.EnableIrInterp) { Console.WriteLine($"# {WireCore.LastIrStats}"); Console.WriteLine($"# {WireCore.LastRevDepStats}"); }
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {halfCycles:N0} master half-cycles in {secs:F3} s");
                Console.WriteLine($"# rate: {stepsHz:N0} hc/s ({secs * 1e6 / halfCycles:F2} µs/hc)");
                Console.WriteLine($"# NodeStates checksum @ t={WireCore.Time}: 0x{stateHash:X16}  (A/B equivalence: must match the baseline run)");
                if (WireCore.CountEvents)
                {
                    Console.WriteLine($"# events: {WireCore.EnqueueCount:N0} EnqueueNode, {WireCore.RecalcNodeCount:N0} RecalcNode over {halfCycles:N0} hc  ({(double)WireCore.RecalcNodeCount / halfCycles:F1} RecalcNode/hc = D)");
                    if (WireCore.EnableIrInterp)
                    {
                        long total = WireCore.RecalcIrCount + WireCore.RecalcAbsorbedCount + WireCore.RecalcHybridCount;
                        double pct(long x) => total > 0 ? 100.0 * x / total : 0;
                        Console.WriteLine($"# D split: IR={WireCore.RecalcIrCount:N0} ({pct(WireCore.RecalcIrCount):F1}%), absorbed={WireCore.RecalcAbsorbedCount:N0} ({pct(WireCore.RecalcAbsorbedCount):F1}%), hybrid={WireCore.RecalcHybridCount:N0} ({pct(WireCore.RecalcHybridCount):F1}%)");
                    }
                    // 策略三 glitch factor: avg times the same node is re-evaluated within one half-cycle (~1.0 ⇒ no glitching)
                    double glitch = WireCore.DistinctRecalcCount > 0 ? (double)WireCore.RecalcNodeCount / WireCore.DistinctRecalcCount : 0;
                    Console.WriteLine($"# glitch factor: {WireCore.RecalcNodeCount:N0} RecalcNode / {WireCore.DistinctRecalcCount:N0} distinct (node,hc) = {glitch:F3} recalcs/node/hc  (>1.1 ⇒ glitches worth chasing; ~1.0 ⇒ none)");
                    if (WireCore.EnableCodegenDispatcher)
                    {
                        // Phase 2.5 Step 2: dispatcher block-eval frequencies. ALU/hc shows how often the
                        // dirty bit fires (cheap byte-watch); a typical 6502 has ALU evals on a fraction
                        // of every cycle (operand fetch + execute), so a healthy figure is single-digit per hc.
                        Console.WriteLine($"# {WireCore.LastDispatcherStats}");
                        Console.WriteLine($"# dispatcher: {WireCore.DispBlockEvalCount:N0} block-evals ({WireCore.DispInterpEvalCount:N0} interp, {WireCore.DispAluEvalCount:N0} ALU) over {halfCycles:N0} hc  ({(double)WireCore.DispAluEvalCount / halfCycles:F2} ALU evals/hc; mode = {(WireCore.EnableCodegenAluWriteback ? "WRITEBACK" : "dry-run")})");
                    }
                }
            }
            finally { WireCore.Shutdown(); }
            return 0;
        }

        private static int Benchmark(string romPath, int frames)
        {
            if (frames < 4) frames = 12;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# benchmark: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {frames} frame(s) headless, Release build recommended");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                swLoad.Stop();

                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < frames; f++) WireCore.RunFrame();
                sw.Stop();

                long halfCycles = WireCore.Time - t0;
                double secs = sw.Elapsed.TotalSeconds;
                if (secs <= 0) secs = 1e-9;
                double cpuCycles = halfCycles / 24.0;             // 24 master half-cycles per 2A03 clk0 cycle
                double fps      = frames / secs;
                double cpuHz    = cpuCycles / secs;
                double stepsHz  = halfCycles / secs;
                const double realFps = 60.0988, realCpuHz = 1_789_773.0;
                const double cycPerInstr = 2.8;                  // rough NES-code average (3..7 cyc opcodes, mostly 2..4)

                Console.WriteLine($"# {WireCore.LastLowerStats}");
                Console.WriteLine($"# {WireCore.LastRcmStats}");
                Console.WriteLine($"# {WireCore.LastFastPathStats}");
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {frames} frames = {halfCycles:N0} master half-cycles = {cpuCycles:N0} 6502 cycles");
                Console.WriteLine($"# real time: {secs:F3} s");
                Console.WriteLine();
                Console.WriteLine($"  FPS  = {fps,8:F2}  simulated NES frames / real second        ( {fps / realFps * 100:F1}% of realtime, i.e. 1 real second of NES takes {realFps / fps:F1} s to simulate )");
                Console.WriteLine($"  MIPS = {cpuHz / 1e6,8:F3}  M simulated 6502 cycles / real second    ( {cpuHz / realCpuHz * 100:F1}% of the real 1.79 MHz 2A03 )");
                Console.WriteLine($"         {cpuHz / cycPerInstr / 1e6,8:F3}  M 6502 instructions / s  (≈, assuming ~{cycPerInstr:F1} cycles/instr)");
                Console.WriteLine($"  ---");
                Console.WriteLine($"  raw   {stepsHz / 1e6,8:F2}  M switch-level steps (master half-cycles) / s   — the actual inner-loop rate");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── Diagnostic: after N frames, dump the PPU's palette RAM + VRAM nametable 0 + rendering state,
        //    and sample pal_ptr/hpos/vpos over the next ~32 pixel-clock edges — to chase the "screen is all
        //    backdrop" issue (did the palette/VRAM writes land? does pal_ptr track the pixel pipeline?). ──
        private static int PpuDump(string romPath, int frames)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)} — running {frames} frame(s)");
            try
            {
                WireCore.LoadSystem(rom);
                int rdis = WireCore.LookupNode("ppu.rendering_disabled");
                for (int f = 0; f < frames; f++)
                {
                    WireCore.RunFrame();
                    if ((f + 1) % 10 == 0 || f == frames - 1)
                    {
                        // count nonzero palette-RAM entries as a quick "has the test drawn anything" gauge
                        int nzPal = 0;
                        for (int i = 0; i < 32; i++)
                        {
                            var pl = new List<int>(); WireCore.ResolveNodes($"ppu.pal_ram_{i:X2}_b[5:0]", pl, quiet: true);
                            if (pl.Count == 6 && WireCore.ReadBits(pl) != 0x0F) nzPal++;
                        }
                        int rd = rdis != WireCore.EmptyNode && WireCore.IsNodeHigh(rdis) ? 1 : 0;
                        Console.WriteLine($"#  frame {f + 1,3}: {WireCore.DumpCpuState()}  rendering_disabled={rd}  pal_ram!=0F:{nzPal}/32");
                        Console.Out.Flush();
                    }
                }
                Console.WriteLine($"# after {frames} frame(s): {WireCore.DumpCpuState()}");

                // palette RAM (32 entries, 6-bit each — the "b" side, like handler_palette_ram)
                var sb = new StringBuilder("palette RAM (6-bit):");
                for (int i = 0; i < 32; i++)
                {
                    var l = new List<int>(); WireCore.ResolveNodes($"ppu.pal_ram_{i:X2}_b[5:0]", l, quiet: true);
                    int v = l.Count == 6 ? WireCore.ReadBits(l) : -1;
                    if ((i & 7) == 0) sb.Append("  ");
                    sb.Append(' ').Append(v < 0 ? "??" : v.ToString("X2"));
                }
                Console.WriteLine(sb);

                // PPU VRAM (u4.ram) — nametable 0
                var vram = WireCore.ResolveMemory("u4.ram");
                if (vram != null && vram.Data.Length >= 64)
                {
                    sb = new StringBuilder("VRAM[0000..003F]:");
                    for (int i = 0; i < 64; i++) { if ((i & 15) == 0) sb.Append("  "); sb.Append(' ').Append(vram.Data[i].ToString("X2")); }
                    Console.WriteLine(sb);
                    int nzNt = 0, nzAt = 0;
                    int ntLen = Math.Min(0x3C0, vram.Data.Length);          // 960-byte name table
                    for (int i = 0; i < ntLen; i++) if (vram.Data[i] != 0) nzNt++;
                    for (int i = 0x3C0; i < 0x400 && i < vram.Data.Length; i++) if (vram.Data[i] != 0) nzAt++;
                    Console.WriteLine($"# nametable 0: {nzNt}/{ntLen} nonzero tile bytes, {nzAt}/64 nonzero attr bytes");
                }
                else Console.WriteLine("# (no u4.ram memory)");

                // rendering / vblank state
                foreach (var n in new[] { "ppu.rendering_disabled", "ppu.in_vblank", "ppu.in_visible_frame", "ppu.in_visible_frame_and_rendering" })
                {
                    int id = WireCore.LookupNode(n);
                    if (id != WireCore.EmptyNode) Console.WriteLine($"# {n} = {(WireCore.IsNodeHigh(id) ? 1 : 0)}");
                }

                // sample pal_ptr / hpos / vpos at the next ~32 pclk1 rising edges
                int pclk1 = WireCore.LookupNode("ppu.pclk1");
                var pp = new List<int>(); WireCore.ResolveNodes("ppu.pal_ptr[4:0]", pp, quiet: true);
                var hp = new List<int>(); WireCore.ResolveNodes("ppu.hpos[8:0]", hp, quiet: true);
                var vp = new List<int>(); WireCore.ResolveNodes("ppu.vpos[8:0]", vp, quiet: true);
                if (pclk1 != WireCore.EmptyNode && pp.Count > 0 && hp.Count > 0 && vp.Count > 0)
                {
                    Console.WriteLine("pixel samples (at pclk1 rising edges) — hpos:vpos:pal_ptr:");
                    bool prev = WireCore.IsNodeHigh(pclk1);
                    int got = 0;
                    sb = new StringBuilder("  ");
                    for (long i = 0; i < 2_000_000 && got < 48; i++)
                    {
                        WireCore.Step(1);
                        bool now = WireCore.IsNodeHigh(pclk1);
                        if (!prev && now)
                        {
                            sb.Append($"{WireCore.ReadBits(hp)}:{WireCore.ReadBits(vp)}:{WireCore.ReadBits(pp):X2}  ");
                            if (++got % 8 == 0) { Console.WriteLine(sb); sb = new StringBuilder("  "); }
                        }
                        prev = now;
                    }
                    if (sb.Length > 2) Console.WriteLine(sb);
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── Trace one $2002 (PPUSTATUS) read: run a frame to reach vblank, step until cpu.ab == 0x2002,
        //    then dump the CPU↔PPU register-path signals for ~40 half-cycles. Finds the broken link
        //    (74LS139 decode? ppu.io_ce? PPU reg logic driving ppu.io_db? data bus back to cpu.db?). ──
        private static int Probe2002(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)} — probing a $2002 read");
            try
            {
                WireCore.LoadSystem(rom);

                int[] ab = ResolveQ("cpu.ab[15:0]");
                int[] db = ResolveQ("cpu.db[7:0]");
                int[] ioAb = ResolveQ("ppu.io_ab[2:0]");
                int[] ioDb = ResolveQ("ppu.io_db[7:0]");
                int rw   = WireCore.LookupNode("cpu.rw");
                int clk0 = WireCore.LookupNode("cpu.clk0");
                int u3y1 = WireCore.LookupNode("u3.1/Y1");
                int u3y0 = WireCore.LookupNode("u3.1/Y0");
                int u3y3 = WireCore.LookupNode("u3.2/Y3");
                int ioCe = WireCore.LookupNode("ppu.io_ce");
                int inVbl= WireCore.LookupNode("ppu.in_vblank");
                int vblF = WireCore.LookupNode("ppu.vbl_flag0");   // may not exist
                int H1(int n) => n != WireCore.EmptyNode && WireCore.IsNodeHigh(n) ? 1 : 0;

                WireCore.RunFrame();   // → in_vblank rising edge: the vbl flag should now be set
                Console.WriteLine($"# at vblank start: t={WireCore.Time}  in_vblank={H1(inVbl)}  {WireCore.DumpCpuState()}");

                // step until the CPU puts $2002 on the address bus
                bool found = false;
                for (long i = 0; i < 200_000; i++)
                {
                    WireCore.Step(1);
                    if (WireCore.ReadBits(ab) == 0x2002) { found = true; break; }
                }
                if (!found) { Console.WriteLine("# no $2002 access seen in 200k half-cycles after vblank"); return 1; }

                Console.WriteLine("# cols: t  clk0  cpu.ab  rw  u3.2/Y3(/romsel)  u3.1/Y0(sram)  u3.1/Y1(ppu)  ppu.io_ce  ppu.io_ab  ppu.io_db  cpu.db  in_vblank");
                for (int j = 0; j < 40; j++)
                {
                    Console.WriteLine($"  {WireCore.Time,8}  {H1(clk0)}  {WireCore.ReadBits(ab):X4}  {(rw != WireCore.EmptyNode && WireCore.IsNodeHigh(rw) ? 'R' : 'W')}  {H1(u3y3)}  {H1(u3y0)}  {H1(u3y1)}  {H1(ioCe)}  {WireCore.ReadBits(ioAb):X1}  {WireCore.ReadBits(ioDb):X2}  {WireCore.ReadBits(db):X2}  {H1(inVbl)}");
                    WireCore.Step(1);
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static int[] ResolveQ(string expr) { var l = new List<int>(); WireCore.ResolveNodes(expr, l, quiet: true); return l.ToArray(); }

        // ── Introspect a node's wiring: pull-up count + the transistors it gates + the transistors it's
        //    a channel end of (with each transistor's gate / other end). For tracing logic structure. ──
        private static int DumpNode(string name)
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            int id = WireCore.LookupNode(name);
            if (id == WireCore.EmptyNode) { Console.Error.WriteLine($"no node named '{name}'"); return 1; }
            WireCore.Node? node = id >= 0 && id < WireCore.Nodes.Count ? WireCore.Nodes[id] : null;
            string Nm(int n) => $"{WireCore.GetNodeName(n)}#{n}";
            Console.WriteLine($"node '{name}' = id {id}");
            if (node == null) { Console.WriteLine("  (no Node object — supply node or unused)"); return 0; }
            Console.WriteLine($"  pullups={node.Pullups}  gates={node.Gates.Count}  c1c2s={node.C1c2s.Count}  callback={(node.Callback != null)}");
            Console.WriteLine($"  ── transistors GATED by this node ({node.Gates.Count}) — i.e. this node turns these on/off:");
            foreach (int tid in node.Gates)
            { var t = WireCore.Transistors[tid]; Console.WriteLine($"     '{t.Name}'  channel: {Nm(t.C1)} <-> {Nm(t.C2)}{(t.IsWeak ? "  (weak)" : "")}"); }
            Console.WriteLine($"  ── transistors with this node as a CHANNEL end ({node.C1c2s.Count}) — i.e. these drive/connect this node when their gate is on:");
            foreach (int tid in node.C1c2s)
            { var t = WireCore.Transistors[tid]; int other = t.C1 == id ? t.C2 : t.C1; Console.WriteLine($"     '{t.Name}'  gate={Nm(t.Gate)}  other end: {Nm(other)}{(t.IsWeak ? "  (weak)" : "")}"); }
            return 0;
        }

        // ── Trace the 2C02's latched vblank flag (the $2002 bit-7 source): does set_vbl_flag pulse at
        //    vblank start? does vbl_flag latch high and stay? does read_2002_output_vblank_flag follow it
        //    onto ppu.io_db during a $2002 read? ──
        private static int ProbeVbl(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)} — probing the 2C02 vbl flag");
            try
            {
                WireCore.LoadSystem(rom);
                int inVbl  = WireCore.LookupNode("ppu.in_vblank");
                int vblF   = WireCore.LookupNode("ppu.vbl_flag");
                int nVblF  = WireCore.LookupNode("ppu./vbl_flag");
                int setVbl = WireCore.LookupNode("ppu.set_vbl_flag");
                int rdOut  = WireCore.LookupNode("ppu.read_2002_output_vblank_flag");
                int nR2002 = WireCore.LookupNode("ppu./r2002");
                int spr0   = WireCore.LookupNode("ppu.spr0_hit");
                int sprOv  = WireCore.LookupNode("ppu.spr_overflow");
                int ioCe   = WireCore.LookupNode("ppu.io_ce");
                int rw     = WireCore.LookupNode("cpu.rw");
                int[] hp = ResolveQ("ppu.hpos[8:0]"), vp = ResolveQ("ppu.vpos[8:0]"), ioDb = ResolveQ("ppu.io_db[7:0]"), ab = ResolveQ("cpu.ab[15:0]");
                int H1(int n) => n != WireCore.EmptyNode && WireCore.IsNodeHigh(n) ? 1 : 0;
                int Rd(int[] a) => WireCore.ReadBits(a);

                Console.WriteLine($"# node ids: in_vblank={inVbl} vbl_flag={vblF} /vbl_flag={nVblF} set_vbl_flag={setVbl} read_2002_output_vblank_flag={rdOut} /r2002={nR2002}");

                // extra read-buffer nodes
                int nRdOut = WireCore.LookupNode("ppu./read_2002_output_vblank_flag");
                int nVblOut = WireCore.LookupNode("ppu./vbl_flag_out");
                int nBuf   = WireCore.LookupNode("ppu./vbl_flag_read_buffer");
                int bufOut = WireCore.LookupNode("ppu.vbl_flag_read_buffer_out");
                int ioDb7  = WireCore.LookupNode("ppu._io_db7");
                int ioCe2  = WireCore.LookupNode("ppu._io_ce");

                int clk0n = WireCore.LookupNode("cpu.clk0");
                WireCore.RunFrame();                                    // → in_vblank rising edge (vpos 240 dot 0)
                for (long i = 0; i < 400_000 && H1(vblF) == 0; i++) WireCore.Step(1);   // → vbl_flag set (vpos 241 dot 1)
                Console.WriteLine($"# vbl_flag set at t={WireCore.Time} vpos={Rd(vp)} hpos={Rd(hp)} — tracing 160 half-cycles (covers the next $2002 read while the flag is up); only printing on change");
                Console.WriteLine("# per half-cycle — t hpos | r_out(7869) /r_out(7729) vbl_flag /vbl_flag /vbl_out buf(/7827) bufOut(7871) | /r2002 _io_ce _io_db7 io_db | cpu.ab clk0");
                string lastLine = "";
                for (int j = 0; j < 160; j++)
                {
                    string line = $"{H1(rdOut)} {H1(nRdOut)} {H1(vblF)} {H1(nVblF)} {H1(nVblOut)} {H1(nBuf)} {H1(bufOut)} | {H1(nR2002)} {H1(ioCe2)} {H1(ioDb7)} {Rd(ioDb):X2} | {Rd(ab):X4} {(clk0n != WireCore.EmptyNode && WireCore.IsNodeHigh(clk0n) ? 1 : 0)}";
                    if (line != lastLine) { Console.WriteLine($"  {WireCore.Time,8} {Rd(hp),3} | {line}"); lastLine = line; }
                    WireCore.Step(1);
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            try
            {
                // TODO (S3): if (benchmark) — measure switch-level cycles/sec; later compare vs IR backend.
                WireCore.LoadSystem(rom);   // composes + attaches handlers + copies ROM + power-on resets

                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < maxWait)
                {
                    WireCore.RunFrame();
                    var r = WireCore.CheckUnitTest();
                    if (r.Complete)
                    {
                        if (r.Code == 0) { Console.WriteLine($"PASS | {Path.GetFileName(path)} | {name}\n{r.Text}"); return 0; }
                        Console.WriteLine($"FAIL({r.Code}) | {Path.GetFileName(path)} | ({r.Text})");
                        return r.Code == 0 ? 1 : r.Code;
                    }
                }
                var rr = WireCore.CheckUnitTest();
                Console.WriteLine($"TIMEOUT | {Path.GetFileName(path)} | {name} | {(rr.Found ? $"sig found, code 0x{rr.Code:X2}" : "no $6000 signature")}  ({WireCore.Time} half-cycles)");
                return 3;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── Step 7 / S1 exit-gate harness: load a ROM, power-on reset, step N CPU cycles (≈ N*24 half-cycles),
        //    and dump the CPU's named-state nodes each cycle — for eyeballing / comparing against
        //    MetalNES / chipsim.js / Perfect6502. ──
        private static int Trace(string path, int cycles)
        {
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {path}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(path)}  (PRG {rom.PrgRom.Length / 1024} KB, CHR {rom.ChrRom.Length / 1024} KB, mapper {rom.Mapper})");
            try
            {
                WireCore.LoadSystem(rom);
                if (WireCore.EnableIrInterp) { WireCore.BuildCombinationalIr(); WireCore.BuildRevDep(); }   // P2.3: build IR after S1 reset
                Console.WriteLine($"# after power-on reset: {WireCore.DumpCpuState()}");
                int prevSync = -1;
                int instrCount = 0;
                for (int c = 0; c < cycles; c++)
                {
                    WireCore.Step(12 * 2);          // one 6502 cycle = 12 master cycles = 24 half-cycles
                    string line = WireCore.DumpCpuState();
                    // mark instruction-fetch cycles (cpu.sync high) for readability
                    bool sync = line.Contains("(fetch)");
                    if (sync) instrCount++;
                    Console.WriteLine($"  cyc {c + 1,5}  {line}");
                    if (c > 12 && WireCore.Time == 0) break;   // sanity: clk never advanced ⇒ bail
                    prevSync = sync ? 1 : 0;
                }
                Console.WriteLine($"# {instrCount} opcode-fetch cycle(s) observed in {cycles} CPU cycles ({WireCore.Time} half-cycles)");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                AprVisual — switch-level NES (S1)

                  AprVisual --rom <game.nes>            show a window; CPU state in the title bar (video output: WIP)
                  AprVisual --trace <rom> [--cycles N]  headless: power-on reset, step N 6502 cycles, dump CPU state each cycle (default N=64)
                  AprVisual --screenshot <rom> [--frames N] [--out p.png]   headless: run N frames, dump the framebuffer to a PNG (default N=3, out=screenshot.png)
                  AprVisual --ppu-dump <rom> [--frames N]   headless: run N frames, then dump palette RAM / VRAM nametable / rendering state / pixel-clock samples
                  AprVisual --benchmark <rom> [--frames N]  headless throughput: simulated FPS, MIPS (6502 cyc/s), raw step rate (default N=12; use a Release build)
                  AprVisual --test <test.nes>           headless: run to the $6000 signature, print PASS/FAIL
                  AprVisual --test-dir <dir>            headless: batch-run *.nes under <dir>
                    [--max-wait <sec>]                  timeout per test (default 15)
                    [--region ntsc|pal|dendy]
                    [--benchmark]                       also time each test (cycles/sec)
                  AprVisual --dump-module <name>        parse <system-def-dir>/<name>.js and print a summary
                  AprVisual --dump-system               compose the full nes-001 + cart netlist and print counts + probes
                  AprVisual --selftest                  run hand-built inverter / NAND / pass-transistor / static-merge circuits and check truth tables
                    [--system-def-dir <dir>]            default: data/system-def
                    [--no-lower]                         skip the S1.5 netlist-lowering pass (A/B comparison)
                  (no args)                             open an empty window
                """);
        }
    }
}
