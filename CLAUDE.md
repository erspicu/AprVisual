# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

The project plan is settled and implementation has started. The plan (`MD/struct/08`) has four stages:

- **S1 — C# rewrite of MetalNES** (the switch-level sim engine) → the foundation. **In progress.** The skeleton lives in `src/AprVisual` (.NET 10, WinForms, x64); the engine itself (`Sim/WireCore.*`) is stubbed with `NotImplementedException` + TODOs that point at the `ref/metalnes-main` functions to port.
- **S2 — netlist → IR** (loop/SCC detection, boolean extraction into an `Expr` IR, an IR interpreter).
- **S3 — CPU proof**: the IR interpreter must be correct *and* benchmarked as much faster than the raw switch-level interpreter (the go/no-go gate for S4).
- **S4 — codegen + GPU**: emit C++/Verilog + a CUDA bit-sliced kernel; per-node equivalence with the CPU IR interpreter.

Each stage has a per-node equivalence gate before the next may begin. Scope: 2A03 first (PPU deferred), NROM only.

## Goal of the project

Take Visual6502-style switch-level netlists (`segdefs` / `transdefs` / `nodenames`) — the **2A03** (NES CPU, modified 6502) and **2C02** (NES PPU) — and turn them into analyzable, verifiable, executable logic models, ultimately a CUDA bit-parallel kernel for fast cycle-accurate simulation of many NES instances. The value is the *translation pipeline* (silicon connectivity → graph → logic/sequencing abstraction → verifiable backends), not the GPU itself.

## Build / run

```
dotnet build AprVisual.sln                                       # from the repo root
dotnet run --project src/AprVisual -- --help
dotnet run --project src/AprVisual -- --rom path\game.nes        # window: live 256x240 switch-level sim
dotnet run --project src/AprVisual -- --test path\test.nes       # headless: run to the blargg $6000 signature, print PASS/FAIL, exit code
dotnet run --project src/AprVisual -- --test-dir path\nes-test-roms\
```

Requires the .NET 10 SDK with the Windows Desktop workload. There is no test project / lint config yet. See `src/AprVisual/README.md` for the layout and the S1 port order.

## Documentation (`MD/`)

All planning/design docs are in Traditional Chinese under `MD/` (the code does **not** go here):

- `MD/struct/` — the plan: `08` = the operative four-stage roadmap; `09` = S1 implementation-style decisions (references `ref/AprNes`); `00`–`07` = the original analysis (overview, technical guide, MVP, risks, etc.); `INDEX.md` lists them.
- `MD/note/` — research notes on existing implementations: currently `ref/metalnes-main` (the S1 reference) — 7 files covering its sim core, module system, system integration, validation, and what it does/doesn't do vs our plan.

Subdirectories under `MD/` are organized by need; new category dirs may be created as the project grows.

## Source layout (`src/AprVisual/`)

Single `WinExe` (net10.0-windows, WinForms, `AllowUnsafeBlocks`, x64). `Program.Main`: args → `Test.TestRunner` (headless), else → `MainForm`.

- `Sim/WireCore.*.cs` — the engine: one monolithic `static unsafe partial class WireCore` (AprNes style), split into `.Parse` (`.js` module loader) / `.Module` (instance node-id alloc; *connection = always-on transistor*; name resolution `a[7:0]`/`a[]`/`x|y|z`/`*wildcard`) / `.Recalc` (recalcNodeList/processQueue/recalcNode/setNodeState/enqueueNode/stepCycle) / `.Group` (addNodeToGroup/getNodeValue; the 256-entry `FlagsToState` LUT is real) / `.Handlers` (per-cycle handler chain; callbacks = fake transistor; behavioral RAM/ROM) / `.System` (load `nes-001` + cart, real reset) / `.Trace` (trace columns; blargg `$6000` detection) / `.Native` (`NativeMemory.AlignedAlloc` wrappers, one-shot free). Hot per-node data is unmanaged (`byte* NodeStates`, `int* TransistorList`, `NodeInfo* NodeInfos`), zero-bounds-check inner loops.
- `Render/NativeApi.cs` + `Render/NativeGDI.cs` — GDI `SetDIBitsToDevice` / `StretchDIBits` blit of an unmanaged ARGB framebuffer onto a control HDC (lifted from `ref/AprNes/tool/NativeRendering.cs`). No PictureBox/Bitmap.Image.
- `Rom/NesRom.cs` — minimal iNES parser (NROM scope).
- `MainForm.cs` — a 256×240 Panel; blits `WireCore.FrameBuffer` via `NativeGDI`.
- `Test/TestRunner.cs` — `--rom` / `--test` / `--test-dir` / `--benchmark` CLI.
- `data/system-def/` — `.js` module definitions to be supplied (from `ref/metalnes-main/data/system-def/` or written fresh); not vendored. See `data/README.md`.

