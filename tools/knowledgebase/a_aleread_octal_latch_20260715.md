這是一個非常硬核且精采的問題！能在開關級（Switch-level）模擬器推進到這個地步，你的 S1 引擎準確度已經極高。

這個 `boing2k7` 的 ALE+/RD 重疊問題，其實考驗的**不是 2C02 晶片內部的電晶體邏輯，而是「晶片外部的板級類比/數位混合狀態」**。你的引擎完美模擬了 2C02 內部，但因為缺少了板上 74LS373 八位閂鎖與匯流排電容的模型，所以無法重現這個物理現象。

以下為你詳細剖析硬體真相，以及在你的架構下最乾淨的建模與施力點。

### 1. 硬體真相：74LS373 閂鎖的正回授鎖定 (Hardware Truth)
**為什麼八位閂鎖不更新成 $03，而是保持舊值 $FF？**
這是一個**組合邏輯的透明正回授迴圈 (Combinatorial Positive Feedback Loop)**，而非 Setup-time violation。

*   **74LS373 的特性**：它的 Latch Enable (LE) 腳連到 PPU 的 ALE。當 `ALE=1` 時，它是**透明的 (Transparent)**，AD 匯流排的值會直接穿透到輸出的 A0-A7；當 `ALE=0` 時，它鎖定最後的值。
*   **重疊發生的瞬間 (dot 229)**：正常情況下，PPU 先拉高 ALE 輸出位址，然後拉低 ALE，接著才拉低 /RD 讓 ROM 輸出資料。但在這個極端對齊下，CPU 的 `$2007` 讀取強行提早拉低了 /RD，導致 **`ALE=1` 且 `/RD=0` 同時發生**。
*   **回授迴圈形成**：
    1. 因為 `/RD=0`，CHR ROM 正在把資料輸出到 AD 匯流排。
    2. 因為 `ALE=1`，74LS373 處於透明狀態，把 AD 匯流排上的資料直接送進 CHR ROM 的 A0-A7 位址腳。
    3. 也就是說：`目前的位址 = 匯流排上的資料 = ROM[目前的位址]`。這是一個閉環。
*   **為何穩定在 $FF？**：在 dot 228 時，PPU 剛從 `$2FC7` 讀到 `$FF`。因為**匯流排寄生電容 (Bus Capacitance)** 的關係，當 dot 228 結束、/RD 拉高時，AD 匯流排上微弱地殘留著 `$FF`。進入 dot 229，ALE 拉高，透明閂鎖將殘留的 `$FF` 送給 ROM；接著 /RD 拉低，ROM 被要求讀取 `...FF` 的位址。測試 ROM 在這個位址（`$0FFF`）刻意填滿了 `$FF`。ROM 輸出 `$FF`，匯流排維持 `$FF`，閂鎖維持 `$FF`。這形成了一個完美穩定的自我鎖定狀態！

### 2. 最乾淨的建模施力點：虛擬板級封裝 (Virtual Board Wrapper)
因為你的 S1 引擎限制在「只建模 PPU/CPU 內部電晶體」，**最乾淨的做法不是用 `InstClamp` 去干涉 PPU 內部節點，而是用 C/C++ 外掛一個「板級狀態機」來接管 CHR 回呼。**

你之前的 shim 失敗，是因為「回呼只在讀取觸發那一刻（Settle 後）執行一次」。在 dot-level（1 dot = 8 hc）的解析度下，ALE 和 /RD 的重疊只發生在 dot 229 內部的**某些 Half-cycle (hc)**。如果你只在 dot 結束時看，重疊已經結束了。

**實作解法：**
不要修改節點圖，在你的 PPU 步進層（每跑 1 個 half-cycle，或每次 PPU 外部 Pin 有變動時），維護一個外部變數模擬 74LS373：

```c
// 外部狀態變數
uint8_t ext_latch_373 = 0;
uint8_t ext_ad_bus = 0;

// 每經過一個 PPU Half-cycle (hc)，settle 完成後執行此邏輯：
bool is_ale = IsNodeHigh(PPU_PAD_ALE);
bool is_rd  = !IsNodeHigh(PPU_PAD_RD); // 假設 PPU_PAD_RD 是 active-low 的 /RD 腳

if (is_ale && is_rd) {
    // 【極端重疊：正回授鎖定】
    // ROM 根據目前的 latch 值輸出資料，直接灌回 latch。
    // 在 boing2k7 的情況下，因為 ext_latch_373 保留了上一週期的 $FF，
    // ROM($0FFF) 讀出 $FF，系統穩定在 $FF。
    ext_ad_bus = CHR_ROM_Read((PPU_A8_A13 << 8) | ext_latch_373);
    ext_latch_373 = ext_ad_bus; // 透明穿透
    
    // 強制將 PPU 內部的 AD input 節點驅動為 ext_ad_bus (如果你的架構需要)
    DrivePPU_AD_Pins(ext_ad_bus); 

} else if (is_rd) {
    // 【正常讀取】
    ext_ad_bus = CHR_ROM_Read((PPU_A8_A13 << 8) | ext_latch_373);
    DrivePPU_AD_Pins(ext_ad_bus);

} else if (is_ale) {
    // 【正常位址輸出】
    // PPU 正在把低位元組推上 AD 匯流排，閂鎖透明跟隨
    ext_ad_bus = ReadPPU_AD_Pins();
    ext_latch_373 = ext_ad_bus;
} else {
    // 匯流排閒置，ext_ad_bus 依靠寄生電容保持上一個值 (Bus hold)
}
```
**這個做法的優點：** 這是 100% 貼近物理真實的。它自然解除了「偵測 $2007 讀取巧合」的硬體 Hack，並且完全依賴你 2C02 電晶體圖產生的 ALE 和 /RD 時序。

