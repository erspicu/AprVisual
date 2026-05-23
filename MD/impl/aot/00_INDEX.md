# aot-codegen branch — INDEX & 成果總覽

> **Branch**:`aot-codegen`(2026-05-23 從 math-algos wind-down 後依 Gemini r3 §Q5 戰略分出)。
> **目標**:把 Visual6502 netlist 直接 AOT-compile 成純 C# 模擬引擎(MetalNES route);S1 變 Oracle 驗證。
> **狀態(本 INDEX 寫於 Phase C-5 完成後)**:**6 階段完成 5 階段**(Phase A 到 C-5),Phase D 整合演示待開工。

---

## 0. 一張圖看整 branch 進度

```
Gemini r3 §Q5 pipeline:
  ┌──────────────────────────────────────────────────────────┐
  │  Netlist (.js) ─→ Partitioner ─→ Macro-blocks Graph ─→    │
  │       (S1 loader)   (Step 3 ✓)        (Step 3 ✓)          │
  │                                                            │
  │       ┌──→ C#/C++ Code Generator (AOT) ──→ Pure Boolean   │
  │       │       (Phase A-C-5 ✓)                Engine        │
  │       │                                                    │
  │       └──→ S1 Oracle ──→ Verify ──────────→ (each phase ✓) │
  └──────────────────────────────────────────────────────────┘
       
                          完成度: 5/6 階段
```

實作對齊 Gemini 設計:**5 of 6 stages landed**。剩下 Phase D 整合演示。

---

## 1. 文件總表

| # | 文件 | 涵蓋階段 | 結果摘要 |
|---|---|---|---|
| 00 | **00_INDEX.md (本文)** | 整 branch 總覽 | 5/6 stages, 97.9% emittable / 99.75% accuracy |
| 00 | 00_design.md | 初始設計 (Phase A-F pipeline)| pipeline architecture + phase plan |
| 01 | 01_phaseAB_results.md | **Phase A + B** | hand-coded → auto-emit, IR inverter 0 mismatch |
| 02 | 02_phaseC_coverage.md | **Phase C-1** | NAND + coverage scanner, 84.5% |
| 03 | 03_phaseC2_muxbus.md | **Phase C-2** | mux_bus + batch verifier, 93.1% / 99.74% |
| 04 | 04_phaseC3_norpass.md | **Phase C-3** | norN+pass, 97.9% / 99.75%(設計教訓:不要 over-eager)|
| 05 | 05_phaseC5_blocklevel.md | **Phase C-5** | AotBlockBuilder + 真實 `.cs` 源碼生成 |

---

## 2. Phase 結果總表

### Phase A — hand-coded AOT vs S1
- 第一個 AOT block:`EvalIrInverter`(8 個 inverter, `notir[i] = NOT(ir[i])`)
- **驗證 200K hc:0 mismatch ✓**
- 證明 「AOT delegate vs S1 NodeStates byte-equal」 路徑可行
- Writeup:01

### Phase B — auto-emitter from netlist topology
- `AotEmitter.EmitForNode(outputId)` pattern-match transistor 拓樸 → 自動生 delegate
- 對 8 個 notir nodes:emitter 自動 discover 8 個 ir gate IDs **完全 match hand-coded**
- 驗證 200K hc:**0 mismatch ✓**
- Writeup:01

### Phase C-1 — NAND + coverage scanner
- 加 `nand` pattern(2 series pull-downs)
- 全 netlist coverage scanner `--aot-coverage`
- 結果:5,402 / 14,727 (36.7%) 全 netlist;**84.5%** 排除 no-pullup 後
- Writeup:02

### Phase C-2 — mux_bus + batch verifier
- 加 `mux_bus` / `mux_bus+pulldown`(NMOS multi-driver wired-OR)
- 加 batch verifier `--aot-verify-all`,per-pattern accuracy
- 結果:5,955 / 14,727 (40.4%);**93.1% emittable**;**99.74% accuracy** over 178M samples
- 100% PERFECT patterns: `mux_bus+pulldown`(502 nodes)+ 所有 `nor2..16`
- Writeup:03

### Phase C-3 — norN+pass(generalised NOR with latch-write)
- 加 `norN+pass`(N=2..16,passToBus=1)
- **重要教訓**:第一版 passToBus≤2 太貪,把 100% PERFECT mux_bus 領域吃進來變壞 → revert narrow
- 結果:6,260 / 14,727 (42.5%);**97.9% emittable**;99.75% accuracy
- 24 / 31 patterns byte-equal PERFECT
- Writeup:04

### Phase C-5 — block-level emit + real `.cs` source
- `AotBlockBuilder.Build(Partition.Block)` → per-block runtime delegates
- `AotBlockBuilder.EmitSource(...)` → **真實 5KB `.cs` 源碼**
- Block #19/#22 driven outputs:75-80% emittable,**0 mismatch / 1M+ samples**
- Demo file:Block_19_cpu_tri_p8.cs(5,402 bytes 純 boolean expressions)
- Writeup:05

---

## 3. 整體 Pattern Coverage

