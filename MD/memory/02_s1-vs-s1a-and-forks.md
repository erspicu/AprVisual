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

## ⚠️ S1A 通則機制「直接執行」化 + 探針清除(2026-07-21,branch `s1a-fold-general-mechanisms`,未合併)
使用者發令:把選項式通則機制的布林 branch 拿掉、一律直接執行(連 default-ON 都不要),並移除低價值/一次性探針。
**只動 S1A,S1 完全不碰**(S1 的 NO_*/ALEREAD_MUX/probe env 全部保留 —— MD/memory/00 的 S1 掛牌配方仍需 `ALEREAD_MUX=1 MUX_HC=...`)。
- **機制無條件執行**(`TestRunner.Test.cs`,commit `9ccd67f`):拔掉 8 個 `NO_*` A/B 閘(M4_EDGE/M6X/M4_P1/M4_OE/M3_ABORT/M1_LXA/M4_FI/M2DECAY)+ `NO_PPU_ALE_FB` + `NO_OB_SHIM`/`NO_DL_SHIM`;
  **`ALEREAD_MUX` 改成 test 模式恆武裝**(`AleReadMuxShim = !_noShims`),`MUX_HC`/`MUX_GATE` env override 移除(碼預設已 = 校準 `13,13,25,44,52` / `3,3,220,226`)。唯一逃生口 = `--no-shims`。死掉的 superseded-shim arming 一併刪。
- **探針清除**(`WireCore.System.cs`,commit `22393e2`,**−836 行**):刪 `DmaProbeStep`(~670 行,[sp]/[sq]/[sr]/.../[ae]/[a5]/[bgs] 子探針)+ 全部 env-gated `*_DEBUG`(OB_DEBUG/MUX_DBG/OE_DEBUG/LAE_DEBUG/PWD_DEBUG/PB_DEBUG/ODMA_DEBUG/PC_WIN)+ 其 Console 站點 + 孤立探針欄位。保留真實邏輯(`_pdLastBus`/`_laePrevAcs`/`_pwdVp` 等)。
- **保留不動**(env-gated 實驗性、預設關、不在認證組):`M2_CAP`/`M2_CENSUS`(未拍板電容仲裁調查)、`M4_DL`(實驗 DL row,與恆開 DlShim 重複)。這三個 env 仍讀取。
- **驗證**:金 benchmark checksum **`0x794A43ABDF169ADA` 不變**(每步重驗;機制只在 test 模式武裝、benchmark 路徑從不武裝);載入診斷確認 8 機制 + M2 + ALERead mux 免 env 自動武裝(mux `sw=13 rp=[13,25) fz=[44,52)`);`ppu_open_bus`+`ppu_read_buffer` PASS。**權威 AC-141(~8h)+ 147(~7.5h)未跑,待使用者用 `.bat` 發**(build net11 用 `dotnet build`)。
- ⚠️ `tools/testrom/run_ac_s1a_*.bat` 仍設 `ALEREAD_MUX=1 MUX_HC=...` —— 對 S1A 現在是**無害 no-op**(env 已不讀),.bat 照跑;註解「ALEREAD_MUX REQUIRED」對 S1A 已不成立。
- 文件已更新:`WebSite/s1a-cli.html`(commit `77fca55`,機制表改「恆開」、env 面標「已移除」、配方去掉 env 前綴)。
- ⚠️ **未合併回 main**;合併前應跑完權威 AC-141 + 147。

## 其他 fork(reference / deprecated)
- `src/AprVisual.S2/`:S1 verbatim copy,承載已結案的 Escape-1 效能研究(`--miter`/`--compile`/`--cones`)。
- `src/AprVisual.Deprecated/`:原始 WinForms live-window 版 + S2/S3/S4 IR/codegen/GPU 實驗。**只讀,別改**。

## 為什麼「S1a 嚇人的結論」不影響 S1 基準
- S1a 是獨立 fork,S1 源碼零改動(git 可證)+ 金 checksum 不變 → **S1 基準物理上不可能因 S1a 退步**。
- 2026-07-18 S1a 掃描出的「翻籤/回歸」結論,根源是**量測程序錯**(K=0 配方 + 漏 ALEREAD_MUX),
  已收回。詳見 [03_lessons-and-gotchas.md](03_lessons-and-gotchas.md)。
