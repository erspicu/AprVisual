# GPU Project Guide

## Document Positioning

This document reorganizes the entire set of `docx` files under `GPU方案` into a single project guide that follows the original narrative flow as closely as possible. The working assumptions for this version are:

- the technical descriptions and recommendations in the original files are treated as correct
- important concepts, methods, and implementation paths should be preserved as completely as possible
- only obvious repetition, conversational endings, or non-essential wording is removed

This is not just a summary. It rebuilds the original content into one continuous project path so that an implementer can move from basic understanding all the way to building a GPU simulation core.

## 1. What This Project Is Actually Trying to Build

The core of the project is to take switch-level chip netlists such as Visual6502, Visual2A03, and Visual2C02, and convert them into higher-level models that can be analyzed, extracted, compiled, and executed.

The final goal is not simply "read a netlist" and not simply "write CUDA." It is a full pipeline:

1. read the original chip netlist
2. build graph-based data structures
3. identify memory nodes, feedback structures, and dynamic logic
4. convert bidirectional switch networks into logically equivalent combinational and sequential logic
5. emit different backends
6. execute or verify them on CPU, CUDA, and potentially FPGA-oriented flows

Because of that, the real key is not the hardware target itself. The real key is the compiler in the middle that can understand switch-level topology and extract logic from it.

## 2. Visual6502 and the Abstraction Level

Visual6502 is not a normal high-level emulator. It is a transistor-level reconstruction of a real chip produced from decapsulation, imaging, and vector reconstruction. For this project, the most important fact is:

- it describes a switch-level netlist
- nodes represent wires or electrically connected regions
- transistors are treated as ideal switches controlled by gate nodes

Its abstraction level sits between:

- something lower than RTL
- something higher than full SPICE-style analog simulation

That is exactly why it is suitable for graph analysis and digital logic extraction, and why it can be translated into CPU or CUDA execution models.

## 3. The Visual6502 Format Is Not an Industry Standard, but That Is an Advantage

The Visual6502 format is not a standard format such as Cadence, Synopsys, SPICE, or Verilog. It is a highly customized project format that usually mixes:

- logic information
- node IDs
- transistor connectivity
- polygon coordinates and visualization data

The practical meaning of this is:

- it is not ideal for direct use with traditional EDA tools
- but it is very suitable for writing a custom parser and loading into memory

Compared with parsing standard SPICE, this format is easier to clean and load in C#, Python, or C++. For this project, that is not a disadvantage. It is the entry point.

## 4. First Decide Which Chip You Actually Want to Process

If the target is NES, the source data should not stop at the generic Visual6502 MOS 6502.

In practice, the correct chip datasets are:

- CPU: Visual2A03
- PPU: Visual2C02

The reason is that the NES does not use a plain standard 6502:

- 2A03 contains a modified CPU core without decimal mode
- it also includes APU and DMA-related logic
- 2C02 is the NES PPU and is significantly more complex than the CPU

So if the goal is physical-level or cycle-accurate NES simulation, the real data source should be 2A03 and 2C02 rather than just the generic 6502.

## 5. Step One: Write a Parser and Turn the Data into a Graph

The first implementation step is to read `segdefs.js` and `transdefs.js` and convert them into a graph model.

The target is straightforward:

- nodes become `NetNode`
- transistors become `NetTransistor`
- gate/source/drain relationships become explicit control and conduction links

A suggested core structure is:

```csharp
public class NetNode
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public char? PullType { get; set; }
    public List<NetTransistor> GatesControlled { get; } = new();
    public List<NetTransistor> ConnectedSwitches { get; } = new();
}

public class NetTransistor
{
    public int Id { get; set; }
    public NetNode Gate { get; set; } = null!;
    public NetNode Source { get; set; } = null!;
    public NetNode Drain { get; set; } = null!;
}
```

In this model you must keep two different kinds of relationships:

- control: which node acts as the gate for which transistor
- conduction: which nodes become connected when a transistor is on

That distinction is the basis of all later analysis.

## 6. PullType in `segdefs.js` Is Extremely Important

The original material places special emphasis on the `+` marker in `segdefs.js`, and this matters a great deal in NMOS chip analysis.

The reason is that many internal 6502 / 2A03 logic regions do not use explicit pull-up switches to VCC. Instead they rely on:

- depletion-mode loads
- pull-up behavior

If the parser does not preserve that property, later simulation will drift badly, because a node without a pull-down path may naturally return to high.

So the parser must preserve more than node IDs and transistor connectivity. It also needs to preserve:

- pull-up / pull-down characteristics
- pin names or otherwise identifiable node names

## 7. This Is Not an Ordinary Logic Tree, but a Bidirectional Switch Network

At this point the project enters its real difficulty.

