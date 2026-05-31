# S2 架構候選 list(我的判斷 + 排序)

> 來源:Gemini 諮詢原文見 [`01_gemini_consult.md`](01_gemini_consult.md)。**本文是我(Claude)
> 過濾、修正、排序後的結論**,加入對本專案 dead-ends 與 S1 實際碼的交叉檢查。
> 使用者規則:這是 **list,先不實作**;任一候選要往下做,必須先在**最小原型 + 整機 NES**
> 上證明「比 S1 快 _且_ bit-exact(checksum `0x794A43ABDF169ADA`)」。

---

## 0. 核心判斷(先講結論)

S1 在 ~77K hc/s 的天花板,**不是 ALU、不是演算法,是記憶體延遲**。具體位置:

- 73% 的 live 節點是 **dyn-singleton**(平均走訪 1.4 節點)。
- 這條最熱路徑現在的動作是:讀 `NodeInfo`(16B)→ 取出 `TlistC1c2s` 索引 →
  **dependent 跳進 `TransistorList`(另一條 350KB 陣列的 cache line)** → 才能掃「我的
  channel 電晶體 gate 是不是全部 OFF」。
- 那個 **dependent pointer-chase 是序列化的 L2 stall(~10–15 cycles)**,每個 singleton recalc 都付一次。

→ **S2 的主賭注 = 消滅這個 dependent chase,讓 singleton 路徑只摸最少的 cache line。**
這點 Gemini 與我一致;但**我對它的次要建議(epoch dedup)有 bit-exact 疑慮**,見 §2 與 §3。

---

## 1. 候選清單(分層 + 排序)

### 🟢 Tier 1 —— 首要原型(真正的賭注)

#### S2-A：節點鄰接內聯(消滅 singleton 路徑的 dependent chase)
- **機制(d-cache)**:把每個節點的 channel 電晶體 gate 直接內聯進節點記錄,singleton
  檢查不再跳 `TransistorList`。把現在的「2 條結構性 cache line + 序列化依賴」收斂成「1 條」。
- **bit-exact**:✅ 純資料佈局轉換,不丟任何電晶體,`NodeConnections`(電容 tie-break)原樣保留
  → 避開 dead-end #8([[hotpath-ceiling-and-antipatterns]] 的 L8 floating tie-break)。
- **工作集**:估算反而**變小** —— 現在 NodeInfo 16B×15K(240KB)+ TransistorList(~350KB)=~590KB;
  內聯後單一陣列 ~480KB(大 fan-out 溢位到 cold 陣列)。更小 + 少一次 indirection。
- **ROI**:我把 Gemini 的「>1.15×」**下修為「+5–15%,需原型確認」**——理由見 §3(那 350KB
  的 ushort list 可能本來就多半待在 L2,省下的是 L2-hit 而非 DRAM-miss,效益沒它說的篤定;
  但「序列化依賴變並行/消失」仍是真 win)。這是**最可能成為結構性一步**的候選。
- **風險**:struct 變太大→ cache-line 密度掉。Gemini 規則「**勿超過 32B**」要遵守。

  **兩個佈局變體,原型要 A/B(我傾向變體 2)**:
  - **變體 1(Gemini 的 AoS)**:單一 32B struct `{flags, count, ovf_idx, gates[7], others[7]}`,
    2 個/64B line。singleton 取 32B、只用到 16B。
  - **變體 2(我的 SoA-split,傾向)**:拆成**兩條平行陣列、皆以 node id 直接索引**:
    - `NodeGates[id] = {flags, count, gates[K]}`(熱;singleton **只**摸這條,調 K 讓它 ≤16B → 4 個/line,密度更高)
    - `NodeOthers[id] = {others[K]}`(冷;**只有真的走訪(~30%)才摸**,且是直接索引、**無 dependent chase**)
    - `> K` fan-out(VCC/GND/clk/bus)→ 溢位 cold 陣列。
    - 理由:70% singleton 不需要 `others[]`,把它從熱 line 拿掉,singleton 路徑密度從 2/line 升到 4/line。
  - **先量 fan-out 分布**決定 K(讓 ~85–90% 節點 inline,其餘溢位);bucketing(gnd/pwr/normal)
    用 count 邊界保留,別丟。

