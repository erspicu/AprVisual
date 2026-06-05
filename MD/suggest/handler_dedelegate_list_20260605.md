# Handler 去 delegate / 改 static 修改清單(2026-06-05)

> **目標**(user 2026-06-05):熱路徑上的 handler 用 delegate(Action)會多轉跳一層 + 擋住 inline;跨 method 共用的東西偏好用全域 static。先列清單,逐項評估,**還沒實作**。
> **原則**:每項 interleaved-paired A/B + checksum `0x794A43ABDF169ADA` 把關;FrameBuffer 不在 checksum 內,動到 video 要另驗 PNG。預期多為小贏~雜訊(真正瓶頸是 BFS settle,delegate invoke 只佔每半週期的零頭),但符合「去間接 + static 化」的方向且低風險。

---

## 熱路徑上的 delegate / 間接點(實測摸清)

每半週期 `StepCycle()`(Recalc.cs:278)會做兩件有間接的事:
1. `RunHandlerChain()` → `_handlerChain?.Invoke()`(Handlers.cs:32)—— **整條 chain 只有一個 handler:時鐘 toggle**(Handlers.cs:224,唯一的 `AddHandler`)。
2. settle 完 → `InvokeCallbacks()`(Recalc.cs:107)→ 逐一 `cb.Callback()`(Handlers.cs:106)—— memory / video handler 走這條(條件觸發)。

---

## 清單

### H1 — 時鐘 toggle 內聯進 StepCycle,拆掉 handler-chain delegate ★主候選
- **現況**:`StepCycle` 呼叫 `RunHandlerChain()` → `_handlerChain?.Invoke()` → 時鐘 lambda `() => { if (NodeStates[clk]!=0) SetLow(clk); else SetHigh(clk); }`(捕捉 local `clk`)。每半週期一次 **delegate 間接呼叫 + null 檢查**,且 lambda body 無法被 inline 進 StepCycle。
- **改法**:chain 只有時鐘,所以直接把 toggle 內聯進 StepCycle,用既有的 static `ClockNode`(Recalc.cs:14 已宣告):
  ```csharp
  private static void StepCycle() {
      int clk = ClockNode;
      if (clk != EmptyNode) { if (NodeStates[clk] != 0) SetLow(clk); else SetHigh(clk); }
      Time++;
  }
  ```
  並在 `AttachClockHandler` 設 `ClockNode = clk;`(確保 step 前已設;目前 ResolveCachedNodes 也會設,但提早設更穩),不再 `AddHandler`。
- **去掉**:每半週期的 delegate invoke + null 檢查;toggle 可被 inline。
- **熱度**:每半週期(300k 次/bench)。**但**每半週期的主成本是時鐘觸發的整輪 settle(數百節點重算),delegate invoke 只是其中 ~1 次間接呼叫 → 預期**小贏~雜訊**。
- **風險**:低。bit-exact 應成立(行為相同)。需確認 `ClockNode` 在第一次 StepCycle 前已設。
- **取捨**:會拿掉通用的 `_handlerChain` 機制(目前只有時鐘在用)。保守變體 = 保留 `_handlerChain` 給未來非時鐘 handler,但 StepCycle 仍特例內聯時鐘(見 H1b)。

### H1b — (H1 的保守變體)保留 chain、只特例內聯時鐘
- 若想保留通用 handler 機制:StepCycle 先內聯時鐘,再 `if (_extraHandlers != null) _extraHandlers.Invoke();`(只有非時鐘 handler 才走 delegate;目前是 null → 等於 H1 的效果但保留擴充點)。
- 取捨:多一個 null 檢查(可忽略),保留通用性。

### H2 — 時鐘 lambda 改用 static ClockNode(若不採 H1)
- 若決定不內聯(保留 delegate):讓 lambda 參照 static `ClockNode` 而非捕捉 local `clk` → lambda 變「不捕捉」→ 編譯成 **cached static delegate**(免每次配置、JIT 較好處理)。
- **基本被 H1 涵蓋**(H1 直接不用 delegate)。只在「想保留 delegate 但去掉 closure 捕捉」時才獨立採用。優先度低。

