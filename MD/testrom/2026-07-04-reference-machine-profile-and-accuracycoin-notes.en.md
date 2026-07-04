# Reference Machine Profile + AccuracyCoin Hardware-Literature Digest

> English translation of `2026-07-04-reference-machine-profile-and-accuracycoin-notes.md` (Traditional-Chinese master). Update both in sync.

> 2026-07-04. The repair direction set by the user: **each machine model/revision has its own characteristics; what the behavioral layer should supply is the correct behavior of "the same machine as our netlist,"**
> not a hybrid stitched together to "make the tests pass." This document turns that principle into an explicit machine profile, and digests the hardware-behavior literature from AccuracyCoin (100thCoin)
> as the reference basis for the deep-trace zone.

## 1. Our reference machine (the calibration target of the behavioral layer)

| Item | Spec | Basis |
|---|---|---|
| CPU | **Ricoh RP2A03G** | Quietust's Visual2A03 die tracing (the netlist itself) |
| PPU | **Ricoh RP2C02G** | Quietust's Visual2C02 die tracing (the netlist itself) |
| Motherboard | **NES-001 (front-loader)** | nes-001.js module (MetalNES lineage); blargg's test machine was also an NTSC front-loader (per the oam_read readme) |
| Clock alignment | Alignment 7 (`--reset-hold-extra 1`) | The alignment calibrated by blargg's NMI edge tests (four-alignment measured matrix, 2026-07-04) |
| Power-on state | Consensus power-on palette table + P=$34 | blargg-machine residual values (his table became the emulator consensus); shim already implemented |

**Inferences**:
- The behavioral layer (memory, bus integration, DMA edges, decay) is always calibrated against "an NES-001 with RP2A03G + RP2C02G";
  source priority: **AccuracyCoin (explicitly pinned to the G revision) > blargg tests (a machine-wise fuzzier 2004-2011 front-loader) > NESdev consensus**.
- **Digital/timing behavior can be pinned to a machine model; analog characteristics remain a "band," not a "point"** — even within the same G revision, the LXA magic value, decay times
  ("depending on the NES and temperature"), and OAM power-on patterns ("at random") still vary randomly / drift between individual units;
  these are always handled as "behavior band + faithful deviation," never hard-coded to a fake precise value.

## 2. AccuracyCoin is our chosen arbiter

First paragraph of the AccuracyCoin README: "**This ROM was designed for an NTSC console with an RP2A03G CPU and
RP2C02G PPU.** Some tests might be automatically skipped on hardware with a different revision."

— Its 141 tests are calibrated for **exactly our two chips**. Hence the discrimination strategy for the deep-trace zone:

> For the same behavior, if **AccuracyCoin (G-revision-calibrated) says we are right while a blargg CRC says we are wrong** → the difference is blargg-machine-specific,
> file it as faithful deviation; if **AccuracyCoin also says we are wrong** → it is a genuine problem in our integration layer / netlist, and its error code localizes it directly
> (2=A mismatch, 3=X, 4=Y, 5=flags).

(Precondition: making AccuracyCoin unattended — the paused AC_ref conversion work appreciates in value because of this; see
`2026-07-02-accuracycoin-unattended-attempt.md`.)

## 3. AccuracyCoin hardware-literature digest (mapped to our remaining FAILs)

### 3.1 Unofficial immediate instructions (mapped to the five-in-a-row failures in instr_test 02/03-immediate)

- **LXA ($AB, = blargg's ATX)** (asm `TEST_LXA_AB` comment, verbatim):
  > "A = ((A | Magic) & Immediate), X = A. The 'Magic' value is not consistent, and so this test cannot
  > rely on any specific value... **unless Immediate is $00, or A is $FF, the outcome is not guaranteed.**"
  - AccuracyCoin only tests the guaranteed cases; the magic value is **displayed but never scored** — authoritative confirmation that the exact LXA value is machine-/analog-dependent.
  - blargg's per-opcode CRC encoded the magic value of his particular machine → our $AB failure has a strong faithful-deviation argument.
- **ARR ($6B)** deterministic semantics (asm comments): A=(A&imm) rotated right; **N=bit7, C=bit6, V=bit5⊕bit6, Z=result is zero**.
- **ANC ($0B/$2B), ASR ($4B)**: deterministic; AccuracyCoin has dedicated tests (`TEST_ANC_0B/2B`, `TEST_ASR_4B`).
- Error-code convention: 2=A, 3=X, 4=Y, 5=flags → running these individual tests on S1 in the future will localize the differing dimension directly.

### 3.2 DMC DMA (mapped to dmc_basics #19, the dma_2007 family, sprdma ×2)

- **"A DMC DMA cannot interrupt a write cycle"** (asm 6426 comment, verbatim):
  > "However, DMC DMA's cannot interrupt a write cycle! Therefore, the address bus cannot be $2007
  > during the DMA, so nothing unusual happens!"
  - Directly explains the behavioral boundary between the dma_2007_write and read families; when tracing, check whether our RDY halt honors this rule.
- **A DMA dummy read can hit $2002 and clear the VBlank flag** (`Test 2 [DMA + $2002]` comment) —
  a read side effect of the residual address left on the address bus during DMA; the key behavior when tracing dma_4016/2007.
- AccuracyCoin has a full `Suite_DMATests` (including DMC DMA Bus Conflicts and DMC DMA + OAM DMA) to use as a cross-reference.

### 3.3 APU Frame Counter IRQ (mapped to irq_flag #6)

The five sub-tests of `TEST_FrameCounterIRQ` define the correct behavior matrix for the IRQ flag:
4-step+enabled=set / 4-step+disabled=not set / 5-step both=not set / reading clears it.
The complaint of blargg irq_flag #6 — "writing $00/$80 to $4017 should not affect the flag" — can be cross-checked against this matrix.

### 3.4 Other citable behavior definitions

- The power-on magic-number $5A convention, `PowerOn_MagicNumber` (cold/warm boot discrimination) — semantically compatible with our power-on shim.
- `Suite_PowerOnState` is DRAW-type (printed, not scored) — 100thCoin's attitude toward power-on state matches ours: display it, don't dogmatically judge right/wrong.

## 4. TriCNES cross-reference (2026-07-04, headless test report provided by the user)

TriCNES = the AccuracyCoin author's own emulator (`TriCNES_ref/` already cloned; the user previously retrofitted a headless test mode,
report in AprNes `site/report/TriCNES_report.html` / `TriCNES_results.json`). Score **169/174**; key comparison points:

