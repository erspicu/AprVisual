# AOT compiler — 00 設計文件

> **Branch**:`aot-codegen`(2026-05-23 從 math-algos wind-down 後分出)。
> **前情**:見 `MD/impl/math-algos/00_INDEX.md` —— Phase 2.5 codegen-as-runtime-accelerator 路徑撞牆,Gemini r3 §Q5 戰略 pivot 推薦走 AOT compiler 路線(放棄 S1 runtime,純靜態編譯)。
> **參照**:`ref/metalnes-main/`(MetalNES 是這條路的成功先例);Visual6502 chipsim.js 是 S1 來源。

---

## 0. 目標 + 範圍

### 目標
**輸入**:Visual6502/MetalNES style 的 netlist(`segdefs` / `transdefs` / `nodenames`)+ partitioner 切出的 macro-block。
**輸出**:純靜態 C# 模擬引擎,**沒有 BFS、沒有 group-resolution、純 boolean equations + bitwise ops**。
**對標**:MetalNES 是 C++ pre-compiled simulator 的成功實例 —— 跟 Visual6502 同 netlist 但每 cycle 走預編譯的 logic equations,速度遠快於 runtime BFS。

### Scope (initial MVP)
- ✅ **單 block AOT**:取 partitioner 找出的一個小 block(24-node APU triangle),emit C# 函數,驗證輸出 ≡ S1。
- ✅ **incremental**:一個 block work 完再做下一個。每個 block 都 S1-Oracle 驗證 byte-equal。
- ✅ **輸出 C#**(跟現有 codebase 同 runtime,直接 link 進 AprVisual.exe)。
- ✅ **partitioner reuse**:用 math-algos `WireCore.Partition.cs` 找 block;選 named-output、size 24-200 的開始。

