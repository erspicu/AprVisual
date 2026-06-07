# Four switch-level simulators compared: perfect6502 / VisualNes / metalnes vs our S1 (2026-06-08)

*(English version of `2026-06-08-四個開關級模擬器比較.md`.)*

I took the three Visual6502-lineage switch-level simulators (perfect6502 / VisualNes / metalnes), actually **compiled them, measured hc/s, and diffed their algorithms**, then put them side by side with **our S1** for a four-way comparison — to see which strategies are worth learning and where each sits performance-wise. **All numbers are on the same machine, the same test ROM (`full_palette.nes`), and the same NES master-clock half-cycle unit** (perfect6502 excepted — it is 6502-only and its hc unit differs; see below).

## 0. TL;DR

| Project | Scope | Lang | hc/s (this machine) | Algorithm character | vs S1 |
|---|---|---|---|---|---|
| **VisualNes** (SourMesen) | whole NES | C++ | **~24K** | literal `chipsim.js` port: O(n²) `std::find` group dedup, `std::vector`+`shared_ptr`, **zero prunes** | 4.5× slower |
| **metalnes** (iaddis) | whole NES | C++ | **~54K** | **S1's direct ancestor**: flags-OR → 256-entry LUT, double-buffered ping-pong, single-sided turn-on, but **no prunes, std::vector** | 2.0× slower |
| **perfect6502** (mist64) | 6502 only | C | **~29K** (note) | heavily optimized chipsim: bitmap state, precomputed dependant CSR, single-sided turn-on | not directly comparable |
| **S1 (ours)** | whole NES | C# | **~108K** | metalnes algorithm + R-1 dynamic-singleton + P-1..P-4 prunes + SoA raw pointers + LUT micro-opts | — |
| (ours, Rust) | whole NES | Rust | ~101K (released) | bit-exact port of S1 | — |

