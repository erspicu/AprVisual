This is a genuine, structural dead end. 

I am giving you the hard "no." Your golden engine has reached the algorithmic and architectural floor for simulating a flat, switch-level netlist of this size on a single-core von Neumann CPU. There is no automatic, single-instance software abstraction left that will yield a meaningful speedup. You have solved the problem as optimally as the hardware allows, and you should stop chasing ghosts.

Here is the precise, mathematically verifiable reason why this is a wall, followed by the brutal teardown of the remaining orthogonal angles.

---

### The Wall, Defined: The Sparsity vs. Density Crossover

Every circuit simulator must choose between two sides of an equation. The cost of a half-cycle is:
**`Cost = MIN( Event_Count × Event_Overhead,  Total_Nodes × Iterations × Native_Op_Cost )`**

**1. The Sparse Side (Your Golden Engine)**
*   **`Event_Count`:** ~90–600. As you proved with the 1.1× macro-event failure, the natural atomic unit of the chip is ~1.4 nodes. You cannot lower the event count because there is no coarser structural clustering to exploit.
*   **`Event_Overhead`:** ~80 CPU cycles.
*   **Total:** ~24,000 to 48,000 CPU cycles / half-cycle.

**2. The Dense Side (AOT / Oblivious)**
*   **`Total_Nodes`:** ~6,000 boolean statements.
*   **`Native_Op_Cost`:** ~2–3 CPU cycles (superscalar L1d read/write + AND/OR).
*   **`Iterations`:** ~6.5. 
*   **Total:** 6,000 × 6.5 × 2.5 ≈ 97,500 CPU cycles / half-cycle (which perfectly explains your measured 3–6× slowdown).

**The inescapable trap:** To make the Dense side beat the Sparse side, you **must** reduce `Iterations` from 6.5 to 1. An iteration count of 1 requires a perfect Topological Sort (a Directed Acyclic Graph). 
**You cannot auto-derive a static DAG from a flat pass-transistor netlist.** The 6502 relies on two-phase transparent latches and bidirectional pass-gates. Without knowing the clock phases *in advance*, the static extraction must treat these transparent paths as cyclic (SCCs). Because you are restricted to "automatic derivation without RTL/source hints", you cannot statically sever the DAG at the latches. You are structurally condemned to 6.5 iterations, meaning Dense execution will always lose to your Golden engine.

---

### Teardown of the Remaining Angles

Here is exactly why the orthogonal ideas you proposed are either mirages or physically impossible on modern CPUs.

#### 1. Temporal / Transition Memoization (Trace Cache)
**Verdict: Mirage & Performance Regression.**
*   **Why it fails:** The 2A03 is not just a CPU; it contains the APU. The APU has multiple internal timers, audio sweeps, and a 15-bit LFSR (noise channel) that tick every single cycle. 
*   Because these counters use mutually coprime wrap-around periods (e.g., triangle linear counter vs. square wave envelope), the **global micro-state of the chip virtually NEVER repeats** during normal execution.
*   Even if we assume a tight infinite loop (like a `JMP *`), the memory footprint required to gather and hash the 2,000+ stateful bits scattered across the chip takes more than the 12.0 µs you are trying to save. 
*   **Correctness Risk:** To hash only the "CPU part" and ignore the APU requires auto-detecting the functional boundary between them to prevent state aliasing. You cannot auto-derive that boundary from a flat netlist. 

#### 2. Reducing Per-EVENT Cost
**Verdict: Hard Physics Floor.**
*   **Why it fails:** Your Golden engine takes ~80 CPU cycles per node-event. In a modern x86/ARM pipeline, 80 cycles is roughly 25–40 instructions. 
*   For every event, the CPU must: fetch from the event queue, load the node state (L1 cache access), check if it flipped (branch), iterate over 1–3 adjacency edges (pointer chase = dependent load), evaluate the LUT, and push to the queue. 
*   You are entirely bottlenecked by **L1d latency (load-use chains) and branch prediction on an irregular graph.** You cannot JIT-compile this to be "branchless" because the data dictates the control flow dynamically. 80 cycles is the absolute architectural speed limit of a CPU walking a random 14.7k node graph. There is nothing left to squeeze.

#### 3. Coarser Time-Step / Cycle-Skipping
**Verdict: Undecidable (for fully automatic).**
*   **Why it fails:** To safely skip cycles, a simulator must mathematically prove that no internal state will cross a threshold that alters output behavior during the skipped window. 
*   In RTL/Verilog, this is sometimes possible because counters are explicit (`count <= count + 1`). In a flat switch-level netlist, a counter is just a mesh of transistors. Proving $N$-cycle predictability requires symbolic execution of the netlist to recover high-level semantics. As you noted, functional block recognition on a flat geometric netlist without metadata is practically undecidable. 

#### 4. SPICE-Acceleration / RTL Co-sim Tricks
**Verdict: Inapplicable.**
*   Commercial ASIC simulators (like Verilator) achieve massive speeds (100+ MHz) because they are *fed* an RTL DAG by the human engineer. They do not auto-derive it from polygons/transistors. 
*   When EDA tools *do* simulate flat switch-networks (e.g., PrimeSim/HSIM), they use partitioning and matrix solvers that are built for accuracy, and they measure performance in milliseconds per cycle, not microseconds. Your engine is already orders of magnitude faster than commercial EDA switch-level simulators.

---

### The Final Conclusion

You are not failing to see a hidden trick; you have fully mapped the boundaries of the problem. 

1. You cannot combine nodes spatially (Macro-events = 1.1×).
2. You cannot flatten the graph statically (Oblivious = 6.5 iterations).
3. You cannot skip time temporally (APU state diverges continuously).
4. You cannot lower the instruction cost (80 cycles is the pointer-chasing floor).

**The exact reason nothing automatic will beat Golden is that Golden optimally exploits the only exploitable property of this specific netlist: dynamic sparsity (only 1% active).** By doing so, it accepts the overhead of dynamic pointer-chasing. You cannot escape the pointer-chasing overhead without switching to dense execution, and dense execution requires a statically perfectly-directed acyclic graph, which cannot be automatically extracted from a two-phase bidirectional switch-level netlist.

You have written what is likely the fastest single-instance switch-level simulator possible for the 2A03 on a von Neumann architecture. Take the win, document the 12.0 µs / 83,000 hc/s as the global optimum, and stop sacrificing engineering time to this wall.