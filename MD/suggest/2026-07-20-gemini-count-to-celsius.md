# Gemini 諮詢:6502 上把衰減 count 轉攝氏(快速、0.1°C)(2026-07-20)

完整回覆 `2026-07-20-gemini-count-to-celsius-reply.txt`(gemini-3.1-pro-preview)。
背景:溫度計 ROM 量到 24-bit「衰減 count」,要在無乘除法/浮點的 6502 上轉成 0.1°C 攝氏顯示。

## Gemini 推薦(採用)
**查表 + 2 的冪次二分搜(radix search)+ 連減法 BCD** —— 全部 division-free。

- **(a) 查表法 = 6502 最佳解**:絕對精確(誤差留在 PC 端)、執行成本極低(只讀記憶體 + 比較)、開發最簡單;
  512 點 × 3 byte ≈ 1.5KB 對 NES ROM 微不足道。**採用。**
- **(b) 直接算 log + 倒數 = 惡夢**:24-bit 定點 log2 + 24-bit 除法,幾百 byte、數千 cycle、兩端易精度崩潰。不做。
- **(c) 分段線性**:還是得先算 ln(count) 或切數十段;既然都要查表不如直接查終極結果。
- **(d) log2 + 小內插**:適合無 ROM 空間的環境(Atari 2600 4KB);NES 上殺雞用牛刀。

**具體設計**:
1. 資料設成 2 的冪次(512 點,0.1°C 一階)。
2. **SoA**(Structure of Arrays):拆成 `thr_lo[512]`/`thr_mid[512]`/`thr_hi[512]` 三個獨立陣列(6502 尋址友善)。
3. 表**單調遞減**(溫度越高 count 越小)。
4. **免除法二分搜**:從 bit8(step=256)測到 bit0,每步 test=idx+step,若 `table[test] >= count` 就保留該 bit
   → 9 次迴圈組出 9-bit index。
5. 顯示:**offset 平移 + 連減法 BCD**(反覆減 100/10 數位數)處理整數/小數/負號,避開所有除法。

## 我的實作(採納)
`tools/thermo_rom/thermo.asm` + `build.py`:
- build.py 從 `counts_by_degree.json`(逐度實測)產生 0.1°C 表 `thermo_table.inc`(thr_lo/mid/hi,index i == 溫度 tenths)。
- ROM:radix 二分搜(read_thr 讀 SoA)→ idx(0..511)→ 連減法 BCD → 顯示 `NN.N DEGREE CELSIUS`。
- 字型改 ASCII 索引(tile == ASCII 碼),ROM 直接寫 ASCII 到 nametable。

**驗證 round-trip**:0/10/20/25/30/40°C 精確;暖端受量測解析度限制(衰減快=count 粗)~±0.5°C(43&44→43.5、50&51→50.5),冷端真 0.1°C。

關聯:深入專文 `WebSite/s1a/nes-thermometer.html`、[[openbus-shim-lastbyte-model]]、記憶 `MD/memory/09_aprnes-openbus-temperature-decay.md`。
