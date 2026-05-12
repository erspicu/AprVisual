# 專案回顧 — 學到什麼、踩了哪些雷（2026-05-13）

> AprVisual：把 Visual6502-style switch-level netlist（2A03 NES CPU + 2C02 PPU）翻譯成可分析、可驗證、可執行的邏輯模型。目標寫的是「單台 correct + real-time-usable」。
>
> **最終狀態**：correct 達成；real-time **沒有**達成、而且確認**這條 pipeline 達不到**。沒關係 —— 過程裡釐清的東西（switch-level 模擬的本質、lift-to-logic 的邊界、各種加速路線為什麼贏不過原本的 event-driven sim）才是這份專案的產出。這份文件把那些記下來。

---

## 1. 一句話總結

**「把一顆真實晶片的 switch-level netlist lift 成 logic IR」是可行的（~85% 乾淨組合邏輯+latch、~6% 殘餘 SCC counter/shift-reg、~14% multi-driver tri-state bus），而且能等價驗證、能生 RTL / 多 backend；但「跑得比原本的 switch-level sim 快」不行 —— 因為原本的 event-driven dirty-set sim 對「14.7k node、每半週期只 ~幾百個變」這種 workload 已經是對的算法，batch-AOT / GPU 都是算法層面多做 ~50× 的工，codegen 再好也補不回來。要 NES real-time 得換一個根本不模擬電晶體的抽象層（= 行為式 cycle-accurate emulator，那是另一個專案）。**

---

## 2. pipeline 各階段做了什麼（= 留下的可重用資產）

| 階段 | 做了什麼 | 狀態 |
|---|---|---|
| **S1** | C# 重寫 MetalNES 的 switch-level event-driven 引擎（`WireCore.*`：`.js` 模組 parser、module instancing、`recalcNodeList`/`processQueue`/`recalcNode`/`computeNodeGroup`、256-entry group-resolution LUT、per-cycle handler chain、行為式 RAM/ROM、系統載入、trace）。算法 = `ref/metalnes-main` 的 `wire_compute`（本身是 Visual6502 `chipsim.js` 的最佳化 port）。 | **DONE**、過 blargg、~47K hc/s（~14s/幀 ≈ 0.07 FPS）。**永久的離線 golden reference。** |
| **S2** | netlist → boolean `Expr` IR（`ConstExpr`/`NodeRefExpr`/`HoldExpr`/`PrevExpr`/`NotExpr`/`AndExpr`/`OrExpr`/`MuxExpr`/`ComplexExpr`）：`DriveAnalysis`（每 node 抽 PullDown/PullUp/Passes）→ `NextStateModel`（每 node 的 `NextExpr` = 半週期 settle 後的值的 boolean tree）→ `SccModel`（Stage A: cross-coupled latch 回復、residual SCC 標記）。`IrEngine` 直譯器（checking mode = 跟 S1 並跑逐 node 比對；driving mode = IR 驅動 netlist + S1 bridge 處理 hybrid/SCC）。 | **DONE**、`--trace-cmp [--engine ir]` 0 mismatch（IR ≡ S1 兩 mode）。 |
| **S3** | whole-system IR 最佳化：γ.0（NodeAlias —— 折掉 always-on-wire 的 buffer/inverter 鏈）、γ.1（size-1/2 SCC solver —— 用代數 substitution 把小 SCC 溶進它的 reader）→ driving-coverage 46.3% → **~79%**（IR-covered ~85%；843 node 在 56 個殘餘 SCC、~2086 個 hybrid pass-transistor bus）。γ.2（topological loop breaker）—— over-cut、parked。 | **DONE**（穩定停止點）、等價 gate 過。 |
| **S4** | codegen：IR 的 EvalOrder（~11.7k 個 acyclic node）→ (a) C# Expression-tree-JIT 的 chunked delegate（`CompileChunkedStep`，預設 step-4）、(b) LLVM-MCJIT 的 `void step(i8* cur, i8* prev)`（`LlvmCodegen.cs`）、(c) D3D11 compute kernel（`GpuCodegen.cs` HLSL bytecode-interpreter + `GpuRunner.cs`）、(d) stack-machine 直譯器（`RunFlatProgram`）—— 四個 step-4 backend 全等價驗過。+ 殘餘 SCC 的 fixed-K（K=32）Gauss-Seidel micro-block（`Step_scc_fixedK`）+ hybrid bus 的 S0/S1/W1 wired-resolution model（`BusResolver.cs` + `ValidateBusResolver`，驗過 0-real-mismatch）+ bit-sliced（`ulong[]`，64 台/word）C# emit + `--dump-emitted-{cs,ll,verilog,hlsl}`。 | **DONE**（codegen + LLVM）；GPU 一台版本 = dead end；ping-pong run-path = parked。 |
| **cpu-opt「β」** | event-driven IR runtime（從 `0a4d758` = S3 末 branch 出去、不帶 S4 codegen）：S1 的 dirty-set event loop，但 per dirty node 用 `EvalExpr(NextExpr[v])` 取代 S1 的 `ComputeNodeGroup`。 | **parked**（`cpu-opt` branch `11a01d3`，~425K/444K mismatch —— 見 §3.3）。 |

