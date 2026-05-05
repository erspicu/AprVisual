# GPU Project Guide, Complete Edition

## Purpose

This document reorganizes the conversational `docx` notes under `GPU方案` into a more implementation-oriented guide. The original material is exploratory and idea-driven. This rewrite turns it into a practical project path with structure, implementation details, validation strategy, and explicit risk notes.

The real goal is not "build a fast ordinary NES emulator." The goal is to build a translation pipeline from switch-level chip netlists into analyzable, verifiable, executable logic models that can later target CPU, CUDA, and possibly FPGA-oriented flows.

The intended use cases are:

- Golden-model style verification
- hardware reverse engineering
- large-scale parallel simulation
- research or AI training workloads

If you only want a playable NES emulator, this route is excessive.

## 1. What This Project Really Is

The core idea can be stated simply:

> Take switch-level netlists such as Visual6502 / Visual2A03 / Visual2C02, convert them into a graph-based logic model, classify combinational and sequential behavior, then emit executable backends.

CUDA is not the hard part. FPGA is not the hard part. The hard part is the middle:

1. understand the source format
2. build the graph model
3. identify dynamic storage, feedback, and bus behavior
4. extract valid logic and state transitions
5. only then generate target backends

Most of the difficulty lives in steps 3 and 4.

## 2. Recommended Reading and Implementation Order

Based on the conversation flow, the most practical order is:

1. Understand what Visual6502 is and what abstraction level it represents.
2. Accept that the source is not standard SPICE or RTL.
3. Write a parser for `segdefs.js` and `transdefs.js`.
4. Build explicit node and transistor objects.
5. Implement DFS / SCC / loop detection for floating regions and feedback.
6. Integrate pull-up behavior.
7. Extract logic from pull-down and pull-up conditions.
8. Convert true state-holding regions into explicit sequential updates.
9. Validate with a CPU backend first.
10. Only then move to CUDA bit-parallel execution.
11. Treat full in-GPU NES/PPU/mapper execution as the final stage, not the beginning.

Starting from "everything stays inside the GPU" is the wrong entry point for most teams.

## 3. Source Data and Format

### 3.1 What Visual6502-style data represents

The source material is essentially a switch-level netlist:

- `Node`: an electrical node formed by connected conductors
- `Transistor`: an on/off channel controlled by a gate node

This is lower level than RTL, but higher level than full analog SPICE simulation. That is why it is attractive for logic extraction, but also why it cannot be treated as if it were already a gate-level netlist.

### 3.2 Why the non-standard format is acceptable

The original notes correctly point out that the format is not an industry standard. In practice, that is often helpful:

- it is close to JSON / JavaScript arrays
- it is much easier to parse than SPICE
- it avoids large amounts of analog process parameters

Still, do not discard important metadata while cleaning:

- visual coordinates may be optional
- pull markers such as `+` are not optional

### 3.3 Minimal parser output

A useful first parser should preserve at least:

- node id
- optional node name
- pull type
- transistor id
- gate/source/drain node ids

Additional source indices are useful for debugging.

## 4. Recommended In-Memory Model

Do not keep working on raw arrays after parsing. Convert everything into explicit graph objects.

```csharp
public sealed class NetNode
{
    public int Id { get; init; }
    public string? Name { get; set; }
    public char? PullType { get; set; }

    public List<NetTransistor> GateOf { get; } = new();
    public List<NetTransistor> ChannelOf { get; } = new();
}

public sealed class NetTransistor
{
    public int Id { get; init; }
    public NetNode Gate { get; init; } = null!;
    public NetNode Source { get; init; } = null!;
    public NetNode Drain { get; init; } = null!;
}

public sealed class NetlistGraph
{
    public Dictionary<int, NetNode> Nodes { get; } = new();
    public List<NetTransistor> Transistors { get; } = new();
}
```

You need to preserve two different relationships:

- control: which node acts as a gate for which transistor
- conduction: which nodes become connected when a transistor is on

Those are not the same thing.

## 5. The Main Conceptual Trap

The conversation repeatedly runs into the same deep issue: switch-level NMOS logic is not a simple directional logic tree.

Reasons:

- pass transistors are bidirectional
- some nodes float and retain state through capacitance
- pull-ups are weak drivers
- buses are shared and may contend
- feedback structures are not always obvious latches

That means this is unsafe as a general strategy:

- start at an output node
- recursively expand backward into a single Boolean tree

That works on simple NAND/NOR regions, but not on the full chip.

## 6. Recommended Analysis Pipeline

### 6.1 Stage A: static graph construction

Start with time-independent structure:

- load all nodes and transistors
- build adjacency
- identify fixed supply nodes such as `VCC`, `VSS`, `GND`
- identify important external pins such as `clk0`, `res`, `rw`, `ab0-ab15`, `db0-db7`

