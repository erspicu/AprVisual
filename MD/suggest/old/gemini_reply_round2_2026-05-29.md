這是一次非常漂亮且殘酷的實測。感謝你提供精確的數據和根因分析。你完全正確，我的前一輪預估（特別是 Q5 和 Q6）基於過時或不適用的假設，在現代 JIT/LLVM 最佳化機制和 NMOS 特定的物理拓樸下，變成了純粹的 overhead。

被狠狠打臉後，我已經更新了對你專案架構的「世界觀」：**這是一個 ALU 運算極少、高度依賴 L1d Cache 命中率、且 Branch Predictor 經常被不規則拓樸擊敗的極端場景。任何增加分支或擴大記憶體 footprint 的操作都是毒藥。**

以下是針對你四個問題的重新校準與具體回覆：

---

### 1. 對於 Q3 Clock-phase wave 0 的重新評估與驗證

**我的修正預估：+2% to +8%（高風險，極可能為負或觸發 L1d 效應）**

我撤回 +15~30% 的樂觀預估。基於 Q5/Q6 的教訓，Q3 有兩個致命缺陷：
1. **靜態 Mask 會擠壓 L1d**：預先儲存 Clock High/Low 的 wave 0 靜態狀態（哪些節點被強制拉高/拉低），需要額外的陣列查詢，這會與現有的熱資料（NodeInfo/State）競爭 L1d cache line。
2. **State-dependent fanout 破壞靜態性**：時脈訊號打開一個 Pass Gate 時，節點電壓是否傳遞，取決於閘道兩端的**動態電壓狀態**與**電容值（大小節點）**。靜態展開無法預知這種動態電荷分享（Charge Sharing），一旦靜態與動態邏輯割裂，為了維持精確度而做的「狀態檢查補償代碼」，其代價會輕易吃掉省下的 BFS enqueue 成本。

**如果仍要嘗試，Sandbox Sweep 的必做一致性驗證：**
*   **Charge Sharing 驗證**：時脈驅動 Pass Gate 導通時，若兩端都是 High-Z（浮接）但帶有不同電荷，Wave 0 能否正確計算最終電壓？（通常靜態 Mask 辦不到，必須退回動態）。
*   **Bidirectional Artifacts**：確保時脈強制拉高的節點，不會在現實中因為另一端接 GND 而導致短路（靜態預先拉高可能會暫時產生非法的 VCC-GND 貫穿電流狀態）。
*   **一致性捕捉**：建立一個 shadow state，每一個 half-cycle，跑一次標準 BFS，再跑一次 Wave 0 + 殘餘 BFS，逐 bit 比對所有 Node State。

---

### 2. 全新的優化方向（無 IR、保持 BFS、無大量平行化）

既然演算法層面已經被你搾乾，剩下的空間在於**資料的「隱式路由」與「拓樸巨集化」**。

**新方向 A：Node ID 分區編碼 (ID Partitioning for Implicit Routing)**
*   **概念**：不要讀取記憶體來判斷節點類型，而是透過 ID 數值的區間來做 branch。
*   **做法**：在資料預處理階段，重新編排 Node ID：
    *   ID `0 ~ 999`: Pure-Logic 節點 (Fast path)
    *   ID `1000 ~ 1999`: 需要 Charge Sharing 計算的類比節點
    *   ID `2000+`: 外部引腳 / 特殊 Handler 節點
*   **收益**：把原本 `if (nodeInfo[id].Type == PURE_LOGIC)` 這種需要 L1d load 的條件，變成 `if (id < 1000)` 的暫存器比較。完全消滅部分 metadata 讀取。

**新方向 B：拓樸巨集合併 (Topology Macro-Merging / Super-Nodes)**
*   **概念**：雖然我們不編譯 IR，但在讀入 netlist 時，可以把常見的電晶體組合（例如 NMOS Inverter：一個 Pull-up 接 VCC，一個 Pull-down 接 GND，由同一個 input 控制）在**靜態資料結構上**合併為一個「Super-Transistor」。
*   **做法**：在 `TlistC1c2s` 中定義特殊的 `other` 標記（例如利用 MSB 標示這是一個巨集）。當觸發此 gate 時，BFS 直接將 output 節點寫入反相狀態，省去經歷 VCC/GND 的兩次電晶體 traversal。這不破壞 event-driven 模型，只是壓縮了圖的深度。

**新方向 C：同態狀態 Bypass (Iso-State Culling)**
*   **概念**：如果一個 Gate 翻轉為 ON，但它兩側的源極和汲極**目前處於相同的邏輯電壓（例如都是 5V）**，那麼這個電晶體導通「不會改變任何狀態」，直接 drop，不要 enqueue 另一端。
*   **做法**：在檢查 `gate == ON` 之後，`if (state[c1] == state[c2]) continue;`。這是一個極為廉價的暫存器讀取，卻能斬斷大量無效的 BFS 擴散。

