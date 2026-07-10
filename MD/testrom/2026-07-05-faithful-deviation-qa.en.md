# Faithful Deviations — an In-Depth Q&A: why some tests are *supposed* to fail here

> After the 2026-07-09/10 clean full regression closed at **146/1 (99.3%, 147 tests)**, just
> **one** long-standing FAIL remains: `cpu_dummy_writes_oam` — the genuine
> faithful deviation we **do not intend to make pass** (the RP2C02G's
> revision-specific OAMADDR-write corruption, which real G hardware fails too).
> This document walks through the questions readers — especially fellow
> emulator developers — are most likely to ask, together with the full evidence.
>
> **Note**: `10-even_odd_timing` was once listed here as a "known integration
> offset awaiting arbitration"; it is **now fixed and PASSES** — a behavioral
> narrow-window write-delay shim supplies the ~1-dot CPU→PPU write-path offset
> (output `08 08 09 07`). It is a *fix* of the "pure CPU+PPU netlist isn't
> enough, the behavioral layer supplies the correct spec" kind, not a faithful
> deviation, and has moved out of this document (see the report page's
> "behavioral layer supplies the missing spec" section and knowledge-base
> §3.1 #13). Traditional-Chinese master: `2026-07-05-faithful-deviation-qa.md`.
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
   12+ items including several analog-race / integration-offset shims; the category is not a box
   for "couldn't fix it", but the residue left after fixes are exhausted and
   the evidence shows real machines behave this way
5. Every entry carries a **verifiable prediction** — anyone is welcome to
   test it against real hardware

---

## (fixed, moved out) `10-even_odd_timing` — was an integration-offset candidate, now PASSES

even_odd was once listed here as a "~1-dot absolute-phase offset in the two-netlist
board-level integration, awaiting arbitration." It is **now fixed and PASSES**: a PPUSim
cross-check proves both dies agree internally (VBL at (241,1), the odd-frame skip at
pre-render dot 339), so the offset is the **CPU→PPU cross-die write path** idealized to
zero delay. We supply that spec with a **test-mode behavioral shim** — delaying the
`$2001` render enable/disable transitions by 16 hc only at pre-render `vpos=261,
hpos=338..339` (the disable side clamps the complement node `/bkg_enable`, because GND
wins and you cannot force the main node high) — so `10-even_odd_timing` outputs
`08 08 09 07` and **passes**, green on the same alignment (K=1) as the whole NMI-edge family.

> This is the "**pure CPU+PPU netlist of the two dies isn't enough; the behavioral layer
> supplies the missing correct spec**" kind: the failure was a gap in our model (unmodeled
> cross-die trace delay), not a property of the real machine, and filling it makes the test
> pass. So it is **not a faithful deviation** — see the report page's "behavioral layer
> supplies the missing spec (these PASS)" section, knowledge-base §3.1 #13, and
> `2026-07-05-even-odd-integration-offset-campaign.md`. (Arbitrating the offset at its
> source — a PPUSim cross-check of the `$2002` read's absolute master-clock latency — rather
> than compensating with a shim, remains a planned follow-up.)

---

## Case 1: `cpu_dummy_writes_oam` — a revision-specific hardware bug (the sole remaining FAIL)

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
   **146/1** with 12+ fixes — three of which (DMC latch, ALU latch, frame-IRQ)
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
