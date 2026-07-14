# 交接:StaleSpriteShiftRegs err2 取證(進行中)+ 本輪 session 全景

**日期**:2026-07-15
**接手第一件事**:讀背景跑結果(見 §1),按 §4 決策樹續打。

---

## 0. 本輪 session 已收成(全部已 commit+push,細節在對應筆記/記憶)

1. **掛牌 136/141 已入帳**(run5b:joyON + R4015 網表補丁;五顆殘留=已知未修;
   ReportAC 記分板/曲線/真結果畫面已發佈)→ 記憶 `banked-136-of-141`
2. **APURegActivation 戰役全破 + E 類新分類**:上游 Visual2A03 缺 a1 下拉管,
   die 幾何 + BreakNES + 硬體行為三重定罪;**使用者拍板:E 類直接補網表不用 shim**
   (t13032b 進 transdefs_named.js;⚠ 2a03.js 載入的是 named 版!)
   → `tools/netlist/apply_2a03_patches.py` + `2a03_patches.md`;金 checksum 兩點皆不變
3. **幾何完備性審計**(`tools/netlist/geom_audit.py`):2A03 萃取漏恰 2(皆補)、
   **2C02 = 0** → 萃取漏類別雙晶片封閉
4. **通解架構三篇**(MD/suggest/2026-07-14:shim通用化、正準重編號提案、混合訊號藍圖)
   + **教學文發佈**(WebSite/semantic-ceiling.html,含適用域章節)
5. 郵件看守工具(tools/watch_run_mail.py)、node_area.py(電容面積資料管線)

**使用者判準(本場戰役鐵律)**:die 上真的有 → 補網表;原始設計本來就沒有 →
不無中生有(引擎語意問題走通則機制)。

## 1. 接手即讀:背景跑 bavyu10df([sz]+欄線版)

```
log = temp/ac/stale/s1_sz2.log
grep -a "\[sz\]" temp/ac/stale/s1_sz2.log | awk -F't=' '$2+0 <  34000000' | head   # control 幀
grep -a "\[sz\]" temp/ac/stale/s1_sz2.log | awk -F't=' '$2+0 >= 35000000' | head   # stunt 幀
```

每行格式:`strobe rows=[N,] cols=[...] at v=.. h=..` —— v=0 dots 2-64 的 clear 段,
兩幀對照 **cols**:

## 2. 案情一頁(完整取證在 MD/testrom/2026-07-15-StaleSpriteShiftRegs-err2-取證.md)

- **測試**:AC page 18 item 2,槽 `$48F`;S1 孤立 = err2、AprNes 神諭 = PASS
- **err2 = Test 2**:scanline 5 中場 18-dot blank 後,X=$FE sprite 0 必須照樣命中;S1 無 hit
- **已翻案 ×4**:H1 $2001 落點(實測 dot15-16/33-34 ✓)、H2a 中場 blank(stunt 幀 v=0 起就死)、
  H2b not_rendering 閂鎖(更新窗實測延伸到 v=261 整行,enable 有吃到)、
  DMA/寫撥桿(源頁 ✓、$2004 字節 ✓、bit-7 SET/CLR 序列 ✓)
- **鐵證**:control 幀 v=5 h66-73 eval 讀出 `05 C5 03 FE`+copy ✓;stunt 幀同座標**全 $FF**
- **毀損時窗**:DMA 結束(t=35.0210M)→ v=5 eval(t=35.0364M)= re-enable 窗
  (enable 落在 **v=261 h=274 pre-render 中段** —— control 的 enable 在 v=242 vblank 帶,
  這是兩幀唯一的結構差異)
- **OAM 布局已確認**:32 列 ×(8 主欄 + 1 副欄)(spr_col0-8 九條欄線);
  clear 段列走訪 0→31 兩幀相同 → 主/副仲裁在**欄**
- **第四代假說**:mid-261 re-enable 殘相位 → clear 段欄選擇錯指主欄(0-7)而非副欄(8)
  → FF 灌爆主 OAM。PPUSim 標尺:真矽 clear 只進副欄(oam.cpp:OB_OAM=NOR(n_PCLK,BLNK)、
  out_latch=MUX(BLNK,…,DB);obj_eval.cpp:OAP=NAND(OR(n_VIS,H0_DD),blnk_latch.nget()))

## 3. 工具與現場(全在工作樹,⚠ 未 commit)

