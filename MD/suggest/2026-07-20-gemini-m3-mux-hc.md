# Gemini 諮詢:ALERead mux 的 MUX_HC 能否從 M3 延遲推導(2026-07-20)

**問題**:MUX_HC=13,13,25,44,52(ALE 讀取相位 mux 的 5 個 hc:swallow/replay/ale-freeze 窗)
能否從 M3(Elmore 延遲,⊕ M5 的 '373)推導,而非手調?完整 prompt + 回覆見
`2026-07-20-gemini-m3-mux-hc-reply.txt`。

## Gemini 核心結論(修正我方先前「M3 可錨定全部比例」的說法)

**關鍵洞見:這 5 個值不是同一種東西,要拆成兩類。**

- **量級檢查**:hc ≈ 23.2ns(NTSC master clock)→ 52hc ≈ **1.2µs**。但晶粒內 NMOS 延遲 10–50ns、
  '373 傳播 12–18ns、pad settle 50–100ns。→ **25/44/52 太大,根本不是物理淨延遲**。

- **13,13(~300ns)= 真物理**:對應 PPU pad 驅動 → 匯流排電容 → '373 閂鎖 的 setup/hold + pad settle。
  **只有這兩個能物理錨定**。

- **25/44/52 = 架構級排程假象**:因為 CPU 排程比 PPU 類比多工早約 1 個 CPU cycle(~12–24hc),
  這三個大值是**離散引擎的相位對齊填充**(discrete-time FIFO/sync buffer),**不是 RC 延遲**、
  M3 永遠算不出;但可由已知的相位 offset 用**公式**表示:`CPU_EARLY_PHASE_OFFSET_HC − PHYSICAL_SETTLING_HC`。

## Gemini 的其他判斷
- **Elmore 釘不死整數 hc**:depletion-load 非線性(定電流→電阻)、grid aliasing(1.8hc 該排 1 或 2?)、
  缺板級寄生(PCB+封裝+'373 ≈ 15–20pF,晶粒內是 fF)。M3 只能給**晶粒內比例**。
- **AWE/moment-matching = 浪費**(garbage in, pristine garbage out);logical effort 是設計工具、非萃取工具。
- **跨晶片合成**:PPU pad driver(萃取)→ 板子 lump ~20pF → '373 用 **TI datasheet**(別 M3 它,18ns)→ 線性相加。
- **RC→hc**:定閾值(V_IL≈0.8V、V_IH≈2.0–2.4V)、t_fall/t_rise 公式、取 **ceil**。

## Gemini 最推薦的路(取代純解析 M3)
**Boundary micro-SPICE 重模擬**:把邊界子電路(PPU ALE pad + AD pad drivers + '373 + CPU receivers)
匯出成 SPICE、手動加 ~20pF 板電容、ngspice transient、量 ALE→CPU 內節點過 2.0V 的 ΔT、除以 hc period 量化。
得到**真物理 hc**後,把 MUX_HC 正式拆成:
- **Real Physics(13s)**= boundary-SPICE 導出;
- **Engine Alignment Padding(25/44/52)**= `CPU_EARLY_PHASE_OFFSET_HC − PHYSICAL_SETTLING_HC` 公式。

## 對本專案的影響
- s1a.html 的 ALERead mux 特殊個案敘述(「M3 可錨定」)**應修正**:只有 13,13 物理可錨(且 boundary-SPICE
  比 M3 準),25/44/52 是架構填充、應改成相位 offset 的公式,不是 RC 推導。
- 這把「5 個手調旋鈕」化約成「2 個物理值(可算)+ 3 個公式值(從已知 offset 導出)」→ 幾乎完全去手調。
