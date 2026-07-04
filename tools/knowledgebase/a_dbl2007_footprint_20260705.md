這是一個非常硬核且經典的電晶體級模擬器設計問題。在離散事件模擬（Discrete Event Simulation, DES）或開關級模擬（Switch-level simulation）中，這被稱為**探針效應（Probe Effect）**或**觀測者效應（Observer Effect）**。

你們所遭遇的困境：**「為了修復 A 測試引入的結構改變，擾動了圖遍歷順序，導致無關的時序競爭 B 測試翻車」**，是 BFS 拓樸收斂引擎最常見的痛點。

以下針對您的問題進行深度分析與具體建議：

---

### 1. 方案排序與評估 (通則性、風險、侵入度、效能)

從長遠的引擎健康度來看，排序如下：**C > A > B** （稍後我會提出更好的**方案 E**）。

#### **第一名：方案 C (編號穩定的掛載 / Tail Allocation)**
*   **通則性**：高。這是從「圖結構」層面徹底消除探針效應的正則解法。
*   **正確性風險**：極低。只要確保 0 ~ N 的原本節點 ID 與 Adjacency List 順序完全不動，BFS 遍歷原始圖的展開樹與佇列順序在數學上保證 100% 同構。
*   **侵入度**：中等。需要修改載入期的 `class-major` 重編號器。
*   **效能**：極佳。與方案 B 相同，都是原生的圖運算，無額外開銷。
*   **結論**：這是最符合 Visual6502 哲學的系統性解法。主晶片是 immutable 的前段陣列，測試儀器永遠分配在陣列尾端（Tail IDs）。

#### **第二名：方案 A (Runtime 注入 Gnd 旗標)**
*   **通則性**：中高。不需要動圖，直接操作狀態。
*   **正確性風險**：中等。繞過 API 直接寫入引擎內部標記，容易引發狀態不同步（見第 2 題詳述）。
*   **侵入度**：低。完全在現有的 per-hc shim 鏈中運作。
*   **效能**：高。僅在特定 hc 進行位元運算與 enqueue。
*   **結論**：作為一個 pragmatic (務實) 的解法，這很誘人，但它是個「引擎後門」，需要極端嚴格的紀律。

#### **第三名：方案 B (標準儀器圖 / Always-attach)**
*   **結論：強烈不建議**。這是一種「打地鼠」策略（Whack-a-mole）。你這次重新校準了 141/4，下次為了解決 APU 的某個 bug 又加了 3 個假節點，難道要再重擲一次骰子？這會讓測試基線失去「收斂性」，每次修改都在破壞穩態。

---

### 2. 方案 A 的疑慮與陷阱分析

您對方案 A 的三個疑慮**全部成立，且非常致命**：

1.  **ForceCompute 特例**：如果 db 節點在某些極端匯流排衝突下（或某些未知測試中）被捲入包含 FC 標籤的群組，Gnd 旗標確實可能被剝離，導致你的 shim 在特定瞬間「幽靈失效」。
2.  **Save/Restore 紀律（狀態洩漏風險）**：這是最大陷阱。如果你在 wave 中途觸發了某種異常，或者釋放條件的邏輯有漏洞（例如 CPU 突然被 Reset 導致預期的 state machine 沒走到），`NodeFlags.Gnd` 可能永遠殘留在該節點上。假電晶體機制之所以安全，是因為「開關狀態」受控於另一個節點，會自然參與網路穩態；直接改 Flag 則是孤立狀態。
3.  **增量重算與快取陷阱（Enqueue 漏報）**：如果你直接修改 `NodeInfo.Flags`，即使你手動 `+enqueue+settle`，但如果引擎底層有**群組快取 (Group Cache)** 或 **前置狀態判斷 (State deduplication)**，它可能認為「電晶體拓樸沒變」，而跳過或優化掉你的 Gnd 傳播。
4.  **額外陷阱：Double-buffer Settle**：如果引擎為了並行化或防止震盪，使用了 Ping-Pong buffer (讀舊態、寫新態)，直接修改旗標可能只改到了 Front buffer，而在下一次迭代被 Back buffer 覆蓋。

**結論**：如果不用方案 C，方案 A 必須包裝成嚴格的 API（例如 `ForceInjectGnd(node)`），並由引擎的 rollback/cleanup 機制統一管理。

