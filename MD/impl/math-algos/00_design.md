# math-algos 探索分支 —— 用數學演算法（乘法 / 矩陣縮減）重新表達 S1

> branch：`math-algos`，從 **`e3c2516`**（= S1 完成 + S1.5 lowering 完成、S2 還沒開始）branch 出去。
> 所以這個 branch *只有* S1 —— 完整的 switch-level 引擎、過 blargg、`--benchmark` 跑得起來；**沒有任何** IR / S2 / S3 / S4 / GPU / codegen / cpu-opt 的東西。乾淨的探索場。
>
> user 的方向（2026-05-13）：**「藉由某些數學演算法、乘法與矩陣縮減運算 應用在這裡」** —— 把 S1 的 switch-level 機器（dirty-set BFS + group walk + flags→state LUT）的某些部分、用線性代數 / 布林代數 / 矩陣運算重新表達 + 可能因此加速。

---

## 0. 為什麼這個 branch

整個 main 上的 S2→S4 pipeline（IR extraction + 多 backend codegen + GPU + cpu-opt-β）探索完了，結論「real-time on CPU 不可達」（見 `MD/RETROSPECTIVE.md`）。但*那個*結論是針對「以 boolean Expr tree + 多 backend codegen 為 paradigm」的 pipeline 講的 —— 它沒有涵蓋「**換一個完全不同的數學表達**」這個方向。比方：

- 「group resolution」現在是 BFS 走連通群 + flags-OR + 256-LUT。但「群」這件事本身可以是個*矩陣*（哪些 node 連在一起 = 一個等價關係 = 一個 0/1 矩陣的 kernel）。BFS 是其中*一種*算法 —— 是不是有別的（更 cache-friendly、更 SIMD-friendly、或者乾脆 closed-form）？
- 「dirty-set 傳播」現在是 BFS over event queue。但「N 個半週期後、哪些 node 會變」是個*傳播算子*（transistor connectivity matrix 的 `k`-step power）。如果某些區域的 transition 矩陣是已知的、稀疏的、或可分塊的、能不能 precompute `M^k` 直接跳過 k 個半週期？
- boolean logic（AND / OR / NOT）在 GF(2) 上 = XOR + AND。所以*任何* boolean function 都可以寫成 ANF（Algebraic Normal Form）= GF(2) 上的多項式。一旦進到 GF(2) 多項式環、就有 Gauss elimination、Gröbner basis、各種「縮減」工具可用。

—— 不是要*替換*整個 S1（S1 仍是 golden reference）、是要找 S1 *某個*子問題裡、是不是有個數學表達能讓那塊變得快很多 / 規律很多 / 可預測很多。**這 branch 的特性 = 純探索、沒有等價 gate、不用對任何下游負責**（沒有下游）。可以放心試各種離經叛道的東西。

---

## 1. S1 現在的核心算法（baseline）

要改之前先看清楚要改什麼。S1 每半週期的工作（`WireCore.Recalc.cs` + `.Group.cs`）：

```
StepCycle:
  RunHandlerChain                    // clk toggle, callbacks
  ProcessQueue:
    while RecalcListNext non-empty:
      swap current/next queue
      foreach nn in current queue:
        if RecalcHash[nn] != 0:       ← dirty flag #1
          RecalcHash[nn] = 0
          RecalcNode(nn):
            ComputeNodeGroup(nn):
              clear _inGroup of last group
              AddNodeToGroup(nn) (recursive over conducting transistors):
                if _inGroup[nn] != 0: return   ← dirty flag #2 / early return
                _inGroup[nn] = 1
                _groupBuf[_groupCount++] = nn
                _groupFlags |= NodeInfos[nn].Flags
                walk TlistC1c2s / TlistC1gnd / TlistC1pwr; for each (gate, other):
                  if NodeStates[gate] != 0: AddNodeToGroup(other)
              newState = FlagsToState[_groupFlags]    ← 256-entry LUT
            for each m in _groupBuf:
              SetNodeState(m, newState):
                if NodeStates[m] == newState: return   ← early return #1（最關鍵）
                NodeStates[m] = newState
                enqueue m's gate-fanout
  InvokeCallbacks
  Time++
```

關鍵速度來源（我們最近跟 Gemini 聊過、見 `MD/閒聊_兩個AI_2026-05-13.md` 跟 `e3c2516` 之後的 S1 對話）：

