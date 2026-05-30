# AprVisual

**A Visual6502-style switch-level NES simulator — and an honest log of what made it faster, and what didn't.**

🌐 **[Project site & full write-up →](https://erspicu.github.io/AprVisual/)**  ·  📊 **[Community benchmark leaderboard →](https://baxermux.org/myemu/AprVisual/)**  ·  ⬇ **[Download the benchmark →](https://github.com/erspicu/AprVisual/releases)**

> 中文讀者:官網支援中英切換(預設依瀏覽器語系),點上面的 **Project site** 即可。

---

AprVisual takes **Visual6502-style transistor netlists** of the NES CPU (**2A03**) and PPU (**2C02**) and turns them into analyzable, verifiable, executable logic models. It simulates the chip at the level of **individual transistors and wires — not opcodes** — and lets the CPU's behavior *emerge* from the physics. The result is bit-for-bit faithful to the real silicon, and (necessarily) far slower than real time.

The real value here is the **translation pipeline** — silicon connectivity → graph → logic/sequencing → verifiable backends — and the **honest record of which optimizations actually work on real hardware**, not any single backend.

> New to "switch-level simulation"? Start with the plain-language **[primer](https://erspicu.github.io/AprVisual/primer.html)** — it explains netlists, conduction detection, the settle queue, and the graph/BFS ideas behind all of this.

## The honest story

The original plan was a four-stage pipeline (S1 switch-level engine → S2 netlist→IR → S3 CPU proof → S4 codegen + GPU) to push the simulation toward real time. We built and verified those stages — and found the counter-intuitive result that the "obvious" abstractions (**IR + codegen, or a GPU kernel**) ended up **slower** than the direct switch-level interpreter (code bloat, lost timing/correctness, algorithmic redundancy in batch re-evaluation). Real time is **~600× out of reach** and known-unreachable via this route.

So the focus became **pushing S1 — the pure switch-level engine — to its limit**, in both **C#** and **Rust**, and documenting the wins and the (many) dead-ends. The recurring lesson, which independently matches what the Visual NES author found in 2017: the gains come from **less work + smaller (cache-fitting) data + tighter codegen**, *not* from cleverer data structures or algorithms.

## Lineage

**Visual6502 (`chipsim.js`)** → **MetalNES** → **AprVisual S1 (C# + Rust)**.

The site documents each step with source-line citations:
- [What MetalNES added over Visual6502](https://erspicu.github.io/AprVisual/metalnes.html)
- [What AprVisual S1 added over MetalNES](https://erspicu.github.io/AprVisual/design.html)
- [How it compares to other netlist NES/6502 simulators](https://erspicu.github.io/AprVisual/comparison.html)

## Performance

On an AMD Ryzen 7 3700X, benchmarking `full_palette` (300k master half-cycles):

| Engine | Rate | Per frame | vs NES NTSC real-time |
|---|---|---|---|
| Rust (`rust-s1`) | ~72K hc/s | ~9.9 s | ~600× too slow |
| C# (`AprVisual.S1`) | ~67K hc/s | ~10.6 s | ~640× too slow |

Both engines produce **bit-identical** output (same checksum). NES NTSC real time needs **42,954,552 hc/s**. **Got a faster CPU? [Run the benchmark and share your numbers.](https://baxermux.org/myemu/AprVisual/)**

## Run the benchmark

The easiest path is the prebuilt, self-contained package (no .NET install needed; Windows + macOS, both engines):

1. **[Download `AprVisualBenchMark.zip`](https://github.com/erspicu/AprVisual/releases)** and unzip.
2. Windows: `run_csharp.bat` / `run_rust.bat` (optional arg = half-cycle count). macOS: `chmod +x *.sh` then `./run_csharp.sh` / `./run_rust.sh`.
3. Each run prints a performance block **and** writes a parseable JSON `.log` to `log/` — upload it to the [leaderboard](https://baxermux.org/myemu/AprVisual/).

## Build from source

Requires the **.NET 10 SDK** (and **Rust/cargo** for the Rust engine).

```sh
dotnet build AprVisual.sln                 # C# engines
( cd experiment/rust-s1 && cargo build --release )   # Rust engine (rust-s1)
```

The optimized switch-level engine lives in `src/AprVisual.S1/` (C#, headless console) and `experiment/rust-s1/` (Rust). See `src/AprVisual/README.md` for the original layout, and `MD/` for the (Traditional Chinese) design docs.

## Repository layout

| Path | What |
|---|---|
| `src/AprVisual.S1/` | The S1 switch-level engine — C#, the focus of optimization. |
| `experiment/rust-s1/` | The Rust port of the S1 engine (bit-identical). |
| `src/AprVisual/` | The original engine + tooling (rendering, ROM parsing). |
| `WebSite/` | The GitHub Pages project site (served at the link above). |
| `MD/` | Design & analysis docs (Traditional Chinese). |
| `tools/` | Helper scripts (benchmark packaging, mail, knowledge-base query). |

## This branch

The latest version currently lives on the **`aot-codegen`** branch (for historical reasons) — that's the canonical branch for now.

## Credits & license

AprVisual's S1 engine is an independent C#/Rust reimplementation of the **[MetalNES](https://github.com/iaddis/metalnes)** wire / group-resolution algorithm, which itself descends from **[Visual6502](https://github.com/trebonian/visual6502)**'s `chipsim` (simulator code: MIT). The switch-level *model* traces to R. E. Bryant's work (MOSSIM, 1981; IEEE TC, 1984).

The bundled **2A03 / 2C02 netlist data** is derived from the **Visual 2A03 / Visual 2C02 / Visual6502** projects and is licensed **CC-BY-NC-SA** — keep the attribution and the same license if you redistribute it. The engine code is by the AprVisual author.
