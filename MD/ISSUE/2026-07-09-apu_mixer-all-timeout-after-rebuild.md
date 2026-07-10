# ISSUE(已解決):apu_mixer / class-A 全數 timeout(detection=none)

> **狀態:已解決(2026-07-09,commit `23ddd89`)。** 本文保留為**原始交接紀錄**——
> 其中「剩下的嫌疑」與「要做什麼」的假說排序**已被實驗推翻**,結論請以根因證明文件為準。

## ✅ 結案摘要(讀這段就夠)

**真正的根因不是引擎重建,也不是 .NET preview。本文原本把嫌疑排在那裡 —— 那是錯的。**

`WireCore.LoadSystem()` 用 **ROM 路徑字串** heuristic(`nes-test-roms` / `nes_test`)決定要不要掛
`cart-extraram` —— 而 blargg `$6000` 協定的 work RAM 就住在那裡。commit `d20f8bc` 把 ROM 打包到
`tools/testrom/roms/` 之後,新路徑**兩個關鍵字都不含** → extraram 沒掛 → `$6000` 簽章區根本不存在
→ **每一個 class-A 測試**(不只 apu_mixer 那 4 個)都回報 `detection=none / timeout`。

- **修復**:test mode 在 `LoadSystem()` 之前設 `WireCore.ForceExtraRam = true`
  (`src/AprVisual.S1/Test/TestRunner.Test.cs`),不再由資料夾名稱推導模擬組態。
- **原假說的反證**:用同一顆「可疑」DLL 跑 golden checksum = **`0x794A43ABDF169ADA`(與基準相符)**
  → **bit-exact 從未被破壞**,引擎自始至終是好的。
- **驗證**:apu_mixer 四筆回到基準判定幀(**721 / 1160 / 970 / 607,一幀不差**);
  修復後乾淨全量回歸 **146 pass / 1 fail / 0 timeout(147 測,6.21 h)**。
- **可帶走的教訓**:**凡是影響模擬組態的條件,不要由路徑/資料夾名稱推導**,要由 CLI flag 或
  test mode 顯式指定。(與 `MD/testrom/2026-07-08-probe-effect-instrument-grade-shims.md`
  的「別動被測物」是同一族教訓。)

**根因證明**:[`2026-07-09-apu_mixer-timeout-root-cause-proof.md`](2026-07-09-apu_mixer-timeout-root-cause-proof.md)
**修復計畫**:[`2026-07-09-testrom-extraram-fix-plan.md`](2026-07-09-testrom-extraram-fix-plan.md)

---

> 以下為 **2026-07-09 04:xx 的原始交接紀錄**,原樣保留(症狀與已排除項仍然正確;
> 唯 §4「剩下的嫌疑」、§6「要做什麼」的假說排序已被推翻)。

## 0. 一句話

**引擎原始碼一行沒改,但「重建後的 DLL」讓 apu_mixer 四個測試從全 PASS 變成全 timeout(連 `$6000` 簽章都沒偵測到)。**
在確認原因之前,**任何效能重跑都不可信**(引擎與 145/1 的引擎行為已不同,數據不可比)。

---

## 1. 症狀

四個 apu_mixer 測試**全部** timeout,且 `detection=none`(引擎從頭到尾**沒看到** blargg 的 `$6000` 簽章),各自跑到自己的幀數預算才停:

| 測試 | 145/1 基準(封存) | AprNes oracle | **現在** |
|---|---|---|---|
| apu_mixer/dmc | pass, det=6000, f=**721** | pass, f=721 | **timeout, det=none, f=1086** |
| apu_mixer/noise | pass, det=6000, f=**1160** | pass, f=1159 | **timeout, det=none, f=1745** |
| apu_mixer/square | pass, det=6000, f=**970** | pass, f=972 | **timeout, det=none, f=1460** |
| apu_mixer/triangle | pass, det=6000, f=**607** | pass, f=608 | **timeout, det=none, f=915** |

- `detection=none` ≠「幀數不夠」。它代表 ROM **根本沒跑到寫簽章那一步**,是**模擬行為變了**。
- 幀預算本身是對的:`min(maxFrames, 1.5×typicalFrames+5)`,而 typicalFrames(721/1159/972/608)經 AprNes 與 S1 自己的通過紀錄**雙重驗證正確**。**不要去調幀預算**,那不是問題所在。

