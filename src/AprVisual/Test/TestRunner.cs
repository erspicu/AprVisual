using System;
using System.IO;
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
    ///   --benchmark             (S3) measure cycles/sec — placeholder for now
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
            string? romPath = null, testPath = null, testDir = null, dumpModule = null, tracePath = null, shotPath = null;
            string systemDefDir = WireCore.SystemDefDir;
            string shotOut = "screenshot.png";
            int maxWait = 15;
            int traceCycles = 64;
            int shotFrames = 3;
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
                    case "--frames":          if (i + 1 < args.Length) int.TryParse(args[++i], out shotFrames); break;
                    case "--out":             if (i + 1 < args.Length) shotOut      = args[++i]; break;
                    case "--dump-module":     if (i + 1 < args.Length) dumpModule   = args[++i]; break;
                    case "--dump-system":     dumpSystem = true; break;
                    case "--selftest":        return SelfTest();
                    case "--system-def-dir":  if (i + 1 < args.Length) systemDefDir = args[++i]; break;
                    case "--max-wait":        if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--region":          if (i + 1 < args.Length) region       = args[++i].ToLowerInvariant(); break;
                    case "--benchmark": benchmark = true; break;
                    case "--help": case "-h": case "/?": PrintUsage(); return 0;
                    default:
                        // bare path → treat as --rom
                        if (romPath is null && testPath is null && testDir is null && dumpModule is null && tracePath is null && shotPath is null && !dumpSystem && !args[i].StartsWith('-'))
                            romPath = args[i];
                        break;
                }
            }

            WireCore.SystemDefDir = systemDefDir;

            if (dumpModule != null) return DumpModule(systemDefDir, dumpModule);
            if (dumpSystem) return DumpSystem();
            if (tracePath != null) return Trace(tracePath, traceCycles);
            if (shotPath != null) return Screenshot(shotPath, shotFrames, shotOut);

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
                  AprVisual --test <test.nes>           headless: run to the $6000 signature, print PASS/FAIL
                  AprVisual --test-dir <dir>            headless: batch-run *.nes under <dir>
                    [--max-wait <sec>]                  timeout per test (default 15)
                    [--region ntsc|pal|dendy]
                    [--benchmark]                       (S3) measure cycles/sec
                  AprVisual --dump-module <name>        parse <system-def-dir>/<name>.js and print a summary
                  AprVisual --dump-system               compose the full nes-001 + cart netlist and print counts + probes
                  AprVisual --selftest                  run hand-built inverter / NAND / pass-transistor circuits and check truth tables
                    [--system-def-dir <dir>]            default: data/system-def
                  (no args)                             open an empty window
                """);
        }
    }
}