- **Dirty flag**（`RecalcHash` + `_inGroup`）= 兩個 O(1) dedup —— 「沒在 queue 就加、沒在 group 就訪、否則跳過」。
- **Early return**（三個）—— `SetNodeState` 的「值沒變、立刻 return」是*最關鍵*的那個；它讓 dirty-set 只在「值真的變了」的地方擴張、不會雪崩。
- **256-LUT** —— group 解析 branch-free。
- **Unmanaged hot path** —— `byte*` / `int*` / `NodeInfo*`、zero-alloc、zero bounds-check。

這些加起來、S1 ≈ 47K hc/s（~14s/幀）。**這 branch 的問題：上面這 5 個機制裡、有哪個能被一個「數學表達」吃掉（替換成更快的東西、或者乾脆消掉）？**

---

## 2. 候選方向（待 user 挑 / 細化）

下面列幾個 *候選*。每個都不是承諾、是「可以在這個 branch 上探一探的角落」。**user 看完選一個（或寫一個新的、或組合）**，我們再做具體的設計+原型。

### 2.1 群結構 = 矩陣 ── group resolution 用線性代數做

「`nn` 跟 `mm` 在同一個導通群」是個*等價關係*。當你 freeze 當下的 `NodeStates`（決定哪些 transistor 導通），這個等價關係 = 一個 N×N 的稀疏 0/1 矩陣 `A`（`A[i,j] = 1` iff `i` 跟 `j` 因某個導通 transistor 直接相連）的*連通分量*。Connected components 可以用：

- **transitive closure**：`A* = (I ∨ A)^N`（boolean 矩陣冪）。算一次得整張等價關係。
- **Union-Find**：O(N α(N))、跟 BFS 同階。
- **譜方法**：`A` 的特徵向量 / Laplacian 結構（過殺、但理論上漂亮）。
- **稀疏矩陣分塊**：netlist 的 transistor graph 是稀疏的（每 node ~3-5 個 transistor）、可分塊（PPU / CPU / APU / cart 大致獨立）。把連通群限制在分塊內計算。

**潛在贏面**：BFS 是 cache-unfriendly（指標追隨機 node id）；分塊矩陣 ops 是 SIMD-friendly + 規律存取。**但** 每半週期*哪些 transistor 導通*在變（這就是為什麼 BFS 動態做），所以 `A` 每半週期都要重建 —— 重建的成本可能吃掉節省。需要量。

### 2.2 propagation = 矩陣冪 ── 跳過 k 個半週期

如果你能寫出「**單一半週期的 state transition** = `state_{t+1} = T(state_t)`」這個*算子*（在某個 input pattern 已知的區域、比方 PPU 的渲染主迴圈、`T` 是個固定的 boolean function），那 `state_{t+k} = T^k(state_0)`。`T^k` 可以*預先算好*（用矩陣冪 / functional iteration）。如果 `T^k` 比 `k * T_apply` 便宜（典型情況：`T` 是個稀疏線性算子在 GF(2) 上，`T^k` 仍稀疏），你就跳過了 k 個半週期。

這是 **Hashlife 的核心想法**（Conway's Game of Life 的 superluminal evolution）—— 它在 CA 上靠 memoization 達到指數倍加速。我們的 netlist 不是 CA、但有類似結構（local rule、translation-invariant 的部分如 PPU/APU 寄存器）。

**潛在贏面**：對 PPU 那種「每個 scanline 重複類似動作」的部分、`T^k` 可能 collapse 一整個 scanline 的 ~341 cycle 成一次表查。**但**：① 真的*有*那個 translation-invariant 結構嗎（需要 audit）；② state space 太大、cache 不命中就退化。

### 2.3 ANF / GF(2) 多項式 + Gauss / Gröbner reduction

每個 node 的 next-state function 都可以寫成 GF(2) 多項式（ANF）。整顆晶片 = 14k 個多項式組成的 ideal。對這個 ideal 做：

- **Gauss elimination**（線性部分）—— 找出 linearly-dependent 的 node（同一個線性組合的兩種寫法）→ 縮減。
- **Gröbner basis**（非線性部分）—— canonical form、消除冗餘乘積。
- **代數因式分解**（Boolean ring）—— 把 `xy + xz` 因成 `x(y + z)`、共享運算。

