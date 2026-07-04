# Faithful Deviations — an Adversarial Q&A, assuming a veteran emulator developer as the challenger

> This document works in reverse: we **write the strongest attacks for the
> challenger first**, then answer each one. Subjects: the two permanent
> faithful deviations (`10-even_odd_timing`, `cpu_dummy_writes_oam`) and the
> "faithful deviation" category itself. Traditional-Chinese master:
> `2026-07-05-faithful-deviation-adversarial-qa.md`. Full evidence chains:
> the [knowledge base](00_test-fix-knowledge-base.md) and the report page's dossier.

## 0. Our admission criteria (rules first, cases second)

A FAIL may be classified as a faithful deviation only if ALL of:

1. **The test author's own documentation** records the behavior as
   hardware-dependent (readme / source comments / on-screen statement)
2. **Community corroboration** (NESdev wiki / forums)
3. Where feasible, **independent-model cross-checks** (e.g. emu-russia's
   independent reverse-engineering of the same die)
4. **Whenever real hardware is deterministic, we fix** — this project fixed
   10+ items including three analog-race shims; the category is not a
   dumping ground but the residue after fixes are exhausted
5. Every deviation carries a **falsifiable prediction** — we invite
   real-hardware refutation

---

## Case 1: `10-even_odd_timing`

### Q1 | "My real NES sometimes passes all ten ppu_vbl_nmi tests. Your 'mutual exclusion' is your simulator's artifact, not physics."

**A:** Our claim is narrower than you think, deliberately so:

- We claim: **on this silicon model**, no single state in the reachable
  power-on alignment space (completely enumerated, below) satisfies both the
  NMI-edge family (8 tests) and 10-even_odd.
- Completeness is not sampling: a K=0..5 sweep proved intermediate reset
  offsets quantize onto the same 4 classes (K=2≡K=1, K=4≡K=3 — identical
  verdicts, fail codes, frame counts); the divider pair restarts on whole
  clk0 periods, so **only these 4 relative phases exist**.
- "PPU ÷4 restarts at /res release; CPU ÷12 free-runs" is not our design
  choice — it is **emergent netlist behavior**, measured by probes.
- Your real-console observation (if it holds) does not directly contradict
  our claim — it would point to a **locatable, concrete error**: a 1-dot
  relative offset between our two divider models. That is precisely the open
  question written in the dossier.

**Falsifiable prediction:** take an NTSC NES-001 and record, **within a
single power-on** (no power cycling), video evidence of both `05-nmi_timing`
and `10-even_odd_timing` passing. Do that and our exclusion claim is refuted
— and gives us the data to correct the divider-phase model. We would treat
it as a gift.

### Q2 | "You chose alignment 7 to maximize your score."

**A:** Partially true — and that is exactly how we wrote it up: any **fixed**
alignment system (including a real NES that is not power-cycled) trades one
group against the other. We chose the phase blargg's NMI-edge tests were
calibrated on (8 tests vs 1), and published the **complete alignment ×
verdict matrix** together with its cost (the 10-even_odd FAIL). Score-gaming
hides the matrix; we made the matrix the primary result.

### Q3 | "Behavioral emulators pass all ten because the consensus timing model allows it. More likely your simulation has a 1-dot error — did you verify the absolute VBL-set dot and skip dot?"

**A:** Honest answer: **the relative phase space is completely verified; the
absolute dot positions are emergent netlist values with no independent
arbitration yet.** This is the one exposed surface this dossier admits.
Mitigation and follow-up:

- Control group: TriCNES (whose author owns real hardware) passes all ten by
  **hand-tuning its power-on offset**; its own source comment reads:
  "Shouldn't this be 0? I don't know why, but this passes all the tests if
  this is 7, so...?" — calibration in alignment space, not evidence that a
  single physical power-on state can satisfy both groups.
