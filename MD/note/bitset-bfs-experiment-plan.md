# Bit-parallel BFS 實驗計畫(branch `bitset-bfs-experiment`)

> 日期:2026-05-25
> Branch:`bitset-bfs-experiment` (from `aot-codegen` commit `cd6e434`)
> 起源:user 提出 bit-parallel BFS / bitset traversal 作為 BFS 加速方向
> 諮詢:Gemini r5(`temp/gemini_bitset_bfs_response.md`)

## TL;DR

**純粹全域 bit-parallel BFS 在這 workload 不會贏** ── group size 兩極分布(50% 是 1-node,大多 1-5 node,只有 PPU 巨大 pass-transistor mesh),bitmap setup 跟掃描 overhead 會壓死小 group。

**真正能贏的策略**:hybrid scalar + Ligra-dense-scan
1. Scalar BFS 起頭(現有 code 路徑保留)
2. 偵測 group 超過閾值(~16 node)── 通常是進到 PPU 大 mesh
3. 切到 **Ligra dense mode**:掃描 PPU 區所有 transistor,branch-predictor 友善的 linear access

## 為何 bit-parallel BFS 看起來有效益

per chip-diag + settle-stats:
- PPU 71.2% of total group-member work(主要 cost)
- PPU 內部 94.5% pass-coupled SCC ── 一個 walk 進去可能展開幾百 node
- 這種大 walk pointer-chasing 每 node ~30ns,bitmap OR/AND 一個 u64 比 64 個 node-visit 快
- 14,730 node → ~230 u64 bitmap → 1.8 KB 塞進 L1d cache

但!
- 99% walks 是小 group(<10 node),bitmap 開銷 > 收益
- adjacency 是 dynamic(gate state dependent),不能 precompute 整張表
- group 解析不只是 connectivity,還有 flag accumulation + capacitance tie-break ── bitset 化不直接

## Gemini 提的具體 design(Q1-Q6 回應)

### Q1 dynamic adjacency

**Bipartite graph** approach。 不存 N×N adjacency(27 MB 塞爆 cache),改 maintain:
- `ActiveTransistors`: ~427 u64 bitset(每個 transistor 1 bit,表示「目前 conducting」)
- 當 node state flip,只 update 「該 node 當 gate 的 transistors」對應 bit。 SetNodeState 時 cost 極低(typical fanout 小)
- BFS expansion:`ActiveT` AND `IncidenceMask[frontier_node]` → conducting neighbors

### Q2 flag accumulation

**不 bit-parallel 化** ── 只 bit-parallel frontier expansion。
- BFS 結束得到 visited bitset
- `tzcnt`(trailing zero count)硬體指令掃出所有 bit position
- Scalar loop 對每個 visited node 讀 Flags + Connections,做傳統 group resolution + tie-break

理由:`_maxConnections / _maxState` 是 scalar integer compare,bit-parallel 化太醜。 conversion bitset→array 用 `tzcnt` 順序掃也 cache-locality 好。

### Q3 multi-source vs per-source

**Per-source + threshold bypass**。 絕對不要 bulk-multi-source ── 不同 dirty source 可能屬於不同 independent group(CPU 跟 PPU 同時 dirty),混 BFS 會造成錯誤 charge sharing。

策略:scalar 起頭,group 大於閾值才 bit-parallel。

### Q4 fast-path 23% nodes

**保留不動**。 fast-path O(1) ~5-10 cycle vs bit-parallel setup 100+ cycle ── 小 group bit-parallel 必死。

### Q5 踩雷的點

1. **Zeroing/iteration overhead**:14.7k node = 230 u64,如果每次走 3-node group 都 memset 230 u64 → 慢 pointer-chase 10×。 **防禦**:動態追蹤 min/max u64 chunk index,只清/掃 touched
2. **tzcnt 瓶頸**:C# / Rust 沒 inline 好的話,`while(mask){bit=tzcnt; mask &= mask-1;}` 自己 overhead 大
3. **Capacitance tie-break 盲點**:Floating-group 用 max-connections winner,bit-parallel 化會丟失 node identity → 必須走 scalar 結算
4. **跟 prune-merge 衝突**:如果有 super-node packing 過,bit-parallel 依賴 dense ID。 確認 lowering 後 ID 緊湊
5. **小 group 的 setup 開銷**:必須 threshold dispatch,不能無條件 bit-parallel

