# Test-Fix Knowledge Base (Master) — Switch-Level NES Simulation vs Real Hardware

> **This is a continuously-updated living document**: every fix / closure updates its section.
> Full evidence chains live in the per-topic notes (index at the end); this file keeps the
> distilled knowledge. Traditional-Chinese master: `00_測試修復知識庫_總綱.md`.
> Created 2026-07-05 | Last updated 2026-07-05

## 0. Status at a glance

| Date | Score | Event |
|---|---|---|
| 2026-07-02 | — | Test infrastructure built (141-ROM catalog, A/A-r/B/C detection, parallel runner, report site) |
| 2026-07-03 | 115/26 → 125/16 | Clock-phase alignment K=1 (+7), power-up shims (+2), decay shim (+1), CNROM |
| 2026-07-04 | 125/16 → 129/12 | DMC latch shim (one shim, +4) |
| 2026-07-05 | 129/12 → 132/9 (regression run confirming) | ALU latch hold shim + LXA magic shim (+3) |

Reference machine: **NES-001 + RP2A03G + RP2C02G** (the revisions the Visual2A03/2C02 dies
were photographed from; also AccuracyCoin's target). Repair doctrine (user's rule):
**behavioral/integration deviations from the real machine get fixed; netlist-faithful
behaviors that real hardware also exhibits get documented as faithful deviations.**

## 1. The most important lesson: a 3-way taxonomy of FAILs

Running the real chip's netlist and still failing test ROMs has exactly three causes
(now a public explainer section on the report page):

1. **Behavioral-integration gaps** — everything outside the two dies (RAM, cartridge,
   clock, controllers, board wiring) is re-created behaviorally; bugs live not in *what*
   value is returned but *in which instant*, whether the bus floats, how long residue
   lingers. → Fix.
2. **Machine-profile differences** — "the NES" is a family, not one machine; a test ROM is
   calibrated against its author's console. → Handle against our pinned reference machine.
3. **Limits of the digital abstraction (analog)** — switch-level is a digital abstraction
   of analog NMOS silicon. Decay, power-on randomness, **timing races**, and
   **ratioed bus fights** are all rewritten by binary quantization.
   → If the real G-revision behavior is deterministic: a documented shim; if real machines
   genuinely vary: faithful deviation + evidence dossier.

## 2. The engine's semantic limits (the physics of category 3 — this project's deepest new knowledge)

A quiescent-settle binary switch model (each half-cycle settles to a fixed point) diverges
from real silicon in four places:

### 2.1 Same-half-cycle races have PER-INSTANCE polarity (no global rule exists)

| Case | The race | Real-silicon verdict | Binary-model verdict |
|---|---|---|---|
| DMC pcm_latch (t14402) | data falling edge vs apu_clk1 closing edge | **data wins** (pass gate still conducts while the clock decays) | gate closes first → capture misses one beat |
| ALU input latches (alua/alub) | bus collapse vs SBADD/DBADD select closing | **gate wins** (hold time met) | collapse ripples through the closing gate → latch corrupted |

- Key: under settled-state semantics "final gate=0 → off"; intra-step ordering does not
  exist, so a same-step race is always decided one way. Real silicon decides per instance
  via analog propagation depths.
- **Strongest corroboration**: emu-russia's APUSim (independent schematic-level
  reverse-engineering of the same die) fails the DMC case the same way (reads $10; we read
  $00; hardware reads $80) — two independent silicon models losing identically, while
  behavioral emulators (TriCNES) pass by construction → it is an abstraction limit, not
  anyone's bug.
- Engineering form: a **documented latch-shim table with polarity** (edge-capture mode /
  hold mode).

### 2.2 Ratioed bus fights are flattened by GND-wins

- LXA's ($AB) magic constant is an analog strength contest (a weak AC drive against the
  data-latch driver on the merged SB/IDB line); real chips yield $EE/$FF depending on chip
  and temperature (NESdev + a TriCNES source comment); the NTSC G-revision consensus is
  $FF. Our LUT (GND wins) quantizes it to $00.
- Delicious contrast: **ANC's A&imm emerges from the very same kind of bus merge**
  (wired-AND), and there the outcome matches real hardware — same mechanism, different
  node, different analog winner.

### 2.3 Operational consequence of the group-resolution LUT priority

