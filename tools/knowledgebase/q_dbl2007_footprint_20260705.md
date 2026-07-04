# 諮詢:電晶體級模擬器的「測試儀器足跡」問題 —— 尋找比 per-test 開關更好的通則

## 背景:引擎架構(固定前提)

我們維護一個 Visual6502 家族的電晶體級 NES 模擬器(C#,2A03+2C02 全晶片 netlist):

- **quiescent-settle 離散引擎**:每個 half-cycle(hc)做一次 clock toggle,然後 BFS 傳播到穩態(一個 "settle wave")。wave 內部的事件順序由節點編號/queue 順序決定,是確定性的,但沒有物理時間概念。
- **群組解算 LUT 優先序**(高到低):`Gnd > Pwr > SetHigh > SetLow > PullUp > State(hold)`。`SetHigh/SetLow` 是 runtime 外部施力旗標(per-node bit);`Gnd/Pwr` 只會由群組內接到電源軌的導通電晶體貢獻。
- **行為層 = handler/假電晶體機制**:RAM/ROM/手把等在「載入期」註冊 callback —— 每個 callback 配一個假節點 + 對每個被觀察節點加一顆假電晶體(gate=被觀察節點, c1=假節點, c2=GND),讓 recalc 機制自然觸發 callback。callback 內用 SetHigh/SetLow 驅動匯流排。
- 載入期結束會做 class-major 節點重編號(效能最佳化),之後 hot arrays 定型。
- **黃金基準**:benchmark 路徑(無 handler 之外的儀器)的 NodeStates checksum 絕不可變;所有測試修復 shim 都是 test-mode only、預設關閉。
- 我們有一個 145 測項的 blargg/AC 測試目錄,成績 141/4,是在「固定的 test-mode 掛載集合」下校準的(對齊 pin K=1)。其中多個測試屬「對齊彩票」類:真機上電相位量化成少數幾態,blargg 對每態記錄一個合法 CRC。

## 問題 A:double_2007_read 的同波賽跑(已解,方法如下)

blargg `double_2007_read`:`LDA abs,X` 跨頁 dummy read + real read 背靠背連讀 $2007。netlist 正確地把兩個讀取脈衝合併成**一次** buffer 推進(與真機一致);唯一分歧:合併讀取中,buffer 重載(staging→inbuf→io→db 的傳播)與 **CPU 的 A 載入發生在同一個 settle wave**(用 hc 級 op-probe 實測確認),所以 CPU 拿到「新值」;真機四種已記錄圖樣全是「舊值/過渡值」(類比傳播延遲輸掉這場賽跑)。

**實測過而失敗的介入**(全部輸給 LUT 優先序或同波時序):
1. post-settle 用 SetLow/SetHigh 力 db/idl —— PPU io 驅動 = 群組含 Pwr/Gnd,外力位階不夠。
2. 恢復源頭(staging/inbuf 閂鎖對)—— 關鍵窗口內它們被 rail-driven(ended-pulse 期間 staging 連著主動驅動的 _db)。
3. 延後管線(read_2007_ended 級)—— 管線節點也是 rail-driven。
4. φ2 落沿後改寫 CPU 輸入閂鎖 idl(電荷節點,force 黏得住)—— 太晚:A 在同一個 wave 已載入。
5. 在 toggle 前邊界放 SetLow 當 wave 初始條件 —— wave 收斂終態確實被接管(db/idl 終態=舊值),但 A 的閂鎖在 wave 中途就咬住瞬態新值。
**唯一成功**:載入期掛 8 顆假電晶體(gate=ctl_i, c1=cpu.db_i, c2=GND),偵測到「重載落在真取樣中」(守門:ab 解碼 $2007 鏡像 + R/W=read + RDY=1 + 非 palette)時 SetHigh(ctl_i) 讓 Gnd 全波壓制 db 上升位元,φ2 落沿釋放。輸出 = old∧new = blargg 清單第一個真機圖樣(85CFD627),完全正確。

## 問題 B:儀器足跡(observer effect)—— 這次諮詢的主題

掛上述 shim(8 ctl 節點 + 1 callback 假節點 + 對應假電晶體)後,**另一個無關測試 `dma_2007_read` 從 PASS 翻 FAIL,而且 shim 全程零開火** —— 純粹因為載入期多了 9 個節點 → class-major 重編號整體位移 → settle wave 內部順序改變 → 該測試內部「DMA-halt vs read」這場彩票類賽跑翻到一個真機不存在的第三面(NESdev 確認 NTSC 2A03 只有 get/put 兩態、額外讀取 2 或 3 次,枚舉完備;我們翻出的圖樣第三行 X=00 不在其中)。

也就是說:**任何載入期圖變更都會重擲所有「同波賽跑」類測試的骰子**。141/4 基線是在固定掛載集合下校準的,每加一個儀器都可能無聲翻掉別的測試。

## 我們目前的做法(user 覺得不夠通則,要求尋找更好方案)

per-test opt-in:catalog 每測項一個 `dbl2007Shim` 布林 → runner 傳 `--dbl2007-shim` → 只有需要這個 shim 的測試(double_2007_read、test_ppu_read_buffer)掛儀器;其他 143 測跑的圖與基線逐位元相同。缺點:這是站在「測試目錄」層面的政策,不是引擎層面的通則;未來每加一個需要 Gnd 位階的 shim,都得重新面對同樣的抉擇。

## 候選通則(請評估優劣與陷阱,也請提出我們沒想到的)

**方案 A:runtime 直接注入 Gnd 旗標(零圖變更)**
不掛任何假節點/假電晶體。偵測改用既有的 test-mode per-hc shim 鏈(已有 DMC/ALU/FrameIRQ shims 在跑,零新增基建);開火時直接把 `NodeFlags.Gnd` OR 進 cpu.db_i 的 NodeInfo.Flags(+enqueue+settle),釋放時清掉。效果應等價於假電晶體(群組 OR 到 Gnd → 全波壓制),但**完全不動載入圖**,基線骰子一顆都不重擲。
疑慮:(1) FlagsToStateOf 有一個特例 —— 群組同時含 ForceCompute+Gnd+Pwr 時會把 Gnd/Pwr 都剝掉再判;db 群組通常不含 FC 節點,但需要確認。(2) 直接操作旗標繞過了 Set* API,屬「引擎後門」,需要嚴格的 save/restore 紀律(db 節點本身沒有靜態 Gnd 旗標,恢復簡單)。(3) 有沒有我們沒看到的機制會快取群組旗標?(我們的理解:每次 group build 都即時 OR,無快取。)

**方案 B:標準儀器圖(always-attach)**
把這 9 個節點變成 test-mode 的**永久標配**(像手把/視訊 handler 一樣),接受一次性的全目錄重新校準(骰子只重擲這一次,之後圖固定)。缺點:違反「不擅自跑全量回歸」的當前約束、而且每次未來加儀器仍要重擲。

**方案 C:編號穩定的掛載(tail allocation)**
讓 shim 節點排除在 class-major 重編號外、固定佔用尾端 id,舊節點 id 與佇列順序完全不變 → 理論上零擾動。需要動重編號器,屬引擎載入路徑的修改(有黃金路徑風險,需 checksum 閘驗證)。

**方案 D:其他?**
switch-level / event-driven 模擬器領域對「加觀測儀器不改變被觀測系統」(probe effect)有沒有標準做法?例如 shadow netlist、雙圖模式、或把儀器層完全隔離在主圖之外的架構?

## 請回答

1. 四個方案的排序與理由(以「通則性、正確性風險、侵入度、效能」四軸)。
2. 方案 A 的三個疑慮是否成立?有沒有其他你看得到的陷阱(例如 double-buffer settle、group cache、增量重算的交互)?
3. 有沒有第五種我們沒想到的做法?
4. 如果你認為 per-test scoping 其實才是對的(例如從「模擬器測試方法學」角度),也請直說並給理由。
