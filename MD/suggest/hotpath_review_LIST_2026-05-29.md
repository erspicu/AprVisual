# Hotpath 優化預備項目 LIST — 2026-05-29

來源整合:
- `netlist_non_ir_optimization_review_20260529.md`(本批主來源)
- `Sim_hotpath_followup_suggestions_2026-05-29.md`(C# 跟進)
- `RustS1_hotpath_suggestions_2026-05-29.md`(Rust 跟進)
- 既有 dead-end 紀錄(`MEMORY.md`)

**基準(2026-05-29 sequential, 5-run median, 200k hc, full_palette)**:
- C# S1: **62,977 hc/s** (peak 64,675)
- Rust S1: **67,517 hc/s** (peak 68,206)
- 兩端 checksum 對等 `0x9B103E5E206E4C37`,距 NES NTSC real-time (42.95M hc/s) ~640-682×

**驗證紀律**:
- 每項變動先在 C# 端 5-run median + checksum match,再 sync 到 Rust 同樣 5-run
- 若實測落在 ±1.5% 雜訊範圍內,標為「雜訊級」並 revert
- 若實測為負,記入專屬 `*-dead-end.md` memory 並 revert(避免重蹈覆轍)
- 進入熱路徑的所有改動,**必須引用至少一個既有 memory 來說明「為什麼這次不會撞同樣 dead-end」**

---

## P0 — 低風險、最先做(預期 sub-2% / 項)

### `#G1` ReadBits branchless gather
- **位置**:C# `WireCore.Handlers.cs:ReadBits(int[])` + `ReadBits(IReadOnlyList<int>)`;Rust `wire.rs:read_bits`
- **改動**:`if (state != 0) v |= 1 << i;` → `v |= state << i;`(C#)/ `v |= (state as u32) << i;`(Rust)
- **前置不變式**:`NodeStates[i] ∈ {0, 1}`(已由 `FlagsToState` 保證)
- **頻率**:每次 RAM/ROM handler 觸發 + 每次 pclk1 rising edge 寫 pixel
- **預估**:0.3-0.8%(memory handler 很熱,pixel write 較冷)
- **風險**:**極低**。 是純粹的「removing well-predicted branch with predictable replacement」── 已驗證的常用 idiom
- **memory 引用**:不撞任何 dead-end ── 移除的是 predictable branch + 同樣 memory access pattern
- **狀態**:☑ **採用** (2026-05-29)
  - C# baseline top-3 mean: 63,305 hc/s
  - C# #G1 top-3 mean:    63,489 hc/s
  - Δ: **+0.29%** (落在預估 0.3-0.8% 下緣,符合預期)
  - checksum 5/5 `0x9B103E5E206E4C37`
  - Rust 端待後續批次處理

### `#G2` SetNodeState loop unswitch
- **位置**:C# `WireCore.Recalc.cs:SetNodeState`;Rust `wire.rs:set_node_state`
- **改動**:`newState` 為 0/1,在 fanout loop 內判斷是 redundant ── 拆兩 loop
- **頻率**:每次 SetNodeState 內所有 tlist_gates(per-transistor)── ~10-50M times/200k hc
- **預估**:1-2%
- **風險**:低,但比 `#G1` 高(代碼複雜度增加)。 注意 LLVM/JIT 可能本來就部分 hoist 了 ── 需先看 hot path 反組譯
- **memory 引用**:**注意** ── `counter-fastpath-dead-end` 顯示「在 SetNodeState 內每 transistor 加 1-2 cmp 可能整體變慢」。 但 loop unswitch 是「減少」每 iter 工作量,方向相反 ── 預期符合
- **狀態**:☑ **採用** (2026-05-29)
  - C# #G1 (prior) top-3 mean: 63,489 hc/s
  - C# #G2 top-3 mean:         64,169 hc/s
  - Δ: **+1.07%** (落在預估 1-2% 內,符合預期)
  - checksum 5/5 `0x9B103E5E206E4C37`
  - Rust 端待後續批次處理

### `#G3` RecalcNodeFast: GND/PWR OR-all
- **位置**:C# `WireCore.FastPath.cs:RecalcNodeFast`;Rust `wire.rs:recalc_node_fast`
- **改動**:`while (*p) { if (state[*p++]) { f |= Gnd; break; } }` → OR-all + `f |= (NodeFlags)(anyGnd << 5)`
- **前置條件**:list 平均長度 ≤ 3-5 才有利(已知 pure-logic-gnd 多為短 list)
- **頻率**:每次 fast-path RecalcNode(C# fast-path 覆蓋 23% live nodes)── ~3-10M times
- **預估**:0.5-1.5%
- **風險**:低,但只對 fast-path 套用。 **不要套到 `AddNodeToGroup` 內的 GND/PWR 掃描** ── BFS 路徑的 list 較長,早 break 仍有價值
- **memory 引用**:不撞 dead-end(範圍限於 fast-path 短 list)
- **狀態**:☒ **退回** (2026-05-29)
  - C# #G2 (prior) top-3 mean: 64,169 hc/s
  - C# #G3 top-3 mean:         62,197 hc/s
  - Δ: **-3.07% (REGRESSION)**
  - checksum 5/5 `0x9B103E5E206E4C37`(correctness ok)
  - **root cause 推測**:JIT 對原 early-break loop 已最佳化好(可能展開或預測分支),強制 OR-all 反而:
    1. 移除了 early-break 對「first ON gate」的提早終止
    2. fast-path lists 不如假設那麼短,完整掃描成本不可忽略
    3. 或 JIT 對 byte-shift + cast 序列生成的代碼比原版差
  - 加入 dead-end 教訓:**「OR-all 比 early-break 快」這個 textbook 假設,在已被 JIT 最佳化的短 list 上不成立**
  - Rust 端 **暫不嘗試**(C# 結果為負,需先 instrumentation 量 list 長度分布再決定)

### `#G4` group_flags OR 移到 pop 時做
- **位置**:C# `WireCore.Group.cs:AddNodeOrApplyDriver`
- **改動**:目前 `_groupFlags |= ns.Flags` 在 enqueue 時讀 `NodeInfos[nn].Flags`;同一 nn 在 pop loop 又讀一次。 移到 pop 時做可省一次 NodeHot read
- **頻率**:每次 BFS visit(~30-60M times/200k hc)
- **預估**:0.5-1%
- **風險**:低 ── 純資料流順序調整,語意等價
- **memory 引用**:不撞 dead-end
- **狀態**:☒ **退回** (2026-05-29)
  - C# #G2 (prior) top-3 mean: 64,169 hc/s
  - C# #G4 top-3 mean:         62,678 hc/s
  - Δ: **-2.32% (REGRESSION)**
  - checksum 5/5 `0x9B103E5E206E4C37`(correctness ok)
  - **root cause 推測**:原本 enqueue + pop 兩次讀 NodeInfos 形成「無意間的 prefetch」效果 ── enqueue 時 warm cache,pop 時命中。 移除後 pop 變成 cold miss
  - 加入 dead-end 教訓:**「省一次讀」不一定等於提速 ── 重複讀有時是 prefetch 效益**。 同一 cache line 重複 access 在 modern CPU 上幾乎免費,而拉開 enqueue/pop 的時間差讓 cache 被其他 BFS visit 沖走
  - Rust 端 **暫不嘗試**(C# 結果為負)

### `#G5` SetNodeState 重排:c1 supply check
- **位置**:C# 已在 `#04 af619f4` 做過(在 hot path 內);Rust 等價優化
- **改動**:C# 已 inline。 Rust 端確認是否同步 ── 看 wire.rs:set_node_state 的 `if c1 != npwr && c1 != ngnd`
- **預估**:0.5-1.5%(Rust 端視 LLVM 是否已自行最佳化)
- **狀態**:☐ Rust 端需驗證 / 套用

---

## P1 — 中風險、需嚴格 A/B(預期 1-3% 或負)

### `#H1` Branchless enqueue (XOR-shielded)
- **位置**:C# `EnqueueNode` + `SetNodeState` 內的 enqueue;Rust `enqueue` + `set_node_state`
- **改動**:`if (hash == 0) { list[count++] = n; hash = 1; }` → `isNew = hash ^ 1; list[count] = n; hash = 1; count += isNew;`
- **前置條件**:
  - `RecalcList` / `RecalcListNext` 配置 `NodeCount + 1`(branchless 寫不前進時仍寫 list[count])
  - `RecalcHash[Npwr] = RecalcHash[Ngnd] = 1` 永久持有(both buffers!)以省 supply check
  - hash 嚴格 0/1,不可有其他值
- **預估**:0.5% ~ -3%(高度不確定)
- **風險**:**中高** ── 原 `if (hash == 0)` 是高頻 **well-predicted** branch
  - 撞 `counter-fastpath-dead-end` memory 的失敗模式:移除 well-predicted branch、增加 unconditional memory write,常輸給 predictor
  - 但這裡不像 counter-fastpath 有 maintenance overhead ── 只是換 branch 為 conditional move + add
- **memory 引用**:**`counter-fastpath-dead-end`** ── 必須先 microbenchmark hash dirty hit rate(若 >90% hit, branchless 應贏;<50%, branchless 大概率輸)
- **狀態**:☒ **不實作** (2026-05-29,based on instrumentation 結果)
  - **Hash hit-rate 量測** (200k hc, full_palette, 套到 EnqueueNode + SetNodeState 內 enqueue):
    - 總 enqueue attempts: **129,799,078**
    - Clean (hash=0 → write):  **125,291,978 (96.5%)**
    - Dirty (hash=1 → skip):     **4,507,100 (3.5%)**
  - **分析**:96.5% 一邊倒 → branch predictor 飽和,預測準確率 ~99%
  - **理論 cycle 模型**(以 body ~3.5 cycle + branch ~0.5 cycle 估算):
    - Branchful avg: 0.965 × 4.0 + 0.035 × 0.5 = **3.88 cycle/iter**
    - Branchless avg: ~3.5 cycle/iter
    - 差值 ~0.38 cycle/iter,折 200k hc 約 **+0.4% bench**(極微)
  - **決定**:不實作 ── 微弱預期收益 + 撞 `counter-fastpath-dead-end` 的「高 predicted branch 不要動」教訓 + 需動 NodeCount+1 capacity 與 supply shield 兩處基礎設施
  - 若日後 D 變化或 ROM 不同(例如 SMB 大量 IO)hit rate 大幅變動,可重測再評估
  - **校準 LIST 內 hit-rate 規則**:原文寫「>90% hit, branchless 應贏」用詞不準。 正確應為「>90% branch UN-predictable → branchless 贏;>90% PREDICTABLE(無論方向)→ branchless 輸或打平」

### `#H2` byte→u16 generation counter for `_inGroup`
- **位置**:C# `WireCore.Group.cs`;Rust `wire.rs:add_node_to_group`
- **改動**:不每 group 結束清 `_inGroup`,用 `_inGroup[nn] == currentGen` 判斷
- **重要**:**counter 必須 u16 (≥ 65K gap) 或 u32**,不可用 byte
  - 理由:D ≈ 50-150 nodes/hc → byte 256 值約 ~2-5 hc 就 rollover,反而頻繁全清
  - 文件原稿說 byte 是錯的
- **頻率**:每次 `compute_node_group` 開頭的清 0 loop(目前每次 group walk 都跑)── 省掉這個 loop
- **預估**:0.5-2%(視 group 大小)
- **風險**:中。 generation rollover 還是要正確處理(`if (gen == MAX) { 全清 _inGroup; gen = 1; }`)
- **memory 引用**:不撞 dead-end
- **狀態**:☒ **退回** (2026-05-29)
  - C# baseline (post-#G2) top-3 mean: 64,169 hc/s
  - C# #H2 top-3 mean:                 61,645 hc/s
  - Δ: **-3.93% (REGRESSION)**
  - checksum 5/5 `0x9B103E5E206E4C37`(correctness ok)
  - **root cause**:**L1d cache budget 撞牆**
    - `_inGroup` byte→ushort 從 14KB 翻到 29KB
    - 加 NodeStates 14KB → 43KB,超過 32KB L1d 限制
    - BFS dedup 變 L1 miss-bound,clear loop 省的工作量遠少於 cache miss 拖累
  - 加入 dead-end 教訓:**L1 cache footprint 是硬限制 ── 任何「擴大熱資料 size」的提案都要先算 L1d 預算**
  - 不嘗試 u32(更糟,4×=58KB)
  - **可能的補救方案**(暫不做,需新提案):保留 byte _inGroup + 加一個 byte generation 域,gen 用 byte 滾動 + 對 group 跑完後標記用過的 entries,只清那些 → 但這比現有方案複雜且收益不明

### `#H3` Rust raw pointer iteration
- **位置**:Rust `wire.rs` 多處 `get_unchecked(i++)` loop
- **改動**:`get_unchecked(i)` → `p = p.add(1)` raw pointer
- **預估**:0% ~ 2%(LLVM 通常已自動最佳化,但 stride 一致時 raw pointer 可能更穩定產 SIB addressing)
- **風險**:中(unsafe 範圍擴大,debug build 不友善)
- **memory 引用**:Rust S1 已用 `get_unchecked` 拿 +3.52%(`5566dfd`),raw pointer 若沒進步就保持 `get_unchecked`
- **狀態**:☐ 待測(僅 Rust)

### `#H4` Rust AlignedVec for hot arrays
- **位置**:Rust `wire.rs:from_snapshot` ── `node_states`, `recalc_hash`, `recalc_hash_next`, `in_group`, `group_buf`, `transistor_list`
- **改動**:用自建 `AlignedVec<T>` 確保 allocation base 64-byte aligned
- **重要警告(原文件正確)**:**不要** `#[repr(align(64))]` 在 element 上 ── 那會讓 stride 膨脹 4×
- **預估**:0.5-1.5%
- **風險**:中(API 改動,maintain cost 增加)
- **memory 引用**:C# 端已用 `NativeMemory.AlignedAlloc(64)` ── Rust 同步合理
- **狀態**:☐ 待測

---

## P2 — 高風險、長期(撞既有 dead-end 機率高,需充分理由再做)

### `#I1` Clock-phase partitioning
- **問題**:NES 有 `clk` / `cpu.clk0` / `ppu.clk0` / `ppu.pclk1` 多 clock domain,單一相位切分不可行
- **如要做**:per-domain 分析 → snapshot 加 per-node clock domain tag → BFS 依當前活躍 domain skip 整段 edge
- **預估**:5-15% 若成功,但 engineering effort 巨大
- **memory 引用**:**`s4-route-single-instance`** ── 整段 batch eval 已嘗試過(AOT codegen,3-6× SLOWER)。 clock-phase 是 batch 的子集,需證明子集為何不撞同樣牆
- **狀態**:☐ **暫不做**(投入產出比差,待 P0/P1 全做完再評估)

### `#I2` Hypergraph cache-line renumber
- **問題**:math-algos 試過 RCM,負效益
- **memory 引用**:**`rust-port-best-config`** ── RCM 是 -3-4%。 Hypergraph 變體要說明「為什麼這次不撞同樣牆」── 文件沒提
- **狀態**:☐ **暫不做**(需先有量化的「BFS gate read 跨 cache line 比例」資料,證明問題真存在)

### `#I3` Long-list bitset mirror
- **問題**:過去 bit-parallel BFS 156× slower
- **如要做**:必須先量「TlistC1gnd/C1pwr length ≥ 16 的 list 佔總 list count 的比例」── 若 <5% 不值得
- **memory 引用**:**`bitset-bfs-dead-end`**
- **狀態**:☐ **暫不做**(先量分布)

### `#I4` Software prefetch
- **適用**:長 `TlistC1c2s` (length > 8)
- **預估**:-1% ~ +1%(modern OoO 通常自動 prefetch)
- **狀態**:☐ 暫不做(優先度低)

---

## 不建議消除的 branch(文件原列正確)

- `ProcessQueue` 內 `if (RecalcHash[nn] != 0)` ── 擋掉昂貴的 RecalcNode 再算
- callback / memory handler 的 `cs` guard ── chip-select inactive 是常態,early return 便宜
- pixel 邊界 check ── 應從 callback 觸發頻率下手,不是 branchless

---

## 建議測試順序(2026-05-29 排程)

| # | 項目 | 端 | 預期 Δ | 預期風險 | 工序 |
|---|---|---|---|---|---|
| 1 | `#G1` ReadBits branchless | C# + Rust | +0.3-0.8% | 低 | 半天 |
| 2 | `#G2` SetNodeState loop unswitch | C# 先 | +1-2% | 低 | 半天 |
| 3 | `#G4` group_flags OR 移到 pop | C# 先 | +0.5-1% | 低 | 半天 |
| 4 | `#G3` RecalcNodeFast OR-all | C# + Rust | +0.5-1.5% | 低 | 半天 |
| 5 | `#G5` Rust c1 supply check | Rust only | +0.5-1.5% | 低 | 1-2 小時(diff vs C# 確認) |
| 6 | `#H2` u16 generation counter | C# 先 | +0.5-2% | 中 | 1 天 |
| 7 | `#H1` branchless enqueue + supply shield | C# 先 | -3% ~ +1% | **中高** | 1 天 + 微基準 hit rate |
| 8 | `#H4` Rust AlignedVec | Rust only | +0.5-1.5% | 中 | 半天 |
| 9 | `#H3` Rust raw pointer iter | Rust only | 0 ~ +2% | 中 | 半天 |

**全部跑完預期**:C# +3-7%、Rust +1-5%(`#H1` 若為負則不計)

---

## A/B 驗證 checklist(每項都要)

```
[ ] C# 5-run sequential median + min + max
[ ] Rust 5-run sequential median + min + max
[ ] checksum 5/5 match baseline `0x9B103E5E206E4C37`
[ ] selftest ALL PASS (C# only)
[ ] PNG md5 match(若改動可能影響 video path)
[ ] fixed half-cycle count: 200,000 hc
[ ] same ROM: full_palette.nes / snapshot/full_palette.aprsnap
[ ] same lowering setting (default on)
[ ] If negative → revert + log in MD/suggest/ as dead-end
[ ] If positive → commit + push 並更新 hotpath_review_LIST 該項狀態
```

---

## 引用的 memory(若改動方向相關,務必先看)

- `counter-fastpath-dead-end` ── well-predicted branch removal 的失敗範例
- `bitset-bfs-dead-end` ── walk-size distribution 沒看就動手的代價
- `rust-port-best-config` ── RCM 重排負效益記錄
- `dead-end-skip-dead-end` ── 「看起來無用」的 skip 常斷別人
- `per-chip-parallel-dead-end` ── 並行不是萬靈丹
- `jit-vs-llvm-recursive-inline` ── C# 改善不一定 sync 到 Rust 為正
- `s1-fork-results` ── 當前 best 基準與累積增益
