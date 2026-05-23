# 10 — Step 3.5 結果:架構限制發現(architectural ceiling)

> 任務追蹤:#57(own 133 ALU closure)→ #58(本文 + 路線決定)。
> 接續:`09_step3_partitioner_results.md`。
> 性質:**重要 negative result** + **架構級別重新評估**。

---

## 0. 結論

🟡 **Step 3.5a 發現 codegen 路徑的真實架構天花板**:
1. **CodegenOwned 只 skip RecalcNode 入口,不阻止 S1 的 group walk 從別處 traverse owned region**。實測 D 從 614.2 降到 521.1(-15%)看似有 saving,但實際是 CPU 卡死(reset 沒走完)→ 不在 do work,**fake speedup**。
2. **Reverse-closure boundary 不可靠** —— 6502 ALU 的 133-node closure 自然包含 `cpu.adh[0..7]`、`cpu.adl[0..7]`、`cpu.cclk`、`cpu.phi2`、`cpu.a0/a4` 等關鍵 CPU bus / clock / register 節點(它們 pullups=0,沒被「stop at pull-up」啟發式擋下)。OWNing 這些直接斷 CPU 命脈。
3. **保守 subset(只 own 8 個 `cpu.#aluresult` mids)**:trace 仍輕微差異(64 line),hc/s 沒明顯改善 → CodegenOwned 在當前架構下對加速貢獻有限。

**結論:在現有 S1 group-resolution 架構下,單純擴大 CodegenOwned 範圍無法產生 speedup。要真實加速需要架構級改變。**

---

## 1. 測試過程 + 量測

### 1.1 完整 closure(133 nodes)owned + transparent latch write

```
mode: --codegen-own (own all 133 reverse-closure nodes + write notalu/alucout/notalucout)

baseline   : 38,662 hc/s  (26.05 µs/hc)  D=614.2 RecalcNode/hc
own-133    : 47,049 hc/s  (21.25 µs/hc)  D=521.1 RecalcNode/hc  +22% (FAKE)
```

⚠️ **CPU 卡死在 PC=00FF**(reset 沒完成)。22% 速度是因為 CPU 不在 simulate 真實工作。

### 1.2 Closure 內容分析(實際包含什麼)

```
# closure inspection (first 40 of 133):
#   ... 11 anonymous mids ...
#   cpu.#aluresult0..7         ← 真正 ALU-internal,8 nodes ✓
#   cpu.a0, cpu.a4             ← ACCUMULATOR bits! 不應在 ALU closure
#   cpu.adh0..7  (8)           ← address bus HIGH bits!
#   cpu.adl0..7  (8)           ← address bus LOW bits!
#   cpu.C78.phi2               ← clock phase 2!
#   cpu.cclk                   ← CPU clock!
#   cpu.cp1                    ← clock phase 1!
#   ... 93 more (most anonymous mids of unclear function) ...
```

`pullups=0` 對所有以上「不該屬於 ALU」的關鍵節點 → 「stop at pull-up」啟發式擋不下它們 → BFS 自然 sweep 進來。

**ALU 跟 CPU 的其餘部分透過大量 pass transistor 互連**(ALU output → SB → A/X/Y;ALU input ← AB/DB),沒有清楚的 "block boundary"。reverse-closure 必然會 leak。

### 1.3 保守 subset(只 own 8 個 `cpu.#aluresult` mids)

```
mode: --codegen-own with filter (only "cpu.#alu*" prefix)

closure size:        8 nodes (was 133)
total CodegenOwned: 26 (8 mids + 8 alu + 8 notalu + 2 alucout = 26)
rate:               37,635 hc/s vs 38,662 baseline = -2.7%
D:                  612.9 vs 614.2 (essentially unchanged)
trace diff:         64 lines (small but non-zero)
```

D 沒變、速度沒變 → CodegenOwned skip 在這幾個節點上**沒省到任何工作**。
Trace 還是小差異 → 即使這 8 個明確 ALU-internal node,owned 後也對下游有微影響(可能因為 S1 群解析的 starter 路徑變了)。

---

## 2. 為什麼 CodegenOwned 不能直接給 speedup —— S1 架構深層原因

### 2.1 S1 group-resolution 模型回顧

```
RecalcNode(nn):
    if (CodegenOwned[nn]) return;            ← skip 入口
    newValue = ComputeNodeGroup(nn)          ← BFS 整個 connected 群
    for each m in group: SetNodeState(m, newValue)
```

`ComputeNodeGroup(nn)` 對 nn 開始,沿 channel 邊 BFS,把 connected 的所有節點(在當前 transistor 狀態下)收集起來,共同決定群的 resolved value。然後群裡所有節點都被 SetNodeState 寫入 newValue。

### 2.2 為什麼 owned 不阻止 group walk

OWN nn 只阻止 `RecalcNode(nn)` 這個入口。但若 `RecalcNode(mm)` 中,mm 跟 nn 在同一群(channel-connected),那麼 `ComputeNodeGroup(mm)` 的 BFS 還是會 traverse nn,共同決定群的 value。

所以 OWN 的節點:
- 入口計算被 skip ✓
- 但作為「群成員」仍會被 traverse + 寫值 ✗
- 整個群的 walk 工作沒被省下

### 2.3 為什麼 own 多了反而會破

