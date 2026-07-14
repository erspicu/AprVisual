# APURegActivation err6 戰役:R4015 讀解碼缺管 —— 第一個 E 類(網表資料缺陷)

**日期**:2026-07-14
**測試**:AccuracyCoin page 13 item 6(APU Register Activation),結果槽 `$45C`
**起點**:孤立 ROM `$45C=$1A`(FAIL err6);**AprNes 神諭同 ROM `$45C=$09`(PASS variant 2)→ S1 真偏差**
**終點**:`$45C=$09`,與神諭一字不差;金 checksum `0x794A43ABDF169ADA` 不變
**修法**:`EnableR4015A1Shim()` —— InstClampLow 覆蓋層,精確模擬上游網表缺失的一顆 PLA 下拉管

---

## 1. 時間軸翻案(兩次誤錨的教訓)

先前把判定錨在 t=15.08M 的 `$055C` 寫入(RESULT hook 首發),當時 $500 頁全零,
以為「OAM 拷貝全零」。全程無樓層的 [st] 監視器翻案:

- 判定前 `$4014`/`$3FFE` **一次都沒被碰** —— 15.08M 的觸發是繪圖迴圈誤中地址,
  $500 全零 = 測前記憶體(**探針三規則之外的新教訓:hook 的地址觸發要配時間窗,
  首發 ≠ 判定**);
- 測試實際執行在 **t≈19.31M**:$3FFE 觸碰 + 168 ticks 後 $4014 寫入。

另一個誤錨:err 高值殘留理論(Test 1 前置套件把 `TEST_DMA_Plus_2007R` 整顆當
副程式呼叫)—— 被 `FAIL_APURegActivation_Pre` 會把 ErrorCode 重設為 1 的事實否決。
err6 = 真的走到 Test 6 評卷。

## 2. Test 5 戲法:S1 完美重演

`[fin]` 最後半週期採樣,逐拍對照 asm 註解的劇本:

```
$3FFE → $8D   (STA abs 操作碼,來自 PPU data bus)
$3FFF → $14   (operand lo,來自 PPU read buffer)
$4000 → $14 → $40   (DMC DMA 準時蓋掉 data bus!)
WRITE $4014 = $50   (→ OAM DMA,來源頁 $50)
```

6502 位址匯流排停在 $4001(範圍內)→ APU 暫存器解碼啟用 → DMA 讀 $50xx 時
每 $20 步幅撞進 APU 鏡像($x5=$4015、$x6=$4016、$x7=$4017)。

## 3. 三份證物鎖定 $x17

真判定瞬間(19.366M)的 $500 頁 vs 答案卷:row0 全對、row1 前七字節全對
($15=$44 APU 鏡像!$16=$41 手把!),**第一個錯字節 = $17:期望 $40,實得
$04(= 清旗後的 $4015 內容)**;之後全面 $04 污染(寫回把 $04 驅上外部匯流排,
OB shim 忠實記錄重播,自我延續)。

`NO_OB_SHIM` A/B:$500 頁逐字節相同 → shim 無罪,污染是網表自身行為。

## 4. 事件記錄器抓到雙開火

窄窗逐步採樣(變化才印),`r15=XYZ` = (r4015, r4016, r4017),0=active:

| 讀 | 解碼 | OAM 閂到 |
|---|---|---|
| $5015 | **0**11(r4015)✓ | $44 ✓ |
| $5016 | 1**0**1(r4016)✓ | $41 ✓ |
| $5017 | **0**1**0**(r4017 **和 r4015 同開**!) | $04 ✗(status 內部驅動壓過外部 $40) |

$15=`10101` 與 $17=`10111` 只差 a1 → r4015 解碼忽略了 a1。

## 5. 解碼結構解剖(transdefs 直讀)

- `r4015`(10749)僅被 `#10975`(正相乘積項)拉低;
- `#10975` = NOR(`#11166`, `#13213`, `#13215`, `_ab3`, `#13217`)
  = `RD & a0=1 & a2=1 & a3=0 & a4=1` —— **無 a1 條件** → $x15 與 $x17 都選中;
- `r4017` 的乘積項 `#11183` 有完整六輸入(含 `#13214` = a1=1)→ 只選 $x17。

(映射:`_abX` = APU 內部閂鎖位址線正相;`#1321x` = 其反相;
`#11166`=NOT(apureg_rd)、`#11265`=NOT(apureg_wr)。)

## 6. BreakNES 矽權威判決 + 上游溯源

