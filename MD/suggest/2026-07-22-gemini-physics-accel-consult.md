# Gemini 3.1 Pro 諮詢:物理屬性能否加速引擎 + 14.5% SCC 重評估划不划算(2026-07-22)

完整回覆:`2026-07-22-gemini-physics-accel-consult-reply.txt`。用 `gemini-3.1-pro-preview`。
背景:levelizable-probe 量到引擎真實圖 99.4% 一顆巨大 SCC、settle 有 14.5% 重評估(全在 SCC 內);
使用者問:(1) 動態手段壓那 14.5% 划不划算/怎麼設計;(2) 把 S1 丟掉的電容/電阻/驅動強度**物理屬性**
拿來加速,有沒有擴展空間。

## Q1 — 動態壓 14.5% SCC 重評估:**別做(估 −15% ~ −30%)**
- 那 14.5% **不是浪費**,是解「互相對驅的 driver」的定點迭代在正常運作。
- 數學:每省 1 個重評估,仍要處理 ~6 個必要評估;`RecalcNode` 才 20-30 cycle,追蹤機制**預算 ≤3-4 cycle/node**,
  一載入 `NodeDepth[nn]`/`ActiveDrivers[nn]` 就多一個 dependent L1 load(4+ cycle)→ 立刻水下。
- Catch-22:要知道上游會不會變,得走 99.4% SCC 圖 = 正是要避開的 memory-bound 工作。
- 結論:雙緩衝 BFS 已是理論最小開銷,任何 inner-loop 檢查都在懲罰 85.5% 的 fast-path 節點。**維持現狀。**

## Q2 — 物理屬性加速
- **Runtime 物理:別做(−30% 或更糟)**。drive-strength 排序=熱迴圈排序;capacitance skip=L1 壓力 + 破 bit-exact;
  RC batching=浮點/數值積分=更慢(Escape-1 已證)。
- **⭐ Static 物理:高價值(估 +20% ~ +100%,還解鎖 AOT codegen)**。

### 勝著:Physics-Assured Masking 靜態砍 SCC 邊
圖是 99.4% SCC 只因為**拓撲上**把 pass-transistor 當雙向;但**物理上**多數 channel 因驅動強度/電容嚴重不對稱
其實是**嚴格單向**(大 clock buffer 驅動小 pass-transistor 進高阻抗 MOSFET 閘 —— 下游根本沒有物理能力回驅上游)。

機制:
1. **離線** pre-processor 用 S1A 的連續電阻/驅動強度模型,評估每條雙向 channel `A<->B`。
2. 算 `B` 回驅 `A` 的最大物理強度。
3. 對照 golden S1 的 `flags-OR` 256-LUT:比 `B` 最大回驅 vs `A` 最小 active hold/drive 強度。
4. **證明恆被遮罩就刪邊**:若能數學證明 `B` 對 `A` 的貢獻**永遠**被 `A` 本地態或更強上游遮掉(A 不管 B 都解成同值),
   就把 `B->A` 依賴邊**永久從 TransistorList 刪除**。

為何贏、且守規則:
- **Bit-exact 零風險**:只是預先算出「這條邊的 flag 貢獻反正會被 S1 LUT 丟掉」→ 最終態逐位元組相同。
- **只移除工作**:實體縮圖,平均導通群 <1.4,`RecalcNode` BFS 步數更少。
- **解鎖靜態序(大 secondary win)**:砍掉功能性死回邊 → **打碎 99.4% SCC 成 DAG(或只剩 latch 之類小 SCC)**。
  levelize 後可靜態重排 `NodeStates` 保證 wave 傳播完美 L1 prefetch;更關鍵 —— 打碎巨大 SCC 直接消掉當年
  AOT/codegen 失敗的根因,**重開 3-6× 編譯靜態 DAG 的門**。

## 與我們既有結果的關係(重要)
這**不同於**當年判死的「單向 pass-gate 降級」([[gemini-accel-ideas-evaluated]] ①,`eval_unidir_demotion.py`
找 PURE-SINK leaf = 0%)。那個找的是「強驅→純 gate-cap 無 pull 的葉」,NMOS depletion 負載下不存在 → 0%。
Gemini 這次是**更一般**的判準:不要求純葉,只要證明**回驅被遮罩**(強度 + LUT 優先序)就能刪回邊 ——
即使沒有純葉也可能刪得掉、打碎 SCC。**這是還沒量過的角度。**

## 待評估(要動手前先量)
1. **強度資料品質**:S1A 的電阻/驅動強度模型精細到能做「恆被遮罩」的**保證級**證明嗎?(不是啟發式)
2. **可刪邊比例**:寫離線分析,量有多少雙向 channel 的回驅可證明恆被遮罩 → 刪掉後 SCC 真的碎掉嗎?
   (若只碎一點、仍是大 SCC,secondary win 不解鎖,價值大減)
3. **bit-exact 閘**:任何刪邊後,golden checksum + AC 141/141 + 147 146/1 全過才算數。
關聯 [[gemini-accel-ideas-evaluated]]、[[beat-s1-rule-and-ir-reattempt-plan]]、[[four-direction-optim-framework]]、
`MD/note/2026-07-22-levelizable-probe-result.md`。
