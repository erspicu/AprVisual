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
- **H3(callback dispatch 去 delegate)**:尚未做 —— 多實例 + 異質 context 是難點,大重構。**待先量 `cb.Callback()` 佔比**再決定值不值。

## 建議順序
1. **H1**(時鐘內聯 + static ClockNode)—— 最直接、最符合你的偏好、低風險。實作 + interleaved-paired A/B + checksum。順帶 H4。
2. 若 H1 有效或中性且你想保留擴充點 → 改用 **H1b**。
3. **H3** 先量佔比再決定(大重構,多實例 context 是難點,可能不值)。

> 註:真正的瓶頸是 BFS settle(ProcessQueueInterp),這些 delegate 只佔每半週期的零頭,所以**主要價值是「去間接 + static 化的程式一致性」,效能預期小贏~雜訊**。一律實測為準(專案鐵律:理論不可恃)。
