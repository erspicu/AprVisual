# Final stress-test: your macro-event proposal FAILED empirically. Is there ANY remaining automatic CPU single-instance path, or is this genuinely a dead end? A clear "dead end, because X" is a fully acceptable answer. Do NOT hand-wave.

This is the third consult in a chain. I will give you the complete picture, including the decisive measurement that killed YOUR OWN previous proposal, so you have everything. I want either a genuinely-new idea we have not tried, or an honest "this is a wall, here is the precise reason." We have repeatedly wasted effort chasing optimism; I value a correct "no" over a hopeful "maybe."

## The target
Simulate the NES 2A03 (6502-family CPU + APU) at the SWITCH/transistor level from a flat Visual6502/MetalNES netlist (~14,700 live nodes, ~30k NMOS pass transistors). Inputs: only connectivity (gate,c1,c2) + VCC/GND/pull-up. No RTL/source. **CPU only, single instance (latency), fully automatic (program-derived from {netlist + a golden oracle}), behavioral correctness OK (not per-node bit-exact).**

## The golden engine (the thing to beat)
Event-driven switch-level (Bryant), ported to C#, heavily optimized (SoA, unmanaged, fast-paths). **12.0 µs/half-cycle = 83,000 hc/s on one core (~48,000 CPU cycles/hc at 4 GHz).** Per half-cycle: ~90–600 nodes change (workload-dependent; ~90 in a quiet window, the average conducting group walked is ~1.4 nodes); for each it walks the connected ON-transistor group, OR's flags → 256-entry LUT → resolved bit, with a floating-capacitance tie-break. Tiny code (fits L1i), tiny hot working set.

## Everything we have tried and RULED OUT (with measured numbers — do not re-propose these)
1. **Oblivious eval** (extract boolean per node, sweep all ~6,000 extracted nodes, relax to fixed point ~6.5 iters because the pass network is one giant bidirectional SCC): 45× slower interpreted. Reason: abandons event-driven sparsity (~39,000 node-evals/hc vs golden's ~90–600).
2. **Oblivious COMPILED** (Roslyn straight-line, ~6,000 statements, ~700 KB of code): 84× slower — and SLOWER than the interpreter. Confirmed i-cache thrash (code footprint ≫ 32 KB L1i, streamed 6.5×/hc → front-end bound). So codegen/LLVM is self-defeating for whole-circuit-every-cycle.
3. **Per-node event-driven boolean** (re-eval only dirty nodes via cheap boolean instead of group walk): correctness FAILS — per-node eval breaks the conducting-group atomicity (a connected group must resolve as a unit; mid-settle per-node eval gives wrong values; the floating tie-break depends on the exact transient event order). Where correct, it only broke even on speed.
4. **Macro-event via SCC condensation (YOUR proposal): cone = the atomic unit, event-driven at cone granularity, est 4–6× if ~50 cones fire/hc.** We MEASURED the cone compression ratio directly. Result, decisive NO-GO:
   - Cones built by far-end (channel) connectivity, cut at bus/latch (the true electrical settle-unit): **2,737 combinational cones, MEAN SIZE 2.0 nodes** (median 1, p90 2, one outlier 629).
   - Per half-cycle: 90 node-events → **81 distinct macro-units → 1.1× compression (essentially NONE).** Activity is ~1:1 node-to-cone — diffuse, does not cluster.
   - Cones built by gate+far-end connectivity instead: collapse into ONE giant 5,349-node cone → firing it = full oblivious again.
   - **The deep reason: the combinational cone mean size (2.0 nodes) ≈ golden's mean conducting group (1.4 nodes). Golden is ALREADY operating at the optimal event granularity. There is no coarser, reusable structural unit to extract — the chip's natural atomic unit IS the ~1.4-node conducting group, and golden already uses exactly that.** Structural compression ceiling ≤ ~2× → cannot yield 4–6×.
5. **AOT batch backends** (compile whole circuit, evaluate per cycle): 3–6× slower than golden (re-evaluates ~14.7k nodes/hc when only hundreds change).
6. **GPU single instance**: 10.7× slower than the CPU golden (one workgroup ≈ 1/76 of the GPU; the rest idle). GPU only helps for MANY-instance bit-sliced THROUGHPUT (a different, latency-irrelevant question).
7. **Functional-block (HLS) recognition** (auto-detect "this is a 16-bit adder/counter" and swap a native op): you yourself called this undecidable for a flat pass-transistor netlist with no geometric/hierarchical metadata. (Agree?)

## What we DID prove (automatic, solid)
~98.9% of the chip's activity is reducible to {boolean logic + registers}; only ~1.1% is truly irreducible analog. The reducibility is real and 100%-behaviorally-validated against golden. It just does not translate into single-core speed, because of the above.

## The core wall, stated plainly
Golden's event-driven switch-level engine is essentially a hand-tuned sparse-graph delta-propagation that already operates at the netlist's natural minimum granularity (the ~1.4-node conducting group). Every abstraction we tried either (a) loses the sparsity (oblivious), (b) breaks group atomicity (per-node), (c) finds no coarser structure to exploit (macro-event compression 1.1×), or (d) blows the i-cache (codegen). Golden does ~48,000 cycles/hc doing ~90–600 minimal node-updates.

## The question — be brutally honest
Is there ANY automatic, CPU-only, single-instance method we have NOT tried that could beat this golden engine? Consider angles ORTHOGONAL to the spatial/structural ones we exhausted, for example (critique, replace, or reject each):

- **Temporal / transition memoization**: the workload (a CPU running code) revisits the same micro-states repeatedly (tight loops, vblank waits, idle polling). Could we automatically hash the "relevant live state + inputs" at a cycle boundary and cache the resulting state-delta, replaying it on a hit instead of re-simulating — i.e. a trace/transition cache for the circuit? What is the relevant-state-identification problem, the hit-rate reality, and the correctness risk (aliasing of un-hashed state)? Is this a real win or a mirage?
- **Reducing per-EVENT cost rather than event count**: golden spends ~80 cycles per node-update (adjacency pointer-chase + flag-OR + LUT + enqueue). Is there an automatic transform that makes each delta-propagation step cheaper (better layout, branchless group resolution, precomputed per-node "next group shape") without changing the event count or breaking atomicity? Or is ~80 cycles already near the floor?
- **Coarser TIME-step / cycle-skipping**: automatically detecting predictable multi-cycle phases and jumping. Is this tractable without behavioral modeling?
- **Anything else** — including approaches from other simulation domains (gate-level ASIC sim, SPICE acceleration, RTL co-sim, JIT/trace techniques) that could be auto-applied here.

For each idea: (a) how is it AUTO-derived from {netlist + oracle}; (b) why would it beat 12 µs/hc given golden is already at the granularity optimum; (c) what breaks it; (d) honest expected speedup and on what workloads.

If the honest answer is "no — golden is at the algorithmic optimum for a single instance and nothing automatic beats it, for reason X," then say exactly that and name X precisely. That is a valuable, fully acceptable conclusion. Do not invent a Verilator/RTL-class number. Tell me the truth.
