# StaleSpriteShiftRegs err2 戰役:取證進行中

**日期**:2026-07-15(進行中)
**測試**:AccuracyCoin page 18 item 2(PPU Misc「Stale Sprite Shift Regs」),結果槽 `$48F`
**基線**:孤立 ROM(`AccuracyCoin_StaleSpriteShiftRegs.nes`,prime-then-IDB 樣板)S1 = `$48F=$0A` err2;
AprNes 神諭 = `$48F=$01` PASS → S1 真偏差
**err2 = Test 2**:scanline 5 的 dot ~15 關渲染、~33 開(18 ppu dots 強制 blank),硬體行為 =
sprite 管線照走,X=$FE 的 sprite 0 照樣在行尾畫出 → sprite-zero hit 必須發生;S1 無 hit。

## 探針艦隊(全部 OB_DEBUG 閘後,WireCore.System.cs)

| 探針 | 內容 | 產出 |
|---|---|---|
| [sp] | 每筆 $2001 寫入落點 + enable/_out/rendering/spr0_hit 邊沿 | 寫入落點=dot15-16/33-34 ✓;stunt 幀整幀無 hit;對照幀 hit@v6 h256 ✓ |
| [sq] | copy_sprite_to_sec_oam / spr_eval_copy_sprite 上升沿 | control 幀心跳 v=0..12(=63 顆 Y=0 假 sprite ∪ sprite0 Y=5);**stunt 幀全幀零 copy** |
| [sr] | eval 機狀態(終止閂/溢位/spr_addr/spr_ptr)@h66/h340 | 兩幀**逐值相同、全程健康** |
| [ss] | rendering 家族(r1/r2/nr/nr2/vis/lt64/eq63/eq65)@h30/64/66 | 兩幀**相同、全健康**(nr=0、vis=1)|
| [su] | not_rendering 閂鎖簇(nr/gate#5829/in#10676/or#5727/rdis)邊沿 | **stunt enable(v261 h274)有更新閂鎖**(gate 在 261 全行開,nr 應聲落 0)|
| [sv] | $4014 觸發瞬間 DMA 源頁($200-$20F)+ DMA 期間 $2004 實寫字節 | 跑動中 |

## 假說淘汰史

1. ❌ H1 $2001 生效延遲落點漂移 —— 實測落點與測試預期一致(S1 write→out ≈1 dot,硬體 2-5,容忍窗內)
2. ❌ H2a 中場 18-dot blank 殺死 eval —— stunt 幀 v=0 起就零 copy,blank(v5)還沒發生
3. ❌ H2b not_rendering vblank 帶閂鎖卡死 —— [ss]/[su] 直測:閂鎖有更新、rendering 家族全健康
4. 🔍 **H3(現行)OAM 空城**:t=35008667 的 $4014 DMA(test 2 的 SetUpSpriteZero 後)之後,
   OAM 無任何 in-range sprite(零 copy 的最簡解釋)→ [sv] 驗 DMA 源頁與實寫字節

## 網表解剖成果(過程副產品,皆已幾何驗證)

- `not_rendering`(1030)= pass-gate 動態閂鎖:pullup + t7061(gate=`+/vpos_eq_240-260_2`
  #5829,輸入 #10676=NOT(#5900)=rendering_disabled 鏡像)
- 閂鎖更新窗實測 = **v=261 整行**(名字寫 240-260,實際 #5727 OR 項含 `vpos_eq_261_2`
  (t6313,pclk1 閘)→ 窗延伸到 pre-render ✓ 與 PPUSim fsm.cpp 的 BLNK_FF 語意方向一致)
- `not_rendering_2`(203)三驅動:t16270(pclk0 取樣 #10553 —— 即時路徑,解釋 v=5 中場
  rendering_1 翻轉)+ t6166/t6167(nr 的靜態跟隨對)
- `in_visible_frame_and_rendering`(835)被 t6087(gate=not_rendering)壓制 —— eval 殺手鏈
  的假想路徑,已被 [su] 洗清
- **幾何比對**:t7061 案發地(x3055-3080, y5570-5670)三明治結構(diff 10676 | poly 5829 |
  diff 1030)完整,支援 poly 全在場 —— 資料層乾淨;配合 2C02 幾何完備性審計(零萃取漏),
  「補網表」在本案暫無空間(使用者判準:die 有才補,無不無中生有)

## 教訓(過程繳費)

- 邊沿記錄器的觸發集要排除相位翻轉訊號(owd 每半 dot 抖,400 預算秒殺)—— 先看一眼
  訊號「質地」再入觸發集
- 匿名節點名(#5541)與特殊字元名(`+/vpos_eq_240-260_2` 含 +、-)會讓 naive regex
  漏列 → 「零驅動者」假象兩次;解剖腳本的字元類要含 `+.-`
- [sq] 的「整幀死亡」差點誤導 —— 先查預算再下結論(count=9,沒爆)

## 下一步

[sv] 結果三岔:源頁錯 → SetUpSpriteZero 的 RAM 寫入沒落地(更上游);源頁對、$2004 字節錯
→ DMA 搬運路徑;字節全對 → OAM 儲存/eval 讀出(2C02 內部,難度升級)。
