# Gemini 諮詢:M1–M7 靜態資料能否反哺熱路徑加速(2026-07-20)

完整回覆 `2026-07-20-gemini-static-data-accel-reply.txt`。背景:效能時代已結案(負面結果,
抽象全慢、只有移除成本才贏)。問:S1a 才產出的 M1–M7 靜態資料,能否解鎖當年做不到的加速?

## Gemini 判決(守住「別加內迴圈檢查」規則)

### ❌ 會輸(加了檢查)
- **M1 強度優先 early-out**:group 均 1.4 節點,多半 size 1(已 fast-path)或 2;為了 early-out 加
  strength fetch + 比較 + 分支,誤預測的 pipeline flush 遠大於「直接 OR 兩 byte + 查 LUT」。違規。
- **M4/M6 相位/latch 閘控**:event-driven 引擎本來就是最佳 phase-gater(輸入不 toggle 就不 enqueue)。
  加靜態相位檢查 = 加內迴圈分支。邊際到負。

### ✅ 真淨加速(用新靜態資料、不加內迴圈檢查)
1. **⭐ 拓撲 bit-vector 佇列(M7)—— 估 15–25%**:丟掉 double-buffer FIFO,換成單一 ~1.25KB bit-vector
   (10,000 bit,常駐 L1);bit index = M7 正準拓撲 ID。enqueue = 無分支 bitwise OR(設兩次=no-op,自動去重);
   scheduler = 用 `__builtin_ctzll` 前掃找下一個。贏在:零佇列溢位邏輯、零重複評估(殺 glitch 重算)、
   拓撲序(上游先於下游)→ 壓平「iterate to quiescence」、每 hc settle 次數大減。M4 latch(環)= 同 level
   或小的次級 bit-vector 掃 back-edge(緊 closing-edge race 很快收斂)。
2. **⭐ 單向 pass-gate 降級(M1/M4)—— 估 10–15%**:MOS 開關雙向才需 BFS/LUT;但用 M1 強度 + M4 拓撲可
   **靜態證明**上千個 pass-transistor 是**嚴格單向**(強驅動→純 gate-cap,或強驅動→更弱下拉)。把 TransistorList
   切成 BiDir(現行 BFS)+ **UniDir** 兩隊;UniDir 的 group resolution 降成純 scalar move:
   `NodeStates[dst]=NodeStates[src]; Enqueue(dst_fanout);`。整段 BFS+flag-OR+LUT 省掉,i-cache footprint 不變
   (純資料驅動,像現有 tagged turn-on gate list)。
3. **M2 電容 tiebreak(冷路徑)—— 1–2%**:別放 BFS;LUT 回「Floating」才掉進冷路徑掃已收集 group 找 max(M2),
   取代連接數的 graph-walk。小移除。

## 對專案的意義(誠實)
- **不推翻「即時不可達」結論**(這些是 10–25%,不是 500×)。但它們是**效能時代唯一沒試過的一類** —— 因為
  M1–M7 靜態資料是 S1a 才有的,當年做效能時根本沒有。→ golden 引擎吞吐**可能**還能往上推。
- **⚠️ 依 [[beat-s1-rule-and-ir-reattempt-plan]]**:大架構改動(尤其 bit-vector 佇列)**須先 minimal prototype
  證快過 S1 + bit-exact** 才動。風險:working set 稀疏時,掃 156 個 64-bit word(多半 0)未必贏小 FIFO ——
  這正是要先原型量的東西。
- 兩個大點子**互相獨立**,可分別原型;unidirectional demotion 較低風險(純多一條 scalar-move 隊,不動佇列結構)。

---

## 實測評估結果(2026-07-20,`tools/eval_*.py`)

兩個點子都在真實網表上用 Python 實測過。**兩個的主賣點都被網表結構打掉**(重新確認「沒有可自動導出的靜態 DAG」那道結束效能時代的牆):

### ① 單向 pass-gate 降級 → **死路**(`tools/eval_unidir_demotion.py`)
27,790 顆電晶體:**PURE-SINK leaf = 0%**。Gemini 的乾淨案例「強驅動→純 gate-cap」根本不存在 —— 這是
NMOS 網表,幾乎每個輸出節點都掛 depletion 負載(segdefs `+`),永遠不是「無 pull 葉節點」。65% 是 supply
(早已 fast-path)、真雙向 pass gate 只 5.4%、其餘 29.7% 是鬆散強度啟發式且與 LUT pull 優先重疊。→ 不做。

### ② 拓撲 bit-vector 佇列 → **主賣點垮、但保留一角**(`tools/eval_bitvector_queue.py`)
完整相依圖(channel 雙向 + gate 有向)+ 真 SCC:**最大 SCC = 80.9%**(2c02 95.4% = 印證「94% SCC」記憶,
2a03 58.1%)。→ **拓撲順序(15-25% 的主來源)只適用 ~19%**;81% 無靜態拓撲序。去重 + L1 那半 order-independent
還活著,但稀疏 bitset 掃 word 可能輸小 FIFO。

## ★ 待辦記錄:19% levelizable 的加速機會(使用者 2026-07-20 提出保留)
**使用者觀察**:19% 不小 —— 若能只對這 19%(SCC 之外、可分層的部分)加速,效益不小;主要顧慮是**別破壞正確性**。

**技術面(可行且可正確)**:
- 這 19% 節點**不在巨大 SCC 裡**,所以相對 SCC 要嘛**純前驅**(只餵 SCC、SCC 不回餵)、要嘛**純後繼**(只受 SCC 影響)
  → **可安全拓撲排序**:levelized 前驅 → SCC 事件驅動 → levelized 後繼。這是標準的 **levelized + event-driven 混合**。
- 潛在收益:那 19% 用拓撲序**一次算完**,取代事件驅動可能的多次重評估。
- ⚠️ **誠實的量級保留**:19% 是**節點比例**、不是**執行時間比例**(runtime 由 81% SCC 主導);而且 mean BFS/settle
  depth ~1.13(settle 本來就 ~1 pass 收斂),所以「省下的重評估」可能不多。**需 runtime 探針**量:levelizable
  節點每次 settle 實際被重評估幾次 → 才知道值不值。
- **正確性關卡(硬性)**:任何實作**必須 bit-exact** —— golden checksum 不變 + AC 141/141 + 147 146/1 全過才算數
  (見 [[golden-checksum-recipe]]、[[beat-s1-rule-and-ir-reattempt-plan]])。混合的風險在「前驅/後繼相對 SCC 的
  排序」若有細微耦合錯位就會破 bit-exact,所以**先 minimal prototype 證快 + 證等價**再談。

→ **結論**:先記錄,不急著做。若哪天要撿效能,這是「還沒完全死」的一角(比 ① 有救),但要先跑 runtime 探針
量真收益、且過 bit-exact 閘。關聯 [[hotpath-ceiling-and-antipatterns]]、[[four-direction-optim-framework]]。
