# Escape-1 設計:自動邏輯抽取 + oblivious 編譯(CPU-first,行為正確、非 bit-exact)

> 來源:Gemini 3.1-pro 諮詢(prompt/原文 log:`tools/knowledgebase/message/`)+ 我的評估 + 我們的環境。
> 使用者定向(2026-06-01):bit-exact switch-level 已確認撞 ~80K 天花板(見 `ceiling.html` / `proto/03`)。
> 新目標 = **自動**把網表抽象成邏輯、跑更快,**接受失去逐節點 bit-exact,但要求行為正確**(framebuffer/截圖
> 一致 + blargg PASS),且**全自動**(非人工)。

## 0. 核心洞見(Gemini,我同意)

**純靜態分析(從無電容值的網表證明 charge-share/clock/state)是研究死路。** 但我們有不對稱優勢:
**Golden Simulator** —— 用**實證動態分析**(在 golden trace 裡觀察)取代不可判定的靜態分析。不需「證明」
某節點是動態 latch,直接「觀察」它。

## 1. Gemini 的管線(Empirical-Structural,全自動)+ 我的評估

| 階段 | 方法 | 我的評估 |
|---|---|---|
| **類比塊偵測** | 每節點猜 boolean 函數,拿 golden trace 比對;**預測失敗 = 類比島**(charge-share/ratioed/6502 ALU carry)→ 留 switch-level | ✅ 這正是我們的 **verify-then-enable**(已有);「analog = verify 失敗」 |
| **時脈發現** | trace 裡與 CLK 嚴格相位關係的節點 = 內部時脈 → 當 primary input | ✅ 實證、自動,合理 |
| **state element** | Tarjan SCC(靜態回授)+「driver 全斷仍維持值」(動態 latch)→ cut 成 register 邊界 | ✅ 合理;register 不是 analog,是「狀態」,切開即可 |
| **levelize** | cut 在 clock/register/analog 後,剩前饋組合邏輯 → 拓樸排序;排序失敗 = 漏切 state,回 trace 找 delay 節點再切 | ✅ 抽出的邏輯**是前饋的** → 可 levelize → 繞過「raw 94% SCC 不可排序」的牆 |
| **codegen** | 輸出直線 C# bitwise | ✅(見下「為何更快」) |

## 2. 🔑 關鍵翻轉:這裡要 OBLIVIOUS,不要 event-driven(且不矛盾)

Gemini:**這規模下別做 event-driven 邏輯模擬**(queue/分支/指標開銷 > 工作);要 **Oblivious Compiled** ——
~5,000 條 boolean 方程式編成**直線 bitwise**,**每半週期全算一遍、無視稀疏性**。

- switch-level:604 event × ~82 cycle ≈ **50,000 cycle/半週期**。
- oblivious boolean:~5,000 方程式 × ~1–2 cycle ≈ **10,000 cycle** → **5–10×**。

**我的評估(重要):這不和我們「oblivious 慢 121×」矛盾。** 那次 oblivious 的每個 eval 是**昂貴的
switch-level 群解**;這裡每個 eval 是**一條便宜 boolean op**(抽取後)。per-node 成本從貴變便宜,calculus 翻轉。
而且**單一大直線函數是循序串流(prefetch 友善),不是 macro-block 的隨機 dispatch i-cache thrash** ——
同時繞過我們兩個死法。**可信。**「稀疏性在小網表 + 超純量 CPU 上是陷阱」這句也對(分支誤判/指標 > AND/OR)。

## 3. 驗證:Dynamic Miter(自動定位)

golden 與 compiled 並排跑、每半週期比對 register + analog-island 輸入;**第一個發散的 register,bug 數學上
保證在它的組合邏輯錐裡** → 自動定位。**這是 verify-then-enable 的延伸,基礎已有。**

## 4. 誠實的不確定性 / 真正的賭注

