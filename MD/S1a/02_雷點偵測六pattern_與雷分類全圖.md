# 雷點偵測六 Pattern 與雷分類全圖(經 141+147 兩戰役封閉驗證)

**日期**:2026-07-16(旗艦 run7 進行中)。**出處**:ALERead 攻破後的方法論討論,回答三個遞進問題:
(a) 有沒有結構 pattern 能主動找出「需要傳播延遲處理」的雷點?
(b) 這些 pattern 可否程式化偵測?
(c) 兩個戰役實際處理過的所有雷,是否全部落在這套分類內?(→ 是,封閉)
**定位**:S1a 開工時「標註/機制目標怎麼選」的偵測方法論;`01_時序標註網表` 的前置掃描篇。
**性質**:方法論參考。掃描器均未實作;實作屬 S1a Phase 0,待使用者啟動。

---

## 一、傳播延遲家族:六個結構指紋(P1–P6)

每一顆已處理的傳播延遲雷都有結構指紋,可歸為六個 pattern。前四個從網表結構掃,第五個用幾何,
第六個抓 settle 瞬態。

### P1 閂鎖賽跑(晶片內最大宗;5+ 顆 shim)
**指紋**:pass-gate 閂鎖(儲存節點唯一驅動路徑經時脈控制的 pass 電晶體),且**致能錐與資料錐共享
支撐** —— 同一觸發事件既關門又改資料,勝負本由傳播延遲決定;零延遲 settle 一律讓新值 ripple
穿過關閉中的門。
**案例**:DmcLatch(t14402 資料沿 vs apu_clk1 關門)、AluLatch(匯流排崩塌 vs SBADD/DBADD 關門)、
DL(φ2 透明)、Dbl2007(緩衝推進,量測 1–7hc)、OamDmaPpuBus($2004 資料 hold 過 /WE)。
**靜態掃**:載入期列舉 pass-gate latch(結構可列舉;mixed-signal M4 計畫同款)→ 檢查 enable 錐 ∩
data 錐(**S2 WireCore.Extract/Cones 的錐計算直接重用**)。注意過濾平凡交集(萬物支撐皆含 clk)。
**動態掃**:settle 內偵測器 ——「pass gate 本波關閉 && 其源側值本波翻轉」→ 記站點、按頻率排名。
早裝的話 DmcLatch/AluLatch 會在測試抓到之前浮出。

### P2 跨晶片取樣器(M6 相位家族;4 顆)
**指紋**:跨 die 邊界的網(清單=nes-001 connections,十幾條)餵進**計數器比較器錐**(下游邏輯
同時吃 hpos/vpos 位元,算「counter==K && 信號」)。介面延遲被抹零 → 效應早 N dot。
**案例**:dot-339($2001→rendering enable)、even_odd(~1-dot 整合偏移)、BGSerialIn(dot%8==7
shifter-load 邊界)、ALERead(io_ce→read_2007_trigger,24hc)。
**掃法**:介面網下游 BFS,支撐含計數器位元即中。全自動,清單小,最乾淨。

### P3 中途廢止(1 顆)
**指紋**:非同步控制寫入 kill 一個**進行中的多週期序列器**;硬體上 kill 信號走內部長路徑,
序列器多退一步;零延遲瞬殺。
**案例**:Dmc4015Abort($4015 disable → DMA sequencer,retire 路徑撐 ~3 cycle)。
**掃法**:靜態偵測 FSM(閂鎖圖 SCC)可做但噪;實用解=動態 ——「規律多 hc 節奏的節點群 + 暫存器
寫入 strobe 落在節奏中段」。**出候選不出判決**(廢止行為的正確答案需 oracle)。

### P4 類比回授迴圈(2 顆)
**指紋**:傳導圖裡經 pass gate / 匯流排 / 行為層回呼的組合迴圈(輸出經讀取路徑繞回輸入)。
**案例**:ALERead 的 74LS373 ALE+/RD 回授、CHR 回授 guard(PpuAleReadFeedbackShim)。
**掃法**:環偵測 —— `HasNonTrivialRomFeedbackCycle` 的 Floyd 雛形**已存在**,擴大範圍即可。
需過濾平凡 SR latch 環(每顆閂鎖都是環);限縮到「跨 pass-gate 同相位開啟 / 經匯流排+回呼」的環。