- **It passes almost all of our remaining problem tests** (the OAM pair, the DMC/DMA family, irq_flag, immediate, even_odd) —
  just like AprNes/Mesen: the behavioral layer implements the consensus rules + defaults to an alignment/OAM mode that can pass. **It does not overturn our faithful-deviation argument,**
  but its implementation (`Emulator.cs`) = the authoritative algorithmic spec of G-revision behavior, the primary reference for the DMC trace.
- **`power_up_palette` FAILs even on TriCNES** — the AccuracyCoin author's emulator likewise cannot pass blargg's machine-specific
  palette residual values. The argument that "this test's values are a single-machine characteristic" gains corroboration from one more independent emulator.
- **`read_write_2007`: TriCNES FAILs, we PASS** — switch-level physics got right a question even the author's hand-written behavioral model got wrong;
  a trophy-grade cross-validation worth recording.
- The remaining 3 FAILs are all MMC3 revision variants (rev_A/MMC6/MMC3_alt) — mapper territory, irrelevant to us.
- Provenance of the decay constant: `PPUBusDecayConstant = 1786830` (Emulator.cs:1393, measured by the author on his console) —
  our open-bus decay shim already cites this modeling.

## 5. Action implications

1. For every item in the deep-trace zone, first consult the comments and expectations of the corresponding AccuracyCoin test (§3 of this document is the index).
2. Reviving the AC_ref unattended conversion goes on the backlog (value upgraded: G-revision arbiter).
3. Immediate action for class F (the five-in-a-row immediate failures): check whether the ANC/ASR/ARR semantics our netlist computes match the deterministic definitions in §3.1
   (single instructions can be run with --trace); for the LXA part, mark it as a faithful-deviation candidate for now.

## 6. DMC #19 deep dive: elimination log (2026-07-04)

Suspect-elimination progress for `7-dmc_basics` #19 ("an empty buffer should be refilled immediately," failing at frame 31):

| Suspect | Method | Result |
|---|---|---|
| Missing DMC DMA functionality | `--rdy-probe` (count RDY toggles per hc) | **Eliminated** — frames 1-16 show ~138 toggles per frame, each halt ≈3.5 CPU cycles (the textbook value); sampling cadence is normal |
| Netlist transcription error | Semantic bidirectional diff against the QuietuST upstream raw files (gate/c1c2/weak multisets after name resolution) | **Eliminated** — 2A03: 10,912=10,912, segdefs 3,373=3,373; 2C02: 16,872=16,872; **zero differences** |
| Lowering (S1.5) | `--no-lower` A/B | **Eliminated** — same code, fails at the same frame |
| RDY pad not wired | Board-definition check + rdy-probe | Unwired is true, but the DMA halt works normally → harmless (the real 2A03 package doesn't expose RDY either) |

**Surviving suspects** (by likelihood):
1. **Upstream Visual2A03 tracing vs the real die** (a tiny tracing difference in the APU/DMC area) — unreachable by the diff; needs cross-checking against emu-russia/breaks or a microscope trace compared with the cycle-level behavior in the nesdev literature.
2. **Behavioral-layer sub-cycle precision** — a narrow attack surface for #19 (the chain is almost entirely in silicon; the behavioral ROM only supplies one byte);
   a thick attack surface for the dma_2007 family (the DMA×PPU bus intersection) — the family may split into two root causes.
3. Prunes/fast-paths — backed by structural proofs + the bit-exact gate; listed last.

**Next step**: microscope trace — run to the moment #19 enables via `$4015`, log rdy / address / data buses and APU internal state per hc,
and compare against nesdev's "first sample within 2-4 cycles after enable" timing to pinpoint the first differing cycle.