**5–10× 可信但未實測。成敗取決於一個數字:boolean 覆蓋率** —— 整顆晶片有多少能乾淨地表示成「其他節點的
boolean 函數」(其餘是 analog island,留 switch-level)。
- 高(>85%)→ 大部分編 oblivious → 5–10× 可期。
- 低 → 太多留 switch-level → 加速有限。
- math-algos 舊結構分類(P2.1)曾給:COMB 62% / SEQ 37%(= register,可當狀態切)/ DYNAMIC 0.9%。
  若 analog 真只佔 ~1%,覆蓋率可能很高 —— **但那是結構分類,不是「boolean 預測 golden」的實證**。

## 5. 第一步(de-risk,本次要做):量 boolean 覆蓋率

不用 dump 巨大 VCD。用**實證一致性探針**(`--coverage`,analysis-only):
- 對每個節點,輸入向量 = 其 channel 電晶體的 **gate 節點狀態 + c1c2 far-end 節點狀態**(radius-1)。
- 跑長 golden trace;若同一輸入向量曾對應**不同**節點值 → 此節點**不是其鄰域的純 boolean 函數**(有隱藏
  狀態/類比/更深相依)→ 標記。否則 → **combinational-coverable**。
- 報告:可覆蓋 % + 依子系統(cpu/ppu)+ 輸入太寬(>cap)的「複雜」節點數。

**這個數字直接決定 Escape-1 的賠率**,且幾乎只用現有基礎(golden + 逐節點比對),零風險、可證偽。
（註:radius-1 一致性是覆蓋率的**下界** —— 有些節點是更深輸入的 combinational,radius-1 會誤判;但作為
第一刀的可行性訊號足夠,且若連 radius-1 覆蓋率就高,結論更強。）

## 5b. 第一步結果(2026-06-01):覆蓋率探針 `--coverage`

實作 radius-1 一致性探針(`WireCore.Coverage.cs`,analysis-only):每節點記「(channel gate + far-end 狀態)
→ 節點值」,同輸入出現不同值 = 標記 stateful。full_palette(30k 與 300k hc,結果穩定):

| | 數值 |
|---|---|
| live 節點 | 14,727 |
| **observed**(有 channel、≤60 inputs、有被 recalc) | 6,833 |
| **CLEAN boolean**(同輸入恆同值) | **6,661 = observed 的 97.5%** |
| STATEFUL/analog(出現矛盾) | 172(2.5%) |
| wide(>60 inputs,未追蹤) | 132 |
| 子系統 clean 率 | cpu 95.8% / ppu 99.0% / other 94.4% |

**判讀(我的)**:**在被 full_palette 活化到的節點裡,97.5% 是乾淨 radius-1 boolean** —— 對 Escape-1 是**強烈正面
訊號**(晶片絕大部分是乾淨邏輯;analog 島只 ~2.5% + 少數 wide,小到可走 hybrid)。30k→300k 不變 = 結論穩。

**誠實的限制**:
1. **只觀測到 ~46% 的 live 節點** —— full_palette 是靜態畫面,只活化約一半;其餘 ~8,000 節點的可覆蓋性**未量**。
   套件只有 `full_palette.nes`(無遊戲 ROM);要 firm up「廣度」,需要一顆會跑多樣 CPU/PPU 活動的遊戲 ROM。
2. radius-1 是**下界**(被標 stateful 的島,其 OUTPUT 可能在更大 radius 下仍 boolean)→ 真實覆蓋率可能更高。
3. 靜態 ROM 輸入變化少 → 抓到的矛盾偏少 → 97.5% 可能略樂觀;遊戲 ROM 會更嚴格。

**結論**:**GO 訊號**(覆蓋率在量到的範圍內極高),但建議用遊戲 ROM 確認廣度後再投入完整抽取器。

## 6. 後續(若覆蓋率夠高)
1. 實作完整抽取器(PullDownCond 通用化 + state/clock/analog 自動標記)。
2. oblivious 編譯(Roslyn / 直線 C#)。
3. Dynamic Miter 驗證 + 截圖/blargg 驗收。
