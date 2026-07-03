# 參考機型檔案(Reference Machine Profile)+ AccuracyCoin 硬體文獻摘要

> 2026-07-04。使用者定調的修復方向:**每種機型有自己的特性;行為層應該補的是「與我們 netlist 同一台機器」的正確行為**,
> 而不是「湊測試通過」的混合體。本文件把這個原則落成明確的機型檔案,並摘錄 AccuracyCoin(100thCoin)
> 的硬體行為文獻作為 trace 深究區的參考依據。

## 1. 我們的參考機型(行為層的校準目標)

| 項目 | 規格 | 依據 |
|---|---|---|
| CPU | **Ricoh RP2A03G** | Quietust Visual2A03 晶粒描繪(netlist 本體) |
| PPU | **Ricoh RP2C02G** | Quietust Visual2C02 晶粒描繪(netlist 本體) |
| 主機板 | **NES-001(前置式)** | nes-001.js 模組(MetalNES 血緣);blargg 的測試機也是 NTSC front-loader(oam_read readme 自述) |
| 時脈對齊 | 對齊 7(`--reset-hold-extra 1`) | blargg NMI 邊沿測試所校準的那個對齊(四對齊實測矩陣,2026-07-04) |
| 上電狀態 | 共識上電 palette 表 + P=$34 | blargg 機台殘值(其表成為模擬器共識);shim 已實作 |

**推論**:
- 行為層(記憶體、匯流排整合、DMA 邊沿、衰減)一律以「RP2A03G + RP2C02G 的 NES-001」為準;
  來源優先序:**AccuracyCoin(明確鎖定 G 版次)> blargg 測試(機型較模糊的 2004-2011 前置機)> NESdev 共識**。
- **數位/時序行為可鎖定機型;類比特性仍是「帶」不是「點」**——同為 G 版次,LXA magic 值、衰減時間
  (「depending on the NES and temperature」)、OAM 上電圖樣(「at random」)在單機之間仍隨機/漂移,
  這些永遠以「行為帶 + 忠實偏差」處理,不硬編一個假的精確值。

## 2. AccuracyCoin 是我們的天選仲裁者

AccuracyCoin README 第一段:「**This ROM was designed for an NTSC console with an RP2A03G CPU and
RP2C02G PPU.** Some tests might be automatically skipped on hardware with a different revision.」

—— 它的 141 個測試就是為**我們這兩顆晶片**校準的。因此 trace 深究區的鑑別策略:

> 同一行為,若 **AccuracyCoin(G 版次校準)判我們對、blargg CRC 判我們錯** → 差異屬於 blargg 機台特定,
> 歸忠實偏差;若 **AccuracyCoin 也判錯** → 是我們整合層/netlist 的真問題,且其錯誤碼直接定位
> (2=A 不符、3=X、4=Y、5=旗標)。

(前置條件:AccuracyCoin 無人值守化 —— 已暫停的 AC_ref 改造工作因此升值,見
`2026-07-02-accuracycoin-unattended-attempt.md`。)

## 3. AccuracyCoin 硬體文獻摘要(對應我們剩餘 FAIL)

### 3.1 非官方立即指令(對應 instr_test 02/03-immediate 的五連掛)

- **LXA($AB,= blargg 的 ATX)**(asm `TEST_LXA_AB` 註解,逐字):
  > "A = ((A | Magic) & Immediate), X = A. The 'Magic' value is not consistent, and so this test cannot
  > rely on any specific value... **unless Immediate is $00, or A is $FF, the outcome is not guaranteed.**"
  - AccuracyCoin 只測保證情形,magic 值**只顯示不判分** —— 權威確認 LXA 精確值是機台/類比相依。
  - blargg 的 per-opcode CRC 編碼了他那台的 magic 值 → 我們的 AB 失敗有強忠實偏差論述。
