# Test-Fix Knowledge Base (Master) — Switch-Level NES Simulation vs Real Hardware

> **This is a continuously-updated living document**: every fix / closure updates its section.
> Full evidence chains live in the per-topic notes (index at the end); this file keeps the
> distilled knowledge. Traditional-Chinese master: `00_測試修復知識庫_總綱.md`.
> Created 2026-07-05 | Last updated 2026-07-08 (145/1 clean full regression; shims globalized)

## 0. Status at a glance

| Date | Score | Event |
|---|---|---|
| **2026-07-07/08** | **145/1 (99.3%) — clean full regression closed** | even_odd + #67 both solved (behavioral shims, made **global** = the Gemini principle "runtime instrument-grade shims should be global; per-test = overfitting"); oam_stress + oam_read_vbl_wait enrolled and PASS; 146 tests in 7h58m, zero surprises, sole FAIL = cpu_dummy_writes_oam (faithful deviation). Perf: weighted mean 111.8 khc/s, aggregate 502, steady-state 660 @7 lanes (20 s stagger excluded). Runner gained LPT "longest-first" scheduling + 1.5× typicalFrames kill-cap |
| 2026-07-02 | — | Test infrastructure built (141-ROM catalog, A/A-r/B/C detection, parallel runner, report site) |
| 2026-07-03 | 115/26 → 125/16 | Clock-phase alignment K=1 (+7), power-up shims (+2), decay shim (+1), CNROM |
| 2026-07-04 | 125/16 → 129/12 | DMC latch shim (one shim, +4) |
| 2026-07-05 | 129/12 → 132/9 (predicted) | ALU latch hold shim + LXA magic shim (+3) |
| 2026-07-04 | **133/8 (94.3%) — full regression closed** | all 141 re-run in 6.6 h, zero unexpected reds; surprise: oam_read landed on the passing pattern (+1 over prediction); throughput: 113.4 khc/s per test, steady-state 680 khc/s @ 6 lanes |
| 2026-07-05 | **142/3 (97.9%)** | double_2007_read solved: global zero-footprint Dbl2007Shim (instrument-grade InstClampLow; Gemini consult settled the general principle, 2.6 probe effect); dma_2007_read collateral fully recovered after three misfire lessons; read_buffer #67 same-root hypothesis disproven (still FAIL with the shim); 10-even_odd reclassified as a ~1-dot integration offset (fix campaign started) |
| 2026-07-05 | **141/4 (97.2%)** | exec_space_apu solved: board tie polarity (u7/u8 spare inputs vss → floating TTL should read high) + cold-port bit0; full $4000→$40FF walk passes; tie blast-radius regression 7/7 green |
| 2026-07-04 | **140/5 (96.6%)** | FrameIrqShim: third member of the same-wave transient family (w4017 wave × apu_clk1 edge flips the RS pair; r4015's partial decode aggravates); both irq_flag tests pass; 26-test frame-IRQ family regression, zero collateral |
| 2026-07-04 | **138/7 (95.2%, catalog 145)** | behavioral joypad + `--input` injection: all four read_joy3 tests enrolled and PASS (incl. test_buttons' scripted 8-button run end-to-end); **dma_4016_read remaining FAIL flipped** (the DMA double-clock corruption emerges naturally from real bus traffic — no dedicated fix) |

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
| gate-level TTL latch modules (pslatch family) | nes-pad's CD4021 | ❌ **structurally undrivable**: the latch pass-gates backdrive the input nodes; in-group GND always wins, so a released button can never be written | move the whole module to the **behavioral layer** (nes-pad-behavioral + Joypad handler; same abstraction as the cartridge) |

### 2.5 Power-up/alignment is a discrete lottery

- CPU÷12 free-runs from power-on; PPU÷4 restarts at /res release → only **4** relative
  phases ({1,7,5,3}); a K=0..5 sweep proved even phases physically don't exist (K=2≡K=1,
  K=4≡K=3 — the divider pair quantizes to whole clk0 periods).
- The NMI-edge family (8 tests) passes {7,5}; 10-even_odd passes {1,3} — **zero
  intersection under complete enumeration (on this model)**. Pinned to alignment 7 (K=1,
  blargg's calibration phase); that intersection is **now bridged at alignment 7 by the
  narrow-window write-delay shim, and even_odd PASSES** (§3.1 #13), with source-level
  arbitration still a follow-up (see the reclassification below).
- **Reclassified (2026-07-05, Gemini consult q/a_even_odd_doctrine)**: a golden
  alignment passing all ten in one power-on is understood to exist on real hardware
  (blargg developed the suite on a real console) — the zero intersection is **not a
  real-machine property** but an **unarbitrated ~1-dot absolute-phase offset in our
  two-netlist board-level integration**. Candidate sources (by likelihood): the
  idealized zero-delay cross-die $2002 read path, the reset-release timing that starts
  the PPU divider, unmodeled clock-pad buffer delays. TriCNES's hand-tuned offset=7
  passing everything corroborates the golden-alignment picture (the pass intervals do
  overlap on real silicon). Arbitration experiment: PPUSim cross-check of BIT $2002's
  absolute master-clock latency (/RD→D7). The phase-space model itself (4 classes,
  CPU÷12 free-run, PPU÷4 restart at /res) matches community consensus.

### 2.6 The probe effect: load-time graph changes re-roll the lottery (2026-07-05, settled by Gemini consult)

**Measured fact**: attaching 9 fake nodes/transistors for the double_2007 shim (which
never fired once) flipped the unrelated dma_2007_read @K=1 from PASS to a pattern real
hardware cannot produce (X=00) — purely because class-major renumbering shifted every
node id, changing intra-wave settle order and flipping the "DMA halt vs read"
alignment-lottery race. **Any load-time graph change re-rolls the dice for every
same-wave-race test.**

**The principle** (consult record `tools/knowledgebase/q/a_dbl2007_footprint_20260705.md`):
test instruments need an *absolute-override force*, not graph edits — the EDA-standard
answer is a VPI-style force/release. Per-test scoping was ruled overfitting/technical
debt; a shim that stands in for physical propagation delay must apply **globally**.

**Implementation**: `InstClampLow`/`InstRelease` (WireCore.Recalc) — ORs the `Gnd` flag
into a node at runtime; group-OR resolution then behaves exactly as if a conducting vss
path joined the group (top of the LUT, overrides active drivers), with **zero graph
change**; golden-checksum A/B bit-identical. Discipline: test-mode shims only, only on
nodes without a static Gnd flag, releases are the caller's responsibility (ResetNes
rebuilds flags, clearing any leak).

**Three mis-fire lessons for the arming guards** (each with a measured corpse):
1. `/r2007` low ≠ the CPU is reading: decode glitches stretch the window — grounding
   into an opcode fetch turned $8D into $8C (STY).
2. During a DMC-DMA stall the address bus parks on $2007 through zombie cycles the CPU
   never consumes; a reload landing *between* pulses is the same — the real sample comes
   cycles later, fully propagated.
3. A reload landing in **phi1** likewise (over half a cycle from the next sample edge).
   The correct race window = **the reload lands inside the same active phi2 phase**
   (measured fall distances: micro dt=1 hc, blargg@K=1 dt=7 hc).

## 3. Fix table (problem → root cause → fix)

### 3.1 Fixed (all shims are test-mode only; the benchmark path is untouched, golden checksum intact)

| # | Tests | Symptom | Root cause (category) | Fix | Net |
|---|---|---|---|---|---|
| 1 | NMI-edge family (8) | VBL/NMI timing off by 1 dot | power-on alignment lottery (§2.5) | `--reset-hold-extra 1` (alignment 7, blargg's calibration phase) | +7 (traded 1: 10-even_odd, later recovered by #13) |
| 2 | power_up_palette | power-on palette ≠ consensus table | undefined power-up state (cat. 3) | inject 32 cells **dual-side complementary** drive→settle→release | +1 |
| 3 | registers | P=$36 instead of real $34 | ditto; Z regenerates during held reset | inject Z=0 **after** /res release (single-side, cpu.p1) | +1 |
| 4 | ppu_open_bus | no decay exists | floating nodes hold forever (cat. 3; real ~600 ms leak) | watch `ppu._io_db[7:0]` latch side; zero after 36 unchanged nonzero frames | +1 |
| 5 | cpu_dummy_reads etc. | mapper 3 unsupported | scope (cartridge = behavioral) | CNROM behavioral CHR bank latch (netlists untouched) | +1 |
| 6 | 7-dmc_basics, sprdma×2, dma_2007_read | DMC IRQ one ACLK late → $4015 reads $00 | pcm_latch closing-edge race (§2.1, data should win) | **DmcLatchShim**: at apu_clk1's falling edge, edge-capture 13907's post-settle value into 13947 | +4 |
| 7 | ANC/ALR/ARR (4 of the immediate trio's 5 rows) | A never updates, flags wrong | ALU input latches corrupted by same-step bus collapse (§2.1, gate should win) | **AluLatchShim**: snapshot alua/alub; restore same-step corruption at SBADD/DBADD falling edges | (with #8) |
| 8 | ATX (5th row) | LXA magic=$00 vs real $FF | ratioed bus fight quantized (§2.2) | **LxaMagicShim**: after $AB completes, force A=X=imm (one-shot) + sustained 3-cycle dual-side drive on the N/Z flag loops ({p7,#1045},{p1,#566}) | trio all pass, +3 |
| 11 | test_cpu_exec_space_apu | $4016 opcode fetch reads $5C (NOP abs,X) instead of $40 (RTI) → PC drifts | board-def tie polarity: LS368 spare inputs tied to vss, real floating TTL reads high (cat. 1) + cold-port bit0 | load-time connection override (6 ties vss→vcc, behavioral-joypad mode only) + cold-port rule; TriCNES/AprNes/NESdev triple-confirmed | +1 |
| 10 | 3-irq_flag ×2 | writing $00/$80 to $4017 falsely clears the frame-IRQ flag | intra-settle transient flips the RS pair (§2.1 family; r4015's ab1-less partial decode aggravates) | **FrameIrqShim**: restore the pair when the flag falls with all three legitimate clear terms (#13170 read-clear / intmode level / _res) inactive | +2 |
| 9 | dma_4016_read + the four read_joy3 tests | controller reads entirely broken (gate-level 4021 structurally undrivable, §2.4 4th texture) | missing behavioral part (cat. 1) | **behavioral joypad**: nes-pad-behavioral shadow module + Joypad handler (4021 protocol, advance on joy-deselect edge), scripted `--input`; DMA corruption emerges from real bus traffic | +5 (catalog +4 all pass, remaining −1) |
| 12 | double_2007_read | the merged double read returns the NEW buffer value (all four real patterns are old/transitional) | the reload's staging→inbuf→io→db propagation AND the CPU's A load complete in the **same settle wave** (op-probe verified; §2.1 family — real silicon's propagation loses that race; every Set-class force at every discrete boundary measurably loses) | **Dbl2007Shim** (global, zero-footprint, §2.6): when a reload lands inside a genuine sample phase (/r2007 low + phi2 high + ab decodes a $2007 mirror + R/W read + RDY high + non-palette), InstClampLow clamps the risen bits = old∧new (same transitional class as the real patterns), released at the phi2 fall; the buffer keeps the netlist's merged single advance | +1 (85CFD627 = blargg's first listed real pattern; dma_2007_read 5E3DF9C4 with zero clamps) |
| 13 | 10-even_odd_timing | the $2001 enable/disable vs the pre-render dot-339 skip decision is off by 1 dot (**a broad delay of every $2001 transition hangs the test** — it measures via cycle-precise repeated $2001 writes) | a ~1-dot integration offset: the CPU→PPU **write path** is idealized to zero delay (§2.5; a PPUSim cross-check proves both dies agree internally — VBL@(241,1), skip@339 — so the offset is in the cross-die write path, not inside a die) | **narrow-window write-delay shim** (global, `--ppu-write-delay 16`): delay bkg/spr_enable transitions by 16 hc only at `vpos=261,hpos=338..339`; **the disable side clamps the complement node `/bkg_enable` low** (GND wins, so you can't InstClampHigh the main node) | +1 (output `08 08 09 07`, verdict frame 138) |
| 14 | test_ppu_read_buffer #67 | `$4014=$20` DMA reads the PPU register space; OAM's first four bytes come out `0A 8A 82 00` (should be `0A 0A 82 8A`) | the `$2004` write data isn't held through the PPU's **delayed OAM write (the /WE pulse)** — by then the PPU I/O bus has been overwritten by the next register read (direct reads `0A 0A 8A 8A` ✓ and DMA-from-RAM ✓; only reading the dynamic registers is wrong; `--no-shims` proves it's not the test harness) | **OamDmaPpuBusShim** (global): on the put cycle queue (spr_addr, spr_data); on the OAM /WE falling edge drive the physical cell, release on the rising edge; skip the attribute byte's non-existent bits 2-4 (hardware mask preserved) | +1 (OAM `0A 0A 82 8A`, verdict frame 1274) |

**Global shims (2026-07-07, Gemini principle)**: #12 dbl2007, #13 write-delay, #14
oam-dma were per-test catalog flags; they are now **on by default in test mode** (like
DmcLatch/ALU). Per the consult, a runtime instrument-grade shim is an "absolute-override,
zero-graph-footprint" force → it should be global; per-test is overfitting (only the
**load-time graph change** of the joypad, `needsJoypad`, stays per-test, §2.6). The clean
146-test regression (7h58m) confirms zero collateral from the three global shims: even_odd
f138, #67 f1274, oam_stress f1709 all PASS, the other 143 all green.

### 3.2 Faithful deviations (not fixed; evidence dossier on the report page)

> **Scope (recalibrated 2026-07-07, in sync with the report-page restructure)**: this section
> keeps only the **genuine** deviations where failing IS the faithful result — forcing a PASS
> would move the simulation **away** from the pinned NES-001 / RP2C02G target. Anything of the
> form "pure CPU+PPU netlist isn't enough, so the behavioral layer supplies the correct spec"
> (open-bus decay, power-up state, DMC IRQ, **even_odd**, #67) is a **fix, not a faithful
> deviation** — it lives in §3.1 and in the report's new "behavioral layer supplies the missing
> spec (these PASS)" section.

| Test | Why failing IS faithful | Triple evidence |
|---|---|---|
| oam_read | OAM is DRAM; blargg recorded 4 real-console patterns, 3 end in "Failed"; **the shim set makes the deterministic power-on land on the passing pattern (now PASS)** — the lottery itself is the faithful behavior; future engine changes may legitimately flip it again | author's readme + NESdev PPU OAM + Mesen2's opt-in corruption setting |
| **cpu_dummy_writes_oam** (sole remaining FAIL) | the test declares on-screen "OAM memory reads MUST be reliable … but NOT on the real NES"; RP2C02G-specific OAMADDR-write corruption — real G hardware fails it too (fixed in 2C03/04/05/07) | on-screen statement + NESdev PPU OAM (2C02G row corruption) + Mesen2 `EnablePpuOamRowCorruption` opt-in |

> ~~10-even_odd_timing~~ **fixed and moved out** — see §3.1 #13: the ~1-dot integration offset is supplied by a narrow-window write-delay shim, now PASSES (`08 08 09 07`).

### 3.3 Remaining FAILs (1, closed by the 2026-07-07/08 clean full regression)

| test | area | state |
|---|---|---|
| cpu_dummy_writes_oam | OAM / OAMADDR | **the sole remaining FAIL, a permanent faithful deviation** (§3.2): RP2C02G OAMADDR-write corruption — real G hardware fails it too; AprNes/TriCNES implement the fixed revisions' idealized behavior and therefore pass. Remains FAIL |

**Ceiling reached**: **145/1 (99.3%)** — even_odd (§3.1 #13) and #67 (§3.1 #14) are both fixed
via global behavioral shims and now PASS; cpu_dummy_writes_oam is the only permanent faithful
FAIL. Going higher would mean replacing the G die's corruption behavior (= moving away from the
pinned machine), so 145/1 is the effective ceiling for the target console (NES-001 + RP2A03G +
RP2C02G).

Known deviation with no failing test today: DMA halt scheduling one APU cycle later
than AC's real-hardware measurement (same 2.1 race family, living at the
run_latch/en_latch stage) — if ever needed, generalize via "ACLK pass-gate edge
capture" (minimal prototype first).

## 4. Instrument inventory (all reusable)

| Instrument | Purpose | Location |
|---|---|---|
| `--bus-trace <rom>` | AB/DB/RW/RDY + DMC IRQ nodes + APU phases + pcm_lc; half-cycle microscope inside event windows | TestRunner |
| `--op-probe <rom> <hexaddr>` | AB-triggered; per-half-cycle datapath (db/idl/alua/alub/sb/A/ADD) + 13 control lines + firing PLA rows | TestRunner |
| `--micro <rom>` | run 3 frames, dump work RAM (micro-ROM result harvesting) | TestRunner |
| `--rdy-probe` / `--phase-probe` | RDY transition stats / divider phase bit-strings | TestRunner |
| `RegisterRawIdAliases` | registers `cpu.#<rawid>` aliases at load → unnamed nodes probeable (default off) | WireCore.Module |
| `--input "A:2,Start:6.5:0.5"` | scripted controller input (AprNes-compatible; button:sec[:holdSec]), feeds the behavioral Joypad handler | TestRunner + WireCore.Handlers |
| `--pass-marker <text>` | custom B-class completion marker (for tally ROMs that never print Passed, e.g. read_joy3) | TestRunner |
| `--watch <n1,n2,...>` | per-frame (--micro) / per-hc (--op-probe) state print of arbitrary nodes | TestRunner |
| `--no-alu-shim` | A/B toggle for the ALU hold shim (diagnostics) | TestRunner |
| `--no-dbl2007-shim` | A/B toggle for the $2007 double-read merge shim (diagnostics) | TestRunner |
| `InstClampLow` / `InstRelease` | instrument-grade force/release (Gnd class, zero graph footprint; §2.6) | WireCore.Recalc |
| `PB_DEBUG=1` | clamp/release event log for the dbl2007 shim (env var) | WireCore.System |
| micro-ROM generator + analyzer | 640-combo unofficial-op semantics diff | temp/micro_imm/ |
| APUSim ground-truth harness | clang++ builds emu-russia APUSim + gate-level 6502; friend-class access to internals | temp/apusim_harness/ |
| static cone walker | transdefs upstream-cone BFS (pull/pass classification) | temp/ (written as needed) |

Probe iron rules (hard-earned): **always print node-id resolution + full-width values**
(a 4-bit lc probe once misled with all-zero low bits); in this netlist's sampling,
`cpu.sync` leads by one cycle (sync=1 at fall N flags cycle N+1 as the opcode fetch);
**always verify the trigger window covers the target event** (two window-artifact
incidents: $4016 reads fell outside the window, nearly misdiagnosing the decode as dead);
a behavioral shift register must advance on a "read definitively over" edge (the joy
deselect edge) — the pad clk's falling edge lands before the CPU samples and advances
one bit early.

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

> Maps to the fix table (§3.1): #6→DMC, #7/#8→ALU/LXA, #11→exec_space, #10→frame-IRQ,
> #9→joypad, #12→dbl2007, #13→even_odd, #14→#67. Notes marked (ZH) are Traditional-Chinese only.

**Root-cause taxonomy & roadmap**
- [2026-07-03 FAIL analysis & path to full score](2026-07-03-fail-analysis-and-path-to-full-score.en.md) — the 6 root-cause categories + roadmap
- [2026-07-04 reference machine profile & AccuracyCoin notes](2026-07-04-reference-machine-profile-and-accuracycoin-notes.en.md) — machine profile, AC/TriCNES doctrine

**Per-case evidence chains (→ §3.1 fixes)**
- [2026-07-04 DMC #19 ACLK pipeline analysis](2026-07-04-dmc19-aclk-pipeline-analysis.en.md) — the DMC race, complete case (APUSim arbitration) → #6
- [2026-07-05 immediate trio ALU-latch race](2026-07-05-immediate-trio-alu-latch-race.en.md) — ALU latches + LXA magic, complete case → #7/#8
- [2026-07-05 read-buffer #67 DMA open-bus diagnosis](2026-07-05-read-buffer-67-dma-openbus.md) (ZH) + [final fix record](../toDoNext/202607071826-test_ppu_read_buffer修復紀錄.md) (ZH) — OamDmaPpuBusShim → #14 (now PASS, frame 1274)
- [2026-07-05 even_odd integration-offset campaign](2026-07-05-even-odd-integration-offset-campaign.md) (ZH) + [final fix record](../toDoNext/202607062345-10-even_odd_timing修復紀錄.md) (ZH) — ~1-dot cross-die write-path offset → narrow-window write-delay shim → #13 (now PASS, `08 08 09 07`)

**Faithful deviation & methodology/architecture**
- [2026-07-05 Faithful-deviation in-depth Q&A](2026-07-05-faithful-deviation-qa.en.md) ([ZH](2026-07-05-faithful-deviation-qa.md)) — the full Q&A and triple evidence for §3.2's sole remaining FAIL (cpu_dummy_writes_oam)
- [2026-07-05 Socket Pattern / global-DUT-fix principle](2026-07-05-socket-pattern-target-architecture.md) (ZH) — Gemini's principle: runtime instrument shims are now **globalized** per this (§3.1 global note, §2.6); only the load-time joypad stays a per-test stopgap
- [2026-07-08 Don't Touch the DUT: probe effect & instrument-grade shims (teaching)](2026-07-08-probe-effect-instrument-grade-shims.en.md) ([ZH](2026-07-08-probe-effect-instrument-grade-shims.md)) — teaching write-up: why a zero-fire graph edit broke an unrelated test, Gemini's instrument-grade force/release principle, Socket Pattern, Graph Fingerprint (also linked from the report page)

**Workflow & test selection**
- [2026-07-02 S1 test-ROM workflow](../testrom_workflow/2026-07-02-s1-testrom-workflow.en.md) ([ZH](../testrom_workflow/2026-07-02-s1-testrom-workflow.md)) — the test workflow
- [2026-07-02 supported test-ROM list](2026-07-02-aprvisual-s1-supported-testroms.md) (ZH) + [detection-method filter](2026-07-02-s1-testrom-detection-filter.md) (ZH) — how the catalog was derived (NROM, PAL excluded, $6000 / per-frame verdict)

- Public-facing version: [live report](https://erspicu.github.io/AprVisual/Report/) (repo path `WebSite/Report/index.html`) — explainer section + hardware-model table + the two sections "behavioral layer supplies the missing spec (these PASS)" and "faithful deviations (evidence dossier)"
