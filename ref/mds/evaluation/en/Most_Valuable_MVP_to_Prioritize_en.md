# Most Valuable MVP to Prioritize

## Core Principle

The most valuable MVP for this project is not "run tens of thousands of NES instances on the GPU." The most valuable MVP is a minimal but trustworthy switch-level logic extraction and verification core.

The value standard for the MVP should not be spectacle. It should be:

- does it validate the core assumptions
- does it prove the route is real
- does it create a stable base for later CPU / CUDA / PPU expansion

## Recommended MVP Target

The best MVP to prioritize is:

### A CPU Reference Evaluator for a Small Real 2A03 Subregion

The scope should be tightly controlled:

- handle 2A03 only, not 2C02
- validate only local subregions, not the full CPU
- do not add mapper complexity yet
- do not build the fully in-GPU architecture yet
- do not aim for the full NES system yet

This MVP should include at least:

1. a parser
2. a graph model
3. conduction analysis
4. floating / hold behavior
5. loop / feedback detection
6. settle-until-converged behavior
7. a CPU evaluator
8. trace output

## Why This Is the Most Valuable MVP

Because it directly validates the real soul of the project:

- can a switch-level netlist be turned into a trustworthy executable logic model

If this step does not work, then GPU acceleration, bit-slicing, PPU support, and fully encapsulated architectures have no real foundation.

If this step does work, the rest of the project becomes expansion and optimization rather than speculation.

## Suggested MVP Content

### Stage 1: Tiny Handcrafted Netlists

Start by building:

- inverter
- NAND
- NOR
- pass transistor
- dynamic latch

Use them to verify:

- the parser / graph / evaluator logic at a basic level

### Stage 2: A Real Local 2A03 Region

Pick one small but meaningful region from the real netlist, for example:

- the reset chain
- a register bit
- part of the ALU carry path

Validate:

- that real source data can be loaded
- that conduction / hold / settle behavior works
- that a stable trace can be produced

### Stage 3: CPU Evaluator and Simple Codegen

Once the local region can be evaluated:

- emit traces
- emit a small IR
- optionally emit simple C++ expressions

At that point, moving toward CUDA starts to make sense.

## MVP Completion Criteria

The MVP should be considered complete when it can:

1. load a real local 2A03 netlist region
2. build the graph
3. perform conduction and state update for a given phase
4. settle until convergence
5. emit trustworthy traces
6. cross-check handcrafted tests and real partial regions

Reaching that point is already highly valuable, and it allows a credible claim that:

- the route is not fantasy
- switch-level to executable logic has been demonstrated inside the project

## What Should Not Be the MVP

The following should not be treated as the first MVP:

- the full NES system
- the full PPU
- complex mappers
- the fully in-GPU architecture
- large-scale AI training throughput

Those belong after the MVP.

## Conclusion

The most valuable MVP to prioritize is not the biggest or the flashiest one. It is:

> a CPU reference evaluator that can perform parser + graph + conduction + hold + settle + trace over a real local 2A03 region.

If that MVP succeeds, the project finally has a genuinely reliable starting point.
