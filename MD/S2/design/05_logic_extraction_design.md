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

**結論(初版,full_palette)**:GO 訊號,但廣度待確認。

### 多 ROM 確認(2026-06-01,決定性)

| ROM | observed | **clean %** | stateful | cpu % | ppu % |
|---|---|---|---|---|---|
| full_palette(靜態) | 6,833(46% live) | 97.5% | 172 | 95.8 | 99.0 |
| cpu.nes(全指令,1M hc,mapper1) | 7,074(46%) | 95.8% | 296 | 93.4 | 98.8 |
| **SMB(遊戲,3M hc,mapper0)** | **11,892(78% live)** | **96.6%** | 409 | 91.7 | **99.3** |

**SMB 把廣度補上了**:真實遊戲活化 **78% 的晶片(11,892 節點)**,其中 **96.6% 是乾淨 radius-1 boolean**。
- **PPU 99.3% clean** —— PPU 幾乎全是乾淨邏輯(計數器/shift register/比較器),極適合 oblivious 編譯。
- **CPU 91.7% clean** —— 那 ~8% 不乾淨的,正是 6502 著名的類比/狀態點(ALU carry chain、decode PLA、動態節點)→ 這些就是要留 switch-level 的 **analog islands**。
- 三 ROM 的 clean% 一致(95.8–97.5%),結論穩健;radius-1 是下界 → 真實覆蓋率 ≥ 此。

**最終判定:GO。** boolean 覆蓋率**高(~96.6%)且廣(78% 晶片)**,analog 島小(~3–4% 總體,集中在 CPU)、
可走 hybrid。Escape-1(自動邏輯抽取 → oblivious 編譯 + analog 島留 switch-level)**可行,5–10× 可信**。
殘留:SMB 未活化的 ~22% 節點(低活動,影響速度小;若啟用時偏差,Dynamic Miter 會抓到 → 退回 switch-level)。

## 5c. 抽取器 + levelizer(2026-06-01,已建並跑)

建 `WireCore.Extract.cs`(`--extract`:跑 coverage 收集真值表 → 抽取 + levelize)。SMB 3M:

| | 數值 |
|---|---|
| clean(一致)節點 | 11,483 |
| **完整真值表(直接可編譯)** | **2,400**(16.3% live) |
| clean 但組合未全觀測 | 9,054(需結構抽取補完) |
| K>24 太寬(經驗 TT 不可行) | 29 |
| **可 levelize(無環)** | **2,102 / 2,400 = 88%,13 層** |
| 組合環(未切 state element) | 298 |

**判讀**:管線跑通,**levelization 可行且淺(13 層,88%)** → oblivious 編譯有利。但**經驗式 TT 只完成 16%**
(SMB 4 frame 未跑遍所有輸入組合)→ 真正抽取要靠**結構式(PullDownCond,從網表算完整 boolean)**,不依賴
觀測全組合。298 組合環 = 待切的 state element。**下一步:結構抽取器 + state 切割**。

## 5d. Dynamic Miter 跑通(2026-06-01,決定性訊號)

建 `WireCore.Logic.cs`(oblivious 邏輯引擎 + Miter)+ 把 `ExtractModel` 持久化模型(`BuildLogicModel`:
`_logicIsExtracted` / `_logicOrder` 拓樸序 / `_logicTTBase`+`_logicTT` dense 真值表)+ `--miter`(phase 1 收
coverage+抽取建模,phase 2 golden 每半週期跑一步 → oblivious sweep 比對)。**SMB 200k(100k 建模 + 100k miter)**:

| | 數值 |
|---|---|
| oblivious 評估節點(levelizable+完整 TT,K≤16) | 1,263 |
| 逐 (node,hc) 比對 | 126,300,000 |
| **MATCH golden** | **96.95%** |
| **完全不發散的節點(真組合)** | **1,026 / 1,263 = 81%** |
| 曾發散(= 待切 state) | 237 |
| oblivious sweep 速度 | 5,289 ns/半週期(239 M node-eval/s) |

