using System;
using AprVisual.Rom;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Top-level system assembly + reset — port of ref/metalnes-main:
        //      system.cpp (system_state::Create / setupRom) + handler_nes_system.h (reset()).
        //    See MD/note/03_系統整合與週期推進.md §3.
        //
        //    Decisions (S1): only NROM (mapper 0); real reset (assert /res, run, deassert) —
        //    NOT the "pretend the bus reads NOP" shortcut (MD/struct/01 §11.3).

        public static string SystemDefDir = "data/system-def";   // where the .js module files live

        // Cached node ids resolved after the netlist is built (used by the framebuffer / tracing).
        // (S1: a thin set; expand as needed — cf. handler_nes_system.h's wire_register_field list.)
        // public static int N_PpuClk0, N_Res, N_CpuClk0, ...;

        /// <summary>
        /// Build the global netlist for an NES board: load nes-001 + cart-mmu0 (+ the runtime PRG/CHR/extra
        /// sub-modules) and instantiate them. Port of the netlist-composition part of system_state::Create.
        /// Does NOT allocate the hot arrays (Reset() — Step 3), attach handlers (Step 6), or load ROM bytes.
        /// </summary>
        public static void ComposeSystem(bool chrIsRam, bool isTestRom)
        {
            ResetBuild();   // clears all build-time state, re-registers vcc/vss

            var nes001 = LoadModuleDef(SystemDefDir, "nes-001");
            var cartMmu0 = LoadModuleDef(SystemDefDir, "cart-mmu0");

            // Append the runtime cartridge sub-modules to cart-mmu0 (prefix "" → instantiated under "cart"):
            // PRG ROM, CHR ROM or CHR RAM, and (for test ROMs) the extra work-RAM at $6000.
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
        }

        /// <summary>
        /// Load the full NES system for <paramref name="rom"/>: build the netlist, allocate state, attach
        /// handlers, allocate the framebuffer, power-on reset. Port of system_state::Create.
        /// TODO: only the netlist composition (ComposeSystem) is implemented so far — the rest is Steps 3, 6, 7:
        ///   ComposeSystem(chrIsRam: rom.ChrRom.Length == 0, isTestRom: …);
        ///   Reset();                                                            // Step 3 — build hot arrays + LUT
        ///   copy rom.PrgRom -> ResolveMemory("cart.prg.rom"); rom.ChrRom -> "cart.chr.rom"
        ///   AttachClockHandler(); AttachMemoryHandlers(); AttachVideoHandler(); // Step 6
        ///   ClockNode = LookupNode("clk");  (resolve other cached node ids)
        ///   FrameBuffer = AllocArray&lt;uint&gt;(ScreenW * ScreenH);
        ///   ResetNes();                                                         // Step 7
        /// </summary>
        public static void LoadSystem(NesRom rom)
        {
            ComposeSystem(chrIsRam: rom.ChrRom.Length == 0, isTestRom: rom.Path.Contains("nes-test-roms", StringComparison.OrdinalIgnoreCase));
            throw new NotImplementedException("WireCore.LoadSystem — netlist composed; Reset()/handlers/framebuffer/ResetNes still TODO (Steps 3, 6, 7)");
        }

        /// <summary>
        /// Power-on reset: clear RAMs, reset all node state, assert /res, run ~192 half-cycles, deassert /res.
        /// TODO (Step 7): port handler_nes_system::reset() — SetHigh(N_Res); Step(12*8*2); SetLow(N_Res);
        /// </summary>
        public static void ResetNes()
            => throw new NotImplementedException("WireCore.ResetNes — port handler_nes_system::reset (Step 7)");

        /// <summary>Run the emulator until the next VBL boundary (one "frame"); returns half-cycles run.</summary>
        public static long RunFrame()
            => throw new NotImplementedException("WireCore.RunFrame (Step 7)");
    }
}
