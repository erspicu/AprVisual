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

## 4. 決策樹(欄線數據到手後)

1. **control 打欄 8、stunt 打欄 0-7** → 假說四定罪 → 解剖欄仲裁控制鏈
   (從 spr_col 驅動器往上:找 OAP 對應節點;PPUSim obj_eval.cpp 的 blnk_latch/OAP 當標尺)
   → 判「連通與矽不符?」:是 → die 幾何三重關(segdefs 頂點 + PPUSim + 硬體行為)
   後補網表;否 → A 類(blnk_latch 在 mid-261 enable 吃暫態 = capture-once 家族)
   → 通則機制或機制級 shim(照政策:shim 全域生效、不認測試)
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
