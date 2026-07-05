# 諮詢:行為手把「載入期圖變更」造成探針效應,per-test scope 是否恰當?有無通則?

> 這是先前 `q/a_dbl2007_footprint_20260705`(探針效應 → 你提出 Tail Allocation
> 方案 C + 儀器級 Force 方案 E)的**同類但不同層**問題,想請你用同一套通則
> 檢驗我們這次的做法。

## 引擎背景(同前)

Visual6502 家族電晶體/開關級 NES 模擬器(C#,2A03+2C02 netlist + NES-001 板卡
TTL 模組)。**quiescent-settle**:每 half-cycle toggle clock → BFS 傳播到穩態。
載入期:組合各模組 → 分配節點 id → **class-major 重編號**(效能最佳化)→ hot
arrays 定型。**上電 CPU-PPU 對齊是離散抽籤**(÷12 vs ÷4,4 種相位),
`ppu_vbl_nmi` / `vbl_nmi_timing` 家族對對齊**極度敏感**(判定差 1 dot 就翻)。

**你先前的通則**(我們已採用於 dbl2007):
- 方案 C(Tail Allocation):主晶片 0~N 節點 id 與 adjacency 順序不可動,測試儀器
  一律掛在尾端 → BFS 展開樹同構 → 零探針效應。
- 方案 E(儀器級 Force):`InstClampLow/High` 直接 OR Gnd/Pwr 旗標進節點(絕對
  覆蓋權、零圖足跡),取代載入期假電晶體。
- 結論:**per-test scope = 過度擬合;測試儀器不該改 DUT 的圖**。

## 這次的問題:EnableJoypadHandler 是「載入期結構變更」

我們的測試模式旗標 `EnableJoypadHandler` 做**兩件載入期圖變更**,而且**對每個
測試都開**:

1. **模組置換**:`PreloadModuleAs` 把閘級 `nes-pad`(CD4021 移位暫存器,真實
   TTL netlist)整個換成 `nes-pad-behavioral`(行為層影子模組,節點數/結構不同)。
   - 為何需要:閘級 4021 在穩態語意下**結構性不可驅動**(pslatch pass-gate
     反向驅動輸入節點,群組內 GND 恆勝,「放開的按鍵」永遠寫不進去)→ 只能整個
     模組行為層化,才能注入手把輸入。
2. **tie 極性改接**:載入 connections 時,把 u7/u8(LS368 buffer)的 6 條備用
   輸入(1A4/2A1/2A2)從綁 `vss` 改綁 `vcc`。
   - 為何需要:board def 綁 vss → LS368 反相成 db2-4=1 → $4016 取指得 $5C 而非
     $40(RTI)。真機那些輸入**浮接**、讀高、反相成 0。改接 vcc 模擬浮接讀高。
   - 修好 `cpu_exec_space_apu`(需要 $4016 open-bus = $40)。

**回歸**:因為對「每個測試」都開,這兩個載入期圖變更**重擲了對齊抽籤** →
`ppu_vbl_nmi`(05-nmi、even_odd 等)、`vbl_nmi_timing` 家族從通過變成**掛死**
(judgment 幀 215/75 → 完全不出判定)。`--no-shims`(關掉所有 runtime shim)
**仍掛** → 證明是這個**載入期圖變更**、不是 runtime shim。

## 我們目前的修法:per-test scope

`--joypad` 旗標預設**關**;catalog 加 `needsJoypad`,只有真正需要手把/tie 的 7 個
測試(exec_space×2、dma_4016_read、read_joy3×4)才傳 `--joypad`;其餘 138 個測試
回到**乾淨的原始圖**(= abb9348 基準,那個 commit 這兩家族還通過)。

**我們自知這和你先前「per-test = 過度擬合」的結論衝突**,所以想請你檢驗:
這次 per-test 是否**反而正確**?還是有更好的通則?

## 兩個變更的本質差異(關鍵)

- **tie 改接**(6 條 vss→vcc):只動 6 條既有連線,節點總數不變。理論上可用
  Tail Allocation 精神?但 u7/u8 是 real rail 相關節點,runtime `InstClampHigh`
  (Pwr)**壓不過 Gnd**(LUT:Gnd > Pwr)→ 無法用零足跡 runtime force 覆寫 vss tie。
  或者:真機那 6 個輸入本來就**該浮接(無連線)**,board def 綁 vss 是建模瑕疵 →
  「正確」做法是**載入時不加這 6 條 vss tie**(但這仍是載入期圖變更)。
- **模組置換**:`nes-pad` → `nes-pad-behavioral` 是**取代既有模組**(非新增儀器),
  節點數/結構不同 → 置換點之後**全部重編號**。Tail Allocation 針對「新增」儀器
  有效,但「取代」既有模組似乎無法尾端化(它佔的是圖中段的既有位置)。

## 請回答

1. **per-test 這次對不對?** 以「載入期結構變更 vs runtime 值覆寫」的區分,
   per-test scope 對「模組置換」這種結構變更是否**反而是正解**(而 dbl2007 那種
   純值覆寫才適合零足跡全域)?請明確區分兩類。
2. **模組置換能否零擾動?** 有沒有辦法讓 `nes-pad → nes-pad-behavioral` 的置換
   **不重編號主圖**(例:保持外部 pin 節點 id 不變、內部行為節點掛尾端;或雙圖
   /影子網表架構)?還是結構置換本質上做不到、只能 scope?
3. **tie 改接的最佳解?** (a) 載入時不加那 6 條 vss tie(視為修正 board def
   建模瑕疵,全域生效)?(b) runtime 覆寫(但 Gnd>Pwr 擋住)?(c) 保持 per-test?
   哪個最通則且不擾動對齊?若「不加 vss tie」是對的,它會不會也擾動對齊?
4. **元層面:如何預防這類回歸?** 這次 bug 的本質是「一個載入期圖變更悄悄重擲了
   沒重跑的對齊敏感家族」。有沒有工程機制(例:netlist graph fingerprint /
   BFS-order 指紋,一旦圖變就標記哪些對齊敏感測試必須重跑)能系統性防止?
5. 若你認為有第五種做法(如 dbl2007 案的方案 E),請提出。
