# Faithful Deviations — an In-Depth Q&A: why some tests are *supposed* to fail here

> The report carries two long-standing FAILs. `cpu_dummy_writes_oam` is a
> faithful deviation we **do not intend to make pass**; `10-even_odd_timing`
> was **reclassified on 2026-07-05** as a *known limitation of this
> simulator's two-netlist integration* (an unarbitrated ~1-dot absolute-phase
> offset) — not a real-machine property, and not yet a fix. This document
> walks through the questions readers — especially fellow emulator
> developers — are most likely to ask, together with the full evidence. Traditional-Chinese master: `2026-07-05-faithful-deviation-qa.md`.
> Complete evidence chains: the [knowledge base](00_test-fix-knowledge-base.md)
> and the report page's dossier.

## 0. What qualifies a FAIL as a "faithful deviation"?

The category has explicit admission criteria — ALL of:

1. **The test author's own documentation** records the behavior as
   hardware-dependent (readme / source comments / on-screen statement)
2. **Community corroboration** (NESdev wiki / forums)
3. Where feasible, **independent-model cross-checks** (e.g. emu-russia's
   independent reverse-engineering of the same die)
4. **Whenever real hardware is deterministic, we fix** — this project fixed
   10+ items including three analog-race shims; the category is not a box
   for "couldn't fix it", but the residue left after fixes are exhausted and
   the evidence shows real machines behave this way
5. Every entry carries a **verifiable prediction** — anyone is welcome to
   test it against real hardware

---

## Case 1: `10-even_odd_timing` — power-on alignment and an unarbitrated 1-dot integration offset

### Q | I've heard a real NES can pass all ten ppu_vbl_nmi tests. What is your position?

**A:** The same as the community's: **a real console can**. blargg developed and
validated the suite on real NTSC hardware, and a "golden alignment" passing all
ten in a single power-on is understood to exist. Our simulator currently passes
nine of the ten (the 8-test NMI-edge family among them); 10-even_odd fails at
our pinned alignment — and we publish the full measurements:

- The power-on alignment space is **completely enumerated** (not sampled): a
  K=0..5 sweep shows intermediate reset offsets quantize onto 4 relative phase
  classes (the divider pair restarts on whole clk0 periods); "CPU ÷12 free-runs,
  PPU ÷4 restarts at /res release" is probed emergent behavior, consistent with
  the community's black-box findings.
- **On this model**, the NMI-edge family passes {7,5} and 10-even_odd passes
  {1,3} — zero intersection. But real hardware has a golden alignment, so this
  zero intersection is **not a property of the real machine**.

### Q | Then where does the zero intersection come from?

**A:** Our diagnosis (2026-07-05, checked against external technical review):
an **absolute-phase offset in the two-netlist board-level integration**, on the
order of one dot, not yet arbitrated. Visual2A03 and Visual2C02 are each
accurate die models; the board-level glue joining them has three candidate
offset sources (by likelihood):

1. **The cross-die $2002 read path is idealized to zero delay** — on real
   hardware the CPU's /RD reaching the PPU and the PPU returning D7 (the VBL
   flag) crosses two clock domains with physical transit time; in simulation
   that span is 0, which can make the CPU read VBL "one dot early".
2. The exact moment the reset release starts the PPU divider (pad rise times
   are unmodeled).
3. Clock-pad-to-divider buffer delays, which may differ between the two dies.

The effect of such an offset is precisely to shift the two test groups' passing
phases apart — manufacturing the *appearance* of mutual exclusion.

### Q | Why alignment 7? Why not just fix the offset?