驗證工具（也是資產）：`--trace-cmp`（checking）、`--trace-cmp --engine ir [--llvm-step|--gpu-step]`（driving 等價）、`--selftest`、`--trace branch_timing/1`（逐 cycle CPU 狀態）、`--benchmark`、`--dump-scc`/`--dump-next`/`--diag-node`、`--dump-topo-levels`。

---

## 3. 踩過的雷（語意 / 設計層面 —— 這部分最值錢）

### 3.1 「把所有東西都塞進 boolean `NextExpr` IR」—— 對組合邏輯/latch 對，對 multi-driver tri-state bus **錯**

一個雙向 pass-transistor bus（CPU data bus、PPU I/O bus、bitline）**不是 boolean node** —— 它是「mux + 強度比較器」（哪個 driver 強度高就贏：GND > VCC/pull-up > depletion load > hold）。γ.4 試著給每個 bus node 一個 wired-AND pseudo-`NextExpr`（`has_pd = OR(PullDown, 各pass: en_i & !Node(other_i))`、`has_pu = OR(<PullUp>, 各pass: en_i & Node(other_i))`、`NextExpr = Mux(has_pd, 0, Mux(has_pu, 1, Hold))`）—— 結果：(a) `io_db*` 那種多 driver bus 上 rare mismatch（「對方此刻是 0」被當成「對方在強力輸出 0」—— 丟失強度資訊），(b) 雙向 pass 把單向 logic cone 串成有向環 → 把 ~10k node 折成一個 **10077-node 巨型 SCC**（比原本的 843 大 ~12 倍、fixed-K 它的 K 可能 20~50、災難）。

**教訓**：bus 是**膠水層 / 迭代邊界**，不是 graph node。正解（S4.2b）= bus 不進 IR graph（不產生雙向 edge → 巨型 SCC 碎回健康的 843-node/56-小-SCC），另外一個獨立的 branch-free resolver block（每 bus 三個 bit-plane：`S0` Strong-0 / `S1` Strong-1 / `W1` Weak-1；`BusVal = (~S0&S1) | (~S0&~S1&W1) | (~S0&~S1&~W1&Hold)`；bus-to-bus 短路用 K_PASS≈3 的 double-buffered 傳播），main loop = data-flow ping-pong（`for outer: { Eval_DAG; Eval_SCCs; FireHandlers; Resolve_Buses }`）。這個 model 驗過 0-real-mismatch（~130-180 個「float-exempt」—— 見 §4.2）。

### 3.2 PPU 的 precharged-dynamic readout —— 「settle 到 fixpoint」永遠重現不了 S1 的 within-half-cycle event sequencing

