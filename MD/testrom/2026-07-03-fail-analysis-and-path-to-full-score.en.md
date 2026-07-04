# S1 Test-Failure Root-Cause Analysis — Where the Gap to Full-Score AprNes Lies

> English translation of `2026-07-03-fail-analysis-and-path-to-full-score.md` (Traditional-Chinese master). Update both in sync.

> 2026-07-03. **Pure study; no code changed.** The first draft analyzed the 22 FAILs from a half-finished run; **finalized after the full run: all 141 tests completed — 115 PASS / 26 FAIL / 0 TIMEOUT**; the 4 newly appearing FAILs have been folded in (see the "full-run addendum" in §1.B and §1.E).
> Control group = AprNes (all PASS on the same batch of tests).
> The user's three suspicions: (1) power-on memory-initialization differences (2) CPU/PPU differences from the hardware the test ROMs expect (3) simplifications in the behavioral-layer memory/bus implementation.
> **Conclusion up front: all three suspicions hit, and they break down further into six root-cause categories; there is also an important philosophical finding — some tests fail on S1 precisely because it is more like real hardware than AprNes.**
>
> **2026-07-03 progress update: both category-A (power-on) tests are now fixed (117 PASS / 24 FAIL)** — see the "completed" note on roadmap item #2 in §3.
> **2026-07-04 progress update: category B (phase) closed (124 PASS / 17 FAIL)** — see the "completed" note on roadmap item #1 in §3.
> **2026-07-04 progress update 2: open-bus decay shim done (125 PASS / 16 FAIL) — the "cheap and certain" zone of roadmap items #1-#3 is fully wrapped up; the remaining 16 = the deep-trace zone + faithful deviations.**

## 0. A prerequisite architectural fact (a key takeaway of this study)

Inspecting the `ROM32K.js` module definition reveals: **S1's bus tri-state is physically modeled** —

```
cs ──inverter (netlist)──> gate node ──> 8 pass transistors (_d[i] ↔ d[i])
```

The behavioral-layer handler only drives the module's **private `_d` nodes**; when the chip is not selected the pass gates are off,
and the external bus `d[]` **genuinely floats** (switch-level "float = hold previous value"). Therefore:

- **The open-bus "hold" behavior is faithful** (not a residual behavioral-layer drive) — better than originally feared.
- What's missing is **temporal decay** (on real hardware the charge leaks away in roughly 0.6-1 seconds; in the switch-level model, float holds forever).
- The behavioral-memory simplifications actually amount to only: (a) no access time (instantaneous response within a settle) (b) no decay (c) timing edges such as DMA are determined by when the callback fires.

## 1. Six root-cause categories for the 22 FAILs

### A. Power-on initial state (suspicion 1 hits) — 2 tests

| Test | Evidence | Root cause |
|---|---|---|
| `cpu_reset/registers` #2 | Measured `P=$36`, expected `P=$34` (Z flag differs) | The netlist's "power-on" is an artificial procedure (discharge all nodes → pullup → settle), and the settle yields Z=1; on a real 2A03 the analog power-on race resolves to Z=0 |
| `blargg_ppu_tests/power_up_palette` $02 | Power-on palette contents don't match | The test's expected values = the power-on residue of blargg's own console; **blargg's own readme notes it is console-dependent**. The netlist's power-on palette = the settle result, not any real console |

AprNes's approach: hard-code `P=$34, S=$FD, A/X/Y=0` (`Main.cs:281`, `CPU.cs:8`) + the conventional power-on palette. **What it implements is the "emulator consensus", not the physics of any chip.**

### B. CPU÷12 / PPU÷4 clock phase alignment (the concrete form of suspicion 2) — 4~7 tests

