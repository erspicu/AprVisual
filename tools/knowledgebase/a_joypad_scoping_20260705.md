這是一個極度精彩的架構問題。它觸及了硬體模擬與自動化測試中最核心的矛盾：**「待測物（DUT）的物理不變性」與「測試儀器的侵入性」之間的衝突。**

針對你提到的「同套通則檢驗」，我們先給出一個核心定調：
**這次的 `per-test scope` 是一半對、一半錯。** 對於「拔插手把」，per-test 在概念上是合理的；但對於「修復主機板拉線錯誤」，per-test 則是標準的過度擬合（Overfitting）。

以下我將用同一套通則，為你逐一拆解這兩項變更，並給出架構級的解答。

---

### 1. per-test 這次對不對？（結構變更 vs 值覆寫）

**解答：對「手把置換」是概念正確但實作錯誤；對「tie 改接」則是完全錯誤。**

我們要嚴格區分 **待測物 (DUT, Device Under Test)** 與 **測試治具 (Fixture/Instrument)**。

*   **Tie 改接（6條 vss→vcc / 浮接）：這是 DUT 修正。**
    真實的 NES 主機板上，那 6 根 pin 本來就是浮接的。原先的 board def 把它綁死在 GND 是**建模瑕疵**。修復主機板瑕疵，必須是**全域生效 (Global)**。如果你只在 7 個測試中修正主機板，等於你用了一張「特製的錯版主機板」去跑其餘 138 個測試，這就是過度擬合。
*   **模組置換（手把）：這是 Test Fixture 變更。**
    在真實世界中，手把本來就是「可拔插的外部設備」。有些測試不插手把，有些測試插手把，所以 `EnableJoypadHandler` 作為 `per-test scope` 在邏輯上是**完全正確**的。
    **但是**，你們目前的實作，是把這個「外部設備的置換」做成了「主圖中段的載入期變更」，從而引發了探針效應（破壞了 DUT 內部的 BFS 順序），這是實作層面的瑕疵。

---

### 2. 模組置換能否零擾動？（The Socket Pattern 方案）

**解答：可以。你需要將「Tail Allocation」升級為「插座模式 (Socket Pattern)」。**

既然手把是外部設備，它的載入與置換就**不應該**干擾主晶片與主機板的節點編號。目前的置換會引發重編號，是因為你們在「圖的中段」取代了它。

**具體做法（The Socket Pattern）：**
1.  **兩階段載入 (Two-Phase Initialization)：**
    *   **Phase 1 (DUT 載入)：** 永遠只載入 NES 主機與 Controller Port（插座邊界）。此時**完全不載入**任何手把（不管是閘級還是行為級）。執行 `class-major 重編號`。
    *   **Phase 2 (Fixture 載入)：** 根據該測試是否傳入 `--joypad`，決定要不要實例化 `nes-pad-behavioral`。如果需要，將其載入。
2.  **動態掛載於尾端：**
    `nes-pad-behavioral` 產生的所有新節點（不管是行為節點還是假電晶體），一律分配 **大於 DUT 最大 ID 的尾端編號**（Tail Allocation 的延伸應用）。
3.  **邊界對接：**
    將手把的輸出 Pin，對接到 Phase 1 已經定型的 Controller Port 節點上。

**結果：** 無論你跑哪一個測試（插手把或不插），Phase 1 的 DUT 內部節點 ID、圖結構、BFS 展開樹將**100% 同構**。零探針效應，對齊抽籤結果絕對不變。

---

### 3. tie 改接的最佳解？

**解答：(a) 載入時不加那 6 條 vss tie（修正 board def，全域生效）。**

*   **為何不選 (b) runtime 覆寫？**
    你已經點出痛點：模擬器底層 `Gnd > Pwr`。而且物理上，用強勢電源去覆寫一個實體接地線，本質上是短路（Short circuit），在開關級模擬器中很容易引發不穩定的狀態競爭。