`GND > VCC/pull-up > external drive > hold` — meaning **an actively-driven control line
cannot be suppressed with SetLow** (an in-group VCC path beats an external drive-low).
→ Shims must act on floating/dynamic nodes, or use sustained drives.

### 2.4 Three textures of storage elements (choosing a shim's point of attack)

| Texture | Examples | One-shot force sticks? | Correct technique |
|---|---|---|---|
| pass-gate dynamic latch | pcm_latch, alua/alub, A/X registers | ✅ (floats and holds after the gate closes) | drive→settle→release |
| cross-coupled regenerative pair | palette cells, pcm_irq | ❌ single side snaps back | **complementary dual-side** drive, then release |
| actively-refreshed loop | P flags (p1/p7 reloaded every cycle from upstream) | ❌ reverted within a cycle | **dual-side pair + sustained drive across several cycles**, then release (every loop phase captures the new state; self-holds) |

### 2.5 Power-up/alignment is a discrete lottery

- CPU÷12 free-runs from power-on; PPU÷4 restarts at /res release → only **4** relative
  phases ({1,7,5,3}); a K=0..5 sweep proved even phases physically don't exist (K=2≡K=1,
  K=4≡K=3 — the divider pair quantizes to whole clk0 periods).
- The NMI-edge family (8 tests) passes {7,5}; 10-even_odd passes {1,3} — **zero
  intersection over the complete enumeration**. We pin alignment 7 (K=1, blargg's
  calibration phase); 10-even_odd's FAIL is the documented cost.
