# S4 — codegen 設計（IR → 可執行 bit-sliced 模型；C# / LLVM）

> **這是 S4 的工作**（`MD/struct/08` 的四階段裡 **S4 = codegen + GPU**）。設計是在 S3 後期草擬的（Gemini 判：別繼續鑽 γ.2 over-cutting、把 codegen 提前 —— 但「提前」指的是「設計先想、實作先動」，不是把它重新歸到 S3；它在 chain 上仍是 S4）。S2 / S3 的中間產物已穩定（見下「§1 目前的 IR 資產」），S4 從那裡接手。
> 接 `MD/impl/S3/02_γ_救回InScc_設計.md` §9。user：codegen 用 C#；Gemini：後端推 LLVM-via-.NET（同一份 IR 能 retarget NVPTX(CUDA)）。**狀態：未開始**（本檔只是設計草稿、§6 的問題還沒丟 Gemini）。

---

## 1. 目前的 IR 資產

- `IrEngine`：每個 node 的 `NextExpr[v]` = 「下半週期 settle 後 v 的值」的 boolean tree（`NodeRef(id)`=current 值 / `Hold(id)`/`Prev(id)`=上半週期值 / `Mux/And/Or/Not/Const`）。
- driving 模式（已驗證 0 mismatch）：每半週期 ① `PrevStates = NodeStates` snapshot ② `RunHandlerChain`（master clock toggle + 行為式 RAM/ROM）deferred-enqueue ③ `ProcessQueueOneLevel`（flush 一層）④ `RunFlatProgram`（按 `EvalOrder` 拓樸序評估 12501 個 driving-covered node 的 NextExpr → flat stack-machine instruction stream，~99K 條）⑤ `ProcessQueue`（S1 的 switch-level BFS 處理剩下的：843 個 SCC node + ~2200 個 hybrid bus node + 它們的 fanout）。
- coverage：14723 個 node —— **12501（85%）driving-evaluated**（有 NextExpr 且不在 SCC，γ.0+γ.1）、**843 在 56 個殘餘 SCC**（counter / shift-register / phase-ring；γ.2 暫停）、**~1379 hybrid bus**（`NextExpr==null`；BD0-7 / io_db / exp_in 那種 multi-driver tri-state bus）。

## 2. 目標 / milestone

- **M_codegen-cpu**：從 IR 產生一個 C# 的 `Step(state)` 函數（per-instance，先非 bit-sliced），跑 benchmark vs S1（2 frame branch_timing = 29.7s）。要 100% 等價（每個 observable node 跟 S1 一致）。
- **M_bitslice-cpu**：把 `Step` bit-sliced（`UInt64` → 64 個並行 NES instance，或 AVX2/512），證明吞吐量隨並行度 scale。
- **M_gpu**（最後）：同一份邏輯 retarget CUDA/NVPTX（這就是 LLVM-via-.NET 的賣點）。
- 「S3 完全結束」≈ M_codegen-cpu 或 M_bitslice-cpu（有可用快速後端 + 等價 + benchmark 提速）；M_gpu 算 bonus / S4。

## 3. codegen 要處理的三類 node

1. **driving-covered（85%，有 NextExpr、acyclic）**：直接把 `NextExpr[v]` 的 tree 編成 `node[v] = <expr>`，按 `EvalOrder` 順序排（拓樸 → 一個 pass）。`NodeRef(x)` → `node_cur[x]`、`Hold(x)`/`Prev(x)` → `node_prev[x]`、`Mux(c,a,b)` → bit-sliced 是 `(c & a) | (~c & b)`、`And`/`Or`/`Not` → `& | ~`。這部分就是 `RunFlatProgram` 的編譯版本（現在是 interpreter、要變成 emitted code）。
2. **殘餘 SCC（843 個 node）→ Fixed-K Micro-Evaluation**（Gemini）：給 SCC 一個任意拓樸序，emit `for k in 0..K(=3~8): { node_cur[v] = <NextExpr[v]>; ... }`（`#pragma unroll` / 手動展開）—— `Node(x in SCC)` 讀 `node_cur[x]`（同一 K-loop 內最新值，Gauss-Seidel）、`Node(x not in SCC)` 讀已算好的 `node_cur[x]`、`Hold/Prev` 讀 `node_prev[x]`。init `node_cur[v] = node_prev[v]`。K 要夠大讓 settling ripple 收斂（小 SCC K=3~4、496-node DMC counter 可能要 K=~16~50；用等價 gate 決定）。GPU 友善（零 divergence）。
3. **hybrid bus（~1379 個 node）→ ???**（待 Gemini）：選項：
   - (a) 也用 Fixed-K micro-eval —— 但它們 `NextExpr==null`，沒 expr 可 eval。要先給它們一個「pseudo-NextExpr」：tri-state bus = `bus = Mux(drv1_en, drv1_val, Mux(drv2_en, drv2_val, ..., Hold(bus)))`（從 DriveAnalysis 的 `Passes` + pull-down 資訊組）—— 然後跟殘餘 SCC 一起丟進 Fixed-K block（bus 跟 SCC node 常常互相耦合，例如 PPU io_db ↔ palette ↔ ...）。
   - (b) 留給 S1（codegen 出來的東西混合呼叫一個小 S1 kernel for the bus nodes）—— CPU 上勉強可、GPU 上不行（warp divergence）。
   - (c) 把 bus 也當「狀態」、用 switch-level group resolution 的 bit-sliced 版（GND wins → VCC → drive → hold → 0 的 256-entry LUT）—— 但 group 是動態的（哪些 transistor 導通隨值變），bit-slice 不了。
   - 傾向 (a)：給 hybrid bus 抽 pseudo-NextExpr（`Mux` chain over drivers）→ 它們就變成「有 NextExpr 但可能在 SCC 裡」→ Fixed-K 處理。這也順便把它們從「S1-only」變成「IR-modeled」→ coverage 衝到 ~100%。

