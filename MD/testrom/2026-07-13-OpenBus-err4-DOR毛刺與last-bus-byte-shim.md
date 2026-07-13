# OpenBus(err4)戰役:DOR 毛刺逃逸與 last-bus-byte shim

日期:2026-07-13 · 對象:AccuracyCoin「Open Bus」測試($408,run4 掛牌值 `$12` = FAIL err 4)
狀態:**全破 —— 孤立 ROM 1/1 PASS、套內 `$408=$01`(見 §7 err6 章)**

## 1. 症狀

AC 的 Open Bus test 4 是全套件最兇的編排:把 PC 跳進未映射位址 `$5600`,
靠「open bus 回傳最後匯流排值」讓 CPU 依序取到 `LSR <$56,X`(來自 JSR 運算元高位的 `$56`)
與 `RTS`(來自 RMW 寫回的 `$60`)。S1 上 `RTS` 的 fetch 讀到 **`$70`**(bit4 漂高),
PC 從 `$5602` 線性流浪到 `$6000` 觸發 IRQ 陷阱 → err 4。

## 2. 取證鏈(六輪探針,各自排除一層)

1. **OB 指令流探針**(PC∈[$4020,$62FF] 印 pc/ir/db/ab):鎖定 `db: $60 → $70` 發生在
   寫回結束、AB 切到 `$5602` 的同一半週期;只有 bit4 翻面。
2. **具名外部驅動排除**:u7.2OE / u8.2OE / u1.gate / cart.prg.gate 在事發瞬間全 OFF。
3. **unmanaged channel 表傾印**(`Node.C1c2s` 在 LoadSystem 後已清空,真相在
   `NodeInfos` 的 TlistC1c2s/C1gnd/C1pwr):db4=#8976 的四對 pair gate 全 0、GND 腳全 0,
   **PWR 腳 `cpu.#12101`=1** —— 2A03 自己的 db4 高驅動在 fetch 週期導通。
4. **per-bit 驅動盤點**:CPU 在該 φ1 驅動的完整位元組正是 `$70`。
5. **`cpu.dor4` 顯微鏡**(SetNodeState hook):`t=…827 dor4→0`(寫入值正確)、
   `t=…875 dor4→1 再 →0(同一 settle 內)` —— **DOR bit4 被 idb 預充電暫態打出毛刺,
   毛刺存活期恰與 pad 驅動窗重疊,逃逸上匯流排;dor4 回 0 時驅動窗已關**。
   真矽上 φ1 驅動窗(百 ns)≫ 毛刺(ns),最終驅動值=穩定 DOR;離散 settle 把時窗順序排錯。
   與 DMC/LAE 同族:pass-gate 時窗賽跑 = 引擎語意極限。

## 3. 兩個錯誤模型(都做了、都量了、都錯)

| 模型 | 結果 | 教訓 |
|---|---|---|
| 寫入後釋放 handler 驅動(SetFloat) | fetch 讀 `$70` | 浮接群含 pull-up 成員時 pull-up 贏過 hold-previous;且 handler 旗標根本被 CS 閘隔離,不在 fetch 的群裡 |
| 寫入值持久驅動(WriteBits) | 一樣 `$70` | 同上 —— 介入點不在事發的節點群內 |
| φ1 重放 **DOR**(未映射窗) | err2:`$5501` 讀到 `$02` | **DOR 不是「最後匯流排值」**:S1 的 DOR 此刻持上次寫入(INC 的 `$02`)。open bus 的正確語意是「匯流排電容記住最後一個*傳輸*位元組(讀寫皆算)」 |

另有一顆低級但致命的 bug:`ResolveNodes("cpu.db[7:0]")` 回傳**升冪** bit 序
(`ReadBits`:index i = bit i),shim 初版反轉了 → 重放 `$50` 寫成鏡像 `$0A`。
症狀特徵「讀值 = 期望值的位元鏡像」可直接當診斷指紋。

## 4. 定案設計:last-bus-byte record/replay shim

`WireCore.System.cs`:`EnableOpenBusShim()` + `OpenBusShimStep()`(掛在 per-half-cycle shim 鏈;
開發期名稱 Phi1DorShim,模型修正後更名)。

- **記錄**:AB 在映射區、或寫入週期 → `_pdLastBus = db`(post-settle)。
- **重放**:AB ∈ 未映射窗($4020..$7FFF;有 cart extra-ram 時 $4020..$5FFF)且讀週期 →
  對每一條 **無導通 channel** 的 db 線(`AnyChannelOn` 讓位真驅動者),
  `LaeForce` 成 `_pdLastBus` 對應位。
- 僅 test mode 啟用;benchmark 路徑不掛,golden checksum 驗證不變(`0x794A43ABDF169ADA`)。
- 快照 v2 未攜帶 `_pdLastBus`:resume 後第一個映射半週期即自癒,f-邊界快照無風險。

孤立 ROM(`AccuracyCoin_OpenBus.nes`,AC_DIAG_OPENBUS 閘,production SHA 不變)驗證:
err4 → **err6**,即 test 1(非零)、2(高位回讀)、3(索引跨頁不更新)、4(執行編排)、
5(dummy read 更新匯流排 + PPU open bus)全過。

## 5.(已破,見 §7)err6 = $4017 latch 賽跑

