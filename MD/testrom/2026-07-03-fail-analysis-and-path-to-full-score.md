# S1 測試失敗根因分析 —— 與滿分 AprNes 的差距在哪裡

> 2026-07-03。**純 study,未改任何 code。** 初稿以跑到一半的 22 個 FAIL 分析;**完跑後定稿:141 個全數完成 —— 115 PASS / 26 FAIL / 0 TIMEOUT**,新增 4 個 FAIL 已補入(見 §1.B、§1.E 的「完跑補充」)。
> 對照組 = AprNes(同一批測試全 PASS)。
> 使用者的三個懷疑:(1) 上電記憶體初始化差異 (2) CPU/PPU 與測試 ROM 預期硬體的差異 (3) 行為層記憶體/匯流排實作簡化。
> **結論先講:三個懷疑全部命中,並可再細分成六個根因類別;另有一個重要的哲學發現 —— 有些測試 S1 失敗正是因為它比 AprNes 更像真硬體。**

## 0. 一個先決的架構事實(study 的重要收穫)

檢視 `ROM32K.js` 模組定義發現:**S1 的匯流排三態是物理建模的** ——

```
cs ──反相器(netlist)──> gate 節點 ──> 8 顆 pass transistor(_d[i] ↔ d[i])
```

行為層 handler 只驅動模組**私有的 `_d` 節點**;晶片未被選取時 pass gate 關閉,
外部匯流排 `d[]` **真實浮空**(開關級「float = 保持前值」)。所以:

- **open-bus 的「保持」行為是忠實的**(不是行為層殘留驅動)——比原先擔心的好。
- 缺的是**時間性衰減**(真硬體電荷約 0.6-1 秒漏掉;開關級模型 float 永久保持)。
- 行為記憶體的簡化實際上只有:(a) 無存取時間(settle 內瞬時回應)(b) 無衰減 (c) DMA 等時序邊沿由 callback 觸發時機決定。

## 1. 22 個 FAIL 的六類根因

### A. 上電初始狀態(懷疑 1 命中)—— 2 個

| 測試 | 證據 | 根因 |
|---|---|---|
| `cpu_reset/registers` #2 | 實測 `P=$36`,期望 `P=$34`(差 Z flag) | netlist 的「上電」是人工程序(全節點放電→pullup→settle),settle 出 Z=1;真 2A03 上電類比競態結果 Z=0 |
| `blargg_ppu_tests/power_up_palette` $02 | 上電調色盤內容不符 | 測試期望值 = blargg 那台主機的上電殘值;**blargg 自己的 readme 就註明機台相關**。netlist 上電 palette = settle 結果,不是任何真實機台 |

AprNes 作法:硬編碼 `P=$34, S=$FD, A/X/Y=0`(`Main.cs:281`、`CPU.cs:8`)+ 慣例上電 palette。**它實作的是「模擬器共識」,不是任何晶片的物理。**

### B. CPU÷12 / PPU÷4 時脈相位對齊(懷疑 2 的具體形式)—— 4~7 個

`ppu_vbl_nmi` 單項 01-04、09、10 全 PASS,**唯獨 05/06/07/08(NMI 邊沿時序四連星)全 FAIL**,
且失敗表格呈系統性 ±1 位移(如 `05-nmi_timing` 的 `08 2` vs 期望 3)。`2-nmi_and_brk`(向量劫持時序)
與 `sprdma_and_dmc_dma` ×2(DMA 週期數 525-528 帶規律擺動)疑似同源。

根因假說:真 NES 上電後 CPU 除頻器(÷12)與 PPU(÷4)的**相對相位有多種可能對齊**,
測試以常見對齊校準;我們的人工 reset 程序讓除頻器 settle 到**另一種合法相位**。
frame 級 vbl 時序(01-04 PASS)不受影響,只有 sub-CPU-cycle 邊沿判定(NMI 壓抑/開關窗口)會偏移。

**完跑補充(證據再強化)**:舊版 `vbl_nmi_timing` 套件跑完後,`1.frame_basics`/`2.vbl_timing`/
`3.even_odd_frames`/`4.vbl_clear_timing` PASS,而 **`5.nmi_suppression`、`6.nmi_disable`、`7.nmi_timing`
三個 NMI 邊沿測試 FAIL** —— 與新版套件 05-08 的失敗完全同構。**兩個獨立套件、同一道 NMI 邊沿分界線**,
相位假說跨套件一致。本類最終成員:新版 4 + 舊版 3 = 7 個(加上疑似的 nmi_and_brk、sprdma×2 最多 10 個)。

