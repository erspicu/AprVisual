# AprVisual.S1 CLI 完整參數手冊(繁中版)

> 對象:`src/AprVisual.S1/`(canonical 開關級引擎,headless console)。
> 執行方式:`dotnet run --project src/AprVisual.S1 -- <參數>` 或直接跑建置後的
> `src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll`(`dotnet <dll> <參數>`)。
> 本文以原始碼 `Test/TestRunner.cs` 的解析為準(2026-07-14 盤點);新增參數請同步更新中英兩檔。
> 英文版:`S1_CLI_reference_EN.md`。

---

## 1. 執行模式(入口,擇一)

| 參數 | 引數 | 說明 |
|---|---|---|
| `--benchmark <rom>` | ROM 路徑 | 效能量測模式:跑固定 half-cycle 數,輸出 hc/s 與 **NodeStates checksum**(黃金驗證用) |
| `--test <rom>` | ROM 路徑 | 單顆測試 ROM:跑到判定(blargg `$6000` 簽名 / 各種 verdict 旗標)或預算耗盡,回傳 exit code |
| `--test-dir <dir>` | 目錄 | 批次跑整個測試 ROM 目錄 |
| `--rom <rom>` | ROM 路徑 | 一般載入(配合畫面輸出/診斷參數用) |
| `--selftest` | — | 內建自我測試(手工小 netlist) |
| `--help` / `-h` / `/?` | — | 使用說明 |

## 2. 模擬環境配置

| 參數 | 引數 | 說明 |
|---|---|---|
| `--system-def-dir <dir>` | 目錄 | `.js` 系統模組定義的位置(通常 `AprVisualBenchMark/data/system-def`) |
| `--extra-ram` | — | 強制掛載 cart-extraram($6000-$7FFF RAM)。**注意:AC 測試量的是該區 open bus,`--ac-verdict` 會自動停用它** |
| `--region <ntsc\|pal>` | 區域 | 選擇區域(預設 ntsc) |
| `--joypad` | — | 掛上行為層手把 + u7/u8 tie-rewire。手把/執行空間類測試必開;**載入期圖變更會重擲對齊彩票**(Socket Pattern) |
| `--reset-hold-extra <K>` | 整數 | reset 多按 K 個 hc(開機 CPU/PPU 相位對齊實驗;掛牌配置 K=1) |
| `--pin <core>` | 核心編號 | 綁定熱執行緒到指定邏輯核心 + High priority + 關 EcoQoS(降 variance;預設 OFF) |
| `--no-lower` | — | 停用 load-time lowering(mapped-checksum 閘的 A/B 用) |
| `--fast-path` | — | 無作用(S1 恆開,保留相容) |

## 3. 測試判定(verdict)

| 參數 | 引數 | 說明 |
|---|---|---|
| `--ac-verdict` | — | AccuracyCoin 判定:讀 CPU RAM `$07F0` 完成塊(`DE B0 61` + passed/total/skipped);自動停用 extra-ram;含風暴感知(PC 停 $06xx 120 幀 → ac-storm 判定 + 傾印結果表);判定時傾印 `$0400-$04FF` 全表 |
| `--screen-verdict` | — | B 類判定:偵測畫面 PASS/FAIL 文字 |
| `--pass-marker <txt>` | 文字 | 自訂 B 類 PASS 字串 |
| `--expected-crc <crc>` | CRC(可多值) | C 類判定:最終畫面 CRC 接受集 |
| `--max-wait <sec>` | 秒 | 判定等待上限 |
| `--max-frames <N>` | 幀 | 模擬幀預算。**教訓:預算不足看起來像卡死 —— 先用 AprNes 跑同 ROM 拿幀數基準**(AC 孤立 ROM joyON 建議 ≥70) |
| `--input "<spec>"` | 如 `A:2,Start:6.5` | 腳本化手把輸入(按鍵:秒) |
| `--test-json <path>` | 路徑 | 每測結果 JSON 輸出 |
| `--test-screenshot <path>` | 路徑 | 判定後截圖 PNG |
| `--shot-delay <N>` | 幀 | 判定後等 N 幀再截圖 |

## 4. 快照 / 續跑(窗口回歸的基礎)

| 參數 | 引數 | 說明 |
|---|---|---|
| `--snapshot-frames <N>` | 幀 | 每 N 幀存完整引擎狀態(v2 格式,含 shim 活狀態;CRC-32 校驗;約 380KB/顆) |
| `--snapshot-dir <dir>` | 目錄 | 快照輸出目錄 |
| `--resume <sav>` | 快照檔 | 從快照秒級續跑。**指紋校驗**(NodeCount/電晶體數/ROM CRC/shim 旗標)不符即拒絕 —— 换配置(如 joyON↔joyOFF)不能互 resume |

