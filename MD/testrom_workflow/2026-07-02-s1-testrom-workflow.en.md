# AprVisual.S1 Test-ROM Verification Workflow

> English translation of `2026-07-02-s1-testrom-workflow.md` (Traditional-Chinese master). Update both in sync.

> Created: 2026-07-02. Purpose: prevent knowledge loss — a complete record of the "run test ROMs → produce the report web page" pipeline and its design decisions.
> Related docs: `MD/testrom/2026-07-02-aprvisual-s1-supported-testroms.md` (the 139-ROM supported list),
> `MD/testrom/2026-07-02-s1-testrom-detection-filter.md` (A/A-r/B/C detection classification).

## One-line overview

```
python tools/testrom/run_tests.py        # run all 141 (A/A-r/B/C) → automatically produces WebSite/Report/
```

After the run, committing `WebSite/Report/` publishes it to GitHub Pages (`erspicu.github.io/AprVisual/Report/`).

## Scope and classification (141 = full NROM set + 2 CNROM)

- Parent set: all 184 ROMs in `nes-test-roms-master/checked/` → NROM (mapper 0) non-PAL = 139,
  plus **CNROM (mapper 3, supported since 2026-07-03)** 2 ROMs (`cpu_dummy_reads` class B, `test_ppu_read_buffer` class A) = **141**.
  - CNROM implementation: behavioral-level CHR bank latch (`SetupCnrom`/`AttachCnromLatch`/`DoMapperLatch` in `WireCore.Handlers.cs`
    plus `BankPtr` in `DoMemRead`); zero netlist changes; loading a mapper ∉ {0,3} now fails with an explicit error.
    Bit-exactness regression passed (mapper-0 smoke checksum unchanged); cpu_dummy_reads PASSes (47 frames), read_buffer renders correctly for 60 frames.
  - read_joy3 (the other 4 mapper-3 ROMs) remains excluded: it requires controller input injection (not implemented in S1).
  - Mapper 1 (MMC1) was evaluated and rejected: the 11 ROMs it would unlock are almost all merged versions of single tests we already run, so the value is low; dynamic mirroring is a moderate amount of engineering.
- S1 is a switch-level simulation at **~5 seconds wall-clock per frame**, so the detection methods deliberately exclude wait-style approaches like "90 consecutive frames of a stable screen":
  - **A (83)**: the blargg `$6000` protocol — read one byte per frame; stop the moment the result is written. **Preferred.**
    - **apu_mixer, 4 ROMs (re-added 2026-07-03; semantic caveat)**: their `$6000=0` only means "the audio sequence played to the end without crashing";
      the real mixing verdict is **auditory** (inverted-wave cancellation; the 2A03 has no mixer read-back register) → treat PASS as a smoke test.
      They are also the slowest group (calibrated at 608-1159 frames; 1-2 hours each on S1).
      For true mixer verification in the future: S1 could tap the DAC output nodes of the 2A03 netlist directly and dump waveforms for comparison against reference recordings (something a behavioral-level emulator cannot do).
  - **A-r (8)**: `$6000` + automatic soft reset (6 in `apu_reset/` + 2 in `cpu_reset/`; apu_reset was briefly dropped and re-added the same day) — the protocol returns `$6000=$81` to request a reset; the engine waits 6 frames then calls `WireCore.SoftReset()` (pulls the res line for 192 half-cycles), up to 10 times.
  - **C (2)**: screen CRC (`dmc_dma_during_read4/dma_2007_read.nes`, `double_2007_read.nes`) — every frame, scan the nametable for an isolated 8-digit hex value; only trust it after 2 identical consecutive frames, then compare against the set of valid CRCs.
  - **B (46, implemented 2026-07-02)**: on-screen-text type — engine flag `--screen-verdict`: every frame, decode nametable 0 (tile=ASCII), look for a terminal `Passed`/`Failed`/`$0X` marker, and only decide after 2 identical consecutive frames (no waiting for 90-frame stability). Smoke verification: palette_ram PASSes at frame 19 (116 seconds wall; the stability method would take 17.6 minutes). Marker-type distribution (measured during calibration): Passed 30, $0X 16.
