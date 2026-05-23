# aot-codegen — Phase E-4a 結果:ROM handler 上線但無 fire,確認 deeper 工程量

> 任務 #82 (cart ROM read handler)。
> 接續 `11_phaseE3_diagnostic.md`(S1 settled = AOT fixed-point,需 memory handlers)。
> 性質:**重要 architectural realization** — Route B 工程量比預期大。

---

## 0. 結論

🟡 **Phase E-4a 上線後仍 0 ROM reads,因為 AOT 完全沒推動 CPU activity**:

```
=== AotRuntime + ROM handler wired ===
wired cart ROM (32 KB), AB ids: [43,37,38,...]
max settle iter   : 1
total node changes: 0 per hc
ROM reads issued  : 0          ← handler 上線但從沒 fire
matches 99.89% → 89% drift (跟 E-2 完全相同)
```

ROM handler 機制工程上對(讀 AB → lookup → 寫 DB → re-settle),但 **AB 永遠 < $8000**,所以從沒進到 ROM range。

---

## 1. 為什麼 AB 永遠 < $8000

AotRuntime 啟動流程:
1. S1 LoadSystem → S1 跑完 reset 序列 → settle 完狀態 snapshot 進 AotRuntime
2. 那個 settled state 裡,AB 是當下值(例如 $000E,可能是 stack pop / RAM access 中)
3. AotRuntime.Step:toggle clk,EvalAll(沒推任何東西),AB 不變

**根本原因**:`clk`(node id 3)沒 gate 任何 emitted delegate。CPU 的 clock 走 internal phi1/phi2 divider chain(複雜 multi-pass structure),沒被 simple patterns match。clk toggle 不導致 phi1/phi2 changes,phi1/phi2 不變就 latches 不 fire,latches 不 fire 就 CPU 不執行任何 cycle。

### 證據

`total node changes per hc = 0` 跨所有 hc 數量 —— EvalAll **絕對沒讓任何 NodeStates 值改變**。

clk 已經 toggle 了(NodeStates[3] 確實 ^= 1),但這個改變沒透過 emit 出來的 logic 傳到任何下游。

---

## 2. 真實架構工程量

要讓 AotRuntime 真實「跑」CPU,需要:

| 缺什麼 | 難度 | 為什麼 |
|---|---|---|
| **Clock divider model** | 中等 | clk → phi1/phi2 的 2x divider 需要明確 model |
| **Phi-aware latch patterns** | 中等-大 | 現在 latch model 忽略 phi,只看 immediate gate;真實 6502 latches 都是 phi-gated |
| **完整 memory map** | 中等 | $0000-$1FFF RAM + $2000-$2007 PPU + $4000-$4017 APU + $8000-$FFFF ROM |
| **PPU 完整 model** | 大 | PPU 自己跑 pixel clock,完全獨立模擬 |
| **APU 完整 model** | 中等 | APU 5 個 channels + DMC + mixer |
| **跨 clock-domain edge timing** | 大 | CPU 1.79MHz / PPU 5.37MHz / 同步點 |
| **External signals** | 小 | /RES, BA0, M2, etc. |

合計:**單純整 CPU runtime 從零開始需 2-4 週**,類似從零寫 MetalNES。

---

## 3. Phase D-2 vs E-2 之間的關鍵差距

```
Phase D-2 (Roslyn compile-load-verify):
  ✓ 100% byte-equal AOT == S1 (for picked node subset)
  ✓ AOT correctness 驗證了
  ⚠ 是 "side-shadow" 模式:S1 推 state,AOT 觀察 + 比對

Phase E-2/E-3/E-4a (AotRuntime 自跑):
  ✗ 自跑不會推 state(EvalAll 是 fixed-point)
  ⚠ 是 "primary engine" 模式:需要 AOT 自己驅動 CPU activity
  
差距:S1 的 propagation 機制本身就是 "BFS group walk + memory handlers + clock handler",
不是「靜態 evaluate Boolean expressions」。AOT delegate 只 cover 後者。
```

---

## 4. 重新評估 Route B 可行性

Gemini r3 §Q5 原本說:「dispatcher 這麼快(4.6% overhead),為什麼還要留著 S1?」這 implicit 假設「AOT delegates + 簡單 dispatcher = simulation engine」。

實際撞牆證明:**「Boolean expressions of node values」≠「functional simulator」**。Functional simulator 需要 active state propagation,而 active state propagation 在 S1 是由 group walk + handlers 提供的。AOT delegates 只是「在 NodeStates 已對的情況下重 compute 並驗證」,不能 originate state changes。

**Gemini r3 §Q5 的 vision 比 Gemini 自己以為的工程量大。** 

---

## 5. 三條路線重新選擇

### 路線 A1 — 接受 D-3 模式(shadow + overwrite)+ 用在 batch / oracle
- ✓ 工程零成本(目前已 working)
- ✓ Correctness 100%
- ❌ 沒 speedup
- 用途:multi-instance batch / debug oracle / regression test

### 路線 B1 — 走完整 Route B (2-4 週工程)
- ⏭ Phase E-3 phi-aware latch model
- ⏭ Phase E-4 完整 memory map
- ⏭ Phase E-5 PPU/APU 整 model
- ⏭ Phase E-6 跑小 ROM verify
- ⏭ Phase E-7 跑完整 ROM
- ⏭ Phase E-8 perf baseline
- 風險:可能還會撞別的牆(e.g., PPU 渲染 timing)

### 路線 C1 — Branch wind-down(跟 math-algos 一樣)
- 已產出完整 Phase A → D-2 工具鏈
- 已驗證 99.74% AOT accuracy + 100% Roslyn load
- 已揭露架構限制(D-4 hybrid 撞牆 + E-2/3/4a 「自跑」也撞牆)
- 留 branch 上給將來 cherry-pick / 完整 MetalNES port

### 路線 D1 — 找別的方向用 AOT 工具
- 例如:用 AOT delegate 加速 S1 的 specific hot 操作
- 或:用 AOT 做 multi-instance parallel sim(每 instance 一個 AOT engine,並行跑)
- 或:用 AOT delegates 當 chip 行為的"reference",catch S1 bugs

---

## 6. 一句話

> **Phase E-4a wire 完 ROM read handler 但完全沒 fire —— 因為 AOT EvalAll 從來沒讓任何 NodeStates 值改變(clk 不 gate 任何 emitted delegate);CPU 完全不執行 cycle。確認 Gemini r3 §Q5 的「100% AOT」vision 工程量比想像大很多(2-4 週,類似從零寫 MetalNES,需 phi-aware latches + 完整 memory map + PPU/APU model + cross-clock timing)。要 user 重新選方向:接受 D-3 shadow + 別的用途 / 走完整 B 大型工程 / wind-down branch / 找別的 angle。**
