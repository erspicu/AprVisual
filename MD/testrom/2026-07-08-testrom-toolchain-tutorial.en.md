# The Test-ROM Toolchain — a hands-on tutorial, from one click to a report card

> A teaching article. It walks you through AprVisual.S1's **hardware test-ROM validation flow** end to
> end: the lazy one-click `.bat`, the manual knobs, how to read the results, how to verify a single test
> yourself, and how to **reproduce** the whole thing. The test ROMs are bundled into this repo, so a
> clone-and-run is all it takes to verify the process yourself. Traditional-Chinese master:
> `2026-07-08-testrom-toolchain-tutorial.md`.

## 0. What this is, and what you get

AprVisual.S1 is a switch-level engine that simulates the NES's two chips (the 2A03 CPU and 2C02 PPU)
**transistor by transistor**. We point it at the standard NES accuracy test ROMs (blargg's suites and
friends), judge them fully automatically, and produce an **interactive report-card web page**.

A run leaves you with:
- `tools/testrom/out/results/*.json` — one result per test (PASS/FAIL + timestamps + half-cycle count + checksum).
- `tools/testrom/out/screenshots/` — the screen at the moment each test was judged.
- `WebSite/Report/index.html` — the **report card**: per-test screenshot, detection method, frame counts,
  per-test throughput, aggregate performance stats, the hardware-model note, and the faithful-deviation dossier.

