using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    internal static partial class TestRunner
    {
        // ── Static diagnostics: netlist dumps, selftests, PPU probes, screenshot/frame-dump. ──
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
            fails += TestCallbackDrainLimit();
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

        private static int TestCallbackDrainLimit()
        {
            Console.WriteLine("callback non-convergence detector:");
            WireCore.ResetBuild();
            WireCore.AddNode(10, "osc");
            bool armed = false;
            WireCore.AddCallback(new[] { WireCore.LookupNode("osc") }, () =>
            {
                if (!armed) return;
                if (WireCore.IsNodeHigh("osc")) WireCore.SetLow("osc");
                else                            WireCore.SetHigh("osc");
            });
            WireCore.Reset();
            WireCore.RecomputeAllNodes();

            int savedLimit = WireCore.CallbackDrainLimit;
            bool caught = false;
            try
            {
                WireCore.CallbackDrainLimit = 32;
                armed = true;
                WireCore.SetHigh("osc");
            }
            catch (InvalidOperationException ex)
            {
                caught = ex.Message.Contains("non-converging callback drain", StringComparison.Ordinal);
            }
            finally
            {
                armed = false;
                WireCore.CallbackDrainLimit = savedLimit;
                WireCore.Shutdown();
            }
            return Check("self-reenqueuing callback is stopped with diagnostics", caught);
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


        // ── --dump-node: introspect one node's pull-up / gated transistors / channel-end transistors ──
        private static int DumpNode(string name)
        {
            try { WireCore.ComposeSystem(chrIsRam: false, isTestRom: true); }
            catch (Exception ex) { Console.Error.WriteLine($"compose failed: {ex.GetType().Name}: {ex.Message}"); return 2; }

            // "@1234" = dump by engine node id directly — unnamed internal nodes (latch loop innards)
            // have no name to look up, but their ids appear in other nodes' dumps.
            int id = name.StartsWith('@') && int.TryParse(name.AsSpan(1), out int rawId)
                   ? rawId : WireCore.LookupNode(name);
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


    }
}
