# aot-codegen — Phase A + B 結果

> 任務追蹤:#63 (design doc) → #64 (target block 選擇) → #65 (hand-code 目標 shape) → #66 (S1-Oracle harness) → #67 (AotEmitter auto-generation)。
> 規劃文件:`00_design.md`(Phase A + B 是 MVP scope)。
> 性質:**首個 AOT 端到端驗證成功**(both hand-coded + auto-emitted)。

---

## 0. 結論 (TL;DR)

🟢 **AOT codegen 路徑第一個技術 milestone 達成**:

| 階段 | 結果 |
|---|---|
| **Phase A** — hand-coded AOT 對 S1 | ✅ 0/200,000 mismatch on `notir = NOT(ir)` |
| **Phase B** — AotEmitter 自動 generate | ✅ Emitter 正確找出 inputs + emit code + 0/200,000 mismatch |
| **Pattern 對齊** | ✅ Emitter discovered inputs **MATCH** hand-coded gate IDs(8/8) |
| **IR 真實有 exercise** | ✅ 2,278 / 200,000 hc(1.14%)IR bit change → 測試非 trivial |

驗證流程:S1 跑 ROM,每 half-cycle settle 後比對:
- hand-coded `EvalIrInverter`(8 inverters) ↔ S1 `notir[0..7]`
- auto-emitted `AotEmitter.EmitForNode(notir[i])` 生成的 delegate ↔ S1 `notir[0..7]`

兩者都 0 diff,證明 **AOT 從 netlist 拓樸自動推導 + 編譯 + runtime eval 整條鏈成立**。

---

## 1. 工程內容

### 1.1 第一個 hand-coded AOT block:6502 IR inverter ladder

```csharp
// src/AprVisual/Codegen/AotBlocks.cs
public static byte EvalIrInverter(byte* nodeStates, IrInverterIds ids) {
    byte predicted = 0;
    for (int i = 0; i < 8; i++)
        if (nodeStates[ids.Ir[i]] == 0) predicted |= (byte)(1 << i);
    return predicted;
}
```

選 IR inverter 因為:
- IR 每 6502 指令 fetch 變動(~1 次/24 master hc)→ inputs reliably change
- 8 個獨立 inverter,清楚 pattern
- 純 combinational(雖然 latch chain 但 settle 後 invariant 成立)

驗證:`--aot-verify-ir-inv <ROM>` flag,run 200K hc:
```
# ir-changing half-cycles: 2,278  (1.14%)  — proves ir IS being exercised
# mismatches: 0 / 200,000  (0.00%)
# VERDICT: AOT inverter eval IS the right model (zero diff vs S1)
```

### 1.2 第一個 AotEmitter

`src/AprVisual/Codegen/AotEmitter.cs` 的 `EmitForNode(outputId)` 演算法:

1. 取目標 node 的 `C1c2s`(它作為 channel-end 的 transistor 清單)
2. 分類每個 channel transistor:
   - `PullDown`:other_end = Ngnd → gate 高時拉 output 到 0
   - `PullToBus`:other_end = 另一非 supply → pass transistor(latch write 之類)
3. Pattern match:
   - **inverter**:1 pull-down + ≤ 1 pass-to-bus + node 有 pull-up → `output = NOT(gate)`
   - **nor**:多個 pull-down + 0 pass + 有 pull-up → `output = NOT(g0 | g1 | ...)`
   - 其他:回 `unsupported(...)` 待後續 phase 擴充

對 8 個 notir 全部 detect 為 `inverter+latch-write`(1 inverter pull-down + 1 phi-latch write transistor)。

```
notir0 (id 8953): pattern='inverter+latch-write', inputs=[9086], expr = (nodeStates[9086] == 0) ? (byte)1 : (byte)0
notir1 (id 9454): pattern='inverter+latch-write', inputs=[10367], ...
...(8 個都一樣 pattern)
emitter's discovered inputs MATCH the hand-coded gate IDs: True
```

每個 `EmitForNode` 回傳:
- `CSharpExpr`:可貼回 .cs 檔的 expression 字串
- `Compiled`:`Func<IntPtr, byte>` delegate(closure over discovered gate id)直接 runtime 跑
- `Pattern`:label,供 emit-summary 使用
- `InputIds`:這個 block 的 input dependency 清單

### 1.3 Verification harness — `--aot-emit-verify-ir`