`ref/breaknes/Chips/APUSim/regs.cpp`:
`pla[4] = NOR6(nREGRD, nA0, A1, nA2, A3, nA4)` —— **真矽 R4015 讀解碼有 A1 輸入**(只選 $15)。
硬體旁證:讀 $4017 不清 frame IRQ(若矽上雙開火,每次讀手把 2 都會清旗 —— 與文件矛盾)。

上游 `ref/drive-download-*/visual2a03-transdefs.js` 對 `#10975` 同樣只有 6 顆管
(缺 gate=`_ab1` 的下拉)→ **缺陷在上游 Visual2A03 資料集本身**(die-shot 描摹漏管),
所有下游專案(含 metalnes)都繼承。

**全面審計**:29 條 r/w 解碼線逐條對 BreakNES PLA 表 —— 讀側 6 條、寫側 22 條
全部完整,**唯一缺陷就是 r4015 的 a1**(w401a 為 DBG 除錯暫存器,結構不同,不計)。

## 7. 分類學新物種:E 類 = 網表資料缺陷

既有 A(引擎語意極限)/ B(板卡外設建模)/ C(引擎真 bug,僅 1)/ D(id-order 樂透)
之外的第五類:**網表資料本身與矽不符**。第一號標本 = r4015 缺 a1 下拉管。
特徵:跨引擎重現(任何忠實執行這份網表的模擬器都錯)、可用獨立矽逆向源
(BreakNES/BreakingNESWiki)仲裁。

## 8. 修法:InstClampLow 覆蓋層(缺管的精確軟體替身)

`EnableR4015A1Shim()`(WireCore.System.cs;env 開關 `NO_R4015_SHIM`):

- 解析 `cpu._ab1`(閂鎖位址 a1 正相線)與 `cpu.#10975`(乘積項);
- 每步:`_ab1==1` → `InstClampLow(#10975)`(= 一條通往 vss 的導通路徑加入群組,
  正是缺失下拉管的群組級語意);`_ab1==0` → `InstRelease`;
- 零圖變更(旗標覆蓋,不重擲 id-order 樂透)、測試模式限定、冪等切換。

**根治方案(緩議)**:transdefs 補一顆管 `[gate=_ab1(10055), c1=#10975(10975), c2=vss]`。
那是載入期圖變更 → 全域樂透重擲 + 金 checksum 合法改變(需重定基準 + 全量重驗),
**排在掛牌 bank 之後、與其他大爆炸候選一起評估,需使用者拍板**。

## 9. 驗證

- 孤立 ROM:`$45C=$09 PASS variant 2` —— **與 AprNes 神諭同值**;$500 頁 rows 0-5
  與答案卷逐字節相同(rows 7+ 差異屬三角波長度計時允許變體,evaluator 收);
- `$5017` 讀:`r15=110`(只剩 r4017),OAM 閂 $40 ✓;
- 金 checksum:`0x794A43ABDF169ADA`(full_palette 300k **--extra-ram**)不變,
  fast-path 3929 ✓;
- 哨兵砲台(OpenBus / LAE / Explicit / Implicit / IDR / APURegActivation,
  全 shim 開)—— 執行中,結果補記於 §11。

### ⚠ 附:金 checksum 配方假警報(40 分鐘的學費)

驗證時漏帶 `--extra-ram` 跑出 `0x6D0249F548FBDA69`(fast-path 3895),一度誤判
回歸,worktree 二分到 4ca6999 才發現乾淨提交也「錯」—— 真相:**金值的標準配方是
`full_palette.nes --bench-hc 300000 --extra-ram`**(見 MD/note/06 §結尾)。
額外 RAM 模組上板改變節點數與 checksum。教訓:配方的每個旗標都是配方的一部分;
比對 fastPathNodes(3929 vs 3895)可以秒判配置錯誤。

## 10. 可重用資產

- `[st]` 全程無樓層監視器(地址觸碰盤點)、`[rd]` DMA 讀取矩陣(final-db per read)、
  `[ev]` 窄窗逐步事件記錄器(tuple 變化才印)—— 皆 OB_DEBUG 閘後,留在 DmaProbeStep;
- transdefs 乘積項解剖 + 29 線 PLA 審計腳本(本檔 §5-§6 的 python 片段,
  對照 BreakNES `regs.cpp`)—— 任何疑似解碼異常都可套用;
- AprNes `--dump-ac-results --dump-debug`(ZP$00/$50 + $500 頁 16×16)= 現成神諭傾印。

## 11. 哨兵結果(補記)

(待砲台完成後填入)