### 6.2 Stage B: conduction graph analysis

For a given control condition:

1. decide which transistors are on
2. treat source and drain as connected for on-transistors
3. compute connected regions or traversals

This tells you:

- whether a node is connected to GND
- whether it is connected to VCC
- whether it is isolated inside a floating island

### 6.3 Stage C: identify state-holding regions

The original notes point in the right direction by using DFS to find floating nodes, but two extra points matter:

- a floating node is not automatically a meaningful memory element
- the real storage element is often a floating connected component, not a single node

The more robust approach is:

1. find connected components cut off from the power network in a phase
2. check whether those components are charged or discharged in another phase
3. classify only those as dynamic state elements

If you classify individual nodes too early, you can break bus islands incorrectly.

### 6.4 Stage D: loop and feedback detection

The conversation mentions three-color DFS and SCC/Tarjan. That is a good foundation.

Recommended practice:

- build a logic-dependency graph
- collapse SCCs into super nodes
- classify each SCC as one of:
  - static cross-coupled storage
  - dynamic storage
  - bus-style feedback / sharing

Stopping recursion when a gray node is found prevents infinite expansion, but it does not by itself guarantee semantic correctness.

## 7. Practical NMOS Evaluation Rules

One of the most useful ideas from the notes is the evaluation priority:

1. if there is a strong path to GND, the next value is low
2. otherwise, if there is a path to a high source or pull-up, the next value is high
3. otherwise, the node holds its previous value

Conceptually:

```text
if connected_to_gnd:
    next = 0
else if connected_to_vcc_or_pullup:
    next = 1
else:
    next = previous
```

This is closer to the full intended model than simply using `!pullDownExpr`.

### 7.1 Pull-up integration

The original material is right to emphasize `PullType == '+'`. Many NMOS structures rely on depletion loads instead of explicit pMOS pull-up networks.

But note carefully:

- `+` does not mean "always equal to 1"
- it means "returns high when not actively discharged"

So `next = !pulldown` is only safe for specific static gate shapes. It is not a universal truth for dynamic sharing regions or bus-like structures.

### 7.2 GND wins

The notes use a "GND wins" rule. As an engineering simplification for ratioed NMOS logic, this is often reasonable.

Still, it should be documented as a simplification, not a complete analog law. Real circuits also involve:

- weak-high versus strong-low competition
- threshold loss
- charging time constants
- charge sharing

For cycle-accurate digital intent, the simplification is often acceptable. For analog fidelity, it is not.

### 7.3 `FindPathsToGnd` alone is not sufficient

The later notes already start acknowledging this.

If a node has no explicit pull-up but is connected through pass transistors to another charged or high node, returning `previous` may be wrong. It may actually recharge high.

A more complete evaluator should include at least:

- `FindPathsToGnd`
- `FindPathsToHigh` or `FindPathsToVcc`
- `IsFloatingIsland`

Without this, dynamic transmission behavior will be misclassified.

## 8. How to Use a Boolean Extractor

### 8.1 Where extraction is reliable

Start with regions that are relatively well behaved:

- standard inverters
- NAND / NOR structures
- simple series / parallel pull-down networks
- clearly clocked precharge/evaluate regions

Do not start with:

- shared buses spanning multiple floating islands
- strongly coupled dynamic logic clusters
- regions where high-path classification is still missing

### 8.2 Prefer an intermediate representation over string concatenation

The original notes often describe direct string generation for C++ or CUDA expressions. That is useful for quick demos, but not for a maintainable system.

A better path is to preserve an IR first:

```csharp
public abstract record Expr;
public record ConstExpr(bool Value) : Expr;
public record NodeExpr(int NodeId) : Expr;
public record NotExpr(Expr Inner) : Expr;
public record AndExpr(IReadOnlyList<Expr> Terms) : Expr;
public record OrExpr(IReadOnlyList<Expr> Terms) : Expr;
public record MuxExpr(Expr Sel, Expr TrueExpr, Expr FalseExpr) : Expr;
public record HoldExpr(int NodeId) : Expr;
```

This makes it possible to:

- simplify expressions
- emit multiple backends
- compare results between backends

### 8.3 Simplification

Safe simplifications are useful:

- `A && true => A`
- `A || false => A`
- deduplicate repeated subexpressions

More advanced options later:

- BDD
- AIG
- SAT-based equivalence checking

Do not start with the most complex simplifier.

## 9. Verilog and CUDA Backends

### 9.1 Verilog backend

Generating structural Verilog is reasonable, but this needs a distinction:

- a debug structural netlist
- a synthesizable logic netlist

Directly translating bidirectional switch graphs into `tran` / `nmos` primitives can be useful for inspection, but not all FPGA flows will like that as a synthesis target. In practice, the synthesizable version should be produced after logic extraction and sequencing.

### 9.2 CUDA backend

