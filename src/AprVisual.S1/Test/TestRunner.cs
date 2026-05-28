using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    // ── AprVisual.S1 fork CLI. Pruned of all post-S1 branches (IR / Codegen / PruneMerge / Levelize
    //    / Oblivious / RCM / SimdQueue / LutTtl / DeadEndSkip / AOT). Optimizations that proved out
    //    are now hardcoded on (fast-path, no-op skip, iterative BFS, batch settle, JIT inline cascade).
    //    Diagnostic flags retained: --chip-diag --settle-stats --dead-end-diag --count-events.
    internal static class TestRunner
    {
        public static int Run(string[] args)
        {
            string? romPath = null, testPath = null, testDir = null;
            string? dumpModule = null, tracePath = null, shotPath = null, ppuDumpPath = null;
            string? probePath = null, probeVblPath = null, dumpNodeName = null, benchPath = null;
            string systemDefDir = WireCore.SystemDefDir;
            string shotOut = "screenshot.png";
            int maxWait = 15;
            int traceCycles = 64;
            int shotFrames = 3;
            int benchHcCount = 0;
            string region = "ntsc";
            bool benchmark = false, dumpSystem = false;

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
                    case "--selftest":        return SelfTest();
                    case "--system-def-dir":  if (i + 1 < args.Length) systemDefDir = args[++i]; break;
                    case "--no-lower":        WireCore.EnableLowering = false; break;
                    case "--chip-diag":       WireCore.EnableChipDiag = true; break;
                    case "--settle-stats":    WireCore.EnableSettleStats = true; break;
                    case "--dead-end-diag":   WireCore.EnableDeadEndDiag = true; break;
                    case "--count-events":    WireCore.CountEvents = true; break;
                    case "--bench-hc":        if (i + 1 < args.Length) int.TryParse(args[++i], out benchHcCount); break;
                    case "--max-wait":        if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--region":          if (i + 1 < args.Length) region       = args[++i].ToLowerInvariant(); break;
                    case "--fast-path":       /* no-op: always on in S1 */ break;
                    case "--benchmark":
                        benchmark = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) benchPath = args[++i];
                        break;
                    case "--help": case "-h": case "/?": PrintUsage(); return 0;
                    default:
                        if (romPath is null && testPath is null && testDir is null && dumpModule is null
                            && tracePath is null && shotPath is null && ppuDumpPath is null && probePath is null
                            && probeVblPath is null && dumpNodeName is null && !dumpSystem && !args[i].StartsWith('-'))
                            romPath = args[i];
                        break;
                }
            }

            WireCore.SystemDefDir = systemDefDir;

            if (dumpModule    != null) return DumpModule(systemDefDir, dumpModule);
            if (dumpSystem)            return DumpSystem();
            if (tracePath     != null) return Trace(tracePath, traceCycles);
            if (shotPath      != null) return Screenshot(shotPath, shotFrames, shotOut);
            if (ppuDumpPath   != null) return PpuDump(ppuDumpPath, shotFrames);
            if (probePath     != null) return Probe2002(probePath);
            if (probeVblPath  != null) return ProbeVbl(probeVblPath);
            if (dumpNodeName  != null) return DumpNode(dumpNodeName);
            if (benchPath     != null && benchHcCount > 0) return BenchmarkHalfCycles(benchPath, benchHcCount);
            if (benchPath     != null) return Benchmark(benchPath, shotFrames);

            if (romPath != null)
            {
                ApplicationConfiguration.Initialize();
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

            if (testPath != null) return RunOneTest(testPath, maxWait, region, benchmark);

            PrintUsage();
            return 0;
        }

        // ── module-level dump: parse one .js def + sub-modules and print a summary ──
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

            var sample = new List<string>();
            foreach (var probe in new[] { "vcc", "vss", "clk0", "res", "rw", "a0", "x0", "y0", "pcl0", "ab0", "db0", "func<ram>" })
                if (def.NodeNames.ContainsKey(probe)) sample.Add(probe);
            if (sample.Count > 0) Console.WriteLine($"\n  sample nodes present: {string.Join(", ", sample)}");
            return 0;
        }

        // ── full nes-001 + cart compose + Reset + a few probes ──
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
            void Probe(string e) { var l = new List<int>(); WireCore.ResolveNodes(e, l); Console.WriteLine($"  {e,-22} -> {l.Count,4} nodes  [{string.Join(",", System.Linq.Enumerable.Take(l, 8))}{(l.Count > 8 ? ",…" : "")}]"); }
            Probe("cpu.ab[15:0]");
            Probe("cpu.db[7:0]");
            Probe("cpu.a[7:0]");
            Probe("cart.edge.cpu_a[14:0]");
            Probe("*.vss");
            Probe("*.vcc");

            try { WireCore.Reset(); }
            catch (Exception ex) { Console.Error.WriteLine($"Reset() failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"); return 2; }

            int stateHigh = 0;
            for (int i = 0; i < WireCore.NodeCount; i++) unsafe { if (WireCore.NodeStates[i] != 0) stateHigh++; }
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

        // ── --selftest: hand-built inverter/NAND/pass-transistor/callback/static-merge circuits ──
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

        private static int TestInverter()
        {
            Console.WriteLine("inverter (y = !a):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a");
            WireCore.AddNode(11, "y");
            WireCore.AddTransistor("inv", gate: 10, c1: 11, c2: WireCore.Ngnd);
            WireCore.Nodes[11]!.Pullups = 1;
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

        private static int TestNand()
        {
            Console.WriteLine("NAND (y = !(a & b)):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "b"); WireCore.AddNode(12, "y"); WireCore.AddNode(13, "mid");
            WireCore.AddTransistor("t1", gate: 10, c1: 12, c2: 13);
            WireCore.AddTransistor("t2", gate: 11, c1: 13, c2: WireCore.Ngnd);
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
            WireCore.SetLow ("en");
            WireCore.SetLow ("in");
            f += Check("en=0 -> out holds previous value (1)", WireCore.IsNodeHigh("out"));
            WireCore.SetHigh("en");
            f += Check("en=1 again -> out tracks in (0)", !WireCore.IsNodeHigh("out"));
            WireCore.Shutdown();
            return f;
        }

        private static int TestCallback()
        {
            Console.WriteLine("callback (fake-transistor watch):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "w");
            int fires = 0;
            WireCore.AddCallback(new[] { WireCore.LookupNode("w") }, () => fires++);
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            int f = 0;
            int before = fires;
            WireCore.SetHigh("w");
            f += Check("callback fires after w: 0 -> 1", fires > before);
            before = fires;
            WireCore.SetHigh("w");
            f += Check("callback does NOT fire when w unchanged", fires == before);
            before = fires;
            WireCore.SetLow("w");
            f += Check("callback fires after w: 1 -> 0", fires > before);
            WireCore.Shutdown();
            return f;
        }

        private static int TestStaticMerge()
        {
            Console.WriteLine("static-group merge (LowerNetlist):");
            bool savedLower = WireCore.EnableLowering;
            WireCore.EnableLowering = true;
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "mid"); WireCore.AddNode(12, "out");
            WireCore.AddTransistor("inv", gate: 10, c1: 11, c2: WireCore.Ngnd);
            WireCore.AddConnection(11, 12);
            WireCore.Nodes[12]!.Pullups = 1;
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

        // ── --screenshot: run N frames headless and PNG the FrameBuffer ──
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

        // ── --benchmark: simulated FPS, MIPS, raw step rate over N frames ──
        public static int Benchmark(string romPath, int frames)
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
                double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
                double cpuCycles = halfCycles / 24.0;
                double fps      = frames / secs;
                double cpuHz    = cpuCycles / secs;
                double stepsHz  = halfCycles / secs;
                const double realFps = 60.0988, realCpuHz = 1_789_773.0;
                const double cycPerInstr = 2.8;

                Console.WriteLine($"# {WireCore.LastLowerStats}");
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

        // ── --bench-hc: time exactly N raw master-half-cycles (finer than --frames; for slow variants) ──
        public static int BenchmarkHalfCycles(string romPath, int hcCount)
        {
            if (hcCount < 1) hcCount = 1000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# bench-hc: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {hcCount:N0} master half-cycles");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                swLoad.Stop();
                WireCore.EnqueueCount = 0; WireCore.RecalcNodeCount = 0;
                if (WireCore.CountEvents) WireCore.InitGlitchDiag();
                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                WireCore.Step(hcCount);
                sw.Stop();
                long halfCycles = WireCore.Time - t0;
                double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
                double stepsHz = halfCycles / secs;
                ulong stateHash = WireCore.NodeStatesChecksum();
                Console.WriteLine($"# {WireCore.LastLowerStats}");
                Console.WriteLine($"# {WireCore.LastFastPathStats}");
                if (WireCore.EnableChipDiag) { WireCore.ChipDiagAfterReport(); Console.WriteLine($"# {WireCore.LastChipDiagStats}"); }
                if (WireCore.EnableSettleStats) { WireCore.BuildSettleStatsString(); Console.WriteLine($"# {WireCore.LastSettleStats}"); }
                if (WireCore.EnableDeadEndDiag) WireCore.ReportDeadEndDiag();
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {halfCycles:N0} master half-cycles in {secs:F3} s");
                Console.WriteLine($"# rate: {stepsHz:N0} hc/s ({secs * 1e6 / halfCycles:F2} µs/hc)");
                Console.WriteLine($"# NodeStates checksum @ t={WireCore.Time}: 0x{stateHash:X16}  (A/B equivalence: must match the baseline run)");
                if (WireCore.CountEvents)
                {
                    Console.WriteLine($"# events: {WireCore.EnqueueCount:N0} EnqueueNode, {WireCore.RecalcNodeCount:N0} RecalcNode over {halfCycles:N0} hc  ({(double)WireCore.RecalcNodeCount / halfCycles:F1} RecalcNode/hc = D)");
                    double glitch = WireCore.DistinctRecalcCount > 0 ? (double)WireCore.RecalcNodeCount / WireCore.DistinctRecalcCount : 0;
                    Console.WriteLine($"# glitch factor: {WireCore.RecalcNodeCount:N0} RecalcNode / {WireCore.DistinctRecalcCount:N0} distinct (node,hc) = {glitch:F3} recalcs/node/hc  (>1.1 ⇒ glitches worth chasing; ~1.0 ⇒ none)");
                }
            }
            finally { WireCore.Shutdown(); }
            return 0;
        }

        // ── --ppu-dump: after N frames, dump palette RAM + VRAM nametable 0 + rendering state + pclk1 samples ──
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

                var sb = new StringBuilder("palette RAM (6-bit):");
                for (int i = 0; i < 32; i++)
                {
                    var l = new List<int>(); WireCore.ResolveNodes($"ppu.pal_ram_{i:X2}_b[5:0]", l, quiet: true);
                    int v = l.Count == 6 ? WireCore.ReadBits(l) : -1;
                    if ((i & 7) == 0) sb.Append("  ");
                    sb.Append(' ').Append(v < 0 ? "??" : v.ToString("X2"));
                }
                Console.WriteLine(sb);

                var vram = WireCore.ResolveMemory("u4.ram");
                if (vram != null && vram.Data.Length >= 64)
                {
                    sb = new StringBuilder("VRAM[0000..003F]:");
                    for (int i = 0; i < 64; i++) { if ((i & 15) == 0) sb.Append("  "); sb.Append(' ').Append(vram.Data[i].ToString("X2")); }
                    Console.WriteLine(sb);
                    int nzNt = 0, nzAt = 0;
                    int ntLen = Math.Min(0x3C0, vram.Data.Length);
                    for (int i = 0; i < ntLen; i++) if (vram.Data[i] != 0) nzNt++;
                    for (int i = 0x3C0; i < 0x400 && i < vram.Data.Length; i++) if (vram.Data[i] != 0) nzAt++;
                    Console.WriteLine($"# nametable 0: {nzNt}/{ntLen} nonzero tile bytes, {nzAt}/64 nonzero attr bytes");
                }
                else Console.WriteLine("# (no u4.ram memory)");

                foreach (var n in new[] { "ppu.rendering_disabled", "ppu.in_vblank", "ppu.in_visible_frame", "ppu.in_visible_frame_and_rendering" })
                {
                    int id = WireCore.LookupNode(n);
                    if (id != WireCore.EmptyNode) Console.WriteLine($"# {n} = {(WireCore.IsNodeHigh(id) ? 1 : 0)}");
                }

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

        // ── --probe2002: trace cpu/ppu bus signals at the next $2002 read after vblank ──
        private static int Probe2002(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)} — probing a $2002 read");
            try
            {
                WireCore.LoadSystem(rom);

                int[] ab   = ResolveQ("cpu.ab[15:0]");
                int[] db   = ResolveQ("cpu.db[7:0]");
                int[] ioAb = ResolveQ("ppu.io_ab[2:0]");
                int[] ioDb = ResolveQ("ppu.io_db[7:0]");
                int rw   = WireCore.LookupNode("cpu.rw");
                int clk0 = WireCore.LookupNode("cpu.clk0");
                int u3y1 = WireCore.LookupNode("u3.1/Y1");
                int u3y0 = WireCore.LookupNode("u3.1/Y0");
                int u3y3 = WireCore.LookupNode("u3.2/Y3");
                int ioCe = WireCore.LookupNode("ppu.io_ce");
                int inVbl= WireCore.LookupNode("ppu.in_vblank");
                int H1(int n) => n != WireCore.EmptyNode && WireCore.IsNodeHigh(n) ? 1 : 0;

                WireCore.RunFrame();
                Console.WriteLine($"# at vblank start: t={WireCore.Time}  in_vblank={H1(inVbl)}  {WireCore.DumpCpuState()}");

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

        // ── --dump-node: introspect one node's pull-up / gated transistors / channel-end transistors ──
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

        // ── --probe-vbl: trace the 2C02 latched vblank flag through the $2002 read path ──
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
                int[] hp = ResolveQ("ppu.hpos[8:0]"), vp = ResolveQ("ppu.vpos[8:0]"), ioDb = ResolveQ("ppu.io_db[7:0]"), ab = ResolveQ("cpu.ab[15:0]");
                int H1(int n) => n != WireCore.EmptyNode && WireCore.IsNodeHigh(n) ? 1 : 0;
                int Rd(int[] a) => WireCore.ReadBits(a);

                Console.WriteLine($"# node ids: in_vblank={inVbl} vbl_flag={vblF} /vbl_flag={nVblF} set_vbl_flag={setVbl} read_2002_output_vblank_flag={rdOut} /r2002={nR2002}");

                int nRdOut = WireCore.LookupNode("ppu./read_2002_output_vblank_flag");
                int nVblOut = WireCore.LookupNode("ppu./vbl_flag_out");
                int nBuf   = WireCore.LookupNode("ppu./vbl_flag_read_buffer");
                int bufOut = WireCore.LookupNode("ppu.vbl_flag_read_buffer_out");
                int ioDb7  = WireCore.LookupNode("ppu._io_db7");
                int ioCe2  = WireCore.LookupNode("ppu._io_ce");
                int clk0n  = WireCore.LookupNode("cpu.clk0");

                WireCore.RunFrame();
                for (long i = 0; i < 400_000 && H1(vblF) == 0; i++) WireCore.Step(1);
                Console.WriteLine($"# vbl_flag set at t={WireCore.Time} vpos={Rd(vp)} hpos={Rd(hp)} — tracing 160 half-cycles");
                Console.WriteLine("# per half-cycle — t hpos | r_out /r_out vbl_flag /vbl_flag /vbl_out /buf bufOut | /r2002 _io_ce _io_db7 io_db | cpu.ab clk0");
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

        // ── --test / --test-dir: run to blargg $6000 signature; print PASS/FAIL ──
        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            try
            {
                WireCore.LoadSystem(rom);

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

        // ── --trace: step N 6502 cycles and dump the CPU's named state each cycle ──
        private static int Trace(string path, int cycles)
        {
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {path}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(path)}  (PRG {rom.PrgRom.Length / 1024} KB, CHR {rom.ChrRom.Length / 1024} KB, mapper {rom.Mapper})");
            try
            {
                WireCore.LoadSystem(rom);
                Console.WriteLine($"# after power-on reset: {WireCore.DumpCpuState()}");
                int instrCount = 0;
                for (int c = 0; c < cycles; c++)
                {
                    WireCore.Step(12 * 2);
                    string line = WireCore.DumpCpuState();
                    bool sync = line.Contains("(fetch)");
                    if (sync) instrCount++;
                    Console.WriteLine($"  cyc {c + 1,5}  {line}");
                    if (c > 12 && WireCore.Time == 0) break;
                }
                Console.WriteLine($"# {instrCount} opcode-fetch cycle(s) observed in {cycles} CPU cycles ({WireCore.Time} half-cycles)");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                AprVisual.S1 — switch-level NES (clean S1 fork)

                  AprVisual.S1 --rom <game.nes>            show a window for that ROM
                  AprVisual.S1 --trace <rom> [--cycles N]  headless: power-on reset, step N 6502 cycles, dump CPU state each cycle (default N=64)
                  AprVisual.S1 --screenshot <rom> [--frames N] [--out p.png]   headless: run N frames, dump the framebuffer to a PNG (default N=3)
                  AprVisual.S1 --ppu-dump <rom> [--frames N]   headless: run N frames, then dump palette RAM / VRAM nametable / rendering state / pclk1 samples
                  AprVisual.S1 --benchmark <rom> [--frames N]  headless throughput: simulated FPS, MIPS, raw step rate (default N=12; Release build recommended)
                  AprVisual.S1 --benchmark <rom> --bench-hc <N>   headless throughput: time exactly N raw master-half-cycles
                  AprVisual.S1 --test <test.nes>           headless: run to the $6000 signature, print PASS/FAIL
                  AprVisual.S1 --test-dir <dir>            headless: batch-run *.nes under <dir>
                    [--max-wait <sec>]                     timeout per test (default 15)
                    [--region ntsc|pal|dendy]
                    [--benchmark]                          also time each test
                  AprVisual.S1 --dump-module <name>        parse <system-def-dir>/<name>.js and print a summary
                  AprVisual.S1 --dump-system               compose the full nes-001 + cart netlist and print counts + probes
                  AprVisual.S1 --dump-node <name>          introspect one node (pull-up / gated trans / channel-end trans)
                  AprVisual.S1 --probe2002 <rom>           trace bus/PPU signals at the next $2002 read after vblank
                  AprVisual.S1 --probe-vbl <rom>           trace the 2C02 vbl flag latch through the $2002 read path
                  AprVisual.S1 --selftest                  run hand-built inverter/NAND/pass/callback/static-merge circuits

                Diagnostic flags (compose with the above):
                    [--system-def-dir <dir>]               default: data/system-def
                    [--no-lower]                           skip the S1.5 netlist-lowering pass (A/B compare)
                    [--chip-diag]                          per-chip BFS-walk diagnostic
                    [--settle-stats]                       ProcessQueue iteration histogram
                    [--dead-end-diag]                      per-node BFS visit count; reports wasted-work candidates
                    [--count-events]                       count EnqueueNode + RecalcNode hits (measures D)
                    [--fast-path]                          no-op (fast-path is always on in S1)

                  (no args)                                open an empty window
                """);
        }
    }
}