當我 OWN `cpu.adh0`(address bus 低位)後,正常情況下:
- RecalcNode(adh0) 從 SetNodeState 觸發 → 計算 → 寫 adh0(以及 adh0 所在群的所有節點)。
- OWN 後:RecalcNode(adh0) skip → adh0 不被更新 → CPU 看到的 address bus 是錯的。

S1 中下游邏輯讀 `NodeStates[cpu.adh0]` 來算 memory address → 拿到陳舊值 → memory 讀錯位元組 → CPU 跑亂。

---

## 3. 真實的加速路徑(架構級選項)

### 選項 A —— 物理移除 owned region 的 transistor

在 LoadSystem 之後、Reset 之前,從 `TransistorList` 跟 `Nodes[].C1c2s` / `Nodes[].Gates` 中**刪掉** owned 區內部的 transistor。
- S1 看到的 netlist 變小 —— owned 區只剩 boundary node 直接連 supply。
- Dispatcher 在 boundary input 變動時計算所有 boundary output 並寫。
- 真正的 work 減少(S1 不再 traverse 移除的部分)。

風險:邊界節點的 group 解析會被破壞(若移除的 transistor 在群結構中重要)。需要仔細設計 boundary 處理。
**工程量:中等。**

### 選項 B —— Phase 2 IR 完整接管

用 Phase 2 已經 extract 的 `Expr` 樹直接代換 group walk。對 Expr-coverable 的節點,RecalcNode 走 EvalExpr,完全不碰 transistor。
- Phase 2 報告 coverage 只有 14.5%(per memory),所以單 IR-only 路徑天花板有限。
- 但對 covered 節點,確實能 skip group walk 工作。
- **記憶體**:`s4-route-single-instance.md` 提到 main 已試過這個方向 → 「3-6× SLOWER than S1」,因為 batch re-eval 14.7K nodes/half-cycle when ~hundreds change。Math-algos 的 event-driven IR 已驗證 Phase 2 = break-even。

所以這條路已知 stuck。

### 選項 C —— 改 S1 模型,讓 group walk 認得 codegen ownership

修改 `ComputeNodeGroup` 的 BFS 規則:遇到 CodegenOwned 節點時**不展開**(視為「supply-like」固定值 node)。這樣 group walk 在 owned 邊界停下,owned region 不被 traverse。

風險:會切壞群的 capacitance / pullup 解析。需要 dispatcher 預先把 owned 區的 boundary 寫對。
**工程量:中等。**

### 選項 D —— 接受現實,改 scope

**承認 S1 的 group-resolution 架構不適合 codegen 接管**,把 codegen 路徑用於不同目的:
- Side-channel optimization(例如 detect 真實 ALU 工作模式,做 caching)。
- 重新走 main 的 oblivious / batch 方向,但加 event-driven gating。
- Branch 收尾,記錄發現,等下一個架構迭代。

---

## 4. 路徑樹(對照 06_alu_validation_results.md 的原規劃)

| Step | 規劃 | 實際結果 |
|---|---|---|
| 1 ALU 黑盒 | native ≥ 3× S1 | ✓ 18.8× |
| 2 Dispatcher | framework 跑 + 量 overhead | ✓ 4.6% |
| 2.5 Writeback | trace ≡ S1 | ✓ functional 一致 |
| 3 Partitioner | 找 50-100 macro-block | ✓ 30 個 codegen-attractive |
| **3.5 Own internal** | **+10% local speedup** | ❌ **不可達**(架構限制)|
| 4 LLVM emit | 真正 codegen | **❓ 沒架構支持的話不會更快** |

**Step 3.5 的失敗 → Step 4 的前提崩潰**:Step 4(LLVM 自動 emit)的目的是讓 dispatcher 跑得更快。但 dispatcher 本身的計算(2.09-3.97 ns/call native)已經比 S1 group walk 快 9-18×。**bottleneck 不在 dispatcher,在 S1 group walk 沒被 skip**。沒架構改變,LLVM emit 不會加速。

---

## 5. 給 user 的選項

**a. 走架構改變的 Step 3.6** —— 實作選項 A(物理移除 transistor)或 C(改 ComputeNodeGroup)。中等工程量 ~2-3 天。**有可能 真正加速 10-30%**,但有 correctness 風險,需要大量測試。

**b. 接受 codegen 路線已驗證 OK 但加速不可達(本架構),branch wind-down** —— 把已完成的(framework + partitioner + functional writeback)當成「codegen 路徑技術可行性 + 架構限制」的工程紀錄,close branch。Memory 補一筆「math-algos 在 Phase 2.5 步進到 Step 3 後,確認 S1 group-resolution 架構是 codegen 接管的天花板」。

**c. 走別的方向** —— 例如把現有 dispatcher framework 用在別處(monitoring、profiling、side analyses),或退回繼續 Phase 1/2 的 event-driven 優化(但 Phase 2 已 break-even,空間有限)。

---

## 6. 一句話收尾

> **Step 3.5 揭露:在現有 S1 group-resolution 架構下,單純擴大 CodegenOwned 無法加速 —— 因為 group walk 仍會從別處 traverse owned region。22% 看似加速是 CPU 卡死 reset 的偽造結果;保守 subset 則完全沒省到工作。真實 codegen 加速需要架構級改變(物理移除 transistor、或改 ComputeNodeGroup BFS 規則),不是再寫 LLVM emit。請選擇方向 a/b/c。**