---

## 2. 決定性證據:`engineVersion` 與結果完全相關

每筆結果 JSON 都有 build 當下的 git HEAD(`engineVersion`)。對照如下:

| engineVersion | build 時間 | apu_mixer 結果 |
|---|---|---|
| `76b36f5`(07-07) | 07-07 | ✅ 全 PASS(det=6000) |
| `ba33c36`(07-08) | 07-08 **20:00** | ✅ 全 PASS(det=6000) |
| `9a7987e`(07-08) | 07-08 **22:59** | ❌ **全 FAIL(det=none)** |

`ba33c36 → 9a7987e` 之間只有 4 個 commit(`dbd194e`, `af8c42f`, `d20f8bc`, `9a7987e`),
`git diff --name-only ba33c36 9a7987e` 顯示它們**只碰 `tools/` 171 檔 + `MD/` + `WebSite/`**:

- ✅ **`src/AprVisual.S1/`(引擎)完全沒動**
- ✅ **`AprVisualBenchMark/data/system-def/`(網表資料)完全沒動**(git clean,mtime 停在 07-04 / 05-12)

**同一份原始碼、同一份網表,兩次 build,行為不同。**

---

## 3. 已排除(請勿重走這些路)

1. **不是幀預算太緊** —— `detection=none`,連簽章都沒出現;預算值也已被 AprNes 驗證正確。
2. **不是 ROM 壞掉** —— `tools/testrom/roms/` 的 ROM 與 `nes-test-roms-master/checked/` 逐位元相同(md5 全數比對通過,含 4 個 apu_mixer)。
3. **不是 dmc 專屬** —— 4 個 apu_mixer **全崩**,不是單一測試的怪癖。
4. **不是「亂啟動 / collision 殘留」** —— 已用**乾淨的 4 並行診斷**(`--filter apu_mixer --rerun --no-build`)重現,無其他行程干擾。
5. **不是引擎原始碼變更** —— `git log -- src/AprVisual.S1` 最後一次動引擎是 `76b36f5`;`git status` 對引擎目錄乾淨。
6. **不是 system-def 網表變更** —— 同上,git clean。

---

## 4. 剩下的嫌疑(只剩兩個)

**(A) 重建出來的 DLL 本身行為變了。**
DLL 於 `2026-07-08 22:59:27` 由 `run_full_regression.bat` 的 `dotnet build` 重建。
本機 SDK 是 **`.NET 11.0.100-preview.4.26230.115`(preview!)**。
> 註:build 會把 git HEAD 內嵌成 `engineVersion`,所以**每個新 commit 都會觸發 DLL 重新編譯**——這解釋了為何 22:59 真的產生了一顆新 DLL(即使原始碼沒變)。
> preview runtime/SDK 若在兩次 build 之間有任何變動,就可能改變引擎行為。**用 preview SDK 產生 golden 數據本身就是風險。**

**(B) `romBase` 改動(commit `d20f8bc`)。**
`catalog.json` 的 `romBase` 從 `nes-test-roms-master/checked` 改成 `tools/testrom/roms`(內建打包的 ROM)。
理論上不該有影響(ROM 逐位元相同),但這是**唯一另一個落在測試路徑上的功能性變更**,必須實驗排除,不能只靠推理。

---

## 5. ⚠ 一個尚未確認、可能更嚴重的未知數

**我們還不知道這是 apu_mixer 專屬,還是「所有 `$6000`(class A)測試」都壞了。**

- 在 `9a7987e` 這顆 DLL 上,**唯一通過的測試是 `cpu_timing_test6`,而它用的是「畫面判定」(det=screen),不是 `$6000` 協定**。
- 也就是說:**這顆 DLL 至今沒有任何一個 class-A(`$6000`)測試通過過。**
- 對照:在 `ba33c36` 那顆 DLL 上,`oam_stress` / `ppu_read_buffer` / `instr_timing` / `instr_test-v3/v5` / `cpu_interrupts` 全都 det=6000 PASS。

**這是最該先釐清的一點** —— 若 `$6000` 偵測在新 DLL 上全面失效,那受影響的不是 4 個 smoke test,而是**整個 class-A 測試集(絕大多數)**。

