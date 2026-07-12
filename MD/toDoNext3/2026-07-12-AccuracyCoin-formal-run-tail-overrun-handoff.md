# AccuracyCoin 正式 141-test:尾段超時未完成 —— 交接文件

> 日期:2026-07-12。接續 `MD/toDoNext2/`(`$2007 Stress` 震盪)的下一章。
>
> 狀態:**`$2007 Stress` 修復已驗收通過;但正式 141-test 在尾段(frame 4480 之後的處女地)
> 超過預期完成點仍未寫完成區塊,由使用者手動停止。停止當下引擎健康,問題在 ROM 端。**

---

## 0. 一句話

**引擎的震盪修好了、也驗收過了;但完整 ROM 第一次跑進 frame 4480 之後的未知領域,
在最後一兩顆測試(頭號嫌犯:`Internal Data Bus`,DMC DMA + `$4015` 匯流排衝突)
花掉遠超 oracle 的時間仍未完成 —— 而我們對「ROM 正在跑哪顆測試」完全沒有可見性,
這是下一步必須先補的東西。**

---

## 1. 產出進度(已完成、已驗證的部分)

### $2007 Stress 修復 —— 驗收全過 ✅

修復本體(commit `8271069`,已 push;詳見 `MD/toDoNext2/2026-07-12-*-fix-design.md`):
1. `--callback-drain-limit N` 不收斂偵測器(把無聲空轉變成帶現場的 exception)
2. 唯讀 ROM callback 不再 watch 自己驅動的 `d[]`(對齊 MetalNES;修掉自我觸發)
3. `ALE + /RD` 且 ROM 低位元組映射形成長度>1 迴圈時 hold 舊輸出(Floyd 判圈;類比迴路 shim)
4. NROM CIRAM A10 依 iNES header 接線(水平鏡像 → PPU A11;修掉繼承自 MetalNES 的寫死接線)

本次(2026-07-12)獨立驗收結果:

| 驗收 | 結果 |
|---|---|
| Release build | ✅ 0 錯誤 |
| 黃金 checksum @300k(獨立重驗,非只信文件) | ✅ `0x794A43ABDF169ADA` |
| 關卡1:isolated `AccuracyCoin_2007Stress.nes`(341-sample 完整答案鍵) | ✅ **`1/1 passed`**,389 幀,2,015 s |
| 關卡2:`AccuracyCoin_2007Sequence.nes`(PPU Misc 前置+Stress) | ✅ **`1/1 passed`**,815 幀,4,437 s |

→ toDoNext2 交接文件「尚未完成」清單的前兩項已補上。**修復本身是成立的。**

### 正式 141-test —— 突破舊死點,但未完成 ⚠️

- 04:47 乾淨開跑(全新 `out/ac`、`--ac-verdict --callback-drain-limit 2000`、K=1、pinned core 2、
  progress checkpoint 每 10 幀、watcher 寄信)。
- **史上第一次活著穿過 frame 4480**:`$2007 Stress` 的 feedback hold 正常觸發
  (log 可見 X=`$36`…`$4C` 等取樣點,最後一筆 hold 在 t=3.268G hc ≈ frame 4573)。
- 之後持續前進到 **frame 5040**(sim 83.9 s,wall 7.73 h,3.60G hc),`Debug_EC` 仍 `$FF`、
  完成區塊未出現,使用者判斷不正常、手動停止。

---

## 2. 問題狀況

### 症狀

| 指標 | 值 | 對照 |
|---|---|---|
| 停止時 frame | 5,040(仍在每 71 s / 10 幀前進) | AprNes oracle 完成點 ≈ **4,808–4,868** |
| 超出幅度 | +170 幀以上且持續增加 | AprNes 尾段(Stress 完→全完)只要 ~240 幀 |
| S1 尾段已花 | 4573→5040 = **467 幀**(≈7.8 主機秒)未完成 | 約 oracle 的 **2 倍**,且還沒到底 |
| drain-limit(2000) | **從未觸發** | netlist 每個 cycle 都正常收斂 |
| 每幀成本 | 7.1 s(rendering-on 的正常價位) | 不是引擎變慢 |

### 這不是上一章的 bug 復發 —— 證據

