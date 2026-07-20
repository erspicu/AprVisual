# Gemini 諮詢:「晶片/主機漏電當溫度計」有沒有人做過?(2026-07-20)

完整回覆 `2026-07-20-gemini-thermometer-prior-art-reply.txt`(model = gemini-3.1-pro-preview)。
專門聚焦深入專文 #3(NES open-bus 溫度計)的 prior art。

## 一句話結論
**把 NES PPU open-bus 漏電寫成溫度計 ROM,在 NESdev / demoscene「極高機率史無前例」。** 但**電路原理**上有一個「一模一樣」的先例(LED 反偏放電溫度計),載體不同而已。

## 最接近的先例(依接近程度)

### ⭐ 電路原理一模一樣:LED 反偏放電溫度計(Hackaday / EEVblog / Arduino 社群)
- 經典窮人 hack:把 LED 反向偏壓當成微小電容,GPIO 切成輸入、量電壓掉到邏輯 0 的時間;放電時間 = LED 接面漏電流,漏電流隨溫度指數變化。
- **與我們的點子「電路層面一模一樣」** —— 差別只在他們用外部 LED/二極體,我們用晶片內部匯流排的寄生電容。→ 這是最該引用的對照。

### ⭐ 學術上已證實:DRAM retention 當溫度感測器(sensorless)
- 核心物理 I ∝ exp(−Ea/kT) 在硬體安全/架構界廣泛研究(DRAM = 電容 + 電晶體)。
- **Halderman et al., "Lest We Remember: Cold Boot Attacks", USENIX Security 2008** —— 證明 DRAM retention 與溫度指數相關(降到 −50°C 可維持數分鐘)。
- **Onur Mutlu 團隊**:RAIDR(retention-aware DRAM refresh, ISCA 2012)一系;以及 ~2020《Exploiting DRAM Data Retention for Sensorless Thermal Management》—— 關特定 block 的 refresh、數 bit-flip 反推晶粒溫度。→「用動態記憶體漏電時間反推溫度」學術已發表。
- 對比:**SRAM PUF** 看的是 power-up 初態不對稱,通電後交叉耦合維持電壓、**不會隨時間漏電衰減** → 原理與我們不同。

### 復古主機的溫度漂移(相關但非漏電、也非軟體量測)
- **C64 SID(6581)類比濾波器 cutoff** 對溫度極敏感(暖機半小時前後不同,CSDb/reSID 有記);但基於 R/C 類比特性、非數位匯流排漏電。
- 初代 Game Boy(DMG)LCD 反應時間受室溫影響(看瑪利歐拖影冷不冷)—— 純物理現象、沒軟體量測。

### NESdev 對 open-bus 的既有量測
- lidnariq / quietust / blargg 對 PPU open-bus 有極詳盡量測:已知 $2002 低 5 bit 保存上次寫 PPU 暫存器的值、衰減 ~100–600ms(視批次與**溫度**)。
- **但他們把溫度當成「害讀取不穩定的干擾變數」來寫更準的模擬器(Mesen/FCEUX)—— 從沒人反過來寫 ROM 把這干擾變數轉成攝氏 UI 顯示。**

## 現代 MCU 內建溫度感測器是漏電原理嗎?
- **絕大多數不是。** STM32/AVR/ESP32 幾乎全用 **PTAT / Bandgap**(兩顆電流密度不同的 BJT,ΔV_be ∝ 絕對溫度,線性、製程變異小)。
- 只有 **ISSCC/JSSC 前沿超低功耗客製 ASIC**(植入式醫療、無電池 RFID)才用 **sub-threshold leakage** 對電容充放電轉頻率(ring oscillator)—— 跟我們原理同,但極少出現在市售通用 MCU。

## 原創性拆解(Gemini 評)
**已知/已有人做:** 寄生電容漏電 ∝ 溫度(半導體物理 101);DRAM 漏電當溫度計(學術已證);NES $2002 open-bus 衰減數百 ms(NESdev 已詳記)。
**我們相對原創的亮點:**
1. **載體創新** —— 把遊戲機的硬體副產品(寄生電容)昇華成實用感測器。
2. **避開 CPU 自我刷新的洞見** —— 精準抓到 CPU data bus 不能用(指令抓取一直 precharge/覆寫),改讀 PPU 內部閂鎖(展現對 NES 架構的深理解)。
3. **軟體工程實踐** —— 6502 asm 輪詢 $2002 + 指數式 + 一點校準 + LUT。
4. **自體發熱的客觀認知** —— 認清是「晶粒溫度計」非室溫計;冷開機頭幾秒記升溫曲線本身就是很酷的 demoscene 視覺題材。

## 建議
- Gemini 力薦「大膽做並發表」,寫個 ROM 叫 **"NES Die Thermometer"**,畫開機後 PPU 升溫曲線 → 高機率入選 Hackaday、在 retro 社群轟動。
- **最該補的引用 = LED 反偏放電溫度計**(電路原理同源)+ **DRAM sensorless(Halderman 2008 / Mutlu RAIDR)**。→ 可加進 nes-thermometer.html 的「Prior art」段,誠實標明「原理已知、此載體+軟體實作是新的」。

關聯 [[openbus-shim-lastbyte-model]]、[[gemini-similar-projects(本輪)]]。