---

## 6. 要做什麼:依序的診斷步驟

### 步驟 1(最快、最決定性):golden checksum 比對

本專案的 bit-exact 金標準。若 checksum 變了,答案就是 (A)。

```powershell
dotnet run --project src/AprVisual.S1 -c Release -- `
  --benchmark AprVisualBenchMark\roms\full_palette.nes `
  --bench-hc 300000 --extra-ram `
  --system-def-dir AprVisualBenchMark\data\system-def
```

- 期望值(README 記載):`0x794A43ABDF169ADA` @300k(另 `0x6D4CCBCE2E9CD599` @1M)
- **checksum 不符** → 現行 DLL **已非 bit-exact**,根因就是 build/runtime(→ 跳步驟 4)。
- **checksum 相符** → 引擎核心是好的,問題在測試路徑(→ 步驟 2、3)。

### 步驟 2(最便宜、釐清爆炸半徑):在現行 DLL 上跑一個「短的 class-A 測試」

驗證 §5 的未知數。挑幀數少的,例如 `instr_test-v3/rom_singles/04-zp_xy.nes`(~644f):

```powershell
python tools\testrom\run_tests.py --filter 04-zp_xy --limit 1 --rerun --no-build
```

- 若 `detection=6000` PASS → `$6000` 偵測正常,問題**限縮在 apu_mixer(APU/DMC 路徑)**。
- 若 `detection=none` → **所有 class-A 測試都壞了**,問題是全域的,嚴重性大幅上升。

### 步驟 3(排除嫌疑 B):用現行 DLL,但改讀「舊路徑」的 ROM

挑最短的 triangle(~607f):

```powershell
dotnet run --project src/AprVisual.S1 -c Release -- `
  --test "nes-test-roms-master\checked\apu_mixer\triangle.nes" `
  --max-frames 900 --reset-hold-extra 1 `
  --system-def-dir AprVisualBenchMark\data\system-def `
  --test-json triangle_oldpath.json