- **最小原型**:見 §4。

---

### 🟡 Tier 2 —— 便宜、可疊加(與 A 一起做)

#### S2-B：把冷路徑 outline 出去,保護 L1 i-cache
- **機制(i-cache)**:settle 主迴圈只留「queue pop + singleton fast-path + 小走訪 BFS」,
  讓核心 walker 舒服待在 32KB L1i。把以下強制 `NoInlining` 推出主迴圈:
  (1) 大 fan-out(VCC/GND)的溢位走訪;(2) supply-anchored group 解析;(3) floating 電容 tie-break。
- **bit-exact**:✅ 純 codegen 邊界調整。
- **ROI**:+2–5%,便宜。
- **⚠ C# 專屬警告(我的補充,Gemini 沒講)**:本專案記憶 [[jit-vs-llvm-recursive-inline]] 有「方法
  變太大讓 JIT 拒絕 inline 熱 cascade = −6%」的教訓。所以 outline **只能推冷路徑**,**熱的
  common path 必須維持 inlined**。C# 與 Rust 要分別量(常 sign-flip)。

---

### 🟠 Tier 3 —— A 勝出後再考慮(複雜度高、報酬中等)

#### S2-C：動態 co-activation 節點重排
- **機制(d-cache)**:離線 profile 哪些 edge 常「同時 ON」,greedy heavy-edge 聚類 → 產生新的
  node-id map,在 lowering 階段燒死(runtime 零成本)。與 RCM 不同:RCM 用**靜態**拓樸(含 90%
  常 OFF 的電晶體);這裡用**實際 runtime ON** 拓樸。
- **bit-exact**:✅ node id 是內部任意索引,只要 remap I/O pin。
- **ROI**:+2–8%。**與 S2-A 疊加**(讓走訪到的 `others` 落在剛取的同一條 line)。
- **風險**:(1) 對特定 ROM overfitting(Gemini 說 NES toggling 高度 cycle-deterministic ——
  CPU 部分大致對,PPU 隨畫面變動較大,需用多個 ROM profile 取交集);(2) build pipeline 複雜度。
- **我的定位**:**只有 A 先證明贏過 S1、且還想再榨**才做。不是第一步。

---

### 🔴 存疑測試 —— 很可能是死路,但便宜可證偽

#### S2-D：Epoch 去重 bit-vector(我對 Gemini 此點有強烈保留)
- **Gemini 主張**:每半週期 memset 一個 visited bit-vector;pop 到已 visited 的節點就 skip,
  宣稱 +5–10%(理由:「已 settle 的 channel-connected component 在該半週期剩餘時間穩定」)。
- **我的判斷(分歧)**:**這個「穩定」前提在我們的引擎大概率不成立**。S1 是 **Gauss-Seidel
  原地 settle**(`SetNodeState` 立即寫 `NodeStates`,wave 內有順序相依 —— 見記憶
  [[hotpath-ceiling-and-antipatterns]] 與先前 queue-order 討論)。一個節點的 group 會因為**稍後**
  另一個 gate flip 而改變,所以同一半週期內**本來就可能需要重算**。跨事件 skip → **很可能破
  bit-exact**。這正是 [[hotpath-ceiling-and-antipatterns]] 列的「state-caching fallacy」反模式。
- **另外**:S1 **已有** per-wave 去重(`RecalcHash` 雙緩衝 + BFS 的 `_inGroup`),Gemini 似乎不知道。
  它的 epoch 是更強(跨整個半週期)的宣稱,風險也更大。
- **處置**:**當成快速證偽實驗**——加 bit-vector、先只驗 checksum。若 checksum 變 → 立刻丟(預期如此)。
  若奇蹟般 bit-exact 才量效能。**不投入建構,先證偽**。

---

### ⛔ 駁回(Gemini 同意 / 我們已證實)

