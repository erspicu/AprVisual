# math-algos branch — INDEX & FINAL VERDICT

> **Status (2026-05-23): Research-Complete**(Phase 2.5 codegen runtime-accelerator 路徑撞牆;按 Gemini r3 §Q4 wind-down,branch 工具留原地不 merge 回 main)。

---

## 0. 一張圖看完整個 branch

```
math-algos 目標(2026-05-23 user re-scope):
  CPU event-driven 加速 S1,distinct from main 的 GPU/oblivious 路徑
  real-time(840K hc/s)NOT a goal;目標 2-3× S1 (~38K → 80-120K hc/s)

實際 ceiling 路徑:
  Phase 1 (pre-IR pruning) ──→  1.37×  ────────┐
  Phase 2 (event-driven IR)─→  break-even ±2% ─┤
  Phase 2.5 (macro codegen):                    │
    Step 1 ALU 黑盒 native bench ──→ 18.8× ✓   │  ← 工具鏈價值
    Step 2 Dispatcher framework ──→ 4.6% ovh ✓  │
    Step 2.5 Writeback ──→ functional ≡ S1 ✓    │
    Step 3 Auto-partitioner ──→ 30 macro-block ✓│  ← Gemini 稱「聖杯級」
    Step 3.5 Own internal ──→ ❌ S1 BFS reach   │
    Step 3.5 Option D (Gemini r3) ──→ ❌ -0.4% saving < 5% overhead
                                                ▼
                              ALL PATHS WIND DOWN
                              code & docs frozen in branch
```

---

## 1. 文件總表(依順序)

| # | 文件 | 涵蓋 | 性質 |
|---|---|---|---|
| 00 | 00_design.md | math-algos 初始 design notes(re-scope 前)| 歷史 |
| 01 | 01_results.md | 初始 results(re-scope 前)| 歷史 |
| 02 | 02_phase1_structural.md | **Phase 1**:pre-IR pruning + levelize 死路 → 1.37× ceiling | 結果 |
| 03 | 03_phase2_scope.md | Phase 2 scope 定義 | 設計 |
| 04 | 04_phase2_ir_results.md | **Phase 2**:event-driven IR interpreter → break-even ±2% | 結果 |
| 05 | 05_codegen_design_notes.md | **Phase 2.5 設計**(Gemini r2 顧問;macro-block + dispatcher + 4-step decision path)| 設計 |
| 06 | 06_alu_validation_results.md | **Step 1**:ALU native bench 18.8× S1 ✓(GO signal) | 結果 |
| 07 | 07_dispatcher_framework_results.md | **Step 2**:bitmask-polling dispatcher + 4.6% overhead ✓ | 結果 |
| 08 | 08_step25_writeback_results.md | **Step 2.5**:writeback functional ≡ S1 ✓(no speedup yet,預料中)| 結果 |
| 09 | 09_step3_partitioner_results.md | **Step 3**:auto-partitioner → 30 codegen-attractive macro-block ✓ | 結果 |
| 10 | 10_step35_architecture_finding.md | **Step 3.5a**:reverse-closure 太亂 + S1 BFS reach → 架構限制發現 | 結果 |
| 11 | 11_optD_results.md | **Step 3.5 Option D**:Gemini r3 BFS-block 機制對,但 owned set 不可擴展 → wind down | **最終** |

---

## 2. Phase 2.5 codegen — 完整成果評估

### 2.1 工具鏈成果(可重用 assets)

✅ **WireCore.Dispatcher.cs** — bitmask-polling macro-block dispatcher
  - `uint64 dirty_mask` + TZCNT + jump-table switch(Gemini r2 §2.8)
  - 4.6% overhead in dry-run,trace IDENTICAL
  - `CodegenInputChanged` hook + `CodegenOwned` skip + Option D BFS-block 三層機制
  - 已有 `--codegen-dispatcher` / `--codegen-writeback` / `--codegen-own` CLI

✅ **WireCore.Partition.cs** — auto-partitioner(Gemini r3 §Q5 稱「聖杯級」)
  - Non-boundary connected component algorithm
  - 14.7K nodes / 26.7K transistors → 2,757 macro-block,30 個 codegen-attractive
  - 自動找到 PPU sprite eval (10×298 nodes)、PPU finex1 (188 nodes/27 outputs)、APU 各 channel
  - `--dump-partition` + `--dump-block-id <id>` CLI

✅ **WireCore.Group.cs Option D BFS-block** — 機制驗證
  - 5 行修改在 `AddNodeToGroup` 中
  - Trace IDENTICAL with 8 owned alu outputs(嚴格正確性驗證)
  - 可在 future codegen 試驗中 toggle 開關

✅ **AluBlock.cpp / AluBlockBindings.cs** — native ALU reference
  - 5-op (SUMS/ANDS/ORS/EORS/SRS) wired-OR 設計
  - 2-4 ns/call,2000/2000 correctness
  - 證明 LLVM 能編出 register-only / branchless mux 形狀