- **探針艦隊**(WireCore.System.cs,全 OB_DEBUG 閘後,硬編碼 Time 窗):
  [sp] $2001 落點+rendering 邊沿 | [sq] copy/eval 上升沿(窗 32.87-33.6M / 35.02-35.7M)
  | [sr] eval 狀態@h66/340 | [ss] rendering 家族@h30/64/66 | [su] not_rendering 閂鎖簇
  (32.82-32.88M / 35.012-35.036M)| [sv] DMA 源頁+$2004 字節(35.008-35.015M)
  | [sw] spr_d@v5 h60-80(32.892-32.893M / 35.0362-35.0373M)| [sx/sy] OAM 寫撥桿
  (18.5325-18.5334M / 35.0086-35.0095M / 35.0216-35.0364M)| [sz] 列+欄@clear
  (32.878-32.8787M / 35.0222-35.0228M)
- **時間軸錨點**(determinstic,重跑不變):stunt DMA=35008667、enable=35021675
  (v261 h274)、stunt 幀 v0=35022208、v5 blank 寫入=35035979/35036123、
  v5 eval=35036372;control 幀 v0=32878016、v5 eval=32892164
- **孤立 ROM**:AprAccuracyCoinUnattended/AccuracyCoin_StaleSpriteShiftRegs.nes
  (page18 item2,已入該 repo;該 repo 可 push)
- **標準跑法**(~13-15 分/輪):
```
DLL=src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll
OB_DEBUG=1 timeout 2400 dotnet "$DLL" --test AprAccuracyCoinUnattended/AccuracyCoin_StaleSpriteShiftRegs.nes \
  --ac-verdict --joypad --callback-drain-limit 2000 --reset-hold-extra 1 --pin 2 \
  --system-def-dir AprVisualBenchMark/data/system-def --max-frames 200 > temp/ac/stale/sXX.log 2>&1
```
- **AprNes 神諭**:`AprNesRef/AprNes/bin/Release/AprNes.exe --rom <rom> --dump-ac-results --dump-debug`
  (AprNesRef 只 commit 不 push!)

## 3.5 最新戰況(交接後補記,2026-07-15)

- **[sz]+cols 判決:假說四陣亡** —— 兩幀 clear 都打欄 8(副欄),clear 無辜;
- **現行輪([sz] 改裝=主欄獵捕)**:全時窗 35.0086-35.0364M,只記「主欄 0-7 有活動」
  的寫撥桿開火(rows+cols+座標);log = `temp/ac/stale/s1_sz3.log`。
  判讀:DMA 段(35.0087-35.0210M)的主欄寫是合法基準(col=addr&7、row=addr>>3,
  應見 256 筆);**DMA 結束後任何主欄開火 = 現行犯**(budget 320,DMA 吃 256,
  餘 64 抓犯罪開頭足夠)。若後段零主欄開火 → 寫入根本沒進 cells?回頭查 DMA 段
  主欄寫的「實際列/欄 vs OAMADDR 預期」是否錯位。

## 3.6 破案級發現(主欄獵捕輪判決)

- **stunt DMA 的 256 筆寫入全部 `rows=[]`** —— 沒有任何 OAM 列線打開,寫入進虛空!
  (欄線走訪 0-7 正常、寫撥桿正常 —— 死的只有列選擇)
- DMA 後**零**主欄開火 → 「存了被沖」不成立;v=5 eval 的 FF = 「無列開啟」的
  預充電匯流排預設值 —— **同一根因解釋寫失敗 + 讀 FF + 零 copy**
- 同幀 clear(v=0)列線正常走 0→31(col 8)→ 列解碼器本體活著;
  死的是「**CPU $2004 存取路徑的列開啟**」,且時間相關:健康 DMA(v=243,
  剛進 blank)列線正常存入;stunt DMA(v=257,**長 blank 深處、跨 2+ 幀**)列線全閉
- 新假說(第五代):$2004 存取的列開啟依賴 OFETCH/W4-FF 類 handshake
  (PPUSim oam.cpp:W4_FF、OFETCH_FF),某個動態閂在長 blank 中「餓死」
  (capture-once / 需要週期性活動保持 arm 的結構)→ A 類味道 → 修法 = 機制級 shim
  (照 2026-07-15 定案:戰役期一律 shim)
- **確認輪跑動中**(log=temp/ac/stale/s1_sz4.log):健康 DMA(18.53M)的列線基準
  + 兩幀 v=5 讀取時的列線([sw] 加 rows 欄位)。判讀:健康 DMA rows 應顯示
  walking row(addr>>3);stunt v=5 讀取 rows 應=[](confirm);control v=5 讀取
  rows 應有活動列
