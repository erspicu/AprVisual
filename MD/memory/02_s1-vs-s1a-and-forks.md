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
- **§8 一次性取證 CLI 探針清除**(commit `64b2f48`,**−536 行**):刪 `--op-probe`/`--bus-trace`/`--rdy-probe`/`--phase-probe`(Probes.cs)、`--probe2002`/`--probe-2001`/`--probe-dma`/`--probe-vbl`(Diag.cs)、`--ppu-memory-trace(-x)`(含 `WireCore.Handlers.cs` 引擎 hook + 熱路徑呼叫點,guard 保護故 checksum 不變)。**保留通用檢視工具**:`--dump-node`/`--dump-module`/`--dump-system`/`--names`/`--watch`/`--micro`/`--trace`/`--ac-dump-work`。
- **`--callback-drain-limit` 移除**(commit 待記):它是診斷用非收斂 tripwire,選一條與正常迴圈**逐 dispatch 位元相同**的慢速 drain(`DrainCallbacksWithLimit`),line 332 的 gate 純 logging。真正的收斂/防遞迴機制是 **`InvokeCallbacks` re-entrancy guard**(`_invoking`,永遠開、修 AC ~24k 深遞迴)—— drain-limit 已冗餘。刪:CallbackDrainLimit 欄位 + DrainCallbacksWithLimit/Throw/DescribeCallback 三方法 + selftest + CLI parse/help。金 checksum 不變(benchmark 本就走正常迴圈)。**S1 保留此旗標**;S1A 的 `run_ac_s1a_*.bat` 已拿掉 `--callback-drain-limit 2000`(S1 的 `run_accuracycoin.bat` 保留)。
- ⚠️ `run_ac_s1a_*.bat` 的 `ALEREAD_MUX=1 MUX_HC=...` 對 S1A 現在是**無害 no-op**(env 已不讀),.bat 照跑;註解「ALEREAD_MUX REQUIRED」對 S1A 已不成立。
- 文件已更新:`WebSite/s1a-cli.html`(機制表改「恆開」、env 面/一次性探針/drain-limit 都拿掉不再列)、`WebSite/s1-cli.html`(分岔註記)。
- 上述四項(NO_* 去閘、探針清除、§8 CLI 探針、drain-limit)累計移除約 **1,900 行**,benchmark 金 checksum 全程 `0x794A43ABDF169ADA` 不變(那時機制只在 test 武裝)。

## ⚠️⚠️ S1A「全模式全武裝」架構改動 + 新 golden checksum(2026-07-21,同分支,commit 待記)
使用者發令:S1A 不該有「test 才武裝」的模式分別 —— **每個模式(含 `--benchmark`)都直接全武裝**。
- **實作**:機制武裝從 `RunOneTest` 搬進**共用 `LoadSystem`**(`WireCore.ArmMechanisms` 旗標,預設 true;`TestRunner.Run` 設 `= !_noShims`)。載入前設 `AleReadMuxShim`/`RegisterRawIdAliases`/`PowerUpStateShim`(pass loop 前),載入後(ClearPostLoadBuildState 之後)呼叫 `ArmS1aMechanisms()`(11 個 Enable*Mech,順序同原 RunOneTest)。RunOneTest 只留 mode-specific:`EnableJoypadHandler`(--joypad)、`PpuAleReadFeedback`(AC)。
- **`--no-shims` = 唯一逃生口**(純開關級引擎 = S1 核心)。
- **調色盤決策**:使用者最終選「S1A 用真實開機狀況」→ `PowerUpStateShim`(開機殘留 + P=$34)納入全武裝集合(甲)。不強求跟 S1 一致。
- **★ S1A GOLDEN CHECKSUM（full_palette,--extra-ram,比照 S1 兩校準點）★**:
  | hc | S1A 全武裝(預設) | raw (`--no-shims`,= S1 核心) |
  |---|---|---|
  | 300k | **`0x41244C26C45EDD32`** | `0x794A43ABDF169ADA` |
  | 1M | **`0xFA016F9CEA14029B`** | `0x6D4CCBCE2E9CD599` |
  - raw 兩點與 S1 **完全吻合** → 證 S1A 核心引擎與 S1 位元相同;全武裝有自己的指紋。
- **驗證**:full_palette 50 幀 frame-dump,S1A(甲,含殘留)vs S1 → 前 2 幀差在開機殘留調色盤(真實特徵、非 bug),第 3-50 幀 byte 相同。`--no-shims` frame-dump vs S1 = 50/50。⚠️ `--test` 全量 AC/147 尚未重驗(機制搬家後行為應等價,待跑)。
- **連帶更新**:`run_tests.py` 的 `GOLDEN_CKSUM` 改成 engine-aware(S1A_ENGINE → `0x41244...`);`AprVisualBenchMarkS1A/` 工具 README 記兩點 checksum。
- ⚠️ **未合併回 main**;合併前跑權威 AC-141 + 147。

## S1A benchmark 工具(`AprVisualBenchMarkS1A/`,2026-07-21)
比照 `AprVisualBenchMark/`(S1 版)建的 S1A 效能包,**C# only(無 Rust、暫無 mac)**,gitignored staging。
- `win/csharp/`(S1A self-contained 多檔 trimmed,33 檔 ~21MB)+ `data/system-def`(**修正版含 t13032b 補管**)+ `roms/full_palette.nes` + `run_csharp.bat`(benchmark,預設全武裝)+ `shot_csharp.bat`(frame-dump)+ README。
- `--benchmark` 直接量**全武裝 S1A** 吞吐(≈128K hc/s);`--no-shims` 量 raw。發佈腳本比照 `tools/release_benchmark.ps1`(S1 版)。

## 其他 fork(reference / deprecated)
- `src/AprVisual.S2/`:S1 verbatim copy,承載已結案的 Escape-1 效能研究(`--miter`/`--compile`/`--cones`)。
- `src/AprVisual.Deprecated/`:原始 WinForms live-window 版 + S2/S3/S4 IR/codegen/GPU 實驗。**只讀,別改**。

## 為什麼「S1a 嚇人的結論」不影響 S1 基準
- S1a 是獨立 fork,S1 源碼零改動(git 可證)+ 金 checksum 不變 → **S1 基準物理上不可能因 S1a 退步**。
- 2026-07-18 S1a 掃描出的「翻籤/回歸」結論,根源是**量測程序錯**(K=0 配方 + 漏 ALEREAD_MUX),
  已收回。詳見 [03_lessons-and-gotchas.md](03_lessons-and-gotchas.md)。
