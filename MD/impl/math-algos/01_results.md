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
| 策略二 fast-path（單做）| `--fast-path` | ~41,300 | 24.2 | 1.00× | ✅✅ **bit-identical**（checksum 完全相同）|
| **#1 + 策略二** ⭐ | `--prune-merge --fast-path` | **~57,000** | **17.5** | **~1.37×** | ⚠️ = #1 的 checksum（fast-path 對 #1 也 bit-identical）|

> 第三批（策略二 fast-path + 策略三 glitch 診斷，2026-05-23）的同-session 對照：baseline 41.5K、fast-path 單做 41.3K、#1 單做 55.0K、#1+fast-path 57.0K（每項 ×2 trial）。詳見 §4c / §4d。

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

## 4c. 策略二 ── 純邏輯閘 O(1) Fast-Path（第二意見的「最高 ROI」候選）

來源：第二意見 AI 的三個後續策略，它判定這個「投資報酬率最高」、建議**第一個做**。`--fast-path`。

**想法**：NMOS 晶片大量 node 是 depletion-load 邏輯閘輸出 —— 帶 pull-up、只透過 transistor 直通 GND 被拉低、**從不**是 pass-transistor bus 的端點。對這種 node `nn`，導通群*恆等於* `{nn}`（因為它沒有任何到普通 node 的 channel：`TlistC1c2s` 空 ⇒ `AddNodeToGroup` 既不會從 nn 遞迴出去、也不會把 nn 拉進別人的群），所以整套 DFS（清 `_inGroup`、push `_groupBuf`、遞迴、追 `_maxConnections`）都是白做。值就是單-node 群的 `GetNodeValue()`。

**比提案更嚴謹的實作**：提案寫「`newState = 1`（PullUp 預設），有 gnd 導通就 0」。我改用 **`FlagsToState[ ns.Flags | (Gnd if 任一 GND-channel 導通) | (Pwr if 任一 VCC-channel 導通) ]`** —— 每次*重新讀* `ns.Flags`，所以即使該 node 在 runtime 被外部驅動（`SetHigh/SetLow` flag）也照走 LUT 優先序、保持與 `ComputeNodeGroup({nn})` **逐位元等價**；提案硬編碼的 `1` 在外部驅動時會錯。分類條件：`PullUp`（保證 OR 後的 flags 非空 ⇒ 走 LUT 而非「純浮接 hold」路徑、這正是讓 LUT 等價的前提）+ `TlistC1c2s==0`（保證群恆為 `{nn}`）+ 排除 `HasCallback/ForceCompute/Pwr/Gnd`。

**實測**：
| | baseline | `--fast-path` | #1 | #1 + `--fast-path` |
|---|---|---|---|---|
| hc/s（bench-hc 50K，×2）| ~41,500 | ~41,300 | ~55,000 | **~57,000** |
| × baseline | 1.00× | **1.00×** | 1.33× | **1.37×** |
| NodeStates checksum | `0x933A…18BE` | **`0x933A…18BE`（同！）** | `0xCF26…C6DC` | **`0xCF26…C6DC`（= #1）** |
| 分類到的 node | — | 3,408（佔 live 的 **23.1%**）| — | 3,408 |

**兩個結論**：
1. **正確性無懈可擊**：`--fast-path` 的 checksum 跟 baseline *完全相同* —— 是這條 branch 上**唯一 bit-identical 的優化**（G/Y 也 bit-identical 但 ~1.00×；#1 只 observably-identical）。等價性是*建構式*證明的，不靠特定 ROM。
2. **單做 ~1.00×（又一個 non-bottleneck）**：fast-path 砍的是「每個 dirty node 的 per-recalc 常數」，但對單-node 群來說那個常數**本來就極小**（同一段 gnd/pwr walk + 幾條 L1-resident ALU 指令；提案以為很貴的 DFS 機制對 1-node 群幾乎免費）。再加上只有 23%（不是提案猜的 80%）的 node 合格 → 省下的時間沒入雜訊。**這再次印證：瓶頸是 D、不是 per-recalc 常數。**

**但它能疊加**：在 **#1 之上**，fast-path 穩定再加 ~3.5%（55.0K→57.0K，×2 trial 不重疊、非雜訊），且 checksum 不變（對 #1 也 bit-identical）。原因：#1 把 value-preserving 的 bus-merge re-eval 剪掉後、**殘餘的 recalc stream 富集了 pure-logic node**（#1 不剪這類），所以 fast-path 在 post-#1 的（較小的）D 裡命中率更高。兩者輕度協同 → 合併天花板 **~1.37×**。

