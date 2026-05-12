# S3 γ 設計 — 救回 InScc node（提升 IR coverage 到 ~99%）

> 接 `01_whole-system提速_設計.md` 的 Step γ（Gemini 排的優先序：α → **γ** → S4-codegen-提前 → β?）。γ = 把那 5705 個 InScc node 化成可 evaluate 的 IR（GPU bit-slicing 跑不了 S1 的 switch-level BFS → coverage 不到 ~99% 整個 GPU 管線就斷了 → 這是專案生死線）。

> **備註（user 指示）**：S3 firing 23 收到 user「α 沒必要先做 → 直接 γ」的判斷理由（見下 §1）；user 也說「loop 到 s3 完全結束就停止」—— 即整個 S3（含 M4 = CUDA codegen）做完後 `CronDelete` 停 loop。

---

## 1. 為什麼跳過 Step α，直接做 γ

`01_*.md` 的 Step α（precise-enqueue bridge：`OpStore` 不 `EnqueueNode` IR node、只 enqueue 非-IR fanout）—— 重新檢視 firing-17 已經量過的數字後判斷 **α 的效益接近 0**：
- bridge 已經有 `OkToSkipInRecalc` 機制（IR-covered 且 channel-component 全 IR 且 NodeRef dep-closure 全 IR 的 node，`ProcessQueue` 可 skip `RecalcNode`）—— 但 14723 個 node 裡只有 **184 個**夠格，且 firing-16 量過「skip-184 對 benchmark 無差」（~165s vs 159.6s）。
- 原因：~5705 個 InScc + ~2126 個 hybrid bus = ~7831 個非-driving-covered node（53% 的 node）。一個 IR node 的 `NextExpr` 只要（transitively）引用到這 7831 裡的任何一個，它在 step 4（flat program）就是用 stale 值算的、**必須**在 step 5（ProcessQueue）被 `RecalcNode` 修正 —— 不能 skip。~96% 的 IR node 都被這 7831 污染 → α-style「skip pure-IR node」幾乎沒得 skip。
- **結論**：bridge 的冗餘 re-derive 是 InScc/hybrid 污染造成的 → 解法是消滅 InScc（γ），不是 α。γ 做完後 depImpure 大幅縮小 → α-style skip 才有意義（順序顛倒：γ 先，α（如果還需要）後）。Gemini 沒有 firing-17 的數字、所以排了 α 先；採用 user 的修正（直接 γ）。

## 2. InScc 的解剖（`--dump-scc` 新增的 SCC anatomy 診斷）

整片 `nes-001`：5705 個 InScc node / **2254 個 SCC**，**全部 size ≥ 2（0 個 size-1 self-loop）**。size 分布：703 / 79 / 54 / 38 / 26 / 20 / 14 / 13 / 11 / 11 / 11 / 9 / 8 / 7×5 / 6×… / …… —— top ~21 個 SCC ≈ 1087 個 node，**剩下 ~2233 個 SCC ≈ 4618 個 node → 平均 size 2.07** ⇒ **絕大多數是 size-2 的 cross-coupled pair**（~4400 個 node）。

大 SCC 的身份（按 named-node 名字判讀）：
| size | named | 是什麼 |
|---|---|---|
| **703** | 367 | **2A03 APU 的 DMC（delta modulation / PCM）sample timer + frame counter**（`cpu.w4014`, `cpu.pcm_t5`/`pcm_+t5`/`pcm_/t5`, … 一大串 `pcm_tN` 計數器 bit + 反相 + carry）|
| 79 | 45 | **APU noise channel 的 envelope/LFSR 計數器**（`cpu.noi_cN`/`noi_+cN`/`noi_/cN`）|
| 54 | 22 | **APU noise channel 的 timer**（`cpu.noi_tN`/`noi_/tN`）|
| 38 | 38 | **PPU sprite evaluation**（`ppu.sprite_in_range`, `ppu.spr_eval_copy_sprite_2`, `ppu.inc_spr_ptr`, `ppu.spr_ptr_overflow`, …）|
| 26 | 0 | 全 unnamed wire junction |
| 20 | 18 | **PPU 的 VRAM address 增量**（`ppu.vramaddr_v10_carry_in`, `ppu.fine_y_eq_7_and_rendering`, `ppu.inc_vramaddr_v_by_32`, … coarse-Y/fine-Y scroll counter 的 carry chain）|
| 14 | 9 | **PPU 的 chroma 生成 ring**（`ppu.chroma_ring3..5`, `ppu.chroma_ring_delayedN`, `ppu.chroma_ringN_save` —— 色彩副載波 phase ring）|
| 13 | 11 | **PPU 的 $2005/$2006 write toggle latch**（`ppu.hvtog`, `ppu./hvtog`, `ppu.toggle_hvtog`, `ppu.write_2005_or_2006` —— 那個著名的 "w" register）|

