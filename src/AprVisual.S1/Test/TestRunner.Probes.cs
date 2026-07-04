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
        // ── HC-granularity instruments: --bus-trace / --op-probe / --micro / --rdy-probe / --phase-probe / --trace. ──
        // ── --bus-trace: microscope for the DMC #19 study. Fast-forwards N frames (--frames), then
        //    steps hc-by-hc for 2 frames detecting phi2 falling edges (CPU cycle boundaries) and logs
        //    every bus cycle touching $4013/$4015 plus every RDY-stalled cycle: relative cycle index,
        //    AB, DB, R/W, RDY. Shows the enable→first-fetch→readback chain cycle by cycle. ──
        private static int _btPrevIrq = -2, _btPrevSet = -2, _btPrevEn = -2, _btTail;
        private static unsafe int S(int node) => node != WireCore.EmptyNode ? WireCore.NodeStates[node] : -1;
        // ── --op-probe: hc-granularity datapath microscope. Triggers when AB == addr; logs
        //    db/idl/alua/alub/sb buses + accumulator for the next ~10 CPU cycles. ──
        private static unsafe int OpProbe(string romPath, int trigAddr)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.RegisterRawIdAliases = true;
                WireCore.LoadSystem(rom);
                WireCore.EnableDmcLatchShim();
                if (!_noAluShim) WireCore.EnableAluLatchShim();
                int phi2 = WireCore.LookupNode("cpu.phi2");
                int[] Bus(string expr, int width) { var l = new List<int>(); WireCore.ResolveNodes(expr, l, quiet: true); return l.Count == width ? l.ToArray() : Array.Empty<int>(); }
                int[] abN = Bus("cpu.ab[15:0]", 16), dbN = Bus("cpu.db[7:0]", 8);
                int[] idl = Bus("cpu.idl[7:0]", 8), alua = Bus("cpu.alua[7:0]", 8), alub = Bus("cpu.alub[7:0]", 8);
                int[] sb = Bus("cpu.sb[7:0]", 8), acc = Bus("cpu.a[7:0]", 8), aluOut = Bus("cpu.alu[7:0]", 8);
                string[] ctlNames = { "dpc24_ACSB", "dpc11_SBADD", "dpc12_0ADD", "dpc9_DBADD", "dpc8_nDBADD",
                                      "dpc15_ANDS", "dpc17_SUMS", "dpc14_SRS", "dpc13_ORS",
                                      "dpc19_ADDSB7", "dpc20_ADDSB06", "dpc23_SBAC", "dpc25_SBDB" };
                int[] ctl = new int[ctlNames.Length];
                for (int c = 0; c < ctlNames.Length; c++) ctl[c] = WireCore.LookupNode("cpu." + ctlNames[c]);
                var plaRows = new List<(string Name, int Node)>();
                foreach (var kv in WireCore.AllNodeNames())
                    if (kv.Key.StartsWith("cpu.op-", StringComparison.Ordinal) || kv.Key.StartsWith("cpu.x-op-", StringComparison.Ordinal))
                        plaRows.Add((kv.Key.Substring(4), kv.Value));
                plaRows.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));
                Console.WriteLine($"# pla rows resolved: {plaRows.Count}");
                string prevFiring = "";
                Console.WriteLine($"# op-probe: trig=AB={trigAddr:X4}; ctl: {string.Join(" ", ctlNames)}");
                var watchN = new List<(string Name, int Node)>();
                if (_watchSpec != null)
                    foreach (var w in _watchSpec.Split(','))
                        watchN.Add((w.Trim(), WireCore.LookupNode(w.Trim())));
                foreach (var (n2, id2) in watchN) if (id2 == WireCore.EmptyNode) Console.Error.WriteLine($"# [watch] '{n2}' unresolved");
                string B(int[] ns) => ns.Length == 0 ? "--" : WireCore.ReadBits(ns).ToString("X2");
                long hcLeft = -1, cyc = 0; int prevPhi = WireCore.NodeStates[phi2];
                for (long i = 0; i < 714_732L * 3 && hcLeft != 0; i++)
                {
                    WireCore.Step(1);
                    int ph = WireCore.NodeStates[phi2];
                    if (prevPhi == 1 && ph == 0) cyc++;
                    int a = WireCore.ReadBits(abN);
                    if (hcLeft < 0 && a == trigAddr) { hcLeft = 24 * 20; Console.WriteLine($"# trigger at cyc {cyc}"); }
                    if (hcLeft > 0)
                    {
                        hcLeft--;
                        var cs = new StringBuilder();
                        for (int c = 0; c < ctl.Length; c++) cs.Append(ctl[c] != WireCore.EmptyNode ? (char)('0' + WireCore.NodeStates[ctl[c]]) : '-');
                        Console.WriteLine($"# cyc{cyc,6} phi2={ph} AB={a:X4} DB={B(dbN)} idl={B(idl)} alua={B(alua)} alub={B(alub)} sb={B(sb)} A={B(acc)} ADD={B(aluOut)} ctl={cs}");
                        var fir = new StringBuilder();
                        foreach (var (nm2, nd) in plaRows) if (WireCore.NodeStates[nd] == 1) fir.Append(nm2).Append(' ');
                        string f = fir.ToString();
                        if (f != prevFiring) { Console.WriteLine($"#      firing: {f}"); prevFiring = f; }
                        if (watchN.Count > 0)
                        {
                            var wb = new StringBuilder("#      w:");
                            foreach (var (n2, id2) in watchN) wb.Append($" {n2}={(id2 != WireCore.EmptyNode ? WireCore.NodeStates[id2].ToString() : "?")}");
                            Console.WriteLine(wb.ToString());
                        }
                    }
                    prevPhi = ph;
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --micro: run N frames then hex-dump work RAM $0200-$07FF (micro-ROM result harvesting) ──
        private static unsafe int MicroDump(string romPath, int frames)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.RegisterRawIdAliases = true;
                WireCore.EnableJoypadHandler = true;
                WireCore.LoadSystem(rom);
                WireCore.EnableDmcLatchShim();
                if (!_noAluShim) WireCore.EnableAluLatchShim();
                WireCore.EnableLxaMagicShim();
                WireCore.EnableFrameIrqShim();
                WireCore.EnablePpuBufShim();
                var microInput = ParseInputSpec(_inputSpec);
                if (microInput.Count > 0 && !WireCore.PadInit()) microInput.Clear();
                var watch = new List<(string Name, int Node)>();
                if (_watchSpec != null)
                    foreach (var w in _watchSpec.Split(','))
                        watch.Add((w.Trim(), WireCore.LookupNode(w.Trim())));
                foreach (var (n, id) in watch) if (id == WireCore.EmptyNode) Console.Error.WriteLine($"# [watch] '{n}' unresolved");
                for (int f = 0; f < frames; f++)
                {
                    foreach (var (btn, pf, rf) in microInput)
                    {
                        if (f + 1 == pf) WireCore.PadSetButton(0, btn, true);
                        if (f + 1 == rf) WireCore.PadSetButton(0, btn, false);
                    }
                    WireCore.RunFrame();
                    if (watch.Count > 0)
                    {
                        var sb2 = new StringBuilder($"# watch f{f + 1}:");
                        foreach (var (n, id) in watch) sb2.Append($" {n}={(id != WireCore.EmptyNode ? WireCore.NodeStates[id].ToString() : "?")}");
                        Console.Error.WriteLine(sb2.ToString());
                    }
                }
                var ram = WireCore.ResolveMemory("u1.ram");
                if (ram == null) { Console.Error.WriteLine("u1.ram unresolved"); return 2; }
                Console.WriteLine($"# marker $07FF = {ram.Read(0x7FF):X2}");
                var sb = new StringBuilder();
                for (int a = 0x200; a < 0x800; a += 16)
                {
                    sb.Clear(); sb.Append(a.ToString("X4")).Append(':');
                    for (int j = 0; j < 16; j++) sb.Append(' ').Append(ram.Read(a + j).ToString("X2"));
                    Console.WriteLine(sb.ToString());
                }
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        private static unsafe int BusTrace(string romPath, int startFrame)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.RegisterRawIdAliases = true;
                WireCore.LoadSystem(rom);
                WireCore.EnableDmcLatchShim();
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
                // DPCM IRQ pipeline stages (raw-id aliases; APUSim correspondence in comments)
                int nPcmFf    = WireCore.LookupNode("cpu.#13907"); // pcm_ff (inverted side feeding the latch)
                int nPcmLatch = WireCore.LookupNode("cpu.#13947"); // pcm_latch (pass gate on apu_clk1)
                int nDmc1     = WireCore.LookupNode("cpu.#11427"); // DMC1 = NOR(pcm_latch, NOT(ACLK2))
                int nNotDmc1  = WireCore.LookupNode("cpu.#11518"); // NOT(DMC1) input to set_pcm_irq NOR
                int nSoutSide = WireCore.LookupNode("cpu.#11473"); // sout_latch complement side
                Console.WriteLine($"# pipeline nodes: pcmff={nPcmFf} pcmlatch={nPcmLatch} dmc1={nDmc1} ndmc1={nNotDmc1} sout={nSoutSide}");
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
                    if (_btTail > 0)   // half-cycle microscope inside the event window
                    {
                        int a = WireCore.ReadBits(abN);
                        Console.WriteLine($"#    hc phi2={ph} AB={a:X4} clk1={S(nClk1)} clk2e={S(nClk2e)} abp={S(nAbp)} | pcmff*={S(nPcmFf)} pcmlatch={S(nPcmLatch)} dmc1={S(nDmc1)} ndmc1={S(nNotDmc1)} sout*={S(nSoutSide)} set={S(nSet)} irq={S(nIrq)}");
                    }
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
    }
}
