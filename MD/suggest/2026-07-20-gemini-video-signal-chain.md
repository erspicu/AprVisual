# Gemini 諮詢:NES 畫面輸出訊號鏈要模擬哪些晶片 + 實作建議(2026-07-20)

完整回覆 `2026-07-20-gemini-video-signal-chain-reply.txt`(gemini-3.1-pro-preview)。
背景:AprVisual 已做到 2A03+2C02 bit-exact 電晶體級,PPU 目前只輸出調色盤索引;想銜接真實 composite/NTSC 類比訊號。

## 1. 完整訊號鏈(2C02 → RCA;**RF 調變器不用模擬**,composite baseband 在進 RF 前就成型)
1. **[2C02 內] 數位色彩/同步邏輯** — 現在停的地方:輸出 Sync / Luma(亮度階)/ Chroma(色彩相位)/ Colorburst 致能 / Emphasis。
2. **[2C02 內] 相位產生器 + MUX** — 21.477MHz 主時脈產生 12 個相位方波(12 步環形計數器)。
3. **[2C02 內] 視訊 DAC** — **不是 R-2R**,是晶片上**串聯電阻分壓網路 + NMOS 傳輸閘**,直接把電壓輸到 **Pin 21 (VOUT)**。粗暴切換不同 DC 電壓拼出方波,靠電視低通平滑。
4. **[主機板] 視訊放大/緩衝**(VOUT 驅動力弱、輸出阻抗高,推不動 75Ω):以 NES-CPU-04~11 板為例
   - **Q1 = PNP(`2SA937` 或等效 `2SA1015`)**:第一級放大 / 位準平移。
   - **Q2 = NPN(`2SC2021` 或等效 `2SC1815`)**:射極隨耦器,提供電流驅動。
   - 被動:偏壓電阻 220Ω/510Ω/110Ω、隔直流電容 220µF。
5. **[電視/擷取] NTSC 解碼器** — 帶通/低通拆 Y/C → 3.58MHz 解調 U/V(I/Q)→ RGB。

**關鍵必模擬**:(3) 內部電阻網路等效 + (4) 雙電晶體放大電路(其非線性已含在下面的量測電壓表裡)。

## 2. 2C02 視訊輸出機制
- **電壓階**:內部一條 VCC(5V)↔GND 串聯電阻網路,數位邏輯開對應 NMOS 把某節點導到 VOUT。Luma 4 階;Chroma = 每色以 3.58MHz 在「高/低電壓」切換成方波。
- **相位編碼**:colorburst 3.579545MHz;主時脈 21.477272MHz = **colorburst 的 6 倍**;12 步環形計數器 @21.477MHz 給 12 相位;**每像素 = 4 個主時脈**。輸出某色時在該相位於 Luma 對應的 High/Low 間切換。
- **Dot crawl 自動產生**:像素長 4 主時脈、色彩週期 6 主時脈,不整除 → 每條掃描線相位偏移 → **只要用 21.477MHz 輸出樣本,dot crawl 自然完美出現**。
- **Emphasis**:$2001 的 R/G/B 位元;開啟時在「非該色相位期間」導通額外下拉電阻 → 整體電壓**降約 20%** → 不是增強某色,是衰減其他色 + 整體亮度。
- **$0D「黑於黑」**:-0.11V,會害電視 sync 分離器誤判。

## 3. 建模 = **Sidecar 類比行為模型(查表,不要 SPICE)**
- **取樣率必須 21.477272 MHz**(NES 類比模擬黃金頻率),每像素 4 樣本,每掃描線 ~341×4=1364 樣本。
- 每 1/21.477M 秒:讀 PPU 數位端 `Sync / Color_Phase(0-11) / Luma(0-3) / Emphasis`;讀 master clock mod 12 得 current_phase;依 Sync/Colorburst/畫面期輸出對應電壓;套 emphasis 衰減。
- **查表電壓(lidnariq 量測,已含外部電晶體非線性,直接套最逼真;相對 1Vpp)**:
  Sync -0.285V · Blanking 0(基準)· Black($0D) -0.11V · Luma0x 0.22(低)/0.61(高)· Luma3x 0.71(低)/1.09(高)。
- **外部放大器等效**(NES-CPU-11):VOUT→Q1(2SA937)基極;Q1 射極 220Ω→5V、集極 510Ω→GND;Q1 射極→Q2(2SC2021)基極;Q2 集極→5V、射極 110Ω+220µF→電視(75Ω)。務實做法:直接用「過這層後的最終電壓」查表(Mesen 就是這樣)。

## 4. 對 AprVisual 的實作路線
1. **在 PPU 網表截斷**:找到驅動 Video DAC 傳輸閘的控制節點 —— `Luma 0-3` / `Chroma 0-15(→12 相位)` / `Sync` / `Colorburst Enable` / `Emphasis R/G/B`,把這些數位控制訊號拉出來。
2. **21.477MHz 取樣 sidecar**:主時脈每 tick 呼叫 `get_composite_voltage(luma, chroma, phase_counter, emphasis, sync)`(查表)。**不要**電晶體級/SPICE 模擬類比放大器(效能崩、精度不成比例)。
3. **NTSC 解碼**:把 21.477MHz float 陣列餵給 **`nes_ntsc`(blargg)** 或參考 **Mesen `VideoFilterNtsc.cpp`**;或自寫簡單 FIR:LP→Y、乘 sin/cos colorburst + LP→I/Q、YIQ→RGB 矩陣、降取樣(如 602×240 / 1204×480 顯示 artifacts)。

**必看**:Visual2C02(Quietust,JS 源碼有 DAC 電阻比例算 VOUT)· Mesen `VideoFilterNtsc.cpp` · Nesdev Wiki「NTSC video」(Color phases / Emphasis 章)。

**一句話**:在「數位控制訊號驅動 DAC 的前一刻」截斷 switch-level 2C02,轉成查表行為模型生成 21.477MHz 類比電壓樣本,再用 DSP 濾波解碼 → bit-exact 邏輯 + 逼真 NTSC 類比畫面。

關聯:AprNes 已有 `NesCore/NTSC_CRT/`(CrtScreen.cs、PhosphorDecay 等),可對照;[[openbus-shim-lastbyte-model]]、混合訊號 sidecar 藍圖 `MD/suggest/2026-07-14-混合訊號模擬延伸藍圖-類比Sidecar`。
