# Phase 2(CPU event-driven IR)結尾報告

> branch:`math-algos`。Phase 2 範圍見 `03_phase2_scope.md`。
> 本文 = Phase 2 全部走完後的最終量測 + 結論 + 留給後續 codegen 的設計轉向。

---

## 0. TL;DR

| 量測 | 數字 |
|---|---|
| S1(switch-level,基準)| ~41,775 hc/s |
| **強化過的 event-driven IR interpreter**(島抽取 + LUT + flat unmanaged pool + dispatch micro-opt + SCC 放寬 + 多 pull-up bus 抽取)| ~41,102 hc/s |
| **實質效應** | **break-even ±2% 雜訊** |

**這顆 NES netlist 上,event-driven IR interpreter 在 CPU 上的*純直譯*天花板 ≈ S1 持平**。在這個結構下,interpreter 走到底也只能打平,不會輸太多也不會贏太多。要真正贏 S1 一個量級,**剩下的槓桿只剩 codegen**(P2.5,使用者已暫緩)或硬啃 #42 的危險區(`Hold/Mux` 動態節點,計畫裡的失敗點 #1)。

並非 IR 無用 —— 是這顆晶片的*結構*(小 fanin NMOS gate 主宰,bus 結構複雜)讓 interpreter 的常數壓不下去。

---

## 1. Phase 2 過關條件 vs 實際達成

| 子任務 | 過關條件 | 結果 |
|---|---|---|
| **P2.1** 全網表 IR routing 分類 | 每個 live node 被分到一類 | ✅ COMB_LOGIC 22.5% / COMB_PASS-stack 21.7% / COMB_PASS-bus 17.9% / SEQ 37% / DYNAMIC 0.9% |
| **P2.2** Expr 結構 + drive-direction 分析 + 抽取 | 每個 IR 節點的 Expr eval == S1 群解 | ✅ 5,124 nodes 抽取,verify 0-mismatch 40M+ checks |
| **P2.3** event-driven IR 直譯器 | 對 S1 CPU trace 逐 cycle 一致(架構狀態硬線)| ✅ 200-cyc trace 0 arch diff,checksum bit-identical for pure-logic |
| **P2.4** 速度 go/no-go | IR-CPU > S1?| ⚠️ **未過**:break-even ±2%(誠實:沒過,結構性原因)|
| #42 覆蓋擴充(後加任務)| 抽更多多節點 / bus / dynamic | ⚠️ 部分:multi-pull bus 抽 48/116,SCC 放寬 +432,其餘需 Hold/Mux 動態建模(放棄) |
| P2.5 codegen | (已被使用者刪除任務)| — |

**Phase 2 整體**:正確性硬骨頭啃下來了(交界 bug 死了 5 個 firing 才用「整島 + 吸收」模型解掉);效能 go/no-go **沒過**(原因見 §4)。

---

## 2. 演進(時序與里程碑)

