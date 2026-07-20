# Gemini 諮詢:NES 音訊輸出鏈(APU → 喇叭)+ 如何模擬(2026-07-20)

完整回覆 `2026-07-20-gemini-audio-signal-chain-reply.txt`(gemini-3.1-pro-preview)。
對應視訊鏈([[../suggest/2026-07-20-gemini-video-signal-chain]]);背景:2A03(含 APU)已 bit-exact 電晶體級,APU 停在 5 聲道數位值,想接到喇叭前的完整類比鏈。取樣基準 **1.789773 MHz(CPU 時脈)**。

## 1. 完整音訊鏈(NES-CPU-11 為例)
- **[2A03 內] 輸出腳**:**Pin 1 = Pulse1+Pulse2 混音**、**Pin 2 = Triangle+Noise+DMC(TND)混音**。兩腳是**開漏極 NMOS 下拉陣列**(無上拉的 drain,只 sink 電流入地)。
- **[主機板] 混音 + 濾波 + 放大**:
  1. **上拉**:Pin1/Pin2 各接 **100Ω(R7/R8)→ +5V**,把下拉電流轉成電壓。
  2. **混音電阻**:Pin1 串 **20kΩ(R9)**、Pin2 串 **12kΩ(R10)**,另一端接一起 = 混音節點。(⚠️ Famicom 數值相反/不同 → 美版/日版方波:三角波音量比不同。)
  3. **耦合/高通**:混音節點串 **1µF(C21)** 隔直流(~90Hz HPF)。
  4. **放大器**:**74HCU04(U7)** 六反相器,拿一顆「無緩衝」邏輯反相器 + **100kΩ(R12)** 回授電阻硬逼在線性區當類比放大器(NES 沒用 op-amp)。
  5. **輸出濾波**:再一組 RC(低通 ~14kHz、高通 ~440Hz)→ RCA。
- **[卡帶擴充音訊]**:**Famicom** 金手指 Audio IN(pin45)/OUT(pin46),卡帶特製晶片(VRC6/Sunsoft5B…)自產音訊在卡帶內混音再送回;**NES** 移到底部 48-pin 擴充槽(EXP pin54)但官方沒用 → 模擬時直接在軟體混音節點加擴充晶片的類比值。
- **[電視前] AV vs RF**:**Composite AV = line-level baseband,模擬這個就夠**;RF 音訊調變 4.5MHz FM 副載波 → **軟體可完全略過**(除非做 RF 雜訊濾鏡)。

## 2. APU DAC 非線性混音(硬體機制)
- **非 R-2R**:每聲道每 bit 控制一個**對地 NMOS 開關**;數位值↑ → 開更多並聯 NMOS → **改變 Pin 對地等效電阻 R_apu**。
- 配主機板 100Ω 上拉 = 分壓:`V_out = 5V · R_apu/(R_apu+100)`;聲道間是**並聯增加電導**(電導線性相加、電阻是倒數關係)→ 放進分壓 → 那條著名**非線性曲線**。
- **Blargg 擬合公式(輸出正規化 0–1)**:
  - Pulse(Pin1):`out = 95.88 / (8128/(pulse1+pulse2) + 100)`
  - TND(Pin2):`out = 159.79 / (1/(triangle/8227 + noise/12241 + dmc/22638) + 100)`
  - (公式裡 `100` = 外部 100Ω 上拉;其餘常數 = NMOS 陣列製程等效導通電阻。)

## 3. DSP 建模
- **產生(1.789773 MHz)**:每 CPU cycle 把 5 個數位值代非線性公式 → 一個 0–1 浮點樣本。
- **IIR 濾波(在 1.789MHz)**:一階差分 `y[n]=a0·x[n]+a1·x[n-1]+b1·y[n-1]`,串聯:**90Hz HPF(耦合電容)+ 440Hz HPF(NES 特有,放大器回授/輸出網;Famicom 只有 ~37Hz 一個)+ 14kHz LPF**。
- **降採樣到 48kHz**:比率 ~37.286(非整數)。⚠️ **不能直接每 37 抽 1**(方波無限諧波 → 嚴重混疊)。
  - 方案 A(標準 DSP,推薦給 cycle-accurate):1.789MHz 下先過 20kHz 截止 FIR 低通(Kaiser 窗 ~64–128 tap)→ decimate。或 CIC 降到近整數倍 + fractional resampler 到精準 48kHz。
  - 方案 B(Blip-Buffer / band-limited synthesis):只在數位步階變化點插預算的帶限步階響應(blargg Nes_Snd_Emu);但我們有 1.789MHz cycle-accurate 驅動,方案 A 更貼硬體思維。

## 4. AprVisual 實作路線
1. **Tap point:在 APU 的 NMOS DAC 閘極截斷**(不跑 SPICE 解 100Ω 分壓節點,太耗)。網表找 5 聲道輸出 latch → DAC NMOS 的數位控制線,每 CPU cycle 讀 5 個整數(0–15,DMC 0–127)。
2. **行為級 sidecar @1.789773 MHz**:Blargg 非線性公式 → 浮點;(選配)加擴充音訊;90/440Hz HPF + 14kHz LPF;**Polyphase FIR decimator** → 48kHz。
3. **⚠️ 自己寫**(使用者既定偏好 [[build-our-own-no-vendor-refsim]]):參考只當原理對照 —— **Visual2A03**(對照 5 聲道 latch 節點位置)、**Mesen `Apu.cpp`/`Filter.cpp`**(看它 90/440Hz/14kHz 一階 IIR 係數公式 + resampler[Hermite/Sinc])—— **自己用 C/C++/Rust 重寫,不 vendor**。

**一句話**:2A03 純數位邏輯走到 NMOS 閘極為止用電晶體級網表;從 DAC 起到喇叭的類比鏈用 1.789MHz 取樣的 DSP 數學模型還原 → 兼顧 cycle-accurate 精度與即時效能。

關聯:視訊鏈 [[../suggest/2026-07-20-gemini-video-signal-chain]]、解碼端 [[../suggest/2026-07-20-gemini-ntsc-decode-side]];AprNes 已有音訊(APU.cs + tool/WaveOutPlayer/AudioPlus)可對照。
