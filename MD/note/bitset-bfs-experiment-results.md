# Bit-parallel BFS 實驗結果與 lessons learned

> 實驗 branch:`bitset-bfs-experiment`(from `aot-codegen` cd6e434)
> 日期:2026-05-25
> 結論:**Day 3 dense linear scan = 156× SLOWER 比 scalar BFS**。 演算法正確(bit-identical checksums),但效能反向確認 bit-parallel BFS 在這 workload 不可行。

## TL;DR

跑 Gemini r5 推薦的「hybrid scalar + Ligra-dense-scan」── infrastructure(Day 1-2)overhead 4.4%,但實際算法(Day 3)**156× 慢於 scalar baseline**。 失敗的根本原因:**workload distribution 不對** ── 99% PPU walk 只有 1-2 個 node,16,258 transistor 的 dense scan 對小 walk 是純粹的 overhead。

## Day-by-day 結果

### Day 1:ActiveTransistors bitset(plumbing)

實作:
- `ulong[] ActiveTransistors`(~430 u64 = 3.4 KB),`SetNodeState` hook 維護
- `TransistorGateNode / C1 / C2`(per-tid flat 陣列,for Day 3 dense scan)
- `NodeGateTidsList`(per-node「我當 gate 的 tids」list,length-prefixed)
- Verify path(`--verify-active-trans`)

Bench(full_palette.nes,50K hc,3 runs avg):
- baseline: 37,795 hc/s
- bitset-bfs: 36,141 hc/s
- **overhead 4.4%**(比 Gemini 預估 <2% 略高)

踩坑:**tid=0 是合法 transistor ID 不能用 0 當 sentinel**(跟 TransistorList 用 node ID 不同 ── node 0 是保留 sentinel,但 transistor IDs 從 0 dense pack)。 改用 length-prefix 後解決。 verify 10K hc 0 mismatch。

Commit:`3733b36`。

### Day 2:PPU subset infrastructure

實作:
- PPU node renumber(local idx 0..8742)
- `PpuTransistorIds[16,258]`(PPU-internal transistors,c1+c2 都在 PPU 或 supply)
- bitset 3 條(frontier / visited / next),137 u64 each = 1.1 KB each

Stats(post-lowering):
- PPU nodes: 8,743(59.4% of 14,723)
- PPU transistors: 16,258(60.5% of 26,775)
- chip-diag: PPU 對應 73.2% 全 BFS group-member work

對齊 ✓。 0 perf impact(pure plumbing)。 Commit:`b1c4378`。

### Day 3:Ligra-dense BFS over PPU subgraph(ALGORITHM)

實作(`ComputeNodeGroupDense`):
1. PPU 起始 walk dispatch 到 dense path
2. BFS expansion: 掃 16,258 PPU transistor / pass,bit-parallel frontier prop
3. visited bitset extraction via `BitOperations.TrailingZeroCount`
4. scalar flag accumulation + capacitance tie-break
5. cross-chip escape detection → fallback scalar(0.3% walks)

Bench(2K hc):
- baseline: **11,904 hc/s**
- dense: **76 hc/s**
- **156× SLOWER**

Bit-identical ✓(checksum `0x13AC672AF6ECC796`)── 演算法正確,但效能災難。

## 為何 156× 慢

### 量化分析

per 2K hc bench:
- scalar walks: 1.09M PPU walks,avg 1.4 node/walk(超小!)
- scalar work: 1.39M node-visits × ~3 pointer-chases ≈ **4M ops**

- dense walks: 1.09M PPU walks,每 walk 需 BFS passes ~3(small walks settle 快但仍走若干 pass)
- dense work: 1.09M walks × 3 passes × 16,258 trans = **53 BILLION transistor 掃瞄**

ratio:53B / 4M ≈ **13,000× 更多 work**。 inner scan 比 pointer-chase 便宜約 100×(SIMD-friendly linear),最終實際 156× 慢符合預期。

### 為何 Gemini r5 的策略沒救

Gemini 提的 threshold 策略(group > 16 才切 dense):

對 *中等* walk(300 nodes,scalar ~1k ops):
- dense:5 passes × 16,258 = 81k ops
- 還是 **80× 更慢**

dense 要贏 scalar 必須 walk 大到 frontier 1000+ nodes / pass。 8743 PPU nodes 全部 walk 才可能 ── 但這是極罕見的「整顆 PPU 同時 dirty」case,實測 0 次。