**A:** Alignment 7 is the phase blargg's NMI-edge tests were calibrated on
(8 tests vs 1). As for fixing the offset: adjusting it *before measuring it*
would turn the power-on phase into a second hand-tuned parameter — the path our
reference contrast took (TriCNES's author tuned the offset until everything
passed; the source comments "I don't know why, but this passes all the tests
if this is 7, so...?"). We chose to publish the complete in-model enumeration
first, then run the arbitration experiment: cross-check `BIT $2002`'s
**absolute master-clock latency** (/RD to data return) against emu-russia's
**PPUSim** (an independent schematic-level reverse-engineering of the same
2C02 die, already used for two-model corroboration in our DMC case). Measure
the offset, correct it — after which the NMI family and 10-even_odd should
pass at the *same* alignment. That is what genuinely fixing this item looks
like.

**Testable:** if you own an NTSC NES-001 and record — within one power-on —
both 05-nmi_timing and 10-even_odd_timing passing, that directly demonstrates
the golden alignment; we would gladly add it to the dossier as another line of
arbitration evidence.

## Case 2: `cpu_dummy_writes_oam` — a revision-specific hardware bug

### Q | This test mainly verifies CPU dummy writes. Could your FAIL be hiding an actual CPU bug?

**A:** There is a direct control group that rules this out: the sibling test
**`cpu_dummy_writes_ppumem` uses the exact same dummy-write verification
mechanism with PPU memory as the vehicle — and we pass it.** The CPU's
double-write behavior is independently verified; the `_oam` variant stops
exactly at the read-back vehicle it itself declares unreliable on real
hardware.

### Q | AprNes and TriCNES both pass these two tests. If the behavioral layer can express this, why can't the transistor level?

**A:** The key is the **PPU revision**, and current NESdev wording is clear:

> "Writing to OAMADDR on the **2C02G** causes OAM corruption — this usually
> seems to **copy sprites 8 and 9 (address $20) over the 8-byte row at the
> target address**."
> "It is known that in the **2C03, 2C04, 2C05, and 2C07, OAMADDR works as
> intended**."

As a timeline:

- Our pinned reference machine, the **RP2C02G, is precisely the revision
  with this OAMADDR-write corruption bug**; later PPUs (2C03/04/05/07) fixed
  it.
- blargg's tests must write $2003 (OAMADDR) to position their reads — on a
  real G console that triggers the corruption. What he printed on screen in
  2005 ("emulators usually pass, the real NES does not") matches the
  mechanism the community localized by 2026.
- Behavioral emulators (AprNes / TriCNES) implement the **fixed revisions'
  idealized OAMADDR**, hence they pass; Mesen2 ships the G behavior as an
  option literally named `EnablePpuOamRowCorruption` (row corruption —
  matching NESdev's 8-byte-row description), and does not pass these tests
  with it enabled.
- Our OAM and its addressing logic are the physical circuits of the traced G
  die, so the corruption **emerges** rather than being an implementation
  option. Put differently: the way to "pass" would be replacing the G
  silicon's behavior with a fixed-revision idealized array — moving the
  simulation further from the machine we target, not closer.

### Q | Is quoting the test author's warning a sufficient reason not to fix it?

**A:** The author's original text is worth reading (screenshot on the report
page; anyone can re-run it):

> "Requirement: OAM memory reads MUST be reliable. This is often the case on
> emulators, but **NOT on the real NES**."

The author printed "emulators usually pass, real hardware doesn't" at the
top of the test screen, and the NESdev mechanism from the previous question
interlocks with that warning. Companion evidence: the same author's
`oam_read` readme records **the same real console** producing four patterns
across power-ons, three of which end in "Failed" — our verdict falls inside
that documented family (currently on the passing pattern; it can shift with
engine state, and that very changeability is characteristic of the behavior).

### Q | Does your corruption pattern match NESdev's "sprite 8/9 row copy" bit-for-bit?

**A:** Not verified to that depth yet — stated plainly: we have not compared
the netlist's corruption pattern bit-for-bit against NESdev's description
(nor do our cells model charge decay — a documented gap). What we currently
claim is **class agreement**: the G revision's "write OAMADDR → OAM
read-back unreliable" behavior class, not a specific bit pattern (the author
himself wrote "these values are probably unique to my NES" — exact patterns
vary per machine anyway). The bit-level comparison is listed as open work.

**Testable (revision-comparison edition):** this analysis predicts that on a
real **RP2C02G** console `cpu_dummy_writes_oam` does not pass and `oam_read`
shows the four-pattern lottery, while on a **2C03/2C05 (RGB PPU)** console
both should pass. Anyone with a Vs./RGB-modded machine can check directly —
either outcome is valuable data.

---

## About the "faithful deviation" category itself

### Q | Couldn't any hard-to-fix bug be declared "an analog phenomenon"?

**A:** A fair concern — best answered with the auditable record:

1. **Far more was fixed than classified**: the score went from 115/26 to
   141/4 with 10+ fixes — three of which (DMC latch, ALU latch, frame-IRQ)
   are themselves "analog races": whenever real hardware behaves
   deterministically, we fix rather than classify.
2. The category has **admission criteria** (the five clauses of §0) and
   every entry carries a **verifiable prediction**.
3. There is precedent for independent-model corroboration: in the DMC case
   we actually ran emu-russia's APUSim — two independent silicon-level
   models agreed while behavioral models pass — a repeatable experiment,
   not a talking point.
4. The category is **bidirectional**: oam_read once sat here with a failing
   pattern; after an engine fix landed it on the passing pattern, we moved
   it off the fail list and rewrote the entry — classification follows the
   evidence.

## Closing

The structure of faithful deviations like cpu_dummy_writes_oam: **the tests
encode the specific physics of the author's machine as expectations.** Behavioral emulators
implement the consensus rules the community distilled, and naturally pass
everything; a transistor-level simulation reproduces the physics of one
specific chip, and therefore honestly diverges exactly where the physics
genuinely varies between machines. What we aim to provide is, for every
point of divergence, a **circuit-level explanation, a revision comparison,
and a verifiable boundary** — so that "why doesn't this pass" becomes
checkable knowledge rather than a hand-wave.
