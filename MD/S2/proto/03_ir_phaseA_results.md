# S2 IR Phase-A 結果(2026-06-01)

> 目標(使用者):一版 C# S2 IR,**正確 + 速度不比 S1 慢**,當階段性產出。
> 結果:**IR 正確達成(bit-exact),但 −2.5% 變慢 → 「不變慢」未達成**。與 Gemini + math-algos 預測完全一致。

## 做了什麼

CPU-first、event-driven、hybrid IR(`src/AprVisual.S2/Sim/WireCore.Ir.cs`,opt-in `--ir`,預設關):
- 抽取最乾淨的 pure-logic 子集(static class-1、pull-up、只 GND 下拉、無 PWR/normal channel、無特殊 flag)→
  每節點一張 **truth-table LUT**(`NOT(any GND-gate ON)`),建構期從管理圖抽(與 S2-A 內聯佈局無關)。
- RecalcNode 加 `cls==IrCls` dispatch → `EvalIr`(讀 gate states、pack index、查表)。
- 覆蓋:**3,390 節點(23% live),45,575 LUT bytes**。

## 驗證(整機 full_palette 300k)

- **正確性:checksum `0x794A43ABDF169ADA` —— bit-exact ✓**(IR 節點與 S1 逐位元組相同;其餘走 S1)。
- **效能:interleaved-paired 10 輪(同一 binary,有 `--ir` vs 無),`--ir` 中位 −2.54%、mean −2.42%、0/9 勝。** → **比 S1 慢。**

## 為何慢(命中 Gemini 預測)

被 IR 化的是 **class-1 節點 —— S1 的 RecalcNodeFast 本來就 O(1) 極快**(S2-A 內聯 + OR-all 掃描)。
`EvalIr`(LUT 索引 pack 迴圈 + IrGateList chase + 查表)**比 RecalcNodeFast 還重** → 對這些易節點淨虧。
Gemini 原話:「per-node IR interpreter 對 80K S1 是 dead on arrival;S1 已逼近指令地板,dispatch 開銷 > S1 的 payload」。
而 S1 做得慢的(熱匯流排 cpu.db/ab 多節點群)抗結構抽取(math-algos 已證),interpreter 抓不到。

→ **interpreter 的天花板在 80K 下是負的**(舊 41K 時是 break-even;S1 翻倍後,固定 dispatch 開銷的相對佔比把它推負)。

## 結論 / 下一步

- **「不變慢」無法用 interpreter 達成**(實測 −2.5%,且與 Gemini/math-algos 一致)。唯一 ≥ S1 的路是
  **Phase-B macro-block codegen**(把多節點鏈 collapse 成單一編譯 C# 方法、縮 D + 消 queue traffic;
  熱 bus 用 unrolled-BFS;先手寫一個 block 驗 ≥ S1)。見 `design/04`。
- Phase-B 是大型、correctness-critical 工程,**不適合無人值守一次性硬趕**(風險:半成品/壞碼)。
- **保留**:本 Phase-A IR(opt-in `--ir`,預設關 → 預設引擎不受影響、仍 ~80K bit-exact)是正確的 IR
  基礎設施(Expr/LUT/dispatch/抽取),Phase-B codegen 直接複用。

## 給後續(有人值守時)

1. 挑一個最熱、形狀規則的子系統(PPU hpos/vpos 計數器 / CPU ALU)。
2. 手寫其 oblivious eval(bit-exact,必要處用 256-LUT),wire 進 queue(enqueue block-id 而非 node)。
3. interleaved-paired vs S1:≥ S1 → codegen 路徑成立 → 自動化(Roslyn/Reflection.Emit)。
