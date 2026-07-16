# ALERead 最終魔王 —— 修正前提後的 io_ce 捕捉-重播修法諮詢

## 你是誰
資深 NES 硬體 + switch-level 模擬器工程師。以下是 AprVisual.S1（二值 switch-level NES 模擬器，
settle-to-quiescence，group 解析優先序 GND > VCC/pull-up > 外部驅動 > hold）的最後一顆未過測試。

## 重大前提修正（推翻你我前五輪諮詢的一個共同錯誤假設）
前五輪諮詢都假設「板級 74LS373 八位閂鎖**未建模**」。**這是錯的。** 我剛實測確認：

- S1 的系統網表 `nes-001.js` 有 `[ "u2", "74LS373", 0 ]`，而 `74LS373.js` 是**完整的 96 電晶體開關級
  透明閂鎖**（每 bit：d→/d 反相器、/LE 閘控 set/reset、交叉耦合 q//q SR latch、/OE 三態輸出 + pull-up）。
- 接線正確（全走名稱）：`ppu.ale→u2.LE`、`vss→u2.OE`、`ppu.ab[7:0]→BD→u2.d[7:0]`、
  `u2.o[7:0]→BA[7:0]`，而 CHR ROM 位址 `chr.a[12:0]→edge.ppu_a[12:0]→BA[12:0]`（=閂鎖後低位 + PPU 高位）。
- **它確實運作**：正常畫面能渲染就是鐵證 —— CHR 取圖時 ppu.ab[7:0] 已切成資料匯流排，位址低位元組
  只能靠 u2 閂住，否則畫面全毀。載入時 u2 節點正常配置、無 "not loaded" 訊息。

**結論：'373 好好的。所以 ALERead 失敗不是缺件，而是純粹的 CPU→PPU 相位問題。**

## 實測鐵證（s1_ae3.log 逐 dot，v=3，[ae] 探針）
```
h=226 ale=1 rd=0 chrAb=$2CF0   ← S1 的 $2007-read /RD 重疊在這（dot 226）
h=227 ale=1 rd=1 chrAb=$2FC7   ✓ 與硬體 dot 227 逐位吻合
h=228 ale=0 rd=0 chrAb=$2FFF   ✓ 與硬體 dot 228 逐位吻合（$FF 塌縮）
h=229 ale=1 rd=1 chrAb=$0F04   ← 硬體在此 ale+/RD 重疊 → 八位閂鎖鎖 $FF → 讀 $0FFF；
                                  但 S1 此處 rd=1（無重疊）→ u2 正常透明追到 $04 → 無 hybrid → FAIL
```
**關鍵區分：CHR 取圖節奏完全對齊（227/228 吻合），只有 CPU 的 `$2007` 讀取存取早了 3 dot（24hc=1 CPU cycle）。**
若相位修對，重疊落在 h=229，現有開關級 u2 會自然抓 $FF → PASS。

## 已證死路（§五-c 實作實證，別再建議）
1. **延遲任何「網表主動驅動」的內部信號不可行**：試過延遲 `read_2007_trigger` 24hc —— 用 InstClampLow
   抑制成功（樣本 12→1），但**重新拉高必敗**：延遲時網表正 drive 該節點低,LUT 優先序 GND>VCC>外部驅動,
   InstClampHigh(Pwr)/SetHigh/互補鉗全壓不過網表 drive-low。→ 任何「延遲信號」路線死。
2. **下游 latch-force 死路**：BOL（硬 force 八位閂鎖持 $FF）過度開火,打壞 2007Stress。資料相依,無乾淨施力點。

## 唯一活路（§五-c 收束）：io_ce 捕捉-重播
把「延遲信號」轉成「延遲存取事件」：捕捉整個 CPU $2007 存取,24hc 後重播進 PPU,讓網表**自己自然生成**
read_2007_trigger/ReadALE(不用硬拉)。重播時全往「低」鉗:io_ce active-low → InstClampLow(必勝),
io_ab/io_db 各位元鉗到捕捉值 —— 無 re-assert-high 問題。

## 施力節點（已定位）
- `ppu.io_ce`(active-low,=0 表 PPU 被存取)、`cpu.rw`(1=read)、`ppu.io_ab[2:0]`(7=$2007)、`ppu.io_db[7:0]`。
- 實測 $2007 讀取介面時序:dots 223-224 ioce=0 ioab=7 rw=1;dot 225+ ioce=1(存取結束)。

## 我要問的(請逐點回答,給工程判斷不要客套)
1. **前提修正後,io_ce 捕捉-重播是否為正解?** 既然 '373 已運作、節奏已對齊、只差 $2007 存取晚 24hc,
   把整個 $2007 讀取存取延遲 24hc 重播進 PPU,是否就能讓 ReadALE 落在 dot 229、u2 自然抓 $FF?

2. **讀緩衝分離問題**:$2007 讀取 =(a) CPU 立即讀「讀取緩衝」(返回值)+(b) PPU ReadALE 重抓 VRAM
   填緩衝(boing2k7 汙染源)。延遲整個 io_ce 會同時延遲 (a)。**但 boing2k7 的 LDA $2007 讀值被丟棄**
   (下一句 LDA $2002 覆蓋)。所以對 boing2k7 延遲 (a) 無害。**問題:2007Stress / dma_2007_read
   會不會在渲染期讀 $2007 並檢查值?** 若我把 shim 閘控在「主動渲染期 + io_ab=7 + rw=read」,是否夠窄
   到不碰那些測試?還是它們也在渲染期讀 $2007 對值?請以 NES 測試實務判斷這個閘的安全性。

3. **重播驅動 PPU 的極性/時機**:重播時我 force io_ce=0 + io_ab + io_db + rw 進 PPU 24hc。這會不會
   連鎖污染 $2004/$2005/$2006 狀態機?要注意什麼邊界(例如重播只做 1 hc 還是持續到 PPU 消化)?

4. **有沒有更簡單、我沒想到的乾淨路線**(既然 '373 已運作)?例如:只延遲 io_ce 這一條 active-low 線
   本身(不動 io_ab/io_db)—— 因為 dots 223-224 io_ab 已是 7、io_db 是讀值,若只把 io_ce 的 assert
   延後 24hc(InstClampHigh 壓抑當下 + 到期放開讓網表自然 assert?)—— 但這撞 §五-c 的 re-assert 牆嗎?
   請判斷「只延 io_ce 一條線」vs「完整捕捉-重播 io_ab/io_db/rw」哪個對、哪個安全。

5. **24hc 精確嗎?** $2007 讀觸發 ReadALE、$2007 寫觸發 WriteALE,延遲量是否一致?先統一 24 再微調可行?