—— Gemini 的「最高 ROI」判斷**再次落空**（同它把 1.3× 歸給 G 的錯）：fast-path 單做是 ROI 最低的之一。但它*乾淨*（bit-identical），所以當作 #1 的免費附加是值得開的。

---

## 4d. 策略三 ── Glitch 診斷（量化，不做那個會壞 latch 的優化）

來源：第二意見 AI 的策略三。它*自己*承認 delay-line 抑制「容易打破因果、害 latch 錯失 edge-trigger」，並退而求其次建議**先量**「一個半週期內、同一個 node 平均被 recalc 幾次」。我只做這個診斷（`--count-events` 時順帶輸出），**不做**那個危險的延遲 buffer（PPU 動態邏輯最容易中招）。

實作：`DistinctRecalcCount` 數每個 node 在每個半週期的*第一次* RecalcNode，於是 `RecalcNode 總數 / distinct(node,hc)` = 每 node 每半週期的平均 recalc 次數。

**實測（full_palette，50K hc）**：
| 變體 | RecalcNode/hc (D) | glitch factor | 解讀 |
|---|---|---|---|
| baseline | 610.4 | **1.138** | >1.1 → 有突波：~12% 的 D（30.5M 中的 3.7M）是半週期內的重複 re-recalc |
| #1 `--prune-merge` | 444.1 | **1.103** | #1 把 glitch 超出量（factor−1.0）從 0.138 壓到 0.103（≈ −25%）|

**結論**：
- **突波稅是真的**（baseline 每 node 每半週期被算 1.14 次）—— 這是一塊 ~12% 的 D、理論上可削。
- **#1 已順手吃掉約 1/4 的突波超出量**（1.138→1.103）：一個 value-preserving 的 merge re-eval 本來就是一種突波；#1 剪它時連帶壓了突波。
- **但安全地攻擊剩下的突波 = 拓樸層級化排程**（給 acyclic logic 子圖排 topo level、保證每個純 logic node 半週期內最多 eval 一次）—— 那 = 部分 S2 IR extraction，**超出這條「不重做 S2」branch 的範圍**。提案的 delay-line 版本在這顆有大量 edge-triggered latch 的晶片上太危險、放棄。
- 所以策略三**作為診斷成功了**（量出 ~12% 的突波稅、且證明 #1 已部分吃掉），但**作為優化在本 branch 不實作**（安全版需要 S2、危險版會壞 latch）。

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

**math-algos branch 的天花板（在 S1 完成基礎上、不重做 S2/IR/codegen）= ~1.37×**（#1 observability merge-pruning + 策略二 fast-path 疊加），代價是接受 #1 的「非-observable settle transient」豁免（CPU 行為 + PPU 輸出 byte-identical、只有內部 dynamic/indeterminate node 不同 —— 同 main 的 float-artifact；fast-path 本身則是 bit-identical、不增加任何豁免）。

關鍵教訓 —— **瓶頸一直是 D（dirty-set 大小），不是 cache、不是 SIMD width、也不是 per-recalc 常數**：
1. **cache 重排（G）≈ 沒用（1.04×）** —— S1 的 NodeStates ~15KB 已塞 L1d、cache 不是瓶頸。
2. **SIMD micro-opt（Y）≈ 沒用（1.00×）** —— byte* 的不規則 walk、C# AVX2 ergonomics 差、且同樣不打 D。
3. **丟 dirty-set（X）= 121× 倒退** —— 證明 D 的 O(D) 演算法是 essential、不能換 O(N)。
4. **剪掉 value-preserving 的 re-eval（#1）→ D 降 27% → 1.32×** —— 唯一打中 D 的、唯一贏的。
5. **O(1) fast-path（策略二）單做 ≈ 沒用（1.00×）** —— per-recalc 的 group-walk 常數對單-node 群本來就極小、且只 23% node 合格；但它 *bit-identical* 且能在 #1 之上再疊 ~3.5%（post-#1 stream 富集 pure-logic node）→ 合併 1.37×。
6. **突波稅（策略三 診斷）≈ 12% 的 D**（glitch factor 1.138；#1 已壓到 1.103）—— 真的存在、但安全削它要拓樸層級化（= 部分 S2）、危險的 delay-line 會壞 latch。

