# tools/visual6502-node — headless Node.js benchmark of the *original* visual6502 JS sim

A reference baseline: how fast does the **pristine visual6502 JavaScript simulator** run,
headless, with no browser? We load its core files **verbatim** (`chipsim.js` + `wires.js` +
`macros.js` from `ref/visual6502-master/`), stub out only the DOM/UI functions, and drive it
with the original `halfStep` / `handleBusRead` / `handleBusWrite` over a flat `memory[]`
**NOP-sled** (the synthetic "Infinite NOP Sled" benchmark — no test ROM needed). This gives a
clean hc/s number to compare against AprVisual S1/etc.

## Run

```
node tools/visual6502-node/bench.js [--chip 6502] [--hc 8000] [--warmup 2000] [--rounds 3]
```

Requires Node.js (tested on v24). Reads netlist data from `ref/visual6502-master/`
(gitignored upstream copy).

## Result (2026-06-15, this machine)

| Engine | Chip | Nodes | hc/s |
|--------|------|-------|------|
| **visual6502 original JS** (chipsim.js recursive group-walk, Node v24) | 6502 | 1704 | **~249** |
| AprVisual **S1** (optimized C# event-driven) | whole NES (2A03+2C02) | ~14,700 | **~135,872** |

The pristine JS reference does the 6502 alone at ~249 hc/s; S1 does ~8.6× more silicon at
~545× the wall-clock rate. This quantifies the distance from the canonical JS reference to the
optimized engine. The rate is extremely stable (248/249/250 across rounds), so tiny magnitudes
suffice.

## Caveats / TODO

- **This is the *simple* `chipsim.js`** (the textbook recursive group-walk), **not** the
  optimized `expertWires.js` that the live visual6502 JSSim website actually uses. The expert
  engine is faster; benchmarking it (heavier browser coupling) is a separate TODO if we want
  the "what the live site runs" number.
- **6502 only so far.** 6800 / z80 netlists exist in `ref/visual6502-master/chip-6800` and
  `chip-z80`, but those chips use chip-specific `support.js` harnesses (different bus protocol),
  not the root `macros.js` — adding them is a TODO.
- Not apples-to-apples vs S1 (different chip, language, simple-vs-optimized algorithm). Report
  it as a *reference baseline*, not a head-to-head.
