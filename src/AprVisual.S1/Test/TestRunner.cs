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
    internal static partial class TestRunner
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
        internal static bool _acVerdict;                  // --ac-verdict: AccuracyCoin unattended completion block in CPU RAM; implies NO cart-extraram (open-bus tests)
        private static bool _acDumpWork;                  // --ac-dump-work: dump AccuracyCoin scratch/results RAM $0500-$06FF at verdict
        internal static int _progressFrames;              // --progress-frames N: every N frames, checkpoint a screenshot + a status line (0 = off)
        internal static string? _progressDir;             // --progress-dir DIR: where those checkpoints land
        private static string? _passMarker;               // --pass-marker: custom terminal PASS text for ROMs that never print "Passed" (e.g. read_joy3 tallies)
        private static string? _inputSpec;                 // --input: scripted controller input "A:1.0,Start:4.0:0.5" (button:sec[:holdSec]; AprNes-compatible)
        private static string? _watchSpec;                 // --watch: node names to print per frame (--micro diagnostics)
        private static bool _noAluShim;                    // --no-alu-shim: A/B toggle (diagnostics)
        internal static bool _noDbl2007Shim;               // --no-dbl2007-shim: A/B toggle (diagnostics)
        internal static bool _noPpuAleReadFeedbackShim;    // --no-ppu-ale-read-feedback-shim: A/B the CHR analog-loop guard
        internal static int  _ppuWriteDelayHc = 16;        // $2001 write-effect delay in hc (even_odd; GLOBAL test-mode, --ppu-write-delay overrides, 0=off)
        internal static bool _oamDmaPpuBusShim = true;      // $4014-from-PPU-I/O-bus OAM write-data hold (GLOBAL test-mode; --no-oam-dma-ppu-bus-shim disables)
        internal static bool _noShims;                     // --no-shims: disable ALL test-mode shims (diagnostics)
        internal static bool _joypad;                      // --joypad: enable behavioral joypad + tie-rewire (per-test; perturbs graph)
        private static int _testShotDelay;                // --shot-delay: extra frames AFTER the verdict before the screenshot (cosmetic —
                                                          // some ROMs keep rendering disabled until after publishing the verdict bytes)

        public static int Run(string[] args)
        {
            string? romPath = null, testPath = null, testDir = null;
            string? dumpModule = null, tracePath = null, shotPath = null, ppuDumpPath = null;
            string? probePath = null, probeVblPath = null, probe2001Path = null, dumpNodeName = null, benchPath = null;
            string? frameDumpPath = null, payloadHistPath = null, fcTaintPath = null, namesArg = null;
            string? phaseProbePath = null;   // --phase-probe: per-hc cpu/ppu clock-phase dump (phase-alignment experiment)
            string? rdyProbePath = null;     // --rdy-probe: per-frame cpu.rdy transition counts (DMC-DMA study)
            string? busTracePath = null;
            string? microPath = null; int microFrames = 3; string? probeDmaPath = null;
            string? opProbePath = null; int opProbeAddr = -1;     // --bus-trace: $4013/$4015 + RDY-stall cycle microscope (DMC #19 study)
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
                    case "--probe-2001":      if (i + 1 < args.Length) probe2001Path = args[++i]; break;
                    case "--dump-node":       if (i + 1 < args.Length) dumpNodeName = args[++i]; break;
                    case "--frames":          if (i + 1 < args.Length) int.TryParse(args[++i], out shotFrames); break;
                    case "--out":             if (i + 1 < args.Length) shotOut      = args[++i]; break;
                    case "--dump-module":     if (i + 1 < args.Length) dumpModule   = args[++i]; break;
                    case "--dump-system":     dumpSystem = true; break;
                    case "--payload-hist":    if (i + 1 < args.Length) payloadHistPath = args[++i]; break;   // NodeInfo inline-payload size distribution (16B-pack study)
                    case "--fc-taint-stats":  if (i + 1 < args.Length) fcTaintPath = args[++i]; break;        // same-state-prune eligibility: FC-free vs FC-tainted channel components (diagnostic only)
                    case "--dump-states":     if (i + 1 < args.Length) _dumpStatesPath = args[++i]; break;    // DIAGNOSTIC: write per-node states after bench for A/B diffing
                    case "--array-footprint": _dumpArrayFootprint = true; break;                              // print hot unmanaged-array base+size at setup (IBS/SPE bucketing)
                    case "--micro":           if (i + 1 < args.Length) microPath = args[++i]; break;   // DIAGNOSTIC: run N frames, dump work RAM $0200-$07FF
                    case "--probe-dma":       if (i + 1 < args.Length) probeDmaPath = args[++i]; break;   // DIAGNOSTIC: trace OAM-DMA addr bus + open-bus (read_buffer #67)
                    case "--micro-frames":    if (i + 1 < args.Length) int.TryParse(args[++i], out microFrames); break;   // DIAGNOSTIC: --micro frame count (default 3)
                    case "--op-probe":        if (i + 2 < args.Length) { opProbePath = args[++i]; opProbeAddr = Convert.ToInt32(args[++i], 16); } break;   // DIAGNOSTIC: hc-log datapath buses when AB hits addr
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
                    case "--ac-verdict":      _acVerdict = true; break;                                                       // test mode: AccuracyCoin completion block ($07F0 = DE B0 61); disables cart-extraram
                    case "--ac-dump-work":    _acDumpWork = true; break;                                                     // diagnostic: dump AccuracyCoin work/results RAM for oracle comparison
                    case "--progress-frames": if (i + 1 < args.Length) int.TryParse(args[++i], out _progressFrames); break;   // test mode: checkpoint cadence, in simulation frames
                    case "--progress-dir":    if (i + 1 < args.Length) _progressDir = args[++i]; break;                       // test mode: checkpoint output directory
                    case "--callback-drain-limit":                                                                        // diagnostic: fail with callback/node evidence instead of hanging
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int callbackDrainLimit))
                            WireCore.CallbackDrainLimit = Math.Max(0, callbackDrainLimit);
                        break;
                    case "--ppu-memory-trace":                                                                            // diagnostic: trace CHR/VRAM callbacks while CPU PC is in an inclusive hex range
                        if (i + 2 < args.Length)
                        {
                            WireCore.PpuMemoryTracePcLo = Convert.ToInt32(args[++i], 16);
                            WireCore.PpuMemoryTracePcHi = Convert.ToInt32(args[++i], 16);
                        }
                        break;
                    case "--ppu-memory-trace-x":                                                                          // diagnostic: optional CPU X filter for --ppu-memory-trace
                        if (i + 1 < args.Length) WireCore.PpuMemoryTraceX = Convert.ToInt32(args[++i], 16);
                        break;
                    case "--pass-marker":     if (i + 1 < args.Length) _passMarker = args[++i]; break;                          // test mode: custom B-class PASS text
                    case "--input":           if (i + 1 < args.Length) _inputSpec = args[++i]; break;                            // test mode: scripted controller input
                    case "--watch":           if (i + 1 < args.Length) _watchSpec = args[++i]; break;                            // DIAGNOSTIC: comma list of node names, printed per frame in --micro
                    case "--no-alu-shim":     _noAluShim = true; break;                                                          // DIAGNOSTIC: A/B the ALU latch hold shim
                    case "--no-dbl2007-shim": _noDbl2007Shim = true; break;                                                        // DIAGNOSTIC: A/B the $2007 double-read merge shim
                    case "--no-ppu-ale-read-feedback-shim": _noPpuAleReadFeedbackShim = true; break;                              // DIAGNOSTIC: expose the raw ALE+Read binary feedback loop
                    case "--no-shims":        _noShims = true; break;                                                                   // DIAGNOSTIC: disable all test-mode shims
                    case "--joypad":          _joypad = true; break;                                                                     // per-test: behavioral joypad + u7/u8 tie-rewire (needed for controller/exec_space)
                    case "--ppu-write-delay": if (i + 1 < args.Length) int.TryParse(args[++i], out _ppuWriteDelayHc); break;           // $2001 write-effect delay N hc (even_odd campaign)
                    case "--oam-dma-ppu-bus-shim": _oamDmaPpuBusShim = true; break;                                                      // (default on) $4014-from-PPU-I/O-bus OAM write-data hold
                    case "--no-oam-dma-ppu-bus-shim": _oamDmaPpuBusShim = false; break;                                                // DIAGNOSTIC: disable the OAM-DMA-PPU-bus shim
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
            if (microPath     != null) return MicroDump(microPath, microFrames);
            if (probeDmaPath  != null) return ProbeDma(probeDmaPath);
            if (opProbePath   != null) return OpProbe(opProbePath, opProbeAddr);
            if (busTracePath  != null) return BusTrace(busTracePath, shotFrames > 3 ? shotFrames : 29);
            if (probePath     != null) return Probe2002(probePath);
            if (probeVblPath  != null) return ProbeVbl(probeVblPath);
            if (probe2001Path != null) return Probe2001(probe2001Path);
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
                  AprVisual.S1 --probe-2001 <rom>          trace a $2001 write -> bkg_enable -> rendering chain + the dot-339 skip window
                  AprVisual.S1 --selftest                  run hand-built inverter/NAND/pass/callback/static-merge circuits

                Diagnostic flags (compose with the above):
                    [--system-def-dir <dir>]               default: data/system-def
                    [--no-lower]                           skip the S1.5 netlist-lowering pass (A/B compare)
                    [--oam-dma-ppu-bus-shim]               opt-in shim for $4014 DMA sourced from PPU I/O registers
                    [--fast-path]                          no-op (fast-path is always on in S1)
                    [--pin [N]]                            cut bench variance: pin the hot thread + High priority + EcoQoS-off
                                                           (Windows; no arg = auto-pick the quietest P-core, N = force logical core N)
                    [--callback-drain-limit <N>]           diagnostic: throw with callback/node evidence if one drain exceeds N dispatches
                    [--ppu-memory-trace <lo> <hi>]         diagnostic: trace CHR/VRAM callbacks while CPU PC is in this hex range
                    [--ppu-memory-trace-x <hex>]           diagnostic: restrict PPU memory trace to one CPU X value
                    [--no-ppu-ale-read-feedback-shim]      diagnostic: disable the CHR ALE+Read analog-feedback guard
                    [--ac-dump-work]                       diagnostic: dump AccuracyCoin CPU RAM $0500-$06FF at verdict

                  (no args)                                print this usage
                """);
        }
    }
}
