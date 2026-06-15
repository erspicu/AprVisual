# tools/cpp-ours — C++ port of AprVisual's OWN (optimized) algorithm

Two C++ ports of AprVisual's event-driven engine (**not** the naive group-walk — that's
`tools/cpp-naive`), both loading engine state exported by the C# tool (identical data) and
**validated bit-exact**:

- **`ours_full.cpp`** — the FULL engine (peak vs peak): the packed 16-byte NodeInfo struct, the
  cls1/cls2 fast-path + RecalcNodeFast, the B1 two-node pair path, the class-major range-prune, the
  double-buffered settle, and the permutation (original-id-order) checksum. `--export-engine-full`.
- **`ours.cpp`** — the architecture CORE only (SoA + event-driven settle + group LUT + floating
  tie-break + P-1..P-4 mask-prune), no micro-op stack. `--export-engine`. A stepping stone.

### Result (2026-06-15, same machine, NOP sled) — all bit-exact

| Chip | C# naive | C++ naive | **C++ ours-full** | C# ours (full) |
|------|---------:|----------:|------------------:|---------------:|
| 6502 | 17,914 | 26,239 | **118,918** | 151,561 |
| 6800 | 12,582 | 17,137 | **61,630**  | 89,924  |
| z80  | 12,255 | 18,452 | **40,255**  | 62,631  |

**The inversion.** At the *naive* algorithm native C++ is ~1.4–1.5× **faster** than C#; at the
*full* engine C# is ~1.27–1.56× **faster** than the faithful C++ port — because the C# hot path is
heavily micro-tuned for the .NET JIT + dynamic PGO (inline cascade, 64-bit dual-loads,
profile-ordered branches) and the clang `-O3` port replicates the algorithm + cache layout but not
that instruction-level tuning. So it's "heavily-tuned managed beats faithful-but-untuned native",
not a language ceiling. **Side-lesson:** the C++ port only caught up to C# once it used the *same*
packed 16-byte node struct (one cache line per 4 nodes) instead of split per-field arrays — the
cache layout is itself an optimization. Full write-up: `MD/note/2026-06-15-…前置研究.md` §8.9–8.10.

---

## `ours.cpp` (the CORE port)

`ours.cpp` loads the engine state exported by the C# tool, so the data is
provably identical and the port can be **validated bit-exact**.

```sh
# 1) export the built engine (post-lower, identity ids = --no-renumber, post-init)
dotnet run -c Release --project src/AprVisual.etc -- --export-engine tools/cpp-ours/6502.engine.txt --chip 6502
# 2) build + run
clang++ -O3 -std=c++17 tools/cpp-ours/ours.cpp -o tools/cpp-ours/ours.exe
tools/cpp-ours/ours.exe 6502 tools/cpp-ours/6502.engine.txt 1000000 50000 5
# 3) validate: its final checksum must equal
dotnet run -c Release --project src/AprVisual.etc -- --cpu-bench src/AprVisual.etc/netlists/6502 --chip 6502 --no-renumber --workload nop --warmup 50000 --bench-hc 1000000 --rounds 5
```

## Scope (honest)

Includes the architectural core. **Deliberately OMITS** the bit-exact-neutral micro-optimizations
(inline-payload node layout, the cls1/cls2 fast path, the B1 pair path, class-major range-prune,
self-captured relayout) — those are performance-only, so leaving them out keeps the result
identical and the port tractable. This is therefore *our algorithm's architecture in C++*, **not**
a peak-vs-peak micro-op race.

## Result (2026-06-15, this machine, NOP sled) — all bit-exact

| Chip | **C++ ours-core** | C# `--no-renumber` | C# ours (full micro-opts) | final checksum (C++ == C#) |
|------|------------------:|-------------------:|--------------------------:|----------------------------|
| 6502 | **69,319** | 110,160 | ~149,736 | `0x1948E581C9666553` ✓ |
| 6800 | **36,828** | 73,358  | ~88,313  | `0x2216CCB5330A83DB` ✓ |
| z80  | **25,603** | 49,775  | ~62,952  | `0x8F396CDADAD74F9C` ✓ |

**Read it carefully:** C++ ours-core is *slower* than C#, but **not because of the language** — the
C++ port simply lacks the micro-op stack. At the *same* optimization level (naive, see
`tools/cpp-naive`) C++ is ~1.4–1.5× *faster* than C#. So the engine's micro-optimizations contribute
more than the C#→C++ language change; a fully-tuned managed engine beats an architecture-only native
port. A clean peak-vs-peak language race would require porting the whole micro-op stack to C++
(project-scale, like the Rust engine). Full write-up: `MD/note/2026-06-15-…前置研究.md` §8.9.
