using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AprVisual.Rom;
using AprVisual.Sim;
using AprVisual.Sim.Logic;

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
            string? romPath = null, testPath = null, testDir = null, dumpModule = null, tracePath = null, shotPath = null, ppuDumpPath = null, probePath = null, probeVblPath = null, dumpNodeName = null, benchPath = null, traceCmpPath = null, dumpEmittedCs = null;
            bool emitBitsliced = false;
            string systemDefDir = WireCore.SystemDefDir;
            string shotOut = "screenshot.png";
            int maxWait = 15;
            int traceCycles = 64;
            int shotFrames = 3;
            string region = "ntsc";
            bool benchmark = false, dumpSystem = false, dumpGraph = false, dumpDrive = false, dumpNext = false, dumpScc = false, useIr = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--rom":             if (i + 1 < args.Length) romPath      = args[++i]; break;
                    case "--test":            if (i + 1 < args.Length) testPath     = args[++i]; break;
                    case "--test-dir":        if (i + 1 < args.Length) testDir      = args[++i]; break;
                    case "--trace":           if (i + 1 < args.Length) tracePath    = args[++i]; break;
                    case "--trace-cmp":       if (i + 1 < args.Length) traceCmpPath = args[++i]; break;   // S2.4/2.6: IR vs S1 per-node per-half-cycle
                    case "--engine":          if (i + 1 < args.Length && args[++i] == "ir") useIr = true; break;   // "ir" → use the S2.4 IR-driving engine instead of S1's switch-level
                    case "--system":          if (i + 1 < args.Length && args[++i] == "2a03") WireCore.UseBare2a03 = true; break;   // "2a03" → the bare-2A03 CPU-only rig (S3 CPU proof) instead of the full NES board
                    case "--diag-node":       if (i + 1 < args.Length) int.TryParse(args[++i], out AprVisual.Sim.Logic.IrEngine.DiagNode); break;  // with --trace-cmp: print details on each mismatch of this node id
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
                    case "--dump-graph":      dumpGraph = true; break;   // S2.0: NetlistGraph role/kind classification
                    case "--dump-drive":      dumpDrive = true; break;   // S2.1: DriveAnalysis (per-node drive structure)
                    case "--dump-next":       dumpNext = true; break;    // S2.2: NextStateBuilder (per-node next-state Expr)
                    case "--dump-scc":        dumpScc = true; break;     // S2.3: SccAnalysis (cross-coupled latch recovery + SCC)
                    case "--dump-emitted-cs": dumpEmittedCs = (i + 1 < args.Length && !args[i + 1].StartsWith('-')) ? args[++i] : "-"; break;  // S4.1: write IrEngine.EmitCsharpSource() (the codegen output) to a file ("-" = stdout)
                    case "--no-compiled-step": AprVisual.Sim.Logic.IrEngine.UseCompiledStep = false; break;   // S4.1 A/B: use the stack-machine interpreter instead of the compiled chunks
                    case "--enable-bus-lowering": AprVisual.Sim.Logic.IrEngine.EnableBusLowering = true; break;   // S4.2 γ.4 A/B: give hybrid bus nodes a wired-AND pseudo-NextExpr
                    case "--bitsliced": emitBitsliced = true; break;   // S4.4: --dump-emitted-cs emits the bit-sliced (ulong[], 64 instances/word) variant
                    case "--selftest":        return SelfTest();
                    case "--system-def-dir":  if (i + 1 < args.Length) systemDefDir = args[++i]; break;
                    case "--no-lower":        WireCore.EnableLowering = false; break;   // A/B: skip the S1.5 lowering pass
                    case "--max-wait":        if (i + 1 < args.Length) int.TryParse(args[++i], out maxWait); break;
                    case "--region":          if (i + 1 < args.Length) region       = args[++i].ToLowerInvariant(); break;
                    case "--benchmark":
                        benchmark = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) benchPath = args[++i];
                        break;
                    case "--help": case "-h": case "/?": PrintUsage(); return 0;
                    default:
                        // bare path → treat as --rom
                        if (romPath is null && testPath is null && testDir is null && dumpModule is null && tracePath is null && traceCmpPath is null && shotPath is null && ppuDumpPath is null && probePath is null && probeVblPath is null && dumpNodeName is null && !dumpSystem && !dumpGraph && !dumpDrive && !dumpNext && !dumpScc && !args[i].StartsWith('-'))
                            romPath = args[i];
                        break;
                }
            }

            WireCore.SystemDefDir = systemDefDir;

            if (dumpModule != null) return DumpModule(systemDefDir, dumpModule);
            if (dumpSystem) return DumpSystem();
            if (dumpGraph) return DumpGraph();
            if (dumpDrive) return DumpDrive();
            if (dumpNext) return DumpNext();
            if (dumpScc) return DumpScc();
            if (dumpEmittedCs != null) return DumpEmittedCs(dumpEmittedCs, emitBitsliced);
            if (tracePath != null) return Trace(tracePath, traceCycles, useIr);
            if (traceCmpPath != null) return useIr ? TraceCmpDrive(traceCmpPath, traceCycles == 64 ? 2000 : traceCycles)
                                                   : TraceCmp(traceCmpPath, traceCycles == 64 ? 2000 : traceCycles);
            if (shotPath != null) return Screenshot(shotPath, shotFrames, shotOut);
            if (ppuDumpPath != null) return PpuDump(ppuDumpPath, shotFrames);
            if (probePath != null) return Probe2002(probePath);
            if (probeVblPath != null) return ProbeVbl(probeVblPath);
            if (dumpNodeName != null) return DumpNode(dumpNodeName);
            if (benchPath != null) return Benchmark(benchPath, shotFrames, useIr);

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

        // ── S2.0 acceptance harness: build the NetlistGraph from the lowered netlist, print the
        //    role / transistor-kind classification + SelfCheck() + spot-checks. Doesn't touch the sim. ──
        private static int DumpGraph()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            Console.WriteLine($"S2.0 NetlistGraph — from the lowered netlist ({WireCore.NonNullNodeCount} nodes, {WireCore.TransistorBuildCount} transistors)");
            Console.WriteLine($"  {WireCore.LastLowerStats}");
            var (s, i, b, n) = g.CountByRole();
            Console.WriteLine($"  node roles:       Supply={s}  Input={i}  Bus={b}  Internal={n}");
            var ck = g.CountByKind();
            var kindNames = Enum.GetNames<AprVisual.Sim.Logic.TransistorKind>();
            Console.WriteLine($"  transistor kinds: {string.Join("  ", kindNames.Select((nm, idx) => $"{nm}={ck[idx]}"))}");
            Console.WriteLine($"  SelfCheck:        {g.SelfCheck()}");

            Console.WriteLine("  spot-checks:");
            foreach (var nm in new[] { "vcc", "vss", "clk", "res", "cpu.db0", "cpu.db7", "ppu.io_db0" })
            {
                int id = WireCore.LookupNode(nm);
                Console.WriteLine($"    {nm,-12} = {(id == WireCore.EmptyNode ? "(no such node)" : g.Describe(id))}");
            }
            var inputs = new System.Collections.Generic.List<string>();
            for (int id = 0; id < g.Role.Length && inputs.Count < 20; id++)
                if (g.Role[id] == AprVisual.Sim.Logic.NodeRole.Input) inputs.Add($"{WireCore.GetNodeName(id)}#{id}");
            Console.WriteLine($"  Input nodes ({(inputs.Count < 20 ? inputs.Count.ToString() : "first 20")}): {string.Join(", ", inputs)}");
            return 0;
        }

        // ── S2.1 acceptance harness: build NetlistGraph + DriveAnalysis from the lowered netlist, print
        //    the drive-structure stats (PullDown some/none/complex, PullUp kinds, transmission-gate count,
        //    Hybrid count + reasons, sample clean gates, spot-checks). Doesn't touch the sim. ──
        private static int DumpDrive()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var (total, complexPd, hybrid, pdNull, pdSome, pu, passLinks, bidir) = AprVisual.Sim.Logic.DriveAnalysis.Stats(di, g);
            Console.WriteLine($"S2.1 DriveAnalysis — from the lowered netlist + S2.0 graph");
            Console.WriteLine($"  {WireCore.LastLowerStats}");
            Console.WriteLine($"  RefineBuses: promoted {AprVisual.Sim.Logic.DriveAnalysis.LastBusesRefined} `_d[]` pin node(s) Internal→Bus");
            Console.WriteLine($"  nodes with DriveInfo: {total}  (Internal+Bus; Supply/Input skipped)");
            Console.WriteLine($"  PullDown:  some={pdSome}  none={pdNull}  complex={complexPd}");
            var puNames = Enum.GetNames<AprVisual.Sim.Logic.PullUpKind>();
            Console.WriteLine($"  PullUp:    {string.Join("  ", puNames.Select((nm, i) => $"{nm}={pu[i]}"))}");
            Console.WriteLine($"  transmission-gate passes: {passLinks}  (Bidirectional={bidir})");
            Console.WriteLine($"  Hybrid nodes: {hybrid}  ({100.0 * hybrid / Math.Max(1, total):F1}%)   → S2.1 IR coverage ≈ {100.0 * (total - hybrid) / Math.Max(1, total):F1}%");

            Console.WriteLine("  first hybrid nodes (with reason):");
            int shown = 0;
            for (int v = 0; v < di.Length && shown < 30; v++)
                if (di[v] is { Hybrid: true } d) { Console.WriteLine($"    {WireCore.GetNodeName(v)}#{v}  — {d.HybridReason}"); shown++; }

            Console.WriteLine("  sample clean gates (PullUp=StaticLoad, simple PullDown):");
            int ns = 0;
            for (int v = 3; v < di.Length && ns < 8; v++)
                if (di[v] is { Hybrid: false, PullUp: AprVisual.Sim.Logic.PullUpKind.StaticLoad } d && d.PullDown != null && !d.PullDown.IsComplex && d.PullDown.Pretty().Length is > 4 and < 50)
                    { Console.WriteLine($"    {AprVisual.Sim.Logic.DriveAnalysis.Describe(d, v)}"); ns++; }

            Console.WriteLine("  spot-checks:");
            foreach (var nm in new[] { "cpu.db0", "ppu.io_db0", "res", "clk" })
            {
                int id = WireCore.LookupNode(nm);
                Console.WriteLine($"    {nm,-12} = {(id == WireCore.EmptyNode ? "(no such node)" : id < di.Length ? AprVisual.Sim.Logic.DriveAnalysis.Describe(di[id], id) : "?")}");
            }
            return 0;
        }

        // ── S2.2 acceptance harness: build NetlistGraph + DriveAnalysis + NextStateBuilder, print
        //    combinational/sequential/hybrid counts + IR coverage + nextExpr shape distribution + sample
        //    logic gates / latches + spot-checks. Doesn't touch the sim. ──
        private static int DumpNext()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var m = AprVisual.Sim.Logic.NextStateModel.Build(g, di);
            var (comb, seq, hybrid, ce, no, ga, mx, ho, ot) = m.Stats(di);
            int total = comb + seq + hybrid;
            Console.WriteLine($"S2.2 NextStateBuilder — nextExpr per node");
            Console.WriteLine($"  {WireCore.LastLowerStats}");
            Console.WriteLine($"  nodes with DriveInfo: {total}  →  combinational={comb}  sequential(Hold-self)={seq}  hybrid(null)={hybrid}");
            Console.WriteLine($"  IR coverage ≈ {100.0 * (comb + seq) / Math.Max(1, total):F1}%   (hybrid {100.0 * hybrid / Math.Max(1, total):F1}% — the cross-coupled latches in there get reclassified sequential by S2.3)");
            Console.WriteLine($"  nextExpr shapes:  Const={ce}  Not(..)={no}  And/Or/NodeRef={ga}  Mux(..)={mx}  Hold(..)={ho}  other={ot}");

            Console.WriteLine("  sample logic gates (nextExpr = Not(..)):");
            int n1 = 0;
            for (int v = 3; v < m.NextExpr.Length && n1 < 8; v++)
                if (m.NextExpr[v] is NotExpr && m.NextExpr[v]!.Pretty().Length is > 4 and < 56) { Console.WriteLine($"    {m.Describe(v)}"); n1++; }
            Console.WriteLine("  sample latches (sequential — Mux(.., Hold(self)) / Hold(self)):");
            int n2 = 0;
            for (int v = 3; v < m.NextExpr.Length && n2 < 6; v++)
                if (v < m.IsSequential.Length && m.IsSequential[v] && m.NextExpr[v]!.Pretty().Length < 70) { Console.WriteLine($"    {m.Describe(v)}"); n2++; }

            Console.WriteLine("  spot-checks:");
            foreach (var nm in new[] { "ppu.io_ce", "cpu.db0", "res", "clk" })
            {
                int id = WireCore.LookupNode(nm);
                Console.WriteLine($"    {nm,-12} = {(id == WireCore.EmptyNode ? "(no such node)" : id < m.NextExpr.Length ? m.Describe(id) : "?")}");
            }
            return 0;
        }

        // ── S2.3 acceptance harness: build NetlistGraph + DriveAnalysis + NextStateBuilder + SccAnalysis,
        //    print how many cross-coupled latches got recovered (hybrid → sequential), the IR coverage
        //    before/after, sample recovered latches, remaining hybrid, spot-checks. Doesn't touch the sim. ──
        // S4.1/S4.4: write IrEngine.EmitCsharpSource(bitsliced) (the codegen output — chunked C# `Step`) to a file ("-" = stdout).
        private static int DumpEmittedCs(string outPath, bool bitsliced)
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }
            AprVisual.Sim.Logic.IrEngine.Build();
            var src = AprVisual.Sim.Logic.IrEngine.EmitCsharpSource(bitsliced);
            int lines = src.Count(c => c == '\n');
            Console.Error.WriteLine($"# emitted {(bitsliced ? "bit-sliced (ulong[], 64/word)" : "scalar (byte[])")} C# step: {AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount} EvalOrder nodes in {AprVisual.Sim.Logic.IrEngine.CompiledChunkCount} chunks ({AprVisual.Sim.Logic.IrEngine.ChunkSize} nodes/chunk) + {AprVisual.Sim.Logic.IrEngine.SccEvalOrders.Length} SCCs fixed-K={AprVisual.Sim.Logic.IrEngine.FixedKScc}; {lines} lines, {src.Length} chars");
            if (outPath == "-") Console.Out.Write(src);
            else { File.WriteAllText(outPath, src); Console.Error.WriteLine($"# written to {outPath}"); }
            return 0;
        }

        private static int DumpScc()
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var s2 = AprVisual.Sim.Logic.NextStateModel.Build(g, di);
            var (s2c, s2s, s2h, _, _, _, _, _, _) = s2.Stats(di);
            var m = AprVisual.Sim.Logic.SccModel.Build(g, di, s2);
            var (comb, seq, hyb, total) = m.Stats(di);
            double covBefore = 100.0 * (s2c + s2s) / Math.Max(1, total);
            double covAfter  = 100.0 * (comb + seq) / Math.Max(1, total);
            Console.WriteLine($"S2.3 SccAnalysis — Stage A (2-node cross-coupled latch recovery)");
            Console.WriteLine($"  {WireCore.LastLowerStats}");
            Console.WriteLine($"  S2.2 (before):  combinational={s2c}  sequential={s2s}  hybrid={s2h}   IR coverage ≈ {covBefore:F1}%");
            Console.WriteLine($"  recovered cross-coupled latches (hybrid → sequential): {m.RecoveredLatches}");
            Console.WriteLine($"  S2.3a (after):  combinational={comb}  sequential={seq}  hybrid={hyb}   IR coverage ≈ {covAfter:F1}%");
            Console.WriteLine($"  {(covAfter >= 85.0 ? $"→ ≥ 85% — IR-coverage target met. Remaining {hyb} hybrid (mostly genuine multi-driver buses + a few unrecovered complex latches). Stages B–E (dependency graph + Tarjan + cross-phase-cycle break) would squeeze a few % more; or move to S2.4 — the per-node equivalence check there validates everything." : "→ < 85% — Stages B–E (dependency graph + Tarjan + cross-phase break) still needed; or loosen the recovery pattern.")}");

            Console.WriteLine("  sample recovered latches (nextExpr = Mux(.., Prev(self))):");
            int ns = 0;
            for (int v = 3; v < m.NextExpr.Length && ns < 10; v++)
                if (v < m.IsSequential.Length && m.IsSequential[v] && !m.Hybrid[v] && m.NextExpr[v] is MuxExpr && m.NextExpr[v]!.Pretty().Contains("prev(") && m.NextExpr[v]!.Pretty().Length < 90)
                    { Console.WriteLine($"    {m.Describe(v)}"); ns++; }
            Console.WriteLine("  remaining hybrid (first 15, with reason):");
            int nh = 0;
            for (int v = 0; v < m.Hybrid.Length && nh < 15; v++)
                if (m.Hybrid[v]) { Console.WriteLine($"    {WireCore.GetNodeName(v)}#{v}  — {di[v]?.HybridReason}"); nh++; }
            Console.WriteLine("  spot-checks:");
            foreach (var nm in new[] { "cpu.a0", "cpu.x0", "cpu.p0", "ppu.io_ce", "cpu.db0" })
            {
                int id = WireCore.LookupNode(nm);
                Console.WriteLine($"    {nm,-12} = {(id == WireCore.EmptyNode ? "(no such node)" : id < m.NextExpr.Length ? m.Describe(id) : "?")}");
            }
            // S2.4 / S3 picture: build the full IR engine (BuildEvalOrder + residual-SCC detection + the flat program)
            AprVisual.Sim.Logic.IrEngine.Build();
            Console.WriteLine($"  → IR engine: {AprVisual.Sim.Logic.IrEngine.IrCoveredCount} IR-covered ({100.0 * AprVisual.Sim.Logic.IrEngine.IrCoveredCount / Math.Max(1, WireCore.NonNullNodeCount):F1}%), {AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount} driving-evaluated, {AprVisual.Sim.Logic.IrEngine.ResidualSccNodes} in residual SCCs → S1, {AprVisual.Sim.Logic.IrEngine.SkippableInRecalcCount} bridge-skippable, {AprVisual.Sim.Logic.IrEngine.FlatInstrCount:N0} flat instrs; {WireCore.NonNullNodeCount} nodes total{(WireCore.UseBare2a03 ? "  [bare-2A03 rig]" : "")}");
            // S4.2b — the hybrid pass-transistor-bus cut-points (BusResolver.Build): to be resolved by an inline S0/S1/W1 ping-pong, not the IR graph.
            {
                var bn = AprVisual.Sim.Logic.IrEngine.BusNodes;
                int lp = 0, bp = 0, npd = 0, nsv = 0, nsl = 0, npc = 0;
                foreach (var b in bn) { lp += b.LogicPasses.Count; bp += b.BusPasses.Count; if (b.PullDown != null) npd++; if (b.StrongVcc) nsv++; if (b.StaticLoad) nsl++; if (b.PullUpCond != null) npc++; }
                Console.WriteLine($"  → S4.2b bus cut-points: {bn.Length} bus nodes  ({lp} LogicPasses + {bp} BusPasses; {npd} w/ PullDown, {nsv} StrongVcc, {nsl} StaticLoad, {npc} conditional pull-up)");
                int ne = 0;
                for (int i = 0; i < bn.Length && ne < 8; i++)
                    if (bn[i].BusPasses.Count > 0) { Console.WriteLine($"      {WireCore.GetNodeName(bn[i].Id)}#{bn[i].Id}  — {bn[i].LogicPasses.Count} logic-pass + {bn[i].BusPasses.Count} bus-pass{(bn[i].PullDown != null ? " +pd" : "")}{(bn[i].StrongVcc ? " +vcc" : "")}{(bn[i].StaticLoad ? " +load" : "")}{(bn[i].PullUpCond != null ? " +pu?" : "")}"); ne++; }
            }

            // ── S3 γ planning: anatomy of the residual SCCs ──
            var comps = AprVisual.Sim.Logic.IrEngine.SccComponents;
            var nx = AprVisual.Sim.Logic.IrEngine.NextExpr;
            int selfLoops = comps.Count(c => c.Length == 1);
            int multi = comps.Count - selfLoops;
            // self-loop NextExpr shape histogram: is it Mux(C, X, NodeRef(self)) with C/X not referencing NodeRef(self)?  ("clean hold" — γ.1 can Prev-cut it)
            int cleanHold = 0, otherSelf = 0;
            bool RefsNode(Expr? e, int id) { if (e == null) return false; var s = new HashSet<int>(); AprVisual.Sim.Logic.IrEngine.CollectIdsPublic(e, s); return s.Contains(id); }
            foreach (var c in comps) if (c.Length == 1)
            {
                int v = c[0];
                bool clean = v < nx.Length && nx[v] is MuxExpr mx && ((mx.B is NodeRefExpr br && br.Id == v && !RefsNode(mx.Cond, v) && !RefsNode(mx.A, v)) || (mx.A is NodeRefExpr ar && ar.Id == v && !RefsNode(mx.Cond, v) && !RefsNode(mx.B, v)));
                if (clean) cleanHold++; else otherSelf++;
            }
            Console.WriteLine($"  → residual SCCs: {comps.Count} total = {selfLoops} self-loop (size-1) + {multi} multi-node; self-loop shapes: {cleanHold} clean-hold Mux(C,X,Node(self)) + {otherSelf} other");
            Console.WriteLine("  → sample 'other' self-loop NextExprs:");
            int so = 0; foreach (var c in comps) if (c.Length == 1 && so < 8) { int v = c[0]; bool clean = v < nx.Length && nx[v] is MuxExpr mx2 && ((mx2.B is NodeRefExpr br2 && br2.Id == v && !RefsNode(mx2.Cond, v) && !RefsNode(mx2.A, v)) || (mx2.A is NodeRefExpr ar2 && ar2.Id == v && !RefsNode(mx2.Cond, v) && !RefsNode(mx2.B, v))); if (!clean) { var p = v < nx.Length ? nx[v]?.Pretty() ?? "?" : "?"; Console.WriteLine($"      {WireCore.GetNodeName(v)}#{v} = {(p.Length > 140 ? p[..140] + "…" : p)}"); so++; } }
            // the biggest SCCs: size + how many of their members are named (have a semantic name vs unnamed wire junction)
            Console.WriteLine("  → biggest multi-node SCCs (size — #named — sample node names):");
            foreach (var c in comps.Where(c => c.Length > 1).OrderByDescending(c => c.Length).Take(8))
            {
                int named = c.Count(v => WireCore.GetNodeName(v) != v.ToString());
                var names = c.Where(v => WireCore.GetNodeName(v) != v.ToString()).Take(12).Select(v => WireCore.GetNodeName(v));
                Console.WriteLine($"      size {c.Length} — {named} named — {string.Join(", ", names)}{(named > 12 ? " …" : "")}");
            }
            // for clk0_int / pclk0 / pclk1: which SCC are they in, and what's their NextExpr?
            Console.WriteLine("  → PPU clock-generator nodes:");
            foreach (var nm in new[] { "ppu.clk0_int", "ppu./clk0_int", "ppu.pclk0", "ppu.pclk1", "ppu.clk0", "clk0" })
            {
                int id = WireCore.LookupNode(nm);
                if (id == WireCore.EmptyNode) { Console.WriteLine($"      {nm}: (no such node)"); continue; }
                int sccSize = comps.FirstOrDefault(c => c.Contains(id))?.Length ?? 0;
                string p = id < nx.Length && nx[id] != null ? nx[id]!.Pretty() : "(no NextExpr — hybrid/Input)";
                Console.WriteLine($"      {nm}#{id}: in SCC of size {sccSize}; NextExpr = {(p.Length > 160 ? p[..160] + "…" : p)}");
            }
            // ── S3 γ.2 planning: per-SCC node detail (pass ports / pull-down / pull-up / SCC-internal NodeRefs) ──
            Console.WriteLine("  → residual multi-node SCC detail (γ.2 planning):");
            foreach (var c in comps.Where(c => c.Length > 1).OrderByDescending(c => c.Length))
            {
                var memberSet = new HashSet<int>(c);
                int withPass = c.Count(v => v < di.Length && di[v] is { } d && d.Passes.Count > 0);
                int withPd   = c.Count(v => v < di.Length && di[v] is { PullDown: not null });
                Console.WriteLine($"    SCC size {c.Length}: {withPass}/{c.Length} have pass ports, {withPd}/{c.Length} have a pull-down");
                int shown = 0;
                foreach (int v in c)
                {
                    if (shown++ >= 6) break;
                    var d = v < di.Length ? di[v] : null;
                    var intRefs = new HashSet<int>(); if (v < nx.Length && nx[v] is { } e) AprVisual.Sim.Logic.IrEngine.CollectIdsPublic(e, intRefs);
                    var cyc = string.Join(",", intRefs.Where(memberSet.Contains).Select(WireCore.GetNodeName));
                    string passes = d == null || d.Passes.Count == 0 ? "—" : string.Join(" ", d.Passes.Select(pl => $"[{pl.Cond.Pretty()}?{WireCore.GetNodeName(pl.Other)}{(pl.OwnerDrives == true ? "←" : pl.OwnerDrives == false ? "→" : "↔")}]"));
                    string pd = d?.PullDown == null ? "—" : d.PullDown is ComplexExpr ? "<complex>" : d.PullDown.Pretty() is var s && s.Length > 50 ? s[..50] + "…" : d.PullDown.Pretty();
                    string pu = d == null ? "?" : d.PullUp.ToString();
                    string p = v < nx.Length && nx[v] != null ? nx[v]!.Pretty() : "(null)";
                    Console.WriteLine($"      {WireCore.GetNodeName(v)}#{v}  cyc-edges→[{cyc}]  pass={passes}  pd={pd}  pu={pu}  next={(p.Length > 110 ? p[..110] + "…" : p)}");
                }
            }
            // ── S3 γ.2 refine: pass-transistor gate fan-out distribution (real clock phases gate many; control signals gate few) ──
            var gateFanout = new System.Collections.Generic.Dictionary<int, int>();
            foreach (var d in di) if (d != null) foreach (var pl in d.Passes) if (pl.Cond is NodeRefExpr cnr) gateFanout[cnr.Id] = gateFanout.GetValueOrDefault(cnr.Id) + 1;
            Console.WriteLine("  → top pass-transistor gate fan-out (node — #pass-transistors-it-gates — in-residual-SCC?):");
            var sccMembers = new HashSet<int>(comps.SelectMany(c => c));
            foreach (var kv in gateFanout.OrderByDescending(kv => kv.Value).Take(40))
                Console.WriteLine($"      {WireCore.GetNodeName(kv.Key)}#{kv.Key}  ×{kv.Value}{(sccMembers.Contains(kv.Key) ? "  (in SCC)" : "")}");
            // and the gates of the residual-SCC latch nodes (di[v].Passes>0 && PullDown==null && PullUp==None):
            Console.WriteLine("  → residual-SCC pure-pass-latch nodes and their gate fan-outs:");
            int latchShown = 0;
            foreach (int v in comps.SelectMany(c => c).Distinct())
            {
                var d = v < di.Length ? di[v] : null;
                if (d == null || d.PullDown != null || d.PullUp != AprVisual.Sim.Logic.PullUpKind.None || d.Passes.Count == 0) continue;
                if (latchShown++ >= 30) break;
                var gates = string.Join(",", d.Passes.Select(pl => pl.Cond is NodeRefExpr g ? $"{WireCore.GetNodeName(g.Id)}×{gateFanout.GetValueOrDefault(g.Id)}" : pl.Cond.Pretty()));
                Console.WriteLine($"      {WireCore.GetNodeName(v)}#{v}  gates={gates}");
            }
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
            fails += TestNetlistGraph();
            fails += TestDriveAnalysis();
            fails += TestNextState();
            fails += TestSccAnalysis();
            fails += TestNodeAlias();
            fails += TestIrEngine();
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

        // S2.0: a hand-built tiny netlist (PullDown / depletion-load / PullUpStrong / PullUpActive / Pass,
        // plus a node that's only ever a gate → Input) — NetlistGraph.BuildFrom() must classify it right + SelfCheck() pass.
        private static int TestNetlistGraph()
        {
            Console.WriteLine("NetlistGraph (S2.0):");
            WireCore.ResetBuild();   // creates vcc=1, vss=2
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "y"); WireCore.AddNode(12, "en");
            WireCore.AddNode(13, "in"); WireCore.AddNode(14, "out"); WireCore.AddNode(15, "clk");
            WireCore.AddNode(16, "x"); WireCore.AddNode(17, "w");
            WireCore.AddTransistor("pd_y",     gate: 10, c1: 11, c2: WireCore.Ngnd);                 // [0] PullDown
            WireCore.AddTransistor("load_y",   gate: 11, c1: 11, c2: WireCore.Npwr, isWeak: true);   // [1] PullUpLoad (gate == source)
            WireCore.AddTransistor("strong_x", gate: WireCore.Npwr, c1: 16, c2: WireCore.Npwr);      // [2] PullUpStrong
            WireCore.AddTransistor("active_w", gate: 10, c1: 17, c2: WireCore.Npwr);                 // [3] PullUpActive (gate is a signal)
            WireCore.AddTransistor("pass_en",  gate: 12, c1: 13, c2: 14);                            // [4] Pass

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            int f = 0;
            f += Check("pd_y -> PullDown",          g.Kind[0] == AprVisual.Sim.Logic.TransistorKind.PullDown);
            f += Check("load_y -> PullUpLoad",      g.Kind[1] == AprVisual.Sim.Logic.TransistorKind.PullUpLoad && g.TransIsWeak[1]);
            f += Check("strong_x -> PullUpStrong",  g.Kind[2] == AprVisual.Sim.Logic.TransistorKind.PullUpStrong);
            f += Check("active_w -> PullUpActive",  g.Kind[3] == AprVisual.Sim.Logic.TransistorKind.PullUpActive);
            f += Check("pass_en -> Pass, dir Unknown", g.Kind[4] == AprVisual.Sim.Logic.TransistorKind.Pass && g.PassDirection[4] == AprVisual.Sim.Logic.PassDir.Unknown);
            f += Check("vcc / vss -> Supply",       g.Role[WireCore.Npwr] == AprVisual.Sim.Logic.NodeRole.Supply && g.Role[WireCore.Ngnd] == AprVisual.Sim.Logic.NodeRole.Supply);
            f += Check("clk -> Input (only a node, no channel/pull-up)", g.Role[WireCore.LookupNode("clk")] == AprVisual.Sim.Logic.NodeRole.Input);
            f += Check("a -> Input (only ever a gate)", g.Role[WireCore.LookupNode("a")] == AprVisual.Sim.Logic.NodeRole.Input);
            f += Check("y -> Internal (a channel end of pd_y/load_y)", g.Role[WireCore.LookupNode("y")] == AprVisual.Sim.Logic.NodeRole.Internal);
            f += Check("SelfCheck() == OK",         g.SelfCheck() == "OK");
            WireCore.Shutdown();
            return f;
        }

        // S2.1: hand-built tiny netlist — inverter (y=!a, static-load pull-up), NAND (z=!(b&c) series stack
        // with a transparent interior 'mid'), and a transmission-gate pass (r↔q gated by 'sel'; q drives a
        // fake gate so q is NOT a stack interior → the pass is a mux; r has a pull-up so r drives q).
        private static int TestDriveAnalysis()
        {
            Console.WriteLine("DriveAnalysis (S2.1):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "y");
            WireCore.AddNode(12, "b"); WireCore.AddNode(13, "c"); WireCore.AddNode(14, "z"); WireCore.AddNode(15, "mid");
            WireCore.AddNode(16, "sel"); WireCore.AddNode(17, "r"); WireCore.AddNode(18, "q"); WireCore.AddNode(19, "sink");
            WireCore.AddTransistor("inv",   gate: 10, c1: 11, c2: WireCore.Ngnd);   WireCore.Nodes[11]!.Pullups = 1;   // y = !a
            WireCore.AddTransistor("nand1", gate: 12, c1: 14, c2: 15);                                                  // b: z—mid
            WireCore.AddTransistor("nand2", gate: 13, c1: 15, c2: WireCore.Ngnd);   WireCore.Nodes[14]!.Pullups = 1;   // c: mid—vss
            WireCore.AddTransistor("pass_sel",   gate: 16, c1: 17, c2: 18);          WireCore.Nodes[17]!.Pullups = 1;   // sel: r <> q  (transmission gate; r driven)
            WireCore.AddTransistor("fakegate_q", gate: 18, c1: 19, c2: WireCore.Ngnd); WireCore.Nodes[19]!.Pullups = 1; // q gates this → q is a real signal, not a stack node

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var dy = di[11]!; var dz = di[14]!; var dr = di[17]!; var dq = di[18]!;
            int f = 0;
            f += Check("y: pullDown == n_a, pullUp == StaticLoad, no passes, not hybrid",
                       dy.PullDown!.Equals(AprVisual.Sim.Logic.Expr.Node(10)) && dy.PullUp == AprVisual.Sim.Logic.PullUpKind.StaticLoad && dy.Passes.Count == 0 && !dy.Hybrid);
            f += Check("z: pullDown == (n_b & n_c), pullUp == StaticLoad, not hybrid",
                       dz.PullDown!.Equals(AprVisual.Sim.Logic.Expr.And(AprVisual.Sim.Logic.Expr.Node(12), AprVisual.Sim.Logic.Expr.Node(13))) && dz.PullUp == AprVisual.Sim.Logic.PullUpKind.StaticLoad && !dz.Hybrid);
            f += Check("'mid' = a stack interior: pullDown == n_c, and gets one inbound write port from pulled-up neighbour z (b ? z : …)",
                       di[15] != null && di[15]!.PullDown!.Equals(AprVisual.Sim.Logic.Expr.Node(13))
                       && di[15]!.Passes.Count == 1 && di[15]!.Passes[0].Other == 14 && di[15]!.Passes[0].OwnerDrives == false && !di[15]!.Hybrid);
            f += Check("r↔q recorded as a transmission-gate pass on both ends (q.Passes→r, r.Passes→q), q has no pull-down",
                       dq.Passes.Any(p => p.Other == 17) && dr.Passes.Any(p => p.Other == 18) && dq.PullDown == null);
            f += Check("r↔q direction: r drives q (r has a pull-up; q is dynamic)",
                       dr.Passes.Single(p => p.Other == 18).OwnerDrives == true && dq.Passes.Single(p => p.Other == 17).OwnerDrives == false);
            WireCore.Shutdown();
            return f;
        }

        // S2.2: hand-built tiny netlist — inverter y=!a, NAND z=!(b&c) (series stack + transparent interior),
        // a dynamic latch q (d --[ph]--> q; q gates a fake gate so q is a real node; d is driven), and a
        // hybrid pair p1↔p2 (a bidirectional pass between two driven nodes) → nextExpr must come out as
        // Not(n_a) / Not(n_b & n_c) / Mux(n_ph, n_d, Hold(q)) / null,null.
        private static int TestNextState()
        {
            Console.WriteLine("NextStateBuilder (S2.2):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "y");
            WireCore.AddNode(12, "b"); WireCore.AddNode(13, "c"); WireCore.AddNode(14, "z"); WireCore.AddNode(15, "mid");
            WireCore.AddNode(16, "ph"); WireCore.AddNode(17, "d"); WireCore.AddNode(18, "q"); WireCore.AddNode(19, "sink");
            WireCore.AddNode(20, "g1"); WireCore.AddNode(21, "p1"); WireCore.AddNode(22, "p2"); WireCore.AddNode(23, "sel");
            WireCore.AddTransistor("inv",   gate: 10, c1: 11, c2: WireCore.Ngnd); WireCore.Nodes[11]!.Pullups = 1;       // y = !a
            WireCore.AddTransistor("nand1", gate: 12, c1: 14, c2: 15);
            WireCore.AddTransistor("nand2", gate: 13, c1: 15, c2: WireCore.Ngnd); WireCore.Nodes[14]!.Pullups = 1;       // z = !(b&c)
            WireCore.AddTransistor("pass_ph",    gate: 16, c1: 17, c2: 18); WireCore.Nodes[17]!.Pullups = 1;            // d↔q  (d driven)
            WireCore.AddTransistor("fakegate_q", gate: 18, c1: 19, c2: WireCore.Ngnd); WireCore.Nodes[19]!.Pullups = 1; // q gates this → q is a real signal
            WireCore.AddTransistor("pd_p1", gate: 20, c1: 21, c2: WireCore.Ngnd); WireCore.Nodes[21]!.Pullups = 1;      // p1 driven
            WireCore.AddTransistor("pd_p2", gate: 20, c1: 22, c2: WireCore.Ngnd); WireCore.Nodes[22]!.Pullups = 1;      // p2 driven
            WireCore.AddTransistor("pass_p1p2", gate: 23, c1: 21, c2: 22);                                              // p1↔p2 (both driven → bidirectional → hybrid)

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var m = AprVisual.Sim.Logic.NextStateModel.Build(g, di);
            var E = (Func<int, AprVisual.Sim.Logic.Expr>)AprVisual.Sim.Logic.Expr.Node;
            int f = 0;
            f += Check("y: nextExpr == !n_a, not sequential",
                       m.NextExpr[11]!.Equals(AprVisual.Sim.Logic.Expr.Not(E(10))) && !m.IsSequential[11]);
            f += Check("z: nextExpr == !(n_b & n_c), not sequential",
                       m.NextExpr[14]!.Equals(AprVisual.Sim.Logic.Expr.Not(AprVisual.Sim.Logic.Expr.And(E(12), E(13)))) && !m.IsSequential[14]);
            f += Check("q: nextExpr == Mux(n_ph, n_d, Hold(q)), sequential",
                       m.NextExpr[18]!.Equals(AprVisual.Sim.Logic.Expr.Mux(E(16), E(17), AprVisual.Sim.Logic.Expr.Hold(18))) && m.IsSequential[18]);
            f += Check("p1, p2: hybrid (nextExpr == null)", m.NextExpr[21] == null && m.NextExpr[22] == null);
            f += Check("Expr.Mux smart-ctor: Mux(c,0,b)==!c&b, Mux(c,1,0)==c, Mux(c,a,a)==a",
                       AprVisual.Sim.Logic.Expr.Mux(E(1), AprVisual.Sim.Logic.Expr.False, E(2)).Equals(AprVisual.Sim.Logic.Expr.And(AprVisual.Sim.Logic.Expr.Not(E(1)), E(2)))
                    && AprVisual.Sim.Logic.Expr.Mux(E(1), AprVisual.Sim.Logic.Expr.True, AprVisual.Sim.Logic.Expr.False).Equals(E(1))
                    && AprVisual.Sim.Logic.Expr.Mux(E(1), E(2), E(2)).Equals(E(2)));
            WireCore.Shutdown();
            return f;
        }

        // S2.3: a hand-built cross-coupled latch — q ⟷ /q (two cross-coupled inverters, each with a
        // static-load pull-up), plus a ratioed write pass q ↔ data gated by we (data is a driven node).
        // S2.1 flags q (and data) hybrid ("bidirectional pass between two driven nodes"); S2.3 Stage A must
        // recover q → nextExpr[q] = Mux(n_we, n_data, Prev(q)), sequential, not hybrid; /q stays Not(n_q).
        private static int TestSccAnalysis()
        {
            Console.WriteLine("SccAnalysis (S2.3, Stage A):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "q"); WireCore.AddNode(11, "nq"); WireCore.AddNode(12, "we"); WireCore.AddNode(13, "data"); WireCore.AddNode(14, "g_data");
            WireCore.AddTransistor("pd_q",    gate: 11, c1: 10, c2: WireCore.Ngnd); WireCore.Nodes[10]!.Pullups = 1;   // q's feedback pull-down (gate = /q)
            WireCore.AddTransistor("pd_nq",   gate: 10, c1: 11, c2: WireCore.Ngnd); WireCore.Nodes[11]!.Pullups = 1;   // /q's feedback pull-down (gate = q)
            WireCore.AddTransistor("pd_data", gate: 14, c1: 13, c2: WireCore.Ngnd); WireCore.Nodes[13]!.Pullups = 1;   // data is a driven node
            WireCore.AddTransistor("write",   gate: 12, c1: 10, c2: 13);                                                // q ↔ data, gated by we (ratioed write port)

            var g = AprVisual.Sim.Logic.NetlistGraph.BuildFrom();
            var di = AprVisual.Sim.Logic.DriveAnalysis.Analyze(g);
            var s2 = AprVisual.Sim.Logic.NextStateModel.Build(g, di);
            var m = AprVisual.Sim.Logic.SccModel.Build(g, di, s2);
            int f = 0;
            f += Check("q was hybrid in S2.2 (bidirectional pass between two strongly-driven nodes)",
                       di[10]!.Hybrid && (di[10]!.HybridReason?.Contains("strongly-driven nodes") ?? false));
            f += Check("S2.3a recovered q: nextExpr == Mux(n_we, n_data, Prev(q)), sequential, not hybrid",
                       m.NextExpr[10]!.Equals(AprVisual.Sim.Logic.Expr.Mux(AprVisual.Sim.Logic.Expr.Node(12), AprVisual.Sim.Logic.Expr.Node(13), AprVisual.Sim.Logic.Expr.Prev(10))) && m.IsSequential[10] && !m.Hybrid[10]);
            f += Check("/q stays Not(Node(q)), combinational, not hybrid",
                       m.NextExpr[11]!.Equals(AprVisual.Sim.Logic.Expr.Not(AprVisual.Sim.Logic.Expr.Node(10))) && !m.IsSequential[11] && !m.Hybrid[11]);
            f += Check("RecoveredLatches == 1", m.RecoveredLatches == 1);
            WireCore.Shutdown();
            return f;
        }

        // S3 γ.0: NodeAlias.Apply — fold pure buffer/inverter nodes out of the NodeRef dependency graph.
        private static int TestNodeAlias()
        {
            Console.WriteLine("NodeAlias (S3 γ.0):");
            var E = (Func<int, Expr>)AprVisual.Sim.Logic.Expr.Node;
            int f = 0;
            {   // 10 = buffer of 11; 12 = inverter of 10 (⇒ of 11); 11 = And(20, NodeRef(10)); 20 = root.
                var nx = new Expr?[21];
                nx[10] = E(11);
                nx[11] = AprVisual.Sim.Logic.Expr.And(E(20), E(10));
                nx[12] = AprVisual.Sim.Logic.Expr.Not(E(10));
                nx[20] = AprVisual.Sim.Logic.Expr.True;
                int aliased = AprVisual.Sim.Logic.NodeAlias.Apply(nx);
                f += Check("buffer+inverter: 2 nodes aliased", aliased == 2);
                f += Check("11's expr no longer references node 10", !PrettyHas(nx[11], "n10"));
                f += Check("12 == Not(Node(11))", nx[12]!.Equals(AprVisual.Sim.Logic.Expr.Not(E(11))));
                f += Check("10's expr still Node(11) (now a leaf)", nx[10]!.Equals(E(11)));
            }
            {   // a buffer/inverter loop  /q = !q,  q = !/q  ⇒ must NOT be aliased (it's a latch — γ.1's job).
                var nx = new Expr?[12];
                nx[10] = AprVisual.Sim.Logic.Expr.Not(E(11));   // q  = !/q
                nx[11] = AprVisual.Sim.Logic.Expr.Not(E(10));   // /q = !q
                int aliased = AprVisual.Sim.Logic.NodeAlias.Apply(nx);
                f += Check("inverter loop left alone (0 aliased)", aliased == 0 && nx[10]!.Equals(AprVisual.Sim.Logic.Expr.Not(E(11))) && nx[11]!.Equals(AprVisual.Sim.Logic.Expr.Not(E(10))));
            }
            return f;

            static bool PrettyHas(Expr? e, string token)
            {
                var ids = new HashSet<int>();
                if (e != null) AprVisual.Sim.Logic.IrEngine.CollectIdsPublic(e, ids);
                return token.StartsWith('n') && int.TryParse(token.AsSpan(1), out int id) && ids.Contains(id);
            }
        }

        // S2.4: build the IR engine over a hand-built tiny netlist (inverter y=!a + a dynamic latch q
        // written by `we` from `data`), drive the inputs, and verify checking-mode finds zero mismatches —
        // i.e. EvalExpr(nextExpr[v]) agrees with S1's value for every IR-covered node, every half-cycle.
        private static int TestIrEngine()
        {
            Console.WriteLine("IrEngine (S2.4, checking mode):");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "a"); WireCore.AddNode(11, "y");                // y = !a
            WireCore.AddNode(12, "we"); WireCore.AddNode(13, "data"); WireCore.AddNode(14, "g_data"); WireCore.AddNode(15, "q"); WireCore.AddNode(16, "qsink");
            WireCore.AddTransistor("inv",   gate: 10, c1: 11, c2: WireCore.Ngnd); WireCore.Nodes[11]!.Pullups = 1;
            WireCore.AddTransistor("pd_data", gate: 14, c1: 13, c2: WireCore.Ngnd); WireCore.Nodes[13]!.Pullups = 1;   // data is driven
            WireCore.AddTransistor("wr",     gate: 12, c1: 13, c2: 15);                                                 // data --[we]--> q  (q dynamic)
            WireCore.AddTransistor("qg",     gate: 15, c1: 16, c2: WireCore.Ngnd); WireCore.Nodes[16]!.Pullups = 1;     // q gates qsink → q is a real signal node
            WireCore.Reset();
            WireCore.RecomputeAllNodes();
            AprVisual.Sim.Logic.IrEngine.Build();
            int f = 0;
            f += Check("IrEngine built, IR-covered nodes > 0", AprVisual.Sim.Logic.IrEngine.IrCoveredCount > 0);
            // drive a few input transitions; checking-mode StepOne() settles S1 and verifies the IR each time
            WireCore.SetLow("we"); WireCore.SetLow("g_data");
            WireCore.SetLow("a");       AprVisual.Sim.Logic.IrEngine.StepOne();
            WireCore.SetHigh("a");      AprVisual.Sim.Logic.IrEngine.StepOne();
            WireCore.SetHigh("we");     AprVisual.Sim.Logic.IrEngine.StepOne();   // we on while g_data low ⇒ data=1 ⇒ q ← 1
            WireCore.SetLow("we");      AprVisual.Sim.Logic.IrEngine.StepOne();   // we off ⇒ q holds 1
            WireCore.SetHigh("g_data"); AprVisual.Sim.Logic.IrEngine.StepOne();   // data → 0 but we off ⇒ q still 1
            WireCore.SetLow("a");       AprVisual.Sim.Logic.IrEngine.StepOne();
            f += Check("no IR-vs-S1 mismatches over 6 half-cycles", AprVisual.Sim.Logic.IrEngine.MismatchCount == 0);
            f += Check("S1 sanity: a=0 ⇒ y=1, q held 1", WireCore.IsNodeHigh("y") && WireCore.IsNodeHigh("q"));
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

        // ── Headless throughput benchmark: how many simulated NES frames / 6502 cycles per real second.
        //    (S3 baseline — the switch-level interpreter's speed, before the IR backend exists to beat it.)
        //    Constants: 2A03 clk0 toggles every 24 master half-cycles ⇒ 1 simulated CPU cycle = 24 steps;
        //    one NTSC frame ≈ 357366 master clocks = 714732 half-cycles; CPU 1.789773 MHz; 60.0988 Hz.
        private static int Benchmark(string romPath, int frames, bool useIr = false)
        {
            if (frames < 1) frames = 12;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# benchmark: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {frames} frame(s) headless, Release build recommended{(useIr ? "  [engine: IR (S2.4 driving mode)]" : "  [engine: S1 switch-level]")}");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                if (useIr) { AprVisual.Sim.Logic.IrEngine.Build(); Console.WriteLine($"# IR build: {AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount} node(s) IR-driven ({100.0 * AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount / Math.Max(1, WireCore.NodeCount):F1}%, {AprVisual.Sim.Logic.IrEngine.FlatInstrCount:N0} flat instrs), {AprVisual.Sim.Logic.IrEngine.ResidualSccNodes} in SCCs → S1, rest hybrid → S1; {AprVisual.Sim.Logic.IrEngine.SkippableInRecalcCount} bridge-skippable"); }
                swLoad.Stop();

                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                if (useIr) for (int f = 0; f < frames; f++) AprVisual.Sim.Logic.IrEngine.RunFrameDriving();
                else       for (int f = 0; f < frames; f++) WireCore.RunFrame();
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

            int id = int.TryParse(name, out int rawId) && rawId >= 0 && rawId < WireCore.Nodes.Count ? rawId : WireCore.LookupNode(name);
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
        private static int Trace(string path, int cycles, bool useIr = false)
        {
            var rom = NesRom.LoadFromFile(path);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {path}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(path)}  (PRG {rom.PrgRom.Length / 1024} KB, CHR {rom.ChrRom.Length / 1024} KB, mapper {rom.Mapper}){(useIr ? "  [engine: IR (S2.4 driving mode)]" : "")}");
            try
            {
                WireCore.LoadSystem(rom);
                if (useIr) { AprVisual.Sim.Logic.IrEngine.Build(); Console.WriteLine($"# IR: {AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount} node(s) IR-driven / {AprVisual.Sim.Logic.IrEngine.ResidualSccNodes} in SCCs → S1, rest hybrid"); }
                Console.WriteLine($"# after power-on reset: {WireCore.DumpCpuState()}");
                int prevSync = -1;
                int instrCount = 0;
                for (int c = 0; c < cycles; c++)
                {
                    if (useIr) AprVisual.Sim.Logic.IrEngine.StepDriving(12 * 2);
                    else       WireCore.Step(12 * 2);          // one 6502 cycle = 12 master cycles = 24 half-cycles
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

        // ── S2.4/S2.6: the IR-vs-S1 equivalence gate. Build the IR engine (IrEngine), then run it in
        //    "checking mode" for N half-cycles — each half-cycle, S1 settles the chip and the IR's
        //    nextExpr[v] is verified against S1's result for every IR-covered node v. Report any mismatch. ──
        private static int TraceCmp(string romPath, int cycles)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# --trace-cmp {Path.GetFileName(romPath)} — {cycles} half-cycles, checking mode (IR vs S1, per node per half-cycle)");
            try
            {
                WireCore.LoadSystem(rom);
                AprVisual.Sim.Logic.IrEngine.Build();
                Console.WriteLine($"  IR-covered nodes: {AprVisual.Sim.Logic.IrEngine.IrCoveredCount}  /  {WireCore.NonNullNodeCount}  ({100.0 * AprVisual.Sim.Logic.IrEngine.IrCoveredCount / Math.Max(1, WireCore.NonNullNodeCount):F1}%)   — checked: {AprVisual.Sim.Logic.IrEngine.CheckedCount} (the rest are un-modelled internal placeholders, skipped)");
                AprVisual.Sim.Logic.IrEngine.Step(cycles);
                long mm = AprVisual.Sim.Logic.IrEngine.MismatchCount;
                Console.WriteLine($"  S4.3 fixed-K SCC micro-block (K={AprVisual.Sim.Logic.IrEngine.FixedKScc}): {(AprVisual.Sim.Logic.IrEngine.SccFixedKMismatchCount == 0 ? "converged to S1 for every SCC node" : $"{AprVisual.Sim.Logic.IrEngine.SccFixedKMismatchCount} (SCC-node, half-cycle) DISAGREEMENTS")} — max iterations any SCC actually needed: {AprVisual.Sim.Logic.IrEngine.SccFixedKMaxK}");
                if (mm == 0)
                {
                    Console.WriteLine($"  ✓ NO MISMATCHES over {cycles} half-cycles ({WireCore.Time} t) — IR ≡ S1 for every IR-covered node. (S2.6 equivalence gate PASSED for this ROM.)");
                    return 0;
                }
                int fn = AprVisual.Sim.Logic.IrEngine.FirstMismatchNode;
                int distinct = AprVisual.Sim.Logic.IrEngine.MismatchByNode.Count;
                Console.WriteLine($"  ✗ {mm} node-mismatch(es) over {cycles} half-cycles, across {distinct} distinct node(s).");
                Console.WriteLine($"    first: t={AprVisual.Sim.Logic.IrEngine.FirstMismatchTime}, node {WireCore.GetNodeName(fn)}#{fn}  nextExpr = {AprVisual.Sim.Logic.IrEngine.NextExpr[fn]?.Pretty()}");
                Console.WriteLine($"    top mismatching nodes (id — # half-cycles — nextExpr):");
                foreach (var kv in AprVisual.Sim.Logic.IrEngine.MismatchByNode.OrderByDescending(p => p.Value).Take(25))
                    Console.WriteLine($"      {WireCore.GetNodeName(kv.Key)}#{kv.Key}  — {kv.Value} — {AprVisual.Sim.Logic.IrEngine.NextExpr[kv.Key]?.Pretty()}");
                return 1;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── S2.4/S2.6 driving-mode equivalence: run S1 N half-cycles snapshotting NodeStates each step, reset,
        //    run the IR-driving engine N half-cycles snapshotting each step, then diff — for every observable
        //    node (drives a transistor gate, or named), the two must be bit-identical. ──
        private static unsafe int TraceCmpDrive(string romPath, int cycles)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            cycles = Math.Min(cycles, 30000);   // memory cap: cycles × NodeCount bytes × 2
            Console.WriteLine($"# --trace-cmp --engine ir {Path.GetFileName(romPath)} — {cycles} half-cycles, driving-mode equivalence (IR-driven NodeStates vs S1-driven, per node per half-cycle)");
            byte[][] s1 = new byte[cycles][], ir = new byte[cycles][];
            int n;
            try
            {
                WireCore.LoadSystem(rom);
                n = WireCore.NodeCount;
                for (int t = 0; t < cycles; t++) { WireCore.Step(1); s1[t] = new byte[n]; new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(s1[t]); }
            }
            finally { WireCore.Shutdown(); }
            // observable[v] = drives a transistor gate, or has a semantic name (matches IrEngine.Build's verification boundary)
            bool[] observable;
            try
            {
                WireCore.LoadSystem(rom);
                if (WireCore.NodeCount != n) { Console.Error.WriteLine($"node count changed between runs ({n} → {WireCore.NodeCount})"); return 2; }
                AprVisual.Sim.Logic.IrEngine.Build();
                Console.WriteLine($"  IR-driven nodes: {AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount}  /  {n}   ({100.0 * AprVisual.Sim.Logic.IrEngine.DrivingCoveredCount / Math.Max(1, n):F1}%)   — {AprVisual.Sim.Logic.IrEngine.ResidualSccNodes} node(s) in residual SCCs → S1, rest hybrid → S1; {AprVisual.Sim.Logic.IrEngine.SkippableInRecalcCount} bridge-skippable");
                observable = new bool[n];
                for (int v = 0; v < n; v++) observable[v] = WireCore.Nodes[v] is { } nd && (nd.Gates.Count > 0 || WireCore.GetNodeName(v) != v.ToString());
                for (int t = 0; t < cycles; t++) { AprVisual.Sim.Logic.IrEngine.StepOneDriving(); ir[t] = new byte[n]; new ReadOnlySpan<byte>(WireCore.NodeStates, n).CopyTo(ir[t]); }
            }
            finally { WireCore.Shutdown(); }
            {
                long bmm = AprVisual.Sim.Logic.IrEngine.BusMismatchCount, bex = AprVisual.Sim.Logic.IrEngine.BusFloatExemptCount;
                Console.WriteLine($"  S4.2b inline bus resolver ({AprVisual.Sim.Logic.IrEngine.BusNodes.Length} bus nodes, K_PASS≤{AprVisual.Sim.Logic.IrEngine.BusKPass}, max actually needed {AprVisual.Sim.Logic.IrEngine.BusKPassMax}): {(bmm == 0 ? "reproduces S1 for every driven bus" : $"{bmm} REAL disagreement(s) with S1 (first: {WireCore.GetNodeName(AprVisual.Sim.Logic.IrEngine.FirstBusMismatchNode)}#{AprVisual.Sim.Logic.IrEngine.FirstBusMismatchNode} @ t≈{AprVisual.Sim.Logic.IrEngine.FirstBusMismatchTime})")}; {bex} float-exempt (S0==S1==W1==0 — unobservable transient, the floating-cap case we don't model)");
            }
            long mm = 0; int firstT = -1, firstV = -1; var byNode = new Dictionary<int, long>();
            for (int t = 0; t < cycles; t++)
                for (int v = 0; v < n; v++)
                    if (observable[v] && s1[t][v] != ir[t][v]) { mm++; byNode[v] = byNode.GetValueOrDefault(v) + 1; if (firstT < 0) { firstT = t; firstV = v; } }
            if (mm == 0) { Console.WriteLine($"  ✓ NO MISMATCHES over {cycles} half-cycles — IR-driven ≡ S1-driven for every observable node. (S2.6 driving-mode equivalence gate PASSED for this ROM.)"); return 0; }
            int distinct = byNode.Count;
            Console.WriteLine($"  ✗ {mm} node-mismatch(es) over {cycles} half-cycles, across {distinct} distinct observable node(s).");
            {
                var ir2 = AprVisual.Sim.Logic.IrEngine.NextExpr;
                var fe = firstV < ir2.Length ? ir2[firstV] : null;
                int prevSelf = firstT > 0 ? ir[firstT - 1][firstV] : -1;
                Console.WriteLine($"    first: t={firstT}, node {WireCore.GetNodeName(firstV)}#{firstV}  ir={ir[firstT][firstV]} s1={s1[firstT][firstV]} prevSelf={prevSelf}  nextExpr = {(fe?.Pretty() ?? "(hybrid/InScc)")}");
                if (fe != null)
                {
                    var refs = new HashSet<int>(); AprVisual.Sim.Logic.IrEngine.CollectIdsPublic(fe, refs);
                    var skipArr = AprVisual.Sim.Logic.IrEngine.OkToSkipInRecalc;
                    var parts = new List<string>();
                    foreach (int m in refs)
                    {
                        int irV = m < n ? ir[firstT][m] : -1, prevV = (m < n && firstT > 0) ? ir[firstT - 1][m] : -1, s1V = m < n ? s1[firstT][m] : -1;
                        string skipped = (m < skipArr.Length && skipArr[m]) ? "[skip]" : "";
                        parts.Add($"{WireCore.GetNodeName(m)}#{m}=ir:{irV}(prev {prevV},s1:{s1V}){skipped}");
                    }
                    Console.WriteLine($"    refs: {string.Join("  ", parts)}");
                }
            }
            Console.WriteLine($"    top mismatching nodes (id — # half-cycles — nextExpr):");
            foreach (var kv in byNode.OrderByDescending(p => p.Value).Take(25))
                Console.WriteLine($"      {WireCore.GetNodeName(kv.Key)}#{kv.Key}  — {kv.Value} — {(kv.Key < AprVisual.Sim.Logic.IrEngine.NextExpr.Length ? AprVisual.Sim.Logic.IrEngine.NextExpr[kv.Key]?.Pretty() ?? "(hybrid/InScc)" : "?")}");
            return 1;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
                AprVisual — switch-level NES (S1)

                  AprVisual --rom <game.nes>            show a window; CPU state in the title bar (video output: WIP)
                  AprVisual --trace <rom> [--cycles N]  headless: power-on reset, step N 6502 cycles, dump CPU state each cycle (default N=64)
                  AprVisual --trace-cmp <rom> [--cycles N]  (S2.4/S2.6) IR vs S1 equivalence: run the IR engine N half-cycles (default 2000) in checking mode, report any node-mismatch
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
                  AprVisual --dump-graph                (S2.0) build the NetlistGraph and print the node-role / transistor-kind classification + SelfCheck
                  AprVisual --dump-drive                (S2.1) run DriveAnalysis and print per-node drive structure stats (PullDown / PullUp / passes / hybrid + coverage)
                  AprVisual --dump-next                 (S2.2) run NextStateBuilder and print per-node nextExpr stats (combinational / sequential / hybrid + IR coverage + shapes)
                  AprVisual --dump-scc                  (S2.3) run SccAnalysis and print the cross-coupled latch recovery + IR coverage before/after
                  AprVisual --selftest                  run hand-built inverter / NAND / pass-transistor / static-merge circuits and check truth tables
                    [--system-def-dir <dir>]            default: data/system-def
                    [--no-lower]                         skip the S1.5 netlist-lowering pass (A/B comparison)
                  (no args)                             open an empty window
                """);
        }
    }
}
