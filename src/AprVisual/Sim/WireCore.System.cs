using System;
using System.Collections.Generic;
using AprVisual.Rom;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Top-level system assembly + reset + frame stepping — port of ref/metalnes-main:
        //      system.cpp (system_state::Create / setupRom) + handler_nes_system.h (reset / onFrameEnd).
        //    See MD/note/03_系統整合與週期推進.md §3.
        //
        //    Decisions (S1): only NROM (mapper 0); real reset (assert /res, run 192 half-cycles, deassert) —
        //    NOT the "pretend the bus reads NOP" shortcut (MD/struct/01 §11.3).

        public static string SystemDefDir = "data/system-def";   // where the .js module files live

        // ── cached nodes / registers / memory, resolved by ResolveCachedNodes() after the netlist is built ──
        public static int N_Res = EmptyNode;          // the board "res" line (→ CIC → cpu.res / ppu.res)
        public static int N_PpuInVblank = EmptyNode;  // rising edge = frame boundary
        public static int N_CpuSync = EmptyNode;      // high during opcode-fetch cycle
        public static int[] R_CpuA = [], R_CpuX = [], R_CpuY = [], R_CpuP = [], R_CpuS = [], R_CpuIr = [];
        public static int[] R_CpuPcl = [], R_CpuPch = [], R_CpuAb = [], R_CpuDb = [];
        public static Memory? M_EramRam;              // cart.eram.ram — the $6000 work RAM used by blargg test ROMs

        private static NesRom? _rom;

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build the global netlist for an NES board: load nes-001 + cart-mmu0 (+ the runtime PRG / CHR /
        /// extra-RAM cartridge sub-modules) and instantiate them. Port of the netlist-composition part of
        /// system_state::Create. Does NOT allocate hot arrays, attach handlers, or copy ROM bytes.
        /// </summary>
        public static void ComposeSystem(bool chrIsRam, bool isTestRom)
        {
            ResetBuild();   // clears all build-time state, re-registers vcc/vss

            var nes001 = LoadModuleDef(SystemDefDir, "nes-001");
            var cartMmu0 = LoadModuleDef(SystemDefDir, "cart-mmu0");

            // Append the runtime cartridge sub-modules to cart-mmu0 (prefix "" → instantiated under "cart").
            LoadModuleDef(SystemDefDir, "cart-mmu0-prgrom");
            cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = "cart-mmu0-prgrom" });

            string chrType = chrIsRam ? "cart-mmu0-chrram" : "cart-mmu0-chrrom";
            LoadModuleDef(SystemDefDir, chrType);
            cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = chrType });

            if (isTestRom)
            {
                LoadModuleDef(SystemDefDir, "cart-extraram");
                cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = "cart-extraram" });
            }

            AddInstance(nes001, "");
            AddInstance(cartMmu0, "cart");

            // S1.5: collapse always-on shorts + drop dead transistors + compact ids (behaviour-preserving;
            // also the canonical netlist S2 will work on). Toggle off with WireCore.EnableLowering / --no-lower.
            if (EnableLowering) LowerNetlist();
            else LastLowerStats = "(lowering disabled — --no-lower)";

            // math-algos G: Reverse Cuthill-McKee node-id reorder. Pure data-layout, behaviour-preserving.
            // Off by default; toggle with WireCore.EnableRcm / --rcm.
            if (EnableRcm) ApplyRcmOrdering();
            else LastRcmStats = "(RCM ordering disabled — default; --rcm to enable)";
        }

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Build + power on the full NES system for <paramref name="rom"/>: compose the netlist, attach the
        /// behavioral handlers (clock / RAM / ROM / video), copy the ROM bytes into the memory regions,
        /// resolve the cached probe nodes, then do a power-on reset. Port of system_state::Create.
        /// </summary>
        public static void LoadSystem(NesRom rom)
        {
            _rom = rom;
            bool chrIsRam = rom.ChrRom.Length == 0;
            bool isTestRom = rom.Path.Contains("nes-test-roms", StringComparison.OrdinalIgnoreCase)
                          || rom.Path.Contains("nes_test", StringComparison.OrdinalIgnoreCase);

            ComposeSystem(chrIsRam, isTestRom);   // WireCore.System.cs (ResetBuild + load defs + AddInstance)

            CopyRomBytes(rom);

            // Handlers add fake nodes/transistors via AddCallback — MUST run before Reset() (which sizes the hot arrays).
            AttachClockHandler();     // WireCore.Handlers.cs — toggles "clk" each half-cycle
            AttachMemoryHandlers();   // RAM (u1, u4) + ROM (cart.prg, cart.chr) handlers
            AttachVideoHandler();     // (S1: placeholder — no-op; the real PPU vid_ → RGB decode is later)

            ResolveCachedNodes();

            ResetNes(full: true);     // clear RAMs + Reset() + alloc FrameBuffer + assert/run/deassert res

            // Phase 2.5 Step 2: setup AFTER ResetNes so NodeCount is finalised (Reset() sets it from
            // NodeArrayCount). Doing this earlier would AllocArray<byte>(0) → zero-byte buffers, then
            // SetNodeState's CodegenInputChanged hook would read OOB on every node write during reset.
            // The reset itself ran without the watch armed; that's fine, the dispatcher will pick up
            // bit 0 on the next SetNodeState that flips an ALU input after reset deasserts.
            if (EnableCodegenDispatcher) CodegenDispatcherSetup();
        }

        private static void CopyRomBytes(NesRom rom)
        {
            var prg = ResolveMemory("cart.prg.rom");
            if (prg != null && rom.PrgRom.Length > 0)
            {
                if (rom.PrgRom.Length == 16 * 1024 && prg.Data.Length >= 32 * 1024)
                {
                    Array.Copy(rom.PrgRom, 0, prg.Data, 0, 16 * 1024);
                    Array.Copy(rom.PrgRom, 0, prg.Data, 16 * 1024, 16 * 1024);   // NROM-128: mirror the 16 KB bank
                }
                else
                {
                    Array.Copy(rom.PrgRom, 0, prg.Data, 0, Math.Min(rom.PrgRom.Length, prg.Data.Length));
                }
            }
            var chr = ResolveMemory("cart.chr.rom");
            if (chr != null && rom.ChrRom.Length > 0)
                Array.Copy(rom.ChrRom, 0, chr.Data, 0, Math.Min(rom.ChrRom.Length, chr.Data.Length));
        }

        private static void ResolveCachedNodes()
        {
            ClockNode      = LookupNode("clk");          // WireCore.Recalc.cs (reference only — toggled by the clock handler)
            N_Res          = LookupNode("res");
            N_PpuInVblank  = LookupNode("ppu.in_vblank");
            N_CpuSync      = LookupNode("cpu.sync");
            R_CpuA   = ResolveQuiet("cpu.a[7:0]");
            R_CpuX   = ResolveQuiet("cpu.x[7:0]");
            R_CpuY   = ResolveQuiet("cpu.y[7:0]");
            R_CpuP   = ResolveQuiet("cpu.p[7:0]");
            R_CpuS   = ResolveQuiet("cpu.s[7:0]");
            R_CpuIr  = ResolveQuiet("cpu.ir[7:0]");
            R_CpuPcl = ResolveQuiet("cpu.pcl[7:0]");
            R_CpuPch = ResolveQuiet("cpu.pch[7:0]");
            R_CpuAb  = ResolveQuiet("cpu.ab[15:0]");
            R_CpuDb  = ResolveQuiet("cpu.db[7:0]");
            M_EramRam = ResolveMemory("cart.eram.ram");
        }

        private static int[] ResolveQuiet(string expr)
        {
            var l = new List<int>();
            ResolveNodes(expr, l, quiet: true);
            return l.ToArray();
        }

        /// <summary>
        /// Reset the NES. <paramref name="full"/> = power-on (clear RAMs, re-power node state, re-alloc the
        /// framebuffer); otherwise a soft reset. Then assert /res for 192 half-cycles and deassert.
        /// Port of handler_nes_system::reset() / softReset().
        /// </summary>
        public static void ResetNes(bool full)
        {
            // PARTIAL mitigation: force --prune-merge off during power-on settle + reset assertion.
            // Cross-coupled latches (PPU vpos/hpos counters, vblank-fire comparators, OAM/palette 6T
            // cells) have two valid stable states; prune-merge's "c1==c2 skip" can let them lock into
            // the INVERTED stable state during the initial settle. This wrapping makes the 500K-hc
            // node state byte-identical to baseline. NOT A FULL FIX: divergence still develops over
            // many millions of half-cycles during normal frame stepping (D-FF clock-edge state
            // changes also expose the skip), so full_palette @ 48 frames still renders black even
            // with this wrapper. A real fix requires identifying cross-coupled SCCs at parse time
            // and never skipping for their nodes. See MD/impl/math-algos/01_results.md §observability.
            bool savedPruneMerge = EnablePruneMerge;
            EnablePruneMerge = false;
            try
            {
                if (full)
                {
                    ResolveMemory("u1.ram")?.Clear();
                    ResolveMemory("u4.ram")?.Clear();
                    ResolveMemory("cart.chrram.ram")?.Clear();
                    Reset();                                  // re-power node state + rebuild hot arrays (frees FrameBuffer too)
                    FrameBuffer = AllocArray<uint>(ScreenW * ScreenH);
                    RecomputeAllNodes();                      // settle the raw power-on state — MetalNES's resetState() does this
                }
                if (N_Res != EmptyNode)
                {
                    SetHigh(N_Res);
                    Step(12 * 8 * 2);                         // = 192 half-cycles with reset asserted
                    SetLow(N_Res);
                }
                else
                {
                    Console.Error.WriteLine("ResetNes: no 'res' node — sim may not start");
                }
            }
            finally { EnablePruneMerge = savedPruneMerge; }
        }

        public static void SoftReset() => ResetNes(full: false);

        // ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Step the simulation until the PPU's in-vblank flag rises (one "frame" boundary), or until
        /// <paramref name="maxHalfCycles"/> have run. Returns the number of half-cycles actually stepped.
        /// Port of the frame-boundary behaviour of handler_nes_system (add_edge_callback on ppu.in_vblank).
        /// </summary>
        public static long RunFrame(long maxHalfCycles = 1_200_000)
        {
            long start = Time;
            if (N_PpuInVblank == EmptyNode)
            {
                // no vblank node available — just step a fixed amount (~one NES frame of half-cycles)
                Step((int)Math.Min(maxHalfCycles, 714_736));
                return Time - start;
            }
            bool prev = NodeStates[N_PpuInVblank] != 0;
            for (long i = 0; i < maxHalfCycles; i++)
            {
                Step(1);
                bool now = NodeStates[N_PpuInVblank] != 0;
                if (!prev && now) break;                  // rising edge → frame boundary
                prev = now;
            }
            return Time - start;
        }
    }
}
