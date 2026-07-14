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

## 第二波取證([sv]→[sz])

- **[sv]**:DMA 無辜 —— 源頁 `05 C5 03 FE FF...` ✓、$2004 逐字節照搬 ✓
- **[sw]**:OAM 讀出對照 —— control 幀 v=5 h66-73 完美讀出 `05 C5 03 FE` + copy 脈衝;
  **stunt 幀同座標全 $FF**(零錯位可能:高位 Y 全出局才會零 copy → 全 FF)
- **[sx]**:寫撥桿對照 —— 兩次 DMA 的 bit-7 SET/CLR 序列**完全一致**且正確對應資料
  (CLR,SET,CLR,SET = 05,C5,03,FE 的 bit7)→ 寫入機制無辜
- **時窗鎖定**:毀損發生在 DMA 結束(35.0210M)→ v=5 eval(35.0364M)之間 =
  re-enable 窗(v=261 h274 起的 pre-render 尾段 + v=0-4)
- **[sy]**(sx 擴窗):v=0 dots 2-64 出現標準 clear 節奏的 FF 寫(每 2 dots 一發)——
  clear 段本來就合法寫 FF(進 secondary),需分辨列/欄目標
- **[sz]**:列選擇兩幀相同(0→31 依序)→ **OAM 陣列布局 = 32 列 × (8 主欄 + 1 副欄)**
  (spr_col0-8 九條欄線!),主/副仲裁在欄不在列 → 欄線版跑動中

## 現行假說(第四代)

mid-261 re-enable 留下的殘相位讓 **clear 段的欄選擇錯指主欄**(control 應打欄 8/副欄,
stunt 打欄 0-7/主欄)→ FF 灌爆主 OAM → sprite 0 消失 → 零 copy → 無 hit → err2。
PPUSim 標尺:真矽 clear 只進 secondary(OB 強制 FF + 副欄寫),主欄不開。
若 S1 的欄仲裁路徑連通與矽不符 → 進 die 證據判準;若連通同矽但 settle 語意選錯
→ A/D 類,通則機制修。


---

## 破案(2026-07-15):OAM blank-edge 回寫毀損(A2 時窗重疊)

### 決定性證據鏈

1. **真 OAM 傾印**(節點 `ppu.oam_ram_XX_b{bit}`,由 oam-dma-ppu-bus shim 的 cell 圖挖出):
   DMA 寫入成功、v=0/v=2 都還在,**v=5 那一刻 sprite 0 從 `05 C5 03 FE` 變成 `FF FF E3 FF`**
   → 前面所有「DMA/儲存/列選擇」假說全部作廢(那 12 顆手猜的候選 cell 根本不是目標)。
2. **逐 dot 追捕**:毀損發生在 **v=5 h=17,即 `rendering_1` 由 1 落 0 的同一個半週期**。
3. **逐半週期解剖**(t=35035978 → 35035979):`row0` 0→1、`col0` 0→1 同時開啟,
   位元線仍帶著 clear 相位的 FF 圖樣(bitA=0/bitB=1)→ cell 當場被改寫
   (bit1:a=1,b=0 → a=0,b=1)。
4. **結構解剖**:OAM cell = 交叉耦合對且**無 pull-up**(只有兩顆下拉管 + 兩顆列閘管)
   → 是**動態 cell**,「1」靠位元線預充電(`pclk0` 閘、接 vcc)與讀取緩衝器的回寫維生
   (DRAM 式 sense-and-restore)。
5. **機制**:BLNK 升起的瞬間,緩衝器來源從「cell 讀值」切成「外部匯流排(clear 的 FF)」,
   而回寫刷新通路還開著 → FF 被刷進 sprite 0。真矽有傳播延遲,列線先關 → 不會毀損。
   **A2(時窗重疊)盲區**的教科書案例。

### 真機標尺(AC OAM_Corruption 測試自帶規格)

- 毀損只在**重新啟用**時發生(不是關閉時);
- 種子 = 關閉當下的 Secondary OAM Address;
- 動作 = `OAM[seed*8 + i] := OAM[i]`(i=0..7)—— **row 0 是「來源」,永不被毀**;
- 原文:「OAM Corruption cannot affect the outcome of a (non-arbitrary) sprite zero hit」。

### 修法:`EnableOamBlankEdgeShim()`(機制級、全域、env `NO_OAMEDGE_SHIM`)

鏡射被定址的 OAM 列(每半週期 64 次讀取,可忽略成本),在 `rendering_1` 落沿把該列
還原成落沿前的內容 = 「渲染關閉邊沿不得寫 OAM」的語意重述。

### 驗證

- OAM 傾印:STUNT v5-pre-eval 從 `FF FF E3 FF` → **`05 C5 03 FE`**(sprite 0 存活);
- `$48F`:**err2 → err3**(Test 2 通過,前進到 Test 3);
- 金 checksum `0x794A43ABDF169ADA`(full_palette 300k --extra-ram)不變、fast-path 3929;
- 六顆哨兵回歸:排在 Test 3 也修好後一次跑。

### 下一關:Test 3(這顆測試的本體)

關渲染 ~10 條掃描線,**sprite 0 的 X 下數計數器(`spr0_p[7:0]`)必須凍結**,
重新啟用後才數到 0 → `spr0_active` → hit。節點家族已定位:
`spr0_p0-7`(+`_next`/`_out`/`_borrow` 進位鏈)、`spr0_active`、`spr0_hit`。
