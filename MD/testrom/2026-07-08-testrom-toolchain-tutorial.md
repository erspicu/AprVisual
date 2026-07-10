# 測試 ROM 工具鏈完整教學 —— 從一鍵到成績單

> 教學向文章。帶你把 AprVisual.S1 的**硬體測試 ROM 驗證流程**從頭跑一次:懶人一鍵 bat、
> 手動細部操作、結果怎麼讀、怎麼自己驗證單一測試、以及整套怎麼**重現**。測試 ROM 已一起
> 打包進本 repo,clone 下來就能跑,方便任何人親自驗證整個過程。
> English version: `2026-07-08-testrom-toolchain-tutorial.en.md`。

## 0. 這是什麼、你會得到什麼

AprVisual.S1 是把 NES 兩顆晶片(2A03 CPU、2C02 PPU)**逐電晶體**模擬的開關級引擎。我們拿它
去跑標準的 NES 精確度測試 ROM(blargg 系列等),全自動判定、產生一份**互動式成績單網頁**。

跑完你會得到:
- `tools/testrom/out/results/*.json` —— 每個測試一筆結果(PASS/FAIL + 時間戳 + 半週期數 + checksum)。
- `tools/testrom/out/screenshots/` —— 每個測試的判定當下畫面。
- `WebSite/Report/index.html` —— **成績單網頁**:每測截圖、判定方式、幀數、per-test 吞吐、
  聚合效能數據、硬體模型說明、忠實偏差卷宗。

目前基準成績:**146 / 1(99.3%,147 測)**,唯一 FAIL 是一個忠實偏差(見報告頁卷宗)。
完整 147 顆的全量回歸,在 7 顆綁定核心上約 **6.2 小時**無人值守跑完。

## 1. 事前準備

| 需要 | 版本 / 說明 |
|---|---|
| **.NET SDK** | .NET 11(引擎目標框架 `net11.0`;`dotnet --version` 應為 11.x) |
| **Python 3** | 跑 `run_tests.py` / `build_report.py`(標準庫即可;報告站截圖轉 WebP 需要 `pillow`,沒有就退回 PNG) |
| **OS** | Windows(bat + `--pin` 綁核走 Windows affinity;Linux/macOS 可跑 python,但綁核部分需自行調整) |
| **測試 ROM** | **已內建**在 `tools/testrom/roms/`(見 §8),無需另外下載 |

第一次可先確認引擎能建置:`dotnet build src/AprVisual.S1 -c Release`(一鍵 bat 會自動做這步)。

## 2. 工具鏈地圖

```
tools/testrom/
├── catalog.json            測試目錄:每個 ROM 的 suite/檔名/class/幀數預算/判定方式
├── roms/                   內建的測試 ROM(147 顆,含各 suite readme.txt 出處)
├── run_tests.py            主 runner:建置引擎 → 平行跑全部測試 → 產生報告
├── build_report.py         把 out/ 的結果組成 WebSite/Report/ 成績單
├── calibrate_ref.py        用行為層模擬器 AprNes 當 oracle,校準各測的幀數預算
├── run_full_regression.bat 【懶人一鍵】跑完整流程(建置→回歸→報告)
├── archive_old_results.bat 【打包舊資料】把上一輪結果封存,下一輪從乾淨目錄開始
├── archive_old_results.ps1 (上面 bat 呼叫的 PowerShell 實作)
├── build_report_only.bat   只從現有結果重建報告頁(不重跑測試)
└── out/                    執行產物(gitignore):results / screenshots / logs / archive_*
```

## 3. 懶人路線 —— 一鍵跑完整套

雙擊 **`tools/testrom/run_full_regression.bat`**(或在終端機執行)。它會:

1. `dotnet build`(Release)重建引擎。
2. 從 `catalog.json` 讀全部測試,用 **7 條綁核 worker** 平行跑(**最長的測試先跑**,見 §5)。
3. 全部跑完後自動呼叫 `build_report.py` 產生 `WebSite/Report/`。

> ⏱️ **這會跑好幾個小時**(開關級模擬很慢,~5 秒/模擬幀;完整一輪約 8 小時)。中途 Ctrl+C 可中止。
> 跑完打開 `WebSite/Report/index.html` 看成績單。

想從乾淨狀態開始,先跑 §4 的封存 bat,再跑這支。

## 4. 打包舊測試資料(下一輪從乾淨開始)

