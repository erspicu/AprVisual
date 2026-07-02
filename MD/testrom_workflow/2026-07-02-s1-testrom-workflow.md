# AprVisual.S1 測試 ROM 驗證工作流

> 建立:2026-07-02。目的:防遺忘 —— 完整記錄「跑 test ROM → 產出報告網頁」的整套流程與設計決策。
> 相關文件:`MD/testrom/2026-07-02-aprvisual-s1-supported-testroms.md`(139 支援清單)、
> `MD/testrom/2026-07-02-s1-testrom-detection-filter.md`(A/A-r/B/C 判定分級)。

## 一句話總覽

```
python tools/testrom/run_tests.py        # 跑完 89 個(A/A-r/C)→ 自動產出 WebSite/Report/
```

跑完後 commit `WebSite/Report/` 即發佈到 GitHub Pages(`erspicu.github.io/AprVisual/Report/`)。

## 範圍與分級(為什麼是 89 個)

- 母集合:`nes-test-roms-master/checked/` 全 184 個 → NROM(mapper 0)且非 PAL = **139**。
- S1 是開關級模擬,**~5 秒 wall-clock / 幀**,所以判定手段刻意排除「連續 90 幀穩定畫面」那種等待型方式:
  - **A(83→79)**:blargg `$6000` 協定 —— 每幀讀一 byte,結果一寫入立即停。**首選**。
    - **apu_mixer 4 個已剔除(2026-07-02)**:讀原始碼確認其 `$6000=0` 只代表「音頻序列播完沒當機」;
      真正的判定(反相波抵銷是否無聲)是**聽覺判斷**(人耳/參考錄音,2A03 沒有混音讀回暫存器)。
      又是最慢的一組(播音 15+ 模擬秒)→ 判定價值最低 × 成本最高,先跳過。
      未來若要真驗混音:S1 可直接 tap 2A03 netlist 的 DAC 輸出節點 dump 波形比對參考錄音(行為層模擬器做不到)。
  - **A-r(8)**:`$6000` + 自動軟重設(`apu_reset/` 6 個 + `cpu_reset/` 2 個;apu_reset 曾短暫剔除後同日加回)—— 協定回 `$6000=$81` 要求重設,引擎等 6 幀後 `WireCore.SoftReset()`(拉 res 線 192 半週期),最多 10 次。
  - **C(2)**:畫面 CRC(`dmc_dma_during_read4/dma_2007_read.nes`、`double_2007_read.nes`)—— 每幀掃 nametable 找孤立 8 位 hex,連續 2 幀相同才採信,與合法 CRC 集合比對。
  - **B(46,暫緩)**:畫面文字型(舊 blargg:sprite_hit、vbl_nmi_timing、blargg_apu_2005 等)。之後做法:每幀掃 nametable 找終端 `Passed`/`Failed`/`$0X` 標記(不等穩定)。**尚未實作**。
- 分級依據 = 靜態掃 `STA $6000` × AprNes 實測 results.json 交叉驗證,0 矛盾。

## 引擎端(`src/AprVisual.S1` 的 `--test` 模式)

```
dotnet AprVisual.S1.dll --test <rom.nes>
    --max-frames <N>          模擬幀數預算(主要上限;預設 900 ≈ 15 模擬秒 ≈ 75 分鐘 wall)
    --max-wait <sec>          wall-clock 安全上限(預設 0 = 停用;主要靠 max-frames)
    --expected-crc <A,B,...>  C 類:合法 CRC 集合(逗號分隔,不分大小寫)
    --test-json <out.json>    寫結構化結果(schema aprvisual-testrom/1)
    --test-screenshot <p.png> 存最終畫面(給報告頁)
    --pin <N>                 熱執行緒鎖邏輯核 N + High priority(runner 會傳)
    --system-def-dir <dir>    netlist 模組目錄(必要!見下)
```

