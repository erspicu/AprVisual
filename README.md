# AprVisual

**A Visual6502-style switch-level NES simulator — and an honest log of what made it faster, and what didn't.**

🌐 **[Project site & full write-up →](https://erspicu.github.io/AprVisual/)**  ·  📊 **[Community benchmark leaderboard →](https://baxermux.org/myemu/AprVisual/)**  ·  ⬇ **[Download the benchmark →](https://github.com/erspicu/AprVisual/releases)**

> 中文讀者:官網支援中英切換(預設依瀏覽器語系),點上面的 **Project site** 即可。

---

AprVisual takes **Visual6502-style transistor netlists** of the NES CPU (**2A03**) and PPU (**2C02**) and turns them into analyzable, verifiable, executable logic models. It simulates the chip at the level of **individual transistors and wires — not opcodes** — and lets the CPU's behavior *emerge* from the physics. The result is bit-for-bit faithful to the real silicon, and (necessarily) far slower than real time.

The real value here is the **translation pipeline** — silicon connectivity → graph → logic/sequencing → verifiable backends — and the **honest record of which optimizations actually work on real hardware**, not any single backend.

> New to "switch-level simulation"? Start with the plain-language **[primer](https://erspicu.github.io/AprVisual/primer.html)** — it explains netlists, conduction detection, the settle queue, and the graph/BFS ideas behind all of this.

## The honest story

The original plan was a four-stage pipeline (S1 switch-level engine → S2 netlist→IR → S3 CPU proof → S4 codegen + GPU) to push the simulation toward real time. We built and verified those stages — and found the counter-intuitive result that the "obvious" abstractions (**IR + codegen, or a GPU kernel**) ended up **slower** than the direct switch-level interpreter (code bloat, lost timing/correctness, algorithmic redundancy in batch re-evaluation). Real time is **~306× out of reach** and known-unreachable via this route.

So the focus became **pushing S1 — the pure switch-level engine — to its limit**, in both **C#** and **Rust**, and documenting the wins and the (many) dead-ends. The recurring lesson, which independently matches what the Visual NES author found in 2017: the gains come from **less work + smaller (cache-fitting) data + tighter codegen**, *not* from a cleverer data structure. The biggest "less work" wins (R-1, and the P-1 → P-4 event-count prunes) *are* algorithmic — but in exactly that spirit: provably **doing less** on the conduction graph, never a fancier structure or a new general algorithm.

A final, CPU-first investigation ("Escape-1") then asked whether the chip could be **automatically abstracted to logic** for speed, accepting behavioral (not per-node bit-exact) fidelity. It proved that **~98.9% of the chip is reducible to logic + registers** (only ~1.1% is genuine analog) — yet *still* couldn't beat the event-driven engine, because that engine already runs at the netlist's natural minimum granularity. The full, data-backed write-up — including a reusable **"which acceleration strategy fits which chip" map** — is the **[study paper →](https://erspicu.github.io/AprVisual/study.html)**.

## Lineage

**Visual6502 (`chipsim.js`)** → **MetalNES** → **AprVisual S1 (C# + Rust)**.

The site documents each step with source-line citations:
- [What MetalNES added over Visual6502](https://erspicu.github.io/AprVisual/metalnes.html)
- [What AprVisual S1 added over MetalNES](https://erspicu.github.io/AprVisual/design.html)
- [How it compares to other netlist NES/6502 simulators](https://erspicu.github.io/AprVisual/comparison.html)

## Performance

On an AMD Ryzen 7 3700X (at boost clock, hot thread pinned to a P-core), benchmarking `full_palette` (400k master half-cycles):

| Engine | Rate | Per frame | vs NES NTSC real-time |
|---|---|---|---|
| C# (`AprVisual.S1`) | ~140.2K hc/s | ~5.1 s | ~306× too slow |
| Rust (`rust-s1`) | ~126.6K hc/s | ~5.7 s | ~339× too slow |

(best of 30 at boost; all bundled launchers `--pin` by default — see the measurement note below.) The big wins are a family of **event-count prunes** — delete a node re-evaluation at the source when it provably can't change the result, bit-exact. **P-1** (same-state turn-on prune) added +11.85% / +11.36%; then **P-2 → P-4** (turn-off isolation + a capacitance-guarded turn-on extension) added another **+7.7% on C# / +10.0% on Rust** — landing positive on *both* back-ends — and together they delete **~21% of all node re-evaluations** per frame. The trick that keeps them exact: a node is identified as a bus / memory / register cell **by its physics** (handler-driven pins, ForceCompute, and a `cap(X) < cap(neighbours)` test that auto-excludes heavy storage cells) — never a hand-listed node. The newest layer (2026-06-11) is **range-prune**: an automatic **class-major node renumbering** sorts each prune class into one contiguous id block, so those mask lookups become **register compares** (one dependent load deleted per endpoint check) — **+3.6% C# / +2.9% Rust**, the story of [a measured dead end coming back under a different objective](https://erspicu.github.io/AprVisual/rcm-revival.html). On top of that, the renumber's locality key is now **self-captured at load** — the loader warms the chip and records the production cascade's true first-touch order through a cold instrumented settle copy (no file, no flag, any ROM) — worth another **+6.2% on C#** (20/20 paired). The newest win (2026-06-12) is the **B1 pair path**: two-node conducting groups are 77% of all group walks, and proving the group is exactly {seed, neighbour} lets the engine resolve the pair inline, skipping the walk machinery — **+7–9% on C# at boost** (under ~1% at base clock: it deletes dependent-load chain links, whose wall-clock share grows with frequency) and **+14.5% on Rust** (whose recursive group walk had been paying a call chain on every pair). A clock-locked session originally under-read the C# gain as +0.6% — see the measurement note. The newest win (2026-06-18) is the **falling-writeback split**: the turn-off fan-out now walks a *pre-filtered* endpoint list built at load (ids below the prune boundary stripped), so the hot loop drops its per-endpoint range compare and iterates a shorter list — **+1.2% on C# / +6.9% on Rust** (bit-exact; the Rust branchless walk had paid that compare on every endpoint, so it gained far more). The newest win (2026-06-20) is a pair of bit-exact hot-path wins verified on a **Raspberry Pi 5 at a locked 3.0 GHz** — now the authoritative A/B bench, because the Zen 2 dev box has become too thermally noisy to judge a sub-1% change (16-round interleaved-paired): a **build-time turn-off dedupe** (strip duplicate endpoints from the turn-off fan-out list at load — the runtime already skips them via the queued bit — so the hot loop walks a shorter list, **−0.84%**) and a **GndPwr fast-path** (`RecalcNodeFast` reads the packed `GndPwr` byte once and special-cases the dominant 1/2-GND-gate, 0-PWR inline shapes, skipping the loop setup and the entire PWR scan, **−1.83%** with branch-miss flat — a pure work-reduction on the 70% fast-path), **~+2.6% cumulative** → **~96.4K hc/s on the Pi5 @3.0G** (the Zen 2 desktop flagship is ~140.2K (best of 30, cold-machine boost, pinned; leaderboard id 42)). Full prune write-up: **[WebSite/prunes.html](https://erspicu.github.io/AprVisual/prunes.html)**. Both engines produce **bit-identical** output — checksum `0x794A43ABDF169ADA` (@300k) / `0x6D4CCBCE2E9CD599` (@1M) — including across the renumbering (the checksum walks original id order). NES NTSC real time needs **42,954,552 hc/s**.

> **Measurement note.** The hot loop is **memory-latency-bound** (dependent-load chains, not cache misses), so throughput scales with CPU clock and is sensitive to boost/thermal state (e.g. the same engine peaks ~140.2K at boost on a cool machine — ~135K typical, ~10% lower on a heat-soaked day, pinned to a P-core — and drops, clock-proportionally, when pinned to base 3.6 GHz). For trustworthy A/B, **use same-day interleaved-paired (or round-robin) runs with the median** — absolute single-run numbers drift too much to compare. (Correction 2026-06-12: we previously recommended locking the clock for sub-1% A/B; on our dev machine the locked state itself proved unrepresentative — a locked session read a +7–9% change as +0.6% — so we now measure unlocked and cross-check any locked reading.) (Update 2026-06-20: on a heat-soaked dev machine even unlocked interleaved-paired can't resolve a sub-1% change; we now treat a **Raspberry Pi 5 at a locked 3.0 GHz** as the authoritative A/B bench — the Pi's locked clock is genuinely stable in a way the desktop's was not — and the per-release perf chart at [/version/](https://baxermux.org/myemu/AprVisual/version/) is measured there.) Both engines accept a **`--pin [N]`** flag that cuts run-to-run variance — **bit-exact** (pure scheduling); what it did is printed on the run's `# [perf]` line. On **Windows** it **auto-detects the best P-core** (highest EfficiencyClass → highest-numbered such physical core → first logical proc, so it never lands on a slow E-core) and hard-pins the hot thread there, raises priority, and disables Win11 EcoQoS throttling; `--pin N` forces logical core N. On **macOS** (Apple Silicon has no hard core-affinity API) it instead requests `USER_INTERACTIVE` QoS, biasing the scheduler onto the performance cores. **All bundled launchers (`run_*.bat` and `run_*.sh`) turn it on by default.** **Got a faster CPU? [Run the benchmark and share your numbers.](https://baxermux.org/myemu/AprVisual/)**

### Generalization across the NMOS era

The engine is not specialized to the NES. We run it **unchanged** on three standalone [Visual 6502](https://www.visual6502.org) netlists — the bare **MOS 6502**, **Motorola 6800**, and **Zilog Z80** — driven by an *infinite NOP sled* at the pin boundary (no test ROM needed). Only a raw-netlist loader and a per-chip clock/bus driver were added; lowering, the prunes, the class-major renumber, and the self-captured relayout all apply as-is, and all three boot and execute. Porting the *reference* algorithm (chipsim.js's recursive group-walk) to **both C# and C++** then splits the headline speedup honestly:

| Chip | JS naive | C# naive | C++ naive | AprVisual | Algorithm (naive→ours) |
|---|---|---|---|---|---|
| MOS 6502 | 249 hc/s | 17,914 | 26,239 | 145,337 | ~8.1× |
| Motorola 6800 | 149 | 12,582 | 17,137 | 89,233 | ~7.1× |
| Zilog Z80 | 166 | 12,255 | 18,452 | 62,491 | ~5.1× |

(same machine, NOP-sled, pinned; all bit-exact across the renumber/locality variants — and the full C++ engine port matches C# per-node checksums on all three chips.) The ~376–599× JS→ours headline factors cleanly: the two compiled naive baselines land within **~1.4×** of each other, so the **~70–85×** step from JavaScript is a one-time *language dividend* (interpreter→compiled), and our methods add a further **~5–8× over a naive switch-level simulator in the same language** (consistent with ~2.5× over the optimized C++ ancestor). We also ported the **full** optimized engine to C++: at the naive algorithm native C++ is ~1.4–1.5× faster than C#, but the fully-tuned C# engine is ~1.27–1.56× faster than a faithful C++ port — an inversion driven by the .NET JIT + dynamic PGO and a shared packed cache layout, **not** a language ceiling. Across JS→C#→C++ the language never moves the needle more than ~1.5×; the contribution is the algorithm. Re-run unchanged on a **Raspberry Pi 5 (Arm Cortex-A76)**, the engine is **bit-exact across the instruction set** — the *identical* full-state checksum as on x64 — and posts the **first ARM-verified entry on the [leaderboard](https://baxermux.org/myemu/AprVisual/)**; the language dividend and the ~4–7× algorithmic gain reproduce, and the C#/C++ inversion shrinks to within ~8% (the language axis matters even less on ARM). Expressed as a clock these bare CPUs simulate at ~31–73 kHz, within ~14–128× of their 1980s home computers (vs ~306× for the whole NES). Full write-up: **[WebSite/cross-cpu.html](https://erspicu.github.io/AprVisual/cross-cpu.html)**; the bare-CPU bench is `src/AprVisual.etc` (`--cpu-bench <dir> --chip 6502|6800|z80 [--naive]`), the original-JS baseline is `tools/visual6502-node`, and the C++ ports are `tools/cpp-naive` + `tools/cpp-ours`.

## Run the benchmark

The easiest path is the prebuilt, self-contained package (no .NET install needed; Windows + macOS, both engines):

1. **[Download `AprVisualBenchMark.zip`](https://github.com/erspicu/AprVisual/releases)** and unzip.
2. Windows: `run_csharp.bat` / `run_rust.bat` — **arg 1** = half-cycle count (default **400000**), **arg 2** = logical core to pin (optional; default = auto-detect your best P-core). They pin to one quiet P-core by default for a clean, low-variance number, and print the chosen core on the `# [perf]` line. macOS: `chmod +x *.sh` then `./run_csharp.sh` / `./run_rust.sh` (also `--pin` by default — on macOS/Apple Silicon that requests P-core QoS rather than a hard pin).
3. Each run prints a performance block **and** writes a parseable JSON `.log` to `log/` (with a `"pinned"` field) — upload it to the [leaderboard](https://baxermux.org/myemu/AprVisual/).

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
| `src/AprVisual.etc/` | An S1 fork for validating **other** CPUs: a raw Visual 6502 netlist loader + pin-level NOP-sled bench for the 6502 / 6800 / Z80 (`--cpu-bench`), plus a faithful C# port of the reference chipsim.js algorithm (`--naive`) for the language-vs-algorithm split. See `cross-cpu.html`. |
| `src/AprVisual.Deprecated/` | The original WinForms engine + tooling (rendering, ROM parsing) + the S2/S3/S4 IR/codegen/GPU experiments — reference only. |
| `WebSite/` | The GitHub Pages project site (served at the link above). |
| `MD/` | Design & analysis docs (Traditional Chinese). |
| `tools/` | Helper scripts (benchmark packaging, mail, knowledge-base query) — incl. `visual6502-node/` (headless Node.js baseline running the original Visual 6502 JS sim) and `cpp-naive/` + `cpp-ours/` (C++ ports of the reference and the full engine, for the language-vs-algorithm split). |

## Credits & license

AprVisual's S1 engine is an independent C#/Rust reimplementation of the **[MetalNES](https://github.com/iaddis/metalnes)** wire / group-resolution algorithm, which itself descends from **[Visual6502](https://github.com/trebonian/visual6502)**'s `chipsim` (simulator code: MIT). The switch-level *model* traces to R. E. Bryant's work (MOSSIM, 1981; IEEE TC, 1984).

The bundled **2A03 / 2C02 netlist data** is derived from the **Visual 2A03 / Visual 2C02 / Visual6502** projects and is licensed **CC-BY-NC-SA** — keep the attribution and the same license if you redistribute it. The engine code is by the AprVisual author.