**PPU clock-generator node**（之前以為在 703 裡，其實不是）：`clk0_int#160 ↔ /clk0_int#182`（size-2 SCC），`pclk0#115 ↔ pclk1#82`（size-2 SCC）。NextExpr：
- `clk0_int = !(/clk0_int | A) & (Hold(clk0_int) | B)`，`/clk0_int = !(clk0_int | B) & (Hold(/clk0_int) | A)` （A = n374、B = n2744 —— 兩個 clock-derived 的 async input）。手算 fixpoint：A=1 → q=0；A=0,B=1 → q=1；A=0,B=0 → hold Prev(q)。⇒ **`NextExpr[clk0_int] = Mux(A, False, Mux(B, True, Prev(clk0_int)))`**（reset 優先於 set）；`/clk0_int` 對稱。
  - 為什麼 Stage A2 沒抓到：Stage A2 的 pattern 要求「pull-up 是 StaticLoad/StrongVcc」，但這裡 pull-up 是 `Conditional`（`Hold(q) | B` —— pull-up transistor 被 q 自己 gate 的 feedback，是 dynamic latch 不是 static load）。

## 3. γ 的兩個子步驟

### γ.1 — 通用 size-2（含 size-3?）SCC solver（= Stage A2 的一般化）—— 最大的單一 win（~2200 SCC、~4400 node）
對一個 size-2 SCC `{a, b}`（`NextExpr[a]` 引用 `Node(b)`、`NextExpr[b]` 引用 `Node(a)`，可能各自也引用 `Node(self)`），用 **「兩步代換」** 求 fixpoint：
- `inner_b = NextExpr[b]` 把所有 `Node(a)→Prev(a)`、`Node(b)→Prev(b)`；   ← b 從 prev state 迭代一步的值
- `NextExpr'[a] = NextExpr[a]` 把所有 `Node(b)→inner_b`、`Node(a)→Prev(a)`；   ← a 在 b 更新後的值（= fixpoint，因為 2-node SCC 從一致的 prev state 出發、2 次 a-更新內收斂）
- `b` 對稱（`inner_a`、`NextExpr'[b]`）。
結果只引用 `Prev(a)`、`Prev(b)` + `f_a`/`f_b` 引用的 async input（那些是 SCC 外的 IR node）⇒ a、b 脫離 SCC、放進 EvalOrder。已用 `clk0_int` 手驗（§2）。風險：中（2-node bistable 從一致 prev 出發 ≤2 次收斂的假設；checking 模式會抓到錯）。size-3 也許可以（3 步代換、expr 體積 ×~倍數），size ≥4 expr 爆炸 ⇒ 只做 size 2（必要時 3）。**注意**：要在「現有 Stage A/A2 跑完、還留在 SCC 裡」的 pair 上做（不是所有 cross-couple；已被 Stage A/A2 處理掉的就不碰）。
- 開放問題：size-2 SCC 裡 `NextExpr[a]` / `NextExpr[b]` 是 S2.2 抽的（含 `Node` ref）—— 但有些可能是 `ComplexExpr`（抽不出來）或 hybrid（`NextExpr==null`）。如果 pair 裡有一個是 `null`/`Complex` → 這個 pair 救不了、留在 SCC（fall back S1）。