### P5 幾何 RC 排名(排序器,非判定器)
**指紋**:真實傳播延遲 ≈ 線長 × 扇出電容。segdefs 每 segment 帶 die 上**多邊形座標**,
目前解析後直接丟棄(Parse.cs「we don't keep the polygons」)—— 免費資料。
**掃法**:載入期算每網 bounding-box 跨距(線長代理)+ 總面積(電容代理)+ 扇出 → top-N
「物理上最慢的網」。這就是 EDA RC extraction 的思路。2026-07-14 Gemini 諮詢的面積電容代理
當年因會動 checksum 被否決 —— **當純診斷排名用是零風險**。
**只出排名不出判決**:替 P1–P4 的候選排優先序。

### P6 毛刺捕捉(2 顆;初版分類漏列,完整性檢查時補上)
**指紋**:settle 中途的**瞬態毛刺被儲存元件咬住** —— SR latch 被一個「到靜止時已消失」的脈衝
誤觸發。硬體的慣性延遲吞掉奈秒毛刺;零延遲邏輯照傳。與 P1 的差異:P1 是關門沿 vs 資料的賽跑,
P6 是**根本不該存在的瞬態**被記憶。
**案例**:FrameIrq 誤清(w4017 波 × apu_clk1 沿翻 RS 對;「同波瞬態家族」)、OamBlankEdge
(關渲染邊沿的寄生 OAM 寫入脈衝)。
**掃法**:動態 ——「儲存元件的 set/reset 輸入在單一 settle 內脈衝後歸位」計數器 + 節點單波
多次翻轉審計。全自動。

---

## 二、可程式化程度(誠實分層)

| Pattern | 靜態 | 動態 | 現成基礎 | 備註 |
|---|---|---|---|---|
| P1 閂鎖賽跑 | ✅ 全自動 | ✅ 全自動 | S2 Extract/Cones 錐機器 | 需過濾平凡 clk 交集 |
| P2 跨晶片取樣 | ✅ 全自動 | ✅ | connections 清單 + hpos/vpos 節點名 | 最乾淨 |
| P3 中途廢止 | ⚠️ 半自動(FSM 噪)| ✅ 出候選 | — | 判決需 oracle |
| P4 回授迴圈 | ✅ 全自動 | — | Floyd 雛形已有 | 需過濾平凡 SR 環 |
| P5 幾何 RC | ✅ 全自動 | — | segdefs 多邊形(撿回即可)| 排名非判定 |
| P6 毛刺捕捉 | — | ✅ 全自動 | [halt]/under-settle 儀器經驗 | — |

**實務漏斗**(避免靜態掃出幾千假陽性):
```
靜態列舉(P1/P2/P4)→ 動態計數(真 ROM 跑,開火頻率排名)
→ P5 物理排序 → 可觀察性過濾(下游錐是否達暫存器/RAM/輸出腳;S2 Cones 重用)
→ [aehc] hc 顯微鏡逐顆確認
```
**Escape-1 的 ~1.1% 不可約類比活動清單 = 現成第一近似**(S2 投資在此變現)。

**根本極限:候選 ≠ 真雷。** 真雷要 (1) 有程式碼踩到該時序、(2) 硬體行為與我們不同 ——
而 (2) 的答案**不在網表裡**(網表沒有時序,這正是問題本身)。判定靠 oracle,但 oracle 比對
本身也可程式化:**AprNes lockstep diff**(calibration_ref.json 基建已半套)、**S2 Dynamic Miter**
(現成的兩模型自動等價比對框架)。
**自動化不了的殘渣只有一種**:連硬體 ground truth 都無文件的角落(boing2k7 作者自己寫
"I'm not sure why it wasn't updated")—— 只能真機量測。

```
候選偵測(P1–P6)      → 100% 可程式化(load-time pass + 儀器化跑)
候選排序(P5+頻率)    → 100% 可程式化
真雷判定              → oracle diff,~90% 可程式化(AprNes lockstep / Dynamic Miter)
最後殘渣              → 硬體無文件角落,只能實機量測
```

---

## 三、範圍外五家族(傳播延遲之外的雷)

| 家族 | 案例 | 偵測把手 | 自動化程度 |
|---|---|---|---|
| **M2 電荷** | OpenBus last-transferred-byte、io_db decay、OAM 動態 cell | 「以 hold-previous 解析的浮接群 + 下游有消費者」動態計數 | ✅ 全自動 |
| **M1 強度** | LXA $AB magic(ratioed 對抗) | 「同群同時有非電源軌 drive-high 與 drive-low」競爭計數器 | 站點自動;**數值不可推導**(W/L 不在網表)→ oracle/實機 |
| **M6/M7 初態與相位** | PowerUpState(P=$34)、power_up_palette、reset-hold K=1、needsJoypad 圖變更 D 類抽籤 | 不是掃網表 —— 是**參數維度**:掃 4 相位 / 上電表 + oracle 對答案 | 掃描自動;正解靠 oracle(blargg golden alignment) |
| **E 類資料缺陷** | r4015 缺 a1 下拉管、u7/u8 tie 極性(浮接 TTL 應讀高) | 規律性/對稱性檢查給弱提示;實務靠 oracle 分歧回溯 | 最弱 |
| **閘級模組語彙不匹配** | CD4021 手把(pslatch 族):閂鎖 pass-gate 反向驅動輸入,GND 恆勝,「放開的按鍵永遠寫不進去」→ **結構性不可驅動** | **看零件 def 即知**(known-by-construction),零 discovery 成本 | 唯一修法=整模組行為層化(S1a M5) |

規律:**越靠近「結構」自動化越強;越靠近「矽的物理參數與真值」越依賴 oracle。**
網表給連接性;時序、強度、初態、勘誤四樣本來就不在網表裡(thesis 的分界)。

---

## 四、兩戰役完整對照(封閉性驗證)

### AC 141 戰役
| 雷 | 家族 |
|---|---|
| DmcLatch / AluLatch / DL / Dbl2007 / OamDmaPpuBus | P1 |
| dot-339 / BGSerialIn / ALERead(+P4 回授) | P2 |
| Dmc4015Abort(ExplicitDMAAbort 16/16) | P3 |
| CHR 回授 guard | P4 |
| FrameIrq / OamBlankEdge | P6 |
| OpenBus last-byte + DL 窗鉗 | M2 |
| LXA magic | M1 |
| PowerUpState、K=1 | M6/M7 |
| r4015 補管(APURegActivation err6) | E |

### nes-test-roms 147 戰役(修復總表:MD/testrom/00 §3)
| 雷 | 家族 |
|---|---|
| DMC latch(+4)、ALU latch、Dbl2007(double_2007_read) | P1(發源地) |
| even_odd(~1-dot)、read_buffer #67 | P2 |
| FrameIrq(同波瞬態家族第三例,140/5 那筆) | P6 |
| LXA magic(+3 的一部分) | M1 |
| io_db decay shim | M2 |
| K=1 相位(NMI-edge 8 顆)、power_up_palette、registers P=$34 | M6/M7 |
| needsJoypad 載入期圖變更抽籤(§2.6 探針效應 → per-test 紀律) | M7/D 類 |
| exec_space_apu tie 極性 + 冷埠 bit0 | E |
| 行為層手把(CD4021 四測 + dma_4016_read) | 語彙不匹配 |

### 不屬於「雷」的兩類(誠實邊界)
- **天然湧現反例**:dma_4016_read —— 「DMA 雙重時脈損毀由真實匯流排流量天然湧現,零專門修正」。
  不是每顆疑難都是雷;有的等真實流量到位就自己對了。
- **忠實偏差(判定不修)**:cpu_dummy_writes_oam(147 唯一殘留)—— 引擎忠於網表、與測試預期歧異,
  文件化為證據卷宗。分類學上掛在 E 類 oracle 歧異邊界,但處置是「不處置」。

**結論:兩戰役 288 顆測試、~25 顆處理過的雷,無一落在分類圖之外 —— 分類封閉。**

```
P1–P6 傳播延遲六 pattern(可全自動掃)
+ M2 電荷 / M1 強度 / M6M7 初態相位 / E 資料缺陷 / 閘級模組語彙不匹配(五個範圍外家族)
+ 忠實偏差(判定不修)+ 天然湧現(不是雷)
```

---

## 五、對 S1a 的直接用途(Phase 0 規格草案)

S1a 開工時,第一步不是寫機制,是跑**偵測 pass** 產出標註目標清單:
1. P1/P2/P4 靜態掃描器(重用 S2 錐機器 + Floyd)→ 候選集。
2. P5 幾何排名(Parse.cs 保留 polygons,一次離線計算)→ 排序。
3. 儀器化跑(P1 動態、P3 候選、P6 審計、M2 浮接計數、M1 競爭計數)於代表性 ROM 集 → 開火頻率。
4. 可觀察性過濾(S2 Cones)→ 修剪。
5. AprNes lockstep / Dynamic Miter 分歧確認 → 真雷清單 → 填 `01` 的標註 sidecar。

已知答案可直接預填:本文兩張對照表的 ~25 顆(機制、節點、量測常數全在各 shim 註解與知識庫)。

## 相關
- `MD/S1a/01_時序標註網表_物理延遲參數化_M3M6實作參考.md`(標註格式與引擎模式;本文是其前置掃描篇)
- `MD/S1a/00_S1a類比重構設計總綱.md`(M1–M7 機制)
- `MD/testrom/00_測試修復知識庫_總綱.md`(147 戰役修復總表、§2.4 四質地、§2.6 探針效應)
- [[aleread-board-octal-latch-boss]]、[[dot339-rendering-propagation-delay]]、[[s2-levers-exhausted-pivot-analysis]]
  (Escape-1 類比島清單)、[[instrument-probe-effect-instclamp]]