> **The most valuable finding: a clean "optimization progress bar"** —
> **VisualNes 24K (unoptimized chipsim) → metalnes 54K (the better C++ ancestor) → S1 108K (our prunes + SoA).**
> metalnes → S1 is **about +100% (2.0×)**, exactly our R-1 + P-1..P-4 + SoA/native work (same algorithm, same scope, same unit, same machine, bit-exact); relative to the unoptimized chipsim (VisualNes) it's about **4.5×**. Nothing in any of them can be "lifted in to beat what we have"; instead all three independently validate that our lineage is sound and our optimizations genuinely lead.
> **S1 number = `--bench-hc 300000 --extra-ram` (the project's standard validation point), 3 runs ~107–108.5K (median 107.9K), checksum `0x794A43ABDF169ADA` = golden ⇒ bit-exact confirmed.** (The earlier 101K was a shorter 200k run without `--extra-ram` — less init amortization.)

Note: perfect6502's hc = one 6502-clock half-cycle, whereas ours/VisualNes/metalnes hc = one **NES master-clock** half-cycle (the 6502 core runs at master/12). **The unit differs by an order of magnitude, so 29K cannot be listed alongside the other three.** One NES frame ≈ 715K master-clock hc (all three whole-NES implementations agree on this).

## 0.5 Lineage tree and "is it a literal JS port?"

All four descend from Visual6502's `chipsim.js`, but their *fidelity to the JS* varies widely — one is transcribed line-for-line, one is an optimized rewrite, one is re-engineered. This is exactly what explains the performance gaps: **the same JS algorithm can differ several-fold purely from how it's written; the only line that improved the *algorithm itself* (LUT → prunes) is metalnes → S1.**

```
Visual6502  chipsim.js  (JavaScript, MIT) ── common ancestor
├── Visual 2A03 / 2C02 (JS, Quietust) ── chipsim.js applied to the two NES chips
│      └── VisualNes (C++/C#) ── literal port of those two JS sims, wired into one NES
├── perfect6502 (C) ── an "optimized rewrite" of chipsim.js, 6502 only
└── metalnes (C++) ── re-engineered descendant (LUT/handlers/modules/analog), whole NES
        └── AprVisual S1 (C#) ── port of metalnes + our optimizations
                └── rust-s1 (Rust) ── bit-exact port of S1
```

| Project | Relationship to the JS | Evidence |
|---|---|---|
| **VisualNes** | **literal port** of chipsim.js | `chipsim.cpp` carries the visual6502 authors' copyright; the algorithm keeps the JS inefficiencies (O(n²) `find` dedup); `macros.cpp` still has the JS `eval(readTriggers[a])`, `nodenames['sync']` left in as comments — the trace of translating straight off the JS |
| **perfect6502** | same source, **optimized rewrite** (not literal) | README: "derived from the JavaScript visual6502 implementation"; but it's fully rewritten in C with bitmap state + precomputed dependant CSR + duplicate removal |
| **metalnes** | same source as visual6502, **re-engineered** (not a port) | adds what chipsim.js doesn't have: 256-LUT group resolve (distinguishes driven-high vs hold-previous), behavioral handlers, a module system, analog composite-video / audio ladders |
| **S1 (ours)** | C# port of metalnes + our optimizations | `SetNodeState`/`ProcessQueueInterp`/`_tlist_gates` layout = metalnes' pre-prune prototype; then layered with R-1 + P-1..P-4 + SoA |

**Key contrast:** VisualNes and perfect6502 both descend straight from chipsim.js — one "transcribed verbatim (24K, whole NES)", the other "rewritten/optimized (29K, but 6502 only)"; the same JS algorithm, several-fold apart purely on implementation. The metalnes → S1 line is the only one that actually improved the *algorithm itself* (LUT group resolution → event-count prunes), pulling away to 108K.

---

## 1. perfect6502 (mist64) — 6502-only, C, heavily optimized

- **Build**: Windows + clang (no make/gcc); the only fix was un-commenting the disabled `#include <windows.h>` in `cbmbasic/readdir.c` (done in a temp copy; `ref/` untouched).
- **Measure**: `--benchmark` runs cbmbasic to the `READY.` prompt = 33,155 hc / ~1.14 s = **~29K hc/s** (6502-only, 1,725 nodes / 3,239 transistors).
- **Strategies worth learning** (we already have all of them, or stronger):
  - **Precomputed dependant CSR + rising/falling split**: at load time, for each node, build a deduplicated "on rising edge enqueue one endpoint / on falling edge enqueue both endpoints" node list; the hot path iterates a node list instead of walking the transistor array. → **S1's `SetNodeState` already does single-sided turn-on / double-sided turn-off**; perfect6502 stores it as a flat CSR (more cache-friendly), but our P-1 same-state prune needs the (c1,c2) pairing, which flattening would lose — and our prune removes more enqueues.
  - **Bitmap 1-bit node state** (uint64-packed): 8× denser, cache-friendly for the group walk. But our flags/prunes need a byte, and our earlier bit-parallel/bitset experiments were dead-ends → not worth it.
  - **Reversed-duplicate transistor removal** ((g,A,B)≡(g,B,A)): we only dedup (g,c1,c2) + supply normalization, so reversed non-supply pairs survive — the one small gap, but the netlist is auto-generated and the count is expected to be near-zero; low priority.
  - It has **no** hold-previous / capacitive float resolution (fully-floating → low); fine for a bare 6502, mandatory for us modeling the whole NES.
- Details: `MD/note/2026-06-08-perfect6502-比較.md`.

## 2. VisualNes (SourMesen) — whole NES, C++, an unoptimized chipsim.js port

- **Build**: the flat `Core/*.cpp` plus a self-written headless driver (parse iNES → load prg/chr → power-on reset → soft reset to re-fetch the vector → time `step` N) compiled directly with clang++ `-std=c++17`. **Clean to build** (Core has no GUI entanglement).
- **Measure**: `full_palette.nes`, **~24K hc/s** (median of 5, whole NES, same unit as ours).
- **Algorithm** (`chipsim.cpp`): a **verbatim C++ transcription of Visual6502's `chipsim.js`**:
  - `addNodeToGroup` dedups with `std::find(group...)` — **linear O(group²) scan** (we/perfect6502 use an O(1) bitmap/flag).
  - `std::vector` + `shared_ptr` churned on the hot path.
  - `getNodeValue`: when hasGnd&&hasPwr it uses a **hard-coded node-id list** as ForceCompute (359, 566, …); floating groups resolve by **largest area (capacitance) wins** — same lineage as us (it has this; perfect6502 doesn't).
  - **No enqueue-time pruning at all** (P-1..P-4).
- **Worth learning**: honestly nothing — it's a "correctness reference" tier; everything it has, we have and faster. Its value is as the **unoptimized-chipsim baseline**: whole NES at 24K, which is exactly what highlights our ~4.5× margin.

## 3. metalnes (iaddis) — whole NES, C++, **S1's direct ancestor**

- **What it is**: `MD/note` has long recorded that S1 is "a headless C# port of metalnes' `wire_compute`." Reading the source confirms it: metalnes' `setNodeState` / `processQueue` / `computeNodeGroup` / `_node_infos[]._tlist_gates` + 0-terminated `(c1,c2)` `_transistor_list` layout **= S1's `SetNodeState`/`ProcessQueueInterp` prototype before the prunes were added**; S1's `RecalcListNext`/`RecalcHashNext` are literal ports of its `_recalc_list_next`/`_recalc_hash_next`.
- **Build (the main effort here)**: metalnes is an OSX-only Apple **Metal** + imgui + audio GUI app (its own README says "Needs lots of optimization"). With the user's authorization to modify metalnes code, I extracted the wire core **headless** in a temp copy:
  - Stubbed `imgui_support.h` (tiny fake ImGui), `triangulate.h` (no-op `BuildTriangleList`), `raster_device.h`/`audio_device.h` (type + `saveImage`/`saveAudio` only, **devices passed as nullptr**; handler_nes_system guards with `if(device)`, so no real implementation is needed).
  - Neutered `handler_nes_system::serialize_fields` (avoids serializing the stub devices).
  - Windows compat: `-DWIN32` (the source uses `WIN32`, not `_WIN32`) + force-included `compat_win.h` (`strcasecmp→_stricmp`, `mkdir→_mkdir`, `timegm→_mkgmtime`, `localtime_r→localtime_s`), fixed `File.cpp`'s two-arg `_mkdir` bug; `-DCATCH_CONFIG_DISABLE` to drop the embedded Catch2 tests; stubbed `SetCurrentThreadName`.
  - Driver: follows `system_state::Create`'s recipe — load `nes-001` + `cart-mmu0` (+ prgrom/chrrom), `setupRom`, attach `handler_rom`/`handler_ram`/`handler_nes_system`, reset, time `wires->step(N)`. **Skips the video_out/audio_out analog-output handlers** (those are the parts needing a raster/audio device), so this measures "engine + logic + RAM/ROM/bus", on par with S1's headless step; the full app would be a bit slower (analog ladders + rendering + imgui).
- **Measure**: `full_palette.nes`, **~54K hc/s** (whole NES, same unit). One frame ≈ 715K hc, consistent with S1's "101K → 7.07 s/frame" → same unit, genuinely running (steady ~18 µs/hc; if stalled it would be orders of magnitude faster).
  - **Re-checked at our standard 200,000 hc (answering the "surely not that fast" doubt): single 200k = 55.8K; extended to 12×200k = 2.4M hc as a rate curve, it stays at 52–56K throughout**, including chunks 7–11 (hc > 1.42M, past full_palette's 2× vblank-wait boot, into the main loop / actual rendering) — so 55K is **not** a boot-transient artifact; the steady-state ~54K is real.
  - **Important caveat**: this number is the "engine + logic + RAM/ROM/bus" **core**; I **skipped `video_out`/`audio_out` analog ladders, and there is no GUI/imgui/render loop**. The full metalnes desktop app (analog composite video + audio + rendering) is **slower** than this — that's the "metalnes is slow" people usually mean. For comparison against S1 this is the **fair** number, since S1's benchmark also doesn't compute analog output (it just steps + checksums NodeStates), and both ran the same light ROM.
- **Strategies worth learning**: it *is* the source of S1's algorithm, so **there's nothing new to learn — we took it all and went further**. What it demonstrates:
  1. Our 256-LUT group resolution (distinguishing "driven high" vs "hold previous"), the weak/depletion flag, the largest-capacitance float tie-break, behavioral RAM/ROM handlers, the module (.js) system, ForceCompute — all originate from metalnes; it's the most complete digital model in this lineage.
  2. **metalnes 54K → S1 108K = ~2.0×**, which is exactly what we layered on top of the ancestor: **R-1 dynamic-singleton fast-path + P-1..P-4 event-count prunes + SoA raw pointers + LUT micro-opts**. A clean third-party reference for our optimization value.
  3. metalnes also models **analog composite video + audio voltage ladders** (perfect6502/VisualNes/we don't) — that's simulation depth, not performance, and beyond our "digital logic + behavioral memory" scope.

---

## 4. Uses for the website / README

Can feed the lineage / comparison narrative:
- Three lineage-mates measured into a progress bar **24K (VisualNes, unoptimized chipsim) < 54K (metalnes, our ancestor) < 108K (S1, ours)**, objectively quantifying our **2.0×** over the direct ancestor and **~4.5×** over the unoptimized chipsim.
- perfect6502 (29K), being 6502-only with a different hc unit, **cannot be listed alongside the whole-NES numbers**; place it as "an optimization reference for the (6502-only) branch."

## 5. Methodology (reproducible)

- Same machine / ROM / unit; each warmed up (past boot transients) before timing; median of 5.
- Build copies live in `temp/{p6502_build,vnes_build,mn_build}/` (gitignored); original clones in `ref/{perfect6502,VisualNes,metalnes-main}/` (gitignored, read-only; metalnes was edited only in the copy, to make it headless).
- S1 number: the released `AprVisualBenchMark/win/csharp/AprVisual.S1.exe` (version fb97342).
  - `--bench-hc 300000 --extra-ram` (standard validation point) = **~108K hc/s** (median 107.9K, 3 runs 107.0–108.5K), checksum `0x794A43ABDF169ADA` = golden ⇒ bit-exact.
  - `--bench-hc 200000` (without --extra-ram) = 101.1K (shorter run, less amortization).