### γ.2 — 大 SCC（703 / 79 / 54 / 38 / 20 / 14 / 13）—— feedback-edge cut at register boundary
這些是 counter / LFSR / ring / carry-chain tangle —— 不能兩步代換（size 太大、expr 爆炸）。做法 = 找一組 feedback edge cut + `Node→Prev` 替換，**cut 點必須在 register 邊界**（= latch 的輸出 node、由 clock 控制更新；不是 stage 內部的組合邏輯邊）。Gemini 的 classifier 建議：NMOS 的 register 邊界 100% 由 pass-transistor 控制 → 找出受 `clk0`/`pclk0`/`pclk1` 等 clock-like node 驅動的 pass-transistor 的寫入埠 → 只有這些 node 的輸入端可標 `Prev()`。
- 具體：對大 SCC，跑一個 Tarjan-DAG 縮點看內部結構；找出「被 clock-like node gate 的 pass-transistor 餵的 node」（= 同步 latch 輸出）作為 cut candidate；cut 它們的「current-value 入邊」（NextExpr 裡引用它們的地方改成 `Prev`）→ SCC 是否崩解？崩解後重新 BuildEvalOrder。
- 之前 Stage D 為什麼壞掉（要避免）：盲目切 back-edge、cut 點不對（cut 了「不能切」的組合邏輯回饋邊，例如 multi-phase clock 的組合邏輯）→ 破 checking 模式（`clk0_int` 每半週期變、`Prev≠Node`）+ 除頻鏈抽出來的 NextExpr 本身就錯。→ γ.2 必須先有 clock-domain classifier、只 cut 同步 latch 的輸出邊。
- 開放問題：(a) 怎麼可靠地認出「clock-like node」（clk0/pclk0/pclk1 是已知的；APU 的 frame-counter clock / DMC timer clock 呢？看名字？看 fanout 大小 + 週期性？看是不是某個分頻 chain 的輸出？）；(b) cut latch 輸出邊後、那個 latch 的 NextExpr 要重抽（從 pass-transistor 寫入埠的 data input + clock condition 組出 `Mux(clk_cond, data, Prev(self))`）—— DriveAnalysis 的 `Passes`（transmission-gate 寫埠）已經有這資訊，要不要在 SccModel 階段重組？(c) 703-node 的 DMC timer 是個 down-counter with reload —— cut 它的 carry-chain feedback 後，`NextExpr[bit_i] = Mux(load, reload_val_i, Mux(tick, bit_i XOR carry_in_i, Prev(bit_i)))` 之類 —— 這能不能自動從 netlist 抽出來、還是要 pattern-match「這是個 down-counter」然後發手寫 IR？

### γ.3（之後）— 剩下的「26-node 全 unnamed」+ 零碎 SCC + 真 hybrid bus
26-node 全 unnamed 的 SCC = 某個 macro cell 內部的 wire tangle（沒 named-node、observable 性低）—— 可能可以直接 hybrid-ize（交給 S1）而不影響 coverage 計算（unnamed no-fanout node 本來就不算 observable）。真 hybrid bus（BD0-7、io_db、exp_in 那些 ~2126 個）—— 那是 multi-driver bus，要 GPU 上做的話得另想（bit-sliced 的 tri-state bus 模型；S4 的事）。

## 4. 想問 Gemini 的（這次 review 的重點）

1. **γ.1 的「兩步代換」對嗎**？2-node bistable latch 從「一致的 prev state」出發、`a_next = f_a(f_b(Prev))` 一定收斂到 fixpoint 嗎（會不會有需要 3+ 步的情況、或 metastable）？size-3 該不該做（3 步代換）？有沒有更穩的 closed-form（例如：把 SCC 當一個 multi-output sequential cell、列出 `Node` 變數的 boolean 方程組、用 BDD/SAT 解 fixpoint）？
2. **γ.2 的 clock-domain classifier**：怎麼可靠地認出「clock-like node」（除了寫死 clk0/pclk0/pclk1）？對 NES 的 APU（frame counter、DMC timer、各 channel timer 都有自己的分頻 clock），pattern-match 大概要認哪幾種（down-counter-with-reload？linear feedback shift register？binary ripple counter？divide-by-N？）？cut latch 輸出邊後重抽那個 latch 的 `NextExpr`（從 DriveAnalysis 的 transmission-gate 寫埠資訊組 `Mux(clk_cond, data, Prev(self))`）—— 這個重抽該放在哪一層（SccModel？新的 Stage？）？
3. **優先序 / 範圍**：γ.1（救 ~4400 個 size-2 node、把 coverage 從 46.3% 拉到 ~76%）做完就先 commit、再做 γ.2？還是 γ.1+γ.2 一起設計好再動手？703-node 的 DMC timer 值不值得花大力氣（DMC 在很多 game 沒用、但「整片 NES」目標要求它正確）？
4. **「夠」的判準**：Gemini 之前說 coverage 及格線 99-100%。如果 γ.1+γ.2 後還剩 ~500-1000 個 node（某些 macro-cell internal tangle）—— 那些是不是「永遠不會動 / observable 性低」可以豁免、還是非得清掉？

