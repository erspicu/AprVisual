# Fast-path / lowering 策略再探 —— Gemini 詢問 + 小心求證(2026-05-30)

使用者問:fast-path 偵測是否還有其他策略?lowering 也一起問。問了 Gemini(`gemini-3.1-pro-preview-customtools`,完整附上架構 + 8 條已知 dead-end + D-cache 瓶頸事實),然後**逐條對照實測數據與既有實驗紀錄求證**。結論:**沒有任何一個新點存活** —— 全部是「已做」「已證實 dead-end」或「真實覆蓋率 <1% 可忽略」。

## 為求證新量到的硬數據(full_palette 200k hc,instrumented build,量完已還原)

**recalc 呼叫分流**:fast-path 22.1M(**18.3%**)/ BFS 98.9M(**81.7%**)。
> fast-path 覆蓋 26.7% 的*節點*,但只佔 18.3% 的*呼叫* —— 非 fast 節點(匯流排)被重算得更頻繁。

**BFS 結果的 group 大小**:sz1=**63.0%**、sz2=30.1%、sz3=5.2%、sz≥4<2%。sz1–3 累計 98.2%。
> 換算:**全部 recalc 有 ~70% 結果是 singleton**(18.3% 靜態 + 51.5% 「BFS 白跑、最後只有自己」),只有 ~30% 真的需要多節點 group walk。

**靜態 c1c2 連通分量(各 live node 所屬分量大小)**:comp1=26.7%、comp2=**11.3%**、comp3=3.9%、comp4=1.7%、comp5=0.3%、**comp>5=56.0%**。
> 一半以上節點在大分量(資料/位址匯流排)。動態 30% 的 size-2 group 多半是大匯流排某 cycle 只連 2 個 —— **靜態封閉對只佔 11.3%**,固定大小 fast-path 抓不到那 30%。

**Supply-anchored 候選(gate==vcc 永導通 supply tie)**:**86 個節點 = 0.6%**。
> weak pull-up 早被折成 PullUp flag,非弱永導通 supply tie 幾乎不存在。

## 逐條求證

| Gemini 建議 | 判定 | 證據 |
|---|---|---|
| Multi-Queue 靜態分派(per-class FIFO,homogeneous drain) | ❌ 已試過、淨負 | = math-algos 的 Partition+Dispatcher;dispatcher overhead ~5% > 省下 0.4% = **−3.2%**。enqueue 比 recalc 多 ~10×,分派成本在 enqueue 端爆掉(同 dead-end #3) |
| Part1-1 Size-2/3 Island(靜態封閉對 O(1)) | ⚠️ 覆蓋有限 + 需 dead-end 機制 | 靜態封閉對僅 **11.3%**(comp2);resolution 仍要分支 → 撞 constraint #1;或靠上面的 dispatcher → 撞已知 −3.2% |
| Part1-2 Driven-Stub Leaf(L 延遲給 parent) | ❌ 已證實壞 CPU | **dead-end L8 / #5**:「stub-removal at lowering broke CPU via floating tie-break」「gate-less leaf 仍貢獻狀態給被拉入的 group」 |
| Part2-1 Hot/Cold 結構拆分 | ✅ 已做 | R1:Rust `NodeHot`(16B)+ 分離 `node_tlist_gates`/`node_connections`;C# `NodeTlistGates`/`NodeConnections` 已分離。8B packing 受 TransistorList offset 範圍限制 |
| Part2-2 Supply-anchored merge | ❌ 覆蓋可忽略 | 實測候選 **0.6%**(86 節點),不值得實作 + 風險 |
| **Part2-3 RCM / graph-locality 重編號** | ❌ **已試過、無效** | `--rcm` 實測 **1.04×(boot)/ 0.98×(穩態)**;Rust **−3~4%**(memory `rust-port-best-config`)。根因:S1 是 dirty-set 跳躍式 sparse 存取,working set 已塞進 L1d/L2,cache **capacity** 已滿足,reordering 改不了 |
| Part2-4 平行電晶體壓縮 | ❌ 大致已做 | lowering 已 re-dedup `(gate,c1,c2)`;不同 gate 平行無法合併 |

## 結論

Fast-path / lowering 的空間已**窮盡**。本輪最大價值是**求證攔截**:Gemini 排第一的 lowering 點(RCM)正是 math-algos 已實作又剪掉的失敗實驗(1.04×/0.98×),差點被當「新點」重做。與既有天花板結論一致(realistic ceiling C# 72–75K / Rust 78–82K,見 memory `hotpath-ceiling-and-antipatterns`)。

**未來查詢務必附上本輪的分布數據**(70% singleton、comp>5=56%、supply-anchored 0.6%、static comp2=11.3%),否則 Gemini 會一再提 Size-2 Island / supply-merge / RCM。
