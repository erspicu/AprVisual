這是一份針對你目前 NES 晶片開關級模擬器 (Switch-level Simulator) 瓶頸的深度分析與實作建議。

在「不導入 IR / Codegen」且「維持現有 Event-driven BFS」的硬約束下，你已經把微架構與記憶體佈局（SoA、雙緩衝、Fast-path）壓榨到了極限。距離 42.95M hc/s 的 600 倍差距，意味著**純粹的指令級最佳化 (Instruction-level optimization) 已經不夠，必須往「演算法級的子圖跳躍 (Subgraph bypassing) 與記憶體存取折疊 (Memory access folding)」發展**。

以下針對你的 6 個問題逐一分析：

---

### Q1. Walk-order 優先化 (Priority Queue 取代 FIFO)
**核心概念**：改變走訪順序以減少反覆震盪，降低 Wave count (目前 max 45)。

1. **預估 ROI**：**-15% 到 -5% (高度負面)**
2. **主要風險**：Priority Queue (例如 Binary Heap) 的插入/拔出成本是 $O(\log N)$。你每半週期平均只有 50-150 個 dirty nodes，BFS walk length 才 1.4。在如此微小的資料集上，維護 PQ 的演算法常數時間（Branching, 記憶體跳躍）絕對會吃掉減少 Wave 帶來的收益。
3. **撞牆檢查**：這與你失敗的「RCM node reorder」類似——過度聰明的動態排程會破壞極簡迴圈的優勢。
4. **實作概要 (替代方案：Static Topological Sort)**：
   * **絕對不要用動態 PQ**。
   * **改用靜態 ID 重新分配**：在離線階段，對整個晶片做拓撲排序 (Topological Sort)。將源頭 (Source/Driver) 的 Node ID 設為小數值，末端 (Receiver) 設為大數值。
   * 你的 FIFO enqueue 邏輯保持不變，但在 `ProcessQueue` 內，將目前的 Dirty Array **以 Radix Sort (如果極小則用 Insertion Sort) 或單純的 BitSet 順序掃描** 來取代 FIFO 取出。確保同一 Wave 內「ID 小的先執行」。這只需極小的額外成本，就能達到 Depth-first / Dataflow-first 的效果。

---

### Q2. Long-list SIMD scan (針對 high-fanout 的 AVX2/SSE2 特化)
**核心概念**：針對長度 > 16 的 transistor list 走 SIMD。

1. **預估 ROI**：**-2% 到 +3% (雜訊級別，吃力不討好)**
2. **主要風險**：
   * **分支預測懲罰**：每次都要判斷 `if (length > 16)`，破壞了原本純量迴圈的流暢度。
   * **Setup 成本**：將 NodeStates Gather 到 SIMD 暫存器，執行 `TestZ` 或 `MoveMask`，再用 `tzcnt` 取出 index 的 setup 成本，對於長度只有 30 的 list 來說，可能不比一個預測極佳的純量迴圈快。
3. **撞牆檢查**：類似「Branchless shouldAdd mask」，試圖用寬度取代分支，但在資料量不足時被 CPU 的預測器打敗。
4. **實作概要 (若真要試)**：
   * 不要在 Runtime 判斷長度。在 `NodeInfo` 的高位元藏一個 Flag `IsLongList`。
   * Enqueue 階段將 LongList 節點分流到另一個專屬的 `ProcessLongListQueue`，集中批次處理 SIMD，避免與短 List 混用同一個迴圈。

---

### Q3. Clock-phase static wave 0 (時脈觸發的初始傳播查表)
**核心概念**：Clock node 翻轉後的第一波傳播是確定性的，直接查表覆蓋。

1. **預估 ROI**：**+15% 到 +30% (強烈建議)**
2. **主要風險**：邊界定義錯誤導致 Checksum 破壞。必須保證被 Memoize 的子圖**絕對沒有**其他非 Clock 的動態輸入（例如內部 Latch 的回授）。
3. **撞牆檢查**：這**不算 IR**，這是「巨集節點 (Macro-node)」或「局部子圖記憶 (Memoization)」，完美契合你成功過的 Pure-logic-gnd Fast-path 策略。
4. **實作概要**：
   * **Init 階段 Sandbox**：將 NES 放在乾淨狀態，手動 Toggle `clk`，記錄**前 2 個 Wave** 內所有「只依賴 clk」而改變的 Node ID 與其最終 State。這些節點通常是 Clock splitters (phi1, phi2) 和基礎時序邏輯。
   * **產生 LUT**：建立兩組 Array：`ClkRise_Updates[(NodeID, State)...]` 與 `ClkFall_Updates[...]`，以及它們觸發的 Wave 2 Dirty 邊界。
   * **Runtime**：當 Master half-cycle 觸發時脈節點時，**跳過 BFS**，直接用一個極短的迴圈將 `Clk_Updates` 寫入 `NodeStates`，並將邊界 Nodes 塞入 Queue 作為 Wave 1 的起點。

---

### Q4. Per-handler dirty-set 加速 (Memory/I/O 的查表)
**核心概念**：將 Memory Handler 的連續讀寫動作視為一個固定模式的 Dirty Set 注入。

