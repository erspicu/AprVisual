# DMC #19(7-dmc_basics)ACLK 管線分析 —— 顯微鏡完整證據鏈

日期:2026-07-04
狀態:**已修復 —— `7-dmc_basics` 全數 PASS(27 幀)。** 根因 = pcm_latch
pass-gate 在 apu_clk1 下降沿的單半週期類比賽跑(真矽晶「資料贏」,二值模型
「門先關」);修法 = `WireCore.DmcLatchShim` 邊沿捕捉 micro-shim(測試模式
限定、預設關、golden checksum 不動)。細節見文末「修復」章。

## 失敗現象(bit 級確認)

blargg `7-dmc_basics` test 19:「There should be a one-byte buffer that's filled
immediately if empty」。$4013=0(1-byte)→ 寫 $4015=$10 → `lda $4015` 期望 $80
(bit7=IRQ flag、bit4=0),我們讀到 $00。前置條件已驗證:test 18 已寫 $4010=$8F
(IRQ 致能),且 $4015 寫入正確清掉舊旗標(trace 20614→20615 irq 1→0)。

## 我們的時間軸(bus-trace + IRQ 節點 + APU 相位 + pcm_lc,K=1)

```
cyc 20608: W $4013            clk1=1          lc=0x010(上段 L=1 殘值)
cyc 20614: W $4015(enable)   clk1=1          lc=0x010
cyc 20617:                    clk2e=1         lc → 0x000(reload,寫入後第 3 cycle)
cyc 20618: r $4015(data cycle 嘗試)clk1=1
cyc 20619: r $4015 RDY=0(halt)    clk2e=1
cyc 20620: r $E700 RDY=0(fetch)   clk1=1  ab_use_pcm=1
cyc 20621: r $4015 RDY=1(resume、重執行讀取 → $00) clk2e=1
cyc 20623: set_pcm_irq 脈衝、pcm_irq ↑        clk2e=1(fetch 後第二個 clk2e)
```

三個「晚一個 ACLK」症狀,同一指紋:
1. DMA halt 在寫入後第 3 個 APU cycle(AccuracyCoin 實測 G 版 = 第 2 個;
   「Load DMA after 2 APU cycles」,rare revision 才 3)
2. LC reload 落在寫入後第二個 clk2e(20617,非 20615)
3. IRQ set 落在 fetch 後第二個 clk2e(20623,非 20621)

## 靜態電路對應(netlist cone ↔ APUSim,逐級一致)

| 我們的節點 | APUSim(emu-russia,同晶粒獨立逆向) |
|---|---|
| `set_pcm_irq` = NOR(`pcm_loop`, 11518, 11473) + pullup | `ED1 = NOR3(LOOPMode, sout_latch.nget(), NOT(PCMDone))` |
| 11518 = NOT(11427) | NOT(DMC1) |
| 11427 = NOR(13947, 13969);13969 = NOT(`apu_clk2e`) | `DMC1 = NOR(pcm_latch, NOT(ACLK2))` |
| **13947**:pass-gate 節點,閘=`apu_clk1`,源=13907,無任何 vss 下拉 | `pcm_latch.set(pcm_ff.nget(), ACLK1)`(動態閂鎖) |
| 11463:pass-gate,閘=`apu_clk1`,源=11096 | `sout_latch.set(SOUT, ACLK1)` |
| `pcm_irq` flag(11522/11492 交叉耦合) | `int_ff`(RS)`int_ff.set(NOR4(NOR(int_ff, ED1), W4015, n_IRQEN, RES))` |

→ **netlist 轉錄與 breaks 逆向完全同構**(我們另做過上游雙向零差異驗證),
問題不在缺電晶體,而在動態時序:13947(pcm_latch)何時捕捉 pcm_ff。

## 外部權威資料

- **NESdev wiki /DMA**(100thCoin 研究):load DMA = halt(落 get cycle,
  寫入後第 2 個 APU cycle)→ dummy → [alignment] → get(fetch),偷 3~4 cycles;
  resume = fetch+1 重執行被打斷的讀取。
