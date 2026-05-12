# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

The plan (`MD/struct/08`) had four stages; the user later widened S3 from "CPU proof" to **whole-system optimization** (every chip, the whole NES's throughput — see `MD/impl/S3/00`). Current state (2026-05):

- **S1 — C# rewrite of MetalNES** (the switch-level sim engine): **DONE.** `src/AprVisual/Sim/WireCore.*` — parser, module instancing, recalc/processQueue, group resolution LUT, handlers, system load, trace. Passes the blargg tests.
- **S2 — netlist → boolean IR** (`Expr` records, DriveAnalysis, NextStateModel, SccModel, the `IrEngine` interpreter — checking mode + driving mode + hybrid bridge): **DONE.** The per-node equivalence gate passes (IR ≡ S1 in both modes).
- **S3 — whole-system IR optimization** (was "CPU proof"; widened, and the "IR-CPU beats S1" gate dropped — the win is GPU parallelism, not single-instance CPU): at a stable stopping point. Done: γ.0 (Node Aliasing), γ.1 (size-1/2 SCC solver) — IR driving-coverage 46.3% → **~85%** (12.5k of 14.7k nodes; 843 still in 56 residual SCCs → S1, ~1379 hybrid multi-driver-bus nodes → S1). γ.2 (topological-loop breaker) — attempted, over-cuts, parked (`Gamma2Enabled=false`). Equivalence gate passes (IR ≡ S1, both modes). See `MD/impl/S3/00_S3_效率與優化.md` (the operative S3 doc + the firing-by-firing log) and `01`/`02` (the α/γ designs). The S3→S4 hand-off artifact = the IR (`IrEngine.NextExpr[]` / `EvalOrder[]` / `InScc[]` / the residual SCC components / the hybrid-bus set).
- **S4 — codegen**: **DONE (codegen + LLVM); GPU & the ping-pong run-path parked.** The IR's EvalOrder (~11.7k acyclic nodes) compiles to (a) C# Expression-tree-JIT'd chunked delegates (`IrEngine.CompileChunkedStep`, the default runtime step-4) and (b) an LLVM-MCJIT'd `void step(i8* cur, i8* prev)` (`Sim/Logic/LlvmCodegen.cs` via `LLVMSharp.Interop` + `libLLVM.runtime.win-x64` NuGet, `--llvm-step`); both also emittable as source (`--dump-emitted-cs [--bitsliced]` / `--dump-emitted-ll`). The 843 residual-SCC nodes have a fixed-K (K=32) Gauss-Seidel micro-block (`Step_scc_fixedK`, codegen artifact + validated model), the 2086 hybrid pass-transistor-bus nodes an S0/S1/W1 wired-resolution model (`Sim/Logic/BusResolver.cs` + `ValidateBusResolver` — validated; `Resolve_Buses()` emit). The bit-sliced (`ulong[]`, 64 instances/word) emit exists as a parked GPU-prototype artifact (multi-instance was dropped — the user only wants one instance correct + real-time). The **ping-pong run-path replacement** (have the codegen replace S1's step-5 ProcessQueue) is **parked** (`PingPongEnabled=false`, `--pingpong`) — "settle to fixpoint" can't reproduce S1's within-half-cycle event sequencing for the PPU's precharged-dynamic readout (same obstacle as γ.2); the runtime keeps S1's ProcessQueue for the SCC + bus + precharged paths + callbacks + behavioral memory. **Benchmark** (branch_timing/1, one frame): S1 ≈ 45K hc/s; IR+LLVM-step ≈ 18K (~2.5× slower than S1); IR+C#-JIT-step ≈ 7K (~6.4× slower) → the LLVM step-4 is ≈ 2.6× faster than the C# one, but the IR runtime is still slower than S1 (step-5 + the redundant full re-eval dominate). Equivalence gate passes. Design + the firing log: `MD/impl/S4/00_codegen_設計.md` (§11 the route, §12 GPU readiness, §13 the LLVM design).
- **TODO / after S4** (no `/loop` — do these on request): (1) the explicit LLVM `-O3` (`LLVMRunPasses("default<O3>")` — currently just MCJIT's default ≈-O2); (2) a **`cpu-opt` branch** forked ~end-of-S3 — an *event-driven* IR interpreter (dirty-set, re-eval only the nodes whose inputs changed — the "β" approach) — the actual path to fast single-instance; (3) S4.6 GPU compute shader (parked — see `MD/impl/S4/00` §12).

Per-node equivalence gate between stages. Scope: 2A03 + 2C02, NROM only. (An autonomous `/loop` cron drove firings 13–29 of S3; the user stopped it at firing 30 to consolidate docs.)

## Goal of the project

Take Visual6502-style switch-level netlists (`segdefs` / `transdefs` / `nodenames`) — the **2A03** (NES CPU, modified 6502) and **2C02** (NES PPU) — and turn them into analyzable, verifiable, executable logic models. **Goal = correctness + real-time-usable** (not "thousands of parallel instances" — that's a bonus); the value is the *translation pipeline* (silicon connectivity → graph → logic/sequencing abstraction → verifiable backends), not the GPU itself. Final form = two execution backends over the same IR: a GPU-friendly bit-sliced one (S4) and, branched off later, a CPU-optimized event-driven one.

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

- **Communicate with the user in Traditional Chinese (繁體中文)** — chat replies, summaries, explanations. Code, identifiers, code comments, and commit messages stay in English; planning/design docs in `MD/` stay in Traditional Chinese. **Prompts to Gemini** (via `tools/knowledgebase/gemini_query.py -f <utf8-prompt-file>`, e.g. the S2 per-step design reviews) are also in Traditional Chinese — pass the prompt via `-f` (a UTF-8 file) not argv, and read the response from `tools/knowledgebase/message/<timestamp>.txt` (the console print mojibakes UTF-8 on Windows; the log file is correct).
- Planning/design docs → `MD/` (Traditional Chinese); code → `src/`; runtime data → `data/`. Don't edit `ref/`.
- Commit/push only when the user asks. The repo is `github.com/erspicu/AprVisual` (private). End commit messages with the `Co-Authored-By: Claude Opus 4.7 (1M context) <noreply@anthropic.com>` trailer.
- When porting a `WireCore` stub, the TODO comment names the `ref/metalnes-main` function and line range; cross-check against `MD/note/`.
