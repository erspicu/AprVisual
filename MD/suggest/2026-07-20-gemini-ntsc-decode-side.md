# Gemini 諮詢:NTSC 解碼端(電視/解碼晶片)+ 自己寫解碼器(2026-07-20)

完整回覆 `2026-07-20-gemini-ntsc-decode-side-reply.txt`(gemini-3.1-pro-preview)。
承接視訊產生端([[../suggest/2026-07-20-gemini-video-signal-chain]]);使用者拍板**自己寫解碼器**(不用 nes_ntsc/blargg、不抄 Mesen)。
取樣率基準 **21.477272 MHz = f_sc(3.579545MHz)的 6 倍** → 每 f_sc 週期正好 6 樣本,數學很乾淨。

## 1. 真實解碼鏈 + 晶片
- **同步分離**:比較器切 slice level 抓低於 blanking 的脈衝,RC 積分分 H-Sync(4.7µs)/V-Sync。經典 IC = **LM1881**。
- **Y/C 分離**:(a) **Notch/BPF**(平價電視):BPF 抓 3.58MHz±0.5 當 C、notch 挖掉當 Y;(b) **Comb filter**(中高階):利用「相鄰行 f_sc 相位反轉」相加抵 C / 相減抵 Y,早期**玻璃延遲線**(63.5µs)→ CCD(Panasonic MN3101)→ 數位(Philips SAA7118 2D/3D)。
- **色彩解調 + PLL**:色彩靠相位角,傳輸有 jitter → 每行 colorburst(~9 週期 3.58MHz)當基準,**PLL 鎖相**後在該行維持頻率去解調出 I/Q。
- **色差矩陣**:Y,I,Q 經電阻網路/op-amp → RGB 推電子槍。
- **經典全能解碼晶片**:**Sony CXA1621S · Philips TDA3562A · Toshiba TA7698AP**(包辦 Y/C 分離[需外掛延遲線]+ PLL + 解調 + RGB 矩陣)。

## 2. 自己寫的 DSP 演算法(虛擬碼要點)
- **Burst 相位量測**(軟體時鐘完美、不需複雜 PLL 抗 jitter,只需量每行 burst 相位):sin/cos 表長度 6(`sin=[0,.866,.866,0,-.866,-.866]`、`cos=[1,.5,-.5,-1,-.5,.5]`),對 burst 段做 I/Q 累加 → `burst_phase=atan2(Q,I)`。
- **Y/C(notch/BPF)**:15–31 階 FIR BPF(中心 3.58/21.47=0.166 歸一化)抽 C,`Y=composite−C`。
- **解調**:`phase_offset=burst_phase+π`;`I=C·cos(2πn/6−offset)`、`Q=C·sin(...)`;必須 LPF 去 2 倍頻(7.16MHz),I 切 ~1.5MHz、Q 切 ~0.5MHz(簡單版全切 1.5MHz=0.07 歸一化)。
- **YIQ→RGB(FCC 矩陣)**:`R=Y+0.9563I+0.6210Q`、`G=Y−0.2721I−0.6474Q`、`B=Y−1.1070I+1.7046Q`,clamp 0–1。

## 3. ⚠️ comb filter 是 NES 的致命陷阱
- **標準 NTSC** 每行相位偏 **180°** → 1-line comb 完美抵銷。
- **NES(2C02)** 每行偏 **120°**(227.33 週期,0.33×360=120°),**3 行一循環** → **標準 1-line comb 不抵銷、色彩嚴重混疊**(當年高階 2D-comb 電視看 NES 反而比廉價 notch 電視醜!)。
- **→ 一律用 Notch(BPF 減法)做 Y/C**,最忠實 80/90 年代 90% 電視,完美重現 color bleeding + **dot crawl**(相位每行差 120° → Y 殘留 3.58MHz 能量在邊緣像螞蟻爬)。**別寫 comb**(除非做能偵測非標準訊號降級回 notch 的現代 decoder IC)。

## 4. CRT 顯示端(優先序 + 貢獻度)
1. **Scanline(40%,必須)**:NES 240p 只畫奇數場;每行畫面像素間留黑/降亮 → 立刻消 LCD 塊狀感。
2. **Phosphor blur / 水平高斯(30%,強烈建議)**:電子束光點高斯 + 水平拖影;對 RGB 做 1D(水平)Gaussian → 順便柔化 notch 殘留的 dot crawl。
3. **Gamma(15%,易且必要)**:CRT gamma ~2.2–2.5;輸出前 `pow(RGB,1/2.2)`,否則暗部死黑。
4. **Shadow mask / aperture grille(10%,看口味)**:RGB 交錯遮罩,shader 較合理。
5. **Bloom(5%,錦上添花)**:亮度>0.8 區大範圍 blur additive 疊回。

## 5. MVP 實作優先序(從零手寫,這樣「偷懶」最不失敗)
- **階段一 作弊版**(不解析 sync、寫死相位):用生成器的 H-Sync 計數器切行 → 簡單 FIR BPF 抽 C、`Y=CVBS−C` → **不寫 PLL,直接用 `(Frame+Line)%3` 推該行理想相位**解調 → YIQ 矩陣。**結果:已能看到彩色瑪利歐,只是邊緣有毛刺。**
- **階段二 軟體 PLL**:寫 `find_next_crossing()` + burst 相位量測;驗證 = 故意在生成器加隨機 jitter,看解碼器能否靠 colorburst 抓回正確顏色(鎖相失敗瑪利歐會變藍/綠)。
- **階段三 DSP 濾波優化**:調 Y/C FIR 階數(低=糊像 RF、高=銳像 AV)、I/Q LPF。
- **階段四 CRT 濾鏡**:scanline + 水平高斯 + gamma → 完美復古類比感,dot crawl 被揉成材質感。

**一句話**:數學很簡單,難在時序 + 濾波器設計;6×f_sc 取樣省掉所有插值;先作弊版驗證 YIQ 矩陣,再逐步加真實缺陷(PLL、FIR 延遲)。

關聯:視訊產生端 [[../suggest/2026-07-20-gemini-video-signal-chain]];AprNes 已有 `NesCore/NTSC_CRT/`(可對照 CRT 端)。