## 6. Gemini 評估（log `tools/knowledgebase/message/20260512_152404.txt`）+ 採用的計畫

「跳過 α 直接 γ 非常正確且務實」（53% node 不純淨 → 依賴傳播污染整圖 → precise-enqueue 形同虛設）。「正式進入消滅拓撲環（Acyclic Graph Extraction）的深水區 —— 這是 GPU bit-slicing 唯一的路（GPU 跑不了 event-driven BFS、只能跑 DAG）」。逐題（全採用）：

1. **γ.1 兩步代換**：對（雙穩態電路的定點迭代）。`a' = f_a(f_b(Prev))` 化簡後一定只依賴 `Prev(a)`/`Prev(b)` + 外部訊號；若代入 2 步後**仍**強烈依賴 `Node(a)`/`Node(b)`（布林化簡消不掉）→ 這不是 latch、是震盪器/race → **反而不該提出 SCC**（留在 SCC 交 S1）。**size-3 不建議**（通常含邏輯延遲的迴路 —— ring osc / glitch-filter，盲目 3 步代換容易抽錯）。BDD/SAT 閉式解：對 k≤4 的微小 SCC 最穩健且數學上正確，但**絕對無法擴到 703**（狀態空間 2^703）。⇒ γ.1 = 「代數代換 + 布林化簡器」、限 **k=2**，極低成本殺 ~4400 node。
2. **γ.2 千萬不要 pattern-match counter/LFSR**（會陷泥淖）—— 只要找 **register boundary**。「clock」在 switch→gate level 只是「控制 pass-transistor 的控制端訊號」、不需要知道它是分頻器還是主頻。**Stage B = Pass-transistor Cut**：掃大 SCC 內所有 node → 找被 pass-transistor 驅動的 node（NES NMOS 邏輯裡 latch 的輸入幾乎 100% 來自 pass-transistor）→ 切斷該 pass-transistor 的「資料輸入端 / 輸出端」、標 `Prev()` → 重抽 `NextExpr[latch] = Mux(pass_gate_control, pass_gate_data, Prev(latch))`。**為什麼不用管 703 是什麼 counter**：一旦 703-SCC 裡所有 latch 輸入端都正確切斷 + 加 `Prev()`，這個巨環就**瞬間瓦解（shatter）成一堆 DAG** —— counter 的加法器/carry-chain、LFSR 的 XOR 本質上只是夾在 `Prev(latch_A)` 和 `NextExpr[latch_B]` 中間的純組合邏輯，邏輯綜合器會自然把這些 NAND/NOR/XOR 展開成一個巨大但完全 acyclic 的布林表達式。**不要寫 hardcoded pattern matcher**。
3. **優先序 = γ.1（commit）→ 處理同義 node（見下 §"node aliasing"）→ γ.2**。γ.1 = low-hanging fruit、影響範圍明確（只動 size-2）、做完立刻 commit、coverage 46.3% → 70%+、也讓剩下的圖變乾淨。大 SCC（含 703-node DMC）**絕對值得花力氣** —— 不是為了跑某些 game，是**為了能上 GPU**（只要一個 node 留在 SCC，GPU 就得在那跑 while-loop 收斂 → SIMT 毀滅性）。
4. **「夠」的判準（for GPU bit-slicing）= sequential node 100% 抽離成 `Prev()` 邊界、combo node 100% 變 DAG**。剩 500-1000 個**不能豁免**（GPU warp divergence：64 個模擬器在同一 warp、一個進 SCC oscillation → 64 個都陪等）。GPU fallback「一半 GPU 一半 CPU」實務上很難（PCIe/memory 傳輸延遲吃光效能）。最終手段：剩幾十個 unnamed tangle 抽不出 latch 模型 → codegen 時把它們包進 `for(i=0;i<3;i++){...}` 微型迭代迴圈強制收斂（很破壞美感、極力避免）。
5. **`+`/`/` 前綴的多版本 node = 「神來之筆 / Critical Insight」—— Node Aliasing / Buffer Removal，必須在更早階段做！** NMOS/CMOS 網表因 fan-out 限制有一堆 buffer node（`pcm_t5`、`pcm_+t5`）/ inverter node（`pcm_/t5`）。**在「建 NextExpr / 拓樸排序前」新增一個 lowering pass**：找出所有純 buffer（`NextExpr[a] = Node(b)`）→ 圖中所有對 `a` 的引用換成 `b`；找出純 inverter（`NextExpr[a] = Not(Node(b))`）→ 引用換成 `!b`。**為什麼重要**：大幅減 node 數、且很多看似 size-4/6 的 SCC 經 aliasing 後現出原形變成標準 size-2 cross-coupled pair → γ.1 抓到更多。
6. **codegen 後端（S3 之後）= 強烈推薦 LLVM via .NET（選項 c）**，(a) Roslyn 字串 / (b) Reflection.Emit IL 都有嚴重瓶頸（35000 行 + 數萬區域變數 → Roslyn parser 慢、RyuJIT register allocation 算法在巨型方法上耗極長時間甚至 fallback 到 unoptimized 機器碼）。LLVM：`-O3` 做跨全域 constant folding / DCE / instruction combining（進一步壓縮網路圖）+ greedy register allocator 處理巨型 basic block 遠超 RyuJIT + **GPU retargeting（一擊必殺）：同一份 LLVM IR 經 .NET LLVM binding 一鍵 retarget 到 CPU(x86/ARM) 或 NVPTX(CUDA)，只維護一套 IRBuilder 邏輯**（用 C# emit 字串的話未來上 GPU 要另寫一套 emit CUDA C/PTX、維護兩套 codegen 後端會發瘋）。硬體模擬界（Verilator）產巨量 flat SSA 是家常便飯、LLVM 架構就是為這種巨型 IR 而生。

