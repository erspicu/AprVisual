# even_odd 整合偏移修復戰役 —— 量測紀錄(已結案 2026-07-06)

> **結案(2026-07-06)**:已修復,`10-even_odd_timing` 現 **PASS**(輸出 `08 08 09 07`)。
> 做法是**窄窗寫延遲 shim**(全域,`--ppu-write-delay 16`,只在 pre-render
> `vpos=261, hpos=338..339` 延遲 `$2001` 開/關背景的轉態 16 hc;disable 側夾互補
> 節點 `/bkg_enable`)——在對齊 7 補上 ~1-dot 跨晶片寫路徑偏移,與 NMI-edge 家族
> 同時綠 → 當時全量回歸 **145/1(99.3%)**(現行基準:**146/1**,147 測)。詳見知識庫 §3.1 #13 與
> [最終修復紀錄](../toDoNext/202607062345-10-even_odd_timing修復紀錄.md)。
> 從源頭用 PPUSim 交叉比對 `$2002` 絕對 master-clock 延遲來仲裁偏移(而非用 shim 補償)
> 仍列後續研究。**以下為當時的調查量測紀錄(保留供追溯)。**
>
> 原目標:仲裁並修正雙 netlist 整合的 ~1-dot 絕對相位偏移,讓 NMI 家族與
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

## PPUSim 仲裁結果(task 2,2026-07-05 完成)

harness:`temp/ppusim_harness/ppu_truth.cpp`(clang++ 直編 emu-russia
RP2C02G;friend-class `UnitTest` 打洞讀 `hv_fsm->INT_FF`/`h`/`v`/
`regs->bge_latch`)。同場景量測:

| 量 | PPUSim(RP2C02G) | 我方 netlist | 判定 |
|---|---|---|---|
| VBL flag set 位置 | **V=241, H=1** | **V=241, H=1**(--probe-vbl) | **逐 dot 相同** |
| frame 交替 | 714736 /714728 hc(差 **8 hc = 1 dot**) | skip@339(339→0 跳 340) | 都對 |
| dot-skip 決策點 | dot 339 | dot 339(--probe-2001) | 相同 |

**核心結論:兩顆晶片在孤立情況下逐 dot 一致**(VBL 都在 (241,1)、skip 都在
339)。所以 ~1-dot 偏移**不在任一晶粒內部**,而是 CPU↔PPU 通訊的**差分傳輸
延遲** —— 且必然在**寫路徑**(even_odd 失敗碼 3「skip 相對 enable BG 太晚」、
X=7 vs 8;讀路徑的 NMI 家族現況 PASS 不能動)。

## 修法:$2001 write-effect delay(task 3,實作完成待驗證)

- **否決 global clock skew**:實測 1-hc PPU 時脈延遲 → 所有測試 detection=none
  (CPU/PPU I/O 失步);且理論上「讀+寫同時移」= 另一個 K,無法破互補鎖。
- **採用差分寫延遲**:`--ppu-write-delay N`(opt-in,預設 0)用儀器級
  InstClamp 把每個 `bkg_enable`/`spr_enable` 轉態夾住 N hc(= 暫存器晚 N hc
  載入)。零圖足跡、讀路徑不動。**probe 驗證**:delay=8 時 bkg 上升由 t=4231704
  延到 4231712(正好 1 dot),selftest ALL PASS。
- 新增引擎 API:`InstClampHigh`(對稱於 InstClampLow,壓 Pwr);`InstRelease`
  改為同時清 Gnd/Pwr。
- **待驗證**:K=1 下掃 even_odd delay∈{4,8,12,16} + 05-nmi delay∈{4,8,16}
  (7 核並行)—— 找到讓 even_odd PASS 而 NMI 續 PASS 的 N。