- The truth behind TriCNES passing all ten: its power-on offset is a hand-tuned parameter
  (author's own words: "I don't know why, but this passes all the tests if this is 7,
  so...?") — calibration in alignment space, not evidence that one physical power-on
  state satisfies both groups.

## 3. Fix table (problem → root cause → fix)

### 3.1 Fixed (all shims are test-mode only; the benchmark path is untouched, golden checksum intact)

| # | Tests | Symptom | Root cause (category) | Fix | Net |
|---|---|---|---|---|---|
| 1 | NMI-edge family (8) | VBL/NMI timing off by 1 dot | power-on alignment lottery (§2.5) | `--reset-hold-extra 1` (alignment 7, blargg's calibration phase) | +7 (traded 1: 10-even_odd) |
| 2 | power_up_palette | power-on palette ≠ consensus table | undefined power-up state (cat. 3) | inject 32 cells **dual-side complementary** drive→settle→release | +1 |
| 3 | registers | P=$36 instead of real $34 | ditto; Z regenerates during held reset | inject Z=0 **after** /res release (single-side, cpu.p1) | +1 |
| 4 | ppu_open_bus | no decay exists | floating nodes hold forever (cat. 3; real ~600 ms leak) | watch `ppu._io_db[7:0]` latch side; zero after 36 unchanged nonzero frames | +1 |
| 5 | cpu_dummy_reads etc. | mapper 3 unsupported | scope (cartridge = behavioral) | CNROM behavioral CHR bank latch (netlists untouched) | +1 |
| 6 | 7-dmc_basics, sprdma×2, dma_2007_read | DMC IRQ one ACLK late → $4015 reads $00 | pcm_latch closing-edge race (§2.1, data should win) | **DmcLatchShim**: at apu_clk1's falling edge, edge-capture 13907's post-settle value into 13947 | +4 |
| 7 | ANC/ALR/ARR (4 of the immediate trio's 5 rows) | A never updates, flags wrong | ALU input latches corrupted by same-step bus collapse (§2.1, gate should win) | **AluLatchShim**: snapshot alua/alub; restore same-step corruption at SBADD/DBADD falling edges | (with #8) |
| 8 | ATX (5th row) | LXA magic=$00 vs real $FF | ratioed bus fight quantized (§2.2) | **LxaMagicShim**: after $AB completes, force A=X=imm (one-shot) + sustained 3-cycle dual-side drive on the N/Z flag loops ({p7,#1045},{p1,#566}) | trio all pass, +3 |

### 3.2 Faithful deviations (not fixed; evidence dossier on the report page)

| Test | Why failing IS faithful | Triple evidence |
|---|---|---|
| oam_read | OAM is DRAM; blargg recorded 4 real-console patterns, 3 end in "Failed"; ours is one of them | author's readme + NESdev PPU OAM + Mesen2's opt-in corruption setting |
| cpu_dummy_writes_oam | the test declares on-screen that its prerequisite fails on real NES | on-screen statement + NESdev power-up state |
| 10-even_odd_timing | alignment mutual exclusion (§2.5); any fixed-alignment system pays this | full K-sweep matrix + completeness proof + TriCNES hand-tuning contrast |

### 3.3 Remaining FAILs (9) and working hypotheses

| Test | Area | Hypothesis / next step |
|---|---|---|
| 3-irq_flag ×2 | APU frame IRQ | not yet investigated; frame-counter IRQ flag timing, possibly the same ACLK-race family |
| dma_4016_read | DMA dummy reads hitting $4016 | controller shift register double-clocked by DMA reads — behavioral controller integration zone |
| double_2007_read | DMC DMA + $2007 CRC | not yet investigated |
| test_ppu_read_buffer | PPU read buffer #67 | not yet investigated (the other 66 subtests pass) |
| test_cpu_exec_space_apu | executing code from APU space | open-bus execution semantics; may interact with the decay shim |
| oam_read, cpu_dummy_writes_oam, 10-even_odd | — | faithful deviations (§3.2), remain FAIL |

Known deviation currently failing no test: the DMA halt schedule runs one APU cycle later
than AccuracyCoin's hardware measurement (same §2.1 race family, living in the
run_latch/en_latch stages) — if ever needed, generalize the shim into an
"ACLK pass-gate edge-capture" rule (minimal prototype first).

## 4. Instrument inventory (all reusable)

| Instrument | Purpose | Location |
|---|---|---|
| `--bus-trace <rom>` | AB/DB/RW/RDY + DMC IRQ nodes + APU phases + pcm_lc; half-cycle microscope inside event windows | TestRunner |
| `--op-probe <rom> <hexaddr>` | AB-triggered; per-half-cycle datapath (db/idl/alua/alub/sb/A/ADD) + 13 control lines + firing PLA rows | TestRunner |
| `--micro <rom>` | run 3 frames, dump work RAM (micro-ROM result harvesting) | TestRunner |
| `--rdy-probe` / `--phase-probe` | RDY transition stats / divider phase bit-strings | TestRunner |
| `RegisterRawIdAliases` | registers `cpu.#<rawid>` aliases at load → unnamed nodes probeable (default off) | WireCore.Module |
| micro-ROM generator + analyzer | 640-combo unofficial-op semantics diff | temp/micro_imm/ |
| APUSim ground-truth harness | clang++ builds emu-russia APUSim + gate-level 6502; friend-class access to internals | temp/apusim_harness/ |
| static cone walker | transdefs upstream-cone BFS (pull/pass classification) | temp/ (written as needed) |

Probe iron rules (hard-earned): **always print node-id resolution + full-width values**
(a 4-bit lc probe once misled with all-zero low bits); in this netlist's sampling,
`cpu.sync` leads by one cycle (sync=1 at fall N flags cycle N+1 as the opcode fetch).

## 5. The boxing-in method (methodology)

Every deep investigation followed the same siege procedure, now proven:

1. **Black-box localization**: the test's own output (failing rows / error codes) + reading
   its source for the expected values
2. **micro-ROM microscopy**: minimal self-built repro, exhaustive inputs, diff against
   documented semantics
3. **Datapath microscopy**: op-probe/bus-trace at half-cycle granularity — verify the data
   path before the control path
4. **External authority arbitration**: NESdev wiki (100thCoin-era), AccuracyCoin/TriCNES
   sources and comments, blargg readme/source, emu-russia breaks/APUSim
5. **Independent-model cross-check**: run APUSim on the same scenario — failing identically
   = abstraction limit; failing differently = our bug
6. **Three-level verification after a fix**: micro-ROM full combos → the target blargg ROM
   → family / full-catalog regression

## 6. Per-topic note index (full evidence chains)

- `2026-07-03-fail-analysis-and-path-to-full-score.md` — the 6 root-cause categories + roadmap
- `2026-07-04-reference-machine-profile-and-accuracycoin-notes.md` — machine profile, AC/TriCNES doctrine
- `2026-07-04-dmc19-aclk-pipeline-analysis.md` — the DMC race, complete case (APUSim arbitration)
- `2026-07-05-immediate-trio-alu-latch-race.md` — ALU latches + LXA magic, complete case
- `../testrom_workflow/2026-07-02-s1-testrom-workflow.md` — the test workflow
- Public-facing version: `WebSite/Report/index.html` — explainer section + hardware-model table + faithful-deviation dossier
