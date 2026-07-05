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
        // ── --probe-2001: even_odd arbitration M2 — measure the $2001 WRITE-effect path.
        //    Window A: first $2001 write after warm-up — cpu grid (ab/rw/phi2) vs /w2001 strobe,
        //    bkg_enable register bit and the rendering_1..4 enable pipeline, in hpos coordinates.
        //    Window B: the pre-render dot-339 skip — skip_dot / even_frame_toggle / hpos sequence. ──
        private static int Probe2001(string romPath)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# {Path.GetFileName(romPath)} — probing $2001 write-effect + dot-339 skip");
            try
            {
                WireCore.LoadSystem(rom);
                WireCore.EnableDmcLatchShim();                    // shim chain gate
                WireCore.EnablePpuWriteDelay(_ppuWriteDelayHc);   // honor --ppu-write-delay in the probe
                int[] ab = ResolveQ("cpu.ab[15:0]"), db = ResolveQ("cpu.db[7:0]");
                int[] hp = ResolveQ("ppu.hpos[8:0]"), vp = ResolveQ("ppu.vpos[8:0]");
                int rw    = WireCore.LookupNode("cpu.rw");
                int phi2  = WireCore.LookupNode("cpu.phi2");
                int w2001 = WireCore.LookupNode("ppu./w2001");
                int wreg  = WireCore.LookupNode("ppu.write_2001_reg");
                int bkg   = WireCore.LookupNode("ppu.bkg_enable");
                int spr   = WireCore.LookupNode("ppu.spr_enable");
                int r1 = WireCore.LookupNode("ppu.rendering_1"), r2 = WireCore.LookupNode("ppu.rendering_2");
                int r3 = WireCore.LookupNode("ppu.rendering_3"), r4 = WireCore.LookupNode("ppu.rendering_4");
                int h339  = WireCore.LookupNode("ppu.hpos_eq_339_and_rendering");
                int skip  = WireCore.LookupNode("ppu.skip_dot");
                int evenT = WireCore.LookupNode("ppu.even_frame_toggle");
                Console.WriteLine($"# ids: /w2001={w2001} write_2001_reg={wreg} bkg_enable={bkg} rendering_1..4={r1},{r2},{r3},{r4} h339={h339} skip_dot={skip} even_toggle={evenT}");
                int H1(int n) => n != WireCore.EmptyNode && WireCore.IsNodeHigh(n) ? 1 : 0;
                int Rd(int[] a) => WireCore.ReadBits(a);

                for (int f = 0; f < 3; f++) WireCore.RunFrame();

                string last = "";
                for (int wa = 0; wa < 3; wa++)
                {
                    bool found = false;
                    for (long i = 0; i < 8_000_000; i++)
                    {
                        WireCore.Step(1);
                        if (Rd(ab) == 0x2001 && H1(rw) == 0 && (Rd(db) & 0x08) != 0) { found = true; break; }
                    }
                    if (!found) { Console.WriteLine("# no $2001 ENABLE write seen in 8M half-cycles"); break; }
                    Console.WriteLine($"# A{wa} (enable write): t vpos hpos | ab db rw phi2 | /w2001 wreg bkg spr | r1 r2 r3 r4");
                    last = "";
                    for (int j = 0; j < 120; j++)
                    {
                        string line = $"{Rd(ab):X4} {Rd(db):X2} {(H1(rw) != 0 ? 'R' : 'W')} {H1(phi2)} | {H1(w2001)} {H1(wreg)} {H1(bkg)} {H1(spr)} | {H1(r1)} {H1(r2)} {H1(r3)} {H1(r4)}";
                        if (line != last) { Console.WriteLine($"  {WireCore.Time,9} {Rd(vp),3} {Rd(hp),3} | {line}"); last = line; }
                        WireCore.Step(1);
                    }
                }

                for (int win = 0; win < 10; win++)
                {
                    bool foundB = false;
                    for (long i = 0; i < 4_000_000; i++)
                    {
                        WireCore.Step(1);
                        if (Rd(vp) == 261 && Rd(hp) >= 330) { foundB = true; break; }
                    }
                    if (!foundB) { Console.WriteLine("# pre-render window not reached"); return 1; }
                    Console.WriteLine($"# B{win}: t vpos hpos | h339 skip evenT | r4 bkg");
                    last = "";
                    for (int j = 0; j < 220; j++)
                    {
                        int h = Rd(hp);
                        if (h >= 336 || h <= 3)
                        {
                            string line = $"{H1(h339)} {H1(skip)} {H1(evenT)} | {H1(r4)} {H1(bkg)}";
                            string full = $"{Rd(vp),3} {h,3} | {line}";
                            if (full != last) { Console.WriteLine($"  {WireCore.Time,9} {full}"); last = full; }
                        }
                        WireCore.Step(1);
                    }
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

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
    }
}
