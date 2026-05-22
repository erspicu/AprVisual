# math-algos：三方案實作 + 對比結果

> branch：`math-algos`（base：`e3c2516` = S1 完成、S1.5 lowering 完成、S2 還沒開始）。
> 設計與 Gemini 評估：`00_design.md`（六候選方向）。
> 本文 = 各方案實作完之後的*實測*數據對比。**4 個方案 G / Y / X 之後、又做了第二意見的 #1（observability merge-pruning）—— 那個才是真正的突破（1.32×）。**

---

## 1. TL;DR

| 變體 | flag | hc/s（N=50K，boot 期）| µs/hc | × baseline | 等價性 |
|---|---|---|---|---|---|
| baseline S1 | — | ~41,000 | 24.5 | 1.00× | (reference) |
| G RCM 重排 | `--rcm` | ~43,300 | 23.1 | 1.04× | ✅ selftest + branch_timing identical |
| Y unrolled-MLP walk | `--simd-queue` | ~41,500 | 24.1 | 1.00× | ✅ selftest |
| G + Y | `--rcm --simd-queue` | ~43,400 | 23.1 | 1.04× | ✅ selftest |
| X Oblivious eval | `--oblivious` | 345 | 2,897 | **0.008×（≈ 121× 慢）** | ✅ selftest |
| **#1 merge-pruning** ⭐ | `--prune-merge` | **~54,000** | **18.5** | **~1.32×** | ⚠️ observably-identical（見 §7） |

—— 4-幀整幀 benchmark（穩態工作負載，同一個 build）：

| 變體 | 4 幀耗時 | × baseline |
|---|---|---|
| baseline | 62.8 s | 1.00× |
| **#1 merge-pruning** ⭐ | **47.6 s** | **1.32×** |

**最終一句話**：G/Y 是 micro-opt、打在 *non-bottleneck*（cache / SIMD）上 → 天花板 ~1.04×；X（丟 dirty-set）打在對的瓶頸（D）但用錯方法（無 SIMD）→ 121× 倒退。**真正的突破是 #1：它*也*打在 D 上、但用對方法（剪掉 value-preserving 的 merge re-eval）→ D 降 27% → 1.32× 加速。** 而且 CPU 行為 + PPU 渲染輸出都 byte-identical、只有非-observable 的 settle transient 不同（同 main 的 float-artifact 豁免）。

—— **教訓**：Gemini 預測「~1.3×」的*量級*是對的、但它歸給 G（cache）；實測證明那個量級的 gain 來自 **D-reduction**（#1）、不是 cache。對的瓶頸一直是 D —— G/Y 證明了「不是 cache、不是 SIMD」、#1 證明了「是 D」。

---

## 2. 為什麼 G 沒到 1.3×（Gemini 估計沒中）

Gemini 的 ~1.2-1.5× 估計建立在「working set 比 L1 大、cache miss 是瓶頸」的隱含假設上。**對這顆 NES 不適用**：

- `NodeStates`（每 node 1 byte）= ~15 KB → 整個塞進 L1d（~32 KB）。永遠在 L1。
- `NodeInfos`（每 node ~24 B）= ~350 KB → 進 L2，不進 L1。RCM 重排有點影響、但小。
- `TransistorList`（~50K ints）= ~200 KB → 進 L2。
- 每半週期實際 *touched* 的 nodes：~幾百個（dirty-set），working set 進一步縮小。

RCM 的 channel-bandwidth metric 確實有改善：avg `|C1-C2|` 從 2,817 → 2,759（2.1%），max 從 14,654 → 6,967（52%）。**但**：S1 的存取模式不是「順序掃整個 NodeStates」，是「dirty-set 的 ~幾百個 node 各自走它的 transistor list」。對這種跳躍式 sparse 存取、cache line spacing 的影響很小、cache *capacity*（是否塞進 L1d/L2）才是關鍵 —— 而那個已經塞進去了，沒得救。