- Classification basis = static scan for `STA $6000` × cross-validation against AprNes's measured results.json; 0 contradictions.

## Engine side (`--test` mode of `src/AprVisual.S1`)

```
dotnet AprVisual.S1.dll --test <rom.nes>
    --max-frames <N>          simulated-frame budget (the primary cap; default 900 ≈ 15 simulated seconds ≈ 75 minutes wall)
    --max-wait <sec>          wall-clock safety cap (default 0 = disabled; max-frames is the primary limit)
    --expected-crc <A,B,...>  class C: set of valid CRCs (comma-separated, case-insensitive)
    --test-json <out.json>    write structured results (schema aprvisual-testrom/1)
    --test-screenshot <p.png> save the final screen (for the report page)
    --pin <N>                 pin the hot thread to logical core N + High priority (the runner passes this)
    --system-def-dir <dir>    netlist module directory (required! see below)
```

- **system-def must point to `AprVisualBenchMark/data/system-def/`** (`src/AprVisual.S1/data/` is empty; data is not vendored).
- Extra RAM ($6000-$7FFF) is auto-enabled when the path contains `nes-test-roms` (isTestRom in `WireCore.System.cs`); when the path doesn't contain it, add `--extra-ram` yourself.
- Detection logic lives in `RunOneTest` in `Test/TestRunner.cs`: each frame `RunFrame()` → `CheckUnitTest()` ($6000) → when no protocol is present: `--expected-crc` scans CRCs, `--screen-verdict` scans terminal text.
- Exit codes: 0=pass, 1..125=fail code, 3=timeout, 2=load error. **Reports go by the JSON `status` field**; do not rely on the exit code alone.
- JSON fields: status/resultCode/detection (`6000`/`6000+reset`/`crc`/`none`)/resetCount/frames/simSeconds/wallSeconds/halfCycles/engineVersion/screenshot/resultText.

## Runner (`tools/testrom/run_tests.py`)

- **The test list = `tools/testrom/catalog.json`** (141 entries with class/maxFrames/expectedCrcs; for class B the runner automatically adds --screen-verdict). Generated by `tools/testrom/gen_catalog.py`; if regeneration is needed, the source of truth is AprNes's `site/report/results.json`.
- **Default 6 workers with a shared queue** (first come, first served — they stagger naturally), each launch staggered by 20 seconds (netlist construction is the heavy-load phase). `--jobs 4` falls back to 4-way.
- **Core pinning: logical cores 2, 6, 10, 14, 4, 12** (3700X: the first 4 = physical cores 1/3/5/7; workers 5 and 6 add physical cores 2/6 → 3 per CCX).
  **Physical core 0 is deliberately avoided** (OS noise); physical core 4 is also left free. SMT logical pair (2i, 2i+1) = physical core i.
  Measured: 4-way at ~114 khc/s per process (80% of a solo run, ~458 aggregate); 6-way trades per-process speed for aggregate (estimated +25-40%); go by the measured khc/s in the report.
- **Resume support**: tests that already have a pass/fail result are automatically skipped (timeouts are re-run); `--rerun` forces everything to run again.
- Common invocations:
  ```
  python tools/testrom/run_tests.py --limit 4          # smoke test (4 ROMs)
  python tools/testrom/run_tests.py --class A-r        # only the 8 soft-reset tests
  python tools/testrom/run_tests.py --filter instr     # substring filter
  python tools/testrom/run_tests.py --report-only      # only rebuild the report page
  ```
- Output: `tools/testrom/out/{results,screenshots,logs}/` (fine to gitignore; the report is re-aggregated from here).
- Each test subprocess has a wall guard (maxFrames×10s+600s) against hangs.

## Report (`tools/testrom/build_report.py` → `WebSite/Report/`)

