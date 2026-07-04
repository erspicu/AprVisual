# even_odd 整合偏移修復戰役 —— 量測紀錄(進行中)

> 目標:仲裁並修正雙 netlist 整合的 ~1-dot 絕對相位偏移,讓 NMI 家族與
> 10-even_odd 在同一對齊共同通過(→ 144/1)。定性依據:Gemini 諮詢
> `tools/knowledgebase/q/a_even_odd_doctrine_20260705.md`。

## 關鍵推論(2026-07-05)

**K 掃描同時平移讀與寫的格點 → 互斥在所有 K 成立 → 偏移是 K 不變量 =
「寫路徑 − 讀路徑」的差分誤差。** 純相位旋轉(K 可達)已被完備枚舉排除;
候選 = 跨晶片存取的傳輸延遲不對稱:讀取回應(PPU→CPU)必須在 CPU 取樣前
出發 = 有效取樣「偏早」;寫入請求(CPU→PPU)在 φ2 尾抵達 = 生效「偏晚」。
真機上兩者朝反方向偏;我們兩者皆理想化為 0(同波完成)。

## 我方 netlist 量測(--probe-vbl / --probe-2001,預設 K)

### M1 讀側($2002,05-nmi_timing)
- `vbl_flag` 升起於 **vpos=241, hpos=1**(t=657643)—— 與 NESdev 真機記載一致
  (晶粒內部位置保真 ✓)。
- $2002 讀取:ab=2002 於 hpos 15(φ2 起),`/r2002` 同 dot 拉低,
  hpos 16 `read_2002_output_vblank_flag`=1、io_db7=1、db=$80,
  flag 同時讀清(vbl_flag 1→0)。讀取「指派」span ≈ dots 15-18。

### M2 寫側($2001,10-even_odd_timing)
- 寫入 strobe:`/w2001` 於寫 φ2 低、`write_2001_reg` 脈衝、`bkg_enable`
  **同 dot 翻轉**(例:dot 45、dot 337)。
- 生效鏈:bkg_enable → `rendering_1..4` 管線(數 dot 內就位)→
  `hpos_eq_339_and_rendering`。
- **skip 現場(B4)**:寫在 dot 337 → bkg 337 翻高 → r4 到位 → dot 339
  `h339`+`skip_dot` 同時點火 → **hpos 339→0(跳過 340)**。
- **奇偶對照(B1)**:唯一差異 `even_frame_toggle`=1 → 不跳(339→340→0)。
  奇偶機構 netlist 內完全正確;skip 條件 = h339 ∧ (evenT=0)。

### 節點名冊(2C02,全部有名)
`skip_dot`(4427)、`even_frame_toggle`(4932)、`/w2001`(4116)、
`write_2001_reg`(4117)、`bkg_enable`(11779)、`rendering_1..4`
(4725,5506,4396,5660)、`hpos_eq_339_and_rendering`(1386)、
`vbl_flag`(4994)、`set_vbl_flag`(1364)、`read_2002_output_vblank_flag`
(3929)、`/r2002`(3926)。

## 工具

- `--probe-2001 <rom>`:窗 A = enable 寫($2001 bit3=1)→ /w2001/wreg/bkg/
  rendering 管線 per-hc;窗 B ×10 = pre-render dot 336..3 的 skip 窗
  (h339/skip_dot/even_frame_toggle/hpos 序列)。
- `--probe-vbl <rom>`(既有):flag-set dot + $2002 讀取鏈。

## 下一步(task 2)

PPUSim harness(ref/breaknes/Chips/PPUSim;dataread.cpp = 讀資料路徑):
同場景量 Δr(flag-set→D7 可見)與 Δw(寫 strobe→enable 生效)的 PCLK 精度
參考值;與我方差分比對 → 得偏移方向與大小 → 板級修正(test-mode 閘)。
仿 temp/apusim_harness 的 clang++ 直編 + friend-class 手法。