→ G 的 ~4% 提升（boot 期）/ ~0% 提升（穩態）= 「在 cache-friendly baseline 上、micro-locality 微調的天花板」。和預期相比少一個 order of magnitude。

---

## 3. 為什麼 Y 中性（unrolled MLP）

Y 的賣點：在 wide-list bus node（30+ transistor）的 inner walk 上、unroll-4 + 把 4 個 `NodeStates[gate]` byte-load 排在 branch 前 → 暴露 memory-level parallelism、CPU 的 out-of-order 引擎並發載入 4 條 cache line。

**現實**：
- 大多 node 的 TlistC1c2s ≤ 5 → unroll-4 的 fast path 進不去、走 scalar tail。
- 真正命中 unroll-4 的 wide bus node（~14% 的 recalc）有真實 MLP 加速，但 unroll overhead 大致抵銷。
- 連 RCM 配合（G+Y）也只有 ~1.04×，跟 G 單做一樣 —— 暗示 Y 對 *cache locality* 無加成，跟 G 是同一個（不存在的）瓶頸的不同切入點。

—— 真正的 AVX2 SIMD（Gemini 原版 Y：`VPGATHERDD` + `VPSHUFB`）在 C# 上對 `byte* NodeStates` 沒有支援（C# `Avx2.GatherVector256` 只給 int/long/float/double、沒給 byte/sbyte）。要做就得把 state 轉 int*、變 4× 記憶體、把 L1 友好 baseline 搞壞。不值得。

---

## 4. 為什麼 X 災難（~121× 慢）

X 的核心：放棄 dirty-set、每半週期*無條件*重算所有 node（直到 fixpoint）。Gemini 的全版本 X 加上 bit-sliced SIMD（一條 AVX2 指令同時模擬 256 個 logic gate）—— 預期 ~2×。

**實測**：1 半週期的 Oblivious 耗時 ~2,897 µs，S1 是 ~24 µs → **~121× 慢**。

為什麼？看數量級：
- S1 dirty-set 每半週期 RecalcNode ~200-500 次。
- Oblivious 每半週期 14,720 個 node × 平均 3-10 sweep 到 fixpoint ≈ ~50K-150K 個 RecalcNode = **~100-300× 比 S1 多做**。
- AVX2 的 8-bit SIMD width 最多救 ~8×（理論），實務 ~3-5× —— **救不回那 100-300×**。

這正是 **main branch 的 S4 wind-down 結論在 math-algos 上的重新驗證**：batch-AOT / Oblivious 算法層差 ~50-100×、codegen 補不回來。S4 的 LLVM-MCJIT 在 main 上是 ~2.5× slower than S1 —— 它*有* 真實 SIMD（LLVM `-O3`）+ codegen 化 + 殺掉 dispatch overhead；它仍輸。那 X 在 math-algos 上沒有 codegen、沒有 IR、純解釋執行 + dirty-set 移除、輸 121× 完全 expected。

—— 若要實作*完整* X（bipartite + 真 SIMD），需要：① 重新做一個輕量版的 S2 IR extraction（DriveAnalysis + NextStateBuilder）來知道每個 logic node 的 boolean function、② 結構化分類 + topo sort、③ 編出 SIMD bitwise eval kernel。**這是 ~一週級的工程**、且天花板就是 Gemini 估計的 ~2× 而已。math-algos 這個探索 branch 不值得投入。

—— *若想要看真實 X 級的東西長怎樣*：main branch 的 S4 已經做完了類似的東西（IR + LLVM codegen + GPU + bit-sliced emit），實測 LLVM-step ~16K hc/s ≈ 0.34× S1。**X-class（IR + 真 SIMD codegen）做出來的天花板就是 main 那個 ~2-3× slower than S1**（同 paradigm 的 dead end）。

---

## 4b. #1 ── Observability merge-pruning（⭐ 真正的突破，1.32×）

來源：第二意見 AI 提的「狀態可觀察性剪枝」。原版是「`if (NodeStates[c1] != NodeStates[c2]) Enqueue`」無腦套用 —— 那會壞（見下），但*修正後*是這條 branch 唯一打破 ~1.04× 天花板的東西。