### 根本問題:workload distribution

| Walk size | 比例 | dense vs scalar |
|---|---|---|
| 1-2 node | 99%+ | dense 慘輸(掃 16K 找 1 個) |
| 5-50 node | <1% | dense 還是輸(~10× 慢) |
| 100-500 node | rare | dense 接近 break-even |
| 1000+ node | 從沒觀察到 | dense 才會贏 |

Ligra dense path 設計給「巨型 graph + frontier 佔幾十 % 的 BFS」── 我們的 PPU graph 是小型(8K nodes)且 frontier 通常 < 0.1%。

## Lessons learned

1. **演算法 RoI 要看 workload 分布,不是 graph 大小**
   - "PPU 有 8743 nodes 跟 16K transistors" 看起來夠大可以 bit-parallel
   - 但實際 walk 平均只 1.4 nodes ── bit-parallel 開銷 > scalar 開銷
   - 之前 `--prune-merge` 也踩這個坑(skip 沒省到主要 cost)
   - 之前 per-chip parallel 也踩(sync overhead > per-wave work)

2. **CPU 上 bit-parallel BFS 的天花板**
   - Gemini 自己就提:「CPU 沒硬體做 Node-adjacency matrix-vector 乘法」
   - Frontier expansion 真正的 SIMD friendly path 在 GPU(萬條 lane 同時掃)
   - CPU 的 256-bit AVX-512 也只能 8× parallel,扣 cache miss 開銷,實際 < 4× ── 還是輸給 pointer-chase 的常數 factor

3. **sentinel 設計教訓**
   - TransistorList 用 0 作 terminator OK,因為 node 0 是保留(EmptyNode sentinel)
   - 但 transistor IDs dense pack 從 0 起,**不能用 0 當 terminator**
   - 一般原則:用 sentinel 前確認該值在 domain 中不會出現

4. **PPU 之外的 path 仍可能有救**
   - Cross-chip walks 只 0.3%,dense path 直接 fallback 沒損失
   - 但 dense path 本身就慢,fallback 也救不了
   - 真正能救的可能是「dispatch 個 IR / 跳過 BFS」── 已經是 S2/S4 codegen 的領域,跟 BFS 本身無關

5. **chip-id 分類 + 子集化 infrastructure 還是有用**
   - 雖然 dense 沒贏,Day 2 的 PpuNodeList / PpuTransistorIds 子集化可能對其他實驗有用
   - 例如 future:per-chip IR codegen,只 emit PPU 區的 code,bench against scalar PPU walks
   - 留在 branch 不刪

## 跟 Gemini r5 預測對照

Gemini 預測「2-4× speedup」「最差 -10%」。 實際 -**99.4%**(156× 慢)── Gemini 嚴重高估 CPU 上 dense scan 的勝面。 Gemini 的盲點:
- 沒考量 NES PPU walk 的小 size 分布
- 過度信任 Ligra-dense 在小 graph 上的表現(Ligra 是給 億級 edge graph 用的)

不過 Gemini 提的 Q5 踩雷分析(setup overhead、capacitance tie-break、prune-merge 衝突)都正確 ── 是 plan 的真正價值。

## 後續方向

不繼續 bit-parallel BFS。 但 branch 保留作 reference:
- `WireCore.BitsetBfs.cs` ── infrastructure code
- 此 doc + `bitset-bfs-experiment-plan.md` ── 完整思考脈絡
- 不 merge 到 main

未來如果要加速 PPU BFS,可能的路徑:
1. **PPU-specific IR codegen**(S2/S4 已嘗試,S4 結論 batch 路徑 3-6× 慢於 S1)
2. **Event-driven 縮小 walk-size**(math-algos branch 已試,Phase 2.5 失敗)
3. **GPU 真實 bit-parallel**(S4 GPU 已完成,功能正確,但批次模式 PPU walk 開銷大)
4. **接受 ~47K hc/s 是 CPU 上 PPU switch-level sim 的上限**(已寫進 memory `s4-route-single-instance.md`)

## Day-by-day commits

- `f2f2cab` plan doc + Gemini r5 consultation
- `3733b36` Day 1: ActiveTransistors bitset + maintenance(4.4% overhead)
- `b1c4378` Day 2: PPU subset infrastructure
- `<this commit>` Day 3: dense BFS algorithm + results doc(156× slower)
