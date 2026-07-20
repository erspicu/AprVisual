# Gemini 諮詢:OpenBus last-byte 衰減的實作(2026-07-20)

完整回覆 `2026-07-20-gemini-openbus-impl-reply.txt`。問:怎麼把手調的 open-bus 衰減換成有原理、可計算的模型。

## 核心重點
1. **正確模型 = Lazy timestamp(懶惰時戳)**:確認我方做法對。驅動時記 `last_driven_hc + last_val`;浮接時,
   值=1 僅當 `last_val==1 且 (now − last_driven_hc) < threshold_hc`。不用 event wheel(25M cycle 未來事件太重)、
   不用 leaky integrator(熱迴圈成本)。NES 匯流排是 φ2/read-enable 同步取樣,所以在 settle 解浮接時 evaluate 就對。
2. **衰減律 = 線性、非指數!** 反偏 PN junction 漏電是**定電流**(對電壓近乎無關)→ `dV/dt = −I_leak/C`,
   `V(t) = V_OH − (I_leak/C)·t`。**這修正了「RC 指數」的想像** —— 是等速下降的斜坡,不是 RC 曲線。
3. **算常數**:`t_decay = C·ΔV/I_leak`,`threshold_hc = C·ΔV/(I_leak·t_hc)`。V_OH≈4.5-5V、V_IL≈1.5V、ΔV≈3.5V、
   C≈20pF、I_leak≈80pA、t_hc=23.28ns → ~600ms → ~25M hc。
4. **逐位元獨立、非整 byte**:8 條線各自 C/漏電不同 → byte 一位一位爛(0xFF→0xEF→0xEE→0x40→0x00,幾十 ms 窗),
   spread ±10-20%。**必須 per-bit timestamp + threshold** 才 test-accurate。
5. **只 1→0**(漏電到 substrate/GND,無上拉 → 不會 0→1)。
6. **refresh 語意**:有到 VDD/GND 的 active path 才 refresh(寫/mapped 讀 refresh;open-bus 讀**不** refresh,
   charge-sharing 可忽略)。
7. **實作**:post-pass 在 8 條外部 DB 節點(不進熱 solver)。struct `{last_driven_hc, threshold_hc, last_val}`。
   test-mode toggle。⚠️ 區分外部 ext_bus vs **PPU 內部 io-latch**(2C02 內部 open bus,$2002 低 5 位)。
8. **從調到算(未來計畫)**:估 C_bus(15-25pF)、I_leak(50-100pA);bench 量測(寫 0xFF、等 N、讀出到 scope、
   掃 N → 看 per-bit 瀑布落點)→ 種 per-bit threshold variance。
9. **誠實天花板**:`I_leak ∝ T³·exp(−Eg/kT)`,**溫度指數相關** —— 30°C 的 NES 比 20°C 快近 2×。所以**沒有普世
   絕對 threshold**。工程解:算 base threshold_hc(C=20pF,I=80pA)寫死當文件 + temp variance 參數 + 8 bit ±10% PRNG。

→ 這給了深入專文 #2(OpenBus)的全部素材:原理、算式、per-bit 瀑布、refresh、實作、未來計畫、天花板。