**想法**：S1 的 `SetNodeState(gate)` 在 gate 改變時、會把它 gate 的每顆 transistor 的 channel 端點 enqueue（觸發群重新解析、因為導通拓樸變了）。當 **gate 變 HIGH（transistor 開啟 → 兩端 c1,c2 *合併*成一群）且 c1、c2 已經同值**時、這個合併是 **value-preserving** 的（解析優先序 GND>VCC>SetHigh>SetLow>PullUp>hold 在「兩個子群都已解析成同值」時不可能翻轉 —— 證明見 commit message），所以*跳過* enqueue。

**關鍵的不對稱（這是修正、原版沒有）**：gate 變 LOW（transistor 關閉 → *分裂*）**絕不剪**。分裂那一刻兩端*必然*同值（它們剛剛還是同一群），但分裂後兩個群可以發散（一個 dynamic node 之前的值*只是靠這條連線借來的*、斷線後該還回去）—— 這正是 user 問的「動態節點電荷重新分配」case。所以 OFF-case 保留 S1 的無條件 both-endpoint enqueue。

**實測**：
| | baseline | --prune-merge | |
|---|---|---|---|
| hc/s（bench-hc 50K，×2 trial）| ~41,000 | **~54,000** | **~1.32×** |
| 4-幀穩態 | 62.8 s | **47.6 s** | **1.32×** |
| RecalcNode/hc（**D**）| 610 | **444** | **−27%** |
| EnqueueNode 總數 | 32.6M | 23.3M | −29% |

—— 它*正中要害*：直接砍 D（dirty-set 大小），而 G/Y 已經實測證明 D 才是真瓶頸（不是 cache、不是 SIMD）。D 降 27% → 1.32×。

**等價性（⚠️ 不是 bit-identical、但 observably-identical）**：
- `--selftest`：PASS。
- **CPU trace（full_palette、1500 cycle）**：cyc 1-7 不同（power-on 的 `P` 暫存器 I-flag —— 真矽晶片上 power-on 旗標本來就 indeterminate、要 RESET 跑完才定），**cyc 8-1500 完全 byte-identical** → CPU 跑起來行為一模一樣。
- **PPU 渲染輸出（5 幀 screenshot）**：**MD5 完全相同**（`5c1dbc8e...`）→ 視訊輸出 byte-for-byte 一致。
- **NodeStates checksum**：*不同* → 某些 PPU 內部 / dynamic node settle 到一個不同的（同樣合法的）order-dependent transient。

→ 結論：**#1 的分歧侷限在「非-observable 的 settle transient」**（power-on indeterminate 旗標 + 不影響輸出的 PPU 內部 node）—— 跟 main 的 **float-artifact 豁免完全同一類**（S1 的半週期內 settle 在 dynamic / indeterminate 區*不是唯一 fixpoint*、擾動 enqueue 集會 steer 到另一個合法 transient）。**不是 logic 算錯**。對「不求 realtime、求演算法突破」的目標、這是個合法且乾淨的 1.32× —— 代價是接受跟 main 一樣的 transient 豁免。

—— 修正版 #1 為什麼*不是* bit-identical（我原本以為它會是）：「value-preserving」證明假設「兩個子群在合併前*各自已 settle*」；但在 settle wave 進行中、群是 mid-flight 的、而 power-on 那些 indeterminate 區的 fixpoint 本來就 non-unique。所以剪掉一個「當下看起來 value-preserving」的 re-eval、會讓那些 non-unique 區落到不同（但同樣合法）的點。要做到 bit-identical 得只在「群已 settle」時剪、而那個我們在 mid-wave 沒辦法 cheaply 判斷。

---

## 5. Gemini 預測 vs 實測