- Queued arbitration path: emu-russia's **PPUSim** (independent
  schematic-level reverse-engineering of the same 2C02 die) can run the same
  scenario; we already used their APUSim for the same kind of arbitration in
  the DMC case (result: two independent silicon models failing identically =
  abstraction limit, not anyone's bug).

---

## Case 2: `cpu_dummy_writes_oam`

### Q1 | "The test's real subject is CPU dummy writes (the double write of RMW opcodes). OAM is just the verification vehicle. Your FAIL is probably hiding a real CPU bug."

**A:** The strongest challenge — with the hardest counter-evidence: the
sibling test **`cpu_dummy_writes_ppumem` uses the exact same dummy-write
mechanism with PPU memory as the vehicle — and we PASS it.** The CPU's
double-write behavior is independently verified. The `_oam` variant fails
precisely at the vehicle it itself declares unreliable.

### Q2 | "AprNes and TriCNES both pass these two tests. If the behavioral layer can do it, why can't you?"

**A:** The answer is **chip revision**, with current NESdev wording (2026):

> "Writing to OAMADDR on the **2C02G** causes OAM corruption — this usually
> seems to **copy sprites 8 and 9 (address $20) over the 8-byte row at the
> target address**."
> "It is known that in the **2C03, 2C04, 2C05, and 2C07, OAMADDR works as
> intended**."

- Our reference machine, the **RP2C02G, is exactly the revision with this
  bug**; later PPUs fixed it.
- blargg's tests must write $2003 (OAMADDR) to position their reads — on a
  real G console that steps into the corruption; his on-screen line
  ("emulators pass, the real NES does not") matches this mechanism.
- AprNes / TriCNES pass because they implement the **idealized (= fixed
  revision) OAMADDR**; Mesen2 ships the G behavior as an **off-by-default
  option literally named `EnablePpuOamRowCorruption`** (row corruption —
  matching NESdev's 8-byte-row wording), and fails these tests when enabled.
- Our OAM and its addressing logic are the die tracing's physical circuits —
  the corruption **emerges** rather than being an implementation option.
  "Passing" would require replacing the G silicon's behavior with a
  fixed-revision idealized array — making the simulation less faithful.

### Q3 | "Quoting the author's warning is a get-out-of-jail card."

**A:** The on-screen original (screenshot on the report; anyone can re-run):

> "Requirement: OAM memory reads MUST be reliable. This is often the case on
> emulators, but **NOT on the real NES**."

The author printed "emulators usually pass, real hardware doesn't" at the
top of the test screen, and Q2's NESdev mechanism (what the author observed
as "unreliable" in 2005, the community has since localized to the G
revision's OAMADDR-write corruption) interlocks with the warning — this is
mechanism, not rhetoric. Companion evidence: the same author's `oam_read`
readme records **the same real console** producing four patterns across
power-ons, three ending in "Failed" — our verdict falls inside that
documented lottery family (currently on the passing pattern; the very fact
that it can flip IS the faithful behavior).

### Q4 | "Your corruption pattern may not match the real G's 'sprite 8/9 row copy' bit-for-bit — it could be a third artificial state."

**A:** Partially valid, and accepted in full: we have not yet verified our
corruption pattern bit-for-bit against NESdev's description (and our cells
do not model charge decay — a documented gap). Our claim is **class
faithfulness**: the G revision's "write OAMADDR → OAM unreliable" behavior
class, not a specific bit pattern (the author himself wrote "these values
are probably unique to my NES" — exact patterns vary per machine anyway).
A bit-level comparison (netlist corruption vs the sprite-8/9-row
description) is listed as open work.

**Falsifiable prediction (revision edition):** on a real **RP2C02G**
console, `cpu_dummy_writes_oam` fails and `oam_read` shows the four-pattern
lottery; on a **2C03/2C05 (RGB PPU)** console both should pass. Anyone with
a Vs./RGB-modded machine can test this directly — either outcome is welcome.

---

## Meta | "The whole 'faithful deviation' category is unfalsifiable cope — any bug can be declared 'analog'."

**A:** Refuted by the track record:

1. **We fixed an order of magnitude more than we exempted**: the score went
   115/26 → 140/5 with 10+ fixes, three of which (DMC latch, ALU latch,
   frame-IRQ) are themselves "analog races" — where real hardware is
   deterministic, we fix rather than exempt.
2. The category has **admission criteria** (§0, five clauses) and every
   entry carries a **falsifiable prediction**.
3. Independent-model arbitration has precedent: in the DMC case we ran
   emu-russia's APUSim — two independent silicon models failed identically
   while behavioral models pass — a repeatable experimental demonstration of
   an abstraction limit, not rhetoric.
4. The category is **bidirectional**: oam_read used to sit here as a failing
   family member; after an engine fix landed it on the passing pattern we
   moved it out of the fail list and rewrote the entry — classification
   follows evidence, not score.

## Closing

The common structure of both deviations: **the tests encode the specific
physics of the author's machine as expectations.** Behavioral emulators
implement the consensus rules and naturally pass; a transistor-level
simulation reproduces the physics of one specific chip and therefore
honestly diverges exactly where physics genuinely varies per machine. What
we can offer — and an all-passing emulator cannot — is a circuit-level
explanation and a falsifiable boundary for every point of divergence.
