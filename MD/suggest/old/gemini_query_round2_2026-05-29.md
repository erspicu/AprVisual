你在 round 1 給了 Q1-Q6 共 6 個非 IR 優化建議。 我把全部都實測完了,結果如下:

【實測戰績】(C# 和 Rust 雙端,5-run sequential top-3 mean)

| 你的建議 | 你的預估 | C# 實測 | Rust 實測 |
|---|---|---|---|
| Q1 Walk-order PQ → 改 static topo sort | -15% to -5% | (未試,你已標負面) | (未試) |
| Q2 Long-list SIMD scan | -2% to +3% | (未試,雜訊級) | (未試) |
| Q3 Clock-phase static wave 0 | **+15% to +30%** | (未試,等你校準) | (未試) |
| Q4 Per-handler dirty-set lookup | +5% to +10% | (未試) | (未試) |
| Q5 Cold path NoInlining | +2% to +6% | **-1.53%** | **-1.70%** |
| Q6 lastGate cache + sort | +3% to +8% | **-1.68%** | **-5.36%** |

兩個試過的全部負面。 預估結果差距驚人(你說零風險,實測卻一致失敗)。

---

【根因分析 ── 校正你的世界觀】

### 根因 1:Q5 失敗 ── 現代編譯器 cold path 已自動處理

你假設「Hot/Cold split 釋放 inline budget」,但實測 C# JIT 和 LLVM 都已自動:
- 根據 branch hint 把 cold 分支推到尾端
- 對 `if (rare_condition) { cold_code }` 的 cold code 做 dead-code-elimination 或 jump-to-cold

強制 `[NoInlining]` / `#[inline(never)]` 反而引入真實 function call(args setup、stack frame、ret),在 hot path 上是純 overhead。 這在 .NET 6+ 和 LLVM rustc 1.60+ 已是常態。

**校正**:「現代 JIT/LLVM 不會把 cold code 放進 hot path」── 你的預估邏輯需要更新。

### 根因 2:Q6 失敗 ── NMOS 拓樸實際 gate 重複率極低

你假設「對 transistor_list 排序 + lastGate cache 可省 NodeStates[gate] 重複讀」。 實測在 NES 2A03/2C02 NMOS 拓樸上:

- 每個 `TlistC1c2s` 是該 node 的 (gate, other) 對列表
- 為了 cache hit,需要同 gate 在同一個 node 的 list 中重複出現
- NMOS 通常 1 transistor / 1 (gate, other) 對,**重複率接近 0**
- 排序後同 gate 還是只出現 1 次,cache hit rate ≈ 0
- 結果:`if (gate == last_gate)` 變純 overhead,每 iter 多 1 cmp + 1 conditional

C# 退 -1.68%,Rust 退 -5.36%(LLVM 對 conditional state-load 的 codegen 比 JIT 更差)。

**校正**:Gemini 假設的「high-fanout gate 在同 node 重複」對 NES NMOS 拓樸不成立。 此類 cache 只在「同 gate 控多 transistor 連到同 node」(例如 SRAM 內 6T cell 共享 word line)情境才有意義,而我們的 lowering 已合併這類結構。

---

【更全面的已知 dead-end pattern(請避免重蹈)】

新增到上次清單:
- **JIT/LLVM 已處理 cold layout**:`[NoInlining]` / `#[inline(never)]` 不會釋放 inline budget,只會引入 function call
- **NMOS 同 gate 重複罕見**:`TlistC1c2s` 內 lastGate cache 不適用
- **L1d 是硬限制**:任何擴大熱資料 size 的提案(generation counter u16/u32)會撞牆
- **Rust LLVM ≠ C# JIT**:branchless / inline 收益不可雙向移植
- **Walk size 平均 1.4 nodes**:bit-parallel BFS / SIMD scan 在小 list 上 setup cost 碾壓收益
- **D ~ 100/hc, BFS depth ~1.4**:priority queue 等動態排程在如此微小 set 上 PQ overhead 吃掉收益

---

【目前確認有效的優化(累積)】

- **Data layout**: NodeInfo SoA hot/cold split、64-byte aligned、ushort transistor_list (0-terminated)
- **Algorithm flattening**: iterative BFS (C#)、recursive BFS (Rust,LLVM 反向偏好)、雙緩衝 FIFO
- **Inline cascade**: 全 hot 路徑標記 AggressiveInlining / inline(always),outer loop 故意不標
- **Specialization**: Pure-logic-gnd fast-path、ROM/RAM handler split、ReadBits int[] 特化
- **Lowering**: S1.5 always-on shorts merge + dead transistor drop
- **Rust 特例 win**: VCC/GND hash shield + branchless XOR enqueue (+1.63%)
- **Compiler**: -C target-cpu=native (+2%,本機限定)

---

【現況基準】

- C# .NET 10: ~64.4K hc/s
- Rust LLVM: ~69.4K hc/s
- NES NTSC real-time: 42.95M hc/s (距離 ~620-670×)
- 兩端 checksum bit-identical `0x9B103E5E206E4C37`

---

【新一輪問題】

考慮上述失敗教訓,請給出**新的、未被上次討論的方向**:

1. 對於 Q3 Clock-phase wave 0,你還堅持 +15-30% 嗎? 在「state-dependent fanout 風險」+「Q5/Q6 預估全錯」的前提下,給一個現實的 ROI 範圍 + 哪些 cases 必須先用 sandbox sweep 驗證一致性。

2. 除了 Q1-Q6,**還有什麼是我們沒想到的**?(限制:不導入 IR/codegen/dispatcher,不破壞 event-driven BFS 結構,不導入 mass parallelism)

3. 我們已試 4 個 dead-end memory 的 pattern(計數器、bit-parallel、per-chip parallel、generation counter)── 是否你能識別出共通的「現代 CPU 上反直覺的反 pattern」並列出 5-10 個?(例如「不要用 X 替代 Y 因為現代 CPU 在 Z 條件下 X 反而慢」)

4. 給一個誠實的天花板評估:在 event-driven BFS + 純 switch-level 框架下,**C# 65K 和 Rust 70K 之上還有多少現實空間**?(避免你上一次「全部達成可到 100-120K」這種樂觀預測,請看實際失敗率後給個校正的數字)

請用 Markdown 回應,務必具體。 我會直接貼回專案。