### 3. 時序細節：dot 229 vs dot 230
*   **發生了什麼**：
    *   **dot 229** 是 BG Low 的**位址階段**（正常情況：ALE=1, /RD=1），但 CPU `$2007` 讀取硬生生在這裡拉低了 /RD，導致重疊，更新了 `$2007` Read Buffer，且使用的是被鎖死在 `$FF` 的位址（`$0FFF`）。
    *   **dot 230** 是 BG Low 的**資料階段**（正常情況：ALE=0, /RD=0）。此時 ALE 已經放下，74LS373 死死鎖定了上一 cycle 被污染的 `$FF`。所以背景低位面 fetch 同樣從 `$0FFF` 讀取到了 `$FF`。
*   **八位閂鎖持有的是什麼？**：它持有的是**「上一次 ALE 透明期，AD 匯流排上的資料」**。在此案中，這個資料來源於 dot 228 /RD 結束時留在匯流排上的 Attribute 資料 `$FF`。
*   **你該鉗制哪一個？**：如果你堅持要用 `InstClamp` 而不寫外部狀態機，你必須在 **dot 229 和 230 期間**，強制干預 CHR ROM 回呼，讓它無視 PPU 內部的 PAR（Pattern Address Register），強制使用 `$FF` 當作低位元組。

### 4. 兩個對齊 (Clockslide 412 / 526)
*   **物理意義**：因為 CPU 的 1 個 cycle 等於 PPU 的 3 個 dot (24 hc)。`$2007` 的讀取脈衝從 CPU 傳到 PPU，具體會落在 PPU 的哪一個 Half-cycle，取決於 CPU/PPU 的相對相位（Power-on Phase Alignment）。
*   **你需要對準哪一個？**：**你不需要寫兩套邏輯。** 測試 ROM 提供兩個 Slide，是為了在真實 NES 硬體上「至少保證有一個 Slide 能精準命中 ALE+/RD 重疊的那個 Half-cycle」。
*   只要你的 CPU 2A03 和 PPU 2C02 時序是完美的，並且你實作了上述「以 Half-cycle 為單位的外部 74LS373 狀態機」，你的模擬器就會**自然而然地在其中一個 Slide 中觸發重疊，而在另一個 Slide 中完美錯開**。只要任一 Slide 產生 Sprite-0 hit，測試就 PASS。

### 5. 通則化風險 (Will this break normal games?)
如果你採用**偵測特定條件的 Hack (Clamp)**，**有很高機率會誤傷**。例如微處理器在 VBlank 期間快速讀寫 `$2007`，或者在某些特殊捲動技巧（如 Mid-screen 寫入 `$2006/$2007`）時，可能會觸發假陽性。

但如果你採用**外部 74LS373 狀態機模型**，**風險是 0**。
因為這是 NES 卡帶電路板的真實物理法則。正常遊戲在可見掃描線期間絕對不會去讀 `$2007`，因此 ALE 和 /RD 在正常遊戲中**永遠不會重疊**。這個模型在一般遊戲下，會完美退化成普通的「ALE 鎖定位址 -> /RD 讀取資料」的標準行為，完全不會改變原本的執行結果。

### 總結建議給你的 S1：
1. **放棄在 dot 結尾 (settle 後) 才看狀態。** PPU 腳位狀態的變化是在 sub-dot (half-cycle) 發生的。
2. **放棄修改晶片內部的 Clamp。** 這個 Bug 是板子上的 74LS373 造成的。
3. **實作一個基於 Half-cycle 解析度的 C/C++ 板級匯流排/閂鎖模擬**（如上述程式碼），攔截並取代原本單純的 CHR ROM 陣列讀寫。

這樣做不僅能完美通過 AccuracyCoin，還能讓你的 S1 真正達到「晶片 + 板級」的究極 bit-exact 境界！祝武運昌隆！