`ppu_vbl_nmi` sub-tests 01-04, 09, 10 all PASS, **while 05/06/07/08 (the NMI edge-timing quadruplet) all FAIL**,
and the failure tables show a systematic ±1 shift (e.g. `05-nmi_timing`'s `08 2` vs expected 3). `2-nmi_and_brk` (vector-hijack timing)
and `sprdma_and_dmc_dma` ×2 (DMA cycle counts 525-528 with a regular oscillation) are suspected to share the same origin.

Root-cause hypothesis: after power-on on a real NES, the **relative phase between the CPU divider (÷12) and the PPU divider (÷4) has multiple possible alignments**,
and the tests are calibrated against the common alignment; our artificial reset procedure lets the dividers settle into **a different, equally legal phase**.
Frame-level vbl timing (01-04 PASS) is unaffected; only sub-CPU-cycle edge decisions (NMI suppression / enable-disable windows) shift.

**Full-run addendum (evidence further strengthened)**: after the older `vbl_nmi_timing` suite finished, `1.frame_basics`/`2.vbl_timing`/
`3.even_odd_frames`/`4.vbl_clear_timing` PASS, while **the three NMI edge tests `5.nmi_suppression`, `6.nmi_disable`, `7.nmi_timing`
FAIL** — exactly isomorphic to the 05-08 failures in the newer suite. **Two independent suites, the same NMI-edge dividing line**;
the phase hypothesis is consistent across suites. Final membership of this category: 4 new + 3 old = 7 (up to 10 including the suspected nmi_and_brk and sprdma×2).

AprNes's approach: it has no phase problem at all — it is a behavioral-layer `tick()` (fixed 1 CPU cycle = 3 PPU dots alignment) +
an explicit two-stage `nmi_delay → nmi_pending` pipeline (a $2002 read can cancel the delay but not the pending) —
**it writes the answer the tests want directly as rules**.

### C. Unmodeled analog phenomena (the boundary between suspicions 2 and 3) — 3 tests

| Test | Evidence | Root cause |
|---|---|---|
| `ppu_open_bus` #3 | "Decay value should become zero by one second" | Switch-level float = hold forever; no charge leakage → never decays |
| `oam_read` | Periodic misalignment in the OAM dump (every 8 bytes the 8th is `-`, some rows `**------`) | The 2C02 OAM is DRAM; the netlist's DRAM-cell behavior (retention/refresh) differs from the real chip's analog characteristics |
| `cpu_dummy_writes_oam` #2 | **Fails at the precondition itself**: "OAM reads must be reliable — emulators usually are, **the real NES is not**" → 4332 read failures | **S1 fails for being too much like real hardware**: the test itself declares that its precondition (reliable OAM reads) does not hold on a real console; AprNes stores OAM in a plain array, hence "reliable" |

`open_bus_decay_timer = 77777` (AprNes `PPU.cs:661`) — one behavioral-layer timer takes care of decay.

### D. Residual-value details of the external open bus — 1 test

`test_cpu_exec_space_apu` #2 "Mysteriously Landed": when fetching instructions from $4000-$40FF, the open-bus value doesn't match expectations.
The hold mechanism is physical (see section 0), but **which value gets held** depends on the last real transfer on the bus;
the timing of the behavioral handler's callback (after settle) may make the residual value differ from real hardware's cycle-by-cycle residue;
and the 2A03's internal APU register region ($4000-$4017 is on-chip) has its own separate internal-bus residue when reading write-only addresses.
Requires a cycle-by-cycle trace to pin down — filed in the deep-research zone.

### E. APU / DMA detail differences — 6 tests

| Test | Evidence |
|---|---|
| `apu_test/3-irq_flag` #6 and `blargg_apu_2005/03.irq_flag` $06 | **Both the new and old versions fail on the same code** (writing $00/$80 to $4017 should not affect the IRQ flag) → a stable behavioral difference, not random; netlist behavior of the frame-counter reset logic vs the real console |
| `apu_test/7-dmc_basics` #19 | "the one-byte buffer should fill immediately" — interaction timing between DMC sample DMA (RDY halting the CPU + bus fetch) and the behavioral ROM |
| `dmc_dma_during_read4` ×3 (2007_read, double_2007, 4016_read) | CRC not in the legal set. Note: **the legal set itself contains 2-4 console-model variants** — our CRC may be the answer of "yet another legal chip", or it may be a DMA timing bug; without real hardware we cannot arbitrate |
| `sprdma_and_dmc_dma` ×2 | DMA cycle-count oscillation — belongs to category-B phase or this category; only a trace can tell |
| `test_ppu_read_buffer` #67 (full-run addendum) | CNROM cartridge; basic PPU I/O and the "Direct poke" / "DMA with ROM" sub-tests are all OK (**CNROM banking itself is correct**); it fails on the "DMA + PPU bus" combined sub-test (sprite-0 hit + $4014 DMA + PPU bus all active at once) — detailed timing of the DMA / PPU-bus interaction |

### F. Unofficial immediate-addressing instructions — 3 tests

`instr_test-v3/02-immediate`, `v5/02-immediate`, and `v5/03-immediate` **all three consistently** fail on the same five opcodes:
`0B AAC`, `2B AAC`, `4B ASR`, `6B ARR`, `AB ATX`.

- `AB ATX (LXA)` is the famous "magic constant" unstable instruction (dependent on analog bus noise); the netlist gives
  the answer of "one clean simulated chip", while blargg's expected value comes from his own physical console — inherently console-dependent.
- But `AAC/ASR/ARR` are generally considered stable instructions, and the three tests fail consistently → pointing to a shared mechanism
  (this whole family is the "A AND #imm" datapath, i.e. internal SB/DB bus merge behavior).
  Could be a capture error in the Visual2A03 netlist in that region, or our operand-fetch timing. **Needs a single-instruction trace study.**

### Final distribution after the full run (26 FAIL)

| Category | Count | Members |
|---|---|---|
| A power-on | 2 | registers, power_up_palette |
| B phase/NMI edge | 7 (~10) | new-suite 05-08 ×4, old-suite 5/6/7.nmi ×3 (+ suspected: nmi_and_brk, sprdma×2) |
| C unmodeled analog | 3 | ppu_open_bus, oam_read, cpu_dummy_writes_oam |
| D open-bus residue | 1 | test_cpu_exec_space_apu |
| E APU/DMA details | 7 | irq_flag ×2, dmc_basics, dma_2007×2, dma_4016, read_buffer #67 |
| F unofficial instructions | 3 | 02-immediate ×2, 03-immediate |
| (B/E boundary) | 3 | nmi_and_brk, sprdma ×2 |

## 2. Why AprNes gets a full score — a qualitative view

AprNes (like almost every mature emulator) implements **the consensus behavior defined by the test ROMs**:
hard-coded power-on values, an explicitly rule-based NMI pipeline, open bus as a variable plus a decay timer, OAM as a plain array,
DMA with `dmc_stolen_tick()` inserting the exact stolen cycles. Whatever the tests examine is what gets written — that is the behavioral
layer's proper job, and the reason it scores full marks.

S1 gives **the physical answer of one specific netlist chip**. The two differ in their definition of "correct":
`cpu_dummy_writes_oam` fails on a real NES too — pursuing a PASS on that test amounts to asking S1 to lower its fidelity.

## 3. Roadmap toward a "full score" (sorted by cost-effectiveness)

| # | Fix | Expected recovery | Effort | Fidelity cost | Recommendation |
|---|---|---|---|---|---|
| 1 | **Clock phase alignment (✅ completed 2026-07-04, net +7 realized)**: `--phase-probe` measurements found that **the CPU ÷12 runs free while the PPU ÷4 restarts on /res release** → `--reset-hold-extra K` can sweep all 4 real-console alignments (relative phase {1,7,5,3} ↔ K={0,1,3,5}). Measured matrix: the 8 NMI-edge family tests pass at alignments {7,5} and fail at {1,3}; `10-even_odd_timing` is exactly the opposite (passes at alignments {1,3}) — **2+2 perfectly complementary, zero overlap** (four-cell matrix completed 2026-07-04) — **no single alignment satisfies them all, consistent with the real-console behavior documented on NESdev** (the even_odd series has always depended on power-on alignment; on real hardware you reboot and take your chances). Verdict: the runner defaults to `--reset-hold-extra 1` (alignment 7), recovering the quadruplet 4 + old-suite triplet 3 + nmi_and_brk 1; `10-even_odd_timing` is reclassified as an **alignment-dependent faithful deviation** (backed by the complete four-alignment matrix); sprdma×2 is ruled out of phase and definitively assigned to category E. The engine default remains 0; benchmark/golden checksums untouched | Net +7 ✅ | Medium | Low — documented | **Done** |
| 2 | **Power-on shim (✅ completed 2026-07-03, +2 realized)**: `WireCore.ApplyPowerUpState` (test mode only; benchmark/golden checksums unaffected). Two implementation findings worth recording: (a) **the palette cells are cross-coupled latches** — after a single-sided drive is released, the complementary side flips the cell back; you must drive both `_a`/`_b` sides complementarily → settle → SetFloat-release both sides for the cell to hold and stay writable as usual afterward (corroborated by the palette_ram regression PASS); (b) **Z=1 is not a power-on residue but is regenerated by the flag logic while res is held** — injecting right after power-on has no effect; the injection only sticks when done after /res release (single-sided cpu.p1 suffices) | +2 ✅ | Small | Medium — documented | **Done** |
| 3 | **PPU open-bus decay shim (✅ completed 2026-07-04, +1 realized)**: the test loop monitors `_io_db` (the latch side; `io_db` is the live internal bus whose value fluctuates constantly and is unusable — a diagnostic lesson); once the value stays stably nonzero for 36 frames (~600ms), it is driven to zero + released. Same modeling approach as the AccuracyCoin author's TriCNES (per-bit timers; the constant measured on his console: 1,786,830 cycles ≈ 1 second). **125/16 — the cheap-and-certain zone fully wrapped up** | +1 ✅ | Small~medium | Medium — documented | **Done** |
| 4 | **Deep-trace zone**: irq_flag #6, dmc_basics #19, exec_space, the five immediate opcodes — for each, do a targeted cycle-by-cycle trace against the nesdev literature to distinguish "netlist capture error (fixable in netlist/handler)" vs "console difference (recorded as a legal deviation)" | 0~+8 | Large (each one is a small research project) | Depends on findings | Schedule for round two |
| 5 | **The three dma CRCs**: cannot arbitrate without real hardware; do #4's DMA trace first, and if the timing checks out, declare "suspected additional legal console variant" | 0~+3 | Folded into #4 | — | Annotate |
| 6 | **The two OAM tests (oam_read, dummy_writes_oam)** | 0 | — | **High** — passing would require turning OAM into an "emulator-style reliable array", directly contradicting the project's purpose | **Recommend not fixing**; document as "real hardware fails likewise; S1 behavior = faithful" |

### An honest definition of "full score"

Only fixing everything (including the not-recommended #6) reaches 141/141. **The recommended goal is not 141/141**, but rather:

> **Every FAIL has a settled root cause and falls into one of two classes: fixed / documented legal deviation (faithful deviation).**

By this definition, after #1-#3 are done the estimate is **~130/141 PASS + ~5 documented deviations + ~6 awaiting trace verdicts**;
the report page can add a fifth status badge for "legal deviations" (e.g. `faithful-fail`) — which is in fact a narrative unique to a
switch-level project and more persuasive than a full score: **emulators get full marks by writing rules; half of our failures come from being too much like the real chip.**

## 4. Research notes and supporting links for faithful deviations (2026-07-04)

The report page's "Faithful deviations" section is kept in sync with this section; every entry has first-hand corroboration from the community or the test authors:

1. **OAM = DRAM (oam_read, cpu_dummy_writes_oam)**
   - [NESdev wiki: PPU OAM](https://www.nesdev.org/wiki/PPU_OAM): "OAM uses dynamic memory (which will slowly decay if the PPU is not rendering)"
   - [NESdev wiki: PPU power up state](https://www.nesdev.org/wiki/PPU_power_up_state): "The contents of OAM are unspecified both at power on and at reset due to DRAM decay"
   - **Strongest evidence — blargg's own oam_read readme**: on his NTSC front-loader NES, four patterns appear at random after power-on/reset,
     **three of which are Failed** (CRC 694ADBE0 / E9E8E60F / 44551956). Our FAIL (CRC E03E03AD) belongs to the same behavioral family.
   - cpu_dummy_writes_oam's on-screen text says so itself: the precondition (reliable OAM reads) holds on emulators, "but NOT on the real NES".

2. **CPU÷12/PPU÷4 power-on alignment (10-even_odd_timing)**
   - [NESdev forums: CPU-PPU clock alignment](https://forums.nesdev.org/viewtopic.php?t=6186): "the beginning of a CPU tick could be offset by 0-3 master clock ticks" = 4 alignments
   - [NESdev wiki: PPU frame timing](https://www.nesdev.org/wiki/PPU_frame_timing): odd-frame dot-skip behavior
   - Our contribution: `--phase-probe` measurements show **the CPU divider runs free while the PPU divider restarts on /res release** →
     `--reset-hold-extra` can deterministically enumerate all 4 alignments; the measured matrix shows the NMI-edge family (passes at alignments {7,5}) and
     even_odd_timing (passes at alignments {1,3}) are **perfectly complementary with no overlap** — on real hardware, too, they only all pass across separate power-ons, each hitting its own alignment by luck.

3. **PPU open-bus decay (ppu_open_bus, pending fix)**
   - blargg's readme verbatim: "If a bit isn't refreshed with a 1 for about 600 milliseconds, it will decay to 0
     (**some decay sooner, depending on the NES and temperature**)" — temperature-dependent analog leakage;
     the switch-level float=hold model has no leakage term; the plan is to patch it with a documented behavioral timer.

4. **Power-on state (fixed, shim)**
   - [NESdev wiki: PPU power up state](https://www.nesdev.org/wiki/PPU_power_up_state): the palette at power-on is "unspecified";
     power_up_palette checks the residue of blargg's own console (whose table became the emulator consensus).

## 5. Appendix: evidence index

- Failure details: `tools/testrom/out/results/*.json` (resultText contains the full blargg output)
- Tri-state physical structure: `AprVisualBenchMark/data/system-def/ROM32K.js` (cs inverter + pass transistors)
- AprNes reference points: `Main.cs:281` (power-on regs), `PPU.cs:661` (decay timer), `MEM.cs tick()` (fixed 1:3 alignment), CLAUDE.md (nmi_delay/nmi_pending model)
- Key corroboration for the phase hypothesis: the basic vbl set/clear timing tests (01-04) all pass; only the edge quadruplet fails, with a ±1 shift
