# AccuracyCoin 無人值守化 —— 首次嘗試記錄(暫停)

> 2026-07-02。狀態:**暫停,未成功**。此筆記保存已確認的事實與死路,供日後重試。

## 目標

把 [100thCoin/AccuracyCoin](https://github.com/100thCoin/AccuracyCoin)(141 個 NES 精確度測試,單一 NROM ROM,
菜單+手把操作)改造成無人值守:開機自動跑全部測試 → 完成信號寫 RAM → S1/AprNes 輪詢判定,不需手把。

## 已確認的事實(值錢的部分)

- **ROM 是 mapper 0(NROM,PRG 2×16K + CHR 8K),為 RP2A03G/RP2C02G NTSC 設計 —— S1 跑得動。**
- ROM 內建 `AutomaticallyRunEveryTestInROM`(頁首按 Start 的路徑):跑完全部測試、畫結果表、
  算 `PostAllPassTally` / `PostAllTestTally`,**最後 RTS 返回呼叫者**(等 Start 離開結果表的迴圈掛在
  NMI 的 $700 跳板上,不阻塞)→ 理論上 boot 端 `JSR` 它、返回後寫完成信號即可。
- **NMI 向量指向 `$0700`(RAM 跳板)**:`.word $0700`(asm 尾端 $FFFA 區)。跳板由
  `SetUpNMIRoutineForMainMenu` 在 boot 後段設定;各測試也會改寫 $700 指向自己的 NMI handler。
- boot 順序(ReloadMainMenu 起):`SetUpNMIRoutineForMainMenu` → **寫 $6000-6002 = BRK**(open-bus 防禦)
  → `TEST_VblankSync_PreTest` + `DMASync`(**pre-test,結果被正式測試引用,必須先跑**)→ 菜單設定。
- 完成信號設計(維持有效):CPU RAM `$07F0-F2 = DE B0 61` 魔術 + `$07F3` pass / `$07F4` total / `$07F5` skipped;
  per-test 結果在 `$0300-$04FF`(AprNes `--dump-ac-results` 已會讀;AprNesRef 另加了 `AC_DONE_HEX` dump $07E0-$07FF)。
- **不要用 $6000 blargg 協定**:(1) ROM 自己會把 $6000-6002 寫成 BRK;(2) 掛 WRAM 會把 $6000-$7FFF
  從 open-bus 變 RAM,污染 open-bus 類測試結果。
- 預估 S1 跑完整套:各頁模擬時間合計 ~300+ 秒 ≈ 18,000+ 幀 ≈ **1 天多(單行程)**。若要平行化得拆頁。

## 兩次嘗試與結果

1. **注入 `ReloadMainMenu` 進入點**(太早):$700 跳板未初始化,測試一開 NMI → 跳進全零 RAM(BRK)→ 崩潰。
   實測:AprNes GUI 灰畫面;無頭 400 模擬秒完成信號全零。
2. **移到 `DMASync` 之後**($700 已設、pre-test 已跑):**仍然灰畫面**,1500 模擬秒完成信號仍零。
   → boot 情境 vs 選單 NMI 情境還有未找到的差異(未解)。

## 日後重試的線索

- ROM 作者的診斷後門:**`$EC`(Debug_EC)**,boot 每步 `INC`,可定位卡在哪一步。
- 懷疑方向:run-all 正常是「從 NMI handler 內」被呼叫(Start 按鍵處理),中斷返回狀態在堆疊上;
  或 run-all 內部 `WaitForVBlank` 依賴選單 NMI handler 設的某個 flag(boot 情境沒人設)。
- 替代路線:不改 ROM,改用 AprNes 式**手把序列驅動**(S1 需先實作 port0 輸入注入)——
  AprNes 的 `run_tests_AccuracyCoin_report.sh` 已有完整的逐頁導航序列可抄。

## 檔案位置

- `AC_ref/`(gitignored):clone + 改造中的 asm(補丁在 `ReloadMainMenu` 區段,有 `[AC_ref unattended patch]` 標記)。
- `AC_ref/AccuracyCoin.nes` = **原版**(已還原);`AccuracyCoin_unattended_wip.nes` = 失敗的改造版;
  `AccuracyCoin_original.nes` = 原版備份。組譯:`./nesasm.exe AccuracyCoin.asm`。
- AprNesRef 的 `AC_DONE_HEX` dump 改動保留(無害,日後有用)。
