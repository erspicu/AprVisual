# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

**The performance investigation is complete; its outcome is a documented, falsifiable negative result.** The original four-stage plan (`MD/struct/08`: S1 switch-level engine → S2 netlist→IR → S3 CPU proof → S4 codegen+GPU) was built and verified, and a later CPU-first restart — **S2 "Escape-1"** (automatic logic extraction → faster behavioral sim) — was taken end-to-end. What stands:

- **S1, the switch-level engine, is the golden, fastest artifact** (~80K half-cycles/s, bit-exact). It lives in **`src/AprVisual.S1/`** (.NET 10 console, x64; `Sim/WireCore.*`, ported from `ref/metalnes-main`), with a bit-identical Rust port in `experiment/rust-s1/`.
- **Every abstraction tried was slower than S1 on one CPU core**: IR interpreter ≈ −2.5%; AOT/codegen 3–6× slower; GPU single-instance ~10.7× slower; the Escape-1 route oblivious 45× / compiled 84× slower (i-cache), macro-event cone compression only 1.1×. Root cause: the event-driven engine already runs at the netlist's natural minimum granularity (~1.4-node conducting group); no auto-derivable static DAG. **Real time (~500–600×) is unreachable via this route.**
- **What Escape-1 DID prove** (fully automatic, in `src/AprVisual.S2/`): ~**98.9%** of the chip's activity is reducible to logic + registers, ~**1.1%** is genuine analog — a clean reducibility result that does *not* translate to single-core speed. Write-up: **`WebSite/study.html`**; design notes in **`MD/S2/`**.

Three source forks: **`src/AprVisual.S1/`** (golden engine — the canonical artifact) · **`src/AprVisual.S2/`** (the Escape-1 investigation engine: `--miter` / `--compile` / `--cones`; conclusions reached) · **`src/AprVisual.Deprecated/`** (the original WinForms app + the S2/S3/S4 IR/codegen/GPU experiments — reference only). Per-node / behavioral equivalence gates were used between stages throughout. Scope: 2A03 + 2C02 (whole NES), NROM.

## Goal of the project

Take Visual6502-style switch-level netlists (`segdefs` / `transdefs` / `nodenames`) — the **2A03** (NES CPU, modified 6502) and **2C02** (NES PPU) — and turn them into **analyzable, verifiable, executable logic models**. The value is the *translation pipeline* (silicon connectivity → graph → logic/sequencing abstraction → verifiable backends) and what it lets us measure/prove about the chip.

> **Correction (2026-06-02):** earlier docs framed the "ultimate goal" as a *CUDA bit-parallel kernel for many NES instances (throughput)*. That was **an early AI misreading of the user's prompt — it was never a preset goal.** GPU/many-instance throughput is at most an orthogonal aside, not the project's objective. The objective is the CPU translation/verification pipeline above. Do not reintroduce "many-instance GPU throughput" as the goal.

## Build / run

```
dotnet build AprVisual.sln                                                            # builds both forks
# Active fork — src/AprVisual.S1/ (headless console; ALL current work):
dotnet run --project src/AprVisual.S1 -- --benchmark path\game.nes --bench-hc 200000  # throughput hc/s + NodeStates checksum
dotnet run --project src/AprVisual.S1 -- --test path\test.nes                         # run to blargg $6000 PASS/FAIL, exit code
dotnet run --project src/AprVisual.S1 -- --test-dir path\nes-test-roms\
# Deprecated fork — src/AprVisual.Deprecated/ (WinForms live window; reference only):
dotnet run --project src/AprVisual.Deprecated -- --rom path\game.nes                  # window: live 256x240 switch-level sim
```

Requires the .NET 10 SDK; the deprecated WinForms fork also needs the Windows Desktop workload (`src/AprVisual.S1/` is portable net10.0). There is no test project / lint config yet. See `src/AprVisual.S1/README.md` for the layout.

## Documentation (`MD/`)

All planning/design docs are in Traditional Chinese under `MD/` (the code does **not** go here):

- `MD/struct/` — the (now-historical) plan: `08` = the original four-stage roadmap (carries a 2026-06-02 correction banner — the GPU/many-instance framing was an early misread, not a goal); `09` = S1 implementation-style decisions (references `ref/AprNes`); `00`–`07` = the original analysis; `INDEX.md` lists them.
- `MD/S2/` — the **S2 Escape-1 investigation** (design + results): logic-extraction / oblivious-eval / Dynamic-Miter / cone-compression notes that conclude the performance question (see `WebSite/study.html` for the public write-up).
- `MD/note/` — research notes on existing implementations: currently `ref/metalnes-main` (the S1 reference) — 7 files covering its sim core, module system, system integration, validation, and what it does/doesn't do vs our plan.

Subdirectories under `MD/` are organized by need; new category dirs may be created as the project grows.

## Source layout (`src/AprVisual.S1/` — active fork)

> **`src/AprVisual.S1/` is the canonical/golden engine** — the distilled, headless **console** perf fork (portable net10.0; `--benchmark` / `--test` / `--test-dir` / frame-dump; no live window — PNG via `Render/PngWriter.cs`). **`src/AprVisual.S2/`** is a verbatim S1 copy that carried the (now-concluded) Escape-1 investigation — it adds `Sim/WireCore.{Coverage,Extract,Logic,Cones,Compile}.cs` and the `--miter`/`--compile`/`--cones` modes; treat it as the experiment record. The original WinForms fork is **`src/AprVisual.Deprecated/`** (live-window app + the IR / codegen / dispatcher / levelize / diag + S4 GPU experiments) — **reference only; don't modify unless explicitly asked.** All three share the `Sim/WireCore.*` layout below; `MainForm.cs` + `Render/NativeGDI.cs` exist only in the deprecated fork.

The deprecated fork is a single `WinExe` (net10.0-windows, WinForms, `AllowUnsafeBlocks`, x64); `Program.Main`: args → `Test.TestRunner` (headless), else → `MainForm`.

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
- **Project rule: commit & push after every completed stage of output or modification** — don't wait to be asked (chain `git add` + `git commit -F` + `git push` in one call). The repo is `github.com/erspicu/AprVisual` (public). End commit messages with the `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` trailer.
- When porting a `WireCore` stub, the TODO comment names the `ref/metalnes-main` function and line range; cross-check against `MD/note/`.