- **system-def 一定要指到 `AprVisualBenchMark/data/system-def/`**(`src/AprVisual.S1/data/` 是空的,data 不 vendor)。
- extra-RAM($6000-$7FFF)靠路徑含 `nes-test-roms` 自動啟用(`WireCore.System.cs` 的 isTestRom);路徑不含時要自己加 `--extra-ram`。
- 偵測邏輯在 `Test/TestRunner.cs` 的 `RunOneTest`:每幀 `RunFrame()` → `CheckUnitTest()`($6000)→ 沒協定且有 `--expected-crc` 才掃 CRC。
- exit code:0=pass、1..125=fail code、3=timeout、2=load error。**報告以 JSON 的 `status` 為準**,別只看 exit code。
- JSON 欄位:status/resultCode/detection(`6000`/`6000+reset`/`crc`/`none`)/resetCount/frames/simSeconds/wallSeconds/halfCycles/engineVersion/screenshot/resultText。

## Runner(`tools/testrom/run_tests.py`)

- **測試清單 = `tools/testrom/catalog.json`**(89 筆,含 class/maxFrames/expectedCrcs)。由 `tools/testrom/gen_catalog.py` 產生;若要重生,依據是 AprNes 的 `site/report/results.json`。
- **4 個 worker、共用佇列**(先到先拿,自然錯開),啟動各錯開 20 秒(netlist 組建是重載階段)。
- **鎖核:邏輯核 2、6、10、14**(3700X:= 物理核 1/3/5/7,兩個 CCX 各 2 個)。
  **刻意避開核 0**(OS 雜訊)。SMT 邏輯對 (2i, 2i+1) = 物理核 i。
- **斷點續跑**:已有 pass/fail 結果的測試自動跳過(timeout 會重跑);`--rerun` 強制全部重來。
- 常用:
  ```
  python tools/testrom/run_tests.py --limit 4          # 煙霧測試(4 個)
  python tools/testrom/run_tests.py --class A-r        # 只跑軟重設那 8 個
  python tools/testrom/run_tests.py --filter instr     # 子字串過濾
  python tools/testrom/run_tests.py --report-only      # 只重建報告頁
  ```
- 輸出:`tools/testrom/out/{results,screenshots,logs}/`(gitignore 掉也可,報告是從這裡再彙整)。
- 每測試 subprocess 有 wall guard(maxFrames×10s+600s)防吊死。

## 報告(`tools/testrom/build_report.py` → `WebSite/Report/`)

- 合併 catalog × out/results → `WebSite/Report/results.json`(schema 與 AprNes 相容:suite/rom/status/exit_code/result_text/screenshot,外加 class/detection/frames/simSeconds/wallSeconds)。
- `index.html` 自足式(結果內嵌,離線可開):統計/進度條/類別與偵測方式徽章/截圖燈箱/套件折疊/搜尋;catalog 有但還沒跑的顯示 **pending**。
- 發佈 = commit + push `WebSite/Report/`(GitHub Pages 從 main 的 WebSite/ 部署)。

## 時間預估(規劃排程用)

- 一幀 ≈ 5 秒 wall(Zen2,~142K hc/s;714,732 hc/幀)。載入(組 netlist+上電)另加 ~2-3 秒。
- 煙霧測試實測:`$6000` 簽章在第 ~4 幀就出現(協定啟動很快)。
- 典型 blargg 單項要模擬 2-10 秒(120-600 幀)→ **每測試約 10-50 分鐘**;89 個 ÷ 4 workers ≈ **一個晚上到一天**。
- 第一輪跑完後,用報告裡的 frames 分佈回頭**調小 catalog 的 maxFrames**(逾時預算目前保守:A=900、A-r=1500)。

## 已知事項 / 待辦

- [ ] 第一輪 89 個全跑(過夜)→ 檢視 pass/fail → 調 maxFrames。
- [ ] A-r 軟重設路徑第一次實跑要盯:`# [test] auto soft reset #N` 行有沒有出現、重設後測試有沒有重新啟動。
- [ ] B 類(46)之後做:`RunOneTest` 加「每幀掃 nametable 終端文字」偵測(`FindNametableCrc` 旁邊加 `FindNametableVerdict`),catalog 加 class B 條目。
- [ ] 失敗的測試 = 真正有價值的發現(switch-level 對 behavioral 的差異),逐一開 MD 記錄。
- 引擎判定與 AprNes 的差異:S1 沒有(也不需要)畫面穩定偵測、手把注入(`read_joy3` 不在 NROM 集內)、PAL(2C07 是另一顆晶片,netlist 層根本不存在)。