**Gemini 的 action items**：(1) Node Aliasing pass（清 `+`/`/` buffer/inverter 冗餘、縮小 SCC）；(2) γ.1 size-2 solver（代數代換 2 步、清 ~80% 微型環）；(3) Stage B Pass-transistor Latch Extraction（只切 pass-transistor 邊界、讓布林展開去處理 703-node counter，不寫 hardcoded pattern matcher）；(4) 準備迎接全 acyclic 圖 → GPU bit-slicing 大門開啟。

## 7. 採用的 γ 實作順序

- **γ.0 = Node Aliasing / Buffer Removal lowering pass** ✅ **已實作（firing 24）** —— `src/AprVisual/Sim/Logic/NodeAlias.cs`：把純 buffer（`NextExpr[a] ≡ NodeRef(b)`）/ inverter（`≡ Not(NodeRef(b))`）解析到 chain root（chase 鏈、偵測 buffer/inverter loop 不碰 —— 那是 γ.1 的 latch），然後 rewrite 所有 NextExpr 讓沒有 NodeRef 指向 aliased node（aliased node 本身留著、`NextExpr[a]` 改成 `NodeRef(root)`/`Not(NodeRef(root))`、變成沒入邊的 leaf）。在 `IrEngine.Build()` 裡 `SccModel.Build` 之後、`BuildEvalOrder` 之前呼叫。`IrEngine.AliasedNodeCount` 計數。Hold/Prev 不 rewrite（仍正確、且不形成 dep edge）。**效果**：InScc 5705→**5183**（−522）、DrivingCovered 6818→**7340**（46.3%→49.8%）、最大 SCC 703→**496**（DMC tangle 裡 ~207 個 buffer/inverter folded out）。SCC 數量 2254 不變、但有 56 個變成 size-1 self-loop（原本 size-2 `v↔a(buffer of v)` → aliasing 後 `v→v` + a 是 leaf）。等價 0 mismatch（checking + driving）、`--selftest` 全 PASS（新增 `TestNodeAlias`）、`--trace` 跟 HEAD 一致。
- **γ.1 = 通用 size-2 SCC solver** ✅ **已實作（firing 25）** —— 在 `IrEngine.BuildEvalOrder` 裡：把「build dep graph + Tarjan」抽成 local func、加 `ResolveSize2()` local func，迴圈（最多 8 次：Tarjan → ResolveSize2 → 若有 resolve 就 rebuild deps + 再 Tarjan）。`ResolveSize2`：對每個 size-2 SCC `{a,b}`（`NextExpr[a]`/`NextExpr[b]` 都非 null 非 Complex），算 `innerB = Sub(eb, a→Prev(a), b→Prev(b))`、`nextA = Sub(ea, b→innerB, a→Prev(a))`（= fixpoint）、對稱算 `nextB`；因為代換把所有 `Node(a)`/`Node(b)` 都換掉了 → 結果**保證**不引用 `Node(a)`/`Node(b)`（仍 defensive check 一下）→ 採用（`NextExpr[a]=nextA`、`InScc[a]=false`、`IsSequential[a]=true`、`Hybrid[a]=false`）。新增 `IrEngine.Substitute(e, Func<int,Expr?>)`（遞迴換 NodeRef、Hold/Prev 不碰、用 smart ctor 重建）+ `IrEngine.Size2ResolvedCount`。布林化簡用現有 smart ctor 就夠（cycle 消除靠代換、不靠化簡）。**效果（整片 nes-001）**：ResidualSccNodes 5183→**899**（−4284！）、DrivingCovered 7340→**11624**（49.8%→**78.9%**！）、SCC 數量 2254→**112**（56 size-1 self-loop + 56 multi-node、最大還是 496 = APU DMC tangle）；flat program 38,446→**98,370** 條（resolved node 的 nextA 含 innerB 的 copy → 體積 ~2× → 之後 CSE pass 可消掉、或交給 LLVM `-O3`）。等價：`--trace-cmp`(checking) 0 mismatch 2500 hc、`--trace-cmp --engine ir`(driving) 0 mismatch 6000 hc + 另一顆 ROM(AccuracyCoin) 0 mismatch；`--selftest` 全 PASS；`--trace` 跟 HEAD 一致。**注意**：DMC tangle（496-node）+ 其餘 ~400 node 還在 SCC ⇒ γ.2（Stage B pass-transistor cut）。56 個 size-1 self-loop（γ.0 把原 size-2 `v↔buffer-of-v` 折成 `v→v` 造成的）—— 還沒處理（Prev-cut？或 γ.2 順便）。
- **γ.2 = Topological-Loop Breaker（targeted SCC back-edge cut）** —— firing 27 拿到 Gemini 的明確演算法（log `..._155332.txt`）：
  - **剩下的 56 個 multi-node SCC（size 5~496）全是「靠 clock phase 隔離的拓樸環」** —— shift-register / phase-ring（PPU chroma ring、hvtog "w" reg）/ synchronous counter（APU noise timer / DMC down-counter）。每個 SCC 裡：「latch 類 node」的 NextExpr 已經是正確的 `Mux(clk_phase, Node(data), Hold(self))` 形（S2.2 抽對了）；「組合邏輯 node」（detector NOR / carry chain）讀 latch 的 `Node` 值。cycle = 組合邏輯讀 latch current、latch 又吃組合邏輯（或前一級 latch）current → 繞成環。
  - **演算法**：γ.1 迴圈跑完後 → build deps + Tarjan → 對 size ≥ 3 的 SCC：在 SCC 的 induced subgraph 上跑 DFS（**從組合邏輯 node 開始**，這樣 back-edge 自然落在 latch 上）→ 收集 back-edges `(consumer → latch)`（consumer 的 NextExpr 引用 `Node(latch)`、latch 在 DFS stack 上）→ **只切「target 是 latch 類 node」的 back-edge：在 consumer 的 NextExpr 裡把 `Node(latch)` 換成 `Prev(latch)`** → rebuild + 再 Tarjan，直到沒 SCC（或 cap）。
  - **為什麼這對**（Gemini）：(1) latch 是 stateful、它的輸出在「形成環的讀者」眼裡本來就該是「state 開始時的值」= `Prev(latch)`（synchronous system 的 Next-State 函數本來就依賴 Current State）；(2) **只切 SCC 內部的 back-edge** → 純 data-path 的 master-slave pipeline 不在 SCC 裡、不受影響（不會 over-cut、不會引入半週期延遲）；(3) clock phase 在 `Mux(clk_phase, ...)` 裡**一律用 `Node(clk_phase)`（current）**（transmission gate 是 level-sensitive；用 Prev 會漏掉只有 1 個 half-cycle 寬的 clock pulse；clock 產生器是單向 feed-forward 給 counter/reg、它自己的環已被 γ.1 解掉、對外 `Node(clk)` 已是 acyclic DAG node）；(4) detector（counter==0 之類）讀 `Prev(latch)` 是 physically correct（reload/carry 條件要在 clock edge 來臨前 evaluate 完）；(5) carry chain 內部（`carry_i` 依賴 `carry_{i-1}` current）**不用切** —— 它本來就是單向 acyclic chain，只切「latch → carry/detector 的源頭邊」整個 496-node SCC 就 shatter 成一棵 DAG（不用 pattern-match「這是 counter」）；(6) latch 判斷條件 `di[v].Passes.Count > 0 && di[v].PullUp == None && NextExpr[v] 是 Mux(g, ..., Hold(v)/Prev(v)) 形` 已足夠（不用判斷 g 是否 clock-like —— 任何接成 feedback-holding 的 pass-transistor 語意上就是 transparent latch）。等價 gate（checking 模式）做安全網。
  - 實作：在 `IrEngine.BuildEvalOrder` 的 γ.1 迴圈後加一個 γ.2 pass（`ResolveSccsByLatchPrev()` 之類）；latch 判斷可用「`NextExpr[v]` 含 `Hold(v)`/`Prev(v)` 自我引用」（不需要 `di`、雖然 `di` 更精準）。