---

### 3. 第五種做法 (強烈建議)：方案 E —— 擴充 LUT，新增 `TestOverride` 儀器位階

回顧您的背景說明，您一開始之所以必須加「假電晶體」，是因為：
> *「實測過而失敗的介入 (1)：用 SetLow/SetHigh 力 db —— 輸給 Pwr/Gnd，外力位階不夠。」*

在 EDA 工具 (如 Verilog VPI 或 SPICE) 中，解決這類問題的標準做法**不是改變 DUT (被測設備) 的網表，而是提供具有「絕對覆蓋權」的測試力 (Force/Release API)。**

你們目前的 LUT 優先順序是：
`Gnd > Pwr > SetHigh > SetLow > PullUp > State(hold)`

其中 `SetHigh/SetLow` 被行為層 (RAM/ROM) 使用，位階低於真實電源是正確的（否則匯流排衝突時 RAM 會燒毀/覆蓋真實晶片的 Gnd）。

**但測試儀器需要更高的特權。**
您可以修改引擎的 LUT 與 NodeFlags，加入 **`InstrumentGnd`** 和 **`InstrumentPwr`**（或稱 `ForceLow / ForceHigh`）：

**新的 LUT 優先序：**
`InstrumentGnd > InstrumentPwr > Gnd > Pwr > SetHigh > SetLow > ...`

**實作方式：**
1. 引擎 API 新增 `InstrumentClamp(node, state)` 與 `InstrumentRelease(node)`。
2. shim 啟動時，呼叫 `InstrumentClamp(cpu.db_i, LOW)`。
3. 引擎內部將 `InstrumentGnd` flag 加入該節點，並 enqueue。
4. 因為 LUT 優先序最高，無論該群組有沒有真實的 Gnd/Pwr，它都會被強制壓制為 Low。
5. shim 結束時呼叫 `InstrumentRelease`。

**優點：**
*   **完全零圖變更**（解決探針效應、保住方案 C 的好處）。
*   **不依賴假電晶體**，不會引入奇怪的群組融合（保住方案 A 的好處，且比方案 A 安全，因為這成為了第一級的引擎 API，不再是 backdoor）。
*   未來處理任何時序彩票競爭，測試腳本擁有「上帝之手」可以直接干預波形，而不用每次都想辦法掛載假電晶體。

---

### 4. 關於 Per-test Scoping (測試範圍隔離) 的哲學探討

User 覺得「每個測項一個布林開關」不夠通則，這種直覺是**完全正確**的。

從「模擬器測試方法學」來看，Test-specific hacks 是一種**技術債**。
1.  **過度擬合 (Overfitting)**：如果一個 shim 只在 `double_2007_read` 啟動，你其實並沒有解決「NES 晶片如何處理這種邊緣情況」的問題，你只是寫了一個 `if (test == A) return correct_answer;` 的高級版本。
2.  **掩蓋系統性缺陷**：`double_2007_read` 暴露的本質問題是：「Quiescent-settle 離散引擎缺乏**傳播延遲 (Propagation Delay)** 的概念」。真實晶片中，PPU Buffer reload 傳遞到 CPU A 暫存器需要 RC 延遲，這段時間差讓 CPU 鎖存到了舊值。

**因此，正確的態度應該是：**
這個 shim 是用邏輯手段去**彌補引擎缺乏實體時間延遲**的補丁。既然它是實體延遲的抽象，它就**應該是全局生效的 (Globally applied)**。如果這個「代表傳播延遲的 shim」破壞了別的測試，代表我們對該延遲的理解還不夠透徹，或者探針效應干擾了實驗。

**總結建議路徑：**
1. **短期 & 最佳架構解**：實作 **方案 E (新增 LUT 儀器最高位階 API)**。它能在完全不改變圖形結構（零探針效應）的情況下，全域開啟這段修復延遲的 shim。
2. **退而求其次**：實作 **方案 C (Tail Allocation)**。將所有測試儀器節點的 ID 延後分配，保證黃金主網表的 ID 絕對穩定，徹底隔離觀測者效應。
3. **廢棄**：停止使用 Per-test flag 控制儀器掛載，因為這會讓你們永遠無法確定模擬器作為一個「整體」是否完備。