**判讀(關鍵)**:
- **81% 抽出節點 levelized 評估完美重現 golden** —— oblivious 邏輯抽象對「真組合多數」是**忠實的**(行為正確路線成立)。
- 237 發散節點**自動被 Miter 定位**,top divergers = `ppu.+hpos0_int`/`ppu.++hpos0_2`/hpos0…(PPU 水平計數器
  回授位元,每半週期 toggle ~50%)→ **正是 radius-1 經驗覆蓋率漏掉的隱藏狀態(state element)**。Miter 把「假
  乾淨」的 state 精準揪出 —— 這就是它的價值(verify-then-enable 的動態版)。
- 這直接定義步驟 4:**把發散節點切成 register(讀前值、時脈邊緣更新),從 oblivious 組合 sweep 移除**。

## 5e. 自動 state-element 切割 + 重驗(2026-06-01,建構即正確)

核心洞見:發散節點是 **NMOS 動態 latch**(pass-gate 關閉時 hold 前值);經驗 TT 只抓「被驅動」值,沒抓 hold,
oblivious sweep 每週期錯誤重驅。自動分離:對每節點**用 golden 輸入**算 `TT[golden inputs]` 並逐半週期比對
golden;曾不符 → 真 state element(切 register);恆符 → 純組合(發散只是上游污染)。`--miter` 改三段:
build → window A(辨識)→ refine → window B(重驗)。**SMB 300k**:

| 階段 | 結果 |
|---|---|
| Window A 原始 miter(1,281 節點) | 96.99% match,237 發散 |
| **自動辨識 state element** | **156**(self-stateful on golden inputs)→ 切 register boundary |
| 精煉 oblivious 集 | **1,125 節點** |
| **Window B 重驗精煉集** | **100.000% match,0 mismatch,0/1,125 發散** ✅ |

**精煉後的 oblivious 集「建構即正確」**:1,125 節點 levelized 評估在 84.4M 次比對逐位元重現 golden。237 發散裡
156 是真 state、81 是下游污染(切 state 後自動恢復)→ 分離法正確。**verify-then-enable 的完整實現:只有對
golden 輸入恆正確的節點才留 oblivious,其餘自動退成 register/switch-level 邊界。**

**誠實的限制(下一個賭注)**:目前 oblivious 集只 1,125 / 14,684 live = 7.7% —— **瓶頸是經驗真值表只完成 ~10%**
(SMB 4-frame 沒跑遍所有輸入組合;coverage 說 ~96% 可乾淨,但 5,156 個 clean 節點「組合未全觀測」)。要把
oblivious 集從 1,125 推向 ~11,000,必須 **結構式抽取**(從網表算完整 boolean,不靠觀測全組合)。
另:sweep 是解譯器(259 M node-eval/s);最終速度要靠 oblivious **編譯**(直線 bitwise)—— 兩者都待做。

## 5f. relaxation + 全 clean 集 + verify-then-enable 收斂(2026-06-01)

**關鍵發現**:放寬到全部 6,562 clean 候選後,strict levelize 從 1,281 **崩到 117** —— 每個 pass transistor 製造
雙向 2-cycle(A、B 互為 far-end),datapath 變成大 SCC。這正是先前「94% bidirectional SCC 牆」。

**解法:不要求 acyclic,改 iterative RELAXATION**(對全候選反覆 sweep 到 fixed point)。2-cycle 是「pass gate
開時 A=B」的條件式合併 → relaxation 會收斂;真雙穩態 latch 不收斂 → 被 self-stateful 切掉。再加三段管線:
**build(coverage 建 TT)→ learn(快速 golden-pass 線上學 TT + 辨識 state)→ validate(凍結 relaxation 量)**,
最後對 relaxation 發散者做 **verify-then-enable 迭代降級**直到 fixpoint。**SMB 1M(build 300k / learn 490k /
validate)**:

| 階段 | 結果 |
|---|---|
| relaxation 候選集(含 cyclic pass net) | 6,562 節點 |
| 收斂 | **平均 5.5 iters/半週期,0 個未收斂** ✅ |
| self-stateful 切 state element | 256 → 6,306 |
| VALIDATE-0(凍結) | 99.59%,274 發散 |
| **發散者迭代降級** | refine-1 切 274 → 6,032;refine-2 切 0(穩定) |
| **VALIDATE-final** | **6,032 節點(41% live),100.000% match,0/6,032 發散** ✅ |