- **ARR($6B)** 確定性語義(asm 註解):A=(A&imm) 右旋;**N=bit7、C=bit6、V=bit5⊕bit6、Z=結果為零**。
- **ANC($0B/$2B)、ASR($4B)**:確定性;AccuracyCoin 有獨立測試(`TEST_ANC_0B/2B`、`TEST_ASR_4B`)。
- 錯誤碼慣例:2=A、3=X、4=Y、5=旗標 → 未來在 S1 上跑這些單項可直接定位差異維度。

### 3.2 DMC DMA(對應 dmc_basics #19、dma_2007 家族、sprdma ×2)

- **「DMC DMA 不能中斷寫入週期」**(asm 6426 註解,逐字):
  > "However, DMC DMA's cannot interrupt a write cycle! Therefore, the address bus cannot be $2007
  > during the DMA, so nothing unusual happens!"
  - 直接解釋 dma_2007_write 與 read 家族的行為分界;trace 時檢查我們的 RDY 停機是否遵守這條。
- **DMA 的 dummy read 可以打到 $2002 並清掉 VBlank flag**(`Test 2 [DMA + $2002]` 註解)——
  DMA 期間位址匯流排殘留位址的讀取副作用;trace dma_4016/2007 時的關鍵行為。
- AccuracyCoin 有整套 `Suite_DMATests`(含 DMC DMA Bus Conflicts、DMC DMA + OAM DMA)可當對照。

### 3.3 APU Frame Counter IRQ(對應 irq_flag #6)

`TEST_FrameCounterIRQ` 的五個子測試定義了 IRQ flag 的正確行為矩陣:
4-step+enabled=set / 4-step+disabled=not set / 5-step 兩者=not set / 讀取即清除。
blargg irq_flag #6 抱怨的「寫 $00/$80 到 $4017 不應影響 flag」可與此矩陣交叉驗證。

### 3.4 其他可引用的行為定義

- 開機魔術數 $5A 慣例、`PowerOn_MagicNumber`(冷/暖開機判別)—— 與我們上電 shim 的語義相容。
- `Suite_PowerOnState` 是 DRAW 型(印出不判分)—— 100thCoin 對上電狀態的態度與我們一致:展示,不武斷判對錯。

## 4. TriCNES 交叉對照(2026-07-04,使用者提供的無頭測試報告)

TriCNES = AccuracyCoin 作者自己的模擬器(`TriCNES_ref/` 已 clone;使用者先前改造出無頭測試模式,
報告見 AprNes `site/report/TriCNES_report.html` / `TriCNES_results.json`)。成績 **169/174**,對照重點:

- **它把我們剩餘問題測試幾乎全過**(OAM 對、DMC/DMA 家族、irq_flag、immediate、even_odd)——
  一如 AprNes/Mesen:行為層實作共識規則 + 預設在能過的對齊/OAM 模式。**不推翻我們的忠實偏差論述**,
  但它的實作(`Emulator.cs`)= G 版次行為的權威演算法規格,是 DMC trace 的第一參考。
- **`power_up_palette` 連 TriCNES 也 FAIL** —— AccuracyCoin 作者的模擬器同樣過不了 blargg 的機台專屬
  調色盤殘值。「該測試值是單一機台特性」的論述再獲一個獨立模擬器佐證。
- **`read_write_2007`:TriCNES FAIL、我們 PASS** —— 開關級物理答對了連作者手寫行為模型都答錯的題;
  值得記錄的獎盃級交叉驗證。
- 其餘 3 個 FAIL 全是 MMC3 版次變體(rev_A/MMC6/MMC3_alt),mapper 範疇,與我們無涉。
- decay 常數出處:`PPUBusDecayConstant = 1786830`(Emulator.cs:1393,作者主機實測)——
  我們的 open-bus 衰減 shim 已引用此建模。

## 5. 行動含義

1. trace 深究區每一項先查 AccuracyCoin 對應測試的註解與期望(本文件 §3 為索引)。
2. AC_ref 無人值守化復活列入待辦(價值升級:G 版次仲裁者)。
3. F 類(immediate 五連掛)的即時行動:比對我們 netlist 算出的 ANC/ASR/ARR 語義是否符合 §3.1
   的確定性定義(可用 --trace 單指令跑),LXA 部分先標忠實偏差候選。