PPU 的 palette RAM / sprite RAM 讀出走 precharged-dynamic bitline：在某個內部時脈相位 **precharge**（bitline 拉高）→ 下個相位 **discharge**（被選中的 cell 把它拉低 or 不拉）→ 再下個相位 **latch capture**。這些內部相位在一個 master 半週期**中間**邊緣（不是半週期開頭/結尾）。`NextExpr` 只描述「半週期結束時的值」—— S1 是靠它的 event queue **按時間順序**把 precharge→discharge→capture 這個序列擺對的。任何把「半週期 = settle 到 fixpoint @ 半週期結束的時脈值」的東西 —— γ.2、ping-pong run-path replacement、all-on-GPU —— 都到不了那個中間相位、所以 palette/sprite readout latch 整片壞掉（ping-pong: ~322K mismatch；bump K_OUTER 4→24 沒用 —— 是 model 問題不是 convergence 問題）。

**教訓**：lift switch-level → logic 時，**within-half-cycle 的 clock-phase 結構不是 implementation detail，是 essential semantics**。要嘛保留 S1 的 chronological event queue 處理那塊（= 現況：runtime 的 step-5 = S1），要嘛得顯式建一個 2-phase（甚至多-phase）precharge model（沒做、複雜度高）。β 沒撞這個牆是因為它**用 S1 的 settle 語意**（event-driven、chronological），只是 per-node 算得不一樣。

### 3.3 「per-node `EvalExpr` 取代 group walk」（β）—— 破壞導通群的原子性

S1 的 `RecalcNode(nn)` → `ComputeNodeGroup(nn)` 走出**導通群**（沿導通的 transistor channel），把群裡**所有 member 一次原子地**設成群值（物理上它們就是同一個電氣節點）。β 的 `EvalExpr(NextExpr[nn])` 只算 `nn` 自己 —— 當 `nn` 透過導通 pass 跟別的 node（hybrid bus / 另一個 IR node）在同一個導通群時，β 只更新 `nn`、群裡其他 member 還是舊值 → 中間出現「A=1、B=0 且 A、B 導通」這種**物理不可能的暫態** → 任何 callback / fanout 在這瞬間讀到就雪崩（mismatch 在幾百個半週期後爆炸）。試了「channel-fanout enqueue 補救」沒夠（Gemini：那只是延後不一致、最終必然演化成「重新發明一個有 bug 的 group walk」）。

**教訓**：β 的 fast path 只能用在 `nn` **動態孤立**（此刻的導通群只有 `{nn}`）時；一旦 `nn` 有導通的 channel-to-normal-node pass，就得退回 S1 的 group walk（它原子地更新整群）。沒實作完（parked 在 `11a01d3`）。即便做出來，Gemini 估的天花板 ~2-3×（~15s → ~5s/幀）—— 還是不 real-time。

### 3.4 γ.2 over-cut —— 「register reads Prev(data)」的隱含假設

γ.2（topological loop breaker）想把殘餘 SCC（counter/shift-reg）拆成「register `n_q <= Prev(n_d)` clocked by edge」—— 對 821/843 個 node 對（hpos counter、APU DMC、chroma ring…），但 over-cut ~2-3 處：「register reads `Prev(data)`」假設 data 在 register 的 clock edge 前是穩定的；當 data 依賴一個被**更早的 derived phase** clock 的 register 時就錯（6502 pipeline register 讀一個 non-stable data source、palette-RAM cascade）。parked（`Gamma2Enabled=false`）。

### 3.5 「AOT batch 一定比 interpreted 快」—— 錯（算法層面）

直覺：codegen 成直線 bitwise code、`-O3`，一定電爆直譯器。實測：IR-driving + C#-JIT step-4 = ~7K hc/s（比 S1 慢 6.5×）、+ LLVM-MCJIT-O3 step-4 = ~16K（慢 2.9×）。原因：IR-driving 的 step-4 每半週期 re-eval **全部 ~14.7k node**（不管哪個變），但實際只有 ~幾百個變 —— 算法層面比 S1 的 dirty-set 多做 ~50× 的工。LLVM 讓每個 eval 比一次 S1 `RecalcNode`（指標追 transistor list + flags-OR + LUT、cache-unfriendly）便宜 ~5-10×，淨值 ~50× ÷ ~10 ≈ 慢 ~3-5×（跟實測吻合）。**codegen quality 補不回算法層面的冗餘。** 連把 fixed-K SCC + bus resolver 折進 LLVM 也只會更慢（K=32 × PingPongK=4 加 ~108k 額外 eval/半週期）；連完美的 Verilator-式 single-pass model 也只是 ≈ S1 速度。

