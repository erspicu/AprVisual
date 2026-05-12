# cpu-opt branch — event-driven IR runtime（「β」版，實驗性 CPU 高速化）

> branch：`cpu-opt`，從 **`0a4d758`**（= S3 末，S4 的 codegen 工作之前）branch 出去。
> 所以這個 branch **沒有** S4 那堆東西（LLVM backend / GPU backend / bit-sliced emit / Verilog emit、`LLVMSharp.Interop`+`Vortice.*` NuGet 依賴、`LlvmCodegen.cs`/`GpuCodegen.cs`/`GpuRunner.cs`/`GpuSpike.cs`、`EmitCsharpSource` 等）—— 那些對 event-driven 是純死重。
> 重用 main 上做好的 IR：S2 的 `IrEngine.NextExpr[]`（per-node boolean function）+ S3 的 γ.0（node aliasing）+ γ.1（size-1/2 SCC solver）。`--trace-cmp --engine ir` 在這 base 上 0 mismatch（driving-coverage 79.3%、843 node 在 56 個殘餘 SCC、其餘 hybrid bus → S1）。

## 0. 為什麼做這個 / 跟 S4 的差別

S4 探索完了（main 上）：step-4（EvalOrder 評估）有 C#-Expression-tree-JIT / stack-machine interpreter / LLVM-MCJIT / GPU-D3D11 四個 backend，全等價驗過 —— **沒有一個打得過 S1**（~40-45K hc/s / ~15s/幀）：
- 它們全是 **batch** —— **每半週期 re-eval 全部 ~11680 個 EvalOrder node**（不管哪個變了）+ step-5（S1 的 ProcessQueue 處理 843 SCC + 2086 bus + fanout）。冗餘 re-eval + step-5 主宰每半週期的成本。
- S1 是 **event-driven** —— 每半週期只 re-eval **變了的 ~幾十到幾百個 node**（recalc/processQueue 的 dirty-set）。

**β 的點子**（Gemini 當初為了 GPU 把它 deprioritize 的那個）：跟 S1 一樣 event-driven（dirty-set、只重算變的），但 **per dirty node 用 `EvalExpr(NextExpr[v])`（一棵靜態 boolean tree）取代 S1 的 `ComputeNodeGroup(v)`（走 transistor 連通群 + flags-OR + 256-entry LUT）**。對那 ~85% 有乾淨 NextExpr 的 node，這比 S1 的 group walk 快；殘餘 SCC node + hybrid bus + 行為式 memory node 退回 S1 的 group walk。dirty-set 的傳播用 S1 的 transistor-fanout（IR 的 NodeRef-fanout 的 superset —— 「某個 upstream 變了就 re-eval 這個 node」永遠安全，頂多多算一次）。

**關鍵**：β 用 **S1 的 settle 語意**（event-driven、按時間順序 —— dirty-set 按順序處理變化直到 quiescent），只是 per-node 算得更快。所以它**不撞 ping-pong / all-on-GPU 撞的那個牆**（PPU 的 within-half-cycle 時脈相位結構 —— 「settle 到 fixpoint @ 半週期結束的時脈值」是個不同的平衡點）。β 的 fixpoint = S1 的 fixpoint（同一個迭代順序）。所以 PPU 照樣對。

跟 S4 完全不同路：β 是「**少做事**」（只重算變的），不是「同樣的事編得更好 / 平行跑」。

## 1. 預期

比 S1 快 ~2-3×（粗估）：per-node 的成本從「走 ~3-5 個 transistor 的連通群 + OR flags + LUT lookup」（指標追逐、cache-unfriendly）變成「eval 一棵 ~3-5-node 的 Expr tree」（如果 compile 過更快）；dirty-set 的數量跟 S1 一樣。實測才算數。

## 2. 實作計畫（phases）

| phase | 內容 |
|---|---|
| **β.0** | `StepOneEventDriven()` 模式 —— 最小改動：基本上 = `WireCore.Step(1)`（S1 的 event loop），但 `RecalcNode(v)` 對 IR-covered 的 `v`（`NextExpr[v] != null && !InScc[v]`）做 `byte nv = (byte)EvalExpr(NextExpr[v]); if (changed) SetNodeState + enqueue fanout;` 取代 `ComputeNodeGroup`；其餘 `v` 走原本的 group walk。`Hold(id)`/`Prev(id)` 讀半週期開頭的 snapshot（同 driving mode）。`--engine event` flag + `--trace-cmp --engine event` 驗 0 mismatch。**注意**：S1 的 `RecalcNode(nn)` 算的是「nn 所在連通群的值」並把群裡所有 node 都設成那個值（一次 RecalcNode 可更新多個 node）；γ.0 之後大多數群是 1-node，所以 β 一次 RecalcNode 算一個 node、跟 S1 數量相當。先確保等價，再談速度。|
| **β.1** | 加速 per-node eval —— compile NextExprs：(a) per-node `Expression.Compile()` delegate（~11680 個 tiny delegate、startup JIT 慢但 per-call 快）、或 (b) per-node RPN bytecode（reuse `CompileFlatProgram` 的 opcode，但 per-node index）+ 快的 byte-stream interpreter、或 (c) precompute「constant-fold + 把 Expr tree flatten 成連續 array」。先試 (a) 看 startup 開銷可不可接受；不行就 (b)。benchmark。|
| **β.2** | 加速 dirty-set / fanout —— 對 IR node 用 IR 的 NodeRef-fanout（比 S1 的 transistor-fanout 小 —— 少 spurious wake）、cache fanout list、用 packed array 不用 List 等。對非-IR node 維持 S1 的 transistor-fanout。|
| **β.3** | benchmark vs S1（`--benchmark <rom> --engine event --frames N`）vs S1 / vs main 的 LLVM-step / C#-JIT-step（雖然在不同 branch、但數字可比）。目標 ~2-3× faster than S1。如果沒達到 → profile（per-node eval? fanout? snapshot?）。|

