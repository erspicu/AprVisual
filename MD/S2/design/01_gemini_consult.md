# S2 設計諮詢 —— Gemini 原文記錄(參考用)

> **這是外部 LLM 諮詢的原始記錄,僅供參考。** 我(Claude)對它的過濾、修正與最終
> 排序 list 見 [`02_architecture_candidates.md`](02_architecture_candidates.md)。
> 使用者交代:Gemini 只當參考,不可照單全收(它有「聽起來很好但忽略我們 settle 語意」
> 的傾向 —— 見 02 的分歧標註)。

## 諮詢設定

- **模型**:`gemini-3.1-pro-preview`(目前 config 最新)
- **日期**:2026-05-31
- **完整 log**:`tools/knowledgebase/message/20260531_224604.txt`(含我送出的完整 prompt)
- **我送出的 prompt 要點**(完整見上述 log):
  - 餵入 S1 完整架構(15K 節點 / 27K pass-transistor、SoA 佈局、事件驅動 BFS group-resolve、
    256-entry LUT、ushort TransistorList、cold 陣列分離、floating tie-break = connection 數)。
  - 餵入**動態行為事實**:平均走訪 1.4 節點、~70% recalc 是 singleton、94% 雙向 SCC 結構牆。
  - 餵入**已證實 dead-ends**(IR/codegen 3–6×、GPU 156×、threads 15×、levelize、RCM、
    prune/merge 破 bit-exact、counter-fastpath −6%、SIMD-queue)——明令不要重提。
  - 要求:單核、cache-conscious、保持事件驅動、保持 bit-exact;每條給 (a) i/d-cache 機制
    (b) 如何保持 bit-exact (c) 誠實 ROI 區間 (d) 風險;最後依 ROI 排序、列出最先做的 2–3 項。

---

## Gemini 回覆原文(verbatim)

> This is exactly the kind of constrained, bare-metal optimization problem where standard
> "high-level" computer science answers fail and mechanical sympathy wins. You have a highly
> sparse, event-driven simulation bottlenecked by the memory hierarchy, not ALU.
>
> Given your constraints (single-core, bit-exact, 1.4 nodes/walk, 70% singletons), the entire
> performance game is about **eliminating dependent pointer chases** and **maximizing
> cache-line density** for the L1/L2 data cache.

### Part 1: Evaluation of Your Proposed Topics

**1. Inlining the SMALL fan-out directly into the node record**
- **Mechanism:** L1/L2 d-cache spatial locality. Eliminates a dependent cache-line fetch.
- **Exactness:** Perfectly exact. Purely a data-layout transformation.
- **ROI Band:** **Structural (>1.15x).** This is your biggest missing win. Currently, your
  singleton fast-path must fetch `NodeInfo`, read an index, and perform a dependent pointer
  chase into `TransistorList` just to check if the gates are OFF. This guarantees an L2 memory
  stall (~10–15 cycles). By packing the connections into a unified struct, the CPU gets
  everything in a single 64B cache-line fetch.
- **Risk/Failure mode:** Bloating the struct size. If you push the struct to 64 bytes, you
  halve your cache-line density. **Do not exceed 32 bytes.**

**2. Node renumbering by DYNAMIC co-activation**
- **Mechanism:** L2 d-cache spatial locality. RCM failed because it clustered based on
  *structural* topology (including the 90% of transistors that are OFF). Dynamic co-activation
  clusters based on *empirical runtime* topology (nodes that are actually ON together).
- **Exactness:** Exact. Node IDs are arbitrary internal indices; you just remap I/O pins.
- **ROI Band:** **Plausible 2–8%.** The theoretical ceiling is low because 70% of walks are
  singletons (length = 0). However, for the 30% of walks that *do* traverse, pulling the
  "other" node into the same or adjacent 64B cache line turns an L2 hit into an L1 hit.
- **Risk/Failure mode:** "Overfitting" to a specific ROM. Fortunately, NES CPU/PPU transistor
  toggling is highly cycle-deterministic regardless of game state.

**3. Compact BYTECODE for the ~6% feed-forward fringe**
- **Mechanism:** i-cache density and branch reduction.
- **Exactness:** Exact, assuming tie-breakers are baked into the emitted byte-code.
- **ROI Band:** **Likely Noise (<2%).** Amdahl's Law is brutal here. If the fringe is 6% of
  the work, infinitely speeding it up yields max 6%. Worse, it requires adding an
  `if (is_bytecode) run_vm() else run_bfs()` branch in your hottest queue-dispatch loop,
  causing branch prediction strain and polluting the L1 i-cache by forcing it to host two
  distinct engines.