### 3.6 「GPU 平行 → 一定更快」—— 錯（一台 NES 是 latency game）

一台 NES 的一個半週期 = 14.7k node 的依賴鏈、要在很短時間內算完 → 這是 **latency** 問題，GPU 是 **throughput** 機器。一個 workgroup ≈ 1/76 of RTX 4080 的 SM；HLSL 沒 function pointer → codegen'd 邏輯只能跑 bytecode-interpreter kernel（thread = 一個 node、跑那 node 的 RPN bytecode、`AllMemoryBarrierWithGroupSync` 隔開拓樸層）→ opcode dispatch 8-way warp-divergent。實測 `--gpu-step`（per-step round-trip）~2.5K hc/s（慢 18×）、`--gpu-bench`（round-trip-free 下界，N 半週期全在 GPU、無 SCC/bus/handler）~4.2K hc/s（**還是慢 ~10.7×**）。GPU 的強項是**多台**（bit-sliced —— bit emit 已做、parked），不是單台。SKSL（`AprNesAvalonia` 的 CRT shader infra）是 fragment-only，根本不適合跑邏輯 compute（沒 groupshared / structured buffer）。

### 3.7 Gemini 的「900× → real-time」—— hand-waving，要自己量

Gemini 建議「100% IR + AOT 編成 bitwise C（Verilator 路線）→ 900× → real-time」。實際上 Verilator-class 提速建立在**乾淨 RTL**（register + combinational cloud、已經是同步抽象）上 —— 不是一個 ~15k-node 的 lift-自-switch-level 晶片模型；後者 event-driven 本來就是對的算法。Gemini 當顧問抓對了不少（γ.4 wired-AND 丟強度資訊、β 的原子性問題 —— 都點對了），但它的**量化估計**要存疑、自己量。（也好在這次先量了 —— 見 §4.3。）

---

## 4. 踩過的雷（工程 / 工具層面）

### 4.1 環境 / 工具鏈

- **`git commit -m "..."` 含 backtick / 括號** 在這個（Windows + bash-via-tool）環境會被 shell command-substitution 搞爛（pathspec error / 把 `` `...` `` 當命令執行）→ 一律 `git commit -F temp/cmsg.txt`，commit message 用 Write tool 寫進檔案（**不要**用 python `-c "..."` heredoc —— 長字串含 backtick/括號/CJK 會被 shell + mojibake 雙重 mangle）。`temp/` 是 gitignored。
- **HLSL / FXC（`cs_5_0`）**：① comment 裡不能有非-ASCII（em-dash → `error X3000: syntax error: unexpected end of file`）；② 沒有 `switch`（用 `if/else-if` 鏈）；③ local array（`uint stk[16]`）被 runtime index → `error X3531: can't unroll loops marked with loop attribute`（FXC 想 unroll 但 `[loop]` 禁止）→ 把 stack 移到 `groupshared uint GStk[NUM_THREADS*MAX_STACK]`（per-thread slice）；④ UAV（device memory）barrier 用 `AllMemoryBarrierWithGroupSync()` **不是** `GroupMemoryBarrierWithGroupSync()`；⑤ `[loop]` attribute、`SV_GroupIndex`。
- **Vortice NuGet**：沒有 "Vortice.Windows" meta-package（`NU1101`）—— 是個別的 `Vortice.Direct3D11` + `Vortice.D3DCompiler`（3.8.3）；Vortice 的 API 用 `uint` 不是 `int`（`BufferDescription.ByteWidth` / `BufferUnorderedAccessView.NumElements` / `StructureByteStride` 要 `(uint)` cast，否則 `CS0266`）。D3D11 headless：`D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, ...)`、`Compiler.Compile(hlsl, entry, name, "cs_5_0", out blob, out err)`、`RWStructuredBuffer<uint>`（`MiscFlags=ResourceOptionFlags.BufferStructured`）+ UAV + staging buffer（`Usage.Staging`, `CPUAccessFlags.Read`）+ `Map(MapMode.Read)`。
- **LLVMSharp.Interop**（+ `libLLVM.runtime.win-x64` 20.1.2 —— bundle libLLVM.dll，不用另裝 LLVM）：MCJIT pattern = `InitializeAllTarget*` + `LinkInMCJIT` → `LLVMModuleRef.CreateWithName` + builder（`BuildAdd/And/Or/Xor/Select/Load/Store/GEP/Ret`）→ `TryVerify` → `CreateMCJITCompiler` + `GetFunctionAddress` + cast `delegate* unmanaged[Cdecl]<...>`。**`LLVMModuleRef` 沒有 `RunPasses` instance method** → 用 static `LLVM.RunPasses(m, (sbyte*)passesPtr, default(LLVMTargetMachineRef), LLVMPassBuilderOptionsRef.Create())`（new-PM、AprGba 的 pattern；記得 `if (err != null)` check）。MCJIT 預設 opt ≈ -O2 —— 要 -O3 得自己跑 pass pipeline。block-JIT 那種要 lazy module 才需 ORC LLJIT；「一支大函式編一次」MCJIT 夠。
- **arg-parse 的 case 裡太早 `return`**：`--dump-emitted-ll` / `--llvm-codegen-test` 一開始在 case 裡直接 `return` → 在 `--system-def-dir` 還沒處理前就跑 → `compose failed: data\system-def\nes-001.js not found` → 要 **defer**（set 一個變數，parse loop 跑完再處理，像 `--dump-emitted-cs` 那樣）。
- **`NodeInfo` 的 namespace**：是 `AprVisual.Sim` namespace **層級**的 struct，不是 nested 在 `WireCore` 裡 → `ref NodeInfo ns` 不是 `ref WireCore.NodeInfo ns`（`CS0426`）。C# 會搜 enclosing namespace，所以 `AprVisual.Sim.Logic` 裡可以不加限定詞引用 `AprVisual.Sim` 的 type（`WireCore`、`NodeInfo`）。

