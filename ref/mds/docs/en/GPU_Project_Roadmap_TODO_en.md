# GPU Project TODO / Roadmap

## Phase 0: Data Preparation

- [ ] Inspect the field structure of `segdefs.js` and `transdefs.js`
- [ ] Define the parser output format
- [ ] Preserve pull-up / pull-down related markers
- [ ] Identify key pins: `clk0`, `res`, `rw`, `ab0-ab15`, `db0-db7`
- [ ] Build reusable cleaned cache files

## Phase 1: Graph Modeling

- [ ] Implement `NetNode`
- [ ] Implement `NetTransistor`
- [ ] Implement `NetlistGraph`
- [ ] Build gate/control adjacency
- [ ] Build conduction adjacency
- [ ] Confirm node/transistor counts match the source data

## Phase 2: CPU Reference Evaluator

- [ ] Define node-state storage
- [ ] Define transistor on/off rules
- [ ] Build the conduction graph for a single phase
- [ ] Detect connected-to-GND
- [ ] Detect connected-to-high
- [ ] Detect floating islands
- [ ] Add hold-previous-state behavior
- [ ] Add iterate-until-converged settling
- [ ] Set a maximum settle count and warnings

## Phase 3: Small Test Data

- [ ] inverter test
- [ ] NAND test
- [ ] NOR test
- [ ] pass-transistor test
- [ ] dynamic latch test
- [ ] shared-bus node test
- [ ] expected trace for each case

## Phase 4: Feedback and State Classification

- [ ] three-color DFS
- [ ] SCC / Tarjan
- [ ] collapse feedback groups into super nodes
- [ ] distinguish static latch / dynamic storage / bus loop
- [ ] validate that classification does not break conduction semantics

## Phase 5: IR and Logic Extraction

- [ ] define expression IR
- [ ] support `Const`
- [ ] support `NodeRef`
- [ ] support `Not`
- [ ] support `And`
- [ ] support `Or`
- [ ] support `Mux`
- [ ] support `Hold`
- [ ] implement basic expression simplification

## Phase 6: Output Backends

- [ ] emit debug JSON
- [ ] emit debug structural Verilog
- [ ] emit synthesis-oriented logic output
- [ ] emit CPU evaluator trace
- [ ] emit CUDA evaluator codegen

## Phase 7: CUDA MVP

- [ ] use SoA layout
- [ ] avoid AoS layout
- [ ] use `uint32_t` or `uint64_t` bit-slicing
- [ ] share IR between CPU and CUDA
- [ ] build batch evaluator first
- [ ] do not start with a fully resident GPU core
- [ ] validate CPU/CUDA node-by-node equivalence

## Phase 8: 2A03 Bus Integration

- [ ] implement reset-vector behavior
- [ ] stop using `NOP on reset` as the formal model
- [ ] integrate NROM PRG ROM
- [ ] integrate RAM mirroring
- [ ] validate the first fetch cycles
- [ ] validate `R/W` and data-bus direction

## Phase 9: Real Partial Validation

- [ ] validate the reset chain region
- [ ] validate a register-bit region
- [ ] validate an ALU carry-path region
- [ ] compare against reference models
- [ ] log non-converging cases

## Phase 10: PPU and Full System

- [ ] make sure the CPU pipeline is stable first
- [ ] then add the 2C02 parser
- [ ] validate CHR bus and shift-register behavior
- [ ] validate sprite evaluation
- [ ] only then evaluate the full in-GPU architecture

## Things Not to Do Too Early

- [ ] do not begin with 10,000 NES instances on the GPU
- [ ] do not begin with CPU/PPU/APU multi-thread partitioning
- [ ] do not begin with complex mappers
- [ ] do not reduce `+` to always `1`
- [ ] do not treat a fixed settle count as proof of correctness