```

- **PASS(f≈607, det=6000)** → 嫌疑在 `romBase`/打包 ROM(儘管 md5 相同,要查 `.gitattributes` / CRLF / checkout 時是否被改寫)。
- **timeout(det=none)** → 排除 B,**確定是 DLL**(→ 步驟 4)。

### 步驟 4(若確定是 DLL):A/B 兩次 build

```powershell
git worktree add ..\AprVisual_ba33c36 ba33c36
dotnet build ..\AprVisual_ba33c36\src\AprVisual.S1 -c Release
# 用這顆舊 DLL 跑 triangle,看是否 PASS
```

- 舊 commit build 出來 **PASS**,新 commit build 出來 **FAIL**,而原始碼相同
  → 證實 **build/runtime 不決定性**。此時檢查:
  - `dotnet --list-sdks` / `--list-runtimes`,對照 07-07~07-08 是否有 preview 更新
  - `AprVisual.S1.csproj` 的 TargetFramework(目前 `net11.0`)
  - 是否有 source generator / MSBuild 把 git 資訊注入而順帶改了別的東西

---

## 7. 要修什麼(視診斷結果)

- **若 bit-exact 已破(步驟 1 checksum 不符)**
  1. **釘死 SDK 版本**:加 `global.json` 指定確切 SDK,**不要用 preview 產生 golden 數據**。
  2. 找出哪個 runtime/JIT 變更改了行為(可能是 unsafe/指標路徑、記憶體配置、或 JIT 優化)。
  3. 重新確立 golden checksum 基準(在釘死的 SDK 上)。
  4. **重跑並重新認證 145/1**,因為舊分數是在舊 runtime 上取得的。

- **若 checksum 沒破,但 class-A 全壞(步驟 2)**
  → 問題在 `--test` 的 `$6000` 偵測路徑(`Test/TestRunner.cs` / `WireCore.Trace`),查是否被某次 tools/ 變更間接影響(理論上不該,但要查)。

- **若只有 apu_mixer 壞**
  → 集中查 APU/DMC 路徑與全域 shim(`DmcLatchShim` 等)在新 DLL 上的行為。

- **無論結果為何,建議加一道防呆**:
  在 `run_tests.py` 的 `build_engine()` 之後,**自動跑一次 golden checksum 並比對**;不符就 abort,不要讓一整輪 8 小時的回歸跑在一顆行為已變的引擎上。

---

## 8. 為什麼這件事重要(影響)

- 本專案的**核心賣點就是 bit-exact / 決定性**(跨 C#/Rust、跨 ISA、跨機器 checksum 相同)。一次「原始碼沒變、重建後行為變了」就直接動搖這個前提。
- **效能數據不可比**:145/1 的效能數字是在 `76b36f5` 引擎上取得的。若現行引擎行為已變,拿它跑出來的 khc/s 與 145/1 **不是同一個東西**,不能放進同一張排行榜/報告。
- **145/1 的分數目前無法重現**(至少 apu_mixer 4 個不會過)。

---

## 9. 證據與檔案位置

| 東西 | 路徑 |
|---|---|
| **145/1 基準結果**(全 PASS) | `tools/testrom/out/archive_20260708_1955/results/` |
| **ba33c36 那輪的 12 筆**(apu_mixer 全 PASS,det=6000) | `tools/testrom/out/archive_20260708_2259/results/` |
| **現行失敗結果**(4×apu_mixer timeout det=none) | `tools/testrom/out/results/apu_mixer__*.json` |
| 最早發現的異常 dmc(存證) | `tools/testrom/out/dmc_ANOMALY_2309run.json` |
| apu_mixer 引擎 log(可見 shim arm 與最後 TIMEOUT 行) | `tools/testrom/out/logs/apu_mixer__*.log` |
| AprNes oracle 校準(721/1159/972/608) | `tools/testrom/calibration_ref.json` |
| 診斷批次 log | `tools/testrom/out/apu_mixer_diag.log` |
| 自動化託管腳本(已停) | scratchpad `orchestrate.py`;log `tools/testrom/out/orchestrator.log` |

**環境**:`.NET SDK 11.0.100-preview.4.26230.115`;DLL build 時間 `2026-07-08 22:59:27`;
引擎最新 `.cs` mtime `2026-07-07 01:13`(**早於 DLL 一天,證明源碼未改而 DLL 被重建**)。

---

## 10. 補充:今晚另外兩個(與本 issue 無關但值得記)的發現

### 10.1 基礎設施 bug:PowerShell `*>>` 會讓 runner 卡死(已修法)

用工具在背景以 `python run_tests.py *>> log.txt` 啟動時,PowerShell 是站在中間「讀 stdout 再寫檔」的角色。
**Claude Code / 該 PowerShell 一被關掉,python 的 stdout 管線就沒人收 → 塞滿 → runner 每跑完一個測試要印完成訊息時 `print()` 卡住 → worker 停在那裡不再領新測試**,lane 一個一個掉光,但 python 還活著(看起來像「還在跑」)。

- **症狀**:log 時間戳凍結,結果檔卻還在增加;dotnet 數量只減不增。
- **正解**:給 python 一個**真正的檔案 fd**(例如 `subprocess.Popen(..., stdout=open(f,'w'))`),或用 `Start-Process -RedirectStandardOutput`。**不要**讓一個會被關掉的行程當中間讀取者。
- 這個 bug 害掉了 20:00 那輪回歸(跑到 22:50 才發現)。

### 10.2 使用者自己雙擊 bat 執行是安全的

`tools/testrom/run_full_regression.bat` 在自己的 console 直接輸出,不經過會斷的管線,關掉 Claude Code 也不受影響(關掉那個黑視窗才會停)。

---

## 11. 目前狀態(交接當下)

- ✅ **沒有任何測試/orchestrator 在跑**(0 個 dotnet、0 個 python)。
- ✅ **沒有關機在進行**(自動關機已被攔下、`shutdown /a` 已執行)。
- ✅ 所有證據已保留(見 §9),`out/results/` 目前只有那 5 筆(4 apu_mixer + cpu_timing)。
- ⛔ **尚未執行**:golden checksum 比對(步驟 1)、class-A 爆炸半徑測試(步驟 2)。
- ⛔ **不要**在解決本 issue 前啟動全量回歸拿效能數據。

**建議第一件事:跑步驟 1 的 golden checksum。** 幾秒鐘就能知道 bit-exact 有沒有破,那會直接決定後面所有方向。