- **Image policy: the original screenshots are kept forever as PNG in `tools/testrom/out/screenshots/`; before going on the web page, build_report converts them to lossless WebP (~70% smaller, PIL `lossless=True, method=6`)**. `WebSite/Report/screenshots/` is a pure build artifact — it is wiped and re-converted on every rebuild.
- Merges catalog × out/results → `WebSite/Report/results.json` (schema compatible with AprNes: suite/rom/status/exit_code/result_text/screenshot, plus class/detection/frames/simSeconds/wallSeconds).
- `index.html` is self-contained (results embedded; opens offline): stats/progress bar/class and detection-method badges/screenshot lightbox/collapsible suites/search; tests present in the catalog but not yet run show as **pending**.
- Publishing = commit + push `WebSite/Report/` (GitHub Pages deploys from WebSite/ on main).

## Time estimates (for scheduling)

- One frame ≈ 5 seconds wall (Zen2, ~142K hc/s; 714,732 hc/frame). Loading (netlist construction + power-up) adds another ~2-3 seconds.
- Smoke-test measurement: the `$6000` signature appears around frame ~4 (the protocol starts up quickly).
- A typical single blargg test needs 2-10 simulated seconds (120-600 frames) → most tests take 2-15 minutes; 141 tests ÷ 6 workers ≈ **half a day to a day** (the 4 apu_mixer ROMs at 1-2 hours each are the tail).
- After the first full run, use the frames distribution in the report to **shrink the maxFrames values in the catalog** (the timeout budgets are currently conservative: A=900, A-r=1500).

## Frame-count calibration (AprNesRef oracle, added 2026-07-02)

- **Idea**: use the behavioral-level AprNes as a fast oracle, measure for each ROM "at which frame the result actually appears", and feed that back to fine-tune S1's budgets.
- **AprNesRef/** (gitignored) = a modified clone of github.com/erspicu/AprNes: `TestRunnerCore.cs` gains `--calib-json`,
  recording per frame: the frame at which the terminal marker first appears (firstMarkerFrame), the last frame the screen changed, the verdict frame, the detection path, and the final CRC.
  The changes are committed locally inside AprNesRef (not pushed back to the original repo). Build: MSBuild Debug x64 + `/t:Restore`.
- **How to run**: `python tools/testrom/calibrate_ref.py` (139 ROMs, 4 threads, about 44 seconds) → `tools/testrom/calibration_ref.json`.
- **gen_catalog.py automatically consumes the calibration file**: budget = verdict/marker frame × 2 + 120, clamped to [300, 2400].
- **Findings from the first calibration round (2026-07-02)**:
  - 91/48 ($6000/screen) agrees with the classification for the third time; AprNes PASSes all 139 (ground truth).
  - `instr_timing/1-instr_timing` needs 1013 frames — the old budget of 900 would produce a false timeout; after calibration it is 2146.
  - apu_mixer measured at 608-1159 frames, confirming the exclusion was correct.
  - **The class-B marker-frame median is only 21 frames** (max 613) → with S1 scanning the nametable every frame for class B, the 46 ROMs finish in about 2-4 hours,
    ~12× cheaper than transplanting the 90-frame stability method (example: palette_ram at frame 18 vs frame 211).
  - **Zero trap ROMs**: no ROM ever shows the marker early while the screen is still changing → per-frame reading is safe.
  - Class-C CRCs were extracted automatically and match the known set.

## Known issues / TODO

- [ ] First full run of all 141 → review pass/fail (a FAIL = a real switch-level vs behavioral-level difference; open an MD study for each); budgets are already set automatically by calibration.
- [x] The A-r soft-reset path is proven (2026-07-02): apu_reset's 4015_cleared / irq_flag_cleared both PASS, detection=`6000+reset`, resets=1, 32-33 frames (~3 minutes). apu_reset is actually fast; only apu_mixer is slow.
- [x] Class B (46) implemented (2026-07-02): `--screen-verdict` + `FindNametableVerdict`; calibration confirms it is safe (zero trap ROMs) and cheap (marker-frame p50=21).
- [ ] Failing tests = the genuinely valuable findings (switch-level vs behavioral differences); record each in its own MD doc.
- Differences between the engine's detection and AprNes: S1 has no (and needs no) screen-stability detection, controller injection (`read_joy3` is not in the NROM set), or PAL (the 2C07 is a different chip; it simply does not exist at the netlist level).
