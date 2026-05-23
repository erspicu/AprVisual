# aot-codegen — Phase E-3 結果:settle 診斷揭示根本問題

> 任務 #81 (settle diagnostic + reset handling)。
> 接續 `10_phaseE2_runtime.md`(AotRuntime 99.89% match @ 1hc)。

---

## 0. 結論(關鍵診斷)

🟡 **加 per-iter NodeStates change counter 揭示根本問題**:

```
=== 1 hc / 10 hc / 100 hc 一致 ===
max settle iter   : 1
total node changes: 0  (0.0 per hc)        ← EvalAll completely no-op!
first-iter changes: 0  (0.0 per hc)
```

**S1 settled state 就是 AOT 的 fixed-point** —— EvalAll 跨所有 hc 跑了一萬+ 次,**從沒改過一個 NodeStates 值**。

Drift 全部來自 AOT pattern accuracy errors(16 個 mismatches 在 hc=1),不是因為 clock toggle 沒傳。

---

## 1. 為什麼 EvalAll 不改 NodeStates(理解)

從 S1 settled state 出發:
1. Toggle `clk`(0→1)
2. EvalAll 跑 5,210 delegates
3. 每個 delegate 讀 inputs(含新 `clk`)+ 寫 outputs

但 outputs 寫的值 **跟之前儲存的相同**:
- Latches 重讀 sources 得到相同值(因為 6502 在 idle steady state,latches 本來就跟 sources 一致)
- Inverters / NOR / mux_bus 重算邏輯,結果也相同
- Net effect:no-op writes,fixed-point trivially achieved

**要看到真實 propagation,需要 CPU 真的執行指令** —— 這需要 **memory handlers**(讀 ROM → 變 data bus → 變 IR/decode → 變 PC → 變 address bus → 讀更多 memory)。

沒 memory handlers,CPU 就是「powered but idle」狀態,所有 latches 處於跟 sources 一致的 equilibrium。

---

## 2. 16-node mismatch 真實成因

不是「propagation 不夠」,而是 **AOT pattern model 在某些 node 上算錯**:

| nn | name | type | AOT | S1 |
|---|---|---|---|---|
| 148 | ppu.chroma_ring4 | latch | 0 | 1 |
| 18 (10hc+) | BA8 | chipboard latch | 1 | 0 |

`ppu.chroma_ring*`(PPU chroma generation)跟 `BA8`(cartridge address bus latch)都是 latches,被我 model 成 "if gate active → src, else hold"。但**真實 6502 latches 的 hold-value 行為**:
- depends on phi(write-phase vs hold-phase)
- depends on capacitance(dynamic node 可能 leak)
- depends on pulldown timing precisely

我的 latch pattern 用 `NodeStates[outputId]` 當 hold-value(讀自己的當前值),理論上對。但:
- 如果 latch 是 master-slave 對(2 個 c1c2s),寫入時機 vs S1 不同步
- 如果 latch 跨 clock domain,phi gate 真實 active window 跟我的 model 不一致

這些是 **Phase C-7 phi-transient accuracy** 該解的問題。

---

## 3. 加的工程:reset handling

```csharp
public int ResetNodeId = -1;
public int ResetHoldHc = 192;   // hold /res low for first 192 hc (matches S1's power-on reset)
public int HcSinceStart;

public void Step() {
    NodeStates[ClockNodeId] ^= 1;
    if (ResetNodeId >= 0 && HcSinceStart < ResetHoldHc) NodeStates[ResetNodeId] = 0;
    // ... settle ...
    HcSinceStart++;
}
```

加 reset 對結果 **沒影響**(因為從 S1 settled state 出發,reset 已 deasserted)。對 Phase E-4 從零開機跑 ROM 有用。

---

## 4. Phase E-4+ 路線重新確認

Phase E-3 確認:**單純 clock toggle 不會驅動 AotRuntime「跑程式」**。要 propagation 必須:

| 必需 | 為什麼 |
|---|---|
| ⭐ Memory handlers | CPU 讀 ROM 才會 fetch 指令、改 PC、推 state |
| Address bus → memory map | AB 變化要 trigger RAM/ROM read,把 byte 寫到 DB |
| Data bus → register | DB 變化 → IR 載入 → decode → control signals |
| Reset deassertion | 從 0 開機需要 reset 序列(我已加,從 S1 settled 出發無關)|
| Clock 真實 toggling | 加了,目前唯一變動是 clk node 本身 |

**E-4(memory handlers)是 unblocker**。沒它 AotRuntime 完全靜止。

---

## 5. 工程量重新估算

Memory handlers 在 S1 有現成 implementation(`WireCore.Handlers.cs`)— 但 hooked into S1's callback mechanism。要 port 到 AotRuntime 需要:
1. Detect address bus change(15-bit AB)
2. Decode address(RAM / ROM / IO regs)
3. Read/Write bytes to/from data bus
4. Trigger memory-mapped IO 行為(PPU/APU registers)

工程量:**中等-大**。可能需要拆成幾個 sub-tasks:
- E-4a: cart ROM read(最簡單,純 lookup)
- E-4b: zero-page + stack RAM (2KB internal)
- E-4c: PPU registers $2000-$2007
- E-4d: APU registers $4000-$4017

每個 sub-task 是中等 1-2 hr 工程。

---

## 6. 一句話

> **Phase E-3 加 per-iter diagnostic 揭示根本:S1 settled state 就是 AOT EvalAll 的 fixed-point,clock toggle 在 idle CPU 下不會驅動 propagation。要看 AotRuntime 真實「跑程式」必須加 memory handlers(E-4)讓 CPU fetch/decode 循環啟動。16-node mismatch 是 pattern accuracy 問題(latch phi 模型不精確),會在 E-4 之後 cascade 變大。**