Current baseline: **146 / 1 (99.3%, 147 tests)** — the sole FAIL is a faithful deviation (see the
report's dossier). A full 147-ROM sweep runs unattended in about **6.2 h** on 7 pinned cores.

## 1. Prerequisites

| Need | Version / notes |
|---|---|
| **.NET SDK** | .NET 11 (the engine targets `net11.0`; `dotnet --version` should be 11.x) |
| **Python 3** | to run `run_tests.py` / `build_report.py` (standard library only; the report's WebP screenshots want `pillow`, else it falls back to PNG) |
| **OS** | Windows (the bats + `--pin` core affinity use Windows affinity; Linux/macOS can run the Python, but you'll adjust the pinning) |
| **Test ROMs** | **bundled** in `tools/testrom/roms/` (see §8) — no separate download |

First time, confirm the engine builds: `dotnet build src/AprVisual.S1 -c Release` (the one-click bat does this for you).

## 2. Map of the toolchain

```
tools/testrom/
├── catalog.json            the test catalog: each ROM's suite/filename/class/frame budget/detection method
├── roms/                   the bundled test ROMs (147, with each suite's readme.txt for provenance)
├── run_tests.py            the runner: build engine → run all tests in parallel → build the report
├── build_report.py         assembles out/ results into the WebSite/Report/ card
├── calibrate_ref.py        uses the behavioral emulator AprNes as an oracle to calibrate frame budgets
├── run_full_regression.bat [ONE-CLICK] the whole flow (build → regression → report)
├── archive_old_results.bat [ARCHIVE] pack the previous run's data so the next starts clean
├── archive_old_results.ps1 (the PowerShell the bat above calls)
├── build_report_only.bat   rebuild only the report page from existing results (no re-run)
└── out/                    run artifacts (gitignored): results / screenshots / logs / archive_*
```

## 3. The lazy path — one click for the whole thing

Double-click **`tools/testrom/run_full_regression.bat`** (or run it in a terminal). It will:

1. `dotnet build` (Release) to rebuild the engine.
2. Read every test from `catalog.json` and run them on **7 core-pinned workers** in parallel
   (**longest tests first**, see §5).
3. When all tests finish, automatically call `build_report.py` to produce `WebSite/Report/`.

> ⏱️ **This takes several hours** (switch-level simulation is slow, ~5 s per simulated frame; a full run is
> ~8 hours). Ctrl+C aborts. When it's done, open `WebSite/Report/index.html`.

To start from a clean slate, run the archive bat in §4 first, then this one.

## 4. Archiving the old run (so the next starts clean)

Double-click **`tools/testrom/archive_old_results.bat`**. It moves `out/`'s `results/`, `screenshots/`,
and `logs/` into `out/archive_<timestamp>/`, so the next regression starts from an **empty** directory —
that way the report's **timing and performance numbers don't mix with an older batch** (the report itself
only uses the newest contiguous run, but a physical split is the safest).

> The implementation is `archive_old_results.ps1` (PowerShell). Before running it, make sure **no runner is
> active** (never stack two runner instances — they'll fight over the cores).

## 5. Manual operation (fine control)

The one-click bat is just a wrapper around `python tools/testrom/run_tests.py`. Call it directly for options:

```
python tools/testrom/run_tests.py                 # default: 7 workers, full catalog, resume (skip already-done)
python tools/testrom/run_tests.py --jobs 4        # fewer workers
python tools/testrom/run_tests.py --filter apu    # only tests whose suite/name contains "apu"
python tools/testrom/run_tests.py --filter oam,ppu # comma = OR, run several families as ONE busy batch
python tools/testrom/run_tests.py --class A-r      # one class only (A / A-r / C)
python tools/testrom/run_tests.py --limit 4        # only the first N pending tests (smoke test)
python tools/testrom/run_tests.py --rerun          # ignore existing results, run everything again
python tools/testrom/run_tests.py --report-only    # don't run tests, just rebuild the report
python tools/testrom/run_tests.py --no-build       # skip the dotnet build
```

What a run does (the key engine/scheduling design):

- **LPT "longest-first" scheduling**: dispatch by `typicalFrames` (falling back to `maxFrames`), longest
  to shortest, so long and short tests run concurrently and the tail doesn't drag out the makespan.
- **Core pinning**: each worker is pinned to a **physical core** (logical cores `[2,6,10,14,4,12,8]`, avoiding
  core 0's OS noise), 7 lanes by default. Workers stagger their starts by **10 s** (netlist compose is the
  memory-heavy phase).
- **Per-test budget**: capped at `min(maxFrames, 1.5×typicalFrames+5)` — a working test reaches its verdict
  well inside the budget, while a hung/regressed one is killed at ~1.5×. A wall guard (`maxFrames×10+600` s) backs it.
- **Reproducible alignment**: `--reset-hold-extra 1` (K=1) pins the power-on CPU-PPU clock phase to one of
  blargg's calibration phases, so verdicts are deterministic run to run.
- **Global test-mode shims**: a few correct behaviors the netlist can't express are supplied by **test-mode
  behavioral shims** (enabled globally); the engine's default (benchmark) path is bit-identical with or
  without them, and the golden checksum never moves. See
  [Don't Touch the DUT — probe effect & instrument-grade shims](2026-07-08-probe-effect-instrument-grade-shims.en.md).

## 6. Verifying a single test (the fastest hands-on check)

Run just one test through the same machinery:

```
python tools/testrom/run_tests.py --filter 10-even_odd_timing --limit 1 --no-build
```

Check `status` in `out/results/ppu_vbl_nmi__rom_singles__10-even_odd_timing.nes.json`; the screenshot is
under `out/screenshots/...`.

You can also drive the engine directly (bypassing the runner):

```
dotnet run --project src/AprVisual.S1 -c Release -- --test tools/testrom/roms/ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes --system-def-dir AprVisualBenchMark/data/system-def --reset-hold-extra 1
```

Exit code 0 = PASS. Add `--test-screenshot out.png` to save the verdict frame.

## 7. Reading the results / the performance numbers

The fields that matter in each result JSON:

| Field | Meaning |
|---|---|
| `status` | `pass` / `fail` (what the report scores) |
| `startedAt` / `finishedAt` | process start/end (wall clock; enables concurrency/Gantt analysis) |
| `elapsedSeconds` | wall seconds for that test |
| `core` | the logical core it was pinned to (= worker lane) |
| `hc` / checksum fields | simulated half-cycles / state checksum (bit-exact across machines) |

The report page's three throughput lenses (all computed in `build_report.py`, which has comments):
- **Weighted mean** (per-test): total hc ÷ total engine wall time — a single worker's raw speed.
- **Campaign aggregate**: total hc ÷ span — includes lane idle (staggered starts, inter-test gaps, tail drain).
- **Steady-state**: per-busy-second rate × peak lanes — the real sustained throughput once the startup stagger is excluded.

## 8. Reproducibility and the bundled ROMs

So that anyone can verify the whole process, this repo **bundles the exact 147 test ROMs we run** under
`tools/testrom/roms/` (grouped by suite), and `catalog.json`'s `romBase` points there — **clone it and you
don't have to hunt for ROMs**.

- **Source**: the [christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms) collection,
  authored by blargg and others; each suite's `readme.txt` (the author's own text/provenance) is bundled
  alongside. These test ROMs have always been freely redistributable for hardware/emulator verification. See
  `tools/testrom/roms/ATTRIBUTION.md`.
- **Scope**: NROM/CNROM, NTSC only (AprVisual.S1's cartridge scope).
- **Determinism**: K=1 alignment + global shims + per-frame verdicts, so a re-run is deterministic and the
  checksum is comparable across machines/ISAs.

## 9. Calibrating frame budgets (advanced, usually untouched)

Each test's `typicalFrames` / `maxFrames` in `catalog.json` are "budgets." They come from `calibrate_ref.py`,
which uses our behavioral emulator [AprNes](https://github.com/erspicu/AprNes) as a **fast oracle** to measure
"about which frame does this ROM reach its verdict," then takes 1.5× as the kill guard. You only recalibrate
when adding tests or changing detection methods; ordinary verification just uses the existing budgets.

## 10. Troubleshooting

- **`MSB3021 / DLL locked`**: a lingering `dotnet` still holds the DLL. Kill all `dotnet` processes and retry, or use `--no-build`.
- **`UnicodeDecodeError` (cp950)**: old decode noise from capturing the build output, now fixed (UTF-8 with replacement); if you patch the runner, keep `encoding="utf-8", errors="replace"`.
- **Never stack two runners**: two runners at once fight over the 7 cores and starve each other. Run one at a time.
- **Empty / mostly-pending report**: `out/results/` has no results — run a batch first, or use `--report-only` to rebuild from whatever exists.
- **Lots of tests report `detection=none` / `budget exhausted, no $6000 signature`**: the engine never saw the blargg signature, which almost always means the `$6000` work RAM (`cart-extraram`) wasn't mounted — **not** that the frame budget is too tight (raising it just runs the failure for longer). Test mode mounts it automatically now (`ForceExtraRam`); only check this if you've modified the engine. Quick check: `dotnet <dll> --test tools/testrom/roms/nes_instr_test/rom_singles/11-special.nes --max-frames 40 ...` should give `detection=6000, frames=11`. (This is exactly the trap hit on 2026-07-09 — see knowledge base §3.4.)
- **Don't lock the clock**: this project's measurement doesn't rely on clock locking; the aggregate throughput over ~8 hours is stable enough.

---

**Further reading**
- [Test-Fix Knowledge Base (master)](00_test-fix-knowledge-base.md) — FAIL taxonomy, the engine's semantic limits, the full fix table, methodology
- [Don't Touch the DUT — probe effect & instrument-grade shims](2026-07-08-probe-effect-instrument-grade-shims.en.md) — how a shim is attached without breaking other tests
- [Faithful-deviation in-depth Q&A](2026-07-05-faithful-deviation-qa.en.md) — the one "supposed-to-be-red" remaining FAIL
- Report card: [live report](https://erspicu.github.io/AprVisual/Report/)
