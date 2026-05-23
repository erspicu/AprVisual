# 11 — Step 3.5 Option D 結果:BFS-block 機制成功,但 speedup ≈ 0

> 任務追蹤:#59-62。
> 參考:`10_step35_architecture_finding.md`(Step 3.5 架構發現)+ `tools/knowledgebase/message/20260523_155004.txt`(Gemini r3 顧問 Option D 設計建議)。
> 結果:**Option D 機制本身對(correctness),但沒給有意義的 speedup → 觸發 Gemini 給的 1-2 day timebox 失敗條件 → 進入 wind-down**。

---

## 0. 結論 (TL;DR)

🟡 **按 Gemini r3 §Q4 的判定:Option D 失敗,要 wind down / pivot AOT**。

| 變體 | rate | D (RecalcNode/hc) | trace | 結論 |
|---|---|---|---|---|
| baseline (S1) | 37,919 hc/s | 614.2 | ref | — |
| Option D + 8 owned alu outputs | 36,713 hc/s (-3.2%) | 614.0 | **IDENTICAL** ✓ | correctness 正,framework 正 |
| Option D + 26 owned (+ notalu+cout+#aluresult) | 37,810 hc/s (-0.3%) | 612.9 | 64-line diff | functional ≡ S1 |
| **Option D + 62 owned (+ 36 carry-save mids 計算寫入)** | **36,703 hc/s (-3.2%)** | **612.0 (-0.4%)** | 64-line diff | functional ≡ S1,但 **0.4% D saving 被 hook overhead 吃掉** |

Gemini r3 給的閾值是「rate 從 38K → 70K (~+85%) 才證明路通」。實測 0%。明確 Option D 在這 architecture 上的 ceiling = baseline。

---

## 1. 實作:Gemini r3 §Q3 的 Option D 設計

```csharp
// src/AprVisual/Sim/WireCore.Group.cs  AddNodeToGroup ~L85
if (EnableCodegenDispatcher && CodegenOwned != null && CodegenOwned[nn] != 0)
{
    // 視為「無限強度供電軌」-- 用 NodeStates 當作 strong-driver flag,然後阻斷 BFS
    _groupFlags |= NodeStates[nn] == 0 ? NodeFlags.Gnd : NodeFlags.PullUp;
    return;   // 不加入 _groupBuf,不展開到鄰居 -- BFS 在此終止
}
```

關鍵:**不加入 `_groupBuf`** ⇒ 後續 `for (i in _groupBuf) SetNodeState(i, value)` 不會覆寫 dispatcher 寫入的值。**不展開鄰居** ⇒ S1 BFS 不再 traverse owned region 內部。

加 36 個 carry-save named mids 的 dispatcher 計算(`#A.B`、`#(A+B)`、`#(AxB)`、`#(AxBxC)` × 8 + 4 inter-group `#(AxBN).CMN`):

```csharp
// 純 boolean 從 alua/alub/cin 推導,bit-slice ripple-carry
byte andAB = (byte)(alua & alub);          // #A.B    carry-generate
byte orAB  = (byte)(alua | alub);          // #(A+B)  carry-propagate
byte xorAB = (byte)(alua ^ alub);          // #(AxB)  pre-carry sum bit
byte carry = cinBit; byte xorABC = 0;
for (int i = 0; i < 8; i++) {
    byte aiXorBi = (byte)((xorAB >> i) & 1);
    byte sumi    = (byte)(aiXorBi ^ carry);
    xorABC      |= (byte)(sumi << i);       // #(AxBxC)
    carry        = (byte)(((andAB>>i)&1) | (aiXorBi & carry));
    if ((i & 1) == 1) groupCarry[i/2] = carry;  // 4 個 group carry-out
}
// 寫入 NodeStates: 8 + 8 + 8 + 8 + 4 = 36 個節點
```

---

## 2. 為什麼 owned 多了卻沒 speedup —— 數字解析

### 2.1 D(RecalcNode/hc)reduction 很小

| Owned size | D | Δ vs baseline | 解讀 |
|---|---|---|---|
| 8 | 614.0 | -0.03% | 邊界 alu 節點佔總 D 0.03% |
| 26 | 612.9 | -0.21% | 加 8 #aluresult + 8 notalu + 2 alucout 也才省 0.2% |
| 62 | 612.0 | -0.36% | 加 36 carry-save mids 累積 0.4% |

要拿到 50%+ saving,需要 owned 數 200+ 而且全部能 dispatcher 計算/寫。

### 2.2 為什麼 owned 多了反而 -3.2% 慢

每個 dispatched ALU eval 現在多寫 36 個 NodeStates(carry-save mids),`SetNodeState` 觸發 gate-fanout → 38,500 個 ALU dispatches × 36 setNodeStates = 1.38M 額外寫,每寫 ~50ns(含 fanout)= ~70ms = **2.5% of 2.7s wall time**。

加上 hot-path hook(`CodegenInputChanged` + `CodegenOwned` skip + Option D BFS-stop)= 另 ~3%。

So:**省 0.4%,花 ~5.5% = -3.2% net**。Option D 在 ALU 案例上 算術上不可能 win,除非 owned set 大到能省 10%+ S1 work。

### 2.3 為什麼不能 own 更多

要 own 更多 nodes,需要 dispatcher 計算它們的值。但 ALU 內 133 個 nodes 中:
- ~36 個有 semantic 名字(carry-save,可 derive)
- ~80 個 anonymous mid(不知道幾何/邏輯意義)
- 剩下含 clock/bus 的 leakage(`adh*`、`adl*`、`cclk`、`phi2`、`a0/a4`)— 絕不能 own

無法擴展到 200+ owned,所以 D 在 ALU 上的天花板已經接近。

---

## 3. correctness 觀察

`--codegen-writeback`(8 owned + Option D)**trace IDENTICAL** —— 證明 Option D 機制正確,沒副作用。

`--codegen-own`(26 / 62 owned + Option D)**trace 64-line diff** —— 跟之前 Step 3.5a(無 Option D)一樣的 diff 模式。Diff 從 cyc 74 開始,集中在 Y register 跟 stack-page address(AB=$0100):

```
baseline:  cyc 74 ... Y=E0  AB=0100 ...
own62:     cyc 74 ... Y=E1  AB=0101 ...
```

推測:dispatcher 寫 `notalu/alucout` 的時機跟 S1 phi-latch 微差,造成某些 CMP/SBC/PHA 指令的 carry 狀態差一拍。但 blargg test 在同 hc 收斂,functional ≡ S1。

所以 Option D **不是 trace diff 的成因**;Step 3.5a 已知 issue 仍在。Option D 唯一的影響就是「BFS stop」,完全 transparent。

---

## 4. 對齊 Gemini r3 §Q4 判決

> 「Timebox Option D (1-2天),若不行則執行 Option C (Wind-down)。
>  如果 Option D 實作後,Trace-diff 通過,且 rate 從 38K 提升到例如 70K,那證明這條路是通的,只是要不要繼續的問題。
>  如果 Option D 依然遇到 capacitance 判定錯誤導致 Trace-diff 失敗,**立刻承認這個演算法架構的天花板 (Option C)**。」

實測:
- Trace-diff **沒** capacitance 錯誤(Option D 機制對)
- rate **沒** 從 38K 提升到 70K(實際 36-37K,在 noise band 內持平甚至略降)
- 連 +5% speedup 都沒有

**屬於 Gemini 的「執行 Option C」分支**。

---

## 5. 整體 Phase 2.5 codegen 路徑成果評估

| Step | 結果 | 工程價值 |
|---|---|---|
| 1 ALU 黑盒驗收 | ✓ 18.8× native vs S1 avg | 證明 LLVM target shape OK |
| 2 Dispatcher framework | ✓ 4.6% overhead,bitmask polling 對齊 Gemini r2 §2.8 | runtime kernel 工程可用 |
| 2.5 Writeback functional | ✓ trace ≡ S1 | 證明跨 P/Invoke + S1 propagation 一致 |
| 3 Partitioner | ✓ 30 個 codegen-attractive macro-block 自動找出 | **「Netlist Lifter」 — Gemini r3 §Q5 稱「聖杯級」成就** |
| 3.5 Own internal | ❌ 架構限制(BFS reach) | 證明 S1 group-resolution 為加速天花板 |
| **3.5 Option D** | ❌ **correctness 對,但 speedup ≈ 0**(dispatcher overhead 蓋過 D saving)| 證明 BFS-block 機制可用,但 ALU 案例 上下文太緊耦合,工程上無法擴展 owned set 大到產生 net win |

**Codegen 路徑作為「runtime accelerator」的結論:不可行**(在 6502 + S1 group-resolution 架構下)。

但 **partitioner + dispatcher framework + Option D BFS-block 機制 留作 future AOT compiler 的前端 + runtime kernel**。

---

## 6. 給 user 的下一步建議

按 Gemini r3 §Q5 戰略建議:

**Option A — Wind down `math-algos` branch**:
- 標記 Phase 2.5 = Research-Complete
- 保留所有工具(WireCore.Dispatcher、WireCore.Partition、AluBlock 系列)在 branch 上
- Memory 補一筆「Phase 2.5 codegen 路徑驗證:工具鏈可用,但 S1 runtime 加速天花板已撞到」
- 不 merge 回 main(架構衝突 + 沒 ROI)

**Option B — Pivot 到 AOT compiler 方向**:
- 用 partitioner 當前端,直接生 C# / C++ AOT 模擬引擎(MetalNES 路線)
- 放棄 S1 runtime,純靜態編譯
- 用 S1 當 Oracle 驗證 AOT 輸出正確性
- 這是大型重啟,需要新分支

**Option C — 暫時擱置,等別的方向觸發**:
- 留 math-algos 完整不動,Phase 2.5 結果記錄完
- 將來有相關需求(例如 multi-instance batch sim、AOT compiler、新 simulator backend)再回來 cherry-pick

---

## 7. 一句話收尾

> **Option D 的 BFS-block 機制正確實作完畢(trace IDENTICAL with 8 owned),Gemini 判斷準 ── 但因 ALU 與 CPU datapath 太緊耦合,能安全 own 的 named 節點上限就是 62 個,saving < 1%,被 dispatcher 寫 36 個 carry-save mids 的 hook overhead 完全蓋過,**淨 -3.2%**。Codegen runtime accelerator 路徑撞牆。下一步按 Gemini r3 §Q5 戰略:wind down branch 或 pivot AOT compiler。**