Switch-level circuits are fundamentally different from ordinary RTL or gate-level logic because:

- signals are not inherently one-way
- pass transistors make source and drain bidirectionally conductive
- some nodes float
- some nodes retain state through parasitic capacitance
- several paths may form shared buses or feedback structures

So the netlist cannot be treated like an ordinary directional logic tree.

This is why the original material repeatedly emphasizes:

- FPGA is a world of one-way LUTs and flip-flops
- switch-level logic is a world of bidirectional switches and floating nodes

If you want to turn a switch-level netlist into something CUDA or FPGA can execute, you must first perform logic extraction and timing extraction in the middle.

## 8. The Core Bottleneck: Memory Behavior

The hardest part of the whole route is how to handle circuits that "remember state."

In the physical switch-level world, memory is not limited to explicit DFFs or latches. Very often it looks like:

- a node temporarily loses its driver
- but because of parasitic capacitance it keeps its previous voltage

That is dynamic logic or floating-node storage.

In a higher-level logic model, this must be translated into:

- explicit feedback
- explicit state nodes
- explicit `current_state -> next_state` behavior

So the compiler must:

- identify which nodes become isolated islands in a given phase
- decide whether those islands must be treated as memory/state elements
- replace them with executable state-update formulas

## 9. Use DFS to Find Dynamic Memory Nodes

The original material provides a clear direction here: under a given condition, start from VCC and GND and traverse only currently conducting transistors using DFS or another connectivity analysis.

The process can be summarized as:

1. decide the current clock or control condition
2. determine which transistors are on
3. traverse every reachable node from VCC and GND
4. any node not reached is currently undriven

Those undriven but still meaningful nodes are the candidates for hidden memory nodes.

This is the key step where the work moves from static netlist handling into dynamic analysis.

## 10. Detect Loops First, Then Cut Them

Once logic extraction starts, another major problem appears:

- bidirectional transmission structures and feedback create loops

If DFS is used to expand logic naively:

- the process may recurse forever
- or it may generate exponentially large expressions

So the proper strategy is:

- detect loops first
- identify feedback and state nodes first
- when logic expansion reaches them, replace further expansion with `current_state`

The original material suggests:

- three-color DFS
- or SCC / Tarjan-style analysis

The point is not merely to keep the code from hanging. The point is to separate sequential logic from combinational logic.

## 11. Boolean Equation Extraction

After graph construction, conduction analysis, memory-node identification, and loop cutting, Boolean equation extraction can begin.

The logic described in the original material is very clear:

- series transistors correspond to AND
- parallel transistors correspond to OR
- if there is a conducting path to GND, the node is pulled low
- if there is no pull-down path and a pull-up exists, the node returns high

For standard NMOS static logic, this makes it possible to translate many real circuit regions into expressions like:

- `!A`
- `!(A && B)`
- `!(A || B)`
- more complex combinations of series and parallel networks

This is how a physical switch network becomes a compilable mathematical expression.

## 12. Integrating PullType into Logic Extraction

It is not enough to search only for GND paths. The later part of the original material extends the extractor by integrating PullType into the rule set:

1. if a conducting pull-down network exists, the result is `false`
2. if no pull-down network exists but the node has `+`, the result is `true`
3. if no pull-down network exists and there is no `+`, the node keeps `current_state`

This means the extractor does not only emit static logic. It can also translate dynamic storage nodes into logic expressions with explicit hold behavior.

For example:

```text
(pullDownExpr) ? false : current_state[node]
```

That is the software form of a floating physical storage node.

## 13. The Final NMOS Extraction Rule

The later original material further strengthens the NMOS extraction model and presents a more complete priority order:

1. first ask whether there is a path to GND
2. if not, ask whether there is a path to VCC or to another high node with `+`
3. if neither exists, keep the previous state

This corresponds to the "GND Wins" idea in ratioed NMOS logic and fills in the gap left by relying only on `FindPathsToGnd`.

A more complete evaluator therefore needs:

- `FindPathsToGnd`
- `FindPathsToHigh` / `FindPathsToVcc`
- `current_state` as the answer for floating nodes

That allows the evaluator to model three distinct cases:

- discharge
- pull-up or recharge
- state retention

## 14. Build a Tiny EDA Compiler First

The original material strongly suggests that the project should not start by attacking the full 2A03.

The better entry point is:

- start from a NAND gate or another very small NMOS test netlist

That makes it possible to verify:

- whether the parser is correct
- whether the graph is correct
- whether conduction analysis is correct
- whether Boolean extraction is correct
- whether code generation is correct

This is essentially the "Hello World" stage of the EDA compiler pipeline, proving that JSON -> Graph -> Equation -> Codegen is working end to end.

## 15. The Graph as the Single Source of Truth

One of the most important architectural ideas in the original material is:

