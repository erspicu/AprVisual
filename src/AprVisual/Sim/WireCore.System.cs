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

        // Cached node/register handles used for the framebuffer + tracing (resolved after load).
        // (S1: a thin set; expand as needed — cf. handler_nes_system.h's wire_register_field list.)
        // public static int N_PpuClk0, N_Res, N_CpuClk0, ...;

        /// <summary>
        /// Load the nes-001 board definition + cartridge (NROM) for <paramref name="rom"/>, build the
        /// global netlist, allocate state, attach handlers, allocate the framebuffer.
        /// TODO: port system_state::Create:
        ///   def = LoadModuleDef(SystemDefDir, "nes-001");                       // (WireCore.Parse.cs)
        ///   cart = LoadModuleDef(SystemDefDir, "cart-mmu0"); add prgrom/chrrom/chrram sub-instances
        ///   AddInstance(def, "");  AddInstance(cart, "cart");                    // (WireCore.Module.cs)
        ///   Reset();                                                            // (WireCore.cs) build arrays/LUT
        ///   copy rom.PrgRom -> ResolveMemory("cart.prg.rom"); rom.ChrRom -> "cart.chr.rom"
        ///   AttachMemoryHandlers(); AttachClockHandler(); AttachVideoHandler();  // (WireCore.Handlers.cs)
        ///   ClockNode = LookupNode("clk");  (resolve other cached node ids)
        ///   FrameBuffer = AllocArray&lt;uint&gt;(ScreenW * ScreenH);
        ///   ResetNes();
        /// </summary>
        public static void LoadSystem(NesRom rom)
        {
            throw new NotImplementedException("WireCore.LoadSystem — port system_state::Create");
        }

        /// <summary>
        /// Power-on reset: clear RAMs, reset all node state, assert /res, run ~192 half-cycles,
        /// deassert /res — let the chip run its own reset sequence (reads the reset vector from PRG ROM).
        /// TODO: port handler_nes_system::reset():
        ///   clear cpuRam/ppuRam/chrRam;  (call WireCore.Reset() to re-power node state)
        ///   SetHigh(N_Res);  Step(12 * 8 * 2 /* = 192 */);  SetLow(N_Res);
        /// </summary>
        public static void ResetNes()
        {
            throw new NotImplementedException("WireCore.ResetNes — port handler_nes_system::reset");
        }

        /// <summary>Run the emulator until the next VBL boundary (one "frame"); returns half-cycles run.</summary>
        public static long RunFrame()
        {
            throw new NotImplementedException("WireCore.RunFrame");
        }
    }
}