```
14,727 live nodes scanned
├─ 8,331 no-pullup (external inputs + dynamic latch storage; not emittable)
└─ 6,396 emittable subset
   ├─ 6,260 (97.9%) supported by AotEmitter ✓
   │  ├─ inverter / inverter+latch-write : 3,784 (60.4%)
   │  ├─ nor2..nor16                      : 1,608+ (25.7%)
   │  ├─ mux_bus / mux_bus+pulldown        :   553 (8.8%)
   │  ├─ norN+pass (N=2..16)               :   381 (6.1%)
   │  └─ nand                              :   105 (1.7%)
   └─ 136 (2.1%) unsupported (AOI/OAI/复杂 pass-through;留 S1 fallback)
```

### Pattern accuracy vs S1(187.8M samples cross-pattern)

```
✓ 100% PERFECT (24 patterns):
   all nor2..nor16 (1,608+ nodes), mux_bus+pulldown (502), most norN+pass

· Sub-1% mismatch (phi-transient):
   inverter           : 0.538%   (phi 寫入瞬間)
   inverter+latch-write: 0.250%
   nand               : 0.185%
   mux_bus (no pd)    : 0.968%
   nor2..6+pass       : 0.22-5.58%

GRAND TOTAL: 99.7479% byte-equal
```

---

## 4. CLI 完整清單(本 branch 加的)

```
AOT 開發核心:
  --aot-coverage <rom>                    全 netlist scan + pattern histogram
  --aot-verify-all <rom> [--bench-hc N]   batch verify all emittable nodes per-pattern
  --aot-verify-block <rom> <id>           verify 一個 Partition.Block 的 AOT 跟 S1
  --aot-emit-block <rom> <id> <out.cs>    生成真實 `.cs` 源碼 + console preview

Per-block hand-coded validators (Phase A):
  --aot-verify-tilemux <rom>              PPU tile_h MUX (8-to-1)
  --aot-verify-ir-inv  <rom>              6502 IR inverter ladder
  --aot-emit-verify-ir <rom>              AotEmitter 自動 emit IR ladder 並驗證
```

(承接自 math-algos 的工具:`--dump-partition`、`--dump-block-id`、`--codegen-dispatcher` 等仍可用)

---

## 5. 可重用 Assets(將來 Phase D 用 + 給未來 branch cherry-pick)

```
src/AprVisual/Codegen/
  AotEmitter.cs           — pattern detection + delegate compilation
                            (inverter, nor2-16, nand, mux_bus 各變體)
                            + ScanCoverage() 全 netlist 統計
  AotBlockBuilder.cs      — block-level delegates + EmitSource(.cs 生成)
  AotBlocks.cs            — hand-coded reference blocks (IR inverter, PPU tile MUX)
  AotBlockBindings.cs     — Phase A bindings (legacy from Phase A demo)
  AotVerifier.cs          — 5 種 verifier:tile_mux / ir_inv / emit_ir / verify_all
                            / verify_block / emit_block / run_coverage_scan

src/AprVisual/Sim/        (繼承自 math-algos)
  WireCore.Partition.cs   — auto-partitioner(Gemini r3 §Q5 稱「聖杯級」工具)
  WireCore.Dispatcher.cs  — bitmask polling dispatcher(Phase D-3 整合用)
  
MD/impl/aot/
  00_INDEX.md (本文) / 00_design.md / 01-05_phase*_results.md
```

---

## 6. 為什麼這條路成功(對照 math-algos 撞牆原因)

| math-algos Phase 2.5 撞牆點 | aot-codegen 解 |
|---|---|
| S1 BFS 從別處 traverse owned region | **沒有 BFS 概念** —— AOT 直接純 boolean expressions,完全替代 S1 |
| CodegenOwned 只 skip 入口 | 沒有 "ownership";AOT 是側觀計算然後 Oracle 比對 |
| Owned 不能包 anonymous mid | **不 own mid** —— 只 emit 有 pull-up 的 boundary outputs,內部 mid 由 S1 或 latch pattern(future)處理 |
| Dispatcher overhead 蓋過 saving | **沒 dispatcher overhead**(每個 delegate 是 inline boolean op);正式 AOT 階段就 directly compiled into static class |

關鍵 insight:AOT 是「**先做出對的 codegen 結果**」,**S1 變 Oracle 驗證**;不是試圖 hijack S1 的 group walk。

---

## 7. Phase D 路線(下一步)

| Phase | 目標 | 工程量 |
|---|---|---|
| **D-1** | Mass emit:跑全 30 個 codegen-candidate blocks,合成 1 個 master .cs file | 小 |
| **D-2** | Roslyn runtime compile + load AotEngine | 中等 |
| **D-3** | 整合 dispatcher(math-algos 工具),event-driven 觸發 emitted blocks | 中等 |
| D-4 | Run ROM under AotEngine,trace ≡ S1 over 100K hc | 中等 |
| D-5 | Performance baseline (AotEngine hc/s vs S1 hc/s) | 小 |
| D-6 (stretch) | S1 fallback path for the 136 unsupported nodes | 中 |

**D-1 + D-2 + D-3 = 第一個 "AOT engine running NES" demo**。預估 1 週工程量。

---

## 8. 一句話收尾

> **`aot-codegen` branch 6 階段已完成 5(Phase A + B + C-1 + C-2 + C-3 + C-5):AotEmitter 從 netlist 自動 pattern-match → 97.9% emittable coverage / 99.75% byte-equal vs S1;AotBlockBuilder 把 Partition.Block 轉成 runtime delegates + 生成真實 `.cs` 源碼。Gemini r3 §Q5 pipeline 5/6 完成。下一步 Phase D 整合演示 AOT engine 跑 NES ROM。**