- **γ.3 = 收尾**：剩下的零碎 SCC / unnamed tangle —— 看能不能再 alias / 再 size-2、或最終手段（micro-iteration block）。目標 coverage ~99-100%。

## 8. 本檔狀態

- firing 23（γ ①）：加 `--dump-scc` SCC anatomy 診斷 + 寫本設計 + 丟 Gemini + 採用計畫。
- firing 24（γ ② part 1）：實作 **γ.0 Node Aliasing pass**（`NodeAlias.cs`）—— InScc 5705→5183、最大 SCC 703→496、等價 0 mismatch。
- firing 25（γ ② part 2）：實作 **γ.1 通用 size-2 SCC solver**（兩步代換）—— InScc 5183→899、DrivingCovered 7340→11624（78.9%）、SCC 2254→112、等價 0 mismatch。
- firing 26（γ ② part a）：γ.1 也吃 size-1 self-loop（1 步代換）—— InScc 899→843、DrivingCovered→11680（79.3%）、SCC 112→56（全 multi-node）。
- firing 27（γ ② part b = γ.2 ①）：加 `--dump-scc` 的 per-SCC node detail 診斷（pass ports / pull-down / pull-up / SCC-internal NodeRefs / NextExpr）→ 看清剩 56 個 SCC 的結構（全是 clock-gated pass-transistor latch 串的 shift-register / counter / phase-ring）→ 寫上面的 γ.2 演算法 + 丟 Gemini（`..._155332.txt`，明確演算法 + 全採用）。**下個 firing（γ ② part c）：實作 γ.2** —— `IrEngine.BuildEvalOrder` 的 γ.1 迴圈後加 γ.2 pass（targeted SCC back-edge cut：DFS 從組合 node 開始、找 back-edge `consumer→latch`、在 consumer 的 NextExpr 把 `Node(latch)→Prev(latch)`、rebuild+再 Tarjan、直到沒 SCC）→ 驗收（等價 0 mismatch + coverage ~99%+ + 不破壞 S1）。

(④⑤ 實作筆記 + 進度日誌 → 接在 `00_S3_效率與優化.md`。)
