# AprVisual.S1 CLI — Complete Reference (English)

> Target: `src/AprVisual.S1/` (the canonical switch-level engine, headless console).
> Run as `dotnet run --project src/AprVisual.S1 -- <args>` or invoke the built DLL directly:
> `dotnet src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll <args>`.
> Source of truth: the parser in `Test/TestRunner.cs` (inventoried 2026-07-14). Keep this file
> and the Traditional-Chinese twin (`S1_CLI_完整參數手冊.md`) in sync when adding switches.

---

## 1. Run modes (entry points, pick one)

| Switch | Args | What it does |
|---|---|---|
| `--benchmark <rom>` | ROM path | Performance mode: run a fixed number of half-cycles, print hc/s and the **NodeStates checksum** (golden verification) |
| `--test <rom>` | ROM path | Single test ROM: run to a verdict (blargg `$6000` signature / whichever verdict flags are set) or budget exhaustion; returns an exit code |
| `--test-dir <dir>` | directory | Batch-run a directory of test ROMs |
| `--rom <rom>` | ROM path | Plain load (combine with screenshot/diagnostic switches) |
| `--selftest` | — | Built-in self test (hand-built mini netlist) |
| `--help` / `-h` / `/?` | — | Usage |

## 2. Simulation environment

| Switch | Args | What it does |
|---|---|---|
| `--system-def-dir <dir>` | directory | Location of the `.js` system module definitions (usually `AprVisualBenchMark/data/system-def`) |
| `--extra-ram` | — | Force cart-extraram ($6000-$7FFF RAM). **AccuracyCoin measures open bus there; `--ac-verdict` disables this automatically** |
| `--region <ntsc\|pal>` | region | Region select (default ntsc) |
| `--joypad` | — | Attach the behavioural joypad + the u7/u8 tie-rewire. Required for controller / execute-from-register-space tests. **A load-time graph change re-rolls the alignment lottery** (Socket Pattern) |
| `--reset-hold-extra <K>` | int | Hold reset K extra half-cycles (power-on CPU/PPU phase alignment; the flagship config uses K=1) |
| `--pin <core>` | core index | Pin the hot thread to a logical core + High priority + EcoQoS off (halves variance; default OFF) |
| `--no-lower` | — | Disable load-time lowering (for the mapped-checksum gate A/B) |
| `--fast-path` | — | No-op (always on in S1; kept for compatibility) |

## 3. Test verdicts

| Switch | Args | What it does |
|---|---|---|
| `--ac-verdict` | — | AccuracyCoin verdict: reads the CPU-RAM completion block at `$07F0` (`DE B0 61` + passed/total/skipped); disables extra-ram; storm-aware (PC parked in $06xx for 120 frames → ac-storm verdict + result-table dump); dumps the full `$0400-$04FF` table at verdict time |
| `--screen-verdict` | — | B-class verdict: on-screen PASS/FAIL text detection |
| `--pass-marker <txt>` | text | Custom B-class PASS string |
| `--expected-crc <crc>` | CRC (repeatable) | C-class verdict: accept set of final-frame CRCs |
| `--max-wait <sec>` | seconds | Verdict wait ceiling |
| `--max-frames <N>` | frames | Simulation-frame budget. **Lesson: a starved budget looks exactly like a wedge — get a frame-count baseline from AprNes first** (isolated AC ROMs under joyON want ≥70) |
| `--input "<spec>"` | e.g. `A:2,Start:6.5` | Scripted controller input (button:seconds) |
| `--test-json <path>` | path | Per-test result JSON |
| `--test-screenshot <path>` | path | Post-verdict PNG |
| `--shot-delay <N>` | frames | Wait N frames after the verdict before the screenshot |

## 4. Snapshots / resume (the foundation of window regressions)

| Switch | Args | What it does |
|---|---|---|
| `--snapshot-frames <N>` | frames | Save a full engine snapshot every N frames (v2 format carries live shim state; CRC-32 trailer; ~380 KB each) |
| `--snapshot-dir <dir>` | directory | Snapshot output directory |
| `--resume <sav>` | snapshot file | Resume from a snapshot in seconds. A **config fingerprint** (node count / transistor count / ROM CRC / shim flags) is checked and mismatches are refused — you cannot resume across config changes (e.g. joypad on↔off) |