### 4.2 驗證方法論

- **每階段 per-node equivalence gate（`--trace-cmp`）是命脈** —— S1 跟 IR 並跑、逐 node 逐半週期比對。沒這個的話 IR 跟 S1 的偏差會無聲累積，等發現時是 debug 地獄（β 的 ~425K mismatch 就是因為一個 fanout edge / 一個原子性問題 cascade 出來的）。
- **「驗證邊界」要想清楚**：unnamed + 無 gate-fanout 的 internal junction（NAND/NOR/AOI pull-down stack 的內部 node、carry-chain 中間 node、precharge-stack node）—— 它的值是寄生電容殘留、一旦真路徑導通就被覆蓋、它的 transient hold 是否符合 S1 的 event-queue-order artifact 是**邏輯上不可觀察**的（標準 EDA transistor→gate 等價檢查也 collapse 這些）→ 抽 model 但**不 gate 等價於它們**。同理 **float-artifact 豁免**：bus 且該半週期結束時 `S0==S1==W1==0`（Hold）跟 S1 不一致 → 那是「不可觀察的 transient」（「最大電容勝」我們不模型化）→ 像 unnamed junction 一樣豁免（~130-180 個、≈0.004% of 4.2M resolutions）。**「忠於邏輯正確性」>「忠於 S1 的 event-queue-order artifact」** —— 但要小心別豁免太多（每個豁免都要能說清楚為什麼不可觀察）。
- **LLVM JIT 的 benchmark 要排除 compile 時間** —— 編譯點放在 inner-loop 之前（`Build()` 末就 `LlvmCodegen.Compile()`），量的是編譯**後**的 inner-loop 速度，不是「編譯 + 跑」。

### 4.3 「先量再做」—— 這次最值得記的方法論

S4.5 收尾後，本來下一步是花好幾個 firing 把 fixed-K SCC + BusResolver 折進 LLVM `step`、再 benchmark。**改成先跑一個有界實驗**：用現有 infra 量 S1 / IR-C#-JIT / IR-LLVM 的 hc/s（~47K / ~7K / ~16K），再算一下「折進去」會增加多少工量（K=32 × PingPongK=4 ≈ +108k eval/半週期 ≈ 比現在的 step-4 大 ~10×）→ 結論：那條路是**負報酬**（會更慢不是更快），省下了那幾個 firing。**教訓**：投入大改之前，先用現有工具量一個下界 / 估一下工量級 —— 一個下午的 benchmark 抵得過一週的白工。