CUDA should not be the first validation environment. It should be the throughput backend after the model is already trusted.

Recommended sequence:

1. build a CPU evaluator
2. validate on tiny handcrafted netlists
3. validate on selected real subcircuits
4. then emit CUDA

Otherwise you end up debugging parsing, logic extraction, timing, memory layout, and branch divergence all at once.

## 10. GPU Memory Layout and Execution Model

### 10.1 SoA beats AoS

The source notes are correct about Structure of Arrays.

Prefer:

```text
state[node_id][instance_id]
```

over:

```text
state[instance_id][node_id]
```

because a warp often updates the same node across many instances.

### 10.2 `bool` is usually not the best storage type

The notes often use `bool*`. That is fine as a conceptual sketch, but usually not ideal for production CUDA:

- alignment and packing are not great
- bandwidth efficiency is poor

A more realistic direction is:

- `uint32_t` bit-slicing
- or `uint64_t`

Then one machine word represents 32 or 64 simulation instances.

### 10.3 Long-running in-GPU loops are possible, but not the best first milestone

Keeping the whole NES inside the GPU and only returning framebuffer/audio is a valid research direction, but it should be treated as an advanced stage:

- interactive latency may be poor
- debugging is harder
- watchdog and scheduling behavior may get in the way

A more practical staging is:

- phase 1: advance a fixed number of half-cycles per launch
- phase 2: return small trace snapshots
- phase 3: consider longer resident kernels

### 10.4 One thread per NES is usually safer than splitting CPU/PPU/APU across threads

One part of the conversation suggests assigning different threads to CPU, PPU, and APU. That is attractive in theory, but not a good first implementation:

- shared RAM and register synchronization become expensive
- branch divergence gets worse
- data races become much harder to reason about

If the system is already reduced to node-update equations, a simpler first design is:

- one thread, or one warp, owns one full instance

Profile before attempting more elaborate partitioning.

## 11. Clocking, Relaxation, and Cycle Accuracy

### 11.1 Cycle-accurate does not mean one Boolean pass

The source notes are correct to talk about:

- two-phase clocks
- half-cycles
- repeated settle / relaxation passes

Switch-level networks may require iterative settling because conduction and feedback do not always resolve in one pass.

Recommended approach:

1. apply a fixed phase input
2. iterate until convergence or a maximum iteration count
3. then switch phase

### 11.2 "Run exactly three settle passes" is a heuristic, not a law

This is an important correction.

Some regions may settle in one pass. Some may require more. A better implementation is:

- detect change
- iterate until stable
- stop at a maximum limit
- log a warning if the limit is exceeded

Example:

```text
while changed and iteration < max_settle:
    evaluate_all_nodes()
```

### 11.3 Reset is not the same as "pretend the bus reads NOP"

This is one of the larger questionable shortcuts in the original example.

For 6502 / 2A03, reset behavior matters because:

- internal state is initialized during reset
- execution then proceeds from the reset vector

Treating reset as if the data bus simply reads `0xEA` can be useful as a bring-up stub, but it should not be documented as correct hardware behavior.

Document it explicitly:

- `NOP on reset` is only a temporary bootstrap shortcut
- the real model must implement the reset vector and actual bus behavior

### 11.4 The relationship between `clk0` and internal clock phases must be checked in the real netlist

The original notes treat `clk0` as the external toggled clock input. That is usually directionally fine, but it must be verified in the actual dataset:

- pin naming may vary
- internal `phi1/phi2` generation should not be assumed from prose alone

Before committing to a final evaluator, verify:

- external clock pin nodes
- internal phase nodes
- precharge control nodes

## 12. Bus Integration and the External World

### 12.1 Bus integration must respect phase and drive direction

The notes are directionally correct:

- read address during the stable phase
- use `R/W` to decide external behavior
- drive data back into the bus nodes

But two practical caveats matter:

- bus direction and tri-state behavior are not simple overwrite semantics
- internal and external drivers may involve precharge, float, or contention

A first digital simplification is acceptable:

- when external memory responds, assume it is the unique driver
- otherwise let the internal chip logic determine the bus state

That is a simplification, not a final physical model.

### 12.2 Do not hard-integrate complex mappers too early

The fully integrated GPU vision includes mapper state. That is fine as an end-state, but not as a first milestone.

A saner order is:

- start with NROM
- add simple mirroring
- then consider MMC1 / MMC3

Every mapper adds a new state machine and validation burden.

### 12.3 PPU should be a second-stage target

The original material implicitly acknowledges this. The practical path is almost certainly:

1. CPU 2A03 first
2. memory bus second
3. PPU 2C02 later

The PPU is much harder because of shift registers, sprite evaluation, and CHR bus behavior.

## 13. Validation Strategy

Without a validation plan, this entire project can become a very convincing but very wrong system.

Validation should be layered.

