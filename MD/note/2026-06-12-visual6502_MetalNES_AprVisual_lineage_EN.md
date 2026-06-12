# visual6502 → MetalNES → AprVisual.S1: the lineage, each generation's optimizations, and our original RCM-improved design (2026-06-12)

> English edition of `2026-06-12-visual6502_MetalNES_AprVisual系譜_優化清單與RCM改良版.md`.
> Sources: WebSite/comparison.html (lineage + same-machine numbers), index.html (optimization arc),
> rcm-revival.html (the RCM reversal), CLAUDE.md, MD/note/01-07 (MetalNES study), MD/note/06 (S1
> algorithm cross-reference).

## 1. The relationship — one algorithm, three generations

```
visual6502 (2010, JS)          ── where the algorithm was born: transistor-level 6502 simulation,
        │                         the prototype of the group-resolution algorithm
        │  chipsim.js: recalcNodeList / recalcNode / computeNodeGroup / getNodeValue
        ▼
MetalNES (C++)                 ── scales the algorithm from "one CPU" to "a whole NES":
        │  wire_module.cpp's wire_compute = an optimized port of chipsim.js
        │  + a module system (2A03 + 2C02 + board TTL) + behavioral RAM/ROM
        ▼
AprVisual.S1 (C# / Rust)       ── our work: first a faithful port with correctness infrastructure
                                  (bit-exact golden checksums, dual-engine cross-validation), then a
                                  systematic optimization program on the algorithm and data layout —
                                  ~2.3× faster than MetalNES on the same machine, bit-verifiable
```

- **visual6502**: browser JS, educational. ~1 clock/s animated, ~250 Hz+ in expert mode. It defined
  the core semantics all three generations run: event-driven settle + conducting-group BFS +
  flags-OR → priority resolution (GND wins → VCC/pull-up → external drive → hold previous) +
  purely-floating groups resolved by the largest-capacitance member's previous state.
- **MetalNES**: S1's direct reference implementation (`ref/metalnes-main`). Our `Sim/WireCore.*` was
  ported function-by-function against its `wire_module.cpp` (MD/note/01 has the mapping).
- **AprVisual.S1**: portable net10.0 headless console, the golden engine; `experiment/rust-s1` is a
  bit-identical Rust twin.

## 2. What MetalNES added on top of visual6502

1. **Whole-system composition**: not just a 6502 — the Visual2A03 + Visual2C02 chip netlists plus
   the board's TTL chips, composed into a complete NES-001 via `.js` system definitions
   (pins / modules / connections / pullups / forceCompute / memory).
2. **An optimized C++ port**: chipsim.js's interpreted logic moved to native data structures; the
   **256-entry FlagsToState lookup table** (one OR-ed flag byte → one table read replaces the
   priority-ladder branches).
3. **Behavioral memory**: RAM/ROM are handlers, not transistors (the callback = fake-transistor
   mechanism) — removing the largest mass of pointless simulation.
4. **ForceCompute**: the special Gnd+Pwr-cancel resolution for certain bus nodes.
5. Trade-offs / limits: only `'+'` segdef pull-ups kept (the `'-'` column dropped), the weak flag
   unused, and no bit-exact equivalence methodology. Measured on our machine: **~55K hc/s**
   headless (our ancestor; VisualNes reads ~24K on the same machine).

## 3. What AprVisual.S1 added on top of MetalNES

### Correctness infrastructure first (and guarding every step since)
- **Bit-exact golden checksums**: an FNV-1a hash over the whole NodeStates array (three goldens at
  300k/400k/1M half-cycles) + an SMB1 10M-half-cycle gate + a selftest; no optimization ships
  unless the checksums are unchanged.
- **Dual-engine cross-validation**: C# and Rust implemented independently, producing bit-identical
  output — an algorithmic bug would have to be made twice, identically.
- Fixes to MetalNES's trade-offs: both `'+'`/`'-'` pulls kept; "driven high" explicitly
  distinguished from "floating hold-previous".

### The shipped performance ledger (chronological; methodology = interleaved-paired A/B behind checksum gates)

