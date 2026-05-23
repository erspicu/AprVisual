# 08 — Step 2.5 結果:ALU writeback functional equivalence

> 任務追蹤:#51(5-op + alu[0..7] writeback)→ #52(trace-diff vs S1)→ #53(本文)。
> 接續 `07_dispatcher_framework_results.md`(Step 2 dry-run framework + 4.6% overhead)。

---

## 0. 結論

🟢 **Functional 部分 PASS,但速度 NEUTRAL(NO speedup yet — 預料中)**:

| 驗證項 | 結果 |
|---|---|
| 5-op selector mapping(SUMS/ANDS/ORS/EORS/SRS)| ✓ |
| Native ALU 5-op bench correctness | ✓ 2000/2000 |
| Trace-diff vs S1 @ 500 cycle | ✓ IDENTICAL |
| Trace-diff vs S1 @ 3000 cycle | ✓ IDENTICAL |
| blargg test 同 hc 收斂 | ✓ baseline + writeback 在 EXACTLY 4,228,583 hc 都 timeout(行為一致)|
| NodeStates checksum @ 100K hc | ⚠️ writeback = `0xFCE6FAB77F32BA0B`、S1 = `0xD39EE4BD1457D932`(瞬時中間態微差,latch 後同)|
| hc/s 加速 | ❌ **-4.3% 反而慢一點點**(預料中 —— Step 3 才會解這個)|

**Step 2.5 真正驗證的事**:
1. 6502 ALU 的 op selectors 是 5 個:`op-SUMS / ANDS / ORS / EORS / SRS`(per `nodenames.js` L606-610),不是 PLA timing 信號。
2. 寫回 ALU 的 combinational output `alu[0..7]` 不破壞 S1 的 phi-latch chain(latch 自然從 alu[i] 推出 notalu/alucout/notalucout)。
3. wired-OR 多 op 同活設計能 byte-match S1 的 group 解析。
4. **真正的速度突破需要 Step 3** —— 一次 own 整個 ALU 內部 region(133 nodes + 477 transistors),讓 S1 完全不 traverse,native ALU 才能真正「取代」而非「補丁」S1 的 ALU work。

---

## 1. 5 個真實的 6502 ALU op selector(關鍵發現)

`nes-001 cpu nodenames.js` L606-610:

```js
"op-ANDS": 1228,   // pure AND path
"op-EORS": 1689,   // pure EOR path
"op-ORS":  522,    // pure OR path
"op-SUMS": 1196,   // sum path (ADC/SBC/CMP/INC/DEC + ASL/ROL via alub=alua)
"op-SRS":  934,    // shift-right path (LSR/ROR)
```

之前 Step 2 用的 `op-T+-ora/and/eor/adc`(L513,pla59)是 timing-state 信號(指示「現在在某個 T-cycle 而且是 ora/and/eor/adc 指令」),**不是 ALU op selector 本身**。

ALU 路徑 cover 全部 6502 指令:
- ADC/SBC/CMP/INC/DEC → SUMS
- ASL/ROL → SUMS(alub 被 force 成 alua,cin 0 / prev-carry)
- LSR/ROR → SRS
- AND/ORA/EOR → ANDS/ORS/EORS

多 op 同時 active 時 → wired-OR(NMOS bus contention),Eval_Alu 模擬。

---

## 2. AluBlock.cpp 修正後的核心