**結論**:Escape-1 管線**端到端跑通且全自動** —— 把晶片 **41% 的節點**抽象成 oblivious 邏輯,relaxation 收斂、
**行為逐位元重現 golden(out-of-sample window)**。274 降級基於 window-0,在 window-1 驗 0 發散 → 非 in-sample 過擬合。

**誠實的速度現實(下一個牆)**:目前 relaxation 是**解譯器**:6,032 節點 × 5.5 iters ≈ 33K node-eval/半週期 @
88M/s ≈ **375µs/hc,比 golden 的 12.5µs/hc 慢 ~30×**。要快必須:(a) oblivious **編譯**成直線 bitwise(消解譯
開銷),且(b) 剩下 ~8,400 個未建模節點(unobserved/wide/analog/state)仍需 switch-level —— **殘留 switch-level
比例才是加速上限**。覆蓋率(holes 仍 ~0.5%)與殘留量靠**結構式抽取**補。

## 5g. 活動量天花板 + 殘留拆解(2026-06-01,翻轉結論)

量 oblivious 集涵蓋多少 golden **狀態變化**(真正的 per-cycle 工作),三分類(SMB,validate window 35k hc):

| 類別 | 佔活動 | 成本 |
|---|---|---|
| oblivious 邏輯節點 | **56.8%** | 編譯 bitwise(便宜) |
| 切出的 state 節點 | **10.4%** | register 更新(便宜,若建模) |
| 真正殘留 switch-level | **32.7%** | switch-level |

→ 樸素 Amdahl 天花板 **~3.1×**(假設 oblivious+register 免費)。**但**殘留再拆解(by cause):

| 殘留成因 | 佔殘留 | 可約? |
|---|---|---|
| wide(>16 輸入,dense TT 放不下) | **55.8%** | ✅ 可約:bus/高扇入 → 結構式/bus 模型 |
| clean 但非候選(K>16) | **29.0%** | ✅ 可約:結構抽取 |
| stateful @ radius-1 | 11.7% | ✅ 可約:deeper-radius / register |
| **no-channel / supply / 真 analog** | **3.5%** | ❌ 真正不可約 |
| 子系統:cpu 5.8% / ppu 22.7% / **other 71.5%** | | |

**翻轉結論**:真正不可約的 analog 只佔殘留 **3.5% = 總活動 ~1.1%**。目前的 3.1× **不是架構極限,而是 dense-TT
K≤16 造成的假牆**。殘留 96.5% 可約 —— 最大槓桿是 **wide/bus 節點(殘留 55.8%)**:它們不是 2^60 dense TT,而是
wired-OR/tristate **bus**,需結構式表示。理想上把殘留壓到 ~1% → Amdahl 天花板可達**數十倍**。

**這正當化「結構式抽取器」(原步驟 3)為下一步**:數據證明它能把殘留 32.7% → ~1-5%,直接攻 85% 殘留。
(誠實註:這是 idealized Amdahl;真實還要扣 relaxation 5.5× 迭代、residual 與 oblivious 的 group 糾纏無法乾淨
切離、register 建模成本。但「不可約只有 ~1%」這個下界是穩的、可證偽的。)

### 5h. Sparse 擴充(K≤60)驗證單調性(2026-06-01)

把候選從 dense K≤16 擴到 **sparse K≤60**(17-60 輸入用 `_covMap` Dictionary 查,key 仍打包成 ulong,不需 2^k 記憶體):

| 抽取技術 | oblivious 活動覆蓋 | 殘留 switch | Amdahl 天花板 | match |
|---|---|---|---|---|
| Dense TT(K≤16) | 56.8% | 32.7% | 3.1× | 100% |
| **+ Sparse(K≤60,+109 節點)** | **66.0%** | **23.2%** | **4.3×** | **100%** |
| (預估)+ Bus 模型(>60) | ~90%+ | ~5% | ~20× | — |
| 不可約底線 | — | ~1% | ~90× | — |