### H3 — InvokeCallbacks 的 `cb.Callback()` delegate dispatch → 去 delegate ◆大重構,先測值不值
- **現況**:`InvokeCallbacks` 逐一 `cb.Callback()`(delegate)。memory / video handler 是這些 callback。
- **熱度**:條件觸發。full_palette 下 CPU 每抓一條指令就觸發 prg.rom callback、video 每像素觸發 → **不算冷**,但仍遠少於 BFS。
- **改法選項**:
  - (a) `CallbackInfo` 加一個 `enum Kind`(Clock/RamRead/RamWrite/Rom/Video)+ 每實例的 context,`InvokeCallbacks` 用 `switch(Kind)` 呼叫 static 方法 → 去掉 delegate、可 inline switch 分支。
  - (b) Gemini Rank 1:`delegate* unmanaged` + context 指標(最快但要重寫整個 AddCallback/CallbackInfo 機制)。
- **問題**:callback 是**多實例 + 異質**(多塊 memory region),context 必須 per-instance(就是我們剛決定 memory 維持託管的那塊)。所以這是**大重構 + 風險高**,且 memory context 若走非託管又回到先前 −0.2~0.7% 的取捨。
- **建議**:**先量「callback dispatch 佔比」**(在 `cb.Callback()` 外圍加計數/計時,或 profiler 看 InvokeCallbacks 佔比)再決定。若佔比 <1% 就不值得做。

### H4 — (H1 子項)時鐘路徑內聯 SetHigh/SetLow
- 時鐘 toggle 呼叫 `SetLow/SetHigh`(Recalc.cs:256-257,非 inline:`if (SetXQueued(nn)) ProcessQueue();`)。`SetXQueued` 已 AggressiveInlining。
- 內聯 H1 後可順手把時鐘特例寫成 `if (SetLowQueued(clk)) ProcessQueue();` 直接展開,少一層 `SetLow` 呼叫。
- 預期:雜訊。低優先,跟 H1 一起測即可。

---

## 📊 結果(2026-06-05)
- **H1 + H4 = ✅ 採用(commit `c882575`,+~1%)** —— 時鐘 toggle 內聯進 StepCycle、用 static `ClockNode`、移除整個 `_handlerChain`/`AddHandler`/`RunHandlerChain`、時鐘路徑直接展開 `SetXQueued+ProcessQueue`。3 批 interleaved-paired **全正**:median +0.77/+0.97/+1.5%,37/60 勝,bit-exact `0x794A43ABDF169ADA`。**比預期大** —— 那個 per-half-cycle delegate invoke 不只是一次間接呼叫,它還擋住了 StepCycle 熱迴圈的 inline。(印證使用者直覺:delegate 多轉跳一層 + 擋 inline,在每半週期的路徑上是真的有感。)
- **H2**:被 H1 涵蓋(已無 delegate),不需要。
- **H3(callback dispatch 去 delegate)= ✅ 做了,分兩階段:**
  - **Stage 1(typed dispatch,commit `6b5e7dc`,+~0.4%)**:`CallbackInfo` 改成帶 `Kind` 的 typed descriptor + per-instance context 欄位;`InvokeCallbacks` 用 `switch(Kind)` 呼叫 static `DoMemRead`/`DoMemReadWrite`/`DoVideo`,不再 `cb.Callback()`(Action)。泛用 Action 只留給罕用/測試路徑。3 批全正(median +0.28/+0.41/+0.54%,35/60)。
  - **Stage 2(memory context 轉非託管,commit `4028ecc`,中性)**:`CallbackInfo` 的 `Addr/DataOut`→`int*`、`MemData`→`byte*`(`Memory.Data` 也 byte*、ROM 載入 Span)。**因為 CallbackInfo 本身就是 dispatch 單位(context 一跳,非多一跳),這次轉非託管終於是中性(52/100),不再是之前 closure/ctx 版的 −0.2~0.7%。** 為非託管一致性保留(零成本)。
  - **關鍵教訓**:之前 memory 轉非託管會慢,根因是 **ctx-holder 多一跳**,不是非託管本身 —— 把 context 直接放在「已被載入的 dispatch 單位(CallbackInfo)」上就消除了懲罰。bit-exact + frame md5 一致全程把關。