## `ref/` — read-only reference material (gitignored, not vendored)

Everything under `ref/` is excluded from version control via `.gitignore` (licensed data / large third-party projects). Treat it as read-only; don't edit it; don't copy code/data out of it without checking its licence.

```
ref/visual6502-master/   upstream JS simulator — chipsim.js/wires.js/expertWires.js (MIT) + 6502/6800/Z80 netlist data (CC-BY-NC-SA)
ref/drive-download-*/     Visual2A03 / Visual2C02 raw netlists (segdefs/transdefs/nodenames .js); 2A03/2C02 transdefs have a 7th column = weak/depletion flag, and nodenames already name internal registers (a0-7, x0-7, pcl0-7, ...)
ref/metalnes-main/        MetalNES — C++ transistor-level NES sim using Visual2A03/2C02 + board TTL chips; the S1 reference implementation (key files: source/metalnes/wire_module.cpp, wire_defs.cpp, system.cpp, handler_*.h, data/system-def/*.js)
ref/AprNes/               the user's existing C# + WinForms NES emulator (net48/net10 dual-target); reference for the rendering path (tool/NativeRendering.cs) and the design style adopted for S1
ref/GPU方案*/             Chinese-language .docx design documents (the original notes; condensed into MD/struct/00-07)
ref/mds/                  English/Chinese markdown of the same design notes
ref/*.zip                 original archives
```

To read a `.docx`: `unzip -p file.docx word/document.xml` and strip XML, or ask the user to paste the relevant section.

## Visual6502 / MetalNES data format (load-bearing for the parser)

- **`segdefs`** — `[node, pull, layer, x1,y1, x2,y2, ...]` polygon per silicon segment. `pull` is `'+'` or `'-'` (pull-up / pull-down — keep **both**; MetalNES only kept `'+'`).
- **`transdefs`** — `[name, gate, c1, c2, bbox[4], geom[5]]` (Visual6502) or `[..., weak]` (2A03/2C02 — the 7th boolean = weak/depletion-load device; **use it** to separate strong pull-down vs weak pull-up). NMOS-only; c1/c2 are interchangeable.
- **`nodenames`** — name → node number (`vcc`, `vss`, `clk0`, `res`, address/data bus, and for 2A03 the internal regs `a0..a7`/`x0..x7`/`y0..y7`/`pcl0..pcl7`/...). Names may have `/` `#` `~` `_` prefixes; PPU multiplexes `ab[7:0]`/`db[7:0]` onto the same nodes.
- MetalNES `.js` modules also have `pins`, `modules` (sub-instances), `connections`, `pullups`, `forceCompute`, `memory:{}`, and `*_files` references to external netlist `.js` — see `MD/note/02`.

The canonical algorithm to reproduce is `wire_compute` in `ref/metalnes-main/source/metalnes/wire_module.cpp` (`recalcNodeList`/`processQueue`/`recalcNode`/`computeNodeGroup`/`addNodeToGroup`/`getNodeValue`) — itself an optimized port of Visual6502's `chipsim.js`. It propagates state changes through connected transistor groups until quiescent, resolving each group via a flags-OR → 256-entry LUT (GND wins → VCC/pull-up → external drive → hold previous → 0; purely-floating groups: largest-capacitance node wins). The S1 C# port lives in `Sim/WireCore.Group.cs` / `.Recalc.cs`. See `MD/note/01` for the walkthrough and the improvements decided for S1 (distinguish "driven high" from "hold previous"; actually use the weak flag).

## Working in this repo

- **Communicate with the user in Traditional Chinese (繁體中文)** — chat replies, summaries, explanations. Code, identifiers, code comments, and commit messages stay in English; planning/design docs in `MD/` stay in Traditional Chinese.
- Planning/design docs → `MD/` (Traditional Chinese); code → `src/`; runtime data → `data/`. Don't edit `ref/`.
- Commit/push only when the user asks. The repo is `github.com/erspicu/AprVisual` (private). End commit messages with the `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer.
- When porting a `WireCore` stub, the TODO comment names the `ref/metalnes-main` function and line range; cross-check against `MD/note/`.