---

## 5. 結論性的 takeaways（如果只記三條）

1. **switch-level transistor sim 的 event-driven dirty-set（Visual6502 `chipsim.js` / MetalNES `wire_compute`）對「~15k node、每半週期只 ~幾百個變」這種 workload 已經是對的算法。** batch-AOT（C# / LLVM）算法層面多做 ~50× 的工、GPU 一台版本 latency 不對（~1/76 SM + warp-divergent）—— 都贏不過它。能贏它的只有 event-driven 的 IR 版（per-node 算更快、dirty-set 一樣 —— 「β」），天花板 ~2-3×、且有原子性難題沒解完。**GPU 的強項是多台（bit-sliced），不是單台。**

2. **把 switch-level netlist lift 成 logic IR 是可行且有價值的**（~85% 乾淨 acyclic+latch、~6% residual SCC 需 fixed-K iteration、~14% multi-driver bus 需 S0/S1/W1 wired-resolution model、bus 是迭代邊界不是 graph node）—— 而且能逐 node 等價驗證、能生 RTL（`--dump-emitted-verilog`）/ C# / LLVM IR / HLSL。**但「100% clean 純組合 IR」會撞 PPU 的 within-half-cycle precharged-dynamic 結構**（precharge→discharge→capture 是個時序，不是 combinational fixpoint）—— 那部分 S1 的 chronological event queue 是 essential、不是 implementation detail。價值在「可分析、可驗證、可生多 backend」，不在速度。

3. **NES real-time（~16ms/幀、要 ~840× over S1）這條 switch-level→logic-IR pipeline 達不到** —— 連完美的 Verilator-式 single-pass model 也只是 ≈ S1 速度。真要 real-time playability 得換抽象層：行為式 cycle-accurate emulator（不模擬電晶體、直接模擬「PPU 在第 N scanline 第 M dot 做什麼」）—— 那就是 `ref/AprNes` 本身，是另一個專案。**「translate the silicon」跟「emulate the console」是兩件事；這專案做的是前者。**

---

## 6. 留給未來的人（如果有人想接）

- **想要 fast 單台 CPU** → `cpu-opt` 分支的 β（`11a01d3`）：fix 那個原子性問題（只 fast-path 動態孤立的 node、其餘走 group walk），預期 ~2-3× over S1。設計 + 踩雷紀錄：`MD/impl/cpu-opt/00_event-driven_設計.md`。
- **想要 RTL / FPGA / Verilator** → `--dump-emitted-verilog` 已經能 emit（IR ≈ synthesizable RTL：EvalOrder→`assign`、latch/SCC→`always @(posedge clk)`、bus→resolved priority mux、memory→`reg [] mem []`）；可能性 + 用途見 `MD/impl/S4/01_S4.6_GPU_compute_設計.md` §8。Verilator 跑出來可能比 S1 還快（它的 ordering pass 處理 within-half-cycle 順序 —— 但要先解決 precharged-dynamic node 在 Verilog 裡怎麼寫）。
- **想要多台 throughput（GPU bit-sliced）** → `EmitCsharpSource(bitsliced=true)` 的 `ulong[]`（64 台/word）emit 已經有；`MD/impl/S4/01` 有 GPU 化的步驟拆解（一個 workgroup + groupshared state + barrier 隔拓樸層 + N 半週期/dispatch + memory handler 也在 GPU 上）；行為式 RAM/ROM 的 per-lane gather/scatter transpose 是巨坑（BMI2 / bit-twiddle）。
- **想要 100% IR / all-on-GPU 等價版** → 得先建一個顯式的 multi-phase precharge model 處理 PPU 的 palette/sprite RAM readout（`MD/impl/S4/00` §11/firing-13、`MD/impl/S3/00` γ.2 都卡在這）—— 是個沒人解過的硬問題。

S1 = 永久的離線 golden reference，任何上面的東西都拿它逐 cycle 驗。**不要試圖加速 S1 本身。**
