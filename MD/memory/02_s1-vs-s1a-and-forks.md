# S1 vs S1A(以及各 fork 的關係)

## 一句話
**S1 = 掛牌金標準(完成態,靠 shim 過測);S1A = 之後才 fork 的「拆 shim」研究分支。**
S1 本體永不因 S1A 的工作而改動。

## S1(`src/AprVisual.S1/`)= 金標準
- 在分支 `aleread-ioce-mux` 上以 shim + ALERead mux 機制達成 **AC 141/141 + blargg 146/147**,
  驗完才合併回 `main`(合併 commit `b01a1c3`「Merge aleread-ioce-mux: 141/141 AccuracyCoin banked」)。
- **自合併起 `src/AprVisual.S1/` 零 commit**(可查:`git log b01a1c3..HEAD -- src/AprVisual.S1/` 為空)。
- 金 checksum `0x794A43ABDF169ADA`(300k hc,`--extra-ram`,full_palette)鎖 bit-exactness,永不動。
- Rust 位元對等版在 `experiment/rust-s1/`。

## S1A(`src/AprVisual.S1A/`)= 拆 shim 研究 fork
- 2026-07-17 從 S1 verbatim fork(commit `2da7f39`)。目標:把每顆 shim 換成有物理原理的機制,
  **拆得掉要三段論驗證,拆不掉誠實記錄成 study**(不捏造)。
- 網站:`WebSite/s1a.html` + `WebSite/s1a/*.html`(方法論專文、shim 總帳、可判定性研究)。
- 引擎機制:M2 電容/時戳衰減、M4 edge-latch、M6×M3 統一相位、M5e 板級 bus-hold(立案)等。
- 進度總帳:`MD/S1a/02_解析工具箱_網站專文_shim退役_長期TODOLIST.md`。
- **S1A 的機制/實驗絕不回改 S1**;S1A 自己逐 phase 重定基準。

## 其他 fork(reference / deprecated)
- `src/AprVisual.S2/`:S1 verbatim copy,承載已結案的 Escape-1 效能研究(`--miter`/`--compile`/`--cones`)。
- `src/AprVisual.Deprecated/`:原始 WinForms live-window 版 + S2/S3/S4 IR/codegen/GPU 實驗。**只讀,別改**。

## 為什麼「S1a 嚇人的結論」不影響 S1 基準
- S1a 是獨立 fork,S1 源碼零改動(git 可證)+ 金 checksum 不變 → **S1 基準物理上不可能因 S1a 退步**。
- 2026-07-18 S1a 掃描出的「翻籤/回歸」結論,根源是**量測程序錯**(K=0 配方 + 漏 ALEREAD_MUX),
  已收回。詳見 [03_lessons-and-gotchas.md](03_lessons-and-gotchas.md)。