**Window-regression recipe** (validate one fix in minutes):
```
dotnet <dll> --test AC.nes --ac-verdict <standard flags> --max-frames 300 \
  --resume <run-snaps>/state_f000200.sav --snapshot-frames 50 --snapshot-dir out/
# then read the table with tools/testrom/ac_snap_results.py --snap out/state_f000300.sav and diff vs baseline
```

## 5. Progress reporting

| Switch | Args | What it does |
|---|---|---|
| `--progress-frames <N>` | frames | Write a checkpoint every N frames (telemetry includes the emulated PC) |
| `--progress-dir <dir>` | directory | Checkpoint output directory |

## 6. Performance measurement

| Switch | Args | What it does |
|---|---|---|
| `--bench-hc <N>` | half-cycles | Benchmark length (golden values: 300000 → `0x794A43ABDF169ADA`; 1M → `0x6D4CCBCE2E9CD599`) |
| `--log-dir <dir>` | directory | Benchmark JSON log output |
| `--dump-states <path>` | path | Dump all node states post-bench (per-node A/B diffing) |
| `--array-footprint` | — | Print hot unmanaged array base+size at setup (IBS/SPE bucketing) |
| `--payload-hist <path>` | path | NodeInfo inline-payload size distribution |
| `--fc-taint-stats <path>` | path | Same-state-prune eligibility statistics (diagnostic) |

**Golden verification recipe** (mandatory after ANY WireCore hot-path change):
```
dotnet <dll> --benchmark AprVisualBenchMark/roms/full_palette.nes --bench-hc 300000 \
  --extra-ram --system-def-dir AprVisualBenchMark/data/system-def
# the checksum MUST be 0x794A43ABDF169ADA
```

## 7. Shim switches (test-mode behavioural patches; never loaded on the benchmark path)

| Switch / env var | What it does |
|---|---|
| `--no-shims` | Disable every test-mode shim |
| `--no-alu-shim` | A/B: the ALU input-latch hold shim (LXA/LAE family) |
| `--no-dbl2007-shim` | A/B: the `$2007` double-read merge shim |
| `--no-ppu-ale-read-feedback-shim` | A/B: expose the raw ALE+RD feedback loop (the $2007 Stress oscillation) |
| `--oam-dma-ppu-bus-shim` / `--no-oam-dma-ppu-bus-shim` | `$4014` OAM write-data held from the PPU I/O bus (default ON) |
| `--ppu-write-delay <N>` | `$2001` write-effect delay of N half-cycles (even_odd campaign; narrow window vpos261/hpos338-339) |
| `--callback-drain-limit <N>` | Non-convergence detector: fail with callback/node evidence instead of hanging (the standard AC config uses 2000) |
| env `NO_OB_SHIM=1` | A/B: disable the open-bus last-transferred-byte replay shim |
| env `NO_DL_SHIM=1` | A/B: disable the input-data-latch phi2-transparency shim ($4016/$4017) |
| env `NO_ABORT_SHIM=1` | A/B: disable the DMC `$4015` deferred-abort shim (retire-early + boundary grid) |
| env `OB_DEBUG=1` | Enable the test-only forensic probe family ([ob]/[obshim]/[dl]/[dma]/[pcm]/[a5]/[fin]…; some carry hardcoded Time windows — TEMP diagnostics) |
| env `LAE_DEBUG=1` | LAE shim forensics (read ring, merge derivation) |
| env `ODMA_DEBUG=1` | OAM-DMA-PPU-bus shim forensics |
| env `PB_DEBUG=1` | PPU ALE/read feedback shim forensics |
| env `PWD_DEBUG=1` | `$2001` write-delay shim forensics |

> Note: the ONLY CLI parsing site in the project is `Test/TestRunner.cs` (`WireCore.Parse.cs`
> is the netlist `.js` module parser — no CLI switches there); env vars inventoried by a
> global `GetEnvironmentVariable` sweep.

