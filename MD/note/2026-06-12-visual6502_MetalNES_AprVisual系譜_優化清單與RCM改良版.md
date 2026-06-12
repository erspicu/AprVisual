# visual6502 → MetalNES → AprVisual.S1:三者關係、各自的優化、與我們原創的 RCM 改良版(2026-06-12)

> 彙整來源:WebSite/comparison.html(系譜與同機數據)、index.html(優化歷程)、rcm-revival.html(RCM 翻盤)、
> CLAUDE.md、MD/note/01-07(MetalNES 研究)、MD/note/06(S1 演算法對照)。本筆記是三者關係的總整理。

## 一、三者的關係(一條演算法的三代傳承)

```
visual6502 (2010, JS)          ── 演算法的發源地:6502 電晶體級模擬、群組解析演算法的原型
        │  chipsim.js 的 recalcNodeList / recalcNode / computeNodeGroup / getNodeValue
        ▼
MetalNES (C++)                 ── 把演算法從「一顆 CPU」擴成「一整台 NES」:
        │  wire_module.cpp 的 wire_compute = chipsim.js 的優化移植
        │  + 模組系統(2A03 + 2C02 + 主機板 TTL)+ 行為式 RAM/ROM
        ▼
AprVisual.S1 (C# / Rust)       ── 我們的工作:先做忠實移植與正確性基建(bit-exact golden、
                                  雙引擎互證),再在演算法與資料佈局上系統性加速 ——
                                  同一台機器上比 MetalNES 快 ~2.3 倍,且逐位元可驗證
```

- **visual6502**:瀏覽器 JS,教育用途。動畫模式約 1 clock/s、expert 模式 ~250Hz+。它定義了這套
  「事件驅動 + 導通群組 BFS + 旗標 OR → 優先序解析(GND 勝 → VCC/上拉 → 外部驅動 → 保持)+
  純浮接群組由最大電容節點保值」的核心演算法 —— 三代引擎跑的都是這一套語意。
- **MetalNES**:S1 的直接參考實作(`ref/metalnes-main`)。我們的 `Sim/WireCore.*` 就是按它的
  `wire_module.cpp` 對照移植的(MD/note/01 有逐函式對照)。
- **AprVisual.S1**:可移植 net10.0 無頭主控台,黃金引擎;`experiment/rust-s1` 是逐位元相同的 Rust 攣生。

## 二、MetalNES 在 visual6502 之上多做了什麼

1. **整機組裝**:不只 6502 —— Visual2A03 + Visual2C02 兩顆晶片網表 + 主機板 TTL 晶片,
   用 `.js` 系統定義(pins / modules / connections / pullups / forceCompute / memory)組成完整 NES-001。
2. **C++ 優化移植**:chipsim.js 的直譯邏輯改為原生資料結構;**256 項 FlagsToState 查找表**
   (旗標 OR 一次查表出值,取代逐優先序判斷)。
3. **行為式記憶體**:RAM/ROM 不用電晶體模擬,掛 handler(callback = 假電晶體機制)—— 省下最大宗的
   不必要模擬量。
4. **ForceCompute 機制**:特定匯流排節點 Gnd+Pwr 互消的特殊解析。
5. 代價/侷限:segdefs 只保留 `'+'` 上拉(丟了 `'-'`)、weak 旗標沒使用、無逐位元等價驗證方法論。
   同機實測 **~55K hc/s**(我們的祖先;VisualNes 同機 ~24K)。

## 三、AprVisual.S1 在 MetalNES 之上做了什麼

### 正確性基建(先於效能,全程護航)
- **逐位元黃金檢查**:NodeStates 全陣列 FNV-1a checksum(300k/400k/1M 三條 golden)+ SMB1 一千萬
  半週期門 + selftest;任何優化必須 checksum 不變才採用。
- **雙引擎互證**:C# 與 Rust 各自獨立實作、輸出逐位元相同 —— 演算法錯誤幾乎不可能兩邊同時犯。
- 修正 MetalNES 的取捨:保留 `'+'`/`'-'` 兩種 pull;明確區分「驅動為高」與「浮接保值」。

### 效能改善清單(出貨者,依時序;量測法 = interleaved-paired + checksum 閘門)