### 13.1 Parser validation

- confirm node/transistor counts
- confirm important pins are found
- confirm pull markers survive parsing

### 13.2 Graph validation

- confirm gate/source/drain mapping
- confirm adjacency construction
- confirm connected components look reasonable under conduction

### 13.3 Tiny handcrafted netlists

Build miniature test circuits:

- inverter
- NAND
- NOR
- tri-state pass transistor
- dynamic latch

Each one needs an expected truth table and expected phase behavior.

### 13.4 Small real-chip regions

Do not start by validating the entire CPU. Start with small real regions from 2A03:

- reset chain
- a simple register bit
- part of an ALU carry path

### 13.5 Compare against reference models

Where possible, compare against:

- Perfect6502-style implementations
- Visual6502 outputs
- known reset / fetch / opcode sequences

### 13.6 CPU/CUDA equivalence

Use the same IR for both backends:

- run the CPU evaluator
- run the CUDA evaluator

The outputs should match node by node. This is essential. Otherwise you cannot tell whether the bug is in the model or in the backend.

## 14. Recommended Delivery Phases

### Phase 0: data cleaning

- parse `segdefs.js` / `transdefs.js`
- export your own cacheable representation

### Phase 1: CPU reference evaluator

- node state update only
- no CUDA yet
- validate on tiny test netlists

### Phase 2: dynamic storage and feedback classification

- SCC / connected component analysis
- pull-up / pull-down / float classification
- settle until convergence

### Phase 3: IR and simple C++ / Verilog emission

- dump expression trees
- dump debug netlists

### Phase 4: CUDA bit-sliced evaluator

- SoA layout
- `uint32_t` / `uint64_t`
- batched execution

### Phase 5: CPU bus and reset vector integration

- NROM only
- validate reset and fetch behavior

### Phase 6: PPU / full system / AI throughput

- only after the earlier phases are trusted

## 15. Corrections and Risk Notes on the Original Conversation

These are not rejections of the original ideas. They are guardrails to prevent implementers from copying the fragile parts too literally.

### 15.1 "Convert switch-level to FPGA, then simulate FPGA on GPU" is usually not the best main path

The later notes partially correct this already. A better route is:

- extract logic / IR directly
- emit CPU / CUDA / Verilog from that IR

Adding a virtual FPGA layer usually adds routing overhead without helping the core problem.

### 15.2 "If pull-up exists, just return true" only works in narrow contexts

If no GND path exists and a pull-up exists, the node often returns high. But if there are:

- competing weak sources
- charge sharing
- non-settled phase behavior

then "just true" can be too aggressive.

### 15.3 "Dynamic node equals previous state" is an engineering abstraction, not full physics

It is a very useful abstraction, but still an abstraction. Real behavior also depends on:

- retention time
- leakage
- charge sharing

For cycle-level digital behavior, it may be acceptable. For finer physical fidelity, it is not enough.

### 15.4 "A giant autonomous NES kernel on GPU" is an end-state, not an MVP

This is a final architecture idea, not a starting milestone. Without prior validation layers, it becomes an opaque black box that is very hard to debug.

### 15.5 Duplicate source chapters

`GPU 內全封裝晶片架構藍圖` and `GPU 實作全封裝晶片架構` are effectively duplicates. One can be kept as the canonical chapter and the other treated as duplicate source material.

## 16. Short Advice for Future Implementers

If you want to build the same kind of system, the stable order is not "write CUDA first." It is:

1. parse the netlist cleanly into a graph
2. build a CPU evaluator with conduction, pull-up, floating hold, and convergence
3. validate on tiny handcrafted circuits
4. validate a small real 2A03 region
5. only then build CUDA bit-slicing

If these steps are incomplete, do not jump to "simulate 10,000 NES instances on a GPU."

## 17. Summary

The real value of this project is not "use a lot of GPU." The value is building a trustworthy translation chain:

- silicon connectivity
- graph model
- logic and sequencing abstraction
- verifiable execution backends

If the middle model is correct, CPU, CUDA, and Verilog are just output formats.

If the middle model is wrong, a sophisticated CUDA backend only makes the wrong answer run faster.

---

## Appendix A: Recommended MVP

A realistic minimum viable milestone is:

- 2A03 only, not 2C02
- NROM only
- validate only the first reset and fetch cycles
- compare CPU evaluator against CUDA evaluator
- support only:
  - GND path
  - VCC/pull-up path
  - floating hold
  - settle until convergence

That is already far closer to a real system than most concept sketches.

## Appendix B: Suggested Output Files

For maintainability, consider emitting at least:

- `nodes.json`
- `transistors.json`
- `graph_debug.json`
- `ir_debug.json`
- `cpu_eval_trace.json`
- `cuda_eval_trace.json`
- `equivalence_report.md`

Those files make future debugging much cheaper.