## 8. Diagnostic probes (the forensic toolbox)

| Switch | Args | What it does |
|---|---|---|
| `--dump-node <name>` | node name / `@<id>` | Dissect one node: pull-ups, transistors it gates, channel-end transistors. `@id` takes a raw engine id (for unnamed nodes). **This mode does not register raw-id aliases (`cpu.#nnnn`) — those resolve in test mode only** |
| `--dump-module <name>` | module | Dump a module definition |
| `--dump-system` | — | Dump the system composition |
| `--names <id1,id2,...>` | id list | Engine ids → names (LoadSystem that keeps the name map) |
| `--watch <spec>` | comma list of node names | Print node values per frame (with `--micro`); also works after a snapshot resume |
| `--micro <path>` + `--micro-frames <N>` | path, frames | Run N frames (default 3), dump work RAM `$0200-$07FF` |
| `--trace <path>` + `--cycles <N>` | path, cycles | Classic trace-column output |
| `--bus-trace <path>` | path | DMC bus microscope |
| `--op-probe <path> <hexaddr>` | path, address | hc-granularity datapath log triggered when AB hits the address |
| `--rdy-probe <path>` | path | rdy transition counts |
| `--phase-probe <path>` | path | Per-hc clock-phase dump |
| `--probe2002 / --probe-vbl <path>` | path | `$2002` / vblank latch path tracing |
| `--probe-2001 <path>` | path | `$2001` write-effect path (M2 measurement) |
| `--probe-dma <path>` | path | OAM-DMA address bus + open-bus tracing |
| `--ppu-memory-trace <lo-hi>` + `--ppu-memory-trace-x <X>` | PC range, X value | Trace CHR/VRAM callbacks while the CPU PC is inside the range (optionally filtered by X) |
| `--ac-dump-work` | — | Dump AccuracyCoin work/result RAM (oracle comparison) |

**The three probe rules** (tuition paid on 2026-07-14 — read before writing any probe):
1. **A PC sample is trustworthy only after two consecutive identical reads** — the PC register mid-update during JSR/RTS produces single-half-cycle transients.
2. **Budget exhaustion ≠ a wedge** — get a frame-count baseline from AprNes on the same ROM before concluding anything hangs.
3. **Sample a bus transaction's value on its LAST half-cycle** — the first half still carries the instruction's operand byte on the bus.

## 9. Screen output

| Switch | Args | What it does |
|---|---|---|
| `--screenshot <path>` + `--frames <N>` + `--out <path>` | path, frames | Screenshot after N frames |
| `--frame-dump <rom>` + `--frame-count <N>` + `--out-dir <dir>` | ROM, frames, dir | Per-frame PNG dump (with progress/timing) |
| `--ppu-dump <path>` | path | PPU state dump |

---

## 10. Recipe quick-reference

**Standard isolated AccuracyCoin ROM run** (reproduce/verify one test, 2-25 min):
```
dotnet <dll> --test AprAccuracyCoinUnattended/AccuracyCoin_<Test>.nes --ac-verdict \
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin 2 \
  --system-def-dir AprVisualBenchMark/data/system-def --max-frames 70 [--joypad]
```

**Flagship (banked sweep) configuration**:
```
dotnet <dll> --test AprAccuracyCoinUnattended/AccuracyCoin.nes --ac-verdict --joypad \
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin 2 \
  --system-def-dir AprVisualBenchMark/data/system-def \
  --snapshot-frames 10 --snapshot-dir <dir> --progress-frames 600 --progress-dir <dir>
# joyON estimated 10-15 h; the scoreboard number banks only when it completes (ReportAC policy)
```

**Shim A/B isolation** (when a shim side-effect is suspected):
```
NO_OB_SHIM=1 NO_DL_SHIM=1 NO_ABORT_SHIM=1 dotnet <dll> --test ...   (all off)
# then re-enable one at a time; netlist-level shims use the --no-* switches
```

**Single-runner rule**: one simulator instance at a time (core contention starves both); never build while a run holds the DLL.