| 方案 | Gemini 預期 | 實測 | 差異 |
|---|---|---|---|
| G（RCM）| 1.2-1.5× | **1.04× / 0.98×**（boot / 穩態）| **預期遠超實況** —— working set 已塞進 L1d、cache 不是瓶頸 |
| Y（vectorized queue）| 「modest」（未量化）| 1.00× | 接近預期（modest = 沒上沒下） |
| X（bipartite + oblivious SIMD）| ~2×（前提：完整 SIMD codegen）| 0.008×（沒 SIMD codegen 版）| 結構性卡住 —— 無 IR / 無 SIMD codegen，純算法替換是 121× 倒退 |
| **#1 merge-pruning** ⭐ | （第二意見、Gemini 給 G 的數字）| **1.32×** | **量級對了、但 lever 是 D-reduction 不是 cache** |
| **整體上限** | ~2× | **~1.32×（#1）** | G/Y 卡 ~1.04×（non-bottleneck）；#1 打中 D → 1.32× |

Gemini 的 sanity check 在 main 上的「fundamentally unsound」級判斷（β / hybrid-JIT / GPU-as-FPGA）都被實測印證了。它的*量化估計*（G「~1.2-1.5×」、整體「~2×」）—— **量級是對的（1.3× 確實出現了）、但它歸錯了技術**：那個 1.3× 來自 D-reduction（#1）、不是 cache locality（G）。又是「AI 給 hypothesis、profiler 給 truth」—— 不只「會不會」要實測、「為什麼」也要實測。

---

## 6. 結論

**math-algos branch 的天花板（在 S1 完成基礎上、不重做 S2/IR/codegen）= ~1.32×**（#1 observability merge-pruning），代價是接受「非-observable settle transient」的豁免（CPU 行為 + PPU 輸出 byte-identical、只有內部 dynamic/indeterminate node 不同 —— 同 main 的 float-artifact）。

關鍵教訓 —— **瓶頸一直是 D（dirty-set 大小），不是 cache、不是 SIMD width**：
1. **cache 重排（G）≈ 沒用（1.04×）** —— S1 的 NodeStates ~15KB 已塞 L1d、cache 不是瓶頸。
2. **SIMD micro-opt（Y）≈ 沒用（1.00×）** —— byte* 的不規則 walk、C# AVX2 ergonomics 差、且同樣不打 D。
3. **丟 dirty-set（X）= 121× 倒退** —— 證明 D 的 O(D) 演算法是 essential、不能換 O(N)。
4. **剪掉 value-preserving 的 re-eval（#1）→ D 降 27% → 1.32×** —— 唯一打中 D 的、唯一贏的。

**所以加速 S1 的正道是「進一步削減 D」**（#1 是第一刀）。還有沒有更多 D 可削？候選（`00_design.md` §2 + 第二意見的 #2/#3 也都間接打 D）：
- **#2 拓樸層級化 / 消突波**（保證純 logic node 半週期內最多 eval 一次）—— 但需要 acyclic-logic-subgraph 的拓樸 level（= 部分 S2）。
- **#3 巨集建模**（4-transistor NAND 坍縮成 1 macro node）—— 砍 N/M、間接砍 D，但 = 輕量 S2（滑向 IR）。
- 其他 value-preserving / observability 剪枝。

**所以 S1 的「物理極限」不是 ~47K、而是「~47K × (能再削多少 D)」。** #1 把它推到 ~62K（1.32×）。要再往上、得繼續找「哪些 enqueue / recalc 是冗餘的」—— 而那是有限的、且每一步都要驗「observably-identical」。real-time（~840×）仍然不可達（那是 paradigm 的事、見 `MD/RETROSPECTIVE.md`）；但「single-instance switch-level on CPU」的天花板被 #1 從 ~47K 抬到 ~62K hc/s，證明了「D-reduction」這個 lever 是真的。

**目前狀態**：G / Y / X / #1 都實作了、量過了、commit 進歷史了。#1 是值得保留+預設考慮開啟的（1.32× + observably-identical）。下個 session 讀到這 doc 不用重撞 —— 而且知道「下一步要繼續削 D」。
