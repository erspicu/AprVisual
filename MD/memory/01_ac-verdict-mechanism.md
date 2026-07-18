# AccuracyCoin 無人版:判定機制(記憶體判讀,不是看畫面)

來源:`AprAccuracyCoinUnattended/README.md`(無人版 fork 說明)、`README_org.md`(upstream 原著,
含錯誤/成功碼)、`AccuracyCoin.asm`、`src/AprVisual.S1/Test/TestRunner.Test.cs`。

## 判決權威 = CPU RAM 的 `$07F0` 完成區塊

ROM 跑完全部測試(`AutomaticallyRunEveryTestInROM`,upstream 邏輯逐位不變)後,自己 tally,把完成
區塊寫進 CPU RAM,然後關 NMI、原地死迴圈(畫面停在結果表)。輪詢魔數,出現即代表跑完、其餘欄位有效:

| 位址 | 意義 |
|---|---|
| `$07F0`–`$07F2` | 魔數 `DE B0 61`(`PostAll...` 寫入) |
| `$07F3` | **通過數**(`PostAllPassTally`)← 掛牌看這格 = 141 |
| `$07F4` | **總數**(`PostAllTestTally`)= 141 |
| `$07F5` | 跳過數(`AllTestMenuTotalSkipped`)= 0 |
| `$0300`–`$04FF` | 每測一個結果 byte |

**為什麼不用 blargg `$6000` 協定**:AC 自己把 `BRK` 寫進 `$6000-$6002` 當 open-bus 測試的防護;
且掛 cart-extraram 會把那region 從 open-bus 變成 RAM、悄悄弄錯 open-bus 測試。所以 S1 的
`--ac-verdict` 模式**自動把 extra-ram 關掉**(`TestRunner.Test.cs:144` `ForceExtraRam = !_acVerdict`)。

## 結果 byte 編碼:奇=通過、偶=失敗

- **通過 = 奇數**(bit0=1)。
- **失敗 = `(錯誤碼 << 2) | 2`**(偶數,bit1=1)。
- 來源:`WebSite/ReportAC/index.html`(掛牌內文)+ `ac_snap_results.py` 註解。
- 用這規則掃結果表:任何偶數 byte = 有測試 FAIL;全奇數 = 目前全過。

## SHA/SHS 多變體:$05 和 $09 都是通過(別誤判)

不穩定指令 SHA($93/$9F)、SHS($9B)有**多個可接受行為**(`README_org.md` Success Codes,line 25
「多個可接受通過行為標藍色數字」):

| 成功碼 | 行為 | 結果 byte `(碼<<2)|1` |
|---|---|---|
| 1 | ABH 同時 AND X 和 A | **$05** ← S1 開關級落點(物理更完整) |
| 2 | 只 AND X | **$09** ← AprNes oracle 落點 |
| 3 | 含 magic 或沒發生 | $0D |

→ 結果表 `$446/$447/$448`(SHA_93/SHA_9F/SHS_9B)= $05,與 oracle 的 $09「不同但都通過」。
**run8 掛牌時這 3 格也是 $05**(ReportAC 內文明講 SHA/SHS「拿的是不同但可接受的變體」)。
oracle 逐位比對會標這 3 格 DIFFERS,**那是預期的、不是 FAIL**。

其他多變體測試(README_org Success Codes):DMA+$2002 Read、DMA+$4016 Read、APU Register
Activation、DMC DMA Bus Conflicts、Implicit DMA Abort、Controller Clocking、PPU Read Buffer、
Address $2004、Sprites on Scanline 0。掛牌 oracle 表記的 variant 例:$45B=$41、$46B=$E1、$476=$41。

## 正牌工具(別土製)

- **哨兵 `tools/testrom/ac_watch.py`**:`--dir <progress-dir> --pid <引擎PID> --every-frames 600`。
  每 600 幀寄 HTML 信附即時截圖;**判決權威 = 引擎寫出的 `AccuracyCoin.json`**(`--test-json`),
  出現才寄 FINAL;進程死寄 ALERT;15 分鐘無進度寄 WARNING。EXPECT_FRAME=4870。
- **中途讀表 `tools/testrom/ac_snap_results.py`**:`--snap <sav>` 或 `--dir <快照目錄>`,
  讀 `.sav` MEMS 的 `$0400-$04FF`,對 oracle 逐位比對。對跑中引擎零干擾。
- 引擎要開 `--test-json <out> --test-screenshot <png>`,哨兵才有判決檔可等。

## 完成時間(實測)
AprNes 二分:完成塊寫在 **frame ≈ 4,870(~81 秒主機時間),141/141、0 skipped**。
S1 開關級 ~6.3 秒/幀單核 → **整套約 8.5 小時**。kill guard 建議 1.5× 完成幀 ≈ 7,305 幀
(掛牌用 `--max-frames 12000` 更寬)。
