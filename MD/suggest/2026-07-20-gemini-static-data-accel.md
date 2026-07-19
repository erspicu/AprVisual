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