停止前抓了 15 秒活體 CPU trace(`evidence/formal-run-at-stop.speedscope.json`,
丟 <https://speedscope.app> 看):

- self-time 集中在 `StepCycle`/`RunFrame` 正常輪轉;15 秒內 `ProcessQueue` 進出 **86 次**、
  `InvokeCallbacks` 45 次、`DoVideo`/`DoMemRead`/shim step 都在動。
- **對照組**:上一章的 netlist 震盪 trace(`MD/toDoNext2/evidence/hang-callback-drain.speedscope.json`)
  是 CPU **釘死在單一次** `InvokeCallbacks → ProcessQueue` 呼叫裡出不來。
- 兩者形狀完全不同。**這次引擎是健康的;是模擬中的 6502 程式自己跑很久/在等某個條件。**

### 為什麼 drain-limit 抓不到這種失效

drain-limit 偵測的是「netlist/callback 層不收斂」。ROM 端的無限等待
(例如測試 polling 一個我們的模型永遠不會產生的旗標/IRQ)在 netlist 層面**每個 cycle 都完美收斂**,
所以 drain-limit 永遠不會響。**這是它防不了的失效模式,需要另一層可見性(見 §5)。**

---

## 3. 分析判斷

### 3.1 停止點在哪顆測試?(頭號嫌犯:`Internal Data Bus`)

執行順序末端(AprNes oracle 量得):`$2007 Stress`(t≈77s 完)→ `ALE+Read`(t≈78s 完)→
最後填的是 `InternalDataBus`(`$0490`)與 `HybridAddresses`(`$0492`),t=78→81s 之間。
**`Internal Data Bus` 是整個 ROM 的最後一顆測試**(Page 20「CPU Behavior 2」收尾)。

它的官方定義(`README_org.md` §Internal Data Bus)正踩我們的行為層地雷區:

> 1. open bus 跨頁讀取須正確,**DMC DMA Timing 須正確**
> 2. **DMC DMA 與 `$4015` 的匯流排衝突**不得影響內部資料匯流排
> 3. `$4015` 讀取只更新內部匯流排,不得影響外部匯流排

旁證:停止前 log 中 `_io_db` decay shim 在 frame 4745、4823 觸發兩次後沉默 ——
一個**不碰 PPU 埠的純 CPU/DMC 測試**才會讓 PPU IO latch 閒置到觸發 decay。時間點吻合。

### 3.2 frame 4480 之後全是處女地

舊版每次都死在 4480,所以 **Page 19 尾段 + Page 20 全部**(StaleSpriteShiftRegs、ALE+Read、
HybridAddresses、Instruction Timing、Implied/Branch Dummy Reads、JSR Edge Cases、Internal Data Bus)
在完整 ROM 情境下**這次是 S1 史上第一次執行**。任何一顆都可能踩到未曾暴露的缺口。
(isolated Stress/Sequence ROM 只覆蓋了 Stress 本身。)

### 3.3 141 顆 vs 行為層缺口 —— 風險地圖

| 行為層近似 / 缺口 | 會踩到的 AC 測試 |
|---|---|
| **DMC 家族**(`DmcLatchShim` = 文件化類比賽跑語意極限;blargg 時代最硬的仗) | Page 13 整頁(DMA+OpenBus / +$2002 / +$2007R/W / +$4015 / +$4016、DMC Bus Conflicts、DMC+OAM DMA、DMA Abort×2)、Page 14 DMC 通道、**Page 20 Internal Data Bus(停止點)** |
| **手把埠**(AC 模式**未開**行為層 joypad;blargg 手把測試當年要 `--joypad`+tie-rewire 才過) | Controller Strobing、Controller Clocking(含 DMC DMA 對 `$4016` 的 clock 干擾) |
| **PPU open bus 衰減**(`_io_db` shim = 整個 latch 一起、36 幀;真機 = **每 bit 獨立** ~600ms) | PPU Register Open Bus |
| **`$2007` 路徑**(`Dbl2007Shim` + 本次新 feedback-hold) | PPU Read Buffer、$2007 Read w/ Rendering、RMW $2007、INC $4014 |
| **OAM**(`OamDmaPpuBusShim`;RP2C02G OAM 損毀 = 已知**忠實偏差**區) | $2004 Stress、Address $2004 Behavior 等 |
| **Frame IRQ**(`FrameIrqShim`) | Frame Counter IRQ / 4-step / 5-step |

**重要提醒:「跑完了」≠「通過了」。** frame 4480 之前那 ~135 顆只知道「有跑完」,
per-test pass/fail 一律看不到 —— 完成區塊只在最後寫。上表 Page 13/14 的 DMC 測試
可能早就默默 FAIL,目前無從得知。

### 3.4 「卡死」還是「只是慢」?—— 誠實答案:未知,略偏向卡死

- 支持「慢」:引擎健康、幀在前進、S1 個別測試本來就比 AprNes 慢
  (isolated Stress:S1 389 幀 vs AprNes 同段 ~300 幀,約 1.3×)。
- 支持「卡死(ROM 端等待)」:若按 1.3× 比例,尾段應在 ~4,890 完成;停止時 5,040 已超出
  推算 150+ 幀且無完成跡象;`Internal Data Bus` 依賴的 DMC 語意正是 shim 近似區,
  「等一個永遠不來的條件」完全可能。
- **判別方法已明確:per-test telemetry(§5)。有了它,重跑一次就能一翻兩瞪眼。**

---

## 4. 可參考的判斷資料

### 隨本文件 commit 的證據(`MD/toDoNext3/evidence/`)

| 檔案 | 內容 |
|---|---|
| `formal-run-at-stop.speedscope.json` | 停止前 15 秒活體 CPU trace —— 引擎健康的證明(對照 toDoNext2 的震盪 trace) |
| `last-checkpoints.txt` | 最後 5 筆 checkpoint(frame 5000–5040,前進中) |
| `shim-tail.txt` | 停止前的 shim log 尾巴(feedback hold 至 X=$4C;decay 4745/4823) |

### 本機保留(gitignored)

| 路徑 | 內容 |
|---|---|
| `tools/testrom/out/ac_formal_stopped_5040/` | 本次正式跑完整資料:504 checkpoint、run log、shots/ |
| `tools/testrom/out/ac_hung_guard/`、`ac_crashed_noguard/` | 上一章的兩次 4480 停止紀錄 |
| `temp/ac/accept_stress.log`、`accept_seq.log` | 兩道驗收關卡的完整輸出 |

### AprNes oracle 基準線(判斷 S1 是否異常的尺)

- 完整 ROM 完成點:frame **4,808–4,868**(~81 主機秒),`141/141`,skipped 0。
- 尾段時間軸:`2004_Stress`/`2002FlagClearTiming`/`StaleSpriteShiftRegs` ≤73s;
  `2007_Stress` 完成於 ~77s;`ALERead` ~78s;`InternalDataBus` + `HybridAddresses` 在 78–81s。
- 量測指令(結果表 dump;base `$0300`,測試結果在 `$0400-$04FF` = hex 字串 offset 512 起):

```bash
AprNesRef/AprNes/bin/Debug/AprNes.exe --rom AprAccuracyCoinUnattended/AccuracyCoin.nes \
  --time <秒> --dump-ac-results   # grep AC_RESULTS_HEX;AC_DONE_HEX 的 $07F0 magic = DE B0 61
```

- 位址對照:`AccuracyCoin.asm` 的 `result_* = $04xx` 定義(147 個);
  尾端:`$48E`=2007_Stress、`$48F`=StaleSpriteShiftRegs、`$490`=InternalDataBus、
  `$491`=ALERead、`$492`=HybridAddresses。

### S1 端觀測事實(本次跑)

- 最後一筆 feedback hold:`t=3268489956`(≈frame 4573,X=`$4C`)。
- `_io_db` decay:frame 4745、4823,之後沉默。
- 最後 checkpoint:frame 5040,simSec 83.86,hc 3,602,204,640,wall 27,820 s。
- drain-limit 2000:全程未觸發。

---

## 5. 建議下一步(依序)

### P0:progress checkpoint 加 per-test telemetry(先做這個,再談重跑)

AC 結果表就在 CPU RAM `$0400-$04FF`,而 `WriteProgress`(`TestRunner.Test.cs`)本來就拿著
`acRam`(`u1.ram`,2KB 涵蓋 `$0000-$07FF`)。每個 checkpoint 多記:

- `resultsFilled`:`$0400-$04FF` 非零 byte 數(≈ 已完成測試數)
- `lastResult`:最高的已填位址(= 最近完成的測試)

效果:進度信直接顯示「跑到第幾顆」;一旦停滯**當場知道卡在哪顆**;中途停掉也有「到目前為止的結果」。
唯讀、test-mode only、幾行 diff。改完照例驗黃金 checksum。

### P1:先用 AprNes 把 `InternalDataBus` 的預期行為/時長量清楚

細切 t=78.0→81.0(0.5s 步進)看 `$490` 何時翻非零、以及它的 result code 是多少 ——
它有多個可接受行為(success codes),知道 oracle 拿哪個 code 對後續 debug 很重要。

### P2:帶著 telemetry 重跑正式 141-test

預算照舊 7,305 幀。有 telemetry 後:
- 若真卡在 `InternalDataBus` → 當場確認,轉 P3。
- 若只是慢 → 等它跑完,順便拿到完整 per-test 成績單(第一次!)。

### P3(若確認卡死):做 isolated `InternalDataBus` 診斷 ROM

照上一手的成功模式(`AccuracyCoin_2007*.nes` 系列,見 fork repo `a4bc4e0`):
保留 power-on/menu/timing pre-test,直接跳到目標測試。把 6.9 小時重現壓到分鐘級,
然後用 `--bus-trace`/`--rdy-probe`(DMC #19 戰役留下的顯微鏡)看它在等什麼。

### 標準守則(每次都要)

- 動過 `WireCore` 熱路徑 → 重驗黃金 checksum `0x794A43ABDF169ADA` @300k(`--extra-ram`)。
- **不要**把 141/141 寫成 S1 成績(那是 AprNes 的);官網 §03·5 AC-PANEL 保持空白直到 S1 真的跑完。
- 正式 AC 跑不掛 cart-extraram(open bus 測試);`--callback-drain-limit 2000` 保險照帶。
- 一次只跑一個 runner;commit 後 push。

---

## 6. Repo 狀態(交接時點)

- **AprVisual main**:與 origin 同步。引擎修復 `8271069`、toDoNext2 文件 `f30b91d`、
  本文件(本 commit)皆已 push。工作區僅剩 `calibration_ref.json` 時間戳一行 + 使用者自己的
  `MD/suggest/` 筆記(不動)。
- **AprAccuracyCoinUnattended fork**:與 origin 同步(`a4bc4e0` 含 6 顆診斷 ROM)。
- **AprNesRef**:有 local-only commit,**刻意不推**(oracle 參考,非產出)。
- 沒有任何測試行程在跑;監視器已停。