### Q6 MVI 3-day plan

#### Day 1:基礎建設 + `ActiveTransistors` 追蹤(無害改動)

1. 新增 field `ulong[] ActiveTransistors` ── `ceil(TransistorCount / 64)` 大小(NES ~27305 trans → ~427 u64 = 3.4 KB)
2. 修改 SetNodeState:當 `NodeStates[nn]` 0↔1 翻轉,update 所有 nn 當 gate 的 transistor bits
3. 需要新 data:per-node「我當 gate 的 transistor ID 列表」(現有 `TlistGates` 是 (c1, c2) pairs,沒 tid)。 加 parallel `TlistGateTids` 列表
4. **Verify**:跑 50K hc bench,assert `ActiveTransistors[tid] == (NodeStates[trans[tid].Gate] != 0)` 每 hc。 確認 100% 一致
5. **Measure**:overhead < 2% 為目標。 如果 > 5% 表示 fanout 太大或 implementation issue

#### Day 2:PPU 區 bit-parallel expansion

1. PPU 區 node renumber(or 直接用全 14k bitmap,~230 u64)
2. 新增 `process_queue_bitparallel` 路徑,當 walk size 超過閾值切過去
3. BFS expansion 用 ActiveTransistors + incidence lookup
4. Verify bit-identical NodeStates checksum

#### Day 3:Ligra-dense fallback + bench

1. 當 frontier 超過 PPU node 20% → 切 dense mode
2. Dense mode:**掃 ALL PPU transistors**(線性 memory access、branch-predictor 友善),per-trans check `ActiveT` + bit 操作 frontier
3. 這是真正的加速來源 ── PPU 大 walk 從 ~hundreds pointer-chase 變 ~thousands linear-scan u64 op
4. Threshold tuning + benchmarking
5. 收尾:visited bitset → scalar loop → flag accumulate + tie-break

## 預期效益

如果做對:**2-4× speedup** over current BFS(理論)。 實際扣 verify cost + threshold 邊界處理,**1.5-2×** 預估。

如果做差:overhead 比 pointer-chase 大,反而 -10%(踩中 Q5 雷區)。

## 跟之前 fail 的對比

| 嘗試 | 是否成功 | 為何 |
|---|---|---|
| `--prune-merge` (digital eq) | ❌ unsafe | cross-coupled cell stable state 模糊 |
| `--prune-merge` (topology) | safe but slow | sync check 比 digital eq 嚴格,skip 太少 |
| per-chip parallel | ❌ 15× 慢 | rayon sync overhead vs tiny per-wave work |
| LUT chip 74HC04/74LS368 | ✅ but ~0.6% gain | TTL 只占 0.6% work |
| LUT chip 74LS139 | ❌ render 黑 | callback 1-wave delay vs CPU bus timing |
| **bit-parallel BFS** | TBD | 屬正面 RoI 路徑(打中真正 cost = PPU 大 group) |

## 風險

- 工程成本估 14-20 hr(緊湊 3 day budget)
- 中途可能發現 group size distribution 不如預期(大 group 比例低 → bit-parallel 沒舞台)
- Bit-identical verify 是必須(否則 checksum 不對,前面 prune-merge 教訓)

## 開工順序

1. 寫這份 doc(完成)
2. Commit doc + Gemini r5 response 到 branch
3. **Day 1 開始**:add `ActiveTransistors` + maintenance + verify
4. 看 overhead 數字決定是否繼續

## 失敗也保留

「做爛了沒關係」── 即使最終沒拿到 speedup,negative result 本身有價值:
- 確認 bit-parallel 在這 workload 不可行的具體原因
- 量出真實 group size distribution
- 留下 Ligra-dense 嘗試 code 作 reference
