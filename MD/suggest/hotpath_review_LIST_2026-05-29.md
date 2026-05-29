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
- **狀態**:☑ **C# 採用** / ☒ **Rust 退回** (2026-05-29)
  - C# baseline top-3 mean: 63,305 hc/s
  - C# #G1 top-3 mean:    63,489 hc/s
  - C# Δ: **+0.29%** (落在預估 0.3-0.8% 下緣)
  - C# checksum 5/5 `0x9B103E5E206E4C37`
  - **Rust 端 sync 後**:
    - Rust baseline top-3 mean: 67,926 hc/s
    - Rust #G1 top-3 mean:    66,909 hc/s
    - Rust Δ: **-1.50% (REGRESSION)**
    - Rust checksum 5/5 `0x9B103E5E206E4C37`(correctness ok)
  - **Root cause(Rust)**:LLVM 對原 `if state != 0` 已產生近最佳碼;branchless 形式的 `state as u32` cast + shift 序列反而較差
  - 加入 dead-end 教訓:**#G1 是 C#-only 收益,不可 blind sync 到 Rust**(撞 `jit-vs-llvm-recursive-inline` memory pattern)

### `#G2` SetNodeState loop unswitch
- **位置**:C# `WireCore.Recalc.cs:SetNodeState`;Rust `wire.rs:set_node_state`
- **改動**:`newState` 為 0/1,在 fanout loop 內判斷是 redundant ── 拆兩 loop
- **頻率**:每次 SetNodeState 內所有 tlist_gates(per-transistor)── ~10-50M times/200k hc
- **預估**:1-2%
- **風險**:低,但比 `#G1` 高(代碼複雜度增加)。 注意 LLVM/JIT 可能本來就部分 hoist 了 ── 需先看 hot path 反組譯
- **memory 引用**:**注意** ── `counter-fastpath-dead-end` 顯示「在 SetNodeState 內每 transistor 加 1-2 cmp 可能整體變慢」。 但 loop unswitch 是「減少」每 iter 工作量,方向相反 ── 預期符合
- **狀態**:☑ **C# 採用** / ☑ **Rust 採用** (2026-05-29)
  - C# #G1 (prior) top-3 mean: 63,489 hc/s
  - C# #G2 top-3 mean:         64,169 hc/s
  - C# Δ: **+1.07%** (落在預估 1-2% 內)
  - C# checksum 5/5 `0x9B103E5E206E4C37`
  - **Rust 端 sync 後**:
    - Rust baseline (post-G1-revert) top-3 mean: 67,926 hc/s
    - Rust #G2 top-3 mean:                       68,389 hc/s
    - Rust Δ: **+0.68%**(低於 C# +1.07%,可能因 LLVM 已部分 unswitch 原 code)
    - Rust checksum 5/5 `0x9B103E5E206E4C37`

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

## P0-2 batch: Rust 全套無分支改造嘗試(2026-05-29 晚)

外部建議分 3 步走:Step 1 VCC/GND hash shield + Step 2 branchless enqueue/set_node_state + Step 3 branchless recalc_node_fast + iterative branchless add_node_to_group。 我分 Phase A/B/C 增量測試。

### Phase A:Step 1 + Step 2 ── **採用 +1.63%**
- baseline (post-G2) top-3 mean: 68,389 hc/s
- Phase A top-3 mean:            69,504 hc/s
- Δ: **+1.63%**,checksum 5/5 `0x9B103E5E206E4C37`
- Commit `da8dca4`
- **重要**:這跟 C# #H1 不實作的決策**相反方向**。 C# 端 instrumentation 顯示 96.5% clean rate,branch predictor 飽和,理論增益 0.4%。 但 Rust 上:
  - LLVM 對 conditional store 的 codegen 路徑與 .NET JIT 不同
  - shield 一次拔掉每 c2 enqueue 的 2 個額外 cmp(`c2 != npwr && c2 != ngnd`)
  - 綜合下來 +1.63% 真實
- 教訓:**Rust LLVM ≠ C# JIT,branchless 收益不可機械套用 C# 結論**

### Phase B:Step 3a recalc_node_fast OR-all ── **退回 -1.86%**
- Phase A baseline top-3 mean: 69,504 hc/s
- Phase B top-3 mean:          68,214 hc/s
- Δ: **-1.86%**,checksum 5/5 OK
- 跟 C# #G3 同樣失敗(-3.07%)── 證實 **LLVM 和 .NET JIT 都對 early-break + 短 list 最佳化得好**

### Phase C:Step 3b iterative branchless add_node_to_group ── **大退回 -19.18%**
- Phase A baseline top-3 mean: 69,504 hc/s
- Phase C top-3 mean:          56,171 hc/s
- Δ: **-19.18% (DISASTER)**,checksum 5/5 OK
- 失敗主因(複合):
  1. 改 iterative 本身在 Rust 是 **-1.3%**(memory `jit-vs-llvm-recursive-inline`)
  2. branchless `should_add` 無條件讀 `node_hot[other_u].flags`,即使 `should_add = 0` 也付出 NodeHot read 成本 ── **cache + dependency 雙重壓力**
  3. 無條件寫 `group_buf[count]` / `in_group[other_u]` / `recalc_hash[other_u]` ── store buffer 飽和
  4. `wrapping_neg` cast + mask 序列在熱路徑增加指令依賴鏈
- 教訓:**「在 BFS hot path 用 branchless mask 取代 if」在這架構是 dead-end**。 早期 visit / dedup 比例高,unconditional work 成本 > 預測 branch 成本。 撞 `counter-fastpath-dead-end` 同樣模式:**無條件 maintenance overhead 必須 < 預測 branch 節省的工作量**

### 累積結果

Rust 從 Phase A 帶 +1.63%,Phase B/C 全退回。 commit da8dca4 為最終狀態。

---

## C# 全套無分支改造嘗試(2026-05-29 晚)

外部建議三批(對應 Rust Phase A/B/C 的 C# 版本)。 C# baseline post-#G2 top-3 mean **64,342 hc/s**。

### Phase F:SetNodeState + shield + branchless enqueue ── **退回 -2.15%**
- baseline: 64,342 hc/s → Phase F: 62,957 hc/s → Δ -2.15%,checksum 5/5
- 對應 Rust Phase A(+1.63% 成功)── **方向反轉**
- C# JIT 對原 `if (hash == 0)` 已最佳化好(predict accuracy ~99% per #H1 量測)
- 與 #H1 預測一致(96.5% clean → 理論 +0.4%,實測 -2.15% 因為 unconditional write 的 store-buffer 成本)
- **教訓 (重要)**:**Rust LLVM ≠ C# JIT**,branchless 收益不可雙向移植
  - Rust 上 LLVM 對 if-form 的 codegen 較差 → branchless 救得到
  - C# 上 JIT 對 if-form 已飽和最佳化 → branchless 反而是 overhead
- 校準:**memory `counter-fastpath-dead-end` 的「高 predicted branch 別動」原則對 C# 仍適用**

### Phase D:RecalcNodeFast 完全無分支(OR-all + 拔外層 if) ── **退回 -2.60%**
- baseline: 64,342 hc/s → Phase D: 62,666 hc/s → Δ -2.60%,checksum 5/5
- 跟 #G3 (-3.07%) 同樣失敗;拔外層 `if (TlistC1gnd != 0)` 救不到(sentinel 0 的路徑 cost 與 cmp 等價)
- 確認:**RecalcNodeFast OR-all 在 C# JIT 和 Rust LLVM 兩端都輸**(短 list + 預期 break 早 → ALU OR 開銷壓不過早出)

### Phase E:AddNodeToGroup 完全無分支(shouldAdd mask) ── **退回 -37.34% 巨災**
- baseline: 64,342 hc/s → Phase E: 40,320 hc/s → **Δ -37.34%**,checksum 5/5
- 比 Rust Phase C (-19.18%) 還慘
- 複合失敗:
  1. 無條件讀 `NodeInfos[other].Flags` 即使 shouldAdd=0(cache + dependency)
  2. 無條件寫 `_inGroup` / `_groupBuf` / `RecalcHash`(store buffer 飽和)
  3. `-shouldAdd` cast → flagMask 序列引入指令依賴鏈
  4. JIT 比 LLVM 更不擅長 bit-twiddling 模式 → 災難擴大 2 倍
- **強化教訓**:**「在 BFS hot path 用 branchless mask 取代 if」確認為 C# 和 Rust 雙端 dead-end**

### 三 Phase 總結

C# 端外部建議的 3 種 branchless 改造**全部退回**。 與 Rust 結果交叉對比:

| 改動 | Rust | C# |
|---|---|---|
| Shield + branchless enqueue | **+1.63%** ✅ | **-2.15%** ❌ |
| RecalcNodeFast OR-all | -1.86% ❌ | -2.60% ❌ |
| AddNodeToGroup branchless mask | -19.18% ❌ | **-37.34%** ❌ |

C# JIT vs Rust LLVM 對 if-form 最佳化程度差很大。 **C# 端 hot path 已逼近 JIT 飽和點,可動範圍極窄**。

### 待測:Phase G 進階 cache 控制(2026-05-29 晚,排入待測)
- Prefetch hints(`Sse.Prefetch0/1` C#;`_mm_prefetch` Rust)── 暗示 CPU 提前載入 TransistorList 下個 segment
- Non-temporal store(`Sse2.StoreNonTemporal` C#;`_mm_stream_si32` Rust)── framebuffer 寫 bypass cache
- 風險:**prefetch 通常 wash 或微負(modern OoO 已自動 prefetch);non-temporal 對冷寫資料有效但 framebuffer 寫頻率低(每 pclk1 rising,~50K hc 一次)效益有限**
- 暫不做(優先度低於現有提案,且預期 ROI 差)

---

## 引用的 memory(若改動方向相關,務必先看)

- `counter-fastpath-dead-end` ── well-predicted branch removal 的失敗範例
- `bitset-bfs-dead-end` ── walk-size distribution 沒看就動手的代價
- `rust-port-best-config` ── RCM 重排負效益記錄
- `dead-end-skip-dead-end` ── 「看起來無用」的 skip 常斷別人
- `per-chip-parallel-dead-end` ── 並行不是萬靈丹
- `jit-vs-llvm-recursive-inline` ── C# 改善不一定 sync 到 Rust 為正
- `s1-fork-results` ── 當前 best 基準與累積增益