| Layer | Item | Gain |
|---|---|---|
| Data layout | unmanaged SoA hot data (byte* states, 16-byte NodeInfo with an **inline payload** (S2-A), ushort transistor lists), zero bounds checks | baseline + S2-A C# +4.18% |
| Load time | lowering: always-on shorts merged (441 nodes / 530 transistors dropped) | +3.7% |
| Walk | iterative BFS + hoisted locals (+3.2%), dual-pair 64-bit loads (+1.2–1.4%) | |
| Fast path | cls1 static pure-logic O(1) resolve (+2%, +1.6% more from dropping the PullUp gate) | |
| **Fast path** | **R-1 dynamic singleton**: all c1c2 gates OFF ⇒ the group is provably {nn} ⇒ O(1) | **C# +18.6% / Rust +12.5% (largest ever)** |
| **Prunes** | **P-1 same-state turn-on prune** (= prune-merge done right: static classification instead of runtime group-ids) | **+11.85% / +11.36%** |
| Prunes | P-2 turn-off isolation, P-3/P-4 capacitance-guarded turn-on extensions (together delete ~21% of all node re-evaluations) | C# +7.7% / Rust +10.0% |
| Micro | &&-clause reorder (C# +~1%), supply-skip fold (C# +1.5–2%), .NET 11 (+~1%) | |
| **Renumbering** | **range-prune + the self-captured locality key (the RCM-improved design, §4)** | **C# +3.56% +6.17% / Rust +2.90%** |
| Fast path | **B1 pair path**: provably-two-node groups resolved inline (size-2 = 77% of all walks) | C# +0.6% / **Rust +14.45%** |

**Same-machine comparison (Ryzen 7 3700X, full_palette, same unit)**:
VisualNes ~24K < **MetalNES ~55K** < perfect6502 29K (6502-only, different unit) <
**S1 C# ~126.7K / Rust ~118.5K hc/s** — about **2.3×** our MetalNES ancestor, with bit-level
verifiability on top. NES real time (42.95M hc/s) remains ~339× away.

## 4. The finale: our original "RCM-improved" design

Classic RCM (Reverse Cuthill-McKee) renumbers nodes by graph adjacency to improve cache locality.
We measured it **ineffective** on this engine in May — the hot set is already cache-resident; the
bound is the dependent-load chain, not misses. June's reversal came from **changing the objective
of renumbering**, in two steps, both original in form:

1. **Range-prune**: the renumber's primary key becomes the *static prune class* — each class
   occupies one contiguous id range, so the hottest loop's prune-mask table lookups become
   **register range compares** (one dependent load deleted per endpoint check). The mask is still
   recomputed at every Reset as ground truth and verified node-by-node against the ranges, with an
   automatic safe-degenerate fallback (the JIT deoptimization-guard pattern). C# +3.56% /
   Rust +2.90% — and the two engines derived **identical block boundaries** (A=460 / S=1275 /
   B=7532), a free cross-validation.
2. **The self-captured first-touch locality key**: within each block, the order comes from no
   static approximation — **the engine measures itself at load**: a three-pass load (classify →
   rebuild with a temporary key + warm up + capture 32K half-cycles of the production cascade's
   TRUE first-pop order through a cold instrumented copy of the settle loop → final rebuild with
   the captured order). No file, no flag, any ROM, immune to workload drift by construction.
   C# +6.17% (20/20 paired). The decisive empirical finding: **the key's value is the PRUNED
   cascade's ORDER, not cache-line density** — an equally-dense non-production order measured ±0.

Prior-art positioning (Gemini consult; details in MD/note/2026-06-11-論文準備\*): the individual
components belong to known lineages — Inspector-Executor (Saltz '91), trace-driven layout
(Chilimbi PLDI '01), dynamic deoptimization guards (Hölzle PLDI '92), ECS archetype sorting — but
the synthesis (**an in-process, self-contained, event-driven execution-trace capture used to relay
out a switch-level simulator's graph memory**) has no precedent and was judged a publishable
original contribution (suggested title: *Dynamic Data Relayout via Online Execution Trace Capture
in Event-Driven Switch-Level Simulation*).

One-line summary of the three generations: **visual6502 invented the algorithm, MetalNES composed
it into a NES, and AprVisual turned it into a bit-verifiable research engine that is 2.3× faster
and contributed an original relayout technique.**