---

## 📊 後續:ChatGPT 4 項「深水區」建議(2026-06-05,user 貼上評估)

H1/H3 把 handler 的 **delegate 結構性成本**(間接呼叫 + 擋 inline)拿掉後,對方又提了 4 項更微的優化。逐項實測(interleaved-paired + bit-exact)。**前提共同問題:全打在 clock/callback 這些「非瓶頸」路徑;結構性成本已被 H1/H3 移除,剩下的是分支預測器/cache 早就免費處理的微成本。**

| # | 建議 | 結果 | 判定 |
|---|---|---|---|
| ③ | `ReadBits(int*,len)` unroll-by-4(內層 immediate shift、每 4 個一次 variable shift,涵蓋所有寬度) | 5 批 **50/100**,median 噪(+1.03/−0.63/+0.73/+1.08/−0%),tmean ~+0.09% | ❌ **噪音,退回**。ReadBits 被**隨機 gather 主導**(NodeStates[node id] 散落),loads 已被 MLP 重疊,展開只省迴圈計數器 → 省不到。對方的 Read8/Read16 特化還對不上 NES 真實寬度(addr 11/15、hpos/vpos 9、palPtr 5、palRam 6) |
| ④ | `CallbackInfo` class → unmanaged struct 連續陣列 | ⛔ **沒做** | ❌ **前提錯**:全系統只 ~5-6 個 CallbackInfo → 常駐 L1/L2 不會 miss;「連續記憶體預取」要遍歷很多項才有意義。且 `CallbackInfo` 有受管欄位(`Action`/`string`/`int[]`)不能進 unmanaged struct → 要拆 hot-struct + 冷 side-table + 把 `Enqueued`/swap-drain 改 by-ref,大重構換不存在的問題 |
| ② | 拆分 callback 佇列(per-Kind queues,消滅 switch) | ⛔ **沒做** | ❌ **過度設計**:每次 settle 只有個位數 callback,DoD 批次化無效益;switch 是 jump table 對個位數幾乎零成本;拆三佇列只是把 switch 搬到 enqueue + 三倍 re-entrancy 複雜度。i-cache 本就非瓶頸(0.14%) |
| ① | branchless 時鐘翻轉(`next=state^1`;`(8>>next)` → SetHigh/SetLow;直接寫 flag) | 5 批 **55/100**,median ~+0.28% / tmean ~+0.12%(**1σ,p≈0.16 不顯著**) | ✅ **採用(commit `2e03442`,依 user 選擇)** —— bit-exact、略偏正但統計不顯著。消滅的是「完美可預測=免費」的分支(交替時鐘),預期本就 ~0;機器當時已熱降頻(範圍 80–92k)測不準。程式碼略不易讀(寫死 flag 位元 `8>>next`),但 user 偏好 branchless 風格 |

**小結**:4 項裡只有 ① 採用(且是邊際不顯著、依偏好留),②④ 沒做(前提錯/過度設計),③ 噪音退回。再次驗證:**delegate 拿掉後,clock/callback 路徑的剩餘微成本已在噪音地板下**;真瓶頸是 BFS settle 的隨機 gather(記憶體延遲),非這些路徑。

> ⚠️ 量測品質註記:本批測試時機器已連續轟炸數小時、熱降頻嚴重(範圍從乾淨的 88–92k 掉到 78–92k),<0.5% 的效果都在噪音地板下。①③ 的「邊際/噪音」判定有此背景;若要更乾淨的結論需在涼機重測。

## 建議順序
1. **H1**(時鐘內聯 + static ClockNode)—— 最直接、最符合你的偏好、低風險。實作 + interleaved-paired A/B + checksum。順帶 H4。
2. 若 H1 有效或中性且你想保留擴充點 → 改用 **H1b**。
3. **H3** 先量佔比再決定(大重構,多實例 context 是難點,可能不值)。

> 註:真正的瓶頸是 BFS settle(ProcessQueueInterp),這些 delegate 只佔每半週期的零頭,所以**主要價值是「去間接 + static 化的程式一致性」,效能預期小贏~雜訊**。一律實測為準(專案鐵律:理論不可恃)。
