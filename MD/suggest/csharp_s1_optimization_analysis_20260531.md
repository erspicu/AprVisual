# C# S1 優化策略 — 深入再分析(2026-05-31, Opus 4.8 / max effort)

> 重新檢視 `src/AprVisual.S1`(C# 為主),在**不重提任何已證實 dead-end** 的前提下,找直攻已知瓶頸的新策略。
> 全部都還沒實作,需以 [[interleaved-paired-bench]] 量測決定採用。期望管理見文末。

## 0. 分析基礎(現況 + 瓶頸 + 禁區)

**已證實瓶頸(PMC)**:memory-latency bound,D-cache miss ~5.09%/cycle 主導;branch-mispredict 0.70%、I-cache 0.14%。L1d 32KB。
**熱迴圈**:`ProcessQueueInterp` 抽 dirty 節點 → `RecalcNode` 分流(fast-path / BFS)→ `SetNodeState` 走 fanout enqueue。平均一次 group walk ~1.4 節點;~70% recalc 結果是 singleton。
**L1d 競爭(關鍵)**:熱路徑同時碰多個 `byte[NodeCount]`(NodeCount≈14.7K):`NodeStates`、`_inGroup`、`RecalcHash`、`RecalcHashNext`、`IsPureLogic` ≈ 5×15KB = **75KB 擠 32KB L1d** → 必然互相 evict。這正是 5.09% D-cache miss 的可能來源之一。
**L2-resident 熱陣列**:`NodeInfos`(16B×14.7K≈240KB)、`TransistorList`(ushort×178K≈356KB)。dirty-set 以散亂 index 存取它們。

**禁區(已實測失敗,不重提)**:per-chip parallel(15×)、bit-parallel/Ligra BFS(156×)、RCM 重排(~1.0× / Rust −3~4%)、oblivious 全掃描(121×)、levelize(−15%)、prune-merge(黑屏 + 淨負)、counter fast-path(−6%)、generation-counter for `_inGroup`(−3.9%)、multi-queue dispatcher/Partition(−3.2%)、small-N SIMD on gnd/pwr scan、全面 branchless(noise/負)。
**鐵律(本 session 學到)**:在最熱 inline 函式「**加**」一個分支,可能 C# + 而 Rust −(codegen 密度差異)→ 偏好「不加分支、或用已載入資料的純計算取代記憶體讀取」的策略;逐引擎量測,不盲目同步。

---

## 1. 新候選策略 LIST(依預期價值排序)

### N1 — 消除 `IsPureLogic[]` 陣列,改從已載入的 `NodeInfo` 內聯計算 fast-path 資格 〔❌ 已試 2026-05-31:bit-exact 但 C# −1.4%,退回〕
- **想法**:`RecalcNode` 目前先讀 `IsPureLogic[nn]`(一個 `byte[14.7K]`)決定走 fast-path。但該分類是**靜態且執行期不變**的純函數:`TlistC1c2s==0 && (Flags & (HasCallback|ForceCompute|Pwr|Gnd))==0`(這四個 flag 都是 reset 期固定、runtime 不動;`TlistC1c2s` 靜態)。改成在 `RecalcNode` 內聯這個判斷 —— 而 `NodeInfos[nn]` 本來下一步(`RecalcNodeFast` 或 `AddNodeToGroup`)就要載入,可順手 `ref` 進來重用。
- **為何可能有效(直攻瓶頸)**:① **直接移除一個 15KB hot byte 陣列**(`IsPureLogic`)→ L1d 競爭從 ~75KB 降到 ~60KB(−20% byte-array 足跡),正中 constraint #7 / PMC D-cache;② 每次 recalc(~121M 次/200k hc)**少一次記憶體讀取**(`IsPureLogic[nn]`),換成對已在暫存器的 `NodeInfo` 做一個 AND+cmp。
- **正確性**:bit-exact。內聯式 = `ClassifyPureLogicNodes` 的完全相同謂詞;exclude 四 flag 為靜態、`TlistC1c2s` 靜態 → 每次呼叫結果與原 `IsPureLogic[nn]` 一致(supply 節點在 `RecalcNode` 開頭已 early-return,null 節點不會被 enqueue)。
- **為何不是 dead-end**:不是加分支 —— 是**用「已載入資料的純計算分支」取代「記憶體載入分支」**;不是 counter/state-caching;不是排程改動。是「砍掉冗餘的 per-node 陣列」這個方向,過去從沒做過。
- **風險/成本**:低。仍保留 `ClassifyPureLogicNodes` 只算 count(給診斷字串用),不配置/不讀陣列。唯一風險是 JIT 對「先 `ref` NodeInfo 再分流」的 codegen —— 需量測(預期正,因為省了 load)。
- **預期幅度**:可能是這份清單最大的一條(直接減 L1d 壓力 + 減 load)。樂觀 +2~5%,需實測;務必逐引擎量(Rust 同樣有 `is_pure_logic` Vec,可平行驗證)。
- **❌ 實測結果(2026-05-31,C#,interleaved-paired 24 輪,200k hc)**:checksum bit-identical(`0x9B103E5E206E4C37`)✅,但效能 **median −1.03% / trimmed −1.41% / exp 只贏 6/24 / median 配對差 −548 hc/s** → **淨負,已退回**。
  - **為何假設錯了(根因)**:① `IsPureLogic`(15KB)本來就穩在 L1d —— dirty-set 反覆訪同一批熱節點,該陣列一直是 warm,移除它沒鬆到真正的壓力(L1d-contention 假設不成立);② 內聯檢查給 ~82% 的 BFS-path 呼叫**多載入 16B 的 `NodeInfo`(常駐 L2)+ 複合分支(`TlistC1c2s==0 && (Flags&mask)==0`)**,比 baseline 的「1-byte L1 讀 + 單一比較」貴;JIT 未把 RecalcNode 的早載入與 AddNodeToGroup 的後載入完全 coalesce。
  - **教訓(歸入 dead-end)**:移除一個「雖熱但已穩在 L1d」的小 byte 陣列、換成對「更大的 L2 record」做複合檢查 = 淨負。又一次「cache-pressure 直覺不可信、最熱的不是最該動的」(同 counter-fastpath 類)。**不要再提這條。**

### N2 — software prefetch 下游 dirty 節點的 `NodeInfo`(`Sse.Prefetch0`)〔❌ 已試 2026-05-31:bit-exact 但 C# −1.5%,退回〕
- **想法**:`ProcessQueueInterp` 內層 `for i in 0..count: nn=RecalcList[i]; …RecalcNode(nn)` —— **下幾個要處理的節點 id 是已知的**(`RecalcList[i+PD]`)。在每次迭代開頭加 `Sse.Prefetch0((byte*)(NodeInfos + RecalcList[i+PD]))`(PD=prefetch distance,試 4/8),把那個節點的 16B 熱記錄提前從 L2 拉進 L1,隱藏 L2 延遲(~12 cycle)。可選同時 prefetch 其 `NodeTlistGates`/`TransistorList` 頭。
- **為何可能有效**:`NodeInfos`(240KB)是 L2-resident,dirty-set 以散亂 index 讀它 = 典型「pointer-chasing into a big array」。software prefetch 是這類存取的教科書解,**硬體 prefetcher 對散亂 index 無能為力**(這點 2026-05-29 Gemini 也指出),但 software prefetch 對**已知的下一個 index** 正好能補。
- **與「已被提過的 prefetch」的差異(關鍵)**:2026-05-29 `netlist_non_ir_optimization_review` 提的是 prefetch 長 `TlistC1c2s` 裡的 `NodeStates[futureGate]` —— **打錯目標**:`NodeStates` 只有 15KB、是 L1d-resident,prefetch 它沒用。本案 prefetch 的是 **L2-resident 的 `NodeInfos`**(完全不同的陣列),且從未實作量測。
- **正確性**:零影響(prefetch 是 hint,不改語意);無新分支、無新陣列。
- **風險**:中。dirty-set 常很小(幾百個),若該批節點已在 L1,prefetch 純屬浪費指令;PD 太短無效、太長污染。必須掃 PD ∈ {0(off),4,8,16} 量測。可能只在大 settle 波有效 → 也可只在 `count > 閾值` 時開(但那是加分支,謹慎)。
- **預期幅度**:不確定,+0~3%。是「直攻 memory-latency wall」最對症的工具,值得一試。
- **❌ 實測結果(2026-05-31,C#,PD=8,interleaved-paired 24 輪,200k hc)**:checksum bit-identical ✅,但效能 **median −1.37% / trimmed −1.51% / exp 只贏 1/24 / 中位差 −975 hc/s** → **淨負,已退回**。
  - **根因**:`NodeInfos` 對 dirty-set 而言**沒有「值得隱藏的 miss」** —— dirty set 反覆訪同一批熱節點,其 NodeInfo 已 warm(L2/L1)。prefetch 指令 + 邊界分支(`pf<count`)+ 索引讀的**每迭代開銷**,加在 ~121M 次熱迴圈上,純虧。**從第三個角度再次印證 math-algos「cache capacity 已滿足、不是瓶頸」**(RCM 1.04× / prefetch −1.5%,殊途同歸)。
  - **教訓**:在已達天花板的熱迴圈加**任何** per-iteration 工作(即使是 prefetch hint + 一個 well-predicted 邊界分支)都淨負。**不要再提這條。**

### N3 — `NodeInfo` 位元壓縮到 8 bytes(halve 240KB→120KB)〔MEDIUM / 投機〕
- **想法**:`NodeInfo` 現為 16B(`Flags` 1B + 3× `int` tlist 索引)。`TransistorList` 長 178,529 → 索引需 18 bits。把 `flags(8) + 3×18bit = 62 bits` 打包進一個 `ulong`(8B),`NodeInfos` 砍半。
- **為何可能有效**:`NodeInfos` 是 L2-resident 熱陣列;砍半 → L2 足跡 −120KB、每 cache line 容 8 個節點(原 4 個)→ dirty-set 觸碰的 line 數減半。
- **風險(正中鐵律)**:每次讀 tlist 索引要 shift+mask(1~2 cycle),加在**最熱迴圈**。這正是「C# + / Rust −」那類 tradeoff 的高危區。**必須逐引擎量**,很可能 C# 賺 Rust 賠。
- **正確性**:bit-exact(純佈局)。**預期**:不確定,可能 ±2%。列為投機,排在 N1/N2 之後。

### N4 — L1d cache-conflict-aware 配置(page coloring)〔➖ 前提實測不成立(2026-05-31),不追〕
- **❌ 前提檢查結果**:量了六個熱陣列的 base `mod 4096`(=L1d 8-way 的 way size):NodeStates=1152 / RecalcHash=3520 / RecalcHashNext=1216 / _inGroup=3776 / NodeInfos=2048 / NodeTlistGates=3648 —— **全部不同**,沒有任何兩個 base 同餘 4KB → 存取同一 index 落在不同 L1d set,**沒有系統性 conflict miss**。原因:`AllocAligned` 用 64-byte(非 page)對齊 + 各陣列大小不同 → base 自然錯開。**前提不存在 → 無標的 → 不實作。**
- **想法**:5.09% D-cache miss 未必全是 capacity miss,可能含 **conflict miss**:L1d 32KB / 8-way / 64B = 64 sets;若 `NodeStates`、`RecalcHash`、`RecalcHashNext`、`_inGroup` 的 unmanaged 配置位址恰好映射到重疊的 set,會互踢。刻意給各陣列加 set-spreading 的對齊 offset(`AllocAligned` 時加 padding),量 miss 率變化。
- **為何可能有效**:純佈局、零熱路徑改動、不加分支、不加陣列 —— 完全不踩鐵律;直接針對 D-cache。
- **風險**:低(改不對就是 0 效益);難在於要實際量 PMC 才知道有沒有 conflict miss。**預期**:不確定,+0~2%。
- **註**:這跟失敗的 RCM **不同** —— RCM 改 node id 順序(node 間 spacing),這裡改的是**不同陣列基底位址的 set 對齊**(陣列間 aliasing)。是兩回事。

### N5 — fast-path「單一 pull-down gate」內聯進 dead 的 `TlistC1c2s` 槽〔MEDIUM / 投機〕
- **想法**:fast-path 節點依定義 `TlistC1c2s==0` → 那個 4B 欄位是**死的**。多數 pure-logic 輸出只透過 **1 顆**電晶體下拉到 GND。把「恰好 1 個 gnd-gate、0 個 pwr-gate」的 fast-path 節點,其 gate id 直接塞進那個 dead 槽;`RecalcNodeFast` 對這類節點直接讀 gate(免 `TransistorList[TlistC1gnd]` 的 L2 間接)。
- **為何可能有效**:省掉常見情況的一次 L2 間接讀(`TransistorList`)。用「本來就浪費的空間」,不擴大 `NodeInfo`。
- **風險**:中。需多一個子分類 + `RecalcNodeFast` 多一條分支(踩鐵律邊緣)。需量。**預期**:+0~2%,投機。

### N6 — 明確 PGO / ReadyToRun / tiering 調校〔❌ 已試 2026-05-31:config 無 headroom〕
- **❌ 實測**:測了 `TieredCompilation=false`(強制 full-opt、無 tiering 暖機、但也無 dynamic PGO)—— 這是最能反映「預設 tiered+PGO 是否最佳」的 config 槓桿。結果 **median −0.42% / trimmed −1.11% / 配對 7/24 → 略負**。代表**預設的 tiered + dynamic PGO 已是最佳,config 沒有 headroom**(dynamic PGO 確實在幫忙,關掉它反而慢)。Visual NES 的 +15% 是 C++ static PGO;C# 的 JIT+dynamic PGO 已吃掉那塊。
- **現況**:csproj 無 `<TieredPGO>`/`<PublishReadyToRun>`/GC 設定;但 .NET 8+ **Dynamic PGO 預設開**,profile 也確認「21 inlinees with PGO data」→ steady-state benchmark 下 dynamic PGO 已生效。
- **可試**:① 明確 `<TieredPGO>true` + 確保量測前有足夠 warmup;② static instrumented PGO(較費工);③ `<PublishReadyToRun>` 看冷啟動。Visual NES 的 +15% PGO 是 **C++ static PGO**(無 JIT),C# 的 JIT+dynamic PGO 多半已吃掉那塊 → **headroom 預期小**。低優先,但量一次成本低。

### N7 — `[MethodImpl(AggressiveOptimization)]` 於 `ProcessQueueInterp`/`RecalcNode`〔➖ 已被 N6 涵蓋,不單獨追〕
- 強制直上 tier-1,省 tier-0 暖機。**但 AggressiveOptimization 會跳過 tier-0 instrumentation → 丟掉 dynamic PGO** —— 這正是 N6(`TieredCompilation=false`)測到的「關 tiering/PGO 略負」的 per-method 版。N6 已證實該方向略負,N7 同理 → **不單獨實作**。

---

## 2. 量測協定(務必遵守)
1. 每條獨立做、**interleaved-paired A/B**(base/exp 預建雙 binary、每輪輪流、median + trimmed-mean + 配對勝場)。
2. 每條都先驗 **checksum `0x9B103E5E206E4C37` @ 200k**(N1/N2/N4 應 bit-identical;N3/N5 也應 bit-identical)。
3. **逐引擎量**(C# 與 Rust 分開);N1/N3 在 Rust 有對應結構可平行驗,但結論可能反向(見鐵律)。
4. 採用門檻:配對顯著為正才留;noise/負一律 revert 並記入 dead-end。

## 3. 期望管理
- 既有結論:realistic ceiling C# ~72-75K(現 ~64-67K),那 12-16% headroom memory 註明「需 disassembly-driven micro-opt,非 AI 點子」。
- **N1(消除 IsPureLogic)與 N2(prefetch NodeInfo)正是這種 low-level、直攻 D-cache 的東西** —— 它們是「**收割那段 headroom**」的候選,不是「突破天花板」。天花板仍是 group-resolution 架構(要突破需 IR/AOT,而那條已驗證更慢)。
- 最該先做的兩條:**N1**(理由最硬:bit-exact、減 load、減 L1d 壓力、不加分支、不是任何 dead-end)、其次 **N2**。N3/N4/N5 投機,N6/N7 低。

## 4. 一句話
新訓練資料下,我認為最有價值的單一新點子是 **N1:把 fast-path 分類從 15KB 的 `IsPureLogic[]` 陣列查表,改成對「本來就要載入的 `NodeInfo`」做一個靜態謂詞的內聯計算** —— 它同時減少記憶體讀取與 L1d 競爭,正中 PMC 指出的瓶頸,且不踩任何已知地雷。建議先實作量測這條。
