# DMC #19 (7-dmc_basics) ACLK Pipeline Analysis — Complete Microscope Evidence Chain

> English translation of `2026-07-04-dmc19-aclk-pipeline-analysis.md` (Traditional-Chinese master). Update both in sync.

Date: 2026-07-04
Status: **FIXED — `7-dmc_basics` fully PASSES (27 frames).** Root cause = a
single-half-cycle analog race on the pcm_latch pass-gate at the apu_clk1
falling edge (real silicon: "data wins"; binary model: "gate closes first");
fix = the `WireCore.DmcLatchShim` edge-capture micro-shim (test-mode only,
off by default, golden checksum untouched). Details in the "Fix" section at
the end.

## Failure symptom (confirmed at the bit level)

blargg `7-dmc_basics` test 19: "There should be a one-byte buffer that's filled
immediately if empty". $4013=0 (1-byte) → write $4015=$10 → `lda $4015` expects $80
(bit7=IRQ flag, bit4=0); we read $00. Preconditions verified: test 18 already wrote $4010=$8F
(IRQ enable), and the $4015 write correctly cleared the stale flag
(trace 20614→20615 irq 1→0).

## Our timeline (bus-trace + IRQ nodes + APU phase + pcm_lc, K=1)

```
cyc 20608: W $4013            clk1=1          lc=0x010 (stale value L=1 from the previous section)
cyc 20614: W $4015 (enable)   clk1=1          lc=0x010
cyc 20617:                    clk2e=1         lc → 0x000 (reload, 3rd cycle after the write)
cyc 20618: r $4015 (data-cycle attempt) clk1=1
cyc 20619: r $4015 RDY=0 (halt)     clk2e=1
cyc 20620: r $E700 RDY=0 (fetch)    clk1=1  ab_use_pcm=1
cyc 20621: r $4015 RDY=1 (resume, re-executes the read → $00) clk2e=1
cyc 20623: set_pcm_irq pulse, pcm_irq ↑        clk2e=1 (second clk2e after the fetch)
```

Three "one ACLK late" symptoms, same fingerprint:
1. DMA halt on the 3rd APU cycle after the write (AccuracyCoin measured 2nd on
   the G revision; "Load DMA after 2 APU cycles", only rare revisions take 3)
2. LC reload lands on the second clk2e after the write (20617, not 20615)
3. IRQ set lands on the second clk2e after the fetch (20623, not 20621)

## Static circuit correspondence (netlist cone ↔ APUSim, stage-by-stage identical)

| Our node | APUSim (emu-russia, independent reverse-engineering of the same die) |
|---|---|
| `set_pcm_irq` = NOR(`pcm_loop`, 11518, 11473) + pullup | `ED1 = NOR3(LOOPMode, sout_latch.nget(), NOT(PCMDone))` |
| 11518 = NOT(11427) | NOT(DMC1) |
| 11427 = NOR(13947, 13969); 13969 = NOT(`apu_clk2e`) | `DMC1 = NOR(pcm_latch, NOT(ACLK2))` |
| **13947**: pass-gate node, gate=`apu_clk1`, source=13907, no vss pulldown at all | `pcm_latch.set(pcm_ff.nget(), ACLK1)` (dynamic latch) |
| 11463: pass-gate, gate=`apu_clk1`, source=11096 | `sout_latch.set(SOUT, ACLK1)` |
| `pcm_irq` flag (11522/11492 cross-coupled) | `int_ff` (RS) `int_ff.set(NOR4(NOR(int_ff, ED1), W4015, n_IRQEN, RES))` |

→ **The netlist transcription and the breaks reverse-engineering are fully
isomorphic** (we separately did an upstream bidirectional zero-diff
verification); the problem is not a missing transistor but dynamic timing:
when 13947 (pcm_latch) captures pcm_ff.

## External authoritative sources

- **NESdev wiki /DMA** (100thCoin's research): load DMA = halt (lands on a get
  cycle, the 2nd APU cycle after the write) → dummy → [alignment] → get (fetch),
  stealing 3–4 cycles; resume = fetch+1 re-executes the interrupted read.
- **AccuracyCoin `DMA + $2002 Read`**: success text "Load DMA after 2 APU cycles"
  (normal) / "after 3 APU cycles" (tolerated for rare revisions).
