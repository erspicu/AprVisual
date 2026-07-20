# Gemini 諮詢:有沒有人做過類似 AprVisual 的專案?(2026-07-20)

完整回覆 `2026-07-20-gemini-similar-projects-reply.txt`(model = gemini-3.1-pro-preview)。
問:把整條「開關級網表 → 圖 → 邏輯/時序抽象 → 可驗證後端」管線 + 物理層分析 + 精度戰役
當成研究平台,有沒有人做過幾乎一樣的?哪些是重造輪子、哪些真新穎?該讀誰?

## (a) 各面向的前人 / 工具(具名)

| 面向 | 關鍵人物 / 專案 |
|---|---|
| 開關級 6502/NES 模擬 | **Visual6502**(Greg James / Barry & Brian Silverman,2010,chipsim.js);**Visual2A03/2C02**(Quietust,Nintendulator 作者,顯微拍攝向量化 = 我們網表源頭);**perfect6502**(Michael Steil,C 移植高效版);**MetalNES**(Jared Forsyth,試 AOT 編譯網表) |
| transistor → logic 自動抽取 | **Randal E. Bryant**(CMU,開關級模擬數學基礎,*A Switch-Level Model and Simulator for MOS Digital Systems*, IEEE TC 1984;**COSMOS** 模擬器含布林抽取);逆向工具 **Degate**(Martin Schobert)、**HAL**(波鴻魯爾大學,gate-level 逆向 + 子圖比對 + 行為抽取) |
| 精度對拍 / test-ROM | **blargg(Shay Green)** 祖師爺;**Sour(Mesen)**、**FHorse(puNES)**;NESdev 頂級模擬器標配 100% blargg —— 但都是「人工觀察 Visual2A03/2C02 → 手寫 C++ 行為級狀態機」,非全自動 |
| 版圖 → 物理參數反推 | **Ken Shirriff**(righto.com,晶粒照片反推電容比/驅動強度/閂鎖/類比延遲,最活躍傳奇);EDA 的 **LVS / Parasitic Extraction(PEX,如 Calibre xRC)**;「我們的工具箱本質 = 為復古晶片客製的輕量級 PEX + 拓撲分析」 |
| 漏電當溫度計 | 學術:**DRAM retention 當溫度感測器**(如 *Using DRAM as a temperature sensor*, 2013);NESdev 早有「OAM decay 受溫度影響」討論(Micro Machines 選單閃爍)。但**把 CPU open-bus 漏電精確建模 + 給 7.2%/°C 量化模型 = NES 社群前沿、原創** |

## (b) 有人做過幾乎一樣的「整條管線」嗎?
**客觀結論:沒有。** 把「逆向資料 → 高效模擬 → 自動邏輯抽取 → 效能/等價形式驗證 → 物理參數推導 → 混合類比」串成單一平台是**首創**。
- 最接近:**MetalNES**(管線前半:網表→模擬器→試 AOT;但目標是「做出能玩的模擬器」,不是「可證偽的分析量測管線」)。
- 次接近:**Siliconpr0n / John McMaster** 工具鏈(偏 GDSII/影像→網表自動化,缺後端動態執行 + 邏輯等價驗證深度)。

## (c) 重造輪子 vs 真新穎(誠實)

**⚠️ 其實是前人做過(prior art):**
1. 「靜態 DAG 處理不了雙向電晶體網(SCC)→ event-driven 反而快」= EDA 經典的 **Cycle-Based vs Event-Driven** 之爭,1990s 就對「大量 pass-transistor + 緊耦合雙向節點」得出同結論。我們的結論精確,但是**被重新證實的經典現象、非全新發現**。
2. 純邏輯 bit-exact 移植 —— perfect6502 已證明。
3. 開關級底層演算法 —— 仍是 Bryant 模型 + Visual6502 優化的延伸。

**🌟 真新穎、少見、高價值:**
1. **98.9% 可約性證明** —— 對真晶片「多少比例純數位邏輯 / 多少不可約類比」做**全自動量化嚴格證明**,逆向社群前所未見(大家通常只憑感覺說「這顆很 tricky」)。
2. **Open-bus 線性放電建模 + 7.2%/°C 溫度計靈敏度** —— 把 edge case 提升到物理量測等級 + 精確公式。
3. **多後端等價驗證下的「可證偽效能天花板」** —— 用現代軟工(IR/AOT/GPU)給明確 benchmark(AOT 3–6× 慢、GPU 10.7× 慢),徹底回答社群多年疑問「Visual6502 能不能編譯到一般模擬器那麼快?單核不行」。
4. **Analog Shim 混合架構** —— 補那 1.1% 類比又維持全局 bit-exact,優雅的 mixed-signal 實踐。

## (d) 該讀 / 該關注
- **人 / 社群**:Ken Shirriff(強烈建議把物理分析工具箱 + latch 掃描分享給他);Quietust 與 NESdev `Hardware` 版(學術純度高,適合貼 test-ROM 戰役 + DMC 類比賽跑);Jared Forsyth(我們的 AOT 負面結果是對 MetalNES 路線的重大回應)。
- **經典文獻**:Bryant 1984(開關級理論祖師);Halderman et al. 2008 冷啟動攻擊(DRAM 漏電/溫度依賴,可當 open-bus 溫度計的學術對標)。
- **發表場域建議**:別只在 emulator 社群 —— 可約性量測 + AOT 效能邊界有資格投 **USENIX WOOT / CHES**(或想走 geek 風的 **SIGBOVIK**)。定位 = 「針對 1980s NMOS/CMOS 晶片的自動化形式驗證 + 物理提取框架」。

---
關聯 [[paper-relocated-to-private]](若寫論文,上述引用清單直接可用)、[[escape1-results]](98.9% 可約性)、
[[s4-route-single-instance]](event-driven vs AOT 天花板 = Gemini 指出的 CBS-vs-EDS 經典重證)、
[[openbus-shim-lastbyte-model]](open-bus 溫度計)。