**所以加速 S1 的正道是「進一步削減 D」**（#1 是第一刀、fast-path 是 #1 之上的免費薄片）。還有沒有更多 D 可削？候選（`00_design.md` §2 + 第二意見的 #2/#3 也都間接打 D）：
- **#2 拓樸層級化 / 消突波**（保證純 logic node 半週期內最多 eval 一次）—— **策略三 已實測證明這塊有 ~12% 的 D**（glitch factor 1.138）—— 但需要 acyclic-logic-subgraph 的拓樸 level（= 部分 S2）。
- **#3 巨集建模**（4-transistor NAND 坍縮成 1 macro node）—— 砍 N/M、間接砍 D，但 = 輕量 S2（滑向 IR）。
- 其他 value-preserving / observability 剪枝。
- **策略一 隔離斷開剪枝**（#1 的 OFF-case 另一半）：理論上可剪「斷開後變孤島且*無任何 forcing flag*的純動態節點」；但因 pull-up 在 S1 是 flag 不是 channel、絕大多數 NMOS 邏輯 node 都有 pull-up（斷開後該浮回 1、不可剪），安全可剪量很可能很小 —— 留作未實作的修正版實驗（要先量安全可剪量）。

**所以 S1 的「物理極限」不是 ~47K、而是「~47K × (能再削多少 D)」。** #1 把它推到 ~62K（1.32×）。要再往上、得繼續找「哪些 enqueue / recalc 是冗餘的」—— 而那是有限的、且每一步都要驗「observably-identical」。real-time（~840×）仍然不可達（那是 paradigm 的事、見 `MD/RETROSPECTIVE.md`）；但「single-instance switch-level on CPU」的天花板被 #1 從 ~47K 抬到 ~62K hc/s，證明了「D-reduction」這個 lever 是真的。

**目前狀態**：G / Y / X / #1 / 策略二 fast-path / 策略三 glitch 診斷 都實作了、量過了、commit 進歷史了。**值得開的兩個**：#1（1.32×、observably-identical）+ 策略二 fast-path（疊到 1.37×、bit-identical）。下個 session 讀到這 doc 不用重撞 —— 而且知道「下一步要繼續削 D，且策略三已經量出拓樸層級化能打的那 ~12%」。

---

## 7. ⚠️ 2026-05-24 修正：#1 的 "observably-identical" 聲明是 over-claim

第 4b/4c/6 節說 `--prune-merge` 「observably-identical」/ 「CPU 行為 + PPU 輸出 byte-identical、只有內部 dynamic/indeterminate node 不同」── 這個聲明**只在 bench-hc 用 `01-basics.nes`（blargg CPU 測試）跑時成立**，因為那個 ROM 只測 CPU、不依賴 PPU rendering output。

**用 `full_palette.nes`（測試 PPU palette 顯示）做 visual 驗證,`--prune-merge` 在 frame 48 渲染出全黑、不是預期的色票網格**(對照 baseline 同 ROM/同 frame = 完整色票)。 

### 7a. Root cause

dump-states 在 500K hc 的 diff 只有 118 行,集中在三類:

1. `ppu.pal_ram_{11,13}_*` 6T SRAM cell ── `_a` 跟 `_b` 顛倒
2. `ppu.oam_ram_100_*` 6T SRAM cell ── 同上
3. ~30 個 unnamed PPU 內部 node:**`vpos2` `hpos2` `vpos_eq_241/261` `+spr_d7` `inc_spr_ptr` 等** ── 也就是 PPU 計數器跟 vblank 觸發比對

cross-coupled latches(D-FF、6T cell)有**兩個合法 stable state**。 prune-merge 的「c1==c2 時跳過 enqueue」假設「equal-value merge 不會改變 resolved value」── 對純靜態邏輯成立,但對 cross-coupled feedback 不成立(merge 改變 group topology + 可能改變 stable state 的選擇)。 power-on settle 時 cell 收斂到 inverted 邊 → vpos/hpos 計數器一開始就 offset → `vpos_eq_241` 在錯誤 scanline 觸發 → vblank 狀態機 desync → `rendering_disabled` 永遠拉著 → 全黑。

OAM/palette cell 雖然 CPU writes 會 overwrite 修正,但 vpos/hpos 是 PPU 內部、CPU 不能直接寫 → 永遠錯下去。

### 7b. Partial mitigation in WireCore.System.cs

`ResetNes` 包了 save/restore EnablePruneMerge,強制 power-on settle + reset assertion 期間關閉 prune-merge。 dump @ 500K hc 跟 5M hc 跟 baseline byte-identical(0 行 diff)。 **但 full_palette @ 48 frame 仍渲染全黑** ── 證明 normal frame stepping 期間,每次 D-FF clock-edge 翻 state 也會 expose 同樣 skip,divergence 持續累積(35M hc 範圍內 5M hc 沒事 但 35M 已 黑)。

