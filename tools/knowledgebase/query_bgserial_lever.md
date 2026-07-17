# BGSerialIn(AC $487)修復槓桿諮詢 —— $2001 寫入效果在 BG shifter-load 邊界的 2-dot 延遲

## 你是誰
資深 NES 硬體 + switch-level 模擬器工程師。AprVisual.S1(二值開關級,settle-to-quiescence,
GND>VCC>外部驅動)。AC 無人版 140/141,最後一顆 = BGSerialIn err2。

## 已定讞的根因鏈(全部實證,不用懷疑)
- 套內三連 FAIL err2(run5b/run7/rp resume),雙圖皆然;AprNes oracle 套內 PASS。
- 所有「孤立 PASS」證據作廢:診斷版建置把 TEST_BGSerialIn 換成 `LDA #1/RTS` stub(fork 註解自證,
  為省 $ED40 前空間)。真身從未被孤立量測過。
- 即:**這是一顆從未修過的 M6 家族 bug**(跨晶片 $2001 寫入效果延遲被抹零),與 dot-339/even_odd 同宗。

## 測試機制(AccuracyCoin.asm 原文關鍵行)
180 條掃描線,每線:
```
LDA #$0 / STA $3E01 ; disable rendering (write 尾落 dot%8==6)
LDA #$1E / STA $2001 ; enable  (write 尾落 dot%8==6;註解:"adding the smallest known delay of 2 brings us to dot%8 == 0")
```
- BG shift registers 在 dot%8==7 載入 pattern 資料;dots 0-255/320-335 每 dot 都在 shift,serial-in
  低位面進 0、高位面進 1。
- 硬體:寫入效果延遲 2~5 dot("depends on the ppu and the clock alignments"),最小 2 →
  disable 效果落 %8==0、enable 效果落下一組 %8==0 → **%8==7 的 reload 落在 OFF 窗內被跳過**,
  shifter 只 shift 不 reload → 高位面串進一排 '1' → 白線 → sprite-0 hit = PASS。
- S1(零延遲):寫入效果即時 → OFF 窗蓋不到(或蓋錯)%8==7 的 reload → reload 照常 → 無白線 →
  無 hit → err 2。

## 既有工具(同宗修復的前例)
- dot-339 shim(已 default):$2001 寫後 clamp `hpos_eq_339_and_rendering` 24hc(force-LOW 抑制,
  可視線 gated)→ 修 StaleSprite T3。9 測回歸乾淨。
- even_odd shim(已 default):$2001 寫效果延遲 16hc,窄窗 vpos261/hpos338-339。
- InstClampLow(Gnd 必勝)/ InstClampHigh(撞 GND>VCC 牆,網表 drive-low 時必敗)。
- 節點候選(2C02 nodenames 實存):`hpos_mod_8_eq_6_or_7`、`hpos_mod_8_eq_6_or_7_and_rendering`
  (疑為 shifter-reload 閘)。另有 bkg_enable / bkg_enable_out / rendering_1 等。
- 07/15 勘察結論:施力點 = 上述 reload 閘節點,幅度 ~16hc,「dot-339 同款外科手術」。

## 問題(請逐點,工程判斷)
1. **精確介入語意**:要重現「reload 被跳過」,是
   (a) enable 寫入後把 reload 閘(`hpos_mod_8_eq_6_or_7_and_rendering`)**壓低 16hc**(force-LOW,無牆),
   讓緊接的 %8==7 reload 不發生;還是
   (b) disable 寫入後把「rendering 對 fetch pipeline 的影響」**撐高 16hc**(force-HIGH,撞牆,需
   互補節點);還是 (a)+(b) 都要?請按 2C02 的 reload 資料流(rendering gating 在哪一級)判斷,
   S1 上哪種組合能讓 180 線的 OFF 窗正確蓋住 %8==7。
2. **閘控與爆炸半徑**:此 shim 若「每次 $2001 寫入後開 16hc」全域生效,AC 套內哪些測試會受害?
   (套內大戶:even_odd 有自己的窄窗 shim、StaleSprite T3 靠 dot-339、RenderingFlagBehavior、
   Scanline0Sprites、各 Sprite0Hit 家族……)是否該 gated 成「寫入落點 hpos%8∈{4..7} 才啟動」
   之類的相位條件,而非時窗?請給最小爆炸半徑的閘。
3. **與既有兩顆 $2001 shim 的疊加**:同一次寫入可能同時觸發 even_odd 窄窗、dot-339 clamp、本 shim。
   衝突嗎?需要互斥條件嗎?
4. **幅度**:16hc(2 dot)是「smallest known delay」;測試設計預期 2~5 dot 都能過嗎(它說 tedious
   because could happen in most of the range)?若 S1 選 16hc 固定,180 線全部同相位,會不會有
   邊界線踩到 3-dot 需求?
5. 若 (a) 案成立,reload 閘被壓 16hc 會不會誤傷同組的 NT/AT fetch(%8==0..5 的位址產生)?