**快照窗回歸配方**(驗證一個修正、幾分鐘):
```
dotnet <dll> --test AC.nes --ac-verdict <標準旗標> --max-frames 300 \
  --resume <run快照>/state_f000200.sav --snapshot-frames 50 --snapshot-dir out/
# 然後用 tools/testrom/ac_snap_results.py --snap out/state_f000300.sav 讀表、對基準 diff
```

## 5. 進度回報

| 參數 | 引數 | 說明 |
|---|---|---|
| `--progress-frames <N>` | 幀 | 每 N 幀寫 checkpoint(含模擬中 PC 的 telemetry) |
| `--progress-dir <dir>` | 目錄 | checkpoint 輸出目錄 |

## 6. 效能量測

| 參數 | 引數 | 說明 |
|---|---|---|
| `--bench-hc <N>` | half-cycles | benchmark 跑多少 hc(黃金驗證:300000 → `0x794A43ABDF169ADA`;1M → `0x6D4CCBCE2E9CD599`) |
| `--log-dir <dir>` | 目錄 | benchmark JSON log 輸出 |
| `--dump-states <path>` | 路徑 | bench 後傾印全節點狀態(A/B 逐節點 diff) |
| `--array-footprint` | — | 印熱 unmanaged 陣列 base+size(IBS/SPE 分桶) |
| `--payload-hist <path>` | 路徑 | NodeInfo inline-payload 大小分佈 |
| `--fc-taint-stats <path>` | 路徑 | same-state-prune 適格性統計(診斷) |

**黃金驗證配方**(任何 WireCore 熱路徑改動後必跑):
```
dotnet <dll> --benchmark AprVisualBenchMark/roms/full_palette.nes --bench-hc 300000 \
  --extra-ram --system-def-dir AprVisualBenchMark/data/system-def
# checksum 必須是 0x794A43ABDF169ADA
```

## 7. Shim 開關(test-mode 行為墊片;benchmark 路徑一律不載)

| 參數 / 環境變數 | 說明 |
|---|---|
| `--no-shims` | 停用全部 test-mode shim |
| `--no-alu-shim` | A/B:ALU 輸入 latch hold shim(LXA/LAE 家族) |
| `--no-dbl2007-shim` | A/B:`$2007` 雙讀合併 shim |
| `--no-ppu-ale-read-feedback-shim` | A/B:暴露原始 ALE+RD 回饋迴圈($2007 Stress 震盪) |
| `--oam-dma-ppu-bus-shim` / `--no-oam-dma-ppu-bus-shim` | `$4014` OAM 寫資料自 PPU I/O bus 保持(預設開) |
| `--ppu-write-delay <N>` | `$2001` 寫入生效延遲 N hc(even_odd 戰役;窄窗 vpos261/hpos338-339) |
| `--callback-drain-limit <N>` | 非收斂偵測:callback 排水超限 → 帶證據失敗而非卡死(AC 標準配置用 2000) |
| 環境 `NO_OB_SHIM=1` | A/B:停用 open-bus last-transferred-byte 重放 shim |
| 環境 `NO_DL_SHIM=1` | A/B:停用輸入資料 latch φ2 透明 shim($4016/$4017) |
| 環境 `NO_ABORT_SHIM=1` | A/B:停用 DMC `$4015` 延遲中止 shim(retire-early + 邊界格點) |
| 環境 `OB_DEBUG=1` | 開 test-only 取證探針群([ob]/[obshim]/[dl]/[dma]/[pcm]/[a5]/[fin] 等;部分帶硬編 Time 窗,屬 TEMP 診斷) |
| 環境 `LAE_DEBUG=1` | LAE shim 取證輸出(讀取環、合併值推導) |
| 環境 `ODMA_DEBUG=1` | OAM-DMA-PPU-bus shim 取證輸出 |
| 環境 `PB_DEBUG=1` | PPU ALE/read feedback shim 取證輸出 |
| 環境 `PWD_DEBUG=1` | `$2001` 寫入延遲 shim 取證輸出 |

> 註:全專案 CLI 解析唯一入口 = `Test/TestRunner.cs`(`WireCore.Parse.cs` 是 netlist `.js`
> 模組解析器,無 CLI 開關);環境變數以 `GetEnvironmentVariable` 全域掃描盤點。

