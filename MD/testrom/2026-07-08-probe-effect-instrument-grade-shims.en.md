# Don't Touch the Device Under Test — a probe-effect lesson, and Gemini's instrument-grade principle

> A teaching article. It tells a pit we actually fell into: to make one test pass we *modified* the
> simulated circuit, and that broke a **completely unrelated** test — even though the modification
> never actually did anything, not once. Then it walks through the principle Google Gemini handed us
> that set the whole thing straight, and the takeaways anyone writing a simulator / EDA tool /
> discrete-event system can keep. Traditional-Chinese master:
> `2026-07-08-probe-effect-instrument-grade-shims.md`.

## 1. Background: what we're doing

AprVisual.S1 is a switch-level engine that simulates the NES's two chips (the 2A03 CPU and 2C02 PPU)
**transistor by transistor**, straight off Visual6502-style silicon netlists. We point it at the
standard hardware-accuracy test ROMs (blargg's suites and friends); the vast majority pass outright.

A handful of "off-by-a-hair" tests need a small **behavioral fix (a shim)**. The reason: that correct
behavior lives *outside* what pure switch-level propagation of the CPU+PPU can express — analog physics
(charge decay, power-on latch settling), a **same-half-cycle race**, or a ~1-dot trace delay in the
two-die board-level integration. Real hardware has a well-defined answer for each; we're just supplying
that spec back.

Which raises the real question: **how do you *attach* a shim without breaking something else?** That
"how you attach it" is what this article is about — the hard way.

## 2. What went wrong: a "zero-fire" shim broke an unrelated test

We set out to fix `double_2007_read`. The first version was the intuitive one — **add a few nodes and
transistors to the netlist graph** to implement the fix logic (9 fake nodes / fake transistors).

The strange part: on that run the shim **never fired once** (zero fire). By intuition, "does nothing =
affects nothing."

The result: a **completely unrelated** test, `dma_2007_read`, flipped from PASS to a pattern **real
hardware cannot produce** (it read X=00).

> How can something that never fires break something else?

## 3. Why: changing the graph re-rolls the "alignment lottery"

At load time the engine performs a **class-major node renumbering** — for performance it sorts nodes
into contiguous id blocks by class. That means:

1. The moment you "add a node," **every node's id shifts**.
2. And id order *is* the engine's per-half-cycle settle-wave order (the relaxation loop that solves the
   circuit to a fixed point) — specifically the **order of processing within a wave**.
3. It so happens that a whole class of tests hinge on a **same-half-cycle race** — e.g. "DMA halt vs.
   bus read," which goes first. In a two-state settled model such a race can only be decided **one way**,
   and which way it tips is sensitive to the settle order.

Chain the three together:

> **Add a few fake nodes → every id shifts → intra-wave settle order changes → that same-wave race flips
> → an unrelated test goes red.**

The key realization: **any load-time change to the graph structure re-rolls the dice for *every*
same-wave-race test.** Even if the thing you added never does anything — because the damage isn't in
*what it does*, it's in *the fact that it changed the graph*.

An analogy: it's like setting a screw down *next to* a precision balance. You never touched the pan, but
the whole system's equilibrium has quietly moved.

## 4. The second mistake: per-test overfitting

After hitting the pit, there's a natural — and equally wrong — reflex: "then I'll only enable this shim
**for this one test** (per-test scope)."

Gemini cut straight to it: that is **overfitting / technical debt**.

The reasoning: a shim that stands in for a *real physical propagation delay* is a property of **the
machine**, not a property of "some test." It must apply consistently to **all** tests. Scoping it
per-test is to claim "this physical phenomenon only exists while running this test" — that isn't
simulation, it's peeking at the answer key.

## 5. Gemini's principle: instrument, don't modify the DUT

The one-liner: **Instrument, don't modify the DUT (Device Under Test).**

Treat the circuit under test like a real chip on the bench. You don't **solder a new circuit onto it**
to measure it — you touch it with a **probe / fixture** from the outside, and lift the probe when
you're done.

### 5.1 Absolute-override force / release (the EDA-standard answer)

A test instrument needs an **absolute override**, not a rewiring. The industry-standard answer is a
VPI-style `force` / `release`: pin a node to a value temporarily, release afterward, **without touching
the circuit topology at all**.

### 5.2 Implementation: InstClampLow / InstClampHigh / InstRelease (zero graph footprint)

Following the principle, we built three runtime APIs: OR the `Gnd` (or `Pwr`) flag directly **into** the
target node. During group resolution this behaves exactly as if a conducting vss path had joined the
group — top of the group-resolution LUT priority, overriding active drivers.

