# AprVisual (S1)

S1 of the four-stage roadmap (`MD/struct/08`): a C# rewrite of the MetalNES switch-level
simulation engine — the foundation everything else builds on. See `MD/struct/09` for the
implementation-style decisions this skeleton follows (`unsafe` + unmanaged pointers, monolithic
`static partial class WireCore`, GDI `SetDIBitsToDevice` rendering, CLI args-branching).

## Build / run

```
dotnet build AprVisual.sln                          # from the repo root
dotnet run --project src/AprVisual -- --help
dotnet run --project src/AprVisual -- --rom path\to\game.nes      # window: live 256x240 sim
dotnet run --project src/AprVisual -- --test path\to\test.nes     # headless: PASS/FAIL + exit code
dotnet run --project src/AprVisual -- --test-dir path\to\nes-test-roms\
```

Requires the .NET 10 SDK with the Windows Desktop workload (`net10.0-windows`, WinForms), x64.

## Status

This is the **skeleton**. Structure, types, the FlagsToState resolution rule, the iNES parser,
the GDI rendering layer, the CLI, and the app shell are real; the simulation engine itself
(`WireCore.*` — parse / module composition / recalc / group / handlers / system / trace) is
stubbed with `NotImplementedException` and TODO comments that point at the corresponding
`ref/metalnes-main/source/metalnes/wire_module.cpp` functions. Running `--rom` currently pops up
the window and reports "sim not implemented yet".

Port order (matches `MD/struct/08` S1.1–S1.7):
1. `WireCore.Parse.cs`     — `.js` module-format loader (`wire_defs::Load`)
2. `WireCore.Module.cs`    — instance node-id alloc, `connection = always-on transistor`, name resolution
3. `WireCore.cs Reset()`   — build flattened transistor lists + the FlagsToState LUT
4. `WireCore.Group.cs`     — `addNodeToGroup` / `getNodeValue` (already has the LUT + scaffolding)
5. `WireCore.Recalc.cs`    — `processQueue` / `recalcNode` / `setNodeState` / `enqueueNode` / `stepCycle`
6. `WireCore.Handlers.cs`  — clock / RAM / ROM handlers; `add_callback` (fake transistor)
7. `WireCore.System.cs`    — load `nes-001` + cart, real reset; `WireCore.Trace.cs` — `$6000` detection

## Layout

```
src/AprVisual/
  AprVisual.csproj            net10.0-windows, WinExe, WinForms, AllowUnsafeBlocks, x64
  Program.cs                  Main: args>0 → Test.TestRunner ; else → MainForm
  MainForm.cs                 256x240 Panel; blits WireCore.FrameBuffer via Render.NativeGDI
  Render/
    NativeApi.cs              gdi32 P/Invoke + BITMAPINFO/BITMAPINFOHEADER (from ref/AprNes)
    NativeGDI.cs              Init / Present (SetDIBitsToDevice or StretchDIBits) / Free
  Rom/
    NesRom.cs                 minimal iNES (.nes) parser (NROM scope)
  Sim/
    WireCore.cs               fields, NodeFlags/NodeValue/NodeInfo/Transistor, Reset/Shutdown
    WireCore.Native.cs        NativeMemory.AlignedAlloc wrappers + one-shot FreeUnmanagedMemory
    WireCore.Parse.cs         .js module-format parsing (stub)
    WireCore.Module.cs        instance composition / connection / ResolveNodes (stub)
    WireCore.Recalc.cs        recalc loop / step (stub)
    WireCore.Group.cs         node group + getNodeValue + FlagsToState LUT (LUT real, walk stubbed)
    WireCore.Handlers.cs      handler chain + callbacks + behavioral memory (stub)
    WireCore.System.cs        load nes-001 + cart, real reset (stub)
    WireCore.Trace.cs         trace columns + $6000 signature detection (stub)
  Test/
    TestRunner.cs             --rom / --test / --test-dir / --benchmark CLI
data/system-def/              .js module definitions (nes-001.js, 2a03/, cart-mmu0*, support chips)
                              — to be supplied (from ref/metalnes-main/data/system-def or our own)
```