雙擊 **`tools/testrom/archive_old_results.bat`**。它會把 `out/` 裡的 `results/`、`screenshots/`、
`logs/` 整批搬到 `out/archive_<時間戳>/`,讓下一輪回歸從**空目錄**開始 —— 報告的**時間與效能
數據就不會混到舊批次**(報告本身也只採「最新連續批次」,但實體隔離最保險)。

> 內部實作是 `archive_old_results.ps1`(PowerShell)。跑之前請確認**沒有 runner 正在跑**
> (別疊兩個 runner 實例,會互搶核心)。

## 5. 手動操作(細部控制)

一鍵 bat 其實就是包一層 `python tools/testrom/run_tests.py`。直接呼叫可加參數:

```
python tools/testrom/run_tests.py                 # 預設:7 workers、全 catalog、resume(已有結果的跳過)
python tools/testrom/run_tests.py --jobs 4        # 少一點 worker
python tools/testrom/run_tests.py --filter apu    # 只跑 suite/檔名含 "apu" 的
python tools/testrom/run_tests.py --filter oam,ppu # 逗號 = OR,合成「一個批次」跑滿核心
python tools/testrom/run_tests.py --class A-r      # 只跑某個 class(A / A-r / C)
python tools/testrom/run_tests.py --limit 4        # 只跑前 4 個待跑的(冒煙測試)
python tools/testrom/run_tests.py --rerun          # 忽略既有結果,全部重跑
python tools/testrom/run_tests.py --report-only    # 不跑測試,只從現有結果重建報告
python tools/testrom/run_tests.py --no-build       # 跳過 dotnet build
python tools/testrom/run_tests.py --no-canary      # 跳過開跑前的引擎 canary(約 70 秒)
```

一輪跑起來會發生什麼(引擎/排程的關鍵設計):

- **開跑前 canary(約 70 秒)**:build 完、派工前先驗兩件事 —— 300k **golden checksum**
  (`0x794A43ABDF169ADA`,bit-exact 閘)與一顆短的 `$6000` 測試(`11-special`,第 11 幀出判定)。
  **任一不符直接 abort**,不讓 6 小時的回歸跑在壞掉的引擎或判定路徑上。`--no-canary` 可略過。
- **LPT「最長優先」排程**:依 `typicalFrames`(退回 `maxFrames`)由長到短派工,讓長測與短測
  並行、尾端不會拖長總時間(makespan)。
- **綁核**:每個 worker 綁一顆**實體核心**(邏輯核 `[2,6,10,14,4,12,8]`,避開 core 0 的 OS 噪音),
  預設 7 條 lane。worker 之間錯開 **10 秒**啟動(netlist 組裝是最吃記憶體的階段)。
- **每測預算**:上限取 `min(maxFrames, 1.5×typicalFrames+5)` —— 正常測試遠在預算內出判定,
  掛住/退化的測試約 1.5 倍就被砍掉。另有牆鐘守門(`maxFrames×10+600` 秒)。
- **可重現對齊**:`--reset-hold-extra 1`(K=1)把上電 CPU-PPU 時脈相位釘在一個 blargg 校準的相位,
  所以同一輪跑幾次判定都一致。
- **全域測試模式 shim**:少數 netlist 算不出的正確行為由**測試模式行為層 shim** 補上(全域啟用);
  引擎預設(benchmark)路徑掛不掛都逐位元相同,golden checksum 不動。詳見
  [別動被測物:探針效應與儀器級 shim](2026-07-08-probe-effect-instrument-grade-shims.md)。

## 6. 驗證單一測試(最快的親手驗證)

用同一套機器只跑一個測試:

```
python tools/testrom/run_tests.py --filter 10-even_odd_timing --limit 1 --no-build
```

看 `out/results/ppu_vbl_nmi__rom_singles__10-even_odd_timing.nes.json` 的 `status`,
截圖在 `out/screenshots/...`。

也可以直接呼叫引擎(繞過 runner):

```
dotnet run --project src/AprVisual.S1 -c Release -- --test tools/testrom/roms/ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes --system-def-dir AprVisualBenchMark/data/system-def --reset-hold-extra 1
```

退出碼 0 = PASS。加 `--test-screenshot out.png` 可存判定畫面。

## 7. 讀結果 / 讀效能數據

每筆結果 JSON 的重點欄位:

| 欄位 | 意義 |
|---|---|
| `status` | `pass` / `fail`(報告據此計分) |
| `startedAt` / `finishedAt` | 行程起訖(牆鐘;可做並行/甘特分析) |
| `elapsedSeconds` | 該測牆鐘秒數 |
| `core` | 綁的邏輯核(= worker lane) |
| `hc` / checksum 類 | 模擬半週期數 / 狀態 checksum(可跨機比對 bit-exact) |

報告頁的三種吞吐口徑(都在 `build_report.py` 算,原始碼有註解):
- **加權平均**(單測):總 hc ÷ 總引擎牆鐘 —— 單一 worker 的原始速度。
- **campaign 聚合**:總 hc ÷ 整段時間 —— 含 lane 閒置(錯開起跑、測間空檔、尾端排空)。
- **穩態**:每忙碌秒速率 × 尖峰 lane 數 —— 扣掉起跑錯開後的實際持續吞吐。

## 8. 重現性與內建的 ROM 打包

為了讓任何人都能親自驗證整個過程,本 repo **內建了我們實際測到的 147 顆測試 ROM**,放在
`tools/testrom/roms/`(依 suite 分目錄),`catalog.json` 的 `romBase` 就指向這裡 —— **clone 下來
不必再去找 ROM**。

- **來源**:[christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms) 合集,
  原作者為 blargg 等人;各 suite 的 `readme.txt`(作者原文/出處)一併打包。這些測試 ROM 一向
  可自由散布、供硬體/模擬器驗證之用。詳見 `tools/testrom/roms/ATTRIBUTION.md`。
- **範圍**:僅 NROM/CNROM、NTSC(AprVisual.S1 的 cartridge scope)。
- **決定性**:K=1 對齊 + 全域 shim + 每幀判讀,所以同一輪重跑判定一致,checksum 可跨機/跨 ISA 比對。

## 9. 校準幀數預算(進階,通常不必動)

`catalog.json` 每個測試的 `typicalFrames` / `maxFrames` 是「預算」。它們由
`calibrate_ref.py` 用我們的行為層模擬器 [AprNes](https://github.com/erspicu/AprNes) 當**快速
oracle** 量出「這個 ROM 大概第幾幀出判定」,再抓 1.5× 當守門。只有在新增測試或改判定方式時才需要
重新校準;一般驗證直接用現成預算即可。

## 10. 疑難排解

- **`MSB3021 / DLL 被鎖`**:有殘留的 `dotnet` 佔著 DLL。關掉所有 `dotnet` 行程再跑,或加 `--no-build`。
- **`UnicodeDecodeError`(cp950)**:舊版擷取 build 輸出時的解碼雜訊,已修(UTF-8 容錯);
  若自行改動請沿用 `encoding="utf-8", errors="replace"`。
- **別疊兩個 runner**:兩個 runner 同時跑會互搶那 7 顆核心、彼此餓死。一次只跑一個。
- **報告是空的 / 大半 pending**:代表 `out/results/` 沒有結果 —— 先跑一輪,或用 `--report-only`
  對「已有的結果」重建。
- **一堆測試 `detection=none` / `budget exhausted, no $6000 signature`**:代表引擎**沒看到 blargg 簽章**,
  通常是 `$6000` work RAM(`cart-extraram`)沒掛上,**不是幀預算太緊**(加幀數只會讓錯誤跑更久)。
  test mode 現在會自動掛(`ForceExtraRam`),若你改過引擎才需要檢查。快速確認:
  `dotnet <dll> --test tools/testrom/roms/nes_instr_test/rom_singles/11-special.nes --max-frames 40 ...`
  應得到 `detection=6000, frames=11`。(這正是 2026-07-09 踩過的坑,見知識庫 §3.4。)
- **不要鎖頻**:本專案量測不靠鎖頻;跨 8 小時的聚合吞吐已足夠穩定。

---

**延伸閱讀**
- [測試修復知識庫(總綱)](00_測試修復知識庫_總綱.md) —— FAIL 三分類、引擎語意極限、修復總表、方法論
- [別動被測物:探針效應與儀器級 shim](2026-07-08-probe-effect-instrument-grade-shims.md) —— shim 怎麼掛才不弄壞別的測試
- [忠實偏差深入解說 Q&A](2026-07-05-faithful-deviation-qa.md) —— 那個「該紅才對」的唯一殘留 FAIL
- 成績單:[線上報告](https://erspicu.github.io/AprVisual/Report/)
