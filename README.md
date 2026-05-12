# AprVisual

**Turning a Visual6502-style switch-level NES netlist into an analyzable, verifiable, executable logic model — and ultimately a bit-parallel kernel that simulates many NES instances at once.**

The interesting part isn't the simulator; it's the **translation pipeline**:

```
silicon connectivity (segdefs / transdefs / nodenames)
   →  transistor + wire graph
   →  switch-level event simulation  (S1 — reference semantics)
   →  boolean / sequencing IR        (S2 — per-node next-state expressions)
   →  optimized, mostly-acyclic IR   (S3 — loop/SCC dissolution, coverage push)
   →  bit-sliced codegen (C# / LLVM) (S4 — many instances in parallel; NVPTX → GPU)
```

Every stage is gated by a **per-node equivalence check** against the previous one, so the fast backends are provably the same logic as the raw switch-level simulation.

Scope: the NES-001 board — the **2A03** (a modified 6502 + the APU) and the **2C02** (PPU) — plus the board's support TTL, NROM cartridges only.

---

## Status

| Stage | What | State |
|---|---|---|
| **S1** | C# rewrite of the MetalNES switch-level engine (`WireCore`: `.js` module loader, instancing, recalc/processQueue, the 256-entry group-resolution LUT, behavioral RAM/ROM handlers, system load, tracing) | ✅ done — passes the blargg test ROMs |
| **S2** | Netlist → boolean IR: `Expr` records (`NodeRef` / `Hold` / `Prev` / `Mux` / `And` / `Or` / `Not` / `Const`), `DriveAnalysis` (per-node pull-down / pull-up / transmission-gate ports), `NextStateModel`, `SccModel` (cross-coupled latch recovery), and the `IrEngine` interpreter — *checking mode* (verify IR ≡ S1 per node, per half-cycle) and *driving mode* (the IR drives the netlist, S1 only fills in the residue) | ✅ done — the equivalence gate passes in both modes |
| **S3** | Whole-system optimization → a fast backend. **Done so far:** Node Aliasing (fold out buffer/inverter nodes) and a general size-1/2 SCC solver (two-step algebraic fixpoint) — IR driving-coverage **46% → ~85%** (12.5k of 14.7k nodes), residual = 843 nodes in 56 small SCCs (counters / shift registers / phase rings) + ~1.4k genuine multi-driver buses, all still handled by the S1 engine. A topological-loop breaker (γ.2) is parked (it over-cuts; the residue will instead be compiled as fixed-K micro-iteration blocks). **Next:** codegen — IR → an executable bit-sliced model. | 🚧 in progress |
| **S4** | Emit C++ / Verilog / a CUDA bit-sliced kernel from the IR; per-node equivalence with the CPU IR. (The LLVM-IR path retargets straight to NVPTX.) | ⬚ not started |

See [`MD/impl/S3/00_S3_效率與優化.md`](MD/impl/S3/00_S3_效率與優化.md) for the live status snapshot, the firing-by-firing log, and the design docs (`01`–`03`).

---

## Build & run

Requires the **.NET 10 SDK** with the Windows Desktop workload (the project is `net10.0-windows`, WinForms, x64, `AllowUnsafeBlocks`).

```sh
dotnet build AprVisual.sln                                                  # from the repo root

dotnet run --project src/AprVisual -- --help
dotnet run --project src/AprVisual -- --rom path\game.nes                   # window: live 256x240 switch-level sim
dotnet run --project src/AprVisual -- --test path\test.nes                  # headless: run to the blargg $6000 signature → PASS/FAIL + exit code
dotnet run --project src/AprVisual -- --test-dir path\nes-test-roms\
dotnet run --project src/AprVisual -- --selftest                            # unit tests (graph / drive analysis / IR / …)
dotnet run --project src/AprVisual -- --trace path\test.nes --cycles 8      # per-cycle CPU-state trace
dotnet run --project src/AprVisual -- --trace-cmp path\test.nes [--engine ir]  # the equivalence gate: IR vs S1, per node, per half-cycle
dotnet run --project src/AprVisual -- --dump-scc                            # IR coverage + the residual-SCC anatomy
dotnet run --project src/AprVisual -- --benchmark path\test.nes [--engine ir]
```

The chip/board netlists are loaded from `--system-def-dir <dir>` (see *Netlist data* below).

---

## Repository layout

```
src/AprVisual/        the C# project (single WinExe; see src/AprVisual/README.md)
  Sim/WireCore.*.cs     S1 — the switch-level engine (unsafe, unmanaged hot data, one static partial class)
  Sim/Logic/*.cs        S2/S3 — Expr, NetlistGraph, DriveAnalysis, NextStateModel, SccModel, NodeAlias, IrEngine
  Render/*.cs           GDI blit of an unmanaged ARGB framebuffer
  Rom/NesRom.cs         minimal iNES parser (NROM)
  Test/TestRunner.cs    CLI: --rom / --test / --selftest / --trace / --trace-cmp / --dump-* / --benchmark
MD/                   all planning & design docs — Traditional Chinese
  struct/               the plan (08 = the operative roadmap) + original analysis
  note/                 research notes on MetalNES (the S1 reference)
  impl/S1, impl/S2, impl/S3   the implementation designs + firing-by-firing progress logs
data/                 runtime data drop point (system-def/) — see below
ref/                  read-only third-party reference material — gitignored, not vendored (see below)
tools/                helper scripts (e.g. tools/knowledgebase — querying Gemini for design reviews)
```

There is no separate test project — `--selftest` is the unit-test entry point, `--trace-cmp` is the equivalence gate.

---

## Netlist data

The 2A03/2C02 switch-level netlists (`segdefs` / `transdefs` / `nodenames`) and the MetalNES `.js` module wrappers are **not vendored here**. They live under `ref/metalnes-main/data/system-def/` (gitignored) and are passed via `--system-def-dir ref/metalnes-main/data/system-def`. The netlist data ultimately derives from **Visual2A03 / Visual2C02** (CC-BY-NC-SA), wrapped by **[MetalNES](https://github.com/trzy/MetalNES)** (MIT). Test ROMs (a nesdev test-ROM collection) are likewise not vendored. The format the parser consumes is described in `MD/note/02`.

---

## How it's verified

S1's switch-level engine is the reference semantics (itself a port of MetalNES's `wire_compute`, which is a port of Visual6502's `chipsim.js`). Every layer above it is checked, per node, per half-cycle, against S1:

- `--selftest` — hand-built tiny netlists (inverter, NAND, pass transistor, dynamic latch, cross-coupled cell, …) against their truth tables, plus the graph/drive/IR passes.
- `--trace-cmp <rom>` — runs S1 and verifies `EvalExpr(NextExpr[v]) == S1's value` for every observable node, every half-cycle (the S2.6 equivalence gate).
- `--trace-cmp <rom> --engine ir` — same, but with the IR *driving* the netlist (the S2.4/S2.5 bridge).
- `--trace <rom> --cycles N` — a per-CPU-cycle state trace; used to confirm a change doesn't perturb S1's behavior byte-for-byte.

---

## Notes

- The design docs in `MD/` are in Traditional Chinese; code, identifiers, comments and commit messages are in English.
- Built on the shoulders of [Visual6502](http://www.visual6502.org/) / Visual2A03 / Visual2C02 and [MetalNES](https://github.com/trzy/MetalNES) — see *Netlist data* for the licensing situation. Don't redistribute the `ref/` contents without checking their licences.