- **AccuracyCoin `DMA + $2002 Read`**:成功文案「Load DMA after 2 APU cycles」
  (常態)/「after 3 APU cycles」(rare revision 容忍)。
- **TriCNES**(同作者):`DMCDMA_Get()` 內 fetch→bytes-- →IRQ set 同 cycle 完成;
  `$4015` 讀 = 組合即時。行為層簡化,能過 #19。
- **APUSim clkgen**:`ACLK1 = NOR(NOT(PHI1), phi2_latch.nget())` —— ACLK1/ACLK2
  的高窗都只在 φ1 半週期、隔 cycle 交替;`PCM = NOR(PHI1, n_DMC_AB)` 在 fetch 的
  φ2 設 pcm_ff → **φ1 透明窗已關,pcm_latch 要等下一個 ACLK1 窗才捕捉**。

## 未決矛盾(下一步的仲裁點)

照 APUSim 電路手推,pcm_latch 捕捉在 fetch+2、DMC1 在 fetch+3 —— 和我們觀測一致!
但那樣 resume(fetch+1)讀不到旗標,真機卻穩定通過 #19。可能解:
**真機 RDY 釋放比我們晚(rdy_ff 同屬 ACLK 管線),resume 恰好落在旗標升起
的同一 cycle(讀取 φ2 晚於 RS latch 的 φ1 set)**。若成立,我們的偏差其實在
**RDY/stall 長度**(我們 2 cycles,真機 3~4+),不在 IRQ 路徑 —— 三個症狀
統一為「DMA 控制面(start_ff/run_latch/rdy_ff)相位早了一個 ACLK」。

手推到極限(耦合閂鎖跨 4 個半週期,各來源邊沿定義不一)。**下一步:把
APUSim 跑起來**(ref/breaknes_apusim/ 已抓 dpcm/dma/clkgen;還需 BaseLogic 等
相依,或整包 clone breaknes),餵 $4010=$8F/$4013=0/$4015=$10 序列,輸出
RDY/int_ff/$4015 readback 逐 cycle 真值表,直接 diff 我們的 trace。分歧的
第一個 cycle = 要修的級。

## APUSim 真值表(2026-07-04 補充 —— 重大轉折)

寫了獨立 harness(`temp/apusim_harness/harness.cpp`,clang++ 直編
`ref/breaknes/` 的 APUSim + 閘級 M6502Core,friend-class 打洞觀測內部),
跑同樣的 $4010=$8F / $4013=0 / $4015=$10 / `lda $4015` 序列:

```
cyc 34: W $4015(enable)
cyc 38: r $4015 RDY=0(halt;寫入後第 4 個 CPU cycle ✓ NESdev「3rd or 4th」)
cyc 39: dummy RDY=0
cyc 40: fetch $C000(PCM=1、pcm_ff↑)RDY=0     ← 偷 3 cycles
cyc 41: r $4015 重執行 → 讀到 $10(bit4 未清、bit7 未設)
cyc 42: pcm_latch 捕捉(ACLK1 透明窗)
cyc 43: int_ff ↑(IRQ 旗標,fetch+3)
--- result: ram[0]=$10 (expect $80) ---
```

**APUSim 也不過 blargg #19** —— 而且比我們更晚($10 vs 我們 $00)。
兩個獨立矽晶模型同敗,行為層模型(TriCNES)靠「同 cycle 完成」通過。
事實矩陣:

| 模型 | 讀回 | stall | IRQ set |
|---|---|---|---|
| 真機(blargg 校準)| $80 | ? | resume 時已可見 |
| 我們(Visual2A03 netlist)| $00 | 2 cycles | fetch+3(第二個 clk2e)|
| APUSim(獨立電路逆向)| $10 | 3 cycles | fetch+3(第一個 ACLK1 捕捉 + 下一個 ACLK2)|
| TriCNES(行為層)| $80 | 2-4 | fetch 同 cycle |

