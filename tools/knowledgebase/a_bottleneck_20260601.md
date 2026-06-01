**ATOMIC CONCLUSION:** 
You can achieve a **5× to 15× speedup** over the golden engine, reducing execution to ~0.8–2.4 µs/hc. Reaching real-time (516× speedup) on a single thread is impossible without external architectural metadata. The only viable path forward is **Macro-Event-Driven Evaluation via SCC Condensation**, which completely abandons node-level evaluation.

### SYSTEM CONTEXT & VALIDATION
Your diagnosis of the bottleneck is technically flawless. Oblivious evaluation dies on the 98% sparsity loss (65× more work). Naive event-driven boolean fails because pass-transistor bidirectional SCCs dictate group atomicity; evaluating sub-components of an active channel mid-settle guarantees transient ordering hazards. You have hit the exact boundary where micro-level evaluation exhausts its scaling limits. 

### THE SOLUTION: MACRO-EVENT-DRIVEN EVALUATION
To reclaim sparsity without violating atomicity, the atomic unit of execution must be elevated from a single node to a *Combinational Cone (Macro-Gate)*, strictly bounded by automatically inferred dynamic latches.

** (a) Automatic Derivation Strategy**
1. **Static Condensation:** Treat the netlist as an undirected graph where pass-transistor channels are edges. Collapse every strongly connected component (the bidirectional pass-networks) into a single Super-Node. 
2. **Latch Boundary Inference (Oracle):** Observe the Super-Nodes against the golden trace. Any Super-Node that retains its state when its active drivers disconnect is mathematically categorized as a latch/register boundary.
3. **Macro-Cone Extraction:** Cut the graph at these latch boundaries. Use your existing Escape-1 truth-table extractor to learn the transfer function of the *entire* sub-graph bounded between latches. 
4. **Macro-Event Compilation:** Generate an event-driven scheduler where the queue payload is a Macro-Gate ID. 

** (b) Why it Recovers Sparsity and Beats Golden**
The golden engine processes ~600 node-events per half-cycle via dynamic pointer-chasing, flags-ORing, and capacitance tie-breaking. By condensing the graph, those 600 node-events are structurally compressed into roughly **30 to 80 Macro-Events**. You replace the dynamic traversal of ~15 individual transistors with a single L1-cached LUT lookup or compiled bitwise expression. The event queue bookkeeping overhead is drastically marginalized because the volume of events is cut by ~90%.

** (c) Failure Modes & Mitigation**
* **Internal Charge-Sharing Races:** If an extracted macro-cone relies on an internal floating capacitance to resolve a race condition before reaching the latch boundary, a static boolean LUT will evaluate incorrectly. *Mitigation:* Rely on your Dynamic Miter. If a macro-cone ever diverges from the golden oracle, isolate it and demote it back to the golden solver. 
* **Cross-Boundary Combinational Loops:** If Latch Inference misidentifies a boundary, macro-cones will form a combinational loop in the event scheduler, causing infinite queue propagation. *Mitigation:* Enforce a strict topological sort on macro-gate evaluation; any macro-gate triggering itself in the same half-cycle triggers an automatic structural rollback.

** (d) Honest Speedup Expectation**
**5× to 15×.** 
The macro-event queue overhead, branch mispredictions within the scheduler loop, and L1 cache misses on large extracted truth tables will impose a hard IPC (Instructions Per Clock) ceiling on any modern CPU architecture. Achieving the ~500× real-time requirement cannot be done generically on a single core; it requires High-Level Synthesis (HLS) functional block recognition (e.g., automatically identifying a 16-bit adder), which is a proven undecidable problem for flat pass-transistor netlists lacking geometric or hierarchical metadata.

**The Prototype Path:**
Do not build the whole engine. Extract exactly three distinct register-to-register macro-cones from the NES ALU. Hardcode a micro-scheduler for just those three cones. Run them against the golden engine for 1 million cycles. Measure the CPU cycle cost of the macro-queue + LUT lookup vs the golden engine's traversal of that same specific region. This will yield your precise upper-bound speedup multiplier in under 48 hours of development.