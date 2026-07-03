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
    //    Diagnostic flags: --no-lower (lowering A/B compare). --fast-path accepted as no-op.
    internal static class TestRunner
    {
        private static string? _dumpStatesPath;   // DIAGNOSTIC: --dump-states output path (per-node states after bench)
        private static bool _dumpArrayFootprint;   // --array-footprint: print hot unmanaged-array base+size at bench setup (for IBS/SPE data-address bucketing)
        private static bool _pinned;               // --pin: hot thread pinned + priority raised (recorded in bench log)

        // ── test-ROM validation options (--test / --test-dir; see MD/testrom_workflow/) ──
        private static int _testMaxFrames = 900;          // --max-frames: simulation-frame budget — the primary limit (switch-level ≈ 5 s wall/frame)
        private static string? _testJsonPath;             // --test-json: structured per-test result JSON (consumed by tools/testrom/build_report.py)
        private static string? _testShotPath;             // --test-screenshot: final-frame PNG for the report page
        private static HashSet<string>? _expectedCrcs;    // --expected-crc: C-class screen-CRC compare (comma-separated accept set)
        private static bool _screenVerdict;               // --screen-verdict: B-class per-frame nametable scan for terminal Passed/Failed/$0X markers
        private static int _testShotDelay;                // --shot-delay: extra frames AFTER the verdict before the screenshot (cosmetic —
                                                          // some ROMs keep rendering disabled until after publishing the verdict bytes)

        public static int Run(string[] args)
        {
            string? romPath = null, testPath = null, testDir = null;
            string? dumpModule = null, tracePath = null, shotPath = null, ppuDumpPath = null;
            string? probePath = null, probeVblPath = null, dumpNodeName = null, benchPath = null;
            string? frameDumpPath = null, payloadHistPath = null, fcTaintPath = null, namesArg = null;
            string? phaseProbePath = null;   // --phase-probe: per-hc cpu/ppu clock-phase dump (phase-alignment experiment)
            string? rdyProbePath = null;     // --rdy-probe: per-frame cpu.rdy transition counts (DMC-DMA study)
            string? busTracePath = null;     // --bus-trace: $4013/$4015 + RDY-stall cycle microscope (DMC #19 study)
            // diagnostic: dump per-node states after the bench run (set via --dump-states)
            string systemDefDir = WireCore.SystemDefDir;
            string shotOut = "screenshot.png";
            string frameOutDir = "frames";
            string logDir = "log";
            int maxWait = 0;    // --max-wait N: wall-clock SAFETY cap in seconds (0 = disabled; --max-frames is the primary limit)
            int traceCycles = 64;
            int shotFrames = 3;
            int frameDumpCount = 50;
            int benchHcCount = 0;
            string region = "ntsc";
            bool benchmark = false, dumpSystem = false;
            bool pin = false; int pinCore = -1;   // --pin [N]: pin hot thread (N = force logical core; absent = auto best P-core)

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
                    case "--frame-dump":      if (i + 1 < args.Length) frameDumpPath = args[++i]; break;   // per-frame PNG dump w/ progress + timing
                    case "--frame-count":     if (i + 1 < args.Length) int.TryParse(args[++i], out frameDumpCount); break;
                    case "--out-dir":         if (i + 1 < args.Length) frameOutDir  = args[++i]; break;
                    case "--ppu-dump":        if (i + 1 < args.Length) ppuDumpPath  = args[++i]; break;
                    case "--probe2002":       if (i + 1 < args.Length) probePath    = args[++i]; break;
                    case "--probe-vbl":       if (i + 1 < args.Length) probeVblPath = args[++i]; break;
                    case "--dump-node":       if (i + 1 < args.Length) dumpNodeName = args[++i]; break;
                    case "--frames":          if (i + 1 < args.Length) int.TryParse(args[++i], out shotFrames); break;
                    case "--out":             if (i + 1 < args.Length) shotOut      = args[++i]; break;
                    case "--dump-module":     if (i + 1 < args.Length) dumpModule   = args[++i]; break;
                    case "--dump-system":     dumpSystem = true; break;
                    case "--payload-hist":    if (i + 1 < args.Length) payloadHistPath = args[++i]; break;   // NodeInfo inline-payload size distribution (16B-pack study)
                    case "--fc-taint-stats":  if (i + 1 < args.Length) fcTaintPath = args[++i]; break;        // same-state-prune eligibility: FC-free vs FC-tainted channel components (diagnostic only)
                    case "--dump-states":     if (i + 1 < args.Length) _dumpStatesPath = args[++i]; break;    // DIAGNOSTIC: write per-node states after bench for A/B diffing
                    case "--array-footprint": _dumpArrayFootprint = true; break;                              // print hot unmanaged-array base+size at setup (IBS/SPE bucketing)
                    case "--names":           if (i + 1 < args.Length) namesArg = args[++i]; break;           // DIAGNOSTIC: id1,id2,... -> names (uses LoadSystem, keeps name map)
                    case "--selftest":        return SelfTest();
                    case "--system-def-dir":  if (i + 1 < args.Length) systemDefDir = args[++i]; break;
                    case "--no-lower":        WireCore.EnableLowering = false; break;
                    case "--extra-ram":       WireCore.ForceExtraRam = true; break;   // force cart-extraram (match Rust snapshot checksum)
                    case "--log-dir":         if (i + 1 < args.Length) logDir = args[++i]; break;   // benchmark JSON log output dir
                    case "--bench-hc":        if (i + 1 < args.Length) int.TryParse(args[++i], out benchHcCount); break;
                    case "--max-wait":        if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--max-frames":      if (i + 1 < args.Length) int.TryParse(args[++i], out _testMaxFrames); break;   // test mode: simulation-frame budget
                    case "--test-json":       if (i + 1 < args.Length) _testJsonPath = args[++i]; break;                      // test mode: per-test result JSON
                    case "--test-screenshot": if (i + 1 < args.Length) _testShotPath = args[++i]; break;                      // test mode: final-frame PNG
                    case "--expected-crc":                                                                                     // test mode: C-class CRC accept set
                        if (i + 1 < args.Length)
                        {
                            _expectedCrcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string c in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                _expectedCrcs.Add(c);
                        }
                        break;
                    case "--screen-verdict":  _screenVerdict = true; break;                                                    // test mode: B-class screen-text detection
                    case "--shot-delay":      if (i + 1 < args.Length) int.TryParse(args[++i], out _testShotDelay); break;    // test mode: post-verdict frames before screenshot
                    case "--reset-hold-extra": if (i + 1 < args.Length) { int.TryParse(args[++i], out int _rhe); WireCore.ResetHoldExtraHc = _rhe; } break;   // phase experiment
                    case "--phase-probe":     if (i + 1 < args.Length) phaseProbePath = args[++i]; break;                     // DIAGNOSTIC: per-hc clock-phase dump
                    case "--rdy-probe":       if (i + 1 < args.Length) rdyProbePath = args[++i]; break;                       // DIAGNOSTIC: rdy transition counts
                    case "--bus-trace":       if (i + 1 < args.Length) busTracePath = args[++i]; break;                       // DIAGNOSTIC: DMC bus microscope
                    case "--region":          if (i + 1 < args.Length) region       = args[++i].ToLowerInvariant(); break;
                    case "--fast-path":       /* no-op: always on in S1 */ break;
                    case "--pin":             // pin hot thread + High priority + disable EcoQoS (opt-in, for clean bench numbers)
                        pin = true;
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out int _pc)) { pinCore = _pc; i++; }
                        break;
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

            if (pin)
            {
                // Thread-pin (not process) + High priority + EcoQoS-off. Cuts run-to-run variance for the
                // memory-latency-bound hot loop by stopping core migration from trashing L1/L2. Opt-in only;
                // status is printed and recorded in the bench JSON ("pinned"). See Sim/PerfTuning.cs.
                _pinned = true;
                Console.WriteLine($"# [perf] {Sim.PerfTuning.Apply(pinCore)}");
            }

            if (dumpModule    != null) return DumpModule(systemDefDir, dumpModule);
            if (dumpSystem)            return DumpSystem();
            if (payloadHistPath != null) return PayloadHist(payloadHistPath);
            if (fcTaintPath   != null) return FcTaintStats(fcTaintPath);
            if (namesArg      != null) return NamesLookup(namesArg);
            if (tracePath     != null) return Trace(tracePath, traceCycles);
            if (shotPath      != null) return Screenshot(shotPath, shotFrames, shotOut);
            if (frameDumpPath != null) return FrameDump(frameDumpPath, frameDumpCount, frameOutDir);
            if (ppuDumpPath   != null) return PpuDump(ppuDumpPath, shotFrames);
            if (phaseProbePath != null) return PhaseProbe(phaseProbePath);
            if (rdyProbePath  != null) return RdyProbe(rdyProbePath, shotFrames > 3 ? shotFrames : 35);
            if (busTracePath  != null) return BusTrace(busTracePath, shotFrames > 3 ? shotFrames : 29);
            if (probePath     != null) return Probe2002(probePath);
            if (probeVblPath  != null) return ProbeVbl(probeVblPath);
            if (dumpNodeName  != null) return DumpNode(dumpNodeName);
            if (benchPath     != null && benchHcCount > 0) return BenchmarkHalfCycles(benchPath, benchHcCount, logDir);
            if (benchPath     != null) return Benchmark(benchPath, shotFrames);

            if (romPath != null)
            {
                // S1 is headless-only (the live WinForms window was removed). Treat a bare ROM
                // path as "give me a quick screenshot" so it still does something useful.
                Console.Error.WriteLine($"# (headless build) no GUI — rendering 3 frames of {Path.GetFileName(romPath)} to screenshot.png");
                return Screenshot(romPath, 3, "screenshot.png");
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

        // ── --payload-hist: NodeInfo inline-payload size distribution (for the 16B-pack study) ──
        private static unsafe int PayloadHist(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            double P(long x, long t) => t == 0 ? 0 : 100.0 * x / t;
            try
            {
                WireCore.LoadSystem(rom);
                int n = WireCore.NodeCount, inlineN = 0, overflow = 0, nullN = 0;
                var hist = new int[16];   // payload = 2*C1c2Count + GndCount + PwrCount (inline nodes)
                for (int nn = 0; nn < n; nn++)
                {
                    if (WireCore.Nodes[nn] == null) { nullN++; continue; }
                    var ns = WireCore.NodeInfos[nn];
                    if (ns.Inline != 0) { inlineN++; int p = 2 * ns.C1c2Count + ns.GndCount + ns.PwrCount; if (p < hist.Length) hist[p]++; }
                    else overflow++;
                }
                Console.WriteLine($"# ===== NodeInfo payload-size distribution ({Path.GetFileName(romPath)}) =====");
                Console.WriteLine($"#  live nodes {n - nullN:N0}  (inline {inlineN:N0} / overflow {overflow:N0})   InlineCap={NodeInfo.InlineCap}");
                for (int p = 0; p <= 8 && p < hist.Length; p++)
                    Console.WriteLine($"#   payload={p,2}: {hist[p],6:N0}  ({P(hist[p], inlineN):F1}% of inline)");
                Console.WriteLine("# ============================================================");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --names: map a comma-separated list of node ids to names (diagnostic) ──
        private static int NamesLookup(string ids)
        {
            // needs the netlist + name map; use ComposeSystem (LoadSystem path keeps _nameByNode).
            var rom = NesRom.LoadFromFile("AprVisualBenchMark/roms/full_palette.nes");
            if (rom is null) { Console.Error.WriteLine("failed to load ROM for name lookup"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                foreach (var s in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (int.TryParse(s, out int id)) Console.WriteLine($"{id}\t{WireCore.GetNodeName(id)}");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --fc-taint-stats: same-state-prune eligibility (FC-free vs FC-tainted channel components) ──
        private static int FcTaintStats(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                Console.WriteLine(WireCore.FcTaintStats());
                return 0;
            }
            finally { WireCore.Shutdown(); }
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
                    AprVisual.Render.PngWriter.Write(outPath, WireCore.FrameBuffer, WireCore.ScreenW, WireCore.ScreenH);
                }
                Console.WriteLine($"# wrote {outPath}  ({WireCore.ScreenW}x{WireCore.ScreenH}, {WireCore.Time} half-cycles total)");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --frame-dump: render N frames, save EACH frame as frame_NNN.png into outDir,
        //    printing per-frame progress + wall-clock time. (--frame-count N, --out-dir DIR) ──
        private static int FrameDump(string romPath, int frameCount, string outDir)
        {
            if (frameCount < 1) frameCount = 50;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Directory.CreateDirectory(outDir);
            Console.WriteLine($"# frame-dump: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper})");
            Console.WriteLine($"# rendering {frameCount} frame(s) -> {Path.GetFullPath(outDir)}");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                swLoad.Stop();
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");

                double totalSecs = 0;
                for (int f = 1; f <= frameCount; f++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    WireCore.RunFrame();
                    sw.Stop();
                    double secs = sw.Elapsed.TotalSeconds;
                    totalSecs += secs;

                    string outPath = Path.Combine(outDir, $"frame_{f:D4}.png");
                    unsafe
                    {
                        if (WireCore.FrameBuffer == null) { Console.Error.WriteLine("no FrameBuffer"); return 2; }
                        AprVisual.Render.PngWriter.Write(outPath, WireCore.FrameBuffer, WireCore.ScreenW, WireCore.ScreenH);
                    }
                    Console.WriteLine($"# frame {f,4}/{frameCount}  done in {secs,6:F2} s  ->  frame_{f:D4}.png");
                    Console.Out.Flush();
                }
                Console.WriteLine($"# =============================================");
                Console.WriteLine($"#  {frameCount} frames in {totalSecs:F1} s  (avg {totalSecs / frameCount:F2} s/frame, {frameCount / totalSecs:F3} fps)");
                Console.WriteLine($"#  output dir: {Path.GetFullPath(outDir)}");
                Console.WriteLine($"# =============================================");
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
        public static int BenchmarkHalfCycles(string romPath, int hcCount, string logDir = "log")
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
                // Memory hygiene before the hot path: LoadSystem's ClearPostLoadBuildState already freed
                // the build graph (~25-50 MB); release the residual name maps + Node shells the hot loop
                // never touches, then force a final compacting Gen2 GC so no collection can fire mid-
                // measurement. Hot data is unmanaged (NodeStates/NodeInfos/TransistorList/...), so this is
                // timing-stability hygiene + minimum resident heap, not a throughput change.
#if DEBUG
                WireCore.InitBoundaryDiag();   // cpu/ppu boundary profiler — BEFORE ReleaseBenchResidualState frees the name maps
#endif
                WireCore.ReleaseBenchResidualState();
                System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
                // [array-footprint] hot-array base + byte size — for cache-miss data-address bucketing
                // (ARM SPE / AMD IBS) + footprint analysis (which arrays exceed L1d/L2). Printed once at
                // setup, zero hot-path cost. Opt-in via --array-footprint (works in Release, for IBS/SPE
                // data-address bucketing); sizes are config-independent. Pointer access needs unsafe.
                if (_dumpArrayFootprint)
                unsafe
                {
                    int nc = WireCore.NodeCount;
                    void Fp(string name, ulong b, long bytes) =>
                        Console.WriteLine($"#   {name,-18} 0x{b:X12}..0x{b + (ulong)bytes:X12} {bytes / 1024.0,9:F1} KB");
                    Console.WriteLine($"# [array-footprint] NodeCount={nc:N0}  (A76: L1d=64KB, L2=512KB/core)");
                    Fp("NodeStates",        (ulong)WireCore.NodeStates,        nc);
                    Fp("NodeInfos",         (ulong)WireCore.NodeInfos,         (long)nc * 16);
                    Fp("RecalcList",        (ulong)WireCore.RecalcList,        (long)nc * 4);
                    Fp("RecalcListNext",    (ulong)WireCore.RecalcListNext,    (long)nc * 4);
                    Fp("RecalcHash",        (ulong)WireCore.RecalcHash,        nc);
                    Fp("RecalcHashNext",    (ulong)WireCore.RecalcHashNext,    nc);
                    Fp("NodeTlistGates",    (ulong)WireCore.NodeTlistGates,    (long)nc * 4);
                    Fp("NodeTlistGatesOff", (ulong)WireCore.NodeTlistGatesOff, (long)nc * 4);
                    Fp("IsPureLogic",       (ulong)WireCore.IsPureLogic,       nc);
                    Fp("TransistorList",    (ulong)WireCore.TransistorList,    (long)WireCore.TransistorListLength * 2);
                    Fp("TransistorListOff", (ulong)WireCore.TransistorListOff, (long)WireCore.TransistorListOffLength * 2);
                    Fp("FlagsToState",      (ulong)WireCore.FlagsToState,      256);
                }
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
                Console.WriteLine($"# {WireCore.LastPruneTaintStats}");
                Console.WriteLine($"# {WireCore.LastTurnOffSkipStats}");
                Console.WriteLine($"# {WireCore.LastRenumberStats}");
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {halfCycles:N0} master half-cycles in {secs:F3} s");
                Console.WriteLine($"# rate: {stepsHz:N0} hc/s ({secs * 1e6 / halfCycles:F2} µs/hc)");
                var (bv, bd) = BuildVersion();
                Console.WriteLine($"# engine: csharp  version: {bv} ({bd})");
                Console.WriteLine($"# NodeStates checksum @ t={WireCore.Time}: 0x{stateHash:X16}  (A/B equivalence: must match the baseline run)");
#if DEBUG
                {
                    // wasted-pop profiler (DEBUG only; counts identical to Release). See WireCore.Recalc.cs.
                    double W(long x) => WireCore.DiagNoChange == 0 ? 0 : 100.0 * x / WireCore.DiagNoChange;
                    Console.WriteLine($"# [waste-profile] pops={WireCore.DiagPops:N0} no-change={WireCore.DiagNoChange:N0} ({(WireCore.DiagPops == 0 ? 0 : 100.0 * WireCore.DiagNoChange / WireCore.DiagPops):F1}%)  (% of waste:)");
                    Console.WriteLine($"#   FloatSingle={WireCore.DiagNCFloatSingle:N0}({W(WireCore.DiagNCFloatSingle):F1}%) FloatMulti={WireCore.DiagNCFloatMulti:N0}({W(WireCore.DiagNCFloatMulti):F1}%,capLTall={WireCore.DiagNCFloatMultiCapLT:N0}) PullUp={WireCore.DiagNCPullUp:N0}({W(WireCore.DiagNCPullUp):F1}%) Supply={WireCore.DiagNCSupply:N0}({W(WireCore.DiagNCSupply):F1}%) Other={WireCore.DiagNCOther:N0}({W(WireCore.DiagNCOther):F1}%)");
                    double Pp(long x) => WireCore.DiagPops == 0 ? 0 : 100.0 * x / WireCore.DiagPops;
                    Console.WriteLine($"# [P-2b candidate] single-channel pure-PullUp class: pops={WireCore.DiagP2bPops:N0} ({Pp(WireCore.DiagP2bPops):F1}% of all) no-change={WireCore.DiagNCP2b:N0} skippable(state==1)={WireCore.DiagNCP2bState1:N0} ({Pp(WireCore.DiagNCP2bState1):F1}% of all pops)");
                    Console.WriteLine($"# [B1 pair-path] pops resolved inline as 2-node groups: {WireCore.DiagPairPath:N0} ({Pp(WireCore.DiagPairPath):F1}% of all pops)");
                    {
                        // [fast-gate dist] DEBUG-only: gnd/pwr/c1c2 gate-count distribution of fast-path
                        // (RecalcNodeFast) pops — sizes Gemini's "Design 1 fixed 2gnd+2pwr+1c1c2" MLP idea.
                        long fp = WireCore.DiagFastPops;
                        double Fp(long x) => fp == 0 ? 0 : 100.0 * x / fp;
                        string Hist(long[] h) { var s = new System.Text.StringBuilder(); for (int i = 0; i < h.Length; i++) if (h[i] != 0) s.Append($" {(i==7?"7+":i.ToString())}:{Fp(h[i]):F1}%"); return s.ToString(); }
                        Console.WriteLine($"# [fast-gate dist] fast-path pops={fp:N0} ({Pp(fp):F1}% of all pops)  inline={Fp(WireCore.DiagFastInline):F1}%  FITS fixed(c1c2<=1,gnd<=2,pwr<=2)={WireCore.DiagFastFitsFixed:N0} ({Fp(WireCore.DiagFastFitsFixed):F1}%)");
                        Console.WriteLine($"#   GndCount:{Hist(WireCore.DiagFastGnd)}");
                        Console.WriteLine($"#   PwrCount:{Hist(WireCore.DiagFastPwr)}");
                        Console.WriteLine($"#   C1c2Count:{Hist(WireCore.DiagFastC1c2)}");
                    }
                    {
                        // [branch-dist] step-0 of the mem-latency branch: direction split of the hot
                        // DATA-DEPENDENT branches → locate the ~6 MPKI branch-miss source. ~50/50 = high
                        // entropy = likely mispredicted; lopsided = the predictor handles it cheaply.
                        long c0 = WireCore.DiagBrCls[0], c1c = WireCore.DiagBrCls[1], c2c = WireCore.DiagBrCls[2];
                        long clsT = c0 + c1c + c2c;
                        double Cp(long x) => clsT == 0 ? 0 : 100.0 * x / clsT;
                        string Split(long a, long b) { long t = a + b; return t == 0 ? "n/a" : $"{100.0 * a / t:F1}% / {100.0 * b / t:F1}%  (n={t:N0})"; }
                        Console.WriteLine($"# [branch-dist] (DEBUG; ~50/50 = high-entropy = likely mispredicted)");
                        Console.WriteLine($"#   dispatch cls: 0(BFS)={Cp(c0):F1}% 1(static-fast)={Cp(c1c):F1}% 2(dyn-singleton)={Cp(c2c):F1}%  (n={clsT:N0})");
                        Console.WriteLine($"#   cls2 channels [allOFF->fast / someON->pair.BFS]: {Split(WireCore.DiagBrCls2Off, WireCore.DiagBrCls2On)}");
                        Console.WriteLine($"#   SetNodeState [turn-on / turn-off]: {Split(WireCore.DiagBrTurnOn, WireCore.DiagBrTurnOff)}");
                        Console.WriteLine($"#   turn-on enqueue prune [keep / skip] (per-transistor, hottest): {Split(WireCore.DiagBrPruneKeep, WireCore.DiagBrPruneSkip)}");
                        Console.WriteLine($"#   fast-path [drive(write) / float(no-op)]: {Split(WireCore.DiagBrFastDrive, WireCore.DiagBrFastFloat)}");
                    }
                    {
                        // [co-read B feasibility] can a NodeStates bitset mirror merge two scattered byte
                        // loads into one 64-bit word load? Only if the co-read node ids share a word (id>>6).
                        // Low % => Scheme B (+ co-read renumber) is dead.
                        double Rp(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
                        Console.WriteLine($"# [co-read B] rising (c1,c2) same 64-bit word: {Rp(WireCore.DiagCoRiseSame, WireCore.DiagCoRiseTot):F1}% (n={WireCore.DiagCoRiseTot:N0})  |  fast-path >=2-gate pops all-in-1-word: {Rp(WireCore.DiagCoFast1Word, WireCore.DiagCoFastMulti):F1}% (n={WireCore.DiagCoFastMulti:N0})");
                    }
                    {
                        // [hotpath-calls] call count at each hot-path-chain method entry: count, % of all
                        // chain calls, and calls per RecalcNode pop — shows where the per-pop work goes.
                        long pq = WireCore.HpProcessQueue, rn = WireCore.HpRecalcNode, rf = WireCore.HpRecalcNodeFast,
                             cg = WireCore.HpComputeNodeGroup, ag = WireCore.HpAddNodeToGroup, gv = WireCore.HpGetNodeValue, ss = WireCore.HpSetNodeState;
                        long tot = pq + rn + rf + cg + ag + gv + ss;
                        double pcent(long a) => tot == 0 ? 0 : 100.0 * a / tot;
                        double perpop(long a) => rn == 0 ? 0 : (double)a / rn;
                        Console.WriteLine($"# [hotpath-calls] (DEBUG; count / % of all chain calls / per-RecalcNode-pop)  chain total={tot:N0}");
                        Console.WriteLine($"#   ProcessQueue     {pq,14:N0}  {pcent(pq),5:F1}%  {perpop(pq),7:F4}/pop  (coarse: per-half-cycle settle driver)");
                        Console.WriteLine($"#   RecalcNode       {rn,14:N0}  {pcent(rn),5:F1}%  {perpop(rn),7:F4}/pop  (= the pop count, the denominator)");
                        Console.WriteLine($"#   RecalcNodeFast   {rf,14:N0}  {pcent(rf),5:F1}%  {perpop(rf),7:F4}/pop  (O(1) singleton resolve)");
                        Console.WriteLine($"#   ComputeNodeGroup {cg,14:N0}  {pcent(cg),5:F1}%  {perpop(cg),7:F4}/pop  (slow BFS group build)");
                        Console.WriteLine($"#   AddNodeToGroup   {ag,14:N0}  {pcent(ag),5:F1}%  {perpop(ag),7:F4}/pop  (BFS walk)");
                        Console.WriteLine($"#   GetNodeValue     {gv,14:N0}  {pcent(gv),5:F1}%  {perpop(gv),7:F4}/pop  (group resolve LUT)");
                        Console.WriteLine($"#   SetNodeState     {ss,14:N0}  {pcent(ss),5:F1}%  {perpop(ss),7:F4}/pop  (write + enqueue)");
                    }
                }
                {
                    // settle-pass distribution (DEBUG only): how many settle waves each ProcessQueue() call
                    // took. Used to revisit the MaxSettlePasses safety cap (Release omits the cap). See
                    // WireCore.Recalc.cs SettlePassTally. Counts all ProcessQueue calls (clk + handler settles).
                    var h = WireCore.SettlePassHist;
                    long calls = WireCore.SettleCalls;
                    long total = 0, sum = 0; int maxIter = 0;
                    for (int i = 0; i < h.Length; i++) { total += h[i]; sum += h[i] * i; if (h[i] != 0) maxIter = i; }
                    double mean = total == 0 ? 0 : (double)sum / total;
                    // percentile helper: smallest pass-count whose cumulative share >= q
                    int Pct(double q) { long need = (long)System.Math.Ceiling(q * total); long cum = 0; for (int i = 0; i < h.Length; i++) { cum += h[i]; if (cum >= need) return i; } return maxIter; }
                    Console.WriteLine($"# [settle-pass-dist] ProcessQueue calls={calls:N0}  passes: mean={mean:F2} max={maxIter} | p50={Pct(0.50)} p90={Pct(0.90)} p99={Pct(0.99)} p99.9={Pct(0.999)} p99.99={Pct(0.9999)}");
                    Console.WriteLine("#   histogram (passes: count, %, cum%):");
                    double cumPct = 0;
                    for (int i = 0; i <= maxIter; i++)
                    {
                        if (h[i] == 0) continue;
                        double pct = total == 0 ? 0 : 100.0 * h[i] / total;
                        cumPct += pct;
                        Console.WriteLine($"#     {i,3}: {h[i],12:N0}  {pct,6:F2}%  {cumPct,6:F2}%");
                    }
                }
                {
                    // BFS group-walk DEPTH distribution (DEBUG only): max BFS level each AddNodeToGroup walk
                    // reached = hops through ON transistors from seed to the farthest conducting member.
                    // Depth 0 = singleton (no conducting neighbour). See WireCore.Group.cs / BfsDepthTally.
                    var h = WireCore.BfsDepthHist;
                    long walks = WireCore.BfsWalks;
                    long total = 0, sum = 0; int maxD = 0;
                    for (int i = 0; i < h.Length; i++) { total += h[i]; sum += h[i] * i; if (h[i] != 0) maxD = i; }
                    double mean = total == 0 ? 0 : (double)sum / total;
                    int Pct(double q) { long need = (long)System.Math.Ceiling(q * total); long cum = 0; for (int i = 0; i < h.Length; i++) { cum += h[i]; if (cum >= need) return i; } return maxD; }
                    Console.WriteLine($"# [bfs-depth-dist] AddNodeToGroup walks={walks:N0}  depth: mean={mean:F2} MAX={maxD} | p50={Pct(0.50)} p90={Pct(0.90)} p99={Pct(0.99)} p99.9={Pct(0.999)} p99.99={Pct(0.9999)}");
                    Console.WriteLine("#   histogram (depth: count, %, cum%):");
                    double cumPct = 0;
                    for (int i = 0; i <= maxD; i++)
                    {
                        if (h[i] == 0) continue;
                        double pct = total == 0 ? 0 : 100.0 * h[i] / total;
                        cumPct += pct;
                        Console.WriteLine($"#     {i,3}: {h[i],12:N0}  {pct,6:F2}%  {cumPct,6:F2}%");
                    }
                }
                {
                    // co-activity / cache-line headroom (DEBUG only) — the renumbering decision gate.
                    // headroom = mean distinct NodeInfo lines touched per half-cycle (current numbering)
                    // vs the perfect-packing floor ceil(distinctNodes/4). Near 1.0 ⇒ renumbering can't gain.
                    long w = WireCore.CoWindows;
                    if (w > 0)
                    {
                        double mPops = (double)WireCore.CoSumPops / w, mNodes = (double)WireCore.CoSumDistinctNodes / w, mLines = (double)WireCore.CoSumDistinctLines / w;
                        double ideal = mNodes / 4.0;
                        Console.WriteLine($"# [co-activity] windows={w:N0}  per-hc: pops={mPops:F1} distinct-nodes={mNodes:F1} NodeInfo-lines={mLines:F1} (ideal {ideal:F1}) headroom={(ideal > 0 ? mLines / ideal : 0):F2}x");
                        Console.WriteLine($"#   global hot set: {WireCore.CoGlobalNodes:N0} nodes ever popped, on {WireCore.CoGlobalLines:N0} lines ({WireCore.CoGlobalLines * 64 / 1024} KB of NodeInfos; ideal {(WireCore.CoGlobalNodes + 3) / 4:N0} lines = {((WireCore.CoGlobalNodes + 3) / 4) * 64 / 1024} KB)");
                        // (the --co-profile per-node dump was removed 2026-06-11 with the --renumber file
                        //  mode — the locality key is now SELF-CAPTURED at load; see WireCore.Renumber.cs.)
                    }
                }
                {
                    // CPU/PPU boundary profiler (DEBUG only) — PDES "split the two chips" feasibility.
                    // See WireCore.Recalc.cs InitBoundaryDiag / BoundaryPopTally.
                    long hc = halfCycles > 0 ? halfCycles : 1;
                    var cut = WireCore.DiagCut; var cr = WireCore.CutResolved;
                    double Ph(long x) => (double)x / hc;
                    Console.WriteLine($"# [cpu-ppu-boundary] over {halfCycles:N0} hc — cut-wire state-changes (per hc):");
                    Console.WriteLine($"#   data-bus db[7:0] (ids={cr[1]}): {cut[1]:N0} ({Ph(cut[1]):F4}/hc)  [also CPU<->RAM traffic — over-counts]");
                    Console.WriteLine($"#   reg-addr ab[2:0] (ids={cr[2]}): {cut[2]:N0} ({Ph(cut[2]):F4}/hc)  [also CPU bus — over-counts]");
                    Console.WriteLine($"#   rw line (ids={cr[5]}): {cut[5]:N0} ({Ph(cut[5]):F4}/hc)  [also CPU bus]");
                    Console.WriteLine($"#   *** io_ce PPU-select (ids={cr[3]}): {cut[3]:N0} ({Ph(cut[3]):F4}/hc)  <-- TRUE CPU<->PPU exchange trigger");
                    Console.WriteLine($"#   *** nmi PPU->CPU (ids={cr[4]}): {cut[4]:N0} ({Ph(cut[4]):F4}/hc)  <-- PPU->CPU coupling");
                    long both = WireCore.DiagHcBothChips, cpuO = WireCore.DiagHcCpuOnly, ppuO = WireCore.DiagHcPpuOnly, seen = WireCore.DiagHcSeen;
                    long quiet = hc - both - cpuO - ppuO;
                    double Pc(long x) => seen > 0 ? 100.0 * x / hc : 0;
                    Console.WriteLine($"#   per-hc chip activity: both={both:N0} ({Pc(both):F1}%) cpu-only={cpuO:N0} ({Pc(cpuO):F1}%) ppu-only={ppuO:N0} ({Pc(ppuO):F1}%) neither~={quiet:N0}");
                    Console.WriteLine($"#   total pops by domain: cpu={WireCore.DiagPopCpu:N0} ppu={WireCore.DiagPopPpu:N0} board={WireCore.DiagPopBoard:N0}");
                    // per-module pop histogram (which modules carry the work) — sorted desc.
                    var mn = WireCore.ModuleNames; var mp = WireCore.DiagModulePops;
                    if (mn != null && mp != null)
                    {
                        long tot = 0; foreach (var x in mp) tot += x;
                        var ord = new int[mn.Length];
                        for (int z = 0; z < ord.Length; z++) ord[z] = z;
                        System.Array.Sort(ord, (a, b) => mp[b].CompareTo(mp[a]));
                        Console.WriteLine($"#   pops by module (of {tot:N0} total):");
                        foreach (int k in ord) { if (mp[k] == 0) continue; Console.WriteLine($"#     {mn[k],-12} {mp[k],14:N0}  {(tot>0?100.0*mp[k]/tot:0),5:F1}%"); }
                        // PDES 2-way work balance + Amdahl ceiling under the SideOf() assignment.
                        long sN = WireCore.DiagSidePops[0], sC = WireCore.DiagSidePops[1], sP = WireCore.DiagSidePops[2];
                        double pc(long x) => tot > 0 ? 100.0 * x / tot : 0;
                        long heavy = System.Math.Max(sC, sP);
                        double ceilExcl = tot > 0 && heavy > 0 ? (double)tot / heavy : 0;            // neutral free
                        double ceilDup = tot > 0 && (heavy + sN) > 0 ? (double)tot / (heavy + sN) : 0; // neutral duplicated on both
                        Console.WriteLine($"#   PDES side split: cpu-side={sC:N0} ({pc(sC):F1}%) ppu-side={sP:N0} ({pc(sP):F1}%) neutral={sN:N0} ({pc(sN):F1}%)");
                        Console.WriteLine($"#   2-way speedup CEILING (sync-free, perfect): {ceilExcl:F2}x (neutral free) .. {ceilDup:F2}x (neutral on both)  [heavy side = {(sP>=sC?"PPU":"CPU")}]");
                    }
                }
#endif
                PrintRealtimeGap(stepsHz);
                WriteBenchLog(logDir, romPath, hcCount, halfCycles, secs, stepsHz, stateHash);
                if (_dumpStatesPath != null) DumpStates(_dumpStatesPath);
            }
            finally { WireCore.Shutdown(); }
            return 0;
        }

        // ── DIAGNOSTIC (--dump-states): write per-node state for A/B node-level diffing ──
        // (Nodes[] shells are freed by ReleaseBenchResidualState before the run; NodeStates is unmanaged
        //  and valid until Shutdown, so dump all NodeCount slots — empty slots read 0 on both sides.)
        private static unsafe void DumpStates(string path)
        {
            using var w = new StreamWriter(path);
            for (int nn = 0; nn < WireCore.NodeCount; nn++)
                w.WriteLine($"{nn} {WireCore.NodeStates[nn]}");
            Console.WriteLine($"# dumped {WireCore.NodeCount} node slots to {path}");
        }

        // NES NTSC runs at 1.789773 MHz CPU * 24 master-half-cycles/CPU-cycle = 42,954,552 hc/s,
        // i.e. 60.0988 frames/s * 714,732 hc/frame. Print how far our sim rate is from that.
        private const double NesRealtimeHcPerSec = 42_954_552.0;
        private const double NesRealtimeFps      = 60.0988;
        private const double NesHcPerFrame       = 714_732.0;   // 357,366 master clocks * 2 half-cycles
        private static void PrintRealtimeGap(double stepsHz)
        {
            double pct      = stepsHz / NesRealtimeHcPerSec * 100.0;
            double gap      = NesRealtimeHcPerSec / stepsHz;
            double fps      = stepsHz / NesHcPerFrame;            // simulated NES frames / real second
            double secPerFr = NesHcPerFrame / stepsHz;            // real seconds to render 1 NES frame
            Console.WriteLine($"# =============================================");
            Console.WriteLine($"#  PERFORMANCE: {stepsHz / 1000.0:F1}K hc/s  ({stepsHz:N0} hc/s)");
            Console.WriteLine($"#  vs NES NTSC real-time ({NesRealtimeHcPerSec / 1000.0:N0}K hc/s):");
            Console.WriteLine($"#    {pct:F3}% of real-time   ->   {gap:F1}x too slow");
            Console.WriteLine($"#    {fps:F3} simulated NES frames / real second  (real NES = {NesRealtimeFps:F1} fps)");
            Console.WriteLine($"#    {secPerFr:F2} s to render 1 frame  (real NES = {1.0 / NesRealtimeFps:F4} s/frame)");
            Console.WriteLine($"# =============================================");
        }

        // Build version embedded by the SetGitVersion MSBuild target (AssemblyMetadata): short git
        // commit + commit date, "-dirty" if built from uncommitted sources. Falls back to "unknown"
        // if absent (git unavailable at build time). Shown in bench output + written to the bench log.
        private static (string ver, string date) BuildVersion()
        {
            string ver = "unknown", date = "unknown";
            foreach (System.Reflection.AssemblyMetadataAttribute a in
                     typeof(TestRunner).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (a.Key == "GitVersion" && !string.IsNullOrEmpty(a.Value)) ver = a.Value!;
                else if (a.Key == "CommitDate" && !string.IsNullOrEmpty(a.Value)) date = a.Value!;
            }
            return (ver, date);
        }

        // ── Benchmark JSON log — one machine-parseable record per run, for a future
        //    "upload your result" aggregation mechanism. Console output is unchanged;
        //    this is an *additional* file: <logDir>/<machineGuid>-<user>-<UTCstamp>-csharp.log
        private static void WriteBenchLog(string logDir, string romPath, long benchHc, long halfCycles,
                                          double secs, double stepsHz, ulong checksum)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                string guid  = MachineGuid(logDir);
                string user  = Environment.UserName;
                var now      = DateTime.UtcNow;
                string stamp = now.ToString("yyyyMMddHHmmss");
                var (bv, bd) = BuildVersion();
                // <engine>-<stamp>-… so logs sort by engine, then chronologically.
                string file  = Path.Combine(logDir, $"csharp-{stamp}-{Safe(guid)}-{Safe(user)}.log");

                double pct      = stepsHz / NesRealtimeHcPerSec * 100.0;
                double gap      = NesRealtimeHcPerSec / stepsHz;
                double secPerFr = NesHcPerFrame / stepsHz;

                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"schema\": \"aprvisual-bench/2\",\n");
                sb.Append("  \"engine\": \"csharp\",\n");
                sb.Append($"  \"engineVersion\": \"{Esc(bv)}\",\n");
                sb.Append($"  \"commitDate\": \"{Esc(bd)}\",\n");
                sb.Append($"  \"timestampUtc\": \"{now:yyyy-MM-ddTHH:mm:ss}Z\",\n");
                sb.Append($"  \"machineGuid\": \"{Esc(guid)}\",\n");
                sb.Append($"  \"user\": \"{Esc(user)}\",\n");
                sb.Append($"  \"os\": \"{Esc(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}\",\n");
                sb.Append($"  \"arch\": \"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\",\n");
                sb.Append($"  \"cpuModel\": \"{Esc(CpuModel())}\",\n");
                sb.Append($"  \"cpuCount\": {Environment.ProcessorCount},\n");
                sb.Append($"  \"rom\": \"{Esc(Path.GetFileName(romPath))}\",\n");
                sb.Append($"  \"benchHc\": {benchHc},\n");
                sb.Append($"  \"halfCycles\": {halfCycles},\n");
                sb.Append($"  \"elapsedSec\": {secs:F6},\n");
                sb.Append($"  \"hcPerSec\": {stepsHz:F1},\n");
                sb.Append($"  \"secondsPerFrame\": {secPerFr:F4},\n");
                sb.Append($"  \"pctRealtime\": {pct:F4},\n");
                sb.Append($"  \"slowdownFactor\": {gap:F1},\n");
                sb.Append($"  \"checksum\": \"0x{checksum:X16}\",\n");
                sb.Append($"  \"fastPathNodes\": {WireCore.PureLogicNodeCount},\n");
                sb.Append($"  \"liveNodes\": {WireCore.NonNullNodeCount},\n");
                sb.Append($"  \"pinned\": {(_pinned ? "true" : "false")}\n");
                sb.Append("}\n");
                File.WriteAllText(file, sb.ToString());
                Console.WriteLine($"# log written: {file}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"# (bench log write skipped: {ex.Message})"); }
        }

        // Stable per-machine id, consistent across the C# and Rust engines on the same machine:
        //   Windows → registry MachineGuid;  macOS → IOPlatformUUID;  else → a GUID cached in logDir.
        private static string MachineGuid(string logDir)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string o = RunShell("reg", @"query HKLM\SOFTWARE\Microsoft\Cryptography /v MachineGuid");
                    foreach (var line in o.Split('\n'))
                    {
                        int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
                        if (idx >= 0) { string g = line.Substring(idx + 6).Trim(); if (g.Length > 0) return g; }
                    }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    string o = RunShell("ioreg", "-rd1 -c IOPlatformExpertDevice");
                    foreach (var line in o.Split('\n'))
                        if (line.Contains("IOPlatformUUID"))
                        {
                            int eq = line.IndexOf('='); int q1 = line.IndexOf('"', eq + 1); int q2 = line.IndexOf('"', q1 + 1);
                            if (q1 >= 0 && q2 > q1) return line.Substring(q1 + 1, q2 - q1 - 1);
                        }
                }
            }
            catch { }
            try
            {
                string f = Path.Combine(logDir, "machine.guid");
                if (File.Exists(f)) { string s = File.ReadAllText(f).Trim(); if (s.Length > 0) return s; }
                string g = Guid.NewGuid().ToString();
                File.WriteAllText(f, g);
                return g;
            }
            catch { return "unknown"; }
        }

        private static string CpuModel()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string o = RunShell("reg", @"query ""HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0"" /v ProcessorNameString");
                    foreach (var line in o.Split('\n'))
                    {
                        int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
                        if (idx >= 0) { string s = line.Substring(idx + 6).Trim(); if (s.Length > 0) return s; }
                    }
                }
                else if (OperatingSystem.IsMacOS())
                    return RunShell("sysctl", "-n machdep.cpu.brand_string").Trim();
            }
            catch { }
            return "unknown";
        }

        private static string RunShell(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return o;
        }

        private static string Safe(string s)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '.' && c[i] != '-' && c[i] != '_') c[i] = '_';
            return new string(c);
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') { sb.Append('\\'); sb.Append(ch); }
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            return sb.ToString();
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
                if (vram != null && vram.Length >= 64)
                {
                    sb = new StringBuilder("VRAM[0000..003F]:");
                    for (int i = 0; i < 64; i++) { if ((i & 15) == 0) sb.Append("  "); sb.Append(' ').Append(vram.Read(i).ToString("X2")); }
                    Console.WriteLine(sb);
                    int nzNt = 0, nzAt = 0;
                    int ntLen = Math.Min(0x3C0, vram.Length);
                    for (int i = 0; i < ntLen; i++) if (vram.Read(i) != 0) nzNt++;
                    for (int i = 0x3C0; i < 0x400 && i < vram.Length; i++) if (vram.Read(i) != 0) nzAt++;
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

        // ── --test / --test-dir: frame-based test-ROM validation. Detection (per simulated frame):
        //      1. blargg $6000 protocol (signature DE B0 61, status < 0x80 = done, 0 = pass)
        //      2. $6000 == 0x81 → auto soft reset (wait 6 frames, WireCore.SoftReset(), max 10× — apu_reset/cpu_reset)
        //      3. --expected-crc: scan nametable 0 (u4.ram, tile == ASCII) for an isolated 8-hex CRC,
        //         require 2 consecutive identical frames, compare against the accept set (dmc_dma visual tests)
        //    Budget: --max-frames (simulation frames, primary); --max-wait (wall seconds, safety, 0 = off).
        //    Outputs: console PASS/FAIL line (+ --test-json structured record, --test-screenshot final PNG).
        private static int RunOneTest(string path, int maxWait, string region, bool benchmark)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.WriteLine($"FAIL(load) | {Path.GetFileName(path)}"); return 2; }

            // Test mode emulates the conventional console power-up state (palette residue + P=$34;
            // documented shim — see WireCore.ApplyPowerUpState). Benchmarks never set this.
            WireCore.PowerUpStateShim = true;

            const int ResetDelayFrames = 6, MaxAutoResets = 10;
            string status = "timeout", detection = "none", resultText = "";
            int resultCode = -1, frames = 0, resetCount = 0;
            long hcRun = 0;
            double wallSecs = 0, loadSecs = 0;

            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                loadSecs = swLoad.Elapsed.TotalSeconds;
                var vram = (_expectedCrcs != null || _screenVerdict) ? WireCore.ResolveMemory("u4.ram") : null;

                // PPU open-bus decay shim (test mode only). The real 2C02's io-bus latch (the "decay
                // register") leaks to 0 in ~600 ms when not refreshed (ppu_open_bus readme: "some decay
                // sooner, depending on the NES and temperature"); floating netlist nodes hold forever.
                // Approximation: if the latch value is nonzero and unchanged for ~36 frames (~600 ms),
                // force it to 0 (drive low, settle, release — the dynamic node then float-holds 0).
                var ioDb = new List<int>();
                WireCore.ResolveNodes("ppu._io_db[7:0]", ioDb, quiet: true);   // _io_db = the io data-bus LATCH side (io_db is the live internal bus)
                int[] ioDbN = ioDb.Count == 8 ? ioDb.ToArray() : Array.Empty<int>();
                int ioPrev = -1, ioStable = 0;
                const int IoDecayFrames = 36;
                Console.Error.WriteLine($"# [shim] _io_db decay armed: resolved {ioDb.Count}/8 nodes");

                long t0 = WireCore.Time;
                int resetAtFrame = -1;
                bool waitRestart = false;      // after a soft reset, ignore the stale 0x81 until the ROM writes something else
                string? prevCrc = null;
                string? prevMarker = null;     // B-class: terminal marker seen last frame (2-frame confirm)
                var sw = System.Diagnostics.Stopwatch.StartNew();

                for (frames = 1; frames <= _testMaxFrames; frames++)
                {
                    WireCore.RunFrame();

                    // open-bus decay shim (see above): value-change resets the clock; same value for
                    // ~600 ms of simulated time ⇒ the charge leaks away on real silicon.
                    if (ioDbN.Length == 8)
                    {
                        int v = WireCore.ReadBits(ioDbN);
                        if (v != ioPrev) { ioPrev = v; ioStable = 0; }
                        else if (v != 0 && ++ioStable >= IoDecayFrames)
                        {
                            foreach (int n in ioDbN) WireCore.SetLow(n);
                            foreach (int n in ioDbN) WireCore.SetFloat(n);
                            int after = WireCore.ReadBits(ioDbN);
                            Console.Error.WriteLine($"# [shim] _io_db decay fired at frame {frames}: {v:X2} -> {after:X2}{(after != 0 ? "  (DID NOT STICK)" : "")}");
                            ioPrev = after; ioStable = 0;
                        }
                    }

                    var r = WireCore.CheckUnitTest();
                    if (r.Found)
                    {
                        if (r.Complete)   // status < 0x80 → final result
                        {
                            resultCode = r.Code;
                            resultText = r.Text.Trim();
                            detection  = resetCount > 0 ? "6000+reset" : "6000";
                            status     = r.Code == 0 ? "pass" : "fail";
                            break;
                        }
                        // status ≥ 0x80: 0x80 running, 0x81 requests a soft reset (blargg reset protocol)
                        if (r.Code == 0x81 && resetAtFrame < 0 && !waitRestart && resetCount < MaxAutoResets)
                            resetAtFrame = frames;
                        if (waitRestart && r.Code != 0x81)
                            waitRestart = false;
                        if (resetAtFrame >= 0 && frames >= resetAtFrame + ResetDelayFrames)
                        {
                            Console.WriteLine($"# [test] auto soft reset #{resetCount + 1} at frame {frames} ($6000=$81 at frame {resetAtFrame})");
                            WireCore.SoftReset();
                            resetAtFrame = -1;
                            waitRestart = true;
                            resetCount++;
                        }
                    }
                    else if (vram != null)
                    {
                        // No $6000 protocol — judge from the screen (nametable 0, tile == ASCII).
                        string nt = DecodeNametable(vram);

                        if (_expectedCrcs != null)
                        {
                            // C-class: terminal on-screen CRC vs the accept set.
                            string? crc = FindNametableCrc(nt);
                            if (crc != null && crc == prevCrc)   // same isolated 8-hex on 2 consecutive frames
                            {
                                bool ok    = _expectedCrcs.Contains(crc);
                                resultCode = ok ? 0 : 1;
                                resultText = $"screen CRC {crc} " + (ok ? "(matched)" : "(NOT in expected set)");
                                detection  = "crc";
                                status     = ok ? "pass" : "fail";
                                break;
                            }
                            prevCrc = crc;
                        }

                        if (_screenVerdict)
                        {
                            // B-class: terminal Passed/Failed text or old-blargg "$0X" code. Terminal =
                            // printed once and the ROM halts (calibration found no ROM that shows a
                            // marker while still drawing) — 2-frame confirm guards mid-draw snapshots.
                            int vc = FindNametableVerdict(nt, out string? marker);
                            if (vc >= 0 && marker != null && marker == prevMarker)
                            {
                                resultCode = vc;
                                resultText = $"(screen: {marker} = {(vc == 0 ? "passed" : "failed")})";
                                detection  = "screen";
                                status     = vc == 0 ? "pass" : "fail";
                                break;
                            }
                            prevMarker = vc >= 0 ? marker : null;
                        }
                    }

                    if (maxWait > 0 && sw.Elapsed.TotalSeconds > maxWait)
                    {
                        resultText = $"wall-clock safety cap ({maxWait}s) hit at frame {frames}";
                        break;
                    }
                }
                sw.Stop();
                wallSecs = sw.Elapsed.TotalSeconds;
                hcRun = WireCore.Time - t0;
                if (frames > _testMaxFrames) frames = _testMaxFrames;   // loop exits at budget+1

                if (status == "timeout")
                {
                    var rr = WireCore.CheckUnitTest();
                    if (resultText.Length == 0)
                        resultText = rr.Found ? $"budget exhausted, $6000=0x{rr.Code:X2}" : "budget exhausted, no $6000 signature";
                    resultCode = 3;
                }

                // --shot-delay: run extra frames after the verdict so the ROM can finish PRESENTING its
                // result (the dmc_dma family keeps rendering disabled while it works the $2007 bus and only
                // enables the screen after publishing the verdict text/CRC to VRAM — which the detection
                // reads directly, so the framebuffer lags). Cosmetic only: frames / wallSeconds /
                // halfCycles in the JSON stay at the verdict point.
                if (_testShotDelay > 0 && _testShotPath != null && status != "timeout")
                    for (int i = 0; i < _testShotDelay; i++) WireCore.RunFrame();

                // final-frame screenshot (for the report page)
                if (_testShotPath != null)
                {
                    try
                    {
                        string? dir = Path.GetDirectoryName(_testShotPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        unsafe
                        {
                            if (WireCore.FrameBuffer != null)
                                AprVisual.Render.PngWriter.Write(_testShotPath, WireCore.FrameBuffer, WireCore.ScreenW, WireCore.ScreenH);
                        }
                    }
                    catch (Exception ex) { Console.Error.WriteLine($"# (screenshot failed: {ex.Message})"); }
                }

                string label = status switch { "pass" => "PASS", "fail" => $"FAIL({resultCode})", _ => "TIMEOUT" };
                Console.WriteLine($"{label} | {Path.GetFileName(path)} | {name}");
                if (resultText.Length > 0) Console.WriteLine(resultText);
                Console.WriteLine($"# frames={frames} simSec={frames / 60.0988:F1} wallSec={wallSecs:F0} hc={hcRun:N0} detection={detection} resets={resetCount} load={loadSecs:F0}s");

                if (_testJsonPath != null)
                    WriteTestJson(path, status, resultCode, detection, resultText, frames, wallSecs, loadSecs, hcRun, resetCount);

                return status switch { "pass" => 0, "fail" => resultCode == 0 ? 1 : Math.Min(resultCode, 125), _ => 3 };
            }
            finally { WireCore.Shutdown(); }
        }

        // Nametable 0 (u4.ram bytes 0..0x3BF) decoded to ASCII — blargg's CHR maps tile == ASCII.
        private static string DecodeNametable(WireCore.Memory vram)
        {
            int n = Math.Min(0x3C0, vram.Length);
            var s = new char[n];
            for (int i = 0; i < n; i++)
            {
                byte t = vram.Read(i);
                s[i] = t is >= 0x20 and < 0x7F ? (char)t : ' ';
            }
            return new string(s);
        }

        private static bool IsHexChar(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');

        // Isolated 8-hex-digit string (a CRC32 print). Neighbours must be non-hex so a longer dump can't alias.
        private static string? FindNametableCrc(string s)
        {
            for (int i = 0; i + 8 <= s.Length; i++)
            {
                bool all = true;
                for (int k = 0; k < 8; k++) if (!IsHexChar(s[i + k])) { all = false; break; }
                if (!all) continue;
                if (i > 0 && IsHexChar(s[i - 1])) continue;
                if (i + 8 < s.Length && IsHexChar(s[i + 8])) continue;
                return s.Substring(i, 8).ToUpperInvariant();
            }
            return null;
        }

        // B-class terminal markers. Returns the result code (0 = pass, 1..15 = fail) and the marker
        // label, or -1 when nothing is visible. Two forms exist in the 46-ROM set (per the AprNesRef
        // calibration sweep): "Passed"/"Failed" text (30) and the old-blargg "$0X" code (16;
        // $01 = pass, $02-$0F = fail code).
        private static int FindNametableVerdict(string s, out string? marker)
        {
            if (s.Contains("Passed") || s.Contains("PASSED")) { marker = "Passed"; return 0; }
            if (s.Contains("Failed") || s.Contains("FAILED")) { marker = "Failed"; return 1; }
            for (int i = 0; i + 2 < s.Length; i++)
            {
                if (s[i] != '$' || s[i + 1] != '0') continue;
                char c = s[i + 2];
                if (c == '1') { marker = "$01"; return 0; }
                if (c is >= '2' and <= '9') { marker = "$0" + c; return c - '0'; }
                if (c is >= 'A' and <= 'F') { marker = "$0" + c; return 10 + (c - 'A'); }
                if (c is >= 'a' and <= 'f') { marker = "$0" + char.ToUpperInvariant(c); return 10 + (c - 'a'); }
            }
            marker = null;
            return -1;
        }

        // Structured per-test record — consumed by tools/testrom/build_report.py (schema aprvisual-testrom/1).
        private static void WriteTestJson(string romPath, string status, int resultCode, string detection,
                                          string resultText, int frames, double wallSecs, double loadSecs,
                                          long hcRun, int resetCount)
        {
            try
            {
                string? dir = Path.GetDirectoryName(_testJsonPath!);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var (bv, bd) = BuildVersion();
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"schema\": \"aprvisual-testrom/1\",\n");
                sb.Append($"  \"rom\": \"{Esc(Path.GetFileName(romPath))}\",\n");
                sb.Append($"  \"status\": \"{status}\",\n");
                sb.Append($"  \"resultCode\": {resultCode},\n");
                sb.Append($"  \"detection\": \"{detection}\",\n");
                sb.Append($"  \"resetCount\": {resetCount},\n");
                sb.Append($"  \"frames\": {frames},\n");
                sb.Append($"  \"maxFrames\": {_testMaxFrames},\n");
                sb.Append($"  \"simSeconds\": {frames / 60.0988:F2},\n");
                sb.Append($"  \"wallSeconds\": {wallSecs:F1},\n");
                sb.Append($"  \"loadSeconds\": {loadSecs:F1},\n");
                sb.Append($"  \"halfCycles\": {hcRun},\n");
                sb.Append($"  \"engineVersion\": \"{Esc(bv)}\",\n");
                sb.Append($"  \"commitDate\": \"{Esc(bd)}\",\n");
                sb.Append($"  \"pinned\": {(_pinned ? "true" : "false")},\n");
                sb.Append($"  \"timestampUtc\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}Z\",\n");
                sb.Append($"  \"screenshot\": \"{Esc(_testShotPath ?? "")}\",\n");
                sb.Append($"  \"resultText\": \"{Esc(resultText)}\"\n");
                sb.Append("}\n");
                File.WriteAllText(_testJsonPath!, sb.ToString());
            }
            catch (Exception ex) { Console.Error.WriteLine($"# (test json write failed: {ex.Message})"); }
        }

        // ── --bus-trace: microscope for the DMC #19 study. Fast-forwards N frames (--frames), then
        //    steps hc-by-hc for 2 frames detecting phi2 falling edges (CPU cycle boundaries) and logs
        //    every bus cycle touching $4013/$4015 plus every RDY-stalled cycle: relative cycle index,
        //    AB, DB, R/W, RDY. Shows the enable→first-fetch→readback chain cycle by cycle. ──
        private static int _btPrevIrq = -2, _btPrevSet = -2, _btPrevEn = -2, _btTail;
        private static unsafe int BusTrace(string romPath, int startFrame)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                int phi2 = WireCore.LookupNode("cpu.phi2");
                int rw   = WireCore.LookupNode("cpu.rw");
                int rdy  = WireCore.LookupNode("cpu.rdy");
                var ab = new List<int>(); WireCore.ResolveNodes("cpu.ab[15:0]", ab, quiet: true);
                var db = new List<int>(); WireCore.ResolveNodes("cpu.db[7:0]", db, quiet: true);
                int[] abN = ab.ToArray(), dbN = db.ToArray();
                if (phi2 == WireCore.EmptyNode || rw == WireCore.EmptyNode || abN.Length != 16 || dbN.Length != 8)
                { Console.Error.WriteLine($"bus-trace: node resolution failed (phi2={phi2} rw={rw} ab={abN.Length} db={dbN.Length})"); return 2; }
                // DMC IRQ microscope nodes (named in the 2A03 netlist)
                int nIrq = WireCore.LookupNode("cpu.pcm_irq");
                int nSet = WireCore.LookupNode("cpu.set_pcm_irq");
                int nEn  = WireCore.LookupNode("cpu.pcm_irqen");
                int nClk1 = WireCore.LookupNode("cpu.apu_clk1");
                int nClk2e = WireCore.LookupNode("cpu.apu_clk2e");
                int nAbp = WireCore.LookupNode("cpu.ab_use_pcm");
                int[] nLc = new int[12];
                for (int b = 0; b < 12; b++) nLc[b] = WireCore.LookupNode("cpu.pcm_lc" + b);
                Console.WriteLine($"# lc nodes: {string.Join(",", nLc)}");
                Console.WriteLine($"# pcm nodes: irq={nIrq} set={nSet} en={nEn} clk1={nClk1} clk2e={nClk2e} abp={nAbp}");

                Console.WriteLine($"# bus-trace: fast-forward {startFrame} frames, then 2 frames hc-stepped");
                for (int f = 0; f < startFrame; f++) WireCore.RunFrame();
                Console.WriteLine($"# --- tracing (cycle index = CPU cycles since trace start) ---");

                long cyc = 0, lastPrintedCyc = -999;
                int prevPhi = WireCore.NodeStates[phi2];
                const long HcSpan = 714_732L * 2;
                for (long i = 0; i < HcSpan; i++)
                {
                    WireCore.Step(1);
                    int ph = WireCore.NodeStates[phi2];
                    if (prevPhi == 1 && ph == 0)   // phi2 falling = CPU cycle boundary
                    {
                        cyc++;
                        int a = WireCore.ReadBits(abN);
                        int r = WireCore.NodeStates[rdy];
                        int vIrq = nIrq != WireCore.EmptyNode ? WireCore.NodeStates[nIrq] : -1;
                        int vSet = nSet != WireCore.EmptyNode ? WireCore.NodeStates[nSet] : -1;
                        int vEn  = nEn  != WireCore.EmptyNode ? WireCore.NodeStates[nEn]  : -1;
                        bool irqChanged = vIrq != _btPrevIrq || vSet != _btPrevSet || vEn != _btPrevEn;
                        _btPrevIrq = vIrq; _btPrevSet = vSet; _btPrevEn = vEn;
                        if (a == 0x4013 || a == 0x4015 || r == 0 || irqChanged) _btTail = 30;
                        if (_btTail > 0)
                        {
                            _btTail--;
                            int d = WireCore.ReadBits(dbN);
                            int w = WireCore.NodeStates[rw];
                            int c1 = nClk1 != WireCore.EmptyNode ? WireCore.NodeStates[nClk1] : -1;
                            int c2e = nClk2e != WireCore.EmptyNode ? WireCore.NodeStates[nClk2e] : -1;
                            int abp = nAbp != WireCore.EmptyNode ? WireCore.NodeStates[nAbp] : -1;
                            int lc = 0;
                            for (int b = 11; b >= 0; b--) lc = (lc << 1) | (nLc[b] != WireCore.EmptyNode ? WireCore.NodeStates[nLc[b]] : 0);
                            if (cyc - lastPrintedCyc > 1) Console.WriteLine("#   ...");
                            Console.WriteLine($"#  cyc {cyc,7}: AB={a:X4} DB={d:X2} {(w == 0 ? "W" : "r")} RDY={r}  irq={vIrq} set={vSet} en={vEn}  clk1={c1} clk2e={c2e} abp={abp} lc={lc:X3}");
                            lastPrintedCyc = cyc;
                        }
                    }
                    prevPhi = ph;
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --rdy-probe: per-frame count of cpu.rdy transitions (DMC/OAM DMA stall activity),
        //    stepping hc-by-hc so sub-frame RDY pulses are visible. DMC-DMA trace-study instrument. ──
        private static unsafe int RdyProbe(string romPath, int frameCount)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                int rdy = WireCore.LookupNode("cpu.rdy");
                if (rdy == WireCore.EmptyNode) { Console.Error.WriteLine("no cpu.rdy node"); return 2; }
                Console.WriteLine($"# rdy-probe: node {rdy}, initial state {WireCore.NodeStates[rdy]}");
                const long HcPerFrame = 714_732;
                int prev = WireCore.NodeStates[rdy];
                for (int f = 1; f <= frameCount; f++)
                {
                    int trans = 0; long lowHc = 0;
                    for (long i = 0; i < HcPerFrame; i++)
                    {
                        WireCore.Step(1);
                        int v = WireCore.NodeStates[rdy];
                        if (v != prev) { trans++; prev = v; }
                        if (v == 0) lowHc++;
                    }
                    Console.WriteLine($"# f{f,3}: rdy transitions={trans,6}  low-hc={lowHc,7}");
                    Console.Out.Flush();
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --phase-probe: bit-string dump of cpu.phi2 / ppu.pclk0 / ppu.pclk1 per half-cycle right
        //    after power-on reset. First instrument of the clock-phase-alignment experiment: shows
        //    whether --reset-hold-extra K shifts the CPU÷12 vs PPU÷4 divider alignment. ──
        private static unsafe int PhaseProbe(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.LoadSystem(rom);
                int phi2  = WireCore.LookupNode("cpu.phi2");
                int pclk0 = WireCore.LookupNode("ppu.pclk0");
                Console.WriteLine($"# phase-probe: resetHoldExtra={WireCore.ResetHoldExtraHc}");
                var phi = new StringBuilder(); var p0 = new StringBuilder();
                for (int i = 0; i < 48; i++)
                {
                    WireCore.Step(1);
                    phi.Append((char)('0' + WireCore.NodeStates[phi2]));
                    p0.Append((char)('0' + WireCore.NodeStates[pclk0]));
                }
                Console.WriteLine($"phi2 ={phi}");
                Console.WriteLine($"pclk0={p0}");
                return 0;
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

                  AprVisual.S1 --rom <game.nes>            headless: render 3 frames to screenshot.png (no GUI)
                  AprVisual.S1 --trace <rom> [--cycles N]  headless: power-on reset, step N 6502 cycles, dump CPU state each cycle (default N=64)
                  AprVisual.S1 --screenshot <rom> [--frames N] [--out p.png]   headless: run N frames, dump the framebuffer to a PNG (default N=3)
                  AprVisual.S1 --ppu-dump <rom> [--frames N]   headless: run N frames, then dump palette RAM / VRAM nametable / rendering state / pclk1 samples
                  AprVisual.S1 --benchmark <rom> [--frames N]  headless throughput: simulated FPS, MIPS, raw step rate (default N=12; Release build recommended)
                  AprVisual.S1 --benchmark <rom> --bench-hc <N>   headless throughput: time exactly N raw master-half-cycles
                  AprVisual.S1 --test <test.nes>           headless test-ROM validation: $6000 protocol (+auto soft-reset on $81)
                  AprVisual.S1 --test-dir <dir>            headless: batch-run *.nes under <dir>
                    [--max-frames <N>]                     simulation-frame budget, the primary limit (default 900 ≈ 15 sim-sec)
                    [--max-wait <sec>]                     wall-clock SAFETY cap (default 0 = disabled)
                    [--expected-crc <A,B,...>]             C-class: accept set for the on-screen CRC (dmc_dma visual tests)
                    [--screen-verdict]                     B-class: per-frame nametable scan for terminal Passed/Failed/$0X markers
                    [--test-json <out.json>]               write a structured per-test result record (for tools/testrom/)
                    [--test-screenshot <out.png>]          save the final frame as PNG (for the report page)
                    [--shot-delay <N>]                     run N extra frames after the verdict before the screenshot (cosmetic)
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
                    [--fast-path]                          no-op (fast-path is always on in S1)
                    [--pin [N]]                            cut bench variance: pin the hot thread + High priority + EcoQoS-off
                                                           (Windows; no arg = auto-pick the quietest P-core, N = force logical core N)

                  (no args)                                print this usage
                """);
        }
    }
}