> The in-memory graph should be the single source of truth.

This means:

- whether you emit Verilog, CUDA, or a CPU evaluator
- none of them should reinterpret the raw netlist independently
- all of them should start from the same graph or intermediate representation

This is very much in line with modern compiler design, such as LLVM or Yosys.

Once the graph and IR are stable:

- the Verilog emitter is just one backend
- the CUDA emitter is just one backend
- the C++ / CPU evaluator is just another backend

## 16. Emit Verilog and CUDA

In the original flow, the graph can be sent in two main directions:

### 1. Verilog

The goal is:

- to preserve the result in a hardware-description form
- and potentially feed it into an FPGA or synthesis-oriented flow later

### 2. CUDA / C++

The goal is:

- to let GPUs execute the logic at large scale in parallel
- or to let a regular CPU compiler validate the model first

This means the project is not tied to a single destination. It naturally supports:

- hardware-oriented validation
- software-oriented accelerated simulation

## 17. Can a CPU Run This Before the GPU Exists

The original material also addresses a very practical question first:

- before using GPU acceleration, can a regular CPU handle this?

The answer is yes, with levels:

- direct switch-level execution is slower, but still possible
- once translated into Boolean logic, CPU execution becomes very suitable

So:

- there is no need to move up to RTL first just to run on a CPU
- the sweet spot between physical fidelity and execution speed is often switch-level translated into gate-level / Boolean logic

This again implies:

- the first verification backend can be an ordinary CPU
- GPU does not need to be the first step

## 18. Why Cycle Accuracy Is Achievable

The original material clearly states that both of these approaches:

- converting the netlist into an FPGA-like model
- converting it into Boolean logic executed directly by the GPU

can be cycle-accurate, as long as the switch-level network is correctly translated into:

- combinational logic
- sequential logic

Cycle accuracy does not mean reproducing every analog detail. It means:

- at each clock boundary or phase boundary
- all registers, state nodes, and external pins match the real hardware state

That is the theoretical foundation of the whole route.

## 19. Clock Driver and Two-Phase Clocking

Once the model becomes executable, the chip has to actually "beat."

The key points from the original material are:

- 6502 / 2A03 uses a non-overlapping two-phase clock
- a host loop or GPU-internal loop must alternate the clock phases

So the execution system needs:

- `current_state`
- `next_state`
- evaluation per half-cycle
- state-buffer swapping between phases

In other words, the generated logic is not just a collection of equations. It has to live inside a real clock-driven simulation loop.

## 20. Relaxation and Repeated Evaluation

The original material mentions several times that:

- in order to stabilize signals under a given phase
- the core evaluation may have to run several times within one half-cycle

This can be understood as:

- some node updates require several rounds of propagation and stabilization
- a single pass is therefore not always sufficient

So the practical execution structure can be:

1. set the current clock phase
2. run node evaluation several times
3. stop when the phase has stabilized

This is an important practical technique for turning switch-level logic into an executable simulator.

## 21. Integrating the Memory Bus

Once the CPU core can advance under internal clocking, the next step is to face the outside world:

- ROM
- RAM
- data bus
- address bus
- `R/W`

The original material states that the external bus should be integrated according to the active clock phase. The general flow is:

- read the address bus when the address is stable
- inspect `R/W`
- on reads, drive data from external memory back onto the data bus
- on writes, capture the data bus value and write it into RAM or mapper logic

So the internal node equations are not enough by themselves. A memory-controller layer is needed to connect the evaluator to external ROM and RAM behavior.

## 22. Translating the Entire Chip into CUDA

After parser construction, graph analysis, loop detection, Boolean extraction, pull-up integration, clock driving, and bus integration are in place, the original material moves into its most ambitious stage:

- automatically generating the full CUDA core from C#

The general form is:

- each node emits one or more `next_state[...] = ...` expressions
- all node updates are gathered into one large device-side function
- a host loop or GPU kernel drives it according to clock phases

This means the real GPU simulation core is not hand-written in the traditional sense. It is compiled from the analysis pipeline.

## 23. Why Bit-Parallel Execution Matters

The original material then introduces a crucial GPU optimization idea:

- do not simulate one instance per scalar `bool`
- instead use bitwise parallel execution

That means:

- each bit in a `uint32_t` represents one simulation instance for the same node
- use `&`, `|`, and `~` instead of `&&`, `||`, and `!`

The effect is:

- one logic expression can evaluate 32 instances at once
- the model maps much better to GPU throughput
- memory usage drops significantly

This is why the C# extractor is eventually upgraded into a bitwise CUDA code generator.

## 24. AoS vs SoA

Once bit-parallel execution is in play, memory layout becomes important.

The original material points out that:

