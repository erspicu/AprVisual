You are a .NET 10 / CoreCLR performance expert. I want a PRECISE, non-generic answer about aggressive optimization KNOBS (build flags, runtime env vars, runtimeconfig.json properties, csproj properties, [MethodImpl] attributes) — NOT algorithm or code-structure changes. An honest "you are already at the optimal config, no free win here, because X" is a fully acceptable and preferred answer over hopeful generic advice. Be specific with exact flag/property names and say what each would do FOR THIS workload.

## The app
- net10.0, x64, single-file-capable console app. Single-threaded throughout.
- It is a switch-level (transistor-level) NES CPU/PPU simulator. The entire runtime is spent in ONE settle loop: a method `ProcessQueueInterp` (~4.6 KB of machine code) into which everything is inlined; it runs ~83,000 times/sec, each call doing a BFS over a tiny "conducting group" of nodes.

## Measured profile (hardware PMU, PerfView/ETW on Ryzen 7 3700X, Zen2)
- **Memory-latency bound.** In the hot method: D-cache misses : I-cache misses ≈ **805 : 1**. ~1 D-cache miss per ~20 CPU cycles. I-cache misses are at the noise floor (the 4.6 KB hot loop is fully L1i-resident).
- NOT i-cache bound, NOT branch-misprediction bound (branch:D-miss ≈ 1:8), NOT compute/ALU bound, NOT SIMD-able (it's scalar, data-dependent random pointer-chase).
- The misses are RANDOM access over unmanaged arrays: `NodeInfos` (~230 KB), `TransistorList` (~225 KB), `NodeStates`, indexed by scattered node ids. Working set spills L2 (512 KB).
- Software prefetch (Sse.Prefetch0, distances 1 and 8) measured NEGATIVE (−1% to −5%): the HW out-of-order engine already overlaps the independent misses (MLP saturated). 

## Current config (what we already do)
- Tiered JIT + Dynamic PGO (both default-on in .NET 10) — we rely on these; the benchmark warms up before the timed window.
- `<Optimize>true`, `<InvariantGlobalization>true`, x64, `AllowUnsafeBlocks`.
- Hot data is UNMANAGED (NativeMemory.AlignedAlloc) — zero allocation / zero GC in the timed loop (managed heap is freed + a GC barrier runs before timing). Hot pointer code uses raw `byte*`/`ushort*`/`int*` with no bounds checks. `[MethodImpl(AggressiveInlining)]` on the hot methods.
- Self-contained multi-file publish (not single-file, not trimmed-affecting-perf).

## Already TESTED and REJECTED (do NOT re-propose)
- **NativeAOT**: −5.5% (it drops dynamic PGO; the dynamic-PGO'd tiered JIT is faster for this steady-state loop).
- Single-file / trimmed publish: no perf effect (expected).
- ReadyToRun: not used; for a steady-state hot loop the tiered JIT re-JITs the hot method to fully-optimized + PGO anyway, so R2R only helps startup (irrelevant to a long benchmark).

## The question
Given a STEADY-STATE, SINGLE-THREADED, MEMORY-LATENCY-BOUND hot loop over UNMANAGED arrays on .NET 10, are there any aggressive optimization PARAMETERS that could plausibly help? For EACH, say whether it helps/neutral/hurts for THIS specific profile and why. Cover at least:
1. `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` on the hot method (skip tiering → full opt from start, but does it lose dynamic PGO for that method? net effect here?).
2. `DOTNET_TieredCompilation=0` / `DOTNET_TC_QuickJitForLoops` / `DOTNET_TieredPGO` tuning — any benefit over the default tiered+PGO for a single hot method?
3. `DOTNET_TC_CallCountThreshold` / `CallCountingDelayMs` (tier-up sooner) — relevant when the loop runs for many seconds?
4. **Large pages / huge pages** for the unmanaged arrays to cut TLB misses (the working set is ~700 KB+ randomly accessed). The arrays are NativeMemory (NOT GC heap) so `DOTNET_GCLargePages` won't cover them — is there a .NET-level or Win32 (VirtualAlloc MEM_LARGE_PAGES) way, and is TLB pressure even plausibly significant at this footprint?
5. GC mode (`<ServerGarbageCollection>`, `<ConcurrentGarbageCollection>`, DATAS) — does it matter when the hot loop does zero managed allocation?
6. Any NEW .NET 10 JIT/codegen knob (e.g. new loop-opt, OSR, instruction-set targeting `DOTNET_EnableXXX` / `<IlcInstructionSet>`, `DOTNET_JitEnableXXX`) specifically relevant to a scalar memory-latency-bound loop.
7. Static PGO (collect-and-embed) vs the dynamic PGO we already have — worth it for one hot method?

Rank anything plausibly positive; be blunt about which are placebo/noise for a memory-latency-bound workload. If the honest answer is "the dynamic-PGO tiered JIT is already the optimal .NET config and the wall is DRAM latency, not the compiler," say so plainly.
