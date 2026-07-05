# 目標架構:Socket Pattern + 全域 DUT 修正 + Graph Fingerprint

> 來源:Gemini 諮詢 `tools/knowledgebase/q/a_joypad_scoping_20260705.md`(接續
> 前次 dbl2007 探針效應諮詢的同一套通則)。**這份是「後面做法修改的參考」**,
> 不是現況 —— 現況是 per-test scope 的**可行 stopgap**(已修好 joypad 回歸)。

## 核心定調(Gemini)

**per-test 這次「一半對一半錯」**,關鍵在區分兩類變更:

| 變更 | 本質 | 正解 |
|---|---|---|
| **tie 改接**(u7/u8 6 條 vss→vcc)| **DUT 修正**(真機那 6 pin 本來浮接,board def 綁 vss 是**建模瑕疵**)| **全域**移除那 6 條 vss tie(不是 per-test;否則等於用「錯版主機板」跑其餘 138 測)|
| **模組置換**(nes-pad → behavioral)| **Test Fixture 變更**(手把是可拔插外設)| per-test **概念正確**,但要用 **Socket Pattern** 做到零擾動 |

## 方案 1:Socket Pattern(模組置換零擾動)

把「Tail Allocation」升級成「插座模式」:

1. **兩階段載入**:
   - Phase 1(DUT):只載入 NES 主機 + Controller Port(插座邊界),**完全不載入
     任何手把**(閘級或行為級都不);執行 class-major 重編號、鎖死主圖 0~N 的
     id 與 BFS 順序。
   - Phase 2(Fixture):依 `--joypad` 決定是否實例化 `nes-pad-behavioral`。
2. **尾端掛載**:行為手把的所有新節點(含假電晶體)一律分配 **> DUT max id** 的
   尾端編號。
3. **邊界對接**:手把輸出 pin 接到 Phase 1 已定型的 Controller Port 節點。

**結果**:插不插手把,Phase 1 的 DUT 內部 id / 圖 / BFS 展開樹 **100% 同構** →
零探針效應,對齊抽籤絕對不變。→ **手把可回到「全域可用」而不回歸任何測試**。

## 方案 2:tie 改接 → 全域移除 vss tie

- (a)✅ 載入時**不加**那 6 條 vss tie(修正 board def,全域)。
- (b)❌ runtime 覆寫:`Gnd > Pwr`,且強電源覆寫實體接地 = 短路,開關級易震盪。
- (c)❌ per-test:這是 DUT 物理 bug,不能 per-test。
- **代價**:移除 6 條邊會擾動 LS368 附近 BFS → **對齊會重擲**。必須**勇敢承受一次**:
  全域移除 → 接受 ppu_vbl_nmi 家族掛掉 → 重新找/重擲該家族的對齊 seed / judgment
  frame → **bake 新基準**。一次性技術債償還。

  > **註(本專案取捨)**:目前 per-test 讓那 138 個非-$4016 測試留在原始 vss-tie
  > 板 —— 而它們**根本不讀 $4016**,tie bug 對它們不可見 → 功能上正確、且**免重校準**。
  > 全域移除 tie 純為架構純度,但要付對齊重校準(會動到 K=1 基準與 even_odd 工作)。
  > **結論:tie 全域化列為 Socket 重構時一起做,不單獨提前。**

## 方案 3:Graph Fingerprint(防止這類回歸復發)—— 高價值低風險

本次 bug 本質 =「無意間改了圖結構,卻沒意識到要重跑對齊敏感測試」。

- Phase 1 完成(掛任何 fixture 前)算 DUT 指紋:總節點數 + 總邊數 +(嚴謹版)
  排序後 adjacency 的 hash。
- 寫死進測試框架設定;每次跑前驗證。不符 → 丟 `DutGraphMutatedException`。
- 效果:下次有人動 board def / 圖結構,CI 立刻 crash 並指名「對齊已重擲,請重驗
  ppu_vbl_nmi 並更新指紋」。把**未知隨機失敗**變成**已知預期變更**。

## 落地優先序(本專案)

1. **現況(已做)**:per-test scope stopgap —— joypad 回歸已修,ppu_vbl_nmi 回綠,
   免重校準。**先維持**。
2. **近期低風險**:實作 **Graph Fingerprint**(方案 3)—— 這正是能**第一時間攔住
   本次 bug** 的機制,且不動對齊基準。值得優先做。
3. **未來聚焦重構**:**Socket Pattern**(方案 1)讓手把回全域零擾動 + 一併**全域
   移除 vss tie**(方案 2)+ 重校準對齊 + bake judgment frame + 更新指紋。這是
   Gemini 的終極解(「不可變 DUT + 動態尾端掛載治具」),但會動到 K=1 與 even_odd
   基準,須當獨立戰役做。