test 6 前兩關其實已過($4016/$4017 讀到 `$5D`,`&$E0=$40` ✓)——
敗在 **CPU 從 `$4017` latch 進 A 的值是 `$00`**,而 post-settle 匯流排整個讀週期都是 `$5D`
(`$4016` 同構卻正確 latch `$5D`)。`--joypad` 開關皆不影響。

## 7. err6 戰役:DL 兩相動態 latch 與 φ2 窗鉗

**取證**:`idl`(cpu.idl[7:0],輸入資料 latch)顯微鏡顯示 —— db 全程 `$5D` 正確,
`idl` 在 `clk1out` 1→0 的那個 settle **單發載入 `$00`** 後不再追隨($4016 同相位載入 `$5D` ✓)。
u7/u8 同構、僅實例節點 id 不同 → settle 順序彩票:u8 的 OE 開啟瞬態(GND-wins 中間解)
被 DL 的捕捉窗抓走。真晶片的 DL 在**整段 φ2 透明**,netlist 版是單發捕捉 —— 引擎語意極限。

**拓撲**(`--dump-node cpu.idl4 / cpu.notidl4`):DL 不是雙穩態!
`idl4` = pullup + `notidl4` 閘的 vss 下拉(純組合輸出);`notidl4` 動態節點,
整段 φ2 被上游(9247)經 `cclk` 導通的 t86 持續重灌;另有 `cp1` 相的 t3342 通路。

**三連敗與正解**(force 方法論,重要):
| 方法 | 結果 | 死因 |
|---|---|---|
| `LaeForce`(Set*+SetFloat)單邊/雙邊 | 不動 | Set* 位階輸給導通中的 Gnd/Pwr 路徑,force 根本沒生效 |
| `InstClampHigh(idl)` + 點放 | 不動 | **InstClampHigh 輸給導通下拉(LUT:Gnd > Pwr)**;且上游整段 φ2 重灌,放手即彈回 |
| **只鉗 `notidl` 一側、鉗滿整段 φ2、φ1 才 release** | ✅ | want=1 → `InstClampLow(notidl)`(Gnd 贏一切);`idl` 經 netlist 組合自然跟隨;φ1 時 `cclk` 關斷、動態節點浮持矯正值,下一週期重新取樣,零殘留 |

**實作**:`EnableDlShim()` + `DlShimStep()`(`_dlHeldMask` 追蹤鉗位;φ2 讀週期比對
`idl != db` 才鉗 → netlist 對時零作用)。這一鉗 err6/err7($4015)/err8(RTI 編排)三關全過,
孤立 ROM **1/1 PASS**。

**err6 驗證矩陣**:golden checksum 不變 ✓;LAE 孤立 1/1 ✓;
早窗 f050→f115:`$408` $12→**$01**(套內 PASS,與 oracle 一致),$40D/$40E 為相位位移
(通過路徑執行時長不同,晚 1-2 幀入表,f115 皆 $01)✓;
f200→f300 窗:全表唯一差異 `$44B`(LAE 預期修正)✓。

## 8. 教訓:全域版在第三個窗現形 → 收窄到 $4016/$4017

全域 DL shim 過了 f050/f200 兩個窗,**f900→f2000 窗(DMC/APU 帶)一口氣打壞 6 顆**:
APULengthCounter/Table、FrameCounterIRQ/4Step/5Step、DMA_Plus_2002R —— 全是
**`$4015` 內部讀取家族**。`$4015` 的值在 2A03 晶片內部產生、根本不走外部匯流排
(OpenBus test 7 原文就這麼寫),強制 `idl := 外部db` 等於拿 open-bus 垃圾蓋掉真值。
有趣:`$46B` DMABusConflict 反而 err2→err3 前進一關(它的 err2 正是 DMA 衝突讀 $4016/$4017)。

**定案:DL shim 作用面收窄到 `ab == $4016/$4017`**(實測案發地)。窄版守住 OpenBus 孤立
1/1、LAE 1/1、checksum 不變;窗 A 重跑:**7 顆回歸全數歸零,且 `$46B` DMABusConflict
$0A(err2)→ $FD(PASS variant 63)整顆翻綠** —— err2 正是 DMA 衝突讀 $4016/$4017 的比對,
案發地精準命中,窄版白撿一顆(oracle $E1 = PASS variant 56;variant 差異與 SHA 家族同類)。
早窗重驗:套內 `$408=$01` 守住 ✓。
方法論:**「netlist 對時零作用」不是安全證明 —— 內部來源讀取(`$4015` 類)時外部 db 與
idl 本來就該不同,全域重申「透明性」反而是錯的。shim 作用面永遠收到實測案發地為止。**

## 6. 驗證矩陣

| 驗證 | 結果 |
|---|---|
| golden checksum @300k | `0x794A43ABDF169ADA` 不變 ✓ |
| 孤立 OpenBus | err4 → err6(test 1-5 過)✓ |
| 孤立 LAE | 1/1 PASS(shim 交互無害)✓ |
| 套內回歸窗 f200→f300 | 全表唯一差異 `$44B`(LAE 預期修正 $0E→$01),shim 零副作用 ✓ |
| 套內早窗 f050→f100($408 新值) | 唯一差異 `$408` $12→$1A(err4→err6,與孤立一致),鄰居全不動 ✓ |

工具沿革:settle 顯微鏡(SetNodeState hook)與 driver-dump 探針為臨時碼,
取證完畢已自熱路徑移除;[ob]/[obshim] 探針保留在 test-only 路徑、OB_DEBUG 環境變數閘。