## 4. 後端：C# emit + LLVM emit，兩份並行做效能對照（user 定調 2026-05）

- **做兩份**：(1) **C# 後端** —— emit C#-source（一個巨大的 `static void Step(...)`、bit-sliced `& | ^ ~`）→ Roslyn 動態編譯。陽春、快 prototype、量 baseline、驗證邏輯正確。風險：~100K 行 / 數萬 local 的方法可能把 RyuJIT 的 register allocation 搞爆（量出來看）。(2) **LLVM 後端**（`LLVMSharp` / LLVM-C API via P/Invoke）—— emit LLVM IR → `-O3`（跨全域 const-folding / DCE / instruction-combining + greedy regalloc 處理巨型 basic block 遠超 RyuJIT）→ JIT/AOT 成 native。整個 codegen 工具都用 C# 寫（不引入 C++ toolchain）。**LLVM-in-.NET 的用法參考 user 抓下來的另一個 .NET+LLVM 專案**（同 owner、例如 github.com/erspicu/AprGba）。
- 兩份的等價 gate（逐 node vs S1）都要過；benchmark 報告列「S1 / C#-emit / LLVM-emit / + bit-sliced ×N」的 throughput 對照。Gemini（`..._152404.txt` §6 / `..._162049.txt`）強烈推 LLVM 是因為「同一份 LLVM IR 一鍵 retarget GPU、只維護一套 IRBuilder」—— 但 user 想兩份都做、量出差距，所以兩份並行。

## 4b. GPU 化 —— target 尚待討論（不急著定）

CUDA / NVPTX **不是唯一選項**。考量：(1) C# 對 CUDA 的 library 支援不一定好處理；(2) **GPU compute shader / shading language**（HLSL compute shader、GLSL/SPIR-V compute、WGSL …）可能更貼 .NET 生態 —— bit-sliced boolean kernel 本來就只是一堆 `& | ^ ~`，compute shader 跑得動；(3) **參考 user 的 `AprNesAvalonia`**（AprNes 的 .NET 版、用 Avalonia UI 框架、自己發展的 shader script）—— 那套「在 .NET 裡跑自訂 shader」的做法可以借。→ S4 的 GPU 那一步先不寫死 target；等 C#/LLVM 兩份 CPU 後端做出來、量過，再討論「CUDA-via-LLVM-NVPTX」vs「compute-shader」路線。

## 5. 驗證計畫

- bit-sliced `Step` 跑 N 個 NES instance（先 1 個，再 64 個都載同一個 ROM）→ 每半週期跟 S1（per-instance）比對每個 observable node。0 mismatch = 等價。
- benchmark：2 frame branch_timing，bit-sliced（64 instance）的 wall-clock / 64 vs S1 的 wall-clock → per-instance 吞吐量比。
- 階段：先 emit + interpret-equivalent 的 `Step`（驗證 codegen 的邏輯）→ 再 bit-slice（驗證並行）→ 再 LLVM（驗證後端）→ 再 GPU（bonus）。

## 6. 想問 Gemini 的

1. **hybrid bus（~1379 個 multi-driver tri-state bus node）怎麼編 codegen**？選項 (a) 抽 pseudo-NextExpr（`Mux` chain over drivers + `Hold(self)`）+ 丟 Fixed-K vs (b) 留 S1 vs (c) 別的？抽 pseudo-NextExpr 的話，drivers 的優先序怎麼決定（switch-level 是 GND > VCC > drive > hold > 0；對一個 tri-state bus 上多個 driver 同時 enable 的衝突怎麼處理 —— 假設設計保證只有一個 enable？還是要 model 衝突？）？這個抽取放哪一層（新的 γ.4 / 在 DriveAnalysis 裡）？
2. **Fixed-K 的 K 怎麼定**？對 496-node 的 DMC down-counter，settling depth（carry chain × per-bit loop）可能 ~50 —— K=50 的展開（每個 SCC node × 50 次）會不會太大？有沒有辦法分析出「這個 SCC 的最小 K」（= 它的 condensation DAG 的最長路徑 + 環長）、或對不同 SCC 用不同 K？Gauss-Seidel（in-place、按拓樸序）能不能讓 K 變很小（理論上一個拓樸序的 pass 就傳遞了大部分）？
3. **後端**：先 Roslyn-emit-C#-source（陽春、快 prototype、量 baseline）再換 LLVM、還是直接上 LLVMSharp？Roslyn 編譯一個 ~100K 行 / 數萬 local 的方法實務上多慢（編譯時間、JIT 時間、跑起來的速度）—— 你估計 S1 的 29.7s/2frame，Roslyn-emit 的 per-instance `Step` 大概多少？LLVM `-O3` 大概多少？bit-sliced ×64 之後 per-instance 大概多少？
4. **bit-slicing 的 word width**：`uint`（32 instance）/ `ulong`（64）/ AVX2 `Vector256` / AVX512 `Vector512` —— C# 上哪個最划算（`Vector<T>` / `System.Runtime.Intrinsics`）？GPU 上一個 thread 一個 instance 還是一個 thread 一個 bit-lane？
5. **整體**：這個 codegen 的最小可行版（MVP）是什麼？我想一個 firing 一個 firing 推 —— firing 1 = ? firing 2 = ? …。哪些可以先簡化（先不 bit-slice、先不 LLVM、先不 GPU、先不管 hybrid bus（暫時混合呼叫 S1））？