AprNes 作法:根本沒有相位問題 —— 它是行為層 `tick()`(1 CPU cycle = 3 PPU dots 固定對齊)+
顯式 `nmi_delay → nmi_pending` 兩段管線($2002 讀可取消 delay 不可取消 pending)——
**直接把測試要的答案寫成規則**。

### C. 類比現象未建模(懷疑 2+3 交界)—— 3 個

| 測試 | 證據 | 根因 |
|---|---|---|
| `ppu_open_bus` #3 | 「Decay value should become zero by one second」 | 開關級 float = 永久保持;無電荷洩漏 → 永不衰減 |
| `oam_read` | OAM dump 週期性錯位(每 8 byte 第 8 個 `-`,部分列 `**------`) | 2C02 OAM 是 DRAM;netlist 的 DRAM 單元行為(保持/刷新)與真晶片的類比特性不同 |
| `cpu_dummy_writes_oam` #2 | **前置需求就掛了**:「OAM 讀取必須可靠 —— 模擬器通常如此,**真 NES 不是**」→ 4332 次讀取失敗 | **S1 太像真硬體而失敗**:測試自己聲明其前置(可靠 OAM 讀)在真機上不成立;AprNes 用普通陣列存 OAM 所以「可靠」 |

`open_bus_decay_timer = 77777`(AprNes `PPU.cs:661`)—— 行為層一個計時器就搞定 decay。

### D. 外部 open-bus 的殘值細節 —— 1 個

`test_cpu_exec_space_apu` #2「Mysteriously Landed」:從 $4000-$40FF 取指時 open-bus 值與期望不符。
保持機制是物理的(見第 0 節),但**保持的「是哪個值」**取決於匯流排上最後一次真實傳輸;
行為 handler 的 callback 觸發時機(settle 後)可能讓殘值與真硬體逐 cycle 的殘值不同;
且 2A03 內部 APU 暫存器區($4000-$4017 在晶片內)讀 write-only 位址的內部匯流排殘值另有一套。
需要逐 cycle trace 才能定位 —— 列入深入研究區。

### E. APU / DMA 細節差異 —— 6 個

| 測試 | 證據 |
|---|---|
| `apu_test/3-irq_flag` #6 與 `blargg_apu_2005/03.irq_flag` $06 | **新舊兩版同碼失敗**(寫 $00/$80 到 $4017 不應影響 IRQ flag)→ 是穩定的行為差異,不是隨機;frame counter 重置邏輯的 netlist 行為 vs 真機 |
| `apu_test/7-dmc_basics` #19 | 「one-byte buffer 應立即填充」—— DMC 取樣 DMA(RDY 停 CPU + 匯流排取指)與行為 ROM 的互動時序 |
| `dmc_dma_during_read4` 三個(2007_read、double_2007、4016_read) | CRC 不在合法集合。注意:**合法集合本身就有 2-4 個機種變體** —— 我們的 CRC 可能是「另一顆合法晶片」的答案,也可能是 DMA 時序 bug;無實機無法仲裁 |
| `sprdma_and_dmc_dma` ×2 | DMA 週期計數擺動 —— 歸 B 類相位或本類,trace 才能分 |
| `test_ppu_read_buffer` #67(完跑補充) | CNROM 卡帶;基礎 PPU I/O 與「Direct poke」「DMA with ROM」子測試全 OK(**CNROM banking 本身無誤**),敗在「DMA + PPU bus」組合子測試(sprite-0 hit + $4014 DMA + PPU 匯流排同時進行)—— DMA 與 PPU 匯流排互動的細節時序 |

### F. 非官方立即定址指令 —— 3 個

`instr_test-v3/02-immediate`、`v5/02-immediate`、`v5/03-immediate` **三個一致**失敗於同五個 opcode:
`0B AAC`、`2B AAC`、`4B ASR`、`6B ARR`、`AB ATX`。

- `AB ATX(LXA)` 是著名的「魔術常數」不穩定指令(依匯流排類比噪聲),netlist 給出的是
  「一顆乾淨模擬晶片」的答案,blargg 的期望值來自他那台實機 —— 本質上機台相關。