✅ **TestRunner CLI 擴充**
  - `--alu-bench [N]` — native ALU 微基準
  - `--dump-block <outputs>` `--block-stop <inputs>` — manual block 反向 closure
  - `--dump-partition` + `--dump-block-id <id>` — auto-partitioner 工具
  - `--codegen-dispatcher` / `--codegen-writeback` / `--codegen-own` — dispatcher 三模式

### 2.2 Runtime-accelerator 結論

❌ **在 S1 group-resolution 架構下,codegen runtime-accelerator 路徑不可行**:
  - S1 的 `ComputeNodeGroup` BFS 不會在 CodegenOwned 邊界停(沒有 Option D 時)
  - 加 Option D 後 BFS 停了,但能安全 own 的 named 節點 cap 在 ~62
  - Owned 62 → S1 work saving 0.4%,被 dispatcher 寫 36 nodes 的 hook overhead 完全蓋過
  - Net speedup = -3.2%

**根本原因**:6502 ALU 跟 CPU datapath 透過 dynamic mid + pass transistor 緊耦合,沒有清楚的 "block boundary"。要 own 整個 region 必須能 dispatcher 計算 70+ anonymous mid,但這些 mid 沒有 semantic 名字,工程上沒有可擴展路徑。

### 2.3 Phase 2.5 對齊 Gemini r2 & r3 設計目標

| Gemini r2 設計要件 | 對齊狀態 | 評語 |
|---|---|---|
| Bitmask polling dispatcher (§2.8) | ✓ Step 2 | 8/8 design points landed |
| Macro-block partitioning (§2.4) | ✓ Step 3 | 找到 30 個真實 macro-block |
| Per-block Context Struct | ✓ AluCtx (5-op) | 16-byte cache-line friendly |
| Hand-coded one block to validate (§5) | ✓ Step 1 ALU | 18.8× — 遠超 >3× 門檻 |
| LLVMSharp.Interop emit | ✗ Step 4 cancelled | 沒架構支持,做了也沒加速 |
| Gemini r3 §Q3 Option D BFS-block | ✓ Step 3.5 OptD | 機制對,但 owned set 不可擴展 |

### 2.4 沒做的(及為什麼)

✗ **Step 4 — LLVMSharp.Interop AOT emit** —— 沒做。Gemini r3 §Q1 直接給判定:「在阻斷 BFS 之前,Step 4 (LLVM) 的加速效益為零」。Option D 證實:即使阻斷 BFS,owned set ceiling 仍卡在 ~62,Step 4 不會給更多。

✗ **AOT compiler 路線**(Gemini r3 §Q5 提的 pivot)—— 沒做。要做需要新分支,大型重啟,放棄 S1 runtime 改純靜態編譯(MetalNES 路線)。**Decision: future work**,不在 math-algos scope。

---

## 3. 為什麼 wind-down 不 merge 回 main

1. **架構衝突**:main 是 GPU/oblivious/batch-IR;math-algos 是 CPU/event-driven/dispatcher。merge 等於把兩種互相排斥的 simulation runtime backend 塞進同個 namespace。
2. **沒 ROI**:dispatcher/partitioner 上的工程沒帶來 runtime speedup,merge 進去 main 就是 dead code。
3. **保存性更好**:留在 math-algos branch 完整成套(11 篇 doc + 工具 + CLI),將來如果要走 AOT 方向直接 cherry-pick partitioner + dispatcher。

---

## 4. 給 future cherry-pick 的 file 清單

如果將來開新 branch 做 AOT compiler 或別的 codegen 方向,值得帶走的:

```
src/AprVisual/Sim/WireCore.Partition.cs      ← partitioner 全套(Gemini「聖杯級」評價)
src/AprVisual/Sim/WireCore.Dispatcher.cs     ← bitmask polling dispatcher + Option D 機制
src/AprVisual/Sim/WireCore.Group.cs L85-95   ← Option D BFS-block 5 行修改
src/AprVisual/Native/AluBlock.cpp            ← 5-op 6502 ALU C++ reference
src/AprVisual/Native/AluBlockBindings.cs     ← P/Invoke binding template
src/AprVisual/Test/TestRunner.cs             ← --alu-bench / --dump-partition / --dump-block-id / --codegen-* flags
MD/impl/math-algos/00..11*.md                ← 全套設計 + 結果 + 失敗原因
tools/knowledgebase/message/2026052*.txt     ← 3 次 Gemini 顧問 Q&A 紀錄(r1/r2/r3)
```

---

## 5. 一句話收尾

> **math-algos branch 在 Phase 2.5 Step 3.5 撞到 S1 group-resolution 的架構天花板:單純 codegen runtime-accelerator 路徑在這個架構上不可行。Phase 1 + Phase 2 + Phase 2.5 Step 1-3 共同產出的工具鏈(partitioner、dispatcher framework、native block reference、Option D BFS-block 機制)留 branch 上不 merge,作為將來 AOT compiler 方向或其他 codegen 實驗的 frontend。**
