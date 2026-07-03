# DMC #19(7-dmc_basics)ACLK 管線分析 —— 顯微鏡完整證據鏈

日期:2026-07-04
狀態:**包圍圈縮至單一電路級;待 APUSim 真值表仲裁**

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
