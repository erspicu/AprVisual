# GPU Project README

## What This Is

This project direction is not about building a conventional software-level NES emulator. It is about taking switch-level chip netlists such as Visual6502, Visual2A03, and Visual2C02, converting them into analyzable, verifiable, executable logic models, and eventually emitting CPU, CUDA, and Verilog-oriented backends.

The real focus is not the GPU itself. The real focus is the translation chain:

- raw netlist
- graph model
- dynamic-node and feedback identification
- combinational and sequential abstraction
- backend emission and validation

## Suitable Goals

This route is best suited for:

- Golden Model verification
- chip reverse engineering
- cycle-accurate research
- large-scale GPU batch simulation
- AI training environments

If the only goal is "play games," a traditional emulator is usually the better choice.

## Recommended Approach

The most stable order is:

1. Parse `segdefs.js` and `transdefs.js`
2. Build `Node`, `Transistor`, and `Graph`
3. Perform conduction analysis, floating-island analysis, and SCC / loop detection
4. Extract combinational logic and state-holding behavior
5. Build a CPU evaluator first
6. Then move to CUDA bit-slicing
7. Only after that, talk about PPU and the fully encapsulated in-GPU architecture

## The Most Important Technical Ideas

- switch-level logic is not an ordinary Boolean tree
- pass transistors are bidirectional
- some nodes float and retain state through capacitance
- pull-up behavior matters, but it does not simply mean `1`
- `FindPathsToGnd` alone is not enough; high-path and floating-island classification also matter
- `dynamic node = previous state` is an engineering abstraction, not the full physics

## Minimum MVP

The suggested MVP is:

- handle 2A03 only
- support NROM only
- validate only the first few fetch cycles after reset
- complete the CPU evaluator first
- then complete the CUDA evaluator
- finally compare them for equivalence

## Known Risks

- treating reset as `NOP` is only a bring-up stub
- a fixed 3-pass settle loop is a heuristic, not a guarantee
- a single giant GPU kernel is difficult to debug
- the PPU is much more complex than the CPU and should come later

## Documents

Full guides:

- [GPU_Project_Guide_Complete_zh.md](C:\Users\2026010501\Downloads\GPU方案-20260330T022628Z-3-001\md\GPU_Project_Guide_Complete_zh.md)
- [GPU_Project_Guide_Complete_en.md](C:\Users\2026010501\Downloads\GPU方案-20260330T022628Z-3-001\md\GPU_Project_Guide_Complete_en.md)

## Next Step

If implementation is about to begin, the best starting point is not CUDA. It is:

- tiny handcrafted netlist tests
- a CPU reference evaluator
- validation against a small real 2A03 region
