# AccuracyCoin test 141 `Internal Data Bus` 分析與交接

> 日期: 2026-07-12  
> 接續: `2026-07-12-AccuracyCoin-formal-run-tail-overrun-handoff.md`  
> 狀態: **正式 141-test 卡死尚未宣告修復；問題位置已確認，短測試已大幅縮小範圍，下一次正式跑所需 telemetry 已補齊。**

## 1. 結論先行

1. 正式 S1 長跑確實停在第 141 項 `Internal Data Bus`，不是 `$2007 Stress` 復發，也不是結果畫面繪製太慢。
2. 最可疑路徑是 `DMASync_Loop`。它反覆讀 `$4000`，平常 open bus 應為 `$40`；精確對齊的 DMC DMA fetch 會把外部 CPU DB 改成樣本值 `$00`，迴圈才退出。
3. CPU open bus 的真機來源確實含類比電荷保存與驅動交接；但現有證據**不能**把問題簡化成「S1 沒有類比 open bus」。第 141 項在 isolated、無 shim、DMC/APU 前置、PPU 前置、正式 final-page pacing 與濃縮正式尾段下都能通過。
4. 目前最高機率是完整前 140 項留下的某個長程 netlist/handler 組合狀態，讓 DMC 已發生卻未在正確相位更新外部 DB，或讓 DMC 根本未發生。兩者必須靠卡死現場的 PC/AB/DB/RDY/DMC 節點區分。
5. **沒有套用 speculative simulator fix。** 將 `$4000` 固定回 `$00`、在 test 141 特判、或廣泛重寫 CPU open bus 都會破壞前面多個 DMA/open-bus 測試，且沒有證據支持。

## 2. 正式卡死位置的證明

正式 run 的 checkpoint 畫面位於:

```text
tools/testrom/out/ac_formal_stopped_5040/shots/
```

關鍵畫面:

| Frame | 畫面 |
|---:|---|
| 4710 | `RUNNING TEST 137 / INSTRUCTION TIMING` |
| 4790 | `RUNNING TEST 141 / INTERNAL DATA BUS` |
| 5040 | 仍是 `RUNNING TEST 141 / INTERNAL DATA BUS` |

frame 4790 到 5040 已超過 250 frames；AprNes 行為層 oracle 的完整 ROM 約 frame 4808-4868 完成並得到 `141/141`。S1 每一 frame 仍正常前進，callback drain guard 沒有觸發，因此這是模擬中 6502 程式等待條件，不是 netlist settle 本身不收斂。

## 3. 為什麼 CPU open bus 是合理嫌疑

### 3.1 ROM 的退出條件

`AccuracyCoin.asm` 的 `DMASync` 在 pre-test 結果為 `$01` 時走 open-bus 同步路徑:

```asm
DMASync_Loop:
    LDA $4000
    BNE DMASync_Loop
```

`LDA $4000` 的 opcode/operand 讀取會先讓外部位址與資料匯流排形成 `$40` 類型的 open-bus 值。DMC DMA 在指定 cycle 從 `$FFC0` 取到 `$00`，下一個 `$4000` open-bus read 應看到 `$00`，Z flag 才成立並離開迴圈。

若模型一直看到 `$40`，這段程式沒有 timeout，會永久停留。這和正式 run 的症狀吻合，但舊 run 沒有保存 emulated PC，所以目前仍是高可信候選，不是已證實的唯一 PC。

### 3.2 真機與兩種模型

- 真機: open bus 是資料線電容保存、漏電、驅動器釋放與另一驅動源接手的結果，帶有類比與製程/溫度差異。
- AprNes 行為層: 明確保存 `cpubus` (外部 DB) 與 `internalBus`。一般 CPU read 同時更新兩者；DMC fetch 只更新外部 `cpubus`；CPU 讀 `$4015` 的 bit 5 取自 `internalBus`。
- AprVisual.S1: 以 `cpu.db[7:0]`、2A03 內部 DB 節點、ROM callback 與 transistor netlist 表示驅動/浮接。離散 settle 對同半週期 race 的解析順序可能和真機類比重疊不同，專案中已有數個窄 shim 處理已證明的 race。