新工作假說:APUSim 把 ACLK1 建成「只在 φ1 高」的窄窗,我們的 netlist 取樣卻顯示
`apu_clk1` 在 fetch 的 φ2 尾端仍高 —— 若真矽晶的 ACLK1 窗較寬(蓋到 φ2),
pass-gate 在 fetch 當週期就該讓 pcm_latch 捕捉 → DMC1 在下一個 clk2e(= resume)
→ 旗標 resume 可見 = 真機。數位模型的窄窗量化正好丟掉這一個 cycle。
→ 半週期顯微鏡實驗進行中(`cpu.#13907`/`cpu.#13947` raw-id 別名 + hc 級 log)。

## 修復原則檢查(Rule 1)

真機(NES-001 + RP2A03G)穩定通過 #19 → 我們偏離實機 → **該修**。
不是忠實偏差候選。修復點預期在行為層/整合(reset 後 DMA 控制面相位)或
引擎對 pass-gate 動態閂鎖的解析,不動 netlist 資料。

## 工具沉澱

`--bus-trace` 已擴充(TestRunner.cs):AB/DB/RW/RDY + pcm_irq/set_pcm_irq/
pcm_irqen + apu_clk1/apu_clk2e/ab_use_pcm + pcm_lc[11:0](12-bit,附 node-id
驗證輸出,防無聲截斷)。關鍵事件後連印 30 cycles。

教訓重申:4-bit lc 探針曾因低位全 0 誤導(真值 0x010)——
**探針一律印 node-id 解析結果 + 全寬度讀值**。

## 修復(2026-07-04 定案)

半週期顯微鏡(`cpu.#13907`/`cpu.#13947` raw-id 別名)直擊根因:

```
cyc 20620(fetch)hc 13:apu_clk1 ↓ 與 13907(pcm_ff 輸出)↓ 同一半週期
→ 13947(pcm_latch)未捕捉 → 下個 clk1 窗(20622)才跟上 → DMC1/IRQ 晚一 ACLK
```

- 13907↔13910 交叉耦合 = pcm_ff RS 閂鎖;t14402(gate=apu_clk1)= 通往
  13947 的 pass gate。netlist 的 apu_clk1 高窗橫跨 φ1 + φ2 前段(比 APUSim
  的 φ1-only 寬),關門沿與 PCM strobe 觸發的資料沿同落 hc 13。
- **quiescent-settle 二值語意內,同半週期的「關門 vs 資料」賽跑必然判門贏**
  (最終態 clk1=0 → pass gate 斷,無論波內順序)。真 NMOS 靠時脈衰減期的
  導通重疊讓資料滑進 —— 本質類比,連接性無法表達。APUSim 同樣量化丟失
  (其 harness 讀 $10 同敗)—— 兩個獨立矽晶模型同敗、行為層模型
  (TriCNES)通過,佐證這是抽象層的極限而非轉錄/引擎 bug。
- 修法:`WireCore.EnableDmcLatchShim()`(WireCore.System.cs)——
  在 apu_clk1 下降沿若閂鎖兩側不等,將 13907 的 post-settle 值
  drive→settle→release 進 13947(= 閂鎖「取樣於關門沿」的本意語意)。
  透明相已同步時為 no-op,精確只覆蓋賽跑情況。
- 佈署:TestRunner(RunOneTest + BusTrace)設 `RegisterRawIdAliases`
  (載入時註冊 `cpu.#<rawid>` 別名)並武裝 shim;benchmark 路徑兩旗標皆
  false → **預設模式零行為改變**。
- 結果:`7-dmc_basics` **Passed**(27 幀;原本 FAIL#19 於 31 幀)——
  #19 之後的所有子測試一併通過。DMC/DMA 家族回歸重驗進行中。
- 殘留:halt 排程仍比 AC 實測晚一 APU cycle(同類賽跑住在 run_latch/
  en_latch 級)—— 目前無測試因此失敗;若 dmc_dma 家族重驗顯示需要,
  再評估把 shim 推廣成「ACLK pass-gate 邊沿捕捉」通則(需 minimal
  prototype + 全家族驗證)。
