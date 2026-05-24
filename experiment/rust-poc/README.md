# wire_microbench — Rust vs C# language-perf PoC

Synthetic microbenchmark of the switch-level "settle to quiescence" hot loop, identical algorithm + identical RNG-seeded random netlist topology in both languages. The goal: isolate language & compiler effect (RyuJIT vs LLVM, alias analysis, autovectorization) on this exact workload, so we can estimate how much porting `WireCore.Recalc.cs` / `WireCore.Group.cs` to Rust would speed up.

**Scope:**
- ✅ The core algorithm: `AddNodeToGroup` / `ComputeNodeGroup` / `SetNodeState` / `ProcessQueue` / `Enqueue`
- ✅ Identical data layout (flat `int[]` TransistorList, packed `NodeInfo` struct, byte arrays for hash/state)
- ✅ Same RNG-generated synthetic netlist (~15k nodes / ~27k transistors, NES-scale)
- ✅ Same stimulus (random node toggle + drain), same iteration count
- ❌ NOT the full WireCore (no handlers, no CPU emulation, no fast-path / prune-merge optimizations)
- ❌ NOT a real ROM workload (the synthetic distribution may differ from a real NES chip's transistor patterns)

If Rust beats C# by 1.2-1.5× here, porting `WireCore` would likely give ~1.1-1.3× in practice. If Rust ties or barely wins, porting is not worth the engineering cost.

## Run

```powershell
# Rust
cd experiment/rust-poc
cargo run --release -- 15164 27305 100000 42

# C#
cd csharp_microbench
dotnet run -c Release -- 15164 27305 100000 42
```

Args:
- `N_NODES` — node count (default 15164, matches NES live-node count)
- `N_TRANSISTORS` — transistor count (default 27305, matches NES)
- `N_ITERS` — bench iterations after warmup (default 100000)
- `SEED` — LCG seed for netlist + stimulus (default 42)

Both print the same checksum if they produced identical NodeStates at end (cross-language identity check).

## What the bench does

1. Build synthetic netlist via seeded LCG (~75% normal-node channels, ~12.5% to GND, ~12.5% to VCC, ~25% nodes get a PullUp flag).
2. Initial settle: enqueue all non-supply nodes, drain queue.
3. Warmup: 1000 iterations of (pick random node + toggle + enqueue + drain).
4. Time `N_ITERS` of the same loop.
5. FNV-1a checksum of final NodeStates → must match between languages.

## Caveats

- Synthetic topology doesn't have NES-specific structures (no 6T cell loops, no D-FF chains, no obvious bus aggregation). So absolute hc/s isn't comparable to the real `bench-hc 50K`, only the RATIO between languages is meaningful.
- Both bench use recursive `AddNodeToGroup` — same risk profile, same stack behavior.
- Rust LTO + codegen-units=1 + opt-level=3 in `Cargo.toml`. C# uses TieredPGO + ServerGC. Both Release builds.
