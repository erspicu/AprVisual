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
        // ── HC-granularity instruments: --micro / --trace. ──
        // ── --micro: run N frames then hex-dump work RAM $0200-$07FF (micro-ROM result harvesting) ──
        private static unsafe int MicroDump(string romPath, int frames)
        {
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            try
            {
                WireCore.RegisterRawIdAliases = true;
                WireCore.EnableJoypadHandler = true;
                WireCore.LoadSystem(rom);   // S1A: LoadSystem arms the full M1–M6 mechanism set
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
                // physical-OAM dump (netlist SRAM cells) — read_buffer #67 diagnostic
                for (int row = 0; row < 32; row += 16)
                {
                    var os = new StringBuilder($"OAM{row:X2}:");
                    for (int b = row; b < row + 16; b++)
                    {
                        int val = 0;
                        for (int bit = 0; bit < 8; bit++)
                        {
                            int nn = WireCore.LookupNode($"ppu.oam_ram_{b:X2}_b{bit}");
                            if (nn != WireCore.EmptyNode && WireCore.IsNodeHigh(nn)) val |= (1 << bit);
                        }
                        os.Append(' ').Append(val.ToString("X2"));
                    }
                    Console.WriteLine(os.ToString());
                }
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