### 7c. 真正的 fix(未實作)

需要 parse-time 識別所有 cross-coupled SCC(D-FF、latches、6T cell),把這些 node 標記為「prune-merge 不可 skip」── 跟「策略三 拓樸層級化」是同類分析(需要 SCC detection + 邊界判斷)。 不在 math-algos 原本 scope 內。

### 7d. 對 showcase / 實際 usage 的影響

| 用途 | `--prune-merge` 是否安全 |
|---|---|
| bench-hc CPU-only ROM(blargg) | ✅ 安全(CPU 行為不受影響)|
| visual ROM(SMB / full_palette / 任何需要 PPU output 的)| ❌ **黑屏** |

→ 「math-algos 天花板 1.37×」這個數字**只對 CPU-only workload 成立**。 visual sim 必須關掉 `--prune-merge`,只能用 baseline + `--fast-path`(bit-identical,~1.05×)。

### 7e. 教訓

bench-hc + checksum 用單一 CPU-only ROM 不夠 ── observably-identical 的驗證集要至少涵蓋一個 PPU visual ROM(full_palette / Mario title screen / 任何 frame-screenshot 可 byte-compare 的)。 這條 lesson 也適用於後續任何 S1 優化的「等價性」聲明。

### 7f. 2026-05-24 真正的 fix(Gemini r3):topological group-ID

第一次嘗試的 "direct 2-cycle latch detection" 抓到 SRAM cells(pal_ram, oam_ram)但漏掉 D-FF outputs(vpos2, hpos2 ── Q 由 master-slave 內部 latch 驅動,自己不在 2-cycle 內),修不好。 諮詢 Gemini r3 後得到**正確 fix**:

> 「`NodeStates[c1] == NodeStates[c2]` 是 digital illusion 蓋在 analog reality 上。 唯一 mathematically safe 的 skip 條件是 **topological equivalence**:assign 每個 `ComputeNodeGroup` BFS walk 一個 fresh GroupID 給 walked nodes;skip iff `NodeGroupIDs[c1] == NodeGroupIDs[c2]`。 已經 physically tied 的兩個 node 加一個 parallel transistor 真的只變 resistance 不變 resolved value;不同 group 的 merge 必須 re-resolve。」

實作:`WireCore.PruneMerge.cs` 新增 `long* NodeGroupIDs`(per-node monotonic ID,init = own node id),`RecalcNode` 在 `ComputeNodeGroup` 之後配發新 `_nextGroupID++` 給 group 全員。 SetNodeState 的 prune-merge skip 改成 `if (NodeGroupIDs[c1] != NodeGroupIDs[c2]) EnqueueNode(c1)`。

### 7g. 實測效能(topology-group-ID fix 後)

| 配置 | hc/s | × baseline | Checksum | full_palette render |
|---|---|---|---|---|
| baseline | 36,323 | 1.000× | `0x54C4...` | ✅ 完整色票 |
| `--fast-path` | 37,935 | 1.044× | `0x54C4...` ✓ | ✅ 完整色票 |
| `--prune-merge`(fixed)| 35,875 | **0.988×** | `0x54C4...` ✓ | ✅ 完整色票 |
| `--prune-merge --fast-path`(fixed)| 37,105 | 1.022× | `0x54C4...` ✓ | ✅ 完整色票 |
| `--prune-merge --fast-path`(broken,前 commit)| 53,788 | 1.48× ⚠️ | `0xF393...` | ❌ 全黑 |

**重新校正的結論**:`--prune-merge` 那 1.32× 加速是**演算法不安全的副產物** ── 它 skip 的是 topologically-distinct 但 digitally-equal 的 case(亦即合法 cross-coupled cell 的 stable state 模糊性)。 改成 topology-correct 後 skip rate 大幅下降,prune-merge 單做甚至倒退(topology check 比 digital equality 多 2 個 long read,而 skip 機會少太多)。

所以**真正可用的 safe 加速天花板** = `--fast-path` 的 **~1.04×**(bit-identical 保證)。 原本 doc claim 的 1.37× 是 broken。 但!對 CPU-only workload(blargg test ROM 等),broken 的 prune-merge 仍 valid(checksum hash 跟 baseline 不同但 blargg PASS/FAIL 邏輯不依賴 PPU rendering),所以 academic 角度「達成 1.37× hc/s on 01-basics.nes」這個 number 仍然真實 ── 只是不能拿去 render visual。
