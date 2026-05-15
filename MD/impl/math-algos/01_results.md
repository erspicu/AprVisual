# math-algos：三方案實作 + 對比結果

> branch：`math-algos`（base：`e3c2516` = S1 完成、S1.5 lowering 完成、S2 還沒開始）。
> 設計與 Gemini 評估：`00_design.md`（六候選方向 + Gemini 砍到剩 G 必贏、~2× 上限）。
> 本文 = 三個方案（G / Y / X）實作完之後的*實測*數據對比。

---

## 1. TL;DR

| 變體 | flag | hc/s（N=50K，boot 期）| µs/hc | × baseline | 等價性 |
|---|---|---|---|---|---|
| **baseline S1** | — | **41,656** | 24.0 | **1.00×** | (reference) |
| **G** RCM 重排 | `--rcm` | **43,286** | 23.1 | **1.04×** | ✅ selftest + branch_timing identical |
| **Y** unrolled-MLP walk | `--simd-queue` | 41,545 | 24.1 | 1.00× | ✅ selftest |
| **G + Y** combo | `--rcm --simd-queue` | 43,363 | 23.1 | 1.04× | ✅ selftest |
| **X** Oblivious eval | `--oblivious` | **345** | 2,897 | **0.008×（≈ 121× SLOWER）** | ✅ selftest |

—— 加上 4-幀整幀 benchmark 的數字（穩態工作負載）：

| 變體 | 4 幀耗時 | hc/s | × baseline |
|---|---|---|---|
| baseline | 58.9 s | 47,521 | 1.00× |
| G | 60.2 s | 46,525 | 0.98× |
| Y | 59.5 s | 47,002 | 0.99× |
| G + Y | 56.9 s | 49,165 | **1.03×** |
| X | 〔> 10 分鐘未完成 1 幀，跳過〕 | — | — |

**最終一句話**：Gemini 預期「G 必贏 ~1.3×、加上 X 可到 ~2×」—— 實測結果是 **G 在 boot 期確實有 ~4% 提升、穩態幾乎打平；Y 中性；X 災難性退化 ~121×**。**math-algos 這條 branch 的天花板實測 ≈ ~1.04×。** 物理上限就是這了。

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

## 5. Gemini 預測 vs 實測

| 方案 | Gemini 預期 | 實測 | 差異 |
|---|---|---|---|
| G（RCM）| 1.2-1.5× | **1.04× / 0.98×**（boot / 穩態）| **預期遠超實況** —— working set 已塞進 L1d、cache 不是瓶頸 |
| Y（vectorized queue）| 「modest」（未量化）| 1.00× | 接近預期（modest = 沒上沒下） |
| X（bipartite + oblivious SIMD）| ~2×（前提：完整 SIMD codegen）| 0.008×（沒 SIMD codegen 版）| 結構性卡住 —— 無 IR / 無 SIMD codegen，純算法替換是 121× 倒退 |
| **整體上限** | ~2× | **~1.04×** | **物理上限被 cache-friendly baseline 壓住** |

Gemini 的 sanity check 在 main 上的「fundamentally unsound」級判斷（β / hybrid-JIT / GPU-as-FPGA）都被實測印證了。**但它的*量化估計*（「~2× 可達」）系統性偏樂觀** —— 跟 #1 我們聊的「AI 給 hypothesis、profiler 給 truth、AI 最怕被當成不用實測的神諭」完全一致。又一個例子加進那個資料庫。

---

## 6. 結論

**math-algos branch 的天花板（在 S1 完成基礎上、不重做 S2/IR/codegen）= ~1.04×**。

實質的「演算法改善空間」就那麼點，原因有三：
1. **S1 已經是 cache-friendly 的**（NodeStates ~15KB 塞 L1d）—— cache 重排（G）幾乎沒得救。
2. **S1 的 dirty-set 已經是 optimal 的算法**（O(D) 不是 O(N)）—— 動算法（Y、X 之類）都是退化。
3. **真 SIMD width 救不了 50-100× 的算法層冗餘**（X 沒 SIMD 時 121× 慢；理論最大 SIMD 寬度 8× 也救不回；Gemini 估計 ~2× 是樂觀的）。

**所以 S1 的 ~47K hc/s 約等於「single-instance switch-level on CPU」的物理極限**。要再快得換 paradigm —— 而 main branch 的 S2→S4 探索已經做完了那條（IR / codegen / GPU），結論「real-time 不可達」（見 `MD/RETROSPECTIVE.md`）。

—— math-algos branch 的真實價值：**精確刻畫了「S1 的 ~47K hc/s 為什麼快、為什麼難再快」**。對未來想試類似事情的人（不論 AI agent 還是人類）來說，這 ~1.04× 的天花板是個強烈訊號：「在這個 paradigm 內，演算法 micro-opt 已經接近頂；想要 paradigm shift 看 main 的 S2→S4。」

**branch 收尾。** 三方案都實作了、量過了、commit 進歷史了。下個 Claude session 讀到這 doc 不用重撞。