這跟 Yosys 的 `abc` 做的事重疊（我們 main 上跑過、`opt + abc` 縮 ~38%）—— 但 ABC 是 AIG 級的 rewriting、不是純 algebraic。**純代數角度可能能再吃一些**（特別是如果你願意改變 representation —— 從 AND/OR/NOT 換成 XOR/AND）。**但**：① 結果還是 boolean expression、要 evaluate 還是 token-by-token；② 跟 main 那條 IR pipeline 在做的事很重疊（已知 codegen 那邊有 ~3-5× 上限）。**所以這條*單獨*不行、但配合 2.1 / 2.2 / 2.4 可能加成。**

### 2.4 sub-circuit memoization（Hashlife 的工程化版）

不是純數學、是「**用空間換時間**」的工程招數，但配合上面幾個會強。對某些子電路：「給定它的輸入 vector（K 個 bit）和 starting state（M 個 bit）、N 個 cycle 之後它的輸出 vector + new state 是什麼」是個 `2^(K+M) → 2^(output+M)` 的查表。如果 K+M 小（~10-20 bit）、cache 進得去、查一次表就跳過 N 個 cycle。

候選 sub-circuit：APU 的 envelope counter / DMC carry chain / PPU 的 hpos/vpos counter / palette readout latch。這些都是 small state、reset-after-N-cycles 的東西，*天生*適合 memoization。

**贏面**：可能很大（如果命中率高）。**輸面**：cache miss + state-pollution 時退化、實作複雜。

### 2.5 transistor 連通圖 = adjacency matrix、做*靜態*分析

不是 runtime 用、是 build-time 用 —— 把 lowering 後的 transistor graph 表示成 adjacency matrix、做：

- **連通分量分析**：找出永遠不會跟主邏輯互動的子網（debug nodes、test 結構）→ 砍掉。
- **譜聚類**：發現 cluster（CPU / PPU / APU / board）→ 各自獨立模擬、減少跨 cluster 的傳播。
- **strongly-connected components on the conduction graph**：把 SCC 整體當一個「複合 node」、群解析降低粒度。
- **PageRank-like centrality**：找出「最常被走訪」的 node → 把它們的 transistor list 排序成 cache-friendly 的版本。

**贏面**：純 build-time，runtime 一行 code 都不改、純粹改 data layout → 可能 1.2-2× from cache。**輸面**：weak（不是質的改變、是優化）。

### 2.6 user 沒列、但我（claude）想到的另一個 ── transistor 模型本身換成 *連續*的

S1 是純 0/1 switch-level。但 SPICE 那邊是連續電壓 + 電阻 + 電容。如果你*放棄 boolean*、用某種 reduced 連續模型（兩值 + 一個「程度」、或者 fixed-point）、然後用矩陣解線性方程組（Kirchhoff）—— 就變成 SPICE 的窮人版。**這超出「乘法+矩陣縮減」的範疇 + 跟 S1 是完全不同的東西**、不一定是你要的方向，但列在這供 reference（如果你*真的*要動 PPU sub-cycle dynamic logic，可能要走這條）。

---

## 3. 不在這個 branch 範圍內

- **跟 main / cpu-opt 的等價驗證**：這 branch 從 `e3c2516` 出去、沒有 IR、沒有 S2/S3/S4。等價只能對 S1 本身或對 blargg。沒有 `--trace-cmp --engine ir` 這種東西。
- **real-time**：這 branch *不承諾* real-time（main 已經結案那是不可達）。目標是「探一個*別的*演算法 paradigm、看它有沒有意外的角落」。
- **carry over 任何 IR 工作**：S2 的 Expr tree、S3 的 SCC analysis、S4 的 codegen 都*不在這 branch 上*。要的話 cherry-pick / re-derive。

---

## 4. 下一步（user 填）

**user**：上面 2.1–2.6 哪個（或哪幾個的組合、或一個我沒列的）是你要的方向？挑了之後、再寫 step-by-step 設計 + 起手原型。

候選範本（你選或自由發揮）：
- ☐ 2.1 group resolution 用線性代數
- ☐ 2.2 propagation = 矩陣冪、跳 k 個半週期
- ☐ 2.3 ANF / GF(2) 縮減
- ☐ 2.4 sub-circuit memoization
- ☐ 2.5 靜態圖分析 + data layout
- ☐ 2.6 連續模型（SPICE-lite）
- ☐ 其他：______________________________

