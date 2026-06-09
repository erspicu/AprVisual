# AprVisual

**A Visual6502-style switch-level NES simulator — and an honest log of what made it faster, and what didn't.**

🌐 **[Project site & full write-up →](https://erspicu.github.io/AprVisual/)**  ·  📊 **[Community benchmark leaderboard →](https://baxermux.org/myemu/AprVisual/)**  ·  ⬇ **[Download the benchmark →](https://github.com/erspicu/AprVisual/releases)**

> 中文讀者:官網支援中英切換(預設依瀏覽器語系),點上面的 **Project site** 即可。

---

AprVisual takes **Visual6502-style transistor netlists** of the NES CPU (**2A03**) and PPU (**2C02**) and turns them into analyzable, verifiable, executable logic models. It simulates the chip at the level of **individual transistors and wires — not opcodes** — and lets the CPU's behavior *emerge* from the physics. The result is bit-for-bit faithful to the real silicon, and (necessarily) far slower than real time.

The real value here is the **translation pipeline** — silicon connectivity → graph → logic/sequencing → verifiable backends — and the **honest record of which optimizations actually work on real hardware**, not any single backend.

> New to "switch-level simulation"? Start with the plain-language **[primer](https://erspicu.github.io/AprVisual/primer.html)** — it explains netlists, conduction detection, the settle queue, and the graph/BFS ideas behind all of this.

## The honest story

The original plan was a four-stage pipeline (S1 switch-level engine → S2 netlist→IR → S3 CPU proof → S4 codegen + GPU) to push the simulation toward real time. We built and verified those stages — and found the counter-intuitive result that the "obvious" abstractions (**IR + codegen, or a GPU kernel**) ended up **slower** than the direct switch-level interpreter (code bloat, lost timing/correctness, algorithmic redundancy in batch re-evaluation). Real time is **~470× out of reach** and known-unreachable via this route.

So the focus became **pushing S1 — the pure switch-level engine — to its limit**, in both **C#** and **Rust**, and documenting the wins and the (many) dead-ends. The recurring lesson, which independently matches what the Visual NES author found in 2017: the gains come from **less work + smaller (cache-fitting) data + tighter codegen**, *not* from a cleverer data structure. The biggest "less work" wins (R-1, and the P-1 → P-4 event-count prunes) *are* algorithmic — but in exactly that spirit: provably **doing less** on the conduction graph, never a fancier structure or a new general algorithm.

A final, CPU-first investigation ("Escape-1") then asked whether the chip could be **automatically abstracted to logic** for speed, accepting behavioral (not per-node bit-exact) fidelity. It proved that **~98.9% of the chip is reducible to logic + registers** (only ~1.1% is genuine analog) — yet *still* couldn't beat the event-driven engine, because that engine already runs at the netlist's natural minimum granularity. The full, data-backed write-up — including a reusable **"which acceleration strategy fits which chip" map** — is the **[study paper →](https://erspicu.github.io/AprVisual/study.html)**.

## Lineage

**Visual6502 (`chipsim.js`)** → **MetalNES** → **AprVisual S1 (C# + Rust)**.

The site documents each step with source-line citations:
- [What MetalNES added over Visual6502](https://erspicu.github.io/AprVisual/metalnes.html)
- [What AprVisual S1 added over MetalNES](https://erspicu.github.io/AprVisual/design.html)
- [How it compares to other netlist NES/6502 simulators](https://erspicu.github.io/AprVisual/comparison.html)

## Performance

On an AMD Ryzen 7 3700X (at boost clock), benchmarking `full_palette` (300k master half-cycles):

| Engine | Rate | Per frame | vs NES NTSC real-time |
|---|---|---|---|
| C# (`AprVisual.S1`) | ~109.4K hc/s | ~6.5 s | ~393× too slow |
| Rust (`rust-s1`) | ~101.0K hc/s | ~7.1 s | ~425× too slow |

(best of 20 at boost.) The big wins are a family of **event-count prunes** — delete a node re-evaluation at the source when it provably can't change the result, bit-exact. **P-1** (same-state turn-on prune) added +11.85% / +11.36%; then **P-2 → P-4** (turn-off isolation + a capacitance-guarded turn-on extension) added another **+7.7% on C# / +10.0% on Rust** — landing positive on *both* back-ends — and together they delete **~21% of all node re-evaluations** per frame. The trick that keeps them exact: a node is identified as a bus / memory / register cell **by its physics** (handler-driven pins, ForceCompute, and a `cap(X) < cap(neighbours)` test that auto-excludes heavy storage cells) — never a hand-listed node. Full write-up: **[WebSite/prunes.html](https://erspicu.github.io/AprVisual/prunes.html)**. Both engines produce **bit-identical** output — checksum `0x794A43ABDF169ADA` (@300k) / `0x6D4CCBCE2E9CD599` (@1M). NES NTSC real time needs **42,954,552 hc/s**.

> **Measurement note.** The hot loop is **memory-latency-bound**, so throughput scales with CPU clock and is sensitive to boost/thermal state (e.g. the same engine peaks ~109.4K at boost — ~107K typical — and drops, clock-proportionally, when pinned to base 3.6 GHz). For trustworthy A/B of a sub-1% change, **lock the CPU to a fixed frequency and use interleaved-paired runs with the median** — absolute single-run numbers drift too much to compare. Both engines also accept an opt-in **`--pin [N]`** flag (Windows) that pins the hot thread to one quiet physical P-core, raises priority, and disables Win11 EcoQoS throttling to further cut run-to-run variance — bit-exact (pure scheduling) and **off by default** so leaderboard runs stay comparable. **Got a faster CPU? [Run the benchmark and share your numbers.](https://baxermux.org/myemu/AprVisual/)**

## Run the benchmark

The easiest path is the prebuilt, self-contained package (no .NET install needed; Windows + macOS, both engines):

1. **[Download `AprVisualBenchMark.zip`](https://github.com/erspicu/AprVisual/releases)** and unzip.
2. Windows: `run_csharp.bat` / `run_rust.bat` (optional arg = half-cycle count). macOS: `chmod +x *.sh` then `./run_csharp.sh` / `./run_rust.sh`.
3. Each run prints a performance block **and** writes a parseable JSON `.log` to `log/` — upload it to the [leaderboard](https://baxermux.org/myemu/AprVisual/).

## Build from source

Requires the **.NET 11 SDK** (and **Rust/cargo** for the Rust engine).

```sh
dotnet build AprVisual.sln                 # C# engines
( cd experiment/rust-s1 && cargo build --release )   # Rust engine (rust-s1)
```

The optimized switch-level engine lives in `src/AprVisual.S1/` (C#, headless console) and `experiment/rust-s1/` (Rust). See `src/AprVisual.Deprecated/README.md` for the original layout, and `MD/` for the (Traditional Chinese) design docs.

## Repository layout

| Path | What |
|---|---|
| `src/AprVisual.S1/` | The S1 switch-level engine — C#, the golden/canonical artifact and focus of optimization. |
| `experiment/rust-s1/` | The Rust port of the S1 engine (bit-identical). |
| `src/AprVisual.S2/` | The Escape-1 investigation engine (automatic logic extraction; `--miter`/`--compile`/`--cones`) — concluded; the negative-result record. |
| `src/AprVisual.Deprecated/` | The original WinForms engine + tooling (rendering, ROM parsing) + the S2/S3/S4 IR/codegen/GPU experiments — reference only. |
| `WebSite/` | The GitHub Pages project site (served at the link above). |
| `MD/` | Design & analysis docs (Traditional Chinese). |
| `tools/` | Helper scripts (benchmark packaging, mail, knowledge-base query). |

## Credits & license

AprVisual's S1 engine is an independent C#/Rust reimplementation of the **[MetalNES](https://github.com/iaddis/metalnes)** wire / group-resolution algorithm, which itself descends from **[Visual6502](https://github.com/trebonian/visual6502)**'s `chipsim` (simulator code: MIT). The switch-level *model* traces to R. E. Bryant's work (MOSSIM, 1981; IEEE TC, 1984).

The bundled **2A03 / 2C02 netlist data** is derived from the **Visual 2A03 / Visual 2C02 / Visual6502** projects and is licensed **CC-BY-NC-SA** — keep the attribution and the same license if you redistribute it. The engine code is by the AprVisual author.
