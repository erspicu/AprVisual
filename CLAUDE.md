# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

This repository is in a **pre-implementation / research stage**. The working directories `MD/` and `etc/` are empty placeholders; all current content lives under `ref/` as vendored reference material and design notes. There is no build system, package manifest, test suite, or lint configuration yet — do not invent commands for tooling that does not exist. When the user asks you to "build", "test", or "run" something, first confirm what they want set up.

## Goal of the project

Build a transistor-level chip simulator that takes Visual6502-style netlists (`segdefs` / `transdefs` / `nodenames`) and compiles them into a CUDA bit-parallel kernel for fast cycle-accurate simulation. The intended target is the NES — meaning the **2A03** (CPU, modified 6502) and **2C02** (PPU). The Visual6502 JS simulator under `ref/visual6502-master/` is the reference algorithm being ported.

The design plan lives in Chinese-language `.docx` files under `ref/GPU方案-.../GPU方案/` and covers, among other things:

- Parsing the Visual6502 netlist into a graph (C# is the planned host language for the compiler).
- Extracting NMOS logic / boolean equations from the transistor netlist.
- Detecting and breaking combinational loops (DFS-based loop detector).
- Handling pull-up / pull-down resistors (`PullType`).
- Emitting Verilog and/or CUDA from the graph; bit-parallel encoding for GPU efficiency.
- A NAND-gate → CUDA "micro EDA" compiler step.
- Discussion of cycle-accurate vs. higher-level abstraction trade-offs, and the analog-engineering challenges of the 2C02 PPU.

If the user references "the plan", "the design doc", or a specific topic by Chinese title, the source is one of those `.docx` files. They are binary Word documents — to read one, unzip it (`unzip -p file.docx word/document.xml`) and strip XML, or ask the user to paste the relevant section.

## Repository layout

```
MD/    — empty (intended for project notes / markdown)
etc/   — empty
ref/   — read-only reference material; do not modify
  visual6502-master/                      — upstream JS simulator (MIT-licensed core, CC-BY-NC-SA data)
    chipsim.js, wires.js, expertWires.js  — the simulation algorithm to port
    segdefs.js, transdefs.js, nodenames.js — 6502 revD netlist data
    chip-6800/, chip-z80/                 — same triplet for 6800 and Z80
  drive-download-.../                     — NES chip netlists
    visual2a03-{segdefs,transdefs,nodenames}.js  — NES CPU
    visual2c02-{segdefs,transdefs,nodenames}.js  — NES PPU
  GPU方案-.../GPU方案/*.docx                — Chinese-language design documents
  *.zip                                   — original archives (already extracted in-place)
```

## Visual6502 data format (load-bearing for the compiler)

When you eventually write parser code, these are the shapes you'll be parsing:

- **`segdefs`** — `[node, pull, layer, x1,y1, x2,y2, ...]` polygon coordinates per silicon segment. `pull` is `'+'` or `'-'` (pull-up / pull-down marker — this is the `PullType` referenced in the design docs).
- **`transdefs`** — `[name, gate, c1, c2, bbox[4], geom[5]]` per transistor. Three node references (gate / channel-1 / channel-2) plus geometry. NMOS-only.
- **`nodenames`** — map of human-readable name → node number (e.g. `vcc`, `vss`, `clk0`, address/data bus bits). The simulator pins `ngnd` / `npwr` from these.

The simulation loop in `chipsim.js` (`recalcNodeList` / `recalcNode` / `getNodeGroup` / `getNodeValue`) is the canonical algorithm to reproduce: it propagates state changes through transistor groups until quiescent, with a hard iteration cap as a loop-breaker. Any CUDA port has to reckon with the fact that this fixpoint is inherently sequential per cycle — this is exactly what the design docs (`GPU 模擬 FPGA 解決順序性問題.docx`, `迴路偵測與斬斷器.docx`) are working out.

## Working in this repo

- **Treat `ref/` as read-only.** Vendored upstream code and licensed data — modifications belong in new top-level directories (likely under `MD/` or new ones the user creates), not as edits inside `ref/`.
- **The 6502 data is CC-BY-NC-SA**; the JS simulator core is MIT. Per-file headers in `ref/visual6502-master/` are authoritative — preserve attribution if any of that code/data is copied into generated output.
- **No git repository** is initialized here yet (`Is a git repository: false`). Don't run `git` commands until the user sets one up.
