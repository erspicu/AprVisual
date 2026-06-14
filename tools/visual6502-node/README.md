# tools/visual6502-node — headless Node.js benchmark of the *original* visual6502 JS sim

A reference baseline: how fast does the **pristine visual6502 JavaScript simulator** run,
headless, with no browser? We load its core files **verbatim** (`chipsim.js` + `wires.js` +
`macros.js` from `ref/visual6502-master/`), stub out only the DOM/UI functions, and drive it
with the original `halfStep` / `handleBusRead` / `handleBusWrite` over a flat `memory[]`
**NOP-sled** (the synthetic "Infinite NOP Sled" benchmark — no test ROM needed). This gives a
clean hc/s number to compare against AprVisual S1/etc.

## Run

```
node tools/visual6502-node/bench.js [--chip 6502|6800|z80] [--hc N] [--warmup N] [--rounds N]
```

Requires Node.js (tested on v24). Reads netlist data from `ref/visual6502-master/`
(gitignored upstream copy). All three chips run on the **same unmodified `chipsim.js` core**;
the only per-chip code is each `support.js` (different clocks + bus protocol + skip-weak setup),
loaded last so its `setupTransistors` / `halfStep` / `initChip` win.

## Results (2026-06-15, this machine, Node v24)

| Engine | Chip | Nodes | Transistors | NOP | hc/s (median) |
|--------|------|-------|-------------|-----|---------------|
| **visual6502 original JS** (chipsim.js recursive group-walk) | 6502 | 1704 | 3510 | 0xEA | **~249** |
| | 6800 | 2012 | 3923 | 0x01 | **~149** |
| | z80  | 3595 | 6781 | 0x00 | **~166** |
| AprVisual **S1** (optimized C# event-driven) | whole NES (2A03+2C02) | ~14,700 | — | — | **~135,872** |

Observations:
- **Speed is not just node count.** z80 is the biggest (3595 nodes) yet beats the 6800 (149);
  the 6800's two-phase `phi1`/`phi2`/`dbe` clock toggles more nodes per half-step. What matters
  is the per-half-cycle group-walk churn under the NOP sled, not chip size.
- Every rate is **extremely stable** (±1–3 hc/s across rounds), so tiny magnitudes suffice.
- These are **pure-core** numbers (UI stripped); the live browser sims are slower still.
- **Anchor:** S1 does the *whole NES* (~8.6× the 6502's silicon) at ~545× the 6502-JS rate —
  the distance from the canonical JS reference to the optimized engine.

## Is there a faster upstream core? No — checked 2026-06-15

The live visual6502 JSSim (`expert.html`) loads, in order: segdefs / transdefs / nodenames /
`wires.js` / `expertWires.js` / **`chipsim.js`** / memtable / `macros.js` / testprogram. The
**simulation core is `chipsim.js`** — the very file we benchmark. `expertWires.js` is *only* the
expert-mode UI (zoom, mouse, URL params, canvas drawing); it defines no sim functions.
`expert-allinone.js` (1.5 MB) is just a single-file bundle whose `recalcNodeList` / `recalcNode`
/ `addNodeToGroup` / `getNodeValue` are **byte-identical** to `chipsim.js`. So:

> **~249 hc/s already IS the upstream's algorithm** (the recursive group-walk). The live
> interactive page is actually *slower*, because it also runs per-step canvas/DOM work that we
> strip here. There is no faster JS core in the project to measure.

## Caveats

- **6502 / 6800 / z80 all done.** Each non-6502 chip loads its own `support.js` after the core;
  6800 and z80 `setupTransistors` skip the `weak` (7th-column) transistors, matching upstream.
- Not apples-to-apples vs S1 (different chip, language, simple-vs-optimized algorithm). Report
  it as a *reference baseline*, not a head-to-head.