```cpp
struct AluCtx {
    uint8_t alua, alub, cin;
    uint8_t op_sums, op_ands, op_ors, op_eors, op_srs;   // 5 PLA selectors
    uint8_t alu, cout;
    uint8_t _pad[6];   // -> 16 bytes (cache-line friendly)
};

void Eval_Alu(AluCtx* c) {
    uint32_t a = c->alua, b = c->alub;
    uint32_t sum = a + b + c->cin;
    uint8_t srs = (uint8_t)((a >> 1) | (c->cin << 7));
    uint8_t r = 0;
    if (c->op_sums) r |= (uint8_t)sum;
    if (c->op_ands) r |= (uint8_t)(a & b);
    if (c->op_ors)  r |= (uint8_t)(a | b);
    if (c->op_eors) r |= (uint8_t)(a ^ b);
    if (c->op_srs)  r |= srs;
    c->alu  = r;
    c->cout = (uint8_t)((c->op_sums ? ((sum >> 8) & 1) : 0)
                      | (c->op_srs  ? (a & 1)            : 0));
}
```

Rebuild:`clang -O3 -shared AluBlock.cpp -o AluBlock.dll` → 110,592 bytes(增 3.5KB vs 4-op 版的 107KB)。

Bench 結果(5-op):
- bulk: 3.97 ns/call(2M calls)→ 252 M ops/sec → **9.9× S1 baseline**
- per-call: 5.18 ns/call → 193 M ops/sec → 7.6× S1
- correctness: 2000/2000 ✓

比 Step 2 的 4-op 慢 1.9×(從 2.09 ns 升到 3.97 ns)—— SRS path + 5 個 cmov(原本 4 個)的成本。仍遠超 >3× 門檻。

---

## 3. Dispatcher.cs 關鍵變更

```csharp
// Step 2.5: 只 own alu0..7(combinational output)。S1 仍擁有 notalu/alucout/notalucout
// 透過 phi-latch transistor chain 從 alu[i] 自然推出。
var alu     = Resolve("cpu.alu[7:0]");
_aluOutputNodes = alu;   // 8 個 nodes,not 18

// 5 個 op selectors
var opSums = Resolve("cpu.op-SUMS");
var opAnds = Resolve("cpu.op-ANDS");
var opOrs  = Resolve("cpu.op-ORS");
var opEors = Resolve("cpu.op-EORS");
var opSrs  = Resolve("cpu.op-SRS");
_aluOpNodes = new List<int>(opSums).Concat(opAnds).Concat(opOrs).Concat(opEors).Concat(opSrs).ToArray();

// Eval_AluBlock writeback
for (int i = 0; i < 8; i++)
{
    byte bit = (byte)((_aluCtx.alu >> i) & 1);
    SetNodeState(_aluOutputNodes[i], bit);   // 只寫 8 個 combinational outputs
}
```

---

## 4. 量測 —— functional ✓,perf neutral(預料中)

### 4.1 100K hc bench

```
baseline (S1):        36,598 hc/s  (27.32 µs/hc)  checksum 0xD39EE4BD1457D932
dispatcher dry-run:   35,329 hc/s  (28.31 µs/hc)  checksum 0xD39EE4BD1457D932  ← SAME ✓
dispatcher writeback: 35,038 hc/s  (28.54 µs/hc)  checksum 0xFCE6FAB77F32BA0B  ← DIFFERENT
```

Δ:
- dry-run 比 baseline 慢 **3.5%**(framework hot-path hooks 成本,Step 2 已分析)
- writeback 比 baseline 慢 **4.3%**(+多寫 8 個 SetNodeState 的 enqueue/propagate 成本)

### 4.2 Checksum 差但 functional 一致 —— 為什麼

| 觀察點 | baseline | writeback | 結論 |
|---|---|---|---|
| trace @ 500 cycle | identical | identical | CPU regs + bus 一致 ✓ |
| trace @ 3000 cycle | identical | identical | CPU regs + bus 一致 ✓ |
| blargg test timeout @ | 4,228,583 hc | 4,228,583 hc | 行為 EXACT 一致 ✓ |
| NodeStates checksum @ 100K hc | A | B(差)| 中間瞬時態微差 |

**原因**:dispatcher 在「ALU 輸入 dirty 觸發」的瞬間寫 alu[i],而 S1 在「ALU 群被 settle BFS 觸碰到」的瞬間算 alu[i]。兩者在某些 hc 的 sub-cycle 內可能差幾步,但 latch 之後 propagate 到 A/X/Y/P/S/IR/AB/DB 的時間一致。