加 sparse 後「clean 但非候選」殘留歸零;殘留現由 **wide bus(>60 輸入,78.6% 殘留,板級 "other" 91.6%)** 主導 ——
即系統 data/address bus。**單調驗證**:每加一種抽取技術,殘留縮、天花板升、且 verify-then-enable 保證全程 100%
正確。**bus 模型是最後一個大槓桿**(把殘留壓到接近 ~1% analog 底線)。

### 5i. BUS 模型 —— 決定性突破(2026-06-01):天花板 4.3× → 20×

關鍵:golden group-resolver(`WireCore.Group.cs`)的語意是 —— 透過 ON channel **遞移**走連通群,OR 起靜態 flags
(PullUp/Pwr/Gnd/SetHigh/SetLow)+ ON 的 gnd/pwr channel,查 256-entry `FlagsToState` LUT 解析;全 None(浮接)
則「最大電容(NodeConnections)成員的現值」勝(這就是 hold)。**State flag 在 runtime 不維護**。

`BusResolve(nn)`(`WireCore.Logic.cs`)**完整複製這個 group walk,但讀 hybrid 狀態**(extracted 讀 LogicState、
boundary 讀 golden)。模型正確時逐位元 == golden 的 `ComputeNodeGroup`。wired-OR/tristate bus(>60 電晶體)就此
結構化解析,**不需 2^K 表**。把 132 個 wide bus 節點納入 oblivious 集:

| 抽取技術 | oblivious 活動覆蓋 | 殘留 switch | Amdahl 天花板 | match |
|---|---|---|---|---|
| Dense(K≤16) | 56.8% | 32.7% | 3.1× | 100% |
| +Sparse(K≤60) | 66.0% | 23.2% | 4.3× | 100% |
| **+Bus(+132 結構式)** | **84.3%** | **5.0%** | **20.1×** | **100%** |
| (下一步)+register 建模 | ~95%+ | ~1% | ~90× | — |

**oblivious 84.3% + 切出 state 10.7% = 95% 活動已乾淨建模、且逐位元重現 golden**(verify-then-enable:refine-1 切
282 → 6,265,refine-2 切 0;relaxation 5.66 iters 收斂、0 未收斂)。殘留 5% 裡僅 ~22.8% 是真不可約 analog
(no-channel/supply),其餘是 radius-1-stateful(register 建模可再縮)。**結論:整顆晶片約 ~99% 活動可約成
邏輯+register,真 analog 只 ~1%。** 速度天花板的「覆蓋率」維度已徹底回答。

### 5j. stateful 節點也走 BusResolve → 殘留壓到 analog 底線 1.1%(2026-06-01)

洞見:動態 latch = 「pass gate 關時浮接 hold」,而 BusResolve 的浮接 tie-break(最大電容成員持值)正是 hold。
所以把**所有 stateful 節點(radius-1-stateful + self-stateful)也走 BusResolve**,只有 BusResolve 在 relaxation
下仍發散的(真 analog / edge-timing 相依序向)才退成 boundary。`_covStateful` 加為結構候選 + self-stateful 轉
structural;verify-then-enable 收斂(refine-1 切 1,106 → refine-2 切 7 → refine-3 切 0)。**SMB 1M**:

| 活動分類 | 佔比 | 成本 |
|---|---|---|
| oblivious-logic | 76.5% | 編譯 bitwise(便宜) |
| cut state-element | 22.4% | register 更新(需建模) |
| **truly-residual switch** | **1.1%** | **100% no-channel/supply/analog —— 真不可約** |

→ **Amdahl 天花板 ~88×**;100.000% match golden;relaxation 6.51 iters 收斂、0 未收斂。

**結論(完整)**:殘留 switch-level 砍到 **1.1% 且 100% 是純 analog** —— 徹底確認「整顆晶片 98.9% 活動可約成
邏輯 + register,真 analog 僅 1.1%」。但此步把 ~12% 從「便宜 oblivious」移到「需建 register(22.4%)」:那 1,106
個在 relaxation fixed-point 下發散的節點是 **edge/timing 相依的真序向節點**(relaxation 算 settled 值,抓不到
「transient 是 load-bearing」),BusResolve 的 hold 不足 → 需**真正的 sample-on-edge register 更新規則**。
**∴ 1.1% 底線穩;天花板介於 20×(只算已便宜的 oblivious+bus)到 88×(register 也做成便宜)之間,取決於 register
建模品質。下一步:真正的 register 建模(找每個序向節點的 D/enable/clock-edge)。**