| 候選 | 駁回理由 |
|---|---|
| 6% feed-forward fringe 編 bytecode | Amdahl 上限 6% + 熱迴圈多一個 `if(is_bytecode)` 分支 + 雙引擎污染 i-cache。**與我們 IR/codegen 3–6× 慢的根因一致**([[s4-route-single-instance]])。Gemini 也自評駁回。 |
| Software prefetch(queue 前瞻) | 走訪僅 1.4 節點,迴圈太短,prefetch 來不及或塞爆 execution port。與 SIMD-queue marginal 一致。**全部做完再說,別先碰。** |

---

## 2. 我與 Gemini 的分歧(誠實標註)

1. **S2-A ROI**:它「>1.15× silver bullet」過於篤定;我下修為「+5–15%,原型為準」。350KB ushort
   list 多半可能已在 L2,省的是 L2-hit 不是 DRAM-miss。**但方向我同意,值得當第一原型。**
2. **S2-A 佈局**:它給單一 32B AoS;**我提 SoA-split(變體 2)對我們 70%-singleton profile 應更好**
   (singleton 不碰 `others[]`,密度 4/line)。原型要 A/B 兩變體。
3. **S2-D epoch dedup**:它樂觀 +5–10%;**我判斷大概率破 bit-exact**(Gauss-Seidel 原地 settle +
   已有 RecalcHash),歸到「存疑、先證偽」。這是它最像「聽起來很好但忽略我們 settle 語意」的一點。
4. **Gemini 不知道的事實**:S1 已有 per-wave 去重(RecalcHash/_inGroup)、已把 NodeStates 與
   NodeConnections 分離為 dense/cold(它的「Don't touch rule」我們已遵守)。

---

## 3. 為什麼這條路「可能」比過去的嘗試有機會

過去全敗的共同根因:**破壞事件驅動稀疏性 或 破壞 bit-exact**(批次重算全圖 / 平行 overhead /
levelize 撞 SCC / merge 破電容)。**S2-A 兩者都不碰**:它仍是事件驅動、仍逐節點、仍 bit-exact,
只是把同一筆工作的**記憶體存取成本**降低。這是唯一還沒被認真攻過、且不違反已知物理限制的維度
(memory-latency,而非 compute 或 parallelism)。**不保證贏**,但它是賠率最好的單一賭注。

---

## 4. 建議的第一個原型(S2-A)與驗證流程

**目標**:用最小改動驗證「消滅 singleton dependent-chase」能否贏過 S1,bit-exact。

1. **先量 fan-out 分布**(channel-transistor count per node)→ 決定 inline K(目標 ≤16B 的 NodeGates record、~85–90% inline)。
2. **改 `WireCore.Reset()` 的扁平化**:除了現有 `TransistorList`,額外建 `NodeGates[]`(內聯 gate ids + count + bucket 邊界)與 `NodeOthers[]`;`> K` 走溢位。
3. **改 `RecalcNode` / `RecalcNodeFast` / dyn-singleton 掃描**:singleton 檢查改讀 `NodeGates[id]`(不碰 `TransistorList`);走訪時讀 `NodeOthers[id]`。
4. **保持不動**:`NodeStates`(dense)、`NodeConnections`(電容)、`FlagsToState` LUT、所有 group-resolve 語意。
5. **驗證(硬閘門,整機 NES)**:
   - 跑 `full_palette` 300k hc → checksum **必須** `0x794A43ABDF169ADA`。不符 → 設計錯,先修對再談效能。
   - interleaved-paired vs S1(交替、中位數 + trimmed-mean + paired-win-count);C# 與 Rust **都**量。
   - 贏過 S1 baseline(C# ~77K / Rust ~76K)且穩定 → S2-A 成立,才繼續(疊 S2-B,評估 S2-C)。
   - 沒贏 → 如實記錄、收掉,S2 不進後續階段(遵守硬閘門)。
6. **先在哪個 fork 做原型**:建議 **Rust(`experiment/rust-s2/`)先行** —— 編譯快、layout 控制直接、
   無 JIT 變數;站穩後再移植 C#(`src/AprVisual.S2/`)並各自量(預期會 sign-flip,分開判定)。

> 註:本文只到「list + 原型計畫」。實作與量測結果另開 `MD/S2/proto/` 記錄。
