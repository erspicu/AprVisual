# Automatic transistor-netlist → fast CPU simulator. Need a faster-than-golden, fully-automatic, CPU-only method. Be brutally honest.

## The system
We simulate the NES 2A03 (a 6502-family CPU + APU) at the SWITCH (transistor) level, from a Visual6502/MetalNES-style netlist: ~14,700 live nodes, ~30k NMOS transistors, pass-transistor logic. We do NOT have RTL or source — ONLY the transistor connectivity (gate, c1, c2) + which nodes are VCC/GND/pull-up. Charge-sharing / ratioed / dynamic timing exists but is not annotated (no capacitances) — so pure static analysis of state/clock/analog from the netlist is undecidable.

## Golden engine (the bit-exact reference we must beat)
Event-driven switch-level (Bryant-style), ported to C#, heavily optimized (struct-of-arrays, unmanaged pointers, fast-path). On one modern CPU core: **~83,000 half-cycles/s = 12.0 µs per half-cycle.** Per half-cycle it resolves connected transistor groups via a flags-OR → 256-entry LUT, with a floating-capacitance tie-break (largest-capacitance node holds). Crucially: **only ~600 nodes change per half-cycle; the average conducting group walked is ~1.4 nodes.** Event-driven sparsity is the whole reason it is this fast.

Real-time NES = 42.95M hc/s → ~516× faster than golden. We do NOT strictly need real-time; we need "meaningfully faster than golden, fully automatically." State the gap honestly anyway.

## What we built and PROVED (fully automatic, no hand-crafting) — "Escape-1"
Using the netlist + the golden engine as an ORACLE (empirical-dynamic analysis, since static analysis is undecidable):
1. Coverage probe: for each node, is its value a consistent boolean function of its radius-1 inputs (its transistors' gate + far-end states) over a long golden trace? → 96% of observed nodes are clean boolean.
2. Extractor: learn each clean node's truth table online (dense table for ≤16 inputs; sparse hash map for 17–60).
3. Bus model: wide (>60-input) data/address buses are resolved by replicating the golden group-resolution (transitive walk over ON channels → flags LUT + largest-capacitance float tie-break = dynamic-latch HOLD), reading the model's own state. No truth table.
4. State cut + verify-then-enable: a Dynamic Miter runs the model beside golden every half-cycle; any node that ever diverges is auto-demoted (state element / true analog). Converges to a set that reproduces golden 100% behaviorally on the covered nodes.

Validated on Super Mario Bros, whole-NES, 100.000% match golden over >1e9 node-checks:
**98.9% of all activity (golden state-changes) is reducible to {cheap boolean logic 76.5%} + {register/structural 22.4%}; only 1.1% is truly irreducible analog (no-channel / charge-share).** The chip is ~99% logic+registers. This reducibility result is solid and automatic.

## The bottleneck (why this does NOT translate into speed)
We evaluate the extracted logic OBLIVIOUSLY: every half-cycle, sweep all ~6,000 extracted nodes. Because the pass-transistor network is one giant BIDIRECTIONAL SCC (every pass transistor makes a 2-cycle: A is B's far-end and vice-versa), we cannot levelize → we iterate the sweep to a fixed point (~6.5 iterations). So ~6,000 × 6.5 ≈ **39,000 node-evals per half-cycle**, vs golden's ~600. ~65× more node-work.
- Interpreted oblivious sweep: 539 µs/hc = **45× SLOWER than golden**.
- Roslyn-compiled straight-line oblivious sweep: 1004 µs/hc = **84× slower** (compiled is slower than interpreted because the ~440 bus/structural group-walk nodes dominate cost and call overhead swamps the dense straight-line savings; the dense boolean part is the cheap minority of total cost).

Conclusion so far: oblivious abandons golden's event-driven sparsity, and that sparsity is worth far more than the per-node cheapness we gain. (This matches our earlier independent finding that an oblivious eval was ~121× slower, and that the ~94% bidirectional pass-transistor SCC + the 1.4-node sparsity is the structural wall.)

We ALSO previously tried EVENT-DRIVEN evaluation of the extracted logic (re-eval only dirty nodes via per-node boolean, like golden but with cheap boolean instead of group resolution). It FAILED two ways: (a) correctness — per-node evaluation breaks golden's "conducting-group atomicity" (a connected group must resolve as one unit; evaluating a member in isolation mid-settle gives the wrong value, and the floating tie-break depends on the exact transient event order), giving ~hundreds of thousands of mismatches; (b) speed — where it was correct it only broke even with golden (the bookkeeping for the dirty queue ≈ the work saved). We also confirmed a GPU single-instance kernel is ~10.7× slower than the CPU golden (one workgroup ≈ 1/76 of the GPU; the rest idle) — GPU only helps for MANY-instance bit-sliced throughput, which is a different (throughput, not latency) question.

## Hard constraints
- **CPU only, single instance** (latency, not multi-instance throughput).
- **Fully automatic**: everything must be program-derived from {netlist + golden oracle}. NO hand-written per-subsystem behavioral code. NO "recognize it's a 6502 and swap in a 6502 emulator." Higher-level abstraction is welcome and expected — but it must be AUTOMATICALLY analyzable and constructible by our tool, generically (it would have to work on an unknown chip too).
- Behavioral correctness is acceptable (NOT per-node bit-exact). We already sacrifice per-node bit-exactness; framebuffer + test ROMs passing is the bar.

## The question
Given {transistor netlist + golden oracle}, is there an AUTOMATIC, CPU-only, higher-abstraction method that recovers the lost sparsity/locality and beats a well-optimized event-driven switch-level engine on a SINGLE instance?

I suspect your instinct is "lift to a higher abstraction" — good, but specify HOW to build it automatically. For EACH idea you propose, give: (a) exactly how it is auto-derived from {netlist + oracle} with no manual per-block work; (b) WHY it would recover sparsity / beat golden's 12 µs/hc; (c) what breaks it (specifically address the group-atomicity, transient-ordering, and bidirectional-SCC problems we already hit); (d) a rough, honest expected speedup vs golden.

Candidate directions you might consider (critique or replace them):
- Automatic register / clock-domain inference + EVENT-DRIVEN evaluation at the REGISTER or combinational-CONE granularity (coarser events than nodes, so fewer events but each event evaluates a cheap pre-extracted cone) — does this dodge the per-node group-atomicity problem by making the cone the atomic unit?
- Automatic combinational-cone memoization / incremental recompute keyed on which cone inputs actually changed (sparsity at the cone level).
- Automatic functional-block recognition (adder / mux / counter / shift-register / RAM) from structure OR from the oracle's observed I/O behavior, replaced by a native op (this is "behavioral lifting" but auto-derived — is it actually tractable from a netlist?).
- Trace-directed / region specialization (observe the golden trace; specialize the hot repeating cycle).
- Anything else.

Be brutally honest. If the honest answer is "on a single CPU instance, fully automatically, you cannot beat a well-optimized event-driven switch-level engine, because X," then say so plainly and explain X — do NOT hand-wave a Verilator/RTL-class number that assumes clean RTL we do not have. We have wasted real effort on optimistic hand-waving before and explicitly want the truth this time. If there IS a real automatic path, describe the smallest prototype that would validate it (faster-than-golden + behaviorally correct) before we invest.