## 8. 診斷探針(取證工具箱)

| 參數 | 引數 | 說明 |
|---|---|---|
| `--dump-node <name>` | 節點名 / `@<id>` | 解剖單節點:pullup、被它 gate 的電晶體、channel 端電晶體。`@id` 直接用引擎 id(未命名節點)。**注意:此模式不掛 raw-id 別名(`cpu.#nnnn`),別名只在 test 模式可解析** |
| `--dump-module <name>` | 模組名 | 傾印模組定義 |
| `--dump-system` | — | 傾印系統組成 |
| `--names <id1,id2,...>` | id 清單 | 引擎 id → 名稱(保留名稱映射的 LoadSystem) |
| `--watch <spec>` | 節點名逗號清單 | 每幀印節點值(配 `--micro`);快照 resume 後也可用 |
| `--micro <path>` + `--micro-frames <N>` | 路徑, 幀數 | 跑 N 幀(預設 3)傾印工作 RAM `$0200-$07FF` |
| `--trace <path>` + `--cycles <N>` | 路徑, 週期 | 傳統 trace 欄位輸出 |
| `--bus-trace <path>` | 路徑 | DMC 匯流排顯微鏡 |
| `--op-probe <path> <hexaddr>` | 路徑, 位址 | AB 命中指定位址時做 hc 級 datapath 匯流排記錄 |
| `--rdy-probe <path>` | 路徑 | rdy 轉換計數 |
| `--phase-probe <path>` | 路徑 | 每 hc 時鐘相位傾印 |
| `--probe2002 / --probe-vbl <path>` | 路徑 | `$2002` / vblank latch 路徑追蹤 |
| `--probe-2001 <path>` | 路徑 | `$2001` 寫入生效路徑(M2 量測) |
| `--probe-dma <path>` | 路徑 | OAM-DMA 位址匯流排 + open-bus 追蹤 |
| `--ppu-memory-trace <lo-hi>` + `--ppu-memory-trace-x <X>` | PC 範圍, X 值 | CPU PC 在範圍內時追蹤 CHR/VRAM callback(可再用 X 過濾) |
| `--ac-dump-work` | — | 傾印 AC 工作/結果 RAM(oracle 比對) |

**探針三鐵律**(2026-07-14 戰役學費,寫探針前必讀):
1. **PC 取樣要連續兩拍同值才可信** —— JSR/RTS 執行中 PC 暫存器半更新會產生單半週期瞬態假象。
2. **「預算耗盡」≠「卡死」** —— 先用 AprNes 跑同 ROM 拿幀數基準再下結論。
3. **匯流排交易的值取樣「最後一個半週期」** —— 首半週期的 db 是還掛在匯流排上的運算元位元組。

## 9. 畫面輸出

| 參數 | 引數 | 說明 |
|---|---|---|
| `--screenshot <path>` + `--frames <N>` + `--out <path>` | 路徑, 幀 | 跑 N 幀後截圖 |
| `--frame-dump <rom>` + `--frame-count <N>` + `--out-dir <dir>` | ROM, 幀數, 目錄 | 逐幀 PNG 傾印(含進度/計時) |
| `--ppu-dump <path>` | 路徑 | PPU 狀態傾印 |

---

## 10. 常用配方速查

**AC 孤立 ROM 標準配置**(重現/驗證一顆測試,2-25 分鐘):
```
dotnet <dll> --test AprAccuracyCoinUnattended/AccuracyCoin_<Test>.nes --ac-verdict \
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin 2 \
  --system-def-dir AprVisualBenchMark/data/system-def --max-frames 70 [--joypad]
```

**掛牌跑(正式完整跑)配置**:
```
dotnet <dll> --test AprAccuracyCoinUnattended/AccuracyCoin.nes --ac-verdict --joypad \
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin 2 \
  --system-def-dir AprVisualBenchMark/data/system-def \
  --snapshot-frames 10 --snapshot-dir <dir> --progress-frames 600 --progress-dir <dir>
# joyON 估 10-15 小時;數字跑完才入帳(ReportAC 政策)
```

**Shim A/B 隔離**(疑似 shim 副作用時):
```
NO_OB_SHIM=1 NO_DL_SHIM=1 NO_ABORT_SHIM=1 dotnet <dll> --test ... (全關)
# 再逐一開回,鎖定嫌疑;netlist 級 shim 用 --no-* 旗標
```

**單 runner 鐵律**:同一時間只跑一個模擬實例(撞核餓死);benchmark 期間不 build(DLL 鎖)。