### 5k. Oblivious 編譯(Roslyn 直線 C#)—— 決定性負結果(2026-06-01)

建 `WireCore.Compile.cs`(`--compile`):把 relaxation sweep 發成直線 C#(每節點一條 `tt[BASE+(ls[a]|ns[b]<<1|…)]`,
bus/sparse 走 `EvalBusOrSparse` 回呼),Roslyn in-memory 編譯成 15 個 `Sweep_k(ls,ns,tt)`、relax 到 fixpoint。
**SMB 1M,100.000% match golden(正確、toolchain 跑通)**,但速度:

| | ns/半週期 | vs golden |
|---|---|---|
| **golden 開關級** | **12.0 µs/hc**(83.3K hc/s) | 1× |
| oblivious 解譯器 sweep | 539 µs/hc | **45× 慢** |
| oblivious 編譯 sweep | 1,004 µs/hc | **84× 慢** |

**結論(誠實、決定性)**:編譯路**走得通且正確,但不會更快 —— 反而比 golden 慢 45-84×**。原因:
1. **Oblivious 放棄了 event-driven 稀疏性**:每半週期要算 ~6,000 節點 × 6.5 relax 迭代 ≈ **39,000 evals/hc**,
   而 golden event-driven 只動 ~600 節點(平均 walk 1.4 節點)→ oblivious 多做 ~65× 的節點數。即使每個 boolean
   eval 較便宜,總 cycle-equiv 仍多。**這就是專案早就發現的「oblivious 慢 ~121×」/「94% 雙向 SCC + 稀疏性是牆」**,
   現在用可跑的編譯原型 + 精確數字證實。
2. **編譯版比解譯器還慢 2×**:bus/結構節點(~440 個 BusResolve group walk)是成本中心,走 cross-assembly 回呼 +
   delegate 邊界的開銷,蓋過了 dense 直線化省下的(dense 本來就便宜、是成本的少數)。
3. **LLVM 不會救**:瓶頸是(a)relaxation 迭代數 × dense 覆蓋(演算法層,非 codegen)、(b)bus group-walk
   (資料相依,無法直線化)。LLVM 只優化已是少數成本的 dense 直線部分。**∴ 不值得做 LLVM。**

**Amdahl 88× 天花板是「可約性(覆蓋率)」上界,假設 oblivious eval 免費 —— 但實測 oblivious eval 比 golden 慢 45×,
不免費。** 所以可約性(98.9%)漂亮,但**透過 dense oblivious eval 無法轉成 CPU 速度**。唯一理論殘存路是 event-driven
boolean(只重算變動節點)—— 但那已是 golden 在做的事(math-algos Phase 2 試過,打平),且 bus 仍需 group walk。
**架構結論維持:此管線無法贏過 S1 的 event-driven 開關級;real-time 不可達。Escape-1 的價值是「可約性證明 + 自動
抽取/驗證管線」,不是 CPU 加速。**

## 6. 後續(進行中)
1. ~~抽取器 + levelizer~~ ✅(5c)  2. ~~oblivious 引擎 + Dynamic Miter~~ ✅(5d)  3. ~~自動 state 切割 + 100% 重驗~~ ✅(5e)
4. ~~relaxation 破 SCC 牆 + verify-then-enable 收斂 41%/100%~~ ✅(5f)  5. ~~活動量天花板 + 殘留拆解(不可約僅 ~1%)~~ ✅(5g)
6. **結構式抽取器(最高槓桿)**:從網表 pull-down/pull-up 路徑 + bus 模型算 wide/clean-非候選節點的公式 → 殘留 →~1-5%。
7. register 建模(state 節點獨立更新,脫離 golden boundary)→ 邁向 standalone。
8. oblivious 編譯成直線 bitwise + 截圖/blargg 行為驗收 + 量真實速度 vs S1 ~80K。
