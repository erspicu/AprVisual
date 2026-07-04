# Faithful Deviations — an In-Depth Q&A: why some tests are *supposed* to fail here

> Two tests in the report (`10-even_odd_timing`, `cpu_dummy_writes_oam`) are
> ones we **do not intend to make pass**. That sounds counter-intuitive, so
> this document walks through the questions readers — especially fellow
> emulator developers — are most likely to ask, together with the full
> evidence. Traditional-Chinese master: `2026-07-05-faithful-deviation-qa.md`.
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

## Case 1: `10-even_odd_timing` — the power-on alignment trade-off

### Q | I've heard a real NES can sometimes pass all ten ppu_vbl_nmi tests. Why do you state it can't here?

**A:** Our claim is considerably more precise than "it can't":

- What we state is: **on this silicon model**, no state in the reachable
  power-on alignment space (fully enumerated, below) satisfies both the
  NMI-edge family (8 tests) and 10-even_odd simultaneously.
- "Fully enumerated" is not sampling: a K=0..5 sweep showed that
  intermediate reset offsets quantize onto the same 4 classes (K=2≡K=1,
  K=4≡K=3 — identical verdicts, fail codes and frame counts); the divider
  pair restarts on whole clk0 periods, so **only these 4 relative phases
  physically exist**.
- "The PPU ÷4 divider restarts at /res release while the CPU ÷12 free-runs"
  is not a rule we designed — it is **emergent netlist behavior**, measured
  with probes.
- A real-console observation contradicting this would be genuinely valuable
  data: it would localize a concrete correction (a 1-dot relative offset
  between our two divider models). We list exactly this as an open question
  in the dossier.

**Testable:** on an NTSC NES-001, record — **within a single power-on** (no
power cycling) — video of both `05-nmi_timing` and `10-even_odd_timing`
passing. With that data we could correct the divider-phase model. That is
progress for everyone.

### Q | Why alignment 7? Couldn't you pick another one?

**A:** Any **fixed**-alignment system (including a real NES that is not
power-cycled) must trade one test group against the other — that is the
nature of this test family, not an implementation defect. We chose the phase
blargg's NMI-edge tests were calibrated on (8 tests vs 1), and published the
**complete alignment × verdict matrix** together with its cost (the single
10-even_odd FAIL), so readers can see the whole picture themselves.

### Q | Behavioral emulators pass all ten. Could your simulation simply be off by one dot?

**A:** Honestly stated: **the relative phase space is fully verified; the
absolute dot positions are emergent netlist values with no independent
arbitration yet** — the one open point of this entry, written out as such.
Two pieces of context:

- How behavioral emulators pass all ten is worth knowing: TriCNES (whose
  author owns real hardware) tunes its power-on offset until the suite
  passes; its own source comment reads "Shouldn't this be 0? I don't know
  why, but this passes all the tests if this is 7, so...?" — that is
  calibration within alignment space, which is a different thing from a
  physical power-on state that satisfies both groups.
- Scheduled follow-up: emu-russia's **PPUSim** (an independent
  schematic-level reverse-engineering of the same 2C02 die) can run the same
  scenario as an arbiter; we already used this method once in the DMC case
  (two independent silicon models agreeing → the property belongs to the
  abstraction, not to either implementation).

---

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

The common structure of both deviations: **the tests encode the specific
physics of the author's machine as expectations.** Behavioral emulators
implement the consensus rules the community distilled, and naturally pass
everything; a transistor-level simulation reproduces the physics of one
specific chip, and therefore honestly diverges exactly where the physics
genuinely varies between machines. What we aim to provide is, for every
point of divergence, a **circuit-level explanation, a revision comparison,
and a verifiable boundary** — so that "why doesn't this pass" becomes
checkable knowledge rather than a hand-wave.