*   **為何不選 (c) per-test？**
    如第 1 點所述，這是 DUT 的物理 bug，不能 per-test。

**「不加 vss tie」會不會擾動對齊？**
**會！而且你必須勇敢承受這一次的擾動。**
這跟測試儀器造成的擾動不同：這是**修復主機板圖紙**所帶來的「真實圖變更」。這 6 條邊的消失，理所當然會改變 LS368 附近的 BFS 展開順序。
**正確的處置方式是：**
1. 全域移除這 6 條 VSS 綁定。
2. 接受 `ppu_vbl_nmi` 家族掛掉的事實。
3. **重新尋找/重擲** 該家族測試所需的對齊 Seed 或 Judgment Frame。
4. 將新的 Judgment 基準 Bake（寫死）進這幾個敏感測試中。這是一次性的技術債償還。

---

### 4. 元層面：如何預防這類回歸？（Graph Fingerprinting 機制）

**解答：引入「DUT 網表指紋 (Netlist Graph Fingerprint)」機制。**

這次的根本原因是：「無意間改變了圖結構，卻沒有意識到需要重跑對齊敏感測試。」這在 EDA 工具或硬體驗證中非常常見，解決方案是給 DUT 算 Hash。

**實作建議：**
1.  **定義 DUT 邊界：** 在 Phase 1（主機板 + 晶片載入並完成 ID 分配後，掛載任何 per-test fixture 之前），提取圖特徵。
2.  **計算 Hash（指紋）：**
    *   總節點數 (Total Nodes)。
    *   總邊數 (Total Edges)。
    *   （可選，更嚴謹）對所有 Node 的 Adjacency List 進行排序後計算 MD5/SHA。
3.  **CI/CD 阻斷機制：**
    將這個 Fingerprint 寫死在測試框架的設定檔中。
    每次執行測試前，驗證載入的 DUT Fingerprint 是否與設定檔相符。
    *   **若相符：** 繼續跑所有測試。
    *   **若不符：** 框架直接拋出 `DutGraphMutatedException`。

**這個機制的巨大好處：**
下次如果有人又偷偷改了 `board def`（比如加了一根線），CI 會在第一時間 Crash，並明確告訴開發者：「你改變了主板結構，對齊抽籤已重擲，請重新驗證 `ppu_vbl_nmi` 並更新 Fingerprint。」
它把**「未知的隨機失敗」**變成了**「已知的預期變更」**。

---

### 5. 總結與第五種做法（架構重整）

回顧先前的 dbl2007 案與本次案件，其實都指向了同一個終極架構目標：**「不可變的 DUT 與 動態尾端掛載的治具」**。

你的第五種做法（最終解法）應該是重構你的測試啟動流程：

1.  **分離圖紙定義：** 將 `nes-pad` 從 `nes-001` 的 base board def 中拔除，讓主板的 Controller Port 變成純粹的 IO Node。
2.  **全域修復 DUT：** 拔除 1A4/2A1/2A2 的 `vss` tie，讓它們浮接，更新 DUT Fingerprint。重新校準並修復 `vbl_nmi_timing` 家族的判定幀。
3.  **實作 Socket 載入器：**
    ```csharp
    // 虛擬碼概念
    var emu = new Emulator();
    emu.LoadDUT("nes-001-fixed"); 
    emu.AssignClassMajorIDs(); // 鎖死主圖 0~N 的 ID 與 BFS 順序
    emu.VerifyDutFingerprint("HASH_8A3B..."); 

    if (testConfig.NeedsJoypad) {
        // Tail Allocation: ID 從 N+1 開始
        var joypad = new BehavioralJoypad(startNodeId: emu.MaxNodeId + 1);
        emu.AttachModule(joypad, port: "Controller1");
    }
    ```

**這樣做，你既保留了 `per-test` 載入手把的彈性，又達成了對主圖 BFS 順序的「絕對零干擾」，完美解決了本次的探針效應危機。**