| 層 | 項目 | 增益 |
|---|---|---|
| 資料佈局 | SoA 非託管熱資料(byte* 狀態、16B NodeInfo + **行內 payload**(S2-A)、ushort 電晶體表)、零邊界檢查 | 基底 + S2-A C# +4.18% |
| 載入期 | lowering:常開短路合併 441 節點 / 刪 530 顆電晶體 | +3.7% |
| 走訪 | 迭代化 BFS + 區域變數提升(+3.2%)、64-bit 雙對載入(+1.2~1.4%) | |
| fast-path | cls1 靜態純邏輯 O(1) 解析(+2%,放寬上拉條件再 +1.6%) | |
| **fast-path** | **R-1 動態單例**:c1c2 閘全關 ⇒ 群組必為 {nn} ⇒ O(1) | **C# +18.6% / Rust +12.5%(史上最大)** |
| **剪枝** | **P-1 同態 turn-on 剪枝**(= prune-merge 的正確版:靜態分類取代執行期 GroupID) | **+11.85% / +11.36%** |
| 剪枝 | P-2 關斷隔離、P-3/P-4 電容守衛 turn-on 擴展(合計刪 ~21% 節點重算) | C# +7.7% / Rust +10.0% |
| 微調 | && 條件重排(C# +~1%)、供電跳過折入遮罩(C# +1.5~2%)、.NET 11(+~1%) | |
| **重編號** | **範圍剪枝 + 自我捕捉 locality 鍵(RCM 改良版,見下)** | **C# +3.56%+6.17% / Rust +2.90%** |
| fast-path | **B1 成對路徑**:可證明兩節點群組就地解析(size-2 = 77% 走訪) | C# +0.6% / **Rust +14.45%** |

**同機對照(Ryzen 7 3700X,full_palette,同口徑)**:
VisualNes ~24K < **MetalNES ~55K** < perfect6502 29K(僅 6502,口徑不同)< **S1 C# ~126.7K / Rust ~118.5K hc/s**。
對祖先 MetalNES 為 **~2.3 倍**,且多了逐位元可驗證性。離 NES 實機(42.95M hc/s)仍 ~339×。

## 四、終章:我們原創的「RCM 改良版」

傳統 RCM(Reverse Cuthill-McKee)是按圖鄰接性重排節點編號以提升快取區域性的經典方法 ——
我們 5 月實測它在這顆引擎上**無效**(熱集早已常駐快取,瓶頸是相依載入鏈不是 cache miss)。
6 月的翻盤靠的是**換掉重編號的目標函數**,分兩步,皆為原創形式:

1. **範圍剪枝(range-prune)**:重編號的主鍵改為「靜態剪枝類別」—— 每類佔一段連續 ID 區間,
   熱迴圈的剪枝遮罩查表變成**暫存器區間比較**(每端點刪一條相依載入)。每次 Reset 重算遮罩當
   基準真相逐節點驗證,不符自動退化(JIT deopt 守衛模式)。C# +3.56% / Rust +2.90%,
   跨引擎推導出完全相同的區塊邊界(A=460/S=1275/B=7532)= 免費互證。
2. **自我捕捉初次觸碰 locality 鍵(self-captured first-touch)**:區塊內的次序不用任何靜態近似,
   而是**讓引擎在載入時自己量自己**:三遍載入 —— 分類 → 以暫時鍵重建+暖機+用 settle 迴圈的
   冷儀器化副本捕捉 32K 半週期的「生產級聯真實初次彈出順序」→ 以捕捉順序做最終重建。
   零檔案、零旗標、任何 ROM 通用、構造上免疫於工作負載漂移。C# +6.17%(20/20)。
   關鍵實證:**鍵的價值在「已剪枝級聯的順序」而非快取行密度**(等密度的非生產順序 = ±0)。

先行研究對照(Gemini 諮詢,詳見 MD/note/2026-06-11-論文準備\*):各組件分屬 Inspector-Executor
(Saltz '91)、軌跡導向佈局(Chilimbi PLDI '01)、動態去優化守衛(Hölzle PLDI '92)、ECS archetype
排序等已知譜系,但「**同進程、自包含、事件驅動軌跡捕捉,用於 switch-level 模擬器的圖記憶體重排**」
這個綜合體沒有先例 —— 判定為可發表的原創貢獻(建議標題:*Dynamic Data Relayout via Online
Execution Trace Capture in Event-Driven Switch-Level Simulation*)。

一句話總結三代:**visual6502 發明了演算法,MetalNES 把它組成一台 NES,AprVisual 把它變成
一個逐位元可驗證、快 2.3 倍、並貢獻了原創重排技術的研究級引擎。**