—— 或者你想丟個更具體的問題（「對 *某個具體的 sub-circuit*（比方 APU DMC counter）試 2.4 看看」、「用 GF(2) Gauss 做一遍 lowering、看能不能再壓 5%」之類），我就照那個方向走。

## 5. Gemini 評估（2026-05-16，full log: `main` 上 `tools/knowledgebase/message/20260516_015558.txt`）

把 §1（S1 baseline）+ §2（六個候選）+ workload 參數（N=14.7K、M=27K、D≈500、G≈1-5）丟給 Gemini 評估。它的判斷**很硬、值得記下來**：

### 5.1 判死刑的（含我自己已經踢的）

- **A、E（Union-Find / Link-Cut Trees）**：演算法 $O(D \log N)$ 對，**但常數估算太樂觀**。LCT 的 Splay tree pointer chasing 一次 op 是「數百個 CPU cycle」；S1 的 `SetNodeState` 加上陣列走訪「大約只要十幾個 CPU cycle（~3-5ns）」。「典型的*為了降大 O 而賠掉常數*，絕對慘輸。」
- **B（全域 SpMV）**：兩個致命傷 ── ① boolean SpMV `y_i = ⋁_j (A_ij ∧ x_j)` 需要 Gather 指令（VPGATHERDD），「Gather 在 CPU 上延遲極高（十幾個 cycle），根本無法發揮理論的 32× 吞吐量」；② **矩陣每半週期都要重建**（哪些 transistor 此刻導通）—— 「重建 $A$ 是 $O(M)$，光這步就比 S1 整個半週期耗時還長」（~10-15µs vs S1 ~21µs/半週期）。直接砍半天花板。
- **C、D（Four Russians / Strassen）**：稀疏網路用 sub-cubic dense matmul 沒意義。我自己已踢、Gemini 確認。
- **F（Hashlife）**：「Switch-level 有連續的 clock phase 與雙向導通、不是 CA 那種離散且單向的 grid；切分 sub-circuit 的邊界條件會爆炸。」範圍外。
- **H（Bit-packed dirty-frontier，我認為最有希望的那個）**：**Gemini 判它是陷阱**。理由：要*同時*維護 event queue（找誰是 dirty）跟 bit-packed array（為了 SIMD）；D=500 散在 N=15K bit ≈ 234 個 ulong 上 = **極度稀疏的位元向量** → 這是 SpMSpV（sparse × sparse），「在 CPU 上沒有完美的硬體 Scatter/Gather 支援、解包 (Unpacking) 轉成 index 的代價會吃掉所有 SIMD 帶來的好處」。

### 5.2 唯一必贏：G（靜態圖分析 + cache-friendly 重排）

「**唯一必贏的純優化**」。用 **Cuthill-McKee (RCM)** 或 DFS ordering 重新給 node id 排序、讓 S1 原本的 BFS 在存取 `NodeStates` 與 `Tlist` 時從 random access 變成具備 spatial locality。**L1/L2 cache hit rate 提升能穩穩榨出 ~1.2–1.5×**。低風險、可量化、不改演算法、純改 data layout。

### 5.3 Gemini 自己提的、比我那 6 個都好的新方向

#### 方向 X：**Bipartite 分離（Logic vs Bus）+ Oblivious Logic SIMD**

打破「全網路一致 graph 視角」，分兩塊處理：

- **Build-time**：netlist 切成 (a) 純 logic subgraph（NAND / NOR / Inverter —— 大概 86% 的 node）和 (b) pass-transistor cluster（雙向、大群 —— 14%）。
- **Runtime**：
  - **對 86% 的 logic：放棄 BFS**。拓樸排序 → **Oblivious Simulation**：不管 dirty 不 dirty、Input 準備好我就*暴力*算。`State_C = State_A & ~State_B` —— **AVX2 一條指令同時模擬 256 個 logic gate**。完全消滅 queue / hash / branch mispredict。
  - **對 14% 的 bus / 雙向開關**：保留 S1 「霸道無比」的 BFS dirty-set 演算法。