**The key: zero graph change.** No new nodes, no new transistors, no id shift, no change to settle order
→ **no lottery re-rolled**. The performance-path (benchmark) golden checksum is **bit-identical** with
or without these shims.

Discipline: test-mode only; only on nodes without a static Gnd flag; release is the caller's
responsibility (a reset rebuilds the flags, clearing any leftover).

### 5.3 Global, not per-test

Since a runtime instrument-grade shim is an "absolute-override, zero-graph-footprint" force, it should
apply **globally** — it stands for the machine's physics, which holds for every test.

Measured proof: we made three such shims (double_2007, the even_odd write-path delay, the OAM-DMA
bus-hold) **global by default in test mode** and re-ran the full 146-test regression — **zero
collateral**: everything that should pass passes, and the only red is that one faithful deviation.

### 5.4 What genuinely *should* be swapped: the Socket Pattern

One kind of change *is* legitimate: swapping a pluggable **fixture**, such as the controller — it's an
external peripheral by design. But "swap the controller" is **also a load-time graph change** in our
engine, so it trips the same probe effect. Gemini's answer is the Socket Pattern:

1. **Two-phase load**: Phase 1 loads only the immutable DUT (the console + the controller-**port
   boundary**), runs the class-major renumbering, and **locks ids 0..N**; Phase 2 then attaches the
   behavioral controller only if requested.
2. **Tail allocation**: every new node the fixture brings is assigned tail ids **> the DUT's max id**.
3. **Boundary docking**: the fixture's output pins connect to the port nodes Phase 1 already froze.

Result: **whether or not a controller is plugged in, the DUT's internal ids / graph / BFS expansion tree
are 100% isomorphic** → zero probe effect. The controller can then be "globally available" without
moving any alignment.

### 5.5 Draw the line: DUT fix vs. fixture change

Gemini separated the two kinds of change — this is the mental skeleton of the whole affair:

| Change | Nature | Correct resolution |
|---|---|---|
| tie rewire (board def ties a pin that should float to vss) | a **DUT modeling flaw** | fix **globally** (not per-test — otherwise the other tests run on a "wrong-revision board") |
| swap the controller module (gate-level → behavioral) | a **fixture change** (pluggable peripheral) | per-test is conceptually right, but use the **Socket Pattern** for zero disturbance |

### 5.6 A safety net: the Graph Fingerprint

The essence of this bug was "someone inadvertently changed the graph without realizing the
alignment-sensitive tests had to be re-verified." The fix is simple and effective:

- After Phase 1 completes (**before** any fixture is attached), compute a DUT fingerprint: node count +
  edge count + (the rigorous version) a hash of the sorted adjacency.
- Bake it into the test harness; verify before every run; on mismatch throw `DutGraphMutatedException`.
- Effect: the next time anyone touches the board def / graph structure, CI immediately reports "**the
  alignment has been re-rolled; re-verify ppu_vbl_nmi and update the fingerprint**" — turning an
  **unknown random failure** into a **known, expected change**.

## 6. Outcome

With the three runtime instrument-grade shims made global, the clean full regression is **145/1
(99.3%)**, zero collateral. The sole remaining FAIL is a **genuine faithful deviation**
(`cpu_dummy_writes_oam` — the RP2C02G-revision-specific OAMADDR-write corruption bug, which real G
hardware fails too) — that one is *supposed* to be red and is out of scope here (see the faithful-
deviation Q&A).

## 7. Takeaways (for anyone writing a simulator / EDA tool / discrete-event system)

1. **Don't modify the thing you're measuring.** Use an external force / release; don't edit the topology.
2. **Beware "zero-fire is still harmful."** The damage can come from *what you added*, not *what it does*
   — especially when the engine is sensitive to node order.
3. **Physical properties are global, not per-test.** A pass that needs per-test scoping is overfitting,
   not simulation.
4. **Separate a DUT bug from a fixture change.** Fix the former globally; swap the latter via a socket.
5. **Encode immutability as a check.** A Graph Fingerprint turns "quietly changed the graph" into "CI
   screams immediately."

---

**Further reading (full technical detail and evidence chains)**
- [Test-Fix Knowledge Base §2.6, the probe effect](00_test-fix-knowledge-base.md) — the three arming-guard misfire lessons, the InstClamp implementation discipline
- [Socket Pattern / global-DUT-fix architecture note](2026-07-05-socket-pattern-target-architecture.md) (ZH) — two-phase load, tail allocation, the three options incl. Graph Fingerprint
- [Faithful-deviation in-depth Q&A](2026-07-05-faithful-deviation-qa.en.md) — the one "supposed-to-be-red" remaining FAIL