所以「行為層與 switch-level 對 open bus 的抽象不同」確實可能相關；但精確嫌疑是 **DMC DMA 對外部 DB 的相位與保存語意**，不是一般意義的長時間 bus decay。DMASync 只跨數個 CPU cycles，PPU `_io_db` 的約 600 ms decay 也不是同一條 bus。

## 4. `Internal Data Bus` 實際測什麼

原測試有三段:

1. 驗證一般 open bus，並複用 `DMA + Open Bus` 的 DMC 對齊讀取。
2. DMC DMA 和 CPU `$4015` read 同時發生時，外部 DMC byte 不得污染 `$4015` 使用的 internal DB bit 5。
3. `$4015` internal DB 的 bit 5 不得反向污染後續 external open-bus read。

因此即使 `DMASync_Loop` 能離開，後兩段仍可能 FAIL；正式症狀則是連結果 byte `$0490` 都沒有機會寫入，較像同步 helper 未返回，而不是一般 pass/fail 判定。

## 5. 短測試策略與結果

完整矩陣見 `2026-07-12-AccuracyCoin-test141-focused-rom-matrix.md`。高價值結果如下:

| 短 ROM / 條件 | S1 結果 | Frames | 主要排除範圍 |
|---|---:|---:|---|
| isolated IDB | PASS 1/1 | 24 | test 141 本體可在乾淨狀態完成 |
| isolated IDB, `--no-shims` | PASS 1/1 | 24 | 基本成功不依賴現有類比 race shim |
| CPU Behavior 2 同頁順序 | PASS 1/1 | 96 | test 137-140 的一般直接狀態 |
| IDB 重複 16 次 | PASS 1/1 | 53 | 簡單 CPU/APU phase sweep 與重複累積 |
| DMA + APU 兩整頁 19 項後接 IDB | PASS 1/1 | 170 | 原 ROM 的主要 DMC/DMA/APU 前置狀態 |
| `$2007` 最後 95 samples 後接 IDB | PASS 1/1 | 145 | 正式 Stress 尾端與最後 feedback windows |
| 完整 341-sample `$2007 Stress` 後接 IDB | PASS 1/1 | 391 | 完整 Stress 累積狀態 |
| `ALE + Read` + `Hybrid Addresses` 後接 IDB | PASS 1/1 | 119 | test 141 的兩個直接 PPU 前置項 |
| final page 正式 post-VBlank pacing，無 marker | PASS 1/1 | 103 | 診斷 marker 與 outer VBlank phase |
| 濃縮正式尾段，無 marker | PASS 1/1 | 331 | 最後 95 Stress samples + ALE/Hybrid + final page 的交叉狀態 |

濃縮正式尾段在 S1 中還實際觸發 8 次 PPU ALE/read feedback hold，以及一次 `_io_db` decay，最後仍正常返回 IDB。這讓「最後幾顆 PPU 測試直接留下壞狀態」的解釋大幅降權。

所有新增 ROM 也先由 `C:/ai_project/AprVisual/AprNesRef/AprNes` 行為層 CLI 驗證 completion block `DE B0 61 01 01 00`。production `AccuracyCoin.nes` 每次重組後 SHA-256 仍為:

```text
63C6F0DDE6B312964184240E722418C50F5AC48682E785556B9749FA90CD3CA3
```

## 6. 已排除或降權的原因

### 已排除

- 正式 run 其實停在 `$2007 Stress`。
- 引擎 callback queue 再次永久震盪。
- `ALE + Read` / `Hybrid Addresses` 本身尚未返回。
- test 141 isolated 必定失敗。
- 現有 `DmcLatchShim` 是 test 141 能否成功的必要條件。
- 診斷 marker 恰好改變 phase 才造成所有短測假通過。
- `WireCore.Time` 的直接 32-bit overflow。`Time` 與相關 shim deadline/activity timestamp 都是 `long`，未找到 `(int)Time` 截斷。