---

### 3. 現代 CPU 上反直覺的反模式 (Anti-patterns)

從你的實測與現代微架構來看，以下是 5 個導致優化變毒藥的核心反模式：

1.  **「狀態快取」反模式 (The State-Caching Fallacy)**
    *   *誤區*：用額外的記憶體（如 last-gate cache, generation counter）記錄狀態以避免重複計算。
    *   *現實*：現代 CPU 的 ALU 是「免費」的（單週期內可並發多個整數運算），但 L1d cache 容量極小且 load-use penalty 有 3-5 週期。**增加 memory footprint 來省 ALU 運算，在現代 CPU 上通常是負優化。**
2.  **「微觀條件分支」反模式 (The Micro-Branch Trap)**
    *   *誤區*：寫一大堆 `if (特殊情況)` 來 short-circuit 邏輯，覺得可以省下後面的程式碼執行。
    *   *現實*：在高度資料相依的模擬（如 Switch-level）中，Branch Predictor 命中率不會是 99%。一個 mispredict 扣除 15-20 cycles。**盲目覆寫（Blind Overwrite）或執行冗餘的輕量計算，永遠快於充滿分支的智慧型跳過。**
3.  **「SIMD 強行封裝」反模式 (The Small-N SIMD Delusion)**
    *   *誤區*：看到 array 操作就想用 SIMD (AVX/NEON) 或 bit-parallel 加速。
    *   *現實*：當 `N < 8`（你的 Walk size 平均才 1.4），將純量搬進 Vector Register 的 setup cost（甚至跨 domain delay）遠大於純量執行完畢的時間。SIMD 需要連續、龐大的吞吐量。
4.  **「手動編譯器微操」反模式 (The Compiler Micromanagement)**
    *   *誤區*：人工標註 `[NoInlining]` 或是手動 hot/cold 分離來「幫助」編譯器。
    *   *現實*：LLVM 和 RyuJIT 具備全域視野與 PGO 啟發。手動干預經常破壞它的 register allocation heuristic 或強迫生成 stack frame。**最佳化資料的 Memory Layout (SoA/Alignment)，把 Code 生成交給編譯器。**
5.  **「無鎖並發幻想」反模式 (The Fine-grained Parallelism Illusion)**
    *   *誤區*：用 lock-free queue 或 per-chip parallel 分攤工作。
    *   *現實*：跨 Core 的 Cache coherence (MESI protocol) 通訊延遲大約在 40-100ns 級別。如果一個 event loop 的 iteration 只有幾十奈秒，通訊成本直接碾壓運算收益（Amdahl's law 的極端體現）。

---

### 4. 誠實的天花板評估 (Honest Ceiling Assessment)

基於「純 event-driven BFS + switch-level + 無 IR 展開」的死規定，我們來算一下物理極限：

*   目前 Rust LLVM 跑到 `~69.4K hc/s`。
*   假設每個 half-cycle 平均觸發 100 個動態節點 (D ~ 100)。
*   雙晶片 (CPU+PPU) = 100 * 2 = 200 個 nodes/hc。
*   每秒評估的節點數：`69,400 * 200 = ~13.88 百萬 nodes/sec`。
*   以現代 5GHz CPU 來說，每秒約 200~250 億個指令週期。這意味著**目前每個 Node evaluation 消耗了將近 1500~1800 個 CPU cycles**。

這聽起來很離譜，對吧？但時間去哪了？
**去 L1/L2 Cache Misses 和 Pipeline Flushes（分支預測失敗）了。**

Switch-level BFS 本質上是「pointer-chasing」（藉由 array index 跳躍），它的記憶體存取模式完全是隨機的。現代 CPU 的 prefetcher 對這種隨機圖遍歷毫無招架之力。每一次 L2 miss 就是 15 cycles，L3 miss 就是 50+ cycles，如果遇到 Branch mispredict 又要再清空 pipeline。

**【校正後的天花板預測】**

在不改變「動態圖遍歷」本質的前提下，你已經撞到了 **CPU Memory Subsystem Latency Wall**。你現在能榨取的，只剩資料結構對 L1 Cache line 的極致利用率。

*   **C# 極限估計：72K - 75K hc/s**（受限於 .NET 陣列邊界檢查與相對保守的 alias analysis）。
*   **Rust LLVM 極限估計：78K - 82K hc/s**（假設完美榨乾資料佈局與 branchless 邏輯）。

**結論**：在現有框架下，**絕對不可能靠小修小補達到 100K 甚至 42.95M (Real-time)。** 現實空間大概只剩下 **10% ~ 18%** 的提升餘地。要跨越這個數量級的鴻溝，唯一的物理途徑就是將動態遍歷 (Dynamic Traversal) 轉為靜態編譯 (AOT Compilation / IR generation)，強迫將資料流固化成連續的 CPU 指令流。