- storing all node states contiguously per emulator instance is intuitive but GPU-unfriendly
- a better layout is SoA, grouping the same node across many instances together

In other words:

- `state[node][instance]`

instead of:

- `state[instance][node]`

This gives a warp much better memory coalescing when it updates the same node across many parallel instances.

## 25. The Idea of Simulating FPGA-Like Logic on the GPU

The original material also explores whether it makes sense to first convert the switch-level network into something closer to FPGA logic, and then simulate that FPGA-style logic on the GPU.

The conclusion is that the idea is conceptually valid because:

- FPGA is fundamentally built around one-way LUTs and flip-flops
- once the switch-level network has been translated into one-way logic, the GPU can simulate it efficiently

At the same time, the material stresses that:

- the real magic is still not in the FPGA layer itself
- the real magic is the logic extraction step before that

So the essential route is:

- convert the messy bidirectional switch network into structured one-way logic
- choose the execution platform after that

## 26. GPU Simulation of NES from a Throughput Perspective

The original material is very clear about using GPUs for NES simulation:

- raw arithmetic throughput is not the bottleneck
- the real bottlenecks are memory layout, branching, clock synchronization, and CPU/GPU interaction

The GPU is most valuable when:

- many NES instances are simulated simultaneously
- the system is used as an AI training platform or validation engine

So once node-update equations are cleanly extracted, the GPU's value is:

- running thousands or tens of thousands of instances at once
- not merely making a single game instance "fast"

## 27. The Fully Encapsulated GPU Architecture

The later original material proposes an even more advanced final direction:

- put ROM, RAM, CPU, PPU, APU, and mapper logic all inside the GPU

This is the so-called "fully encapsulated silicon fortress" model:

- each thread or execution unit represents a fully autonomous NES
- the ROM image lives in VRAM
- RAM, mapper state, and node state all remain on the GPU
- clock advancement, node evaluation, and bus handshaking all happen inside the GPU
- the host only provides inputs and reads back outputs

The advantage is:

- CPU/GPU round-trips are almost eliminated
- throughput for very large simulation batches becomes dramatically higher

So if the end goal is AI training, large-scale simulation, or Golden Model validation, this is a very compelling final architecture.

## 28. PPU: The Final Boss

Once the CPU pipeline works, the final large direction in the original material is:

- apply the same method to the 2C02 PPU

The PPU is more complex than the CPU because it includes:

- many shift registers
- sprite evaluation
- graphics data flow
- more complicated timing and bus interaction

But in principle, if parser, graph, loop detection, Boolean extraction, PullType handling, and bitwise code generation all work, then:

- the PPU can follow the same translation route

That is why the final height of the original project flow is not merely translating the CPU into CUDA, but translating the full core NES chips into GPU-executable logic.

## 29. Recommended Implementation Order

When the entire document set is reorganized into a single path, the most natural sequence becomes:

1. understand Visual6502 / Visual2A03 / Visual2C02 abstraction levels
2. write a parser for `segdefs.js` / `transdefs.js`
3. build `NetNode`, `NetTransistor`, and `NetlistGraph`
4. preserve PullType and important pin information
5. use DFS / connectivity analysis to find floating nodes and islands
6. use loop detection / SCC analysis to cut feedback
7. implement the Boolean extractor
8. integrate PullType and `current_state` into the extraction rules
9. validate the full pipeline on a tiny NAND / inverter netlist
10. emit C++ or a CPU evaluator first
11. emit an initial CUDA backend
12. upgrade the logic into a bitwise extractor
13. reorganize memory into SoA layout
14. build the clock driver and memory bus
15. implement 2A03 first
16. extend to 2C02 afterward
17. finally move into the fully encapsulated GPU architecture

## 30. The Real Value of This Project

The value of this route is not merely building a "fast emulator." It is building a new level of capability:

- infer logic intent from real chip topology
- generate executable models directly from hardware netlists
- share one graph-analysis core across CPU, CUDA, and Verilog
- bring silicon-level structures into a world of analysis, verification, and large-scale parallel execution

If that translation chain works, then:

- Golden Models
- GPU large-batch simulation
- FPGA-oriented output
- advanced PPU validation

all become different applications of the same toolchain.

## Closing

The complete `GPU方案` document set describes not one isolated trick, but a coherent strategy:

- use Visual6502-style netlists as raw material
- build graphs and analyzers in C#
- understand the circuit through DFS, loop detection, and Boolean extraction
- convert the switch-level world into executable logic and state updates
- emit CPU, CUDA, and Verilog backends
- and ultimately reach a fully encapsulated, high-throughput GPU simulation core

If followed in this order, the project grows naturally from data cleaning to cycle accuracy, GPU acceleration, and eventually complete chip-level NES simulation, forming a bridge from silicon-level connectivity to modern parallel computing platforms.
