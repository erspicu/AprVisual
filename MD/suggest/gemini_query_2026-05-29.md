我在做 NES (2A03 + 2C02) 開關級晶片模擬器,目前狀態:

【現況基準】(full_palette.nes, 200k master half-cycle)
- C# .NET 10: ~64.4K hc/s
- Rust LLVM: ~69.4K hc/s (target-cpu=native 可達 ~69.8K)
- NES NTSC real-time 需要 42.95M hc/s → 距離約 620-670×
- 兩端 NodeStates checksum bit-identical

【架構約束】
- 純 switch-level BFS 模擬,event-driven settle (recalcNodeList / ProcessQueue / RecalcNode → ComputeNodeGroup → AddNodeToGroup)
- L3 layout: NodeInfo SoA 拆 hot/cold、ushort transistor_list (0-terminated)、byte NodeStates/RecalcHash/_inGroup
- Fast-path: pure-logic-gnd 節點 (覆蓋 23%) 走 O(1) 查表
- 雙緩衝 FIFO settle, settle-stats max iter 觀察到 45 (full_palette) / 41 (SMB)
- D ~ 50-150 dirty nodes/hc, BFS walk size 平均 ~1.4 nodes
- 平均 ComputeNodeGroup per hc ~77 (扣掉 fast-path)

【硬約束】
**不導入 IR / codegen / dispatcher**。 過去經驗 AOT batch-eval 3-6× SLOWER 因為 batch re-evals ~14.7K nodes 而實際只 ~hundreds 改變。 必須保持「依當前 dirty set 做 BFS」的 event-driven 結構。

【已試 dead-end 邊界(請避開重蹈)】

效能負面:
- Branchless XOR enqueue (C#): -2.15% (predictor 已飽和)
- OR-all on early-break GND/PWR scans: C# -3.07%, Rust -1.86%
- Branchless shouldAdd mask in BFS (Rust Phase C / C# Phase E): -19.18% / -37.34%
- u16 generation counter for _inGroup (C#): -3.93% (L1d 撞牆: in_group byte→u16 14KB→29KB + NodeStates 14KB > 32KB L1d)
- byte generation counter (Rust): -1.65% (rollover too frequent + per-visit gen field read)
- Raw pointer iteration (Rust): -1.57%
- Counter FastPath (active_gnd/pwr_count maintenance): Rust -6%
- RCM node reorder for cache locality: -3-4%
- Per-chip rayon parallel: 15× SLOWER (per-wave work 太小)
- Bit-parallel Ligra-dense BFS: 156× SLOWER (walk size 平均 1.4)
- Dead-end skip (跳過 Gates.Count==0 nodes): 破壞 CPU bus state

效能 noise-level (已驗證落在 ±1-2% 量測噪聲內):
- c2 supply check fold (consecutive const range check): JIT 已自動 fold
- SetHigh/SetLow wrapper inline 標記
- #[inline] hint on recursive add_node_to_group (Rust)
- AlignedVec (Rust): node_hot/recalc_hash 已 64-byte 對齊

【已採納的(累積)】
L3 layout、NodeInfo SoA split、Fast-path、Inline cascade、ReadBits branchless、SetNodeState loop unswitch、NodeConnections 延後讀、Shield + branchless enqueue (Rust only)

---

【問題清單】── 都在「不走 IR」前提下:

Q1. **Walk-order 優先化**: 目前雙緩衝 FIFO,每 ProcessQueue settle 平均 15-20 wave 才收斂,max 觀察到 45。 有方法用 priority queue (depth-first / critical-path-first / fanout-cost-first) 減少 wave count 嗎? 必須維持 fixpoint convergence + checksum bit-identical。

Q2. **Long-list SIMD scan**: TlistC1c2s / TlistC1gnd 大多 1-5 entry,但 high-fanout (clock fanout、bus drivers) 有 30+。 針對「list length > 16」特化一條 AVX2/SSE2 路徑 (Sse2.LoadVector128 + Sse41.TestZ) 合理嗎? 還是分佈太偏短,setup cost 吃掉收益?

Q3. **Clock-phase static wave 0**: NES clock 每 12 master cycle toggle,toggle 後第一波 BFS propagation 是確定性的 (只依 clock fanout subnet)。 能否在 Reset() 預計算「clock 上升/下降後的初始 dirty set + 該批 group 的解析結果」並存成查表,runtime 直接套用? 這算 IR 嗎還是單純 memoization?

Q4. **Per-handler dirty-set 加速**: Memory handler 模式固定 (cs 變 → 讀 address → 寫 data_out)。 能否預計算「對某個地址 X,handler 寫入後會 dirty 的 node 集合」用 indexed lookup 取代 settle? 邊界是不引入 IR ── 依然執行 switch-level 模擬,只是 settle 結果靠 lookup。

Q5. **Hot/cold method 分離反向操作**: 過去用 [MethodImpl(AggressiveInlining)] / #[inline(always)] 擴大 inline,但 JIT/LLVM 有 IL/IR 大小門檻,有時 inline 太多反而失敗。 反向操作:把 cold path (例如 GetNodeValue 的 floating tie-break,<1% 走訪) 強制 NoInlining,讓 hot path inline budget 騰出。 有實測案例?

Q6. **High-fanout gate RLE**: Clock node fanout 100+ transistors,目前 transistor_list 存 (gate, c1) 對,gate ID 重複。 對 high-fanout gate 用 RLE [count, gate, c1₁..c1_count] 編碼,省 SetNodeState fanout loop 內的重複 gate read,跟 IR 無關純資料結構改。 有人試過嗎?

請對每個問題給:
1. 預估 ROI 範圍 (-X% to +Y%)
2. 主要風險
3. 是否撞既有 dead-end pattern
4. 實作概要

回答請用 Markdown,我會直接貼回專案文件做參考。
