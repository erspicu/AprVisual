# MD/memory — 專案內持久記憶(跨壓縮、跨換模型都在)

> 為什麼有這個目錄:Claude 的工作記憶 = 對話 context,對話壓縮或換模型時會遺失沒寫下來的
> 操作細節(2026-07-18 實際發生過:漏用正牌工具、漏設 ALEREAD_MUX、K=0 配方誤判)。
> 這裡放**跨 session 必須活下來的操作事實**,每條都附 source 檔案行號 + commit,可直接查證。
> 這是 repo 內版控的記憶,和使用者層 `~/.claude/.../memory/` 不同(那個是偏好/方向)。

## 動手前必讀順序
1. **[00_baseline-and-run-recipes.md](00_baseline-and-run-recipes.md)** — 掛牌基準(S1=141/141 AC + 146/147 blargg)
   與**逐字掛牌配方**(AC 的 CLI + `ALEREAD_MUX=1 MUX_HC=...` env;147 的 runner 指令)。
   **要重跑/驗證任何成績,先讀這頁把配方抄對。**
2. **[01_ac-verdict-mechanism.md](01_ac-verdict-mechanism.md)** — AC 怎麼自己判定通過(`$07F0` 完成區塊、
   奇=通過編碼、SHA/SHS 多變體、中途讀表工具)。
3. **[02_s1-vs-s1a-and-forks.md](02_s1-vs-s1a-and-forks.md)** — S1(金標準,合併後零改動)vs S1A(拆 shim 研究 fork)。
4. **[03_lessons-and-gotchas.md](03_lessons-and-gotchas.md)** — 血淚教訓:ALEREAD_MUX opt-in 陷阱、
   K=0 配方假警報、土製工具、抽籤測試。

## 鐵律(從教訓濃縮)
- **重跑前先讀本目錄 + runner 原始碼確認配方**,別靠記憶、別土製工具。
- **成績 = 引擎 build × 網表 × 測試配方**;缺一個變數都不能跨時代比。
- 用正牌工具:`tools/testrom/ac_watch.py`(哨兵)、`ac_snap_results.py`(中途讀表)、
  `run_tests.py`(147)。
- 網表一律用修正版 `AprVisualBenchMark/data/system-def/`(含 t13032b/t14634b 補管)。
