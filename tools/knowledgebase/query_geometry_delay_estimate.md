# 從 die 幾何估算傳播延遲(hc 級)—— 物理參考依據可行性諮詢

## 你是誰
資深 NMOS 時代 IC 物理 + EDA 時序分析工程師。AprVisual.S1:二值開關級 NES 模擬器(2A03+2C02,
Visual6502 系 netlist),settle-to-quiescence 零延遲。M6 戰役已實測出四個延遲錨點並以 shim 修復;
S1a 計畫要把延遲通則化成「時序標註網表」(SDF 式 sidecar),**但標註的數字目前只能靠逐測試校準**。

## 手上的原料
- **segdefs**:每 segment = `[node, pull('+'/'-'), layer, x1,y1, x2,y2, ...]` 多邊形頂點 —— 有**幾何**
  (可算面積、周長、bounding box、長寬比)和**層別**(Visual6502 慣例:metal / poly / diffusion 等)。
  目前解析後全數丟棄。
- **transdefs**:`[name, gate, c1, c2, bbox[4], geom[5]]` —— 器件位置與 bbox(≈ W/L 驅動強度代理)。
- **圖結構**:node = 合併後的 net;每 net 的扇出(gate 數)可數;pass-gate 串聯鏈可走訪。
- **製程**:Ricoh 版 NMOS,~1982,約 3-6 µm 級;depletion-load ratioed logic(上拉弱)。
- **實測錨點(黃金校準點,test-ROM 驗證)**:
  1. 跨晶片 $2001/$2007 效果延遲 ≈ **24 hc(3 dots ≈ 70ns)**(含 pad/封裝/板線/接收端同步)
  2. $2001→BG shifter-reload 邊界 ≈ **16 hc(2 dots ≈ 47ns)**
  3. dot-339 rendering-enable 傳播 rise/fall ≈ **16/18 hc 不對稱**(NMOS 上拉慢下拉快方向?實測 rise 16 fall 18)
  4. 2A03 內部 $4015→DMC sequencer 控制路徑 ≈ **1 CPU cycle 級(~24hc)存活窗**
- 時間粒度:主時鐘 21.47727 MHz → **1 hc ≈ 23.3 ns**。1982 NMOS 單級閘延遲 ~10-50 ns
  → 一級邏輯 ≈ 0.5~2 hc。

## 想法(使用者提案)
用幾何 + 層別 + 扇出 + 器件尺寸,算出 per-net 的傳播延遲估計(Elmore 一階?),量化到 hc,
作為時序標註的**物理參考依據** —— 至少給相對排序與分級,而不是每個延遲都要靠測試逐一校準。

## 問題(逐點,工程判斷)
1. **通不通?** 在這種資料條件(2D 多邊形 + 層別,無製程 deck)下,Elmore/Penfield-Rubinstein 一階
   估算對「hc 級分級」(sub-hc 可忽略 / 1-2hc / pad 級 2-5dot)的可行性?目標不是 ps 精度,
   是分類與排序。
2. **估算配方**:給一個實務 pipeline —— 從 polygon 抽 R(層別 sheet-Ω/sq × squares,長寬怎麼從
   多邊形近似)、抽 C(面積 × 每層 fF/µm² + 下游 gate 電容 = 扇出 × 器件 bbox 面積)、
   pass-gate 串聯電阻(NMOS on-resistance 量級)、驅動端強度(depletion 上拉 vs enhancement 下拉)。
   1982 6µm NMOS 的典型參數表(sheet R、Cox、Ron)請給文獻級數字。
3. **rise/fall 不對稱**:ratioed NMOS 的弱上拉怎麼進模型(rise 用 depletion-load 電流、fall 用
   下拉管)?我們實測 16/18 的方向(rise 16 fall 18)合理嗎,還是暗示量的是別的東西?
4. **校準策略**:只有 4 個錨點,怎麼回歸?(單一全域 scale factor?分層 scale —— 晶片內 vs pad?
   rise/fall 各一?)錨點 1 含封裝/板線(幾何資料沒有),是否應該把跨晶片獨立成常數、
   幾何模型只管晶片內?
5. **誤差與陷阱**:2D 抽取漏掉的東西(fringe、耦合、contact/via 電阻、溫度電壓、個體差)對
   「分級」目標的殺傷力?哪些會系統性高估/低估?
6. **前例**:Crystal / TV(Berkeley NMOS timing analyzers)、或社群有沒有人對 Visual6502 系
   netlist 做過幾何時序標註?有沒有現成參數可借?
7. **投資報酬**:相對於「每個延遲逐測試校準」,這條路的甜蜜點在哪?(例:P5 排名器 + 錨點回歸
   → 產生 sidecar 初稿 → 測試只驗不校?)