- 下一步:解剖 spr_row 驅動鏈上游(CPU 存取路徑的列開啟閘)找出餓死的閂,
  對照 PPUSim oam.cpp 的 W4_FF/OFETCH_FF 定 shim 施力點

## 3.7 校正 + cell 直測輪(最後在飛)

- **§3.6 的 rows=[] 是取樣相位假象**:健康 DMA(確實有存入)寫入時同樣 rows=[],
  control 成功讀 $05 的瞬間也 rows=[] —— 列脈衝窄,取樣點錯過;列選擇理論作廢。
  僅存事實差異:cells 內容 control=05C503FE、stunt=FF。
- **cell 直測輪跑動中**(log=temp/ac/stale/s1_sc1.log):row0 的 12 顆 cell 候選
  (ppu.#3028/#3066/#3120/#3156/#3202/#3240/#3285/#3318/#3363/#3409/#3463/#3495,
  來自「gate=spr_row0 的 132 顆管」解剖,#3xxx 側=cell、對側=欄位線)在三時點
  (35008600 pre-DMA / 35021200 post-DMA / 35036320 v=5 eval 前)快照。
- ⚠ 已知風險:[sc] 用 `Time ==` 精確等號觸發,若 DmaProbeStep 的步進跳過該值會
  整輪空白 —— 若 log 無 [sc] 行,改成範圍觸發 + one-shot 旗標重跑。
- 判讀:post-DMA cells 有資料、v=5 變 FF → 「存了被吃」(row-open charge-share,
  嫌疑=blank 期無 refresh 的列開啟);post-DMA 就 FF → 存入失敗(回頭追寫路徑的
  cell commit);cells 三時點不變 → 這 12 顆不是目標字節的 cell(換 col 映射再選)。
- 修法提醒:2026-07-15 定案 —— 查明後一律機制級 shim(通則推遲到全修完)。

## 3.8 cell 直測結果(本 session 最終數據,log=temp/ac/stale/s1_sc1.log)

```
pre-DMA  (t=35008600): 0 1 0 1 0 1 0 1 1 0 1 0
post-DMA (t=35021200): 0 1 0 1 0 1 0 1 1 0 1 0   ← 與 pre-DMA 完全相同!
v=5 eval (t=35036320): 0 1 0 1 0 1 0 1 0 1 0 1   ← 末 4 顆翻轉(成對:10→01, 10→01)
```

**初步判讀(需下一步驗證後才可定論):**
1. **stunt DMA 對這 12 顆 cell 零改變** → 傾向「存入失敗」分支(寫入根本沒進 cells)
   —— 但前提是這些 cell 屬於 DMA 目標字節,**候選的 (欄,位) 映射未定**;
2. 0101 交錯 + 成對翻轉 → cell 疑似互補對儲存(cell/notcell),末 4 顆 = 2 個實際位;
3. 末 4 顆在 re-enable 窗翻轉 → 這 2 位可能是 row0 的**col-8(副欄)cell**,被 v=0-4
   的 clear 合法寫 FF —— 若成立,則「主 cell 不動 + 副 cell 有動」= 存入失敗說的旁證。

**下一步(下個 session 第一動作):**
1. 把 12 顆候選映射到 (欄,位):解剖每顆的成對節點(#243/#274/#281... 欄位線側)
   → 這些欄位線接到哪個 col-mux / col 索引;確認哪些屬 col0-3(sprite0 的 Y/tile/attr/X)
   哪些屬 col8;
2. 映射確認「主 cell 在 DMA 中不動」後 → 解剖 $2004 寫路徑的 cell commit
   (欄位線 → row0 pass 管 → cell 的群組解算),在寫入瞬間粒度(t=35008763 附近
   逐半週期)對照健康 DMA(t=18532724 附近)—— 找出兩者在 cell commit 上的差異
   (嫌疑:寫脈衝與 row 脈衝的相位對不上 = 又一場 settle 內時序賽跑,A 類);
3. 修法照定案:機制級 shim。

## 4. 決策樹(欄線數據到手後)

1. **control 打欄 8、stunt 打欄 0-7** → 假說四定罪 → 解剖欄仲裁控制鏈
   (從 spr_col 驅動器往上:找 OAP 對應節點;PPUSim obj_eval.cpp 的 blnk_latch/OAP 當標尺)
   → 判「連通與矽不符?」:是 → die 幾何三重關(segdefs 頂點 + PPUSim + 硬體行為)
   後補網表(E 類既定例外);否 → A 類(blnk_latch 在 mid-261 enable 吃暫態 =
   capture-once 家族)→ **機制級 shim**(2026-07-15 使用者定案:修測試階段一律 shim、
   全域生效、不認測試名;通則機制推遲到全部修完後的 Accuracy Epoch —— 記憶
   shim-first-generalize-later)
2. **兩幀欄相同(都欄 8)** → 副欄無辜 → 擴窗掃 v=261 h274-340(fetch 段)+
   v=0-4 全段的欄活動,找「主欄何時被誰開」(主欄開 + OB=FF 的瞬間 = 犯罪現場)
3. **修好後標準驗證**:孤立 ROM $48F=$01(=神諭)→ 金 checksum(**配方必帶
   --extra-ram**,300k=0x794A43ABDF169ADA、fast-path 3929)→ 六顆哨兵
   (OpenBus/LAE/兩Abort/IDR/APURegActivation+本顆)→ 下次掛牌跑(**--max-frames 12000!**)
4. **連動檢查**:修好後 OAM_Corruption($47B)行為可能改變(同街區),重看一眼

## 5. 剩餘戰場(依既定難度排序)

StaleSpriteShiftRegs(本案)→ Address2004 err10 → BGSerialIn(小心 ~1-dot 相位債)
→ ALERead(大魔王:octal latch 回授迴圈)→ OAM_Corruption(忠實偏差候選,需使用者拍板)

## 6. 慣例提醒

繁中對談 | commit+push 成對(AprNesRef 例外:不 push)| Zen2 不鎖頻 | 單跑者規則
(build 前確認無 sim 跑;殺跑後 taskkill 孤兒 dotnet)| 探針三規則 + 本役新增:
相位訊號別進邊沿觸發集、匿名/特殊字元節點名的 regex 字元類要含 `+.-`、
下結論前先查探針預算 | scratch 進 temp/


---

# 【2026-07-15 續戰:Test 2 已破,Test 3 前線】

## A. Test 2 破案 + 修復(已 commit+push)

**根因(A2 時窗重疊)**:2C02 主 OAM 的 cell 是**無 pull-up 的交叉耦合對** = 動態 cell,
「1」靠位元線預充電(`pclk0` 閘接 vcc)+ 讀取緩衝器回寫刷新維生(DRAM 式 sense-and-restore)。
在 `rendering_1` 落 0 的**同一個半週期**,`spr_row0` 與 `spr_col0` 同時開啟,而位元線還帶著
clear 相位的 `$FF` 圖樣、緩衝器來源已切到外部匯流排 → **$FF 被刷進 sprite 0**
(`05 C5 03 FE` → `FF FF E3 FF`)→ eval 找不到 in-range sprite → 無 hit。
真矽有傳播延遲(列線先關)→ 不會毀損。

**真機標尺(AC OAM_Corruption 測試自帶規格)**:毀損只在**重新啟用**時發生,
且是 `OAM[seed*8+i] := OAM[i]` 的複製 —— **row 0 是來源,永不被毀**;原文:
「OAM Corruption cannot affect the outcome of a (non-arbitrary) sprite zero hit」。

**修法**:`EnableOamBlankEdgeShim()`(機制級、全域、env `NO_OAMEDGE_SHIM`)——
鏡射被定址的 OAM 列,在渲染關閉邊沿還原。
**驗證**:OAM 傾印確認 sprite 0 存活;`$48F` err2 → **err3**;金 checksum 不變(3929)。

## B. Test 3(現行前線)—— 這顆測試的本體

**機制**:渲染在第 4 行末關閉時,sprite shifter 與 BG shifter 都「滿載」第 5 行的資料;
blank 期間兩者凍結(**但 X 計數器照數** —— 這正是 Test 2 的發現);
X=$30 的計數器在 blank 中數到 0 → 進入 **halted**(繪製)模式;
第 14 行末重新啟用 → 第 15 行畫的是兩份**陳舊**資料的疊加:sprite 0 從 x=0 立刻畫出、
BG 吐出第 5 行的舊圖塊(左上角方塊不透明)→ 重疊 → hit。
姊妹測試「Stale **BG** Shift Regs」S1 已通過 → BG 側凍結正確,**破在 sprite 側**。

**已解剖的關鍵節點**:
- `spr0_p[7:0]` = X 下數計數器(+ `_next`/`_out`/`_borrow` 進位鏈);
- `spr0_active` = halted/繪製模式旗標。驅動鏈直接印證真機規格:
  `t14682` gate=`hpos_eq_339_and_rendering_and_/spr0_p7_borrow_and_pclk0` → 拉低 active
  = 「**渲染中**的 dot 339 才重設回 counting」(blank 中不重設 → 維持 halted ✓);
- `spr0_c0/c1` = 圖樣輸出位(由 `spr_d0` 經 fetch 相位 load 閘寫入的動態閂);
- `spr_slot_0_opaque` = sprite 0 此像素不透明 → 餵給 sprite-0 hit。

**測得**:blank 期間 cnt=$00、act=1(模式正確!)→ 破法**不是**計數器/模式,
懷疑圖樣被移光或動態衰減。

**⚠ 探針陷阱(已繳學費)**:`Sync_ToLine0Dot1` → `Sync_ToPreRenderDot324` 內部會
**反覆開關渲染 3 輪**(各等一幀)判別偶奇幀對齊 → 時間軸被推遲 ~6 幀,
且 vblank 中的 disable 會混淆「戲法偵測」。現行探針改用**自我定位**:
只認「畫面中段(v=1..200)的 rendering 1→0 邊沿」= 戲法簽名。

**在飛**:bxsgyk9xy → `temp/ac/stale/s1_t3d.log`(逐行 cnt/act/c0c1/opaque/hit)。

## C. 修完後的收尾清單

1. 六顆哨兵回歸(OpenBus/LAE/兩 Abort/IDR/APURegActivation)+ 本顆;
2. 金 checksum(`--extra-ram`,3929);
3. **ReportAC 第十章戰記**(使用者指定)+ 發佈;
4. 檢查 **OAM_Corruption($47B)** 是否連動(同街區;真機規格已在手,
   若 S1 缺「row0→row_seed 複製」語意需另補);
5. 下一顆推薦:**Address2004 err10**(錨點最好)。


---

# 【Test 3 根因定案:$2001 生效早了 3 個 dot(對齊債)】

## 逐 dot 鐵證(`temp/ac/stale/s1_t3e.log`)

```
RE-ENABLED at v=14 h=337   cnt=$30 act=1      ← S1 的渲染在 dot 337 恢復
v=15 h=000  cnt=$30 act=1  use0=0 opq=0
v=15 h=001  cnt=$30 act=0                     ← 計數器被重設回 counting!
v=15 h=002  cnt=$2F  ... 逐 dot 下數 → sprite 畫在 x=48
```

## 真機規則(AC 測試 Test 5 註解自帶)

- sprite 的 shifter counter 有兩種模式:**counting**(下數)與 **halted**(繪製中);
- **「渲染中的 dot 339」才會把 counter 設回 counting**;若 dot 339 時渲染是關的,
  counter 維持先前狀態(通常是 halted);
- Test 3 的設計:渲染在 **dot 340** 才恢復 → dot 339 仍關閉 → counter 維持 halted
  → 第 15 行 sprite **立刻從 x=0 畫出**,壓在**陳舊 BG shifter** 吐出的左上方塊(不透明)
  → sprite-zero hit ✓。

## S1 的偏差

`$2001` 寫入生效**早了約 3 個 dot**(337 vs 340)→ dot 339 變成「渲染中」→ counter
被重設 → sprite 跑到 x=48(BG 空白)→ 無 hit → err3。

**歸類**:不是新 bug,是撞上**已知系統性債務** —— CPU/PPU 跨晶片 ~1-dot 絕對相位偏移
(KB §2.5,NMI-edge/even_odd 零交集的同一個根)+ `$2001` 寫入延遲(真機 2-5 PPU cycle,
依 clock alignment 而異;BGSerialIn 的註解也明說這點)。

## 為何不在此冒進修

動這個全域時序參數會牽動**對齊敏感家族**(8 顆 NMI-edge + even_odd + 本顆 + BGSerialIn)。
六顆哨兵擔保不了,**必須整套旗艦跑驗證**(8h)。建議與 BGSerialIn(同樣吃 $2001 延遲)
一起當「**對齊/寫入延遲校準**」專案處理,或等 Accuracy Epoch 的延遲原語(L1-f)落地。

## 現有 PpuWriteDelay shim

`EnablePpuWriteDelay(_ppuWriteDelayHc)` —— 目前是**窄窗**版(vpos261/hpos338-339,
為 even_odd 校準,預設 16 hc)。通則化成「全域 $2001 生效延遲」是候選修法,
但需完整回歸。