| 提交 | 內容 | 結果 |
|---|---|---|
| `0d43060` | P2.1 全網表 routing 分類(`--dump-levels` 擴充)| 出 IR 覆蓋地圖 |
| `b99398e` | P2.2 it.1:Expr pool + evaluator,純 logic 子集驗證 | 0 mismatch / 27M checks |
| `251057b` | COMB_PASS 形狀分析(stack 21.7% / bus 17.9%)| 為 it.2 鋪路 |
| `342e3f8` | P2.2 it.2:pull-down 路徑列舉抽取(stack)| 5,124 nodes,0 mismatch |
| `b086af9` | P2.3 v1:event-driven 直譯器 —— pure-logic bit-identical,**stack 凍住 CPU(交界 bug)** | 機制驗證 OK / 卡關 |
| `6589b71` | 5 firing 卡關後 + 階段性除錯(brute-force oblivious matches S1 → 確認是觸發完整性 bug)| 找到 bus 誤判 mid 的 mid-guard |
| `2ad26be` | P2.3 final:**整島抽取 + mids 吸收 → 交界 bug 結構上消失** | 全 ir-interp trace 0 arch diff vs S1 |
| `e8d9fa8` | P2.4 step 1:LUT(Gemini r1 Strategy A)+ unmanaged flat pool | 正確,但 ~10% 慢 |
| `d78936e` | P2.4 step 2:dispatch micro-opt(IrClass + IrHasConsumers)| 10% → 7% gap |
| `cb05c37` | P2.4 step 3(#42 partial):放寬 gate-only-SCC 限制(+432 latch outputs)| 7% → 3% gap |
| `c6474ff` | P2.4 ceiling:tight x5 量測 → **+1.7% faster** | 在 noise 偏好那邊的數字 |
| `6fbe23d` | P2.4 #42 partial:multi-pull tri-state bus(48 islands)| **~0% 額外加速**,真實效應 = break-even |

**關鍵轉折**:`2ad26be`(整島模型),它把「IR 節點與 hybrid 節點共群、互相無法觸發」這個*結構性*問題用「設計避開」的方式解掉:**islands 是封閉的 pass component,IR 節點只透過 gate fanout 對外連接,不會與 hybrid 共群** → 交界 bug 不可能發生。後面所有 P2.4 工作都建立在這個模型上。

---

## 3. 最終 IR 設計(committed `6fbe23d`)

```
WireCore.Ir.cs:
  ExprOp { Const0, Const1, NodeRef, Not, Or, And, Mux, Hold }
  IrNode { Op, A, B, C }      // unmanaged flat pool _irFlat (no bounds check)
  IrRoot[nn]                  // -1 = hybrid / >=0 = root expr index
  IrAbsorbed[nn]              // 1 = absorbed mid (inert, skipped in RecalcNode + AddNodeToGroup)
  IrUseLut[nn] / IrLut* CSR   // K<=14 -> precomputed byte[2^K] truth table
  IrClass[nn]                 // collapsed dispatch (Hybrid/Absorbed/Lut/Expr)
  _revDepStart/_revDepList    // reverse-dependency CSR: gate -> IR consumers
  IrHasConsumers[nn]          // 1 iff revDep non-empty (skip SetNodeState revDep loop)
```

**抽取演算法(per pass-island,union-find pass-components)**:
- **單 pull-up 乾淨島**(主流):輸出 v 的 Expr = `NOT(PullDownCond(v))`,其餘成員為 absorbed mids(無 pull-up、不 gate 任何東西)。3,408 個。
- **多 pull-up tri-state bus 島**(P2.4 #42):
  - bus_line = 非-pull-up 中*直連最多 pull-up* 的節點。
  - 每個 driver d:`PullDownCond(d)` 但停在 bus_line(用 `_irBusBoundary`)。
  - bus_line Expr = `Mux(en_1 ? d_1 : Mux(en_2 ? d_2 : ... : NodeRef(bus_line)))` —— 末層 self-ref 提供 Hold 語意,由 event-driven settle + SetNodeState 早返回實現。
  - 48 個 island 抽出,68 個 multi-pull 因不符 single-direct-pass 模式被拒。

**runtime dispatch**:
```csharp
if (EnableIrInterp && IrClass != null) {
    byte cls = IrClass[nn];
    switch (cls) {
        case Hybrid:   fall through to S1 group walk;
        case Absorbed: return;
        case Lut:      SetNodeState(nn, EvalLut(nn));       return;  // O(1), branch-free
        case Expr:     SetNodeState(nn, EvalExpr(IrRoot[nn])); return;
    }
}
```

**最終覆蓋**:
```
3,602 extracted (3,408 logic + 146 multi-node + 48 bus-islands), 246 absorbed
LUT: 3,698 tables (88 KB total), 8 K>14 fall back to EvalExpr
revDep: ~6k edges, 3,217 nodes have >=1 IR consumer
```

---

## 4. 為什麼 break-even —— 結構性原因(關鍵分析)

### D 分布(重要證據)

`--ir-interp --count-events`:
```
D = 613 RecalcNode / hc
  IR路徑:    14.7%   ← 我們優化的地方
  absorbed:   0.7%   ← 零成本跳過
  HYBRID:    84.6%   ← 沒被 IR 化的、仍走 S1 群走訪
```

**85% 的工作量還在 S1 hybrid 路徑上。** IR 化的 15% 多半是 1-2 input 的小 gate(LUT 平均 K=1.86),S1 處理它們本來就*極快*(1-node 群走訪 + 256-entry LUT)——「LUT 對它們省的 ≈ IR machinery 加的開銷」,所以淨效應 ≈ 0。

### 為什麼熱的東西沒被 IR 化

剩下 1,640 個未抽的 islands:
- **multi-pull = 68**(扣掉抽出的 48):複雜多階 bus(cpu.db / cpu.ab 這類),中間有 latch / multi-pass / 多重 enable,**簡單 tri-state 模式抓不到**。
- **no-pull = 1,084**:純動態 / 浮接群(PPU shift register / sprite latch 等)。要 Hold/Mux 建模(`m_new = Mux(any-gnd-path, 0, m_prev)`)= 計畫文件 §15.3 列為「失敗點 #1」的 **dynamic node 抽象錯**。
- **mid-gates = 440**:內部 mid 對外 gate,試過內部-gating 放寬有正確性破口(absorbed mid 的 NodeStates 凍住,卻被 NodeRef 它的 Expr 讀到),需要對 mid 也做 Hold/Mux。

**真正的熱點 = cpu.db / cpu.ab 這些主匯流排**,但它們的結構不適合*純結構性* IR 抽取。

### NES 結構性事實(從量測歸納)

1. **小 fanin 為主**:NMOS gate 的 K 平均 = 1.86(LUT 平均輸入只有 ~2 個)。inverter 和 small NOR 是主流。
2. **S1 對小 gate 已極快**:1-node 群走訪 ≈ 幾條 ALU 指令 + 256-entry LUT。比 IR machinery 的 dispatch + revDep 還精簡。
3. **熱的多節點群結構複雜**:不是漂亮的 series-parallel pull-down(那種已被 stack 抽取吃了),而是有 latch / 多階驅動的複合結構。
4. **D 不大但分散**:610 RecalcNode/hc 分布在數千 nodes 上,沒有特別集中的瓶頸點。

→ 結論:**純-interpreter 的優化空間被結構卡死**。要破這個天花板,得改變 runtime model(不再是 per-node Expr eval + dirty queue)。

---

## 5. 所有走過的設計嘗試 + outcome

### IR machinery 嘗試

| 嘗試 | 結果 |
|---|---|
| **option A**:per-node IR,group 走訪走 IR-as-boundary-driver | ⛔ **5 firings 死路**。bus_line(hybrid)與 driver(IR)在同群,各自獨立解析 → 互相無法觸發,CPU 凍住 |
| **option B**:整島抽取 + mids 吸收(closed pass-island) | ✅ 結構上避開交界 bug |
| brute-force oblivious(re-eval all per hc)| ✅ 正確,但 121× 慢(同 X 實驗)—— 證明 dirty-set 不能丟 |

### Eval 路徑優化(per Gemini r1 Strategy A/B/C)

| 嘗試 | 結果 |
|---|---|
| **Strategy A:LUT 真值表** + flat unmanaged pool | ✅ 正確,~0% solo(因為小 K 的 S1 已經很快)|
| dispatch micro-opt(`IrClass` 一個 byte + `IrHasConsumers`)| 7% gap → 3% gap |
| Strategy B:linearized bytecode VM | 未做(LUT 已涵蓋 K≤14;K>14 只 8 個,不值得另寫 VM)|
| Strategy C:macro-coalescing(A→B→C 串成大島)| 未做(大多 IR 輸出有多重消費者,可融合的少)|

### 覆蓋擴充

| 嘗試 | 結果 |
|---|---|
| **放寬 gate-only-SCC 限制**(收 cross-coupled latch outputs)| +432 nodes,gap 從 7% 收到 3% |
| **multi-pull tri-state bus 抽取** | +48 islands,但 ~0% 額外加速(抽到的不是熱 bus)|
| internal-gating-mid 放寬 | ⛔ 有正確性破口(absorbed mid 被 Expr NodeRef 讀到)|
| Hold/Mux 動態節點建模(1,084 no-pull islands)| 未做(失敗點 #1 風險;CP 已被 multi-pull 結果打臉一次,不再加碼)|

### 觀測工具

`--dump-levels`(SCC + level 結構)、`--verify-ir`(逐節點 0-mismatch 驗證,40M+ checks)、`--ir-brute`(oblivious 對照組,證實是觸發 bug)、`--dump-states`(高位 node ID 比對找 root divergence)、`--count-events` D 分類分布。

---

## 6. 給 codegen(P2.5,延後)的設計轉向 —— r2.txt 的關鍵警告

`r2.txt`(Gemini 對 codegen 路徑的分析)指出:**現在這條 interpreter 友好的 IR 設計,若要走到 LLVM/C# codegen,有幾個方向必須翻轉,否則會撞 main 的 S4 撞過的同一面牆**。

### r2 點出的兩個死胡同

1. **Giant Oblivious Function**:把全部 14.7k 節點按拓樸序生成一個巨型 bitwise 函數,每半週期呼叫一次。
   - LLVM 會優化得很漂亮(register alloc / instruction schedule),但**仍是 O(N) 的工作量**(S1 是 O(D)≈610)。
   - 數萬行 codegen → **I-cache 崩潰** → 速度反而崩。
   - **這就是 main 的 S4 撞過的牆**(3-6× slower than S1)。

2. **Per-node JIT(function pointer dispatch)**:保留 dirty queue,每個 RecalcNode 內部編譯成獨立小函數,queue 存 function pointers。
   - 每次 dispatch 是 **indirect branch** → CPU 分支預測器無法預測 → pipeline stall。
   - LLVM 產生的快機械碼好處被 dispatcher overhead 吃光。

### r2 的可行 codegen 方向

- **Coarse-Grained Event-Driven(macro-block)**:把整網表分成 ~50–100 個 macro-block(整顆 ALU、整顆 PPU 掃描計數器),每個 block 一個 LLVM-emit 函數 `void Eval_X(byte* in, byte* out, byte* internal)`。Queue 排在 block 層級。
- **VLIW / SIMD 向量化**:對同層 independent gates 強制 packed-vector(AVX2 256-bit)。
- **Trace-based JIT**:profile + fuse hot trace。

### 為了 codegen,**現在的 IR 設計要動哪些地方**

| 設計選擇 | 現在(interpreter 友好) | codegen 需要轉向 |
|---|---|---|
| **抽取粒度** | per-node Expr(3,602 個小 Expr,K 平均 1.86)| **per-macro-block**(~50–100 個大塊),每塊內部多節點組合邏輯一起 |
| **runtime model** | byte LUT lookup(K≤14)| inline bitwise ops,**捨棄 LUT**(LLVM 暫存器配置會比 table read 快,只要 block 夠大)|
| **queue 粒度** | per-node dirty queue + revDep | **per-block** dirty queue;block 之間的 input/output 才放 queue |
| **block 邊界** | 沒概念(per-node)| 必須明確定義:每個 block 的 **Inputs**(外讀)、**Outputs**(被外讀)、**Internal state**(block 私有,可在暫存器內)|
| **動態節點 / Hold** | 用 NodeRef-self / SetNodeState 早返回隱式 hold | block 內部用 local state(暫存器/區域變數),hold 自然發生;**不需要 Hold Expr type** |
| **記憶體佈局** | `byte* NodeStates` flat,RCM 重排已最佳化 cache | 必須**繼續尊重 RCM 順序**,否則 codegen 再快也卡在 memory fetch。block 內取 contiguous slice |
| **絕對不能** | (interpreter 自然事件驅動,沒這問題)| **絕不 oblivious 全掃**(已被 main 證偽),**絕不 per-node function pointer queue**(indirect branch 死路)|

### r2 對 codegen 的整體判斷

> 「LLVM 是優化*常數因子*的神,但**無法逆轉演算法複雜度劣勢**。如果 codegen 的 IR 還是 per-node 細粒度,LLVM 救不了。」

**轉折點**:現在 island-based per-node IR 是 *interpreter 的正確設計* —— 它讓 dirty-set + LUT eval 都成立。但要進 codegen,**必須先在 IR 層加一個「macro-block 切割」階段**,把多個 per-node IR 融合成大塊,給 LLVM 真正能優化的單位。

### r2 的具體工程建議(原文)

> 「不要一開始就寫編譯器。挑 NES 中一個相對獨立且複雜的區塊(例如 PPU 的 Address Latch 或 CPU 的 Decoder),手寫一段 C++(模擬 LLVM 產生的樣子),透過 P/Invoke 讓 C# 呼叫。測試一下:把這個區塊當黑盒呼叫,和 S1 用 BFS 走進去算的效能差距有多少。**如果這個差距值得,再寫整個 Codegen Backend**。」

→ 這跟你「先看 IR 的純粹上限再決定 codegen」的策略**完全一致**。Phase 2 的結果(break-even)告訴我們:**interpreter 沒空間了,要往上必須走 macro-block codegen,而且要先用一個 P/Invoke 黑盒驗收益**。

---

## 7. 開放問題 / 後續方向

如果有一天回來繼續推:

1. **macro-block 切割演算法**:把現在的 island 模型升級成 macro-block 分割。可能的方向:
   - 把多個強耦合 island fuse 成 block(用 gate-fanout 連通分量)。
   - block 邊界處理:Input / Output / Internal state 區分。
   - 目標 50–100 個 block,每個 ~100-300 nodes。
2. **macro-block 黑盒驗收**(r2 建議):手寫一個典型 block(例如 CPU instruction decoder)的 C++,P/Invoke 呼叫,量比 S1 快幾倍。**如果不到 5×,codegen 不值得;如果 10×+,推 codegen**。
3. **保留 event-driven 骨架**:絕不 oblivious 全掃。block-level queue。
4. **AVX2 SIMD 機會**:粗看 NES 大多 sequential dependent(register → ALU → bus),SIMD 機會少;但 PPU 的 8 個 sprite eval 同時跑可能可以 lane-parallel。
5. **Hold/Mux 動態節點建模**(1,084 islands)的價值,仍然待重新評估 —— 也許在 macro-block 框架下,動態節點直接是 block 內部 local state,不需要單獨建模。
6. **#42 完整化**:multi-pull 剩 68 個的複雜結構,如果 macro-block 切割後仍未被吃,可能要 trace-based JIT 處理熱路徑(`r2` Strategy 3)。

---

## 8. 一句話收尾

> Phase 2 把「event-driven IR interpreter on CPU」的天花板釘在了 **與 S1 持平(±2%)**。這不是失敗 —— 它是個有方向性的結論:這顆 NES 的結構(小 fanin NMOS + 複雜 bus)讓*純直譯*的常數壓無可壓。要破這個天花板,**剩下的就只有 macro-block codegen 一條路**,而 r2 已經把那條路上的雷區(oblivious 死、indirect-branch 死、I-cache 死)畫好了。

**Phase 2 結束。下一個動作應該是 codegen 黑盒可行性驗收(r2 §3),不是繼續打磨 interpreter。**