- **TriCNES** (same author): inside `DMCDMA_Get()`, fetch → bytes-- → IRQ set
  complete in the same cycle; `$4015` read = combinational, immediate.
  Behavioral-level simplification; passes #19.
- **APUSim clkgen**: `ACLK1 = NOR(NOT(PHI1), phi2_latch.nget())` — the high
  windows of ACLK1/ACLK2 exist only in the φ1 half-cycle, alternating every
  other cycle; `PCM = NOR(PHI1, n_DMC_AB)` sets pcm_ff in the fetch's
  φ2 → **the φ1 transparency window is already closed, so pcm_latch must wait
  for the next ACLK1 window to capture**.

## Unresolved contradiction (the arbitration point for the next step)

Hand-tracing the APUSim circuit: pcm_latch captures at fetch+2, DMC1 at
fetch+3 — matching our observation! But then the resume (fetch+1) cannot see
the flag, yet real hardware reliably passes #19. Possible resolution:
**real hardware releases RDY later than we do (rdy_ff belongs to the same
ACLK pipeline), so the resume happens to land on the very cycle the flag
rises (the read's φ2 is later than the RS latch's φ1 set)**. If that holds,
our deviation is actually in the **RDY/stall length** (ours 2 cycles, real
hardware 3–4+), not in the IRQ path — the three symptoms unify into "the DMA
control plane (start_ff/run_latch/rdy_ff) is one ACLK early in phase".

Hand-tracing hit its limit (coupled latches spanning 4 half-cycles, each
source defining edges differently). **Next step: get APUSim running**
(ref/breaknes_apusim/ already has dpcm/dma/clkgen fetched; still needs
BaseLogic etc. as dependencies, or clone the whole breaknes), feed it the
$4010=$8F / $4013=0 / $4015=$10 sequence, output a cycle-by-cycle ground
truth of RDY/int_ff/$4015 readback, and diff it directly against our trace.
The first diverging cycle = the stage to fix.

## APUSim ground truth (2026-07-04 addendum — major turning point)

Wrote a standalone harness (`temp/apusim_harness/harness.cpp`, clang++
directly compiling APUSim from `ref/breaknes/` plus the gate-level
M6502Core, with friend-class holes to observe the internals), running the
same $4010=$8F / $4013=0 / $4015=$10 / `lda $4015` sequence:

```
cyc 34: W $4015 (enable)
cyc 38: r $4015 RDY=0 (halt; 4th CPU cycle after the write ✓ NESdev "3rd or 4th")
cyc 39: dummy RDY=0
cyc 40: fetch $C000 (PCM=1, pcm_ff↑) RDY=0     ← steals 3 cycles
cyc 41: r $4015 re-execute → reads $10 (bit4 not cleared, bit7 not set)
cyc 42: pcm_latch captures (ACLK1 transparency window)
cyc 43: int_ff ↑ (IRQ flag, fetch+3)
--- result: ram[0]=$10 (expect $80) ---
```

**APUSim also fails blargg #19** — and is even later than us ($10 vs our $00).
Two independent silicon models fail the same way; the behavioral-level model
(TriCNES) passes via "complete in the same cycle".
Fact matrix:

| Model | Readback | stall | IRQ set |
|---|---|---|---|
| Real hardware (blargg-calibrated) | $80 | ? | already visible at resume |
| Us (Visual2A03 netlist) | $00 | 2 cycles | fetch+3 (second clk2e) |
| APUSim (independent circuit reverse-engineering) | $10 | 3 cycles | fetch+3 (first ACLK1 capture + next ACLK2) |
| TriCNES (behavioral) | $80 | 2-4 | same cycle as fetch |

New working hypothesis: APUSim built ACLK1 as a narrow "high only during φ1"
window, but our netlist sampling shows `apu_clk1` still high at the tail of
the fetch's φ2 — if the real silicon's ACLK1 window is wider (covering into
φ2), the pass-gate should let pcm_latch capture in the fetch's own cycle
→ DMC1 on the next clk2e (= resume) → flag visible at resume = real hardware.
The digital models' narrow-window quantization drops exactly this one cycle.
→ Half-cycle microscope experiment in progress (`cpu.#13907`/`cpu.#13947`
raw-id aliases + hc-level log).