## 3. 等價 gate

`--trace-cmp --engine event`（跟 `--trace-cmp --engine ir` 同樣的對照 S1）→ 0 mismatch。先驗 bare-2A03、再驗全系統 nes-001。

## 4. 進度日誌

| 日期 | commit | phase | 內容 |
|---|---|---|---|
| 2026-05-12 | `4eccb6d` | branch 建立 + 設計 | `git checkout -b cpu-opt 0a4d758`（S3 末、S4 前）。寫這份設計。base 驗過（`--selftest` 全 PASS、`--trace-cmp --engine ir` 0 mismatch、S1 baseline ~40-43K hc/s / ~15s/幀）。|
| 2026-05-12 | `7145cf4` | β.0（WIP） | 實作了：`WireCore.ProcessQueueWith(Action<int>)`（= ProcessQueue 的 copy、每個 enqueued node 用 callback 重算；S1 的 ProcessQueue/hot-path 不動）；`IrEngine.UseEventDriven` flag + `RecalcNodeForEventDriven(nn)` + `StepOneEventDriven()` + `BuildIrFanout()`；`StepOneDriving` 在 `UseEventDriven` 時 dispatch 到它；`TestRunner --engine event`。預設 path 不受影響（`--selftest`/`--trace`/`--trace-cmp --engine ir` 全 OK）。但 `--trace-cmp --engine event` ~326K mismatch（第一個 t=6 `ppu.spr_d7_int#193`(hybrid)）。|
| 2026-05-12 | `<pending>` | β.0 修嘗試（仍 WIP，沒搞定） | 加了：(1) `EvalExprCurrent(Expr e)` —— β 的 `EvalExpr` 變體，`Hold(id)`/`Prev(id)` 讀**當前** `NodeStates[id]` 而非 `PrevStates`（transparent latch `Mux(clk,data,Hold(self))` 在 clk=0 時 hold 它**當前**的值 = keeper 語意；若 clk 是 mid-half-cycle 邊緣的 derived phase，正確值是 mid-half-cycle 值、不是半週期開頭 —— batch driving mode 靠 step-5 的 S1 ProcessQueue 重算 latch 補救，β 沒這個補救）；`RecalcNodeForEventDriven` 改用 `EvalExprCurrent`；`StepOneEventDriven` 拿掉 PrevStates snapshot（β 不用、省 ~14KB memcpy/半週期）。(2) channel-fanout：`RecalcNodeForEventDriven` 在 nn 改變後也 enqueue `nn.TlistC1c2s` 裡（gate 導通的）`other` endpoint（透過導通 pass 跟 nn 連的 node）。**結果**：bare-2A03 第一個 mismatch 從 t=11 推到 **t=107**（`EvalExprCurrent` 修了 CPU 的 latch），但**整體還是 ~425K（bare）/ ~444K（nes-001）mismatch**。**根因還沒抓到** —— `spr_d7_int#193`(hybrid) 在 t=6 該被 enqueue 重算但沒（β 漏了某條 fanout edge），或它 group 的某個 input stale。**下個 firing**：用 `--diag-node 193`（checking 模式可能不行、driving 的 TraceCmpDrive 有 diag）/ 手動 trace 抓到底漏哪條 edge；或**重新考慮 β formulation** —— candidates：(a) IR node 也走 `WireCore.RecalcNode`（完整 group walk），只是 group walk 過程中對「純-IR 1-node 葉子」用 EvalExpr 替代 `getNodeValue`+flags 累積（保留 group walk 的 side-effect）；(b) β 當「memoization」用 —— `RecalcNode(nn)` 先檢查 nn 的相關 input 自上次以來變了沒、沒變就 skip（不是 substitution）；(c) 老實承認 β 太難、loop 收尾。`UseEventDriven` 預設 false（opt-in），不影響任何既有東西。|