- 但 `AAC/ASR/ARR` 一般認為是穩定指令,三測試一致失敗 → 指向共同機制
  (這族全是「A AND #imm」資料路徑,內部 SB/DB 匯流排合併行為)。
  可能是 Visual2A03 netlist 在該區的描繪誤差,或我們的 operand 取值時序。**需要單指令 trace study。**

### 完跑後的最終分佈(26 FAIL)

| 類 | 個數 | 成員 |
|---|---|---|
| A 上電 | 2 | registers、power_up_palette |
| B 相位/NMI邊沿 | 7(~10) | 新版 05-08 ×4、舊版 5/6/7.nmi ×3(+疑似:nmi_and_brk、sprdma×2) |
| C 類比未建模 | 3 | ppu_open_bus、oam_read、cpu_dummy_writes_oam |
| D open-bus 殘值 | 1 | test_cpu_exec_space_apu |
| E APU/DMA 細節 | 7 | irq_flag ×2、dmc_basics、dma_2007×2、dma_4016、read_buffer #67 |
| F 非官方指令 | 3 | 02-immediate ×2、03-immediate |
| (B/E 邊界) | 3 | nmi_and_brk、sprdma ×2 |

## 2. AprNes 為什麼滿分 —— 定性

AprNes(和幾乎所有成熟模擬器)實作的是**測試 ROM 所定義的共識行為**:
power-on 值硬編碼、NMI 管線顯式規則化、open-bus 用變數+衰減計時器、OAM 用普通陣列、
DMA 用 `dmc_stolen_tick()` 精確補週期。測試怎麼考,就怎麼寫 —— 這是行為層的本份,也是它滿分的原因。

S1 給出的是**一顆特定 netlist 晶片的物理答案**。兩者對「正確」的定義不同:
`cpu_dummy_writes_oam` 在真 NES 上同樣會失敗 —— 追求該測試 PASS 等於要求 S1 降低忠實度。

## 3. 通往「滿分」的路線圖(依 CP 值排序)

| # | 修正 | 預期收復 | 工程量 | 忠實度代價 | 建議 |
|---|---|---|---|---|---|
| 1 | **時脈相位對齊實驗**:reset 釋放時刻掃 1-3 個 PPU dot 相位,對 `05-nmi_timing` 找出過版相位設為預設 | +4~7(NMI 四連星、nmi_and_brk、sprdma×2) | 中(實驗自動化容易) | **低** —— 相位在真機上也是自由參數,選「常見對齊」完全正當 | **首選** |
| 2 | **上電 shim**:power-on settle 後把 P 修為 $34(清 Z 節點)、palette RAM 寫入慣例上電值 | +2(registers、power_up_palette) | 小 | 中 —— 明確標注為「模擬 blargg 機台上電」的行為疊加 | 做,但要文件化 |
| 3 | **PPU open-bus 衰減 shim**:行為計時器,io 閂鎖節點 ~0.6 模擬秒未刷新即歸零(仿 AprNes 77777) | +1(ppu_open_bus) | 小~中 | 中 —— 為 netlist 加上它物理上沒有的洩漏 | 做 |
| 4 | **trace 深究區**:irq_flag #6、dmc_basics #19、exec_space、immediate 五 opcode —— 每個做單點逐 cycle trace 對照 nesdev 文獻,分辨「netlist 描繪誤差(可修 netlist/handler)」vs「機台差異(記為合法偏差)」 | 0~+8 | 大(每個都是一次小研究) | 視結論 | 排第二輪 |
| 5 | **dma CRC 三個**:無實機無法仲裁;先做 #4 的 DMA trace,若時序正確則申報「疑似另一合法機種變體」 | 0~+3 | 併入 #4 | — | 標注 |
| 6 | **OAM 兩個(oam_read、dummy_writes_oam)** | 0 | — | **高** —— 要 PASS 就得把 OAM 改成「模擬器式可靠陣列」,直接違背專案本意 | **建議不修**,文件標注「真硬體同樣失敗;S1 行為 = 忠實」 |

### 誠實的滿分定義

全修完(含不建議的 #6)才能到 141/141。**建議的目標不是 141/141**,而是:

> **每個 FAIL 都有已定案的根因,並分為兩類:已修正 / 已文件化的合法偏差(faithful deviation)。**

照此定義,#1-#3 完成後預估 **~130/141 PASS + ~5 個文件化偏差 + ~6 個待 trace 定案**;
報告頁可為「合法偏差」加第五種狀態徽章(如 `faithful-fail`),這反而是 switch-level 專案獨有的、
比滿分更有說服力的敘事 —— **模擬器滿分靠寫規則,我們的失敗有一半是因為太像真的晶片。**

## 4. 附:證據索引

- 失敗明細:`tools/testrom/out/results/*.json`(resultText 含 blargg 完整輸出)
- 三態物理結構:`AprVisualBenchMark/data/system-def/ROM32K.js`(cs 反相器 + pass transistor)
- AprNes 對照點:`Main.cs:281`(power-on regs)、`PPU.cs:661`(decay timer)、`MEM.cs tick()`(固定 1:3 對齊)、CLAUDE.md(nmi_delay/nmi_pending 模型)
- 相位假說的關鍵佐證:vbl 基礎/set/clear 時序(01-04)全過,唯邊沿四連星失敗且呈 ±1 位移