### 尚未排除

- 前 140 項中更早的某個測試留下動態節點或 callback 驅動狀態，必須和尾段狀態組合才觸發。
- 正式 run 的特定 CPU/APU/PPU 相位是低機率 alignment race；短 ROM 的 phase 集合仍未涵蓋。
- DMC request/RDY sequence 有發生，但 PRG-ROM `$FFC0` byte 沒在正確 settle wave 對 `cpu.db` 可見。
- DMC request 根本未重新 arm，導致 `$4000` 永遠保留 `$40`。
- 第 141 項內另一個 loop，而非第一個 `DMASync_Loop`。新 PC telemetry 會直接判別。

## 7. 無效證據，禁止引用

調查早期有兩類 NESASM 陷阱:

1. helper 從 `$ECC2` 往後寫超過固定 `.org $ED40`，NESASM 沒報錯而由 DPCM sample 靜默覆寫尾端。那些 24-frame `Repeat` / `PPU Tail` PASS 是空跑或提早返回，不是有效證據。
2. 長 symbol 名稱被 NESASM 截斷碰撞，早期 DMC-prime loop 組錯。已改成短且唯一的 labels，並核對 `.fns` 與 machine bytes。

有效 helper 必須位於 `$ECC2` 附近、在 `$ED40` 前看到真正 `RTS`，且原始 `$ED40` sample bytes 保持不變。本文矩陣只列修正後有效 run。

## 8. 本次可見性修正

`src/AprVisual.S1/Test/TestRunner.Test.cs` 的 AccuracyCoin progress JSON 現在額外記錄:

```text
acStage
acPage
acItem
acIteration
cpuPc
cpuAb
cpuDb
resultsFilled
lastResultAddress
lastResultValue
```

timeout 也會輸出 page/item/result progress 與完整 `DumpCpuState()`。這些讀取只發生在 test runner checkpoint，不驅動任何 netlist node。

1-frame smoke test 已用 PowerShell `ConvertFrom-Json` 成功解析；範例:

```json
{"acPage":0,"acItem":0,"cpuPc":32838,"cpuAb":32838,"cpuDb":173}
```

## 9. 下一步，避免再做組合爆炸

1. 不再任意串接更多 predecessor。現有短測已涵蓋 DMC/APU 整頁、完整 Stress、直接 PPU 尾段、final page 與其高價值交叉組合。
2. 下一次值得支付正式 run 成本時，使用新版 telemetry，每 10 frames 保存 page/item/PC/AB/DB/result count。
3. 若停滯時 `cpuPc` 落在 production ROM 的 `DMASync_Loop`，且 `cpuDb`/`cpuAb` 顯示 `$4000/$40`，再加一個只觀測以下節點的窄 probe:
   - CPU `RDY`, `RW`, `AB`, external `DB`
   - DMC DMA request/get/put 與 sample address
   - PRG-ROM callback select/address/data drive
   - 2A03 internal DB
4. 若 DMC fetch 有發生但 external DB 未見 `$00`，修復應落在 ROM handler drive/settle 邊界；若 request 未發生，修復應落在 DMC reload/enable state。兩者不可用同一個 open-bus 特判處理。
5. 若 PC 不在 `DMASync_Loop`，以實際 PC 回到 `.fns` 定位，不再沿用 open-bus 假說。

## 10. 驗收界線

目前可宣告:

- test 141 與多個高風險濃縮序列在 S1 通過。
- 正式卡死已定位到 test 141，且下一次 run 不再是黑盒。
- production ROM 沒有因診斷分支改變。

目前不可宣告:

- 正式 AccuracyCoin 問題已修復。
- S1 已得到 `141/141`。
- CPU open bus 已被證實是唯一根因。