- **Risk/Failure mode:** Destroying the i-cache efficiency of the remaining 94% SCC.
  **Reject this idea.**

**4. Software prefetch of the next queue entry**
- **Mechanism:** d-cache latency hiding via `__builtin_prefetch` / `_mm_prefetch`.
- **Exactness:** Exact.
- **ROI Band:** **Noise to slightly negative (-2% to +2%).** Prefetching only works if the
  compute loop has enough ILP and duration to overlap the memory latency. Your average walk is
  1.4 nodes—the loop is too fast (dozens of cycles). Prefetching `Queue[i+1]` will arrive too
  late. Lookahead prefetching `Queue[i+4]` *might* yield a tiny gain but usually just clogs CPU
  execution ports and evicts useful L1d lines.
- **Risk/Failure mode:** Execution port saturation. Skip it until everything else is done.

**5. Keeping dispatch/settle loop small for i-cache**
- **Mechanism:** L1 i-cache hit rate. The core walker should comfortably fit in a 32KB L1i.
- **ROI Band:** **Plausible 2–5%.**
- **What bloats it:** Inlining the heavy machinery. The main loop should ONLY contain the queue
  pop, the singleton fast-path, and the small-walk BFS. You must force the compiler to outline
  (`NoInlining`) the code for: (1) traversing overflow transistor arrays for massive fan-out
  nodes (VCC/GND); (2) resolving supply-anchored groups; (3) the floating-group capacitance
  tie-breaker logic.

**6. FastSPICE / Event-driven cache-oblivious techniques**
- **The "Epoch Timestamping" Deduplication:** If nodes A and B are in the same active group,
  evaluating A settles B. If B was *also* in the recalc queue due to a simultaneous gate flip,
  popping B later in the same half-cycle redundantly triggers the fast-path. Keep a dense
  bit-vector that you zero out every half-cycle. Mark nodes as you visit them. If you pop a node
  and its bit is set, `continue`. **ROI: Plausible 5-10%.**
- **The "Don't touch" Rule:** Do **NOT** merge `NodeStates` into the `NodeInfo` struct. Keep
  `NodeStates` as a separate, dense 15KB `byte[]`. Why? 15KB permanently locks into the L1
  d-cache. The singleton fast-path relies entirely on checking gate states; by keeping them
  separate, you guarantee that gate checks are strictly L1 hits, even while `NodeInfo` structs
  trigger L2 hits.

### Part 2: The Ranked Prototype Plan

**Rank 1: The 32-Byte "SoA-in-AoS" Node Struct (The Silver Bullet)**
Redesign your data layout so that >85% of nodes require exactly **one cache-line touch**.

```c
// Aligns perfectly to 32 bytes. 2 nodes per 64B cache line.
struct Node {
    uint8_t flags;
    uint8_t count;
    uint16_t overflow_idx; // Points to cold TransistorList if count > 7
    uint16_t gates[7];     // Contiguous for fast-path unrolling
    uint16_t others[7];    // Contiguous for BFS traversal
};
```
For your 70% singletons, the fast path becomes (state[] accesses guaranteed L1d hits):
```c
Node* n = &nodes[curr_id]; // 1 L2 Cache miss (32 bytes fetched)
bool any_on = false;
for(int i = 0; i < n->count; ++i) {
    if (states[n->gates[i]]) { any_on = true; break; }
}
if (!any_on) { /* direct resolve singleton */ }
```
This removes the `TransistorList` array entirely from the hot path.

**Rank 2: Epoch Deduplication (Bit-Vector)** — allocate ~15,000 bits, memset 0 each half-cycle;
when a node is popped, if its bit is set skip it, else evaluate and set bits for all nodes in its
resolved group.

**Rank 3: Dynamic Co-Activation Node Reordering** — profile S1 (run SMB 10 frames), count ON
edges, greedy heavy-edge matching → new node-ID map, hardcoded at lowering (zero runtime cost).

### Summary (Gemini)
> If you implement the **32-Byte Inline Struct**, outline your cold paths to **protect L1
> i-cache**, and retain your **dense 15KB state array**, you will mathematically strip out one
> L2 cache miss per node evaluation. At ~77K hc/s, that latency is currently your absolute
> ceiling. Fixing it will yield a structural, non-noise leap over S1.

---

*我的批判性過濾與最終 list → [`02_architecture_candidates.md`](02_architecture_candidates.md)。*