1. **預估 ROI**：**+5% 到 +10% (有條件的正面)**
2. **主要風險**：L1d Cache 污染。如果預先計算的表太大（例如 `Address(16-bit) x Data(8-bit)` = 16MB），查表的 Cache Miss 成本會完全抹殺掉省下 BFS 的時間。
3. **撞牆檢查**：必須避開「u16 generation counter」遇到的 L1d Cache 撞牆慘劇。查表結構必須極小。
4. **實作概要**：
   * **不要紀錄 Data Payload 的完整結果**，而是紀錄**結構**。
   * 記憶體寫入時，變化的只有 Data Bus 上的 Pads 和 R/W pin。
   * 建立一個 Fast-path：`ApplyMemoryWrite(ushort addr, byte data)`。
   * 程式碼內**Hardcode / Unroll** 對這 8 個 Data Pin Node 和 R/W Node 的狀態寫入，並「直接」將這些 Pads 預先知道的 First-layer receivers 塞入 FIFO。
   * 避開 `SetNodeState` 裡的一般化邏輯，用特製的迴圈直接對匯流排引腳做 Bulk-update。

---

### Q5. Hot/cold method 分離反向操作 (強制 Cold Path 不 Inline)
**核心概念**：釋放 Hot path 的 Inline budget，降低 Instruction Cache 壓力與暫存器溢出 (Register Spill)。

1. **預估 ROI**：**+2% 到 +6% (穩定且零風險)**
2. **主要風險**：無。這是現代高效能 C#/Rust 的標準操作。
3. **撞牆檢查**：這與過度 Inline 的失敗經驗剛好互補。
4. **實作概要**：
   * **找出 Cold Path**：例如 `GetNodeValue` 遇到 Floating 需要爬 Transistor tree 判定 Pull-up/Pull-down tie-break 的邏輯 (發生率 < 1%)。
   * **C# 實作**：
     ```csharp
     [MethodImpl(MethodImplOptions.AggressiveInlining)]
     byte GetNodeValue(int id) {
         if (likely_condition) return FastResolve(id);
         return GetNodeValueCold(id); // Only pass necessary args
     }

     [MethodImpl(MethodImplOptions.NoInlining)]
     byte GetNodeValueCold(int id) { /* Complex tie-break logic */ }
     ```
   * **Rust 實作**：
     ```rust
     #[inline(always)]
     fn get_node_value(&self, id: u16) -> u8 {
         if likely_condition { return self.fast_resolve(id); }
         self.get_node_value_cold(id)
     }

     #[cold]           // 關鍵：提示 LLVM 將此代碼放到冷的記憶體分頁
     #[inline(never)]  // 絕對不 inline
     fn get_node_value_cold(&self, id: u16) -> u8 { ... }
     ```

---

### Q6. High-fanout gate RLE (節點串列結構壓縮)
**核心概念**：減少 Fanout loop 內因為重複讀取相同 Gate ID 而造成的記憶體存取。

1. **預估 ROI**：**+3% 到 +8% (取決於實作的輕量化程度)**
2. **主要風險**：解碼 RLE (判斷這個是 count 還是 node_id) 引入的分支，可能會吃掉省下記憶體讀取的優勢。破壞了現有 `ushort` 0-terminated 的極速純量迴圈。
3. **撞牆檢查**：引入過多中介狀態 (State machine) 的解碼邏輯通常會拖慢 CPU。
4. **實作概要 (建議改用 Soft-RLE / 排序快取法)**：
   * **不要改用複雜的 `[count, gate, c1, c1...]` 結構**。
   * 維持現有陣列格式 `(gate, c1)`，但在離線建立模擬器時，**對 `transistor_list` 依照 `gate_id` 進行排序**。
   * **Runtime 優化**：利用軟體層面的變數來快取前一次的 gate 狀態，消滅重複的 Array lookup：
     ```csharp
     ushort lastGateId = 0xFFFF;
     byte lastGateState = 0;

     for (int i = 0; ; i += 2) {
         ushort gate = list[i];
         if (gate == 0) break; // 0-terminated
         ushort c1 = list[i+1];

         byte gateState;
         if (gate == lastGateId) {
             gateState = lastGateState; // 直接命中暫存器，0 記憶體讀取！
         } else {
             gateState = NodeStates[gate];
             lastGateId = gate;
             lastGateState = gateState;
         }
         // Apply gateState to c1...
     }
     ```
   * 這種方法保留了硬體 Prefetcher 最愛的線性 Scan，又達到了 RLE 省讀取次數的目的。

---

### 總結策略建議

要在不破壞硬約束的前提下推進效能，你目前的破局點在於 **Q3 (時脈巨集節點)** 與 **Q6 (利用資料排序的暫存器快取)**。

1. **立刻實作 Q5 (Hot/Cold 分離)**，這是不用動架構的純粹效能紅利，能讓 CPU 的 L1i Cache 更健康。
2. **實作 Q6 的「排序 + lastGate Cache」**，取代複雜的 RLE 結構修改，這能有效降低 high-fanout 的記憶體頻寬。
3. **投資 Q3 (Clock-phase Static Wave)**。模擬器中通常有 30%-40% 的運算力耗費在推動 Clock 網路本身的充放電。把 Clock tree 的第一階段傳播用預先算好的 Mask 與 State lookup 直接覆寫，是你能達到大幅度 (10%+) 躍進的最佳途徑，且這符合 Switch-level 模擬的精神，只是將計算時間提前到了 Init 階段。

*(註：即便這些微優化全數實作且達到最佳 ROI，效能極限可能提升 50% ~ 80%，達到 100K-120K hc/s。距離 42M hc/s 的 600x 差距，在「純 Event-driven BFS + 完整節點解析」的數學極限下是難以跨越的。未來的某個階段，你可能仍需妥協，將晶片內部非耦合的純組合邏輯區塊 (Combinational clouds) 轉換為 LUT 或方程式，才能迎來 100x 等級的爆發。)*