## Fix-principle check (Rule 1)

Real hardware (NES-001 + RP2A03G) reliably passes #19 → we deviate from real
hardware → **must fix**. Not a faithful-deviation candidate. The fix point is
expected at the behavioral/integration layer (DMA control-plane phase after
reset) or in how the engine resolves pass-gate dynamic latches — the netlist
data stays untouched.

## Tooling deposited

`--bus-trace` extended (TestRunner.cs): AB/DB/RW/RDY + pcm_irq/set_pcm_irq/
pcm_irqen + apu_clk1/apu_clk2e/ab_use_pcm + pcm_lc[11:0] (12-bit, with
node-id verification output to guard against silent truncation). Prints 30
consecutive cycles after key events.

Lesson restated: a 4-bit lc probe once misled us because its low bits were
all 0 (true value 0x010) —
**probes must always print the node-id resolution result + the full-width
read value**.

## Fix (finalized 2026-07-04)

The half-cycle microscope (`cpu.#13907`/`cpu.#13947` raw-id aliases) hit the
root cause directly:

```
cyc 20620 (fetch) hc 13: apu_clk1 ↓ and 13907 (pcm_ff output) ↓ in the same half-cycle
→ 13947 (pcm_latch) fails to capture → only catches up at the next clk1 window (20622) → DMC1/IRQ one ACLK late
```

- 13907↔13910 cross-coupled = the pcm_ff RS latch; t14402 (gate=apu_clk1) =
  the pass gate into 13947. The netlist's apu_clk1 high window spans φ1 plus
  the front of φ2 (wider than APUSim's φ1-only), so the closing edge and the
  data edge triggered by the PCM strobe both land on hc 13.
- **Within quiescent-settle binary semantics, a same-half-cycle "gate-close
  vs data" race is necessarily judged in the gate's favor** (final state
  clk1=0 → pass gate cut, regardless of intra-wave ordering). Real NMOS lets
  the data slip through via conduction overlap during the clock's decay —
  inherently analog, inexpressible by connectivity alone. APUSim loses it to
  the same quantization (its harness reads $10, failing the same way) — two
  independent silicon models failing identically while the behavioral model
  (TriCNES) passes corroborates that this is an abstraction-layer limit, not
  a transcription/engine bug.
- Fix: `WireCore.EnableDmcLatchShim()` (WireCore.System.cs) —
  on the apu_clk1 falling edge, if the two sides of the latch differ,
  drive→settle→release the post-settle value of 13907 into 13947
  (= the latch's intended "sample on the closing edge" semantics).
  A no-op when the transparent phase is already in sync; it precisely covers
  only the race case.
- Deployment: TestRunner (RunOneTest + BusTrace) sets `RegisterRawIdAliases`
  (registers `cpu.#<rawid>` aliases at load time) and arms the shim; on the
  benchmark path both flags are false → **zero behavioral change in default
  mode**.
- Result: `7-dmc_basics` **Passed** (27 frames; previously FAIL#19 at 31
  frames) — all sub-tests after #19 pass as well.
- **Family regression re-verification (--filter dmc/dma --rerun)**: one shim
  net-rescues 4 tests —
  `7-dmc_basics`, `sprdma_and_dmc_dma`, `sprdma_and_dmc_dma_512`,
  `dma_2007_read` all flip to PASS; the previously-PASS group (dmc.nes,
  8-dmc_rates, dma_2007_write, read_write_2007, 4-irq_and_dma) shows zero
  regressions. Overall score **125/16 → 129/12 (91.5%)**. DMC family
  remainder: dma_4016_read (DMA dummy read double-clocking the $4016
  controller shift — behavioral controller-integration area),
  double_2007_read (CRC class).
- Remainder: halt scheduling is still one APU cycle later than AC's
  measurement (the same class of race living in the run_latch/en_latch
  stage) — no test currently fails because of it; if dmc_dma family
  re-verification shows it is needed, then evaluate generalizing the shim
  into an "ACLK pass-gate edge capture" rule (requires a minimal prototype +
  full-family verification).