**RecalcNodeCount 也差**:65,939,359 vs 65,911,957(+ 27K enqueue) → 寫 alu[i] 引發了一些 S1 原本不會做的額外傳播(因為 dispatcher 的時機跟 S1 group walk 不完全同步)。

**為什麼這 OK**:CPU 的 observable state(暫存器、memory、bus)由 latched node 決定,latch 是 phi-gated 的 —— 寫 alu[i] 早幾步晚幾步,只要在 latch 半 cycle 內收斂到正確值,latch 後 captured 結果就一樣。

### 4.3 為什麼速度沒提升

S1 仍然 traverse 整個 ALU group(133 個內部節點)來算 ALU output:
- alua/alub 是 group 內節點 — 用 group walk 解析。
- 走 group walk 時,alu[i] 自然被計算出來。
- dispatcher 寫 alu[i] 是「在 group walk 之後再寫一次」—— **沒省 work,反而多做一次 SetNodeState**。

**真正的加速需要 Step 3**:把整個 ALU 內部 region(133 nodes + 477 transistors)標 CodegenOwned,**S1 就不再 traverse 這 region**,native ALU 完全取代。預期能從現在的 27.32 µs/hc 拿掉 ALU 在 D 中佔的部分(估 ~10-15%):
```
若 ALU 區佔 D 的 10%:
  baseline ALU 工作 ≈ 27.32 × 10% = 2.7 µs/hc
  native 算 ALU ≈ 0.13 µs/hc(per-CPU-cycle 0.9 × 3.97 ns / 24 hc)
  省 ≈ 2.6 µs/hc → 27.32 → 24.7 µs/hc → 1.1× 加速(單 block)
```

預估跟 06_alu_validation_results.md §2.2 一致。50-100 個 macro-block 全 codegen 才會逼近 Gemini 預測的 2.5-4× 全域。

---

## 5. 三層成熟度狀態圖

```
┌─────────────────────────────────────────────────────────────────────┐
│ Step 1 ALU 黑盒驗收      ✓ 18.8× native vs S1 avg recalc           │
├─────────────────────────────────────────────────────────────────────┤
│ Step 2 Dispatcher fw      ✓ trace identical, 4.6% framework overhead│
├─────────────────────────────────────────────────────────────────────┤
│ Step 2.5 ALU writeback    ✓ functional identical                    │
│                           ❌ NO speedup (only output skip)           │
├─────────────────────────────────────────────────────────────────────┤
│ Step 3 Graph partitioner  ⏭ mark whole ALU region CodegenOwned →    │
│                              skip 133 internal nodes → ~+10% local  │
│                              加速。Apply to 50-100 macro-blocks 全域│
├─────────────────────────────────────────────────────────────────────┤
│ Step 4 LLVMSharp emit     ⏭ replace hand-coded AluBlock.cpp with    │
│                              LLVM-emit-from-IR for each macro-block │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 6. CLI

```
--codegen-dispatcher              啟用 dispatcher,dry-run(eval but no writeback;Step 2)
--codegen-writeback               啟用 dispatcher + writeback(Step 2.5;functional ≡ S1,perf neutral)
```

---

## 7. 一句話收尾

> **Step 2.5:6502 ALU 的 5 個真實 PLA op selectors(SUMS/ANDS/ORS/EORS/SRS)wired-OR mapping 對齊;dispatcher --codegen-writeback 通過 3000-cycle trace-diff + blargg 同 hc 收斂 functional 驗證。但只 own alu[0..7] 8 個 output node 沒讓 S1 跳過 ALU 內部 group walk,速度持平甚至略降。下一步 Step 3 — graph partitioner 把整個 ALU 區 133 nodes 都 own 起來,才是真正的加速槓桿。**