### Out of scope(initial)
- ❌ **整 chip AOT**:14.7k node 一次 codegen。Main 已驗證 oblivious batch 3-6× SLOWER。Gemini r3 §Q3 列為 dead-end (a)。
- ❌ **multi-instance / GPU**:後話。
- ❌ **效能優化**:correctness first;能跑對再談快。
- ❌ **LLVM emit**:後階段(若 C# 路證實 OK,可考慮加 LLVMSharp.Interop 加速 hot block)。

---

## 1. Pipeline 架構

```
                  ┌──────────────────────────────────────────────────┐
                  │                  Netlist (.js)                    │
                  │   segdefs / transdefs / nodenames                 │
                  │   (Visual6502 / MetalNES format)                  │
                  └─────────────────────────┬────────────────────────┘
                                             │
                                             │ WireCore.Parse / Module / Lower
                                             ▼
                  ┌──────────────────────────────────────────────────┐
                  │  In-memory netlist (Nodes[], Transistors[],       │
                  │  ResolveNodes, etc.) — same as S1's loader        │
                  └─────────────────────────┬────────────────────────┘
                                             │
                                ┌────────────┼────────────┐
                                │            │            │
                                ▼            ▼            ▼
                       ┌────────────┐ ┌───────────┐ ┌─────────────┐
                       │ S1 runtime │ │ Partition │ │ AotEmitter  │
                       │ (Oracle)   │ │ (Step 3)  │ │  (NEW)      │
                       └─────┬──────┘ └─────┬─────┘ └──────┬──────┘
                             │              │              │
                             │              │ Block[] ─────┤
                             │              │              ▼
                             │              │      ┌───────────────┐
                             │              │      │ Generated C#  │
                             │              │      │ per-block     │
                             │              │      │ Eval funcs    │
                             │              │      └───────┬───────┘
                             │              │              │ Roslyn / csc
                             │              │              ▼
                             │              │      ┌───────────────┐
                             │              │      │ Compiled .dll │
                             │              │      │ AotEngine     │
                             │              │      └───────┬───────┘
                             │              │              │
                             └──────────────┼──────────────┘
                                            ▼
                             ┌─────────────────────────────────┐
                             │  Verification harness           │
                             │  - run both S1 + AotEngine on   │
                             │    same ROM                     │
                             │  - per-hc NodeStates diff       │
                             │  - report divergences           │
                             └─────────────────────────────────┘
```

---

## 2. AotEmitter — 輸出 contract

### 每個 block 編成什麼樣的 C#

```csharp
// Generated for block #19  (cpu.tri_p8, 24 internal nodes, 62 inputs, 20 driven outputs)
public static class Block_19_cpu_tri_p8
{
    // Inputs: boundary nodes this block reads. IDs resolved at compile time.
    private const int INPUT_cpu_sq1__p0 = 10543;
    private const int INPUT_cpu_sq1__p1 = 10573;
    // ... 62 input IDs

    // Outputs: boundary nodes this block drives.
    private const int OUTPUT_xxx_0 = 12345;
    // ... 20 output IDs

    // Internal nodes (24): for codegen we can hold their values in local variables.
    // No external observer reads them.

    // The eval function. Reads inputs from nodeStates, computes outputs, writes back.
    public static void Eval(byte* nodeStates)
    {
        // Read inputs into locals
        byte in_sq1_p0 = nodeStates[INPUT_cpu_sq1__p0];
        byte in_sq1_p1 = nodeStates[INPUT_cpu_sq1__p1];
        // ...

        // Compute internal nodes (purely combinational where possible)
        byte mid_0 = (byte)(in_xxx & in_yyy);   // sample: AND
        byte mid_1 = (byte)(~mid_0 | in_zzz);   // sample: NOR  
        // ...

        // Compute + write outputs
        nodeStates[OUTPUT_xxx_0] = (byte)(mid_5 ^ mid_7);
        // ...
    }
}
```

### Block dispatcher(取代 S1 runtime)

```csharp
// Generated top-level. Replaces S1's ProcessQueue / RecalcNode.
public static class AotEngine
{
    public static void StepHalfCycle(byte* nodeStates)
    {
        // Toggle clock, fire memory handlers, etc. (reuse S1's handlers)
        // ...
        // Drain dirty blocks until quiescent (similar to math-algos's
        // Dispatcher framework, but with the FULL set of generated blocks)
        Block_19_cpu_tri_p8.Eval(nodeStates);
        Block_16_ppu_finex1.Eval(nodeStates);
        // ... in some order
    }
}
```

---

## 3. 核心難題 + 設計選擇

### 3.1 Pass-transistor 雙向流向

NMOS pass transistor 在 group walk 時是 bidirectional —— c1, c2 雙端互相影響(gate on)。AOT 編譯時要決定方向(`output = input` 還是 `input = output`)。

**解法**:依 partitioner 的 boundary inference(`DrivenOutputs` 表示「block computes this boundary」,`BoundaryInputs` 表示「block reads this boundary」)。pass-transistor 內部 if 連接兩個 internal nodes,內部本身可保持 symmetric(用 mid 變數);只在 boundary 處決定方向。

### 3.2 多 driver 共享 bus

NMOS wired-OR:多個 pull-down 競爭同 bus,任一 ON → bus = 0。AOT 編成:
```csharp
output = (drv0_off & drv1_off & ... & drvN_off) ? 1 : 0;
// 或等價的 boolean: output = ~(drv0 | drv1 | ... | drvN)
```

### 3.3 Latch / 動態節點

無 pull-up 的 dynamic node 在 floating 時 hold previous value(parasitic capacitance abstraction)。AOT 需保留 state-prev:
```csharp
byte latch_X = (gate_phi ? input : nodeStates[LATCH_X_ID]);
nodeStates[LATCH_X_ID] = latch_X;
```

state-prev/state-next pattern 是 Gemini r2 §2.3 提到的「latch 透過 macro-block context struct 處理」的具體實現。

### 3.4 Capacitance / "largest connections wins"

純 floating group(無 pull-up、無強驅動)→ 最大 connection 的節點的 state 勝出。
**AOT 處理**:極少數 group 純 floating;先不處理,觀察 S1-Oracle diff 是否有此 case;若有再加 logic。

### 3.5 Block 間 ordering

S1 是 event-driven,dirty queue 處理。AOT 要靜態 order:
- 用 Phase 1 的 levelize 工具(`--dump-levels`)算出 topological order(忽略 SCC 內部 ordering)。
- 或保留 event-driven dispatcher(從 math-algos Step 2 cherry-pick)+ AOT 各 block 為 eval function。

**初期選 option 2**:dispatcher reuse,只是 ALU/dispatcher 改成靜態查表 + AOT eval。

---

## 4. Verification harness (Oracle 比對)

```
Test ROM
  ├─→ S1 runtime ─── per-hc NodeStates snapshot ────┐
  │                                                  │
  └─→ AotEngine ─── per-hc NodeStates snapshot ─── DIFF
                                                     │
                                                     ▼
                                            Pass / report divergence node IDs
```

實作:
- `--aot-verify <ROM>` CLI mode
- 跑 N 個 half-cycle,每 hc 後比對 S1.NodeStates vs AotEngine.NodeStates
- 任何節點 mismatch → 記錄 (hc, node id, name, S1 val, AOT val)
- 報告 first 10 + summary

---

## 5. Phase 計劃

| Phase | 目標 | Deliverable |
|---|---|---|
| **A** | 單一小 block 端到端 | hand-coded `Block_19_cpu_tri_p8.cs` + verification harness;確認 byte-equal S1 |
| **B** | AotEmitter 自動產生同樣 block | `AotEmitter.Emit(Block) → C# source`;auto-generated 跟 hand-coded 一致 |
| **C** | 多 block + 整合 dispatcher | 10-20 blocks 都 AOT,dispatcher 整合;驗證 trace ≡ S1 |
| **D** | 50+ blocks(approaching MetalNES coverage) | 60% chip AOT, 40% S1 fallback |
| **E** | 100% chip AOT | 完整 AotEngine 取代 S1 runtime |
| **F** | 效能優化(若 D/E 結果 OK) | profile + LLVM emit hot block / batch optimization |

每 phase 都有 verifiable deliverable + go/no-go criterion。

**MVP(本文 scope)= Phase A + Phase B**:單 block 端到端 + emitter 第一版。

---

## 6. 為什麼這條路可能成功(對照 Step 3.5 失敗原因)

| Step 3.5 失敗點 | AOT 解 |
|---|---|
| S1 BFS 從別處 traverse owned region | **沒有 S1 BFS** —— AOT 完全替代,整個 chip 走預編譯函數 |
| CodegenOwned 只 skip 入口 | 沒有 "ownership"概念;直接 evaluate |
| Owned set 受限於 named node | **不需要 own**;codegen 從 transistor 結構直接 derive |
| Dispatcher overhead 蓋過 saving | 沒有 hybrid 模式;純 AOT |

Gemini r3 §Q5 預測:「math-algos 的 partitioner + dispatcher framework 留下來作為 AOT compiler 的 frontend」。本 branch 即實踐這個建議。

**也可能失敗的點**(尚待驗證):
- Capacitance / float-only group 在 AOT 不好模擬 → 可能有 trace diff
- Pass-transistor bidirectional 流向決定錯誤 → 邏輯錯
- 整 chip AOT 出來的代碼可能太大 → I-cache miss(主要主因)
- ROM-specific edge case(e.g., open-bus PPU 行為)

每個 phase 都有 verify 步驟,fail-fast。

---

## 7. 立即下一步

1. ✅ branch `aot-codegen` from math-algos — done
2. ✅ 本設計文件 — this file
3. 跑 `--dump-block-id 19/20/21` 看 APU triangle 三個 block 內部,選最簡單作為 first AOT target
4. hand-code 該 block 的 C# eval function(基於 transistor inspection)
5. 寫 `--aot-verify` harness
6. 跑 verification,fix correctness bugs
7. 寫 AotEmitter 第一版,讓它輸出同 shape

---

## 8. 一句話

> **`math-algos` Phase 2.5 證明 codegen-as-runtime-accelerator 因 S1 BFS 撞牆;`aot-codegen` 走 Gemini r3 §Q5 戰略 pivot:partitioner 為前端,直接生純 C# 模擬引擎,放棄 S1 runtime,以 S1 作 Oracle 驗證。MVP scope = 單一 24-node APU triangle block 端到端 + emitter 第一版。**
