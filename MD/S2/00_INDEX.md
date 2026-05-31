# S2 階段 —— 文件索引

> 本目錄記錄 **S2 的重新設計與發展過程**。S2 的原定義是「netlist → IR」,但 2026-05-31
> 經使用者決策，**整個方向歸零重來**(見下「方向重設」)。本目錄取代先前散落在
> `MD/impl/`、`MD/suggest/`、`MD/note/` 的 S2/IR 殘留討論，作為 S2 的單一文件家。

---

## 方向重設(2026-05-31)

**背景**:S1(switch-level 事件驅動引擎)完成且調校到頂之後，後續嘗試的每一條路
**全部慢於 S1**,已逐一證實為死路:

| 方向 | 結果 | 根因 |
|---|---|---|
| netlist → IR / 整程式 codegen | 3–6× 慢 | i-cache 爆掉 + 批次每半週期重算 ~14.7K 節點(只有數百個真的變) → 摧毀事件驅動稀疏性 |
| GPU / bit-parallel dense BFS | 156× 慢 | 平均走訪僅 1.4 節點,平行 overhead 壓垮小走訪 |
| per-chip / 細粒度多執行緒 | 15× 慢 | 每波工作太小,攤不掉同步成本 |
| levelize / 拓樸排程 | 不可行 | 被 94% 雙向 SCC 擋住 |
| RCM 節點重排 | 無增益 | (matrix bandwidth 取向,與動態走訪局部性無關) |
| prune/merge、stub 移除 | 破壞 bit-exact | floating-group tie-break 依賴每節點 connection 數(寄生電容) |

**使用者決策**:
1. **以 CPU 為主**(不碰 GPU)。
2. **硬性閘門**:S2 必須**真的贏過 S1**,否則**不進入後續階段**。
3. S2 設計**先問 Gemini**(特別注意 **i-cache / d-cache** 效能)當參考,再加入合理判斷,
   重新設計架構或方法,**先整理成 list**。
4. **驗證政策**:一律**跑整個 NES 系統**測試 —— 子電路太小,測不到 PPU/CPU 更複雜結構
   的互動問題。

這與既有記憶 [[beat-s1-rule-and-ir-reattempt-plan]] 一致:任何大型架構移動,必須在
**最小原型**上先證明「**比 S1 快 _且_ bit-exact**」才往下投入(先驗證點、再投資點)。

---

## S1 baseline(S2 要打敗的門檻)

詳見 [`perf/01_s1_baseline.md`](perf/01_s1_baseline.md)。摘要(300k hc / full_palette,
6 輪丟 warmup):

| 引擎 | 中位數 | trimmed-mean | bit-exact checksum |
|---|---|---|---|
| **C# S1** | **77.1K hc/s** | 76.4K | `0x794A43ABDF169ADA` @ t=300192 |
| **Rust S1** | **75.8K hc/s** | 76.3K | `0x794A43ABDF169ADA` @ t=300000 |

→ **S2 門檻 ≈ 76–77K hc/s,且 checksum 必須仍為 `0x794A43ABDF169ADA`**。
real-time NES 需 42,955K hc/s(~552–572× 之遙;不期望達成,目標是清楚且可重現地超越 S1)。

---

## S2 起點(已建立)

兩份引擎已從 S1 **逐字複製**為獨立目錄(commit `6058b1b`),引擎碼與 S1 逐位元組相同:
- C#:`src/AprVisual.S2/`(AssemblyName `AprVisual.S2`,已入 `AprVisual.sln`)
- Rust:`experiment/rust-s2/`(crate + bin `wire_s2`)

起點 = S1,尚未動任何改造。

---

## 驗證政策(所有 S2 實驗共用)

1. **整機 NES 工作負載**:`full_palette`(主)、必要時 `smb`、blargg test ROM。
   不用孤立子電路 —— PPU/CPU 互動、bus 多重驅動、floating tie-break 這些只有整機才會踩到。
2. **bit-exact 閘門**:NodeStates checksum 必須 == S1 的 `0x794A43ABDF169ADA`(@300k)。
   任何改動先過這關,才看效能。
3. **效能量測**:interleaved-paired(交替 base/exp,中位數 + trimmed-mean + paired-win-count);
   sub-2% 差異不採信 batched A/B(時間漂移偏差)。見記憶 [[interleaved-paired-bench]]。
4. **兩引擎都要看**:C# 與 Rust 的 JIT/LLVM 行為不同,同一改動常 sign-flip
   (見 [[jit-vs-llvm-recursive-inline]]),不可只測一邊就下結論。

---

## 子目錄

- `perf/` —— 效能 baseline 與量測紀錄
  - `01_s1_baseline.md` —— S1 baseline(S2 門檻)
- `design/` —— 架構設計探索(Gemini 諮詢 + 自身判斷 + 候選 list)
  - `01_gemini_consult.md` —— Gemini 3.1-pro 原文記錄(參考用,逐字保留)
  - `02_architecture_candidates.md` —— **過濾後的判斷 + 排序 list**(主交付物)
  - `03_old_methods_reeval.md` —— 舊 dead-ends 重評估(哪些值得新模型重試):O1 batched 負面重驗(零風險)+ O2 prune/merge 解耦電容(需驗 bit-exact)值得;其餘根本死路封存

> 視需要再增子目錄(如 `proto/` 原型實驗紀錄、`note/` 主題研究)。

## 候選結論摘要(詳見 design/02)

主賭注 **S2-A:節點鄰接內聯** —— 消滅 73% singleton 熱路徑那次 dependent pointer-chase
(序列化 L2 stall),把 2 條 cache line 收成 1 條;bit-exact 安全(不丟電晶體、保留電容數)。
我把 Gemini 的「>1.15× silver bullet」下修為「+5–15%,原型為準」,並提出比它更適合本專案
70%-singleton profile 的 SoA-split 佈局變體。次要:S2-B(outline 冷路徑保 i-cache,+2–5%)、
S2-C(動態 co-activation 重排,A 勝出後再做)。**駁回** bytecode-6%(Amdahl + i-cache,與我們
IR 失敗同因)與 prefetch(走訪太短)。**存疑先證偽** S2-D epoch dedup(我判斷大概率破 bit-exact
—— Gauss-Seidel 原地 settle,且 S1 已有 RecalcHash 去重;Gemini 此點忽略了我們的 settle 語意)。