執行 N 個 half-cycle,每 step 後:
```
emitterByte = OR( emitted[i].Compiled(nodeStates) << i ) for i in 0..7
handByte    = EvalIrInverter(nodeStates, ids)
actualByte  = ReadIrInverterActual(nodeStates, ids)  // bit i = NodeStates[notir[i]]
// 比對 emitterByte vs actualByte 跟 handByte vs actualByte
```

結果:
```
# samples: 200,000
# hand-coded eval mismatches : 0 / 200,000
# auto-emitted eval mismatches: 0 / 200,000
# VERDICT: AUTO-EMITTED AOT IS EQUIVALENT TO HAND-CODED AND TO S1 (0 diff). Phase B milestone achieved.
```

---

## 2. 為什麼這證明了 AOT 路線可行

| math-algos Phase 2.5 failure 點 | aot-codegen 解 |
|---|---|
| S1 BFS 從 owned region 別處 traverse | **不用 OWN** —— AOT 是 *側觀* eval,跟 S1 並存比對。等覆蓋率達 100% 再考慮取代 S1。 |
| Owned 不能包 anonymous mid | **不需要 own** —— AOT 從 transistor 結構 derive output value，內部 mid 完全不出現在 eval 函數裡 |
| Dispatcher overhead 蓋過 saving | **沒 dispatcher** —— AOT delegate 直接 IntPtr 接 NodeStates,inline 一個 byte load + branch |

AOT 路線本質上是 **「先做出對的編譯結果」**,然後才談取代 S1。先用 Oracle 驗 correctness,確定無 functional regression 後,再 phase E 替換 runtime。

---

## 3. 下一步 phase 路線(per 00_design.md)

| Phase | 目標 | Deliverable | Difficulty |
|---|---|---|---|
| ✅ A | 單 block hand-code + Oracle | IR inverter 0 mismatch | done |
| ✅ B | Emitter auto-gen | Same code via netlist analysis | done |
| ⏭ C | 多 block + dispatcher 整合 | 10-20 blocks AOT,trace ≡ S1 over real ROM | 中等 |
| ⏭ D | 50+ block coverage | 60% chip AOT, 40% S1 fallback | 中等-高 |
| ⏭ E | 100% chip AOT | 純 AOT engine 取代 S1 runtime | 高 |
| ⏭ F | 效能優化 | LLVM emit / batch / 各種 trick | 視 D/E 結果 |

### Phase C 需要的工程:
1. **更多 patterns** —— NAND ladder(2 series pull-downs)、多 pass-transistor pattern、latch storage、bus multi-driver wired-OR。
2. **Block-level emit** —— 從 `Partition.Block` 出發 emit 整 block 一個函數(含其 inputs/outputs 介面),而非 per-node 個別 delegate。
3. **Dispatcher 整合** —— 用 math-algos 的 Dispatcher.cs(bitmask polling),把每個 emitted block 註冊為一個 bit,event-driven 觸發 eval。
4. **覆蓋率工具** —— 算 "現在 emitter 能處理多少 % 的 nodes"。

Phase C 工程量約 1-2 週(看 pattern 數量擴充速度)。

---

## 4. CLI

```
--aot-verify-tilemux <rom>     [bench-hc N]  hand-coded PPU tile_h MUX vs S1 (Phase A try 1)
--aot-verify-ir-inv  <rom>     [bench-hc N]  hand-coded IR inverter ladder vs S1 (Phase A done)
--aot-emit-verify-ir <rom>     [bench-hc N]  AotEmitter auto-generated IR inverter vs S1 (Phase B done)
```

---

## 5. Repro

```
git: aot-codegen branch HEAD (commit after this writeup)
工具鏈: .NET 10 + Roslyn (C# compile-time emit; Linq Expressions 為 runtime delegate)
ROM: ref/metalnes-main/data/roms/nes-test-roms/instr_test-v3/rom_singles/01-implied.nes
命令:
  dotnet run --project src/AprVisual -c Release -- --aot-emit-verify-ir <ROM> --bench-hc 200000 --system-def-dir <SDF>
```

預期輸出:
```
# auto-emitted eval mismatches: 0 / 200,000
# VERDICT: AUTO-EMITTED AOT IS EQUIVALENT TO HAND-CODED AND TO S1 (0 diff). Phase B milestone achieved.
```

---

## 6. 一句話

> **AOT compiler 從 netlist 拓樸自動 emit C# eval code + runtime 接 delegate + 對 S1 byte-equal,Phase A (hand-code) 跟 Phase B (auto-emit) 兩個 milestone 在 IR inverter ladder 上達成。下一步 Phase C 擴 pattern set + integrate dispatcher + 多 block 覆蓋。**