這個方向比我提的「H bit-packed frontier」乾淨太多 —— 它不需要 SpMSpV，因為它放棄了「只算 dirty 的」這個 invariant、用「無論如何把 86% 邏輯閘批量批掉」換 SIMD 的全寬度。**這正是 S1 的 dirty-flag 對 logic 那一塊的*反命題* —— 而對小群、淺收斂的 logic 來說，反命題可能反而勝出。**

#### 方向 Y（次選）：Vectorized Event Queue（保留 S1、加 SIMD 進去）

不搞矩陣、保留 queue，但用 SIMD 加速 queue 處理：① 從 queue 一次 pop 8 個 dirty nodes；② AVX2 `VPGATHERDD` 並發載入 flags + neighbors；③ 用 `VPSHUFB` 平行查表（替代 256-LUT）一次算 8 個 new state；④ 有變化再 enqueue。保留 O(D) + 加 SIMD 寬度。「組語功力要好，但保留了 dirty-set 的演算法優勢」。

### 5.4 天花板估計

| 配置 | 估計 hc/s | 倍數 |
|---|---|---|
| 目前 S1（純量 C#） | ~47K | 1.0× |
| + G（RCM 重排 layout） | ~55-60K | **1.2-1.3×** |
| + G + 方向 X（Bipartite + Oblivious Logic SIMD） | ~80-100K | **~2×** |

**硬性物理上限 ≈ 2×。** Gemini：「**記憶體延遲的物理極限就擺在那裡** —— D=500 決定了你必須做指標追蹤、一次 L3 cache miss 就是 40-50ns、相當於數百個 ALU 指令。任何沒有從根本上消滅『狀態讀寫』的演算法（例如 JIT 融合成單一函數 —— 這是 main branch 排除過的作法），都跨不過這道物理之牆。」

### 5.5 給 User 的明確建議（Gemini 的話）

> **「停止對『全域稀疏矩陣 SpMV』的浪漫幻想。對於 event-driven、極度稀疏且拓樸不規則的 switch-level 電路，矩陣是錯誤的抽象。**
>
> **把精力投資在 *Data Layout 重排 (G)* 與 *邏輯閘的批次化 / Oblivious SIMD* 上 —— 這才是能帶來量化改善的務實做法。」**

---

## 6. 修正後的下一步（user 決定要不要走、走哪個）

基於 Gemini 評估、原本 §4 那個 6 候選清單可以簡化成：

- ☐ **G** —— Cuthill-McKee / RCM 重排 node id、把 `NodeStates` + `Tlist` 變 cache-friendly。低風險、~1.2-1.3×、是 prerequisite for 任何更深的工作。
- ☐ **方向 X** —— Bipartite 分離 + Oblivious Logic SIMD。中風險、~2× 上限、需要：① build-time 把 netlist 切成 logic / bus 兩塊（這部分跟 S2 `DriveAnalysis` 概念相關、但更輕量、純 topology）、② logic 拓樸排序、③ SIMD bitwise logic eval（C# 的 `Vector256<byte>` / `Vector256<ulong>`）。
- ☐ **方向 Y** —— Vectorized event queue（VPGATHERDD + VPSHUFB）。組語/Intrinsics 重，補強 S1 而不替換它。
- ☐ **全部都不做** —— 結論其實是「S1 已經很接近 CPU 的物理極限了；想再快得換 paradigm（main 那條 IR pipeline），那條已經結案不可達 real-time」。所以這 branch 的真實價值可能不是「速度突破」、是「**精確刻畫 S1 為什麼快、它的極限在哪、跨不過去的物理是什麼**」 —— 那部分這次的 Gemini 評估已經給了。

**user 挑一個（或挑「全都不做、把這 branch 當分析記錄收尾」）—— 我照那個走。**

---

## 進度日誌

| 日期 | commit | 內容 |
|---|---|---|
| 2026-05-13 | `ed807da` | branch 建立 + 設計骨架（六個候選方向） |
| 2026-05-16 | `<pending>` | Gemini 評估完整版加進來 —— B、C、D、E、F、H 全砍、A 慘輸；**唯一必贏 = G（RCM 重排）~1.3×**；Gemini 自己提的新方向 = **Bipartite 分離 + Oblivious Logic SIMD** ~2× 上限。給出物理上限 ~2×。 |
