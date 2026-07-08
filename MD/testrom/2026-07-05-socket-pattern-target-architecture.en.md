# Target Architecture: Socket Pattern + Global DUT Fix + Graph Fingerprint

> Traditional-Chinese master: `2026-07-05-socket-pattern-target-architecture.md`.

> Source: Gemini consultation `tools/knowledgebase/q/a_joypad_scoping_20260705.md` (a
> continuation of the same general principle from the previous dbl2007 probe-effect
> consultation). **This is a "reference for how the approach should later be revised",**
> not the current state — the current state is a **viable stopgap** using per-test scope
> (the joypad regression is already fixed).

## Core Framing (Gemini)

**This time per-test is "half right, half wrong"**; the key is to distinguish two kinds of change:

| Change | Nature | Correct fix |
|---|---|---|
| **tie re-wiring** (u7/u8, 6 lines vss→vcc) | **DUT fix** (on the real chip those 6 pins are actually floating; tying them to vss in the board def is a **modeling defect**) | **Globally** remove those 6 vss ties (not per-test; otherwise it's like running the other 138 tests on a "wrong-revision motherboard") |
| **module replacement** (nes-pad → behavioral) | **Test Fixture change** (the controller is a pluggable peripheral) | per-test is **conceptually correct**, but must be done with the **Socket Pattern** for zero disturbance |

## Option 1: Socket Pattern (zero-disturbance module replacement)

Upgrade "Tail Allocation" into a "socket pattern":

1. **Two-phase loading**:
   - Phase 1 (DUT): load only the NES console + Controller Port (the socket boundary), and
     **do not load any controller at all** (neither switch-level nor behavioral); perform
     class-major renumbering, freezing the ids and BFS order of the main graph 0~N.
   - Phase 2 (Fixture): based on `--joypad`, decide whether to instantiate `nes-pad-behavioral`.
2. **Tail mounting**: all new nodes of the behavioral controller (including fake transistors) are
   uniformly assigned tail numbers **> DUT max id**.
3. **Boundary hookup**: the controller's output pins connect to the Controller Port nodes already
   finalized in Phase 1.

**Result**: whether or not a controller is plugged in, the Phase 1 DUT-internal ids / graph / BFS
expansion tree are **100% isomorphic** → zero probe effect, the alignment lottery is absolutely
unchanged. → **the controller can return to "globally available" without regressing any test**.

## Option 2: tie re-wiring → globally remove the vss tie

- (a) ✅ At load time, **do not add** those 6 vss ties (fix the board def, globally).
- (b) ❌ runtime override: `Gnd > Pwr`, and a strong supply overriding a physical ground = short circuit,
  easily oscillates at the switch level.
- (c) ❌ per-test: this is a DUT physical bug, it cannot be per-test.
- **Cost**: removing the 6 edges disturbs the BFS near LS368 → **alignment will be re-thrown**. You must
  **bravely take the hit once**: remove globally → accept that the ppu_vbl_nmi family breaks → re-find /
  re-throw that family's alignment seed / judgment frame → **bake a new baseline**. A one-time technical-debt
  repayment.

  > **Note (this project's trade-off)**: currently per-test leaves those 138 non-$4016 tests on the
  > original vss-tie board — and they **don't read $4016 at all**, so the tie bug is invisible to them →
  > functionally correct, and **no recalibration needed**. Removing the tie globally is purely for
  > architectural purity, but it costs an alignment recalibration (which would touch the K=1 baseline and the
  > even_odd work).
  > **Conclusion: globalizing the tie is scheduled to be done together with the Socket refactor, not brought
  > forward on its own.**

## Option 3: Graph Fingerprint (prevent this class of regression from recurring) — high value, low risk

The essence of this bug = "inadvertently changed the graph structure, without realizing that
alignment-sensitive tests needed re-running".

- When Phase 1 completes (before mounting any fixture), compute the DUT fingerprint: total node count +
  total edge count + (rigorous version) a hash of the sorted adjacency.
- Hard-code it into the test framework config; verify before every run. Mismatch → throw
  `DutGraphMutatedException`.
- Effect: next time someone touches the board def / graph structure, CI immediately crashes and names it:
  "alignment has been re-thrown, please re-verify ppu_vbl_nmi and update the fingerprint". Turns an
  **unknown random failure** into a **known expected change**.

## Landing Priority (this project)

1. **Current state (done)**: per-test scope stopgap — the joypad regression is fixed, ppu_vbl_nmi is back
   to green, no recalibration. **Keep this for now.**
2. **Near-term, low-risk**: implement the **Graph Fingerprint** (Option 3) — this is precisely the mechanism
   that would have **caught this bug at the first moment**, and it doesn't touch the alignment baseline.
   Worth doing first.
3. **Future focused refactor**: the **Socket Pattern** (Option 1) lets the controller return to global with
   zero disturbance + together **globally remove the vss tie** (Option 2) + recalibrate alignment + bake the
   judgment frame + update the fingerprint. This is Gemini's ultimate solution ("immutable DUT + dynamic
   tail-mounted fixture"), but it will touch the K=1 and even_odd baselines, so it must be done as an
   independent campaign.
