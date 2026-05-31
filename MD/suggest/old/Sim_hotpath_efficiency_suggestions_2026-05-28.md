# Sim hotpath 效率分析與建議

日期: 2026-05-28  
範圍: `C:\ai_project\AprVisual\src\AprVisual.S1\Sim\*.cs`  
限制: 不考慮 IR、codegen、levelize、oblivious、SIMD queue 這類架構型路線；只針對現有 S1 interpreter / switch-level path 做改善。

## 目前 hotpath 形狀

主要 runtime path:

`Step()` -> `StepCycle()` -> `RunHandlerChain()` -> clock `SetHigh/SetLow()` -> `ProcessQueueInterp()` -> `RecalcNode()` -> `RecalcNodeFast()` 或 `ComputeNodeGroup()` -> `SetNodeState()` -> `InvokeCallbacks()`

相關位置:

- `WireCore.Recalc.cs:73-107`: settle queue 主迴圈。
- `WireCore.Recalc.cs:110-143`: `RecalcNode` 與 `SetNodeState`。
- `WireCore.Group.cs:67-150`: group BFS / resolution。
- `WireCore.FastPath.cs:46-90`: pure-logic fast path。
- `WireCore.Handlers.cs:68-131`: callback、`ReadBits`、`WriteBits`。
- `WireCore.Handlers.cs:145-150`: clock handler。
- `WireCore.System.cs:176-194`: `RunFrame` 每 half-cycle polling。

目前已經有幾個有效基礎優化: unmanaged SoA、`ushort` adjacency、byte queue hash、iterative BFS、pure-logic fast path、batch `WriteBits` settle、lowering。建議以下變更時要保留這些方向，不要把 hot data 拉回 managed object graph。

## 快速量測紀錄

環境可 build: `dotnet build -c Release` 成功，.NET SDK `10.0.108`。

測試命令:

```powershell
dotnet run -c Release -- --system-def-dir "C:\ai_project\AprVisual\ref\metalnes-main\data\system-def" --benchmark "C:\ai_project\AprVisual\nes-test-roms-master\不需要測試(偏向展示DEMO)\full_palette\full_palette.nes" --bench-hc 50000
```

結果:

- lowering on: `49,049 hc/s`, `20.39 us/hc`
- lowering off: `46,880 hc/s`, `21.33 us/hc`
- 這次 workload 上 lowering 約 +4.6%。
- lowering on 時: nodes `15164 -> 14723`，transistors `27305 -> 26775`，pure-logic fast path 分類 `3,408` nodes，約 `23.1%` live nodes。

這只是單 ROM、單次短量測，不應當作最終數字；但足以支持「現有 lowering / fast-path 確實在熱路徑上有價值」。

## 建議優先順序

### P0. `SetNodeState` 內聯 enqueue，避免每個 fanout 呼叫 `EnqueueNode`

位置: `WireCore.Recalc.cs:127-143`、`WireCore.Recalc.cs:45-54`

`SetNodeState` 是每個狀態翻轉的 fanout 擴散點。現在每個 gate fanout 至少呼叫一次 `EnqueueNode(c1)`，gate 變 low 時可能再呼叫 `EnqueueNode(c2)`。`EnqueueNode` 會做 supply check、hash check、list push。對 `c1` 而言，`AddTransistor` 已把 supply 正規化到 `c2`，所以 `c1` 理論上不是 `Npwr/Ngnd`，可以在 hot loop 直接做 hash/list push。

建議方向:

- 在 `SetNodeState` 開頭把 `RecalcListNext`、`RecalcHashNext`、`RecalcListNextCount` 複製到 local。
- 對 `c1` 直接 inline:
  - `if (nextHash[c1] == 0) { nextList[nextCount++] = c1; nextHash[c1] = 1; }`
- 對 `c2` 只在 `newState == 0` 且非 supply 時 inline enqueue。
- loop 結束再寫回 `RecalcListNextCount = nextCount`。

預期收益: 高。這是在每次 node flip 的 fanout path 上省方法呼叫、static field 讀寫與不必要 supply check。  
風險: 低到中。要確認 `c1` invariant 永遠成立；可以加 Debug assertion。  
驗證: 同一 ROM、同一 lowering 設定下比較 `NodeStatesChecksum` 與 benchmark。

### P0. group BFS 只在必要時讀 `NodeConnections`

位置: `WireCore.Group.cs:126-136`

目前 `AddNodeOrApplyDriver` 對每個 group node 都讀 `NodeConnections[nn]`，但 `_maxState/_maxConnections` 只有在 `_groupFlags == None` 的 floating group 才會影響結果。多數 driven/pull-up/supply group 不需要這個 cold array load。

建議方向:

```csharp
NodeFlags flags = ns.Flags;
_groupFlags |= flags;
if (_groupFlags == NodeFlags.None)
{
    int conn = NodeConnections[nn];
    if (conn > _maxConnections) { _maxState = NodeStates[nn]; _maxConnections = conn; }
}
```

如果 group 前段是 floating、後段才遇到 flag，前段多做的 max 計算會被忽略，語意仍然正確。若整個 group 都 floating，仍會完整維持 tie-break。

預期收益: 中到高，取決於 driven group 比例。  
風險: 中。必須確認 `_groupFlags` 只要非 `None` 就不會需要 floating tie-break。以目前 `GetNodeValue()` 邏輯是成立的。

### P0. 已找到 GND/PWR 後跳過同類 supply scan

位置: `WireCore.Group.cs:111-120`

目前每個 group node 都會掃 `TlistC1gnd` / `TlistC1pwr`，即使 `_groupFlags` 已經有 `Gnd` 或 `Pwr`。同一個 flag 重複找到沒有新資訊。

建議方向:

- `if ((_groupFlags & NodeFlags.Gnd) == 0 && ns.TlistC1gnd != 0) ...`
- `if ((_groupFlags & NodeFlags.Pwr) == 0 && ns.TlistC1pwr != 0) ...`

`ForceCompute` 只需要知道 group 是否同時有 GND/PWR，不需要知道有幾個 GND/PWR，所以跳過同類重掃是語意等價。

預期收益: 中。對大型 bus group 或 supply-heavy group 會比較有感。  
風險: 低。

### P1. callback 改成 pending queue，避免每次 settle 後掃整個 `_callbacks`

位置: `WireCore.Handlers.cs:68-84`

`ProcessQueueInterp()` 每次結束都呼叫 `InvokeCallbacks()`。現在 `InvokeCallbacks()` 先掃 `_callbacks` 找是否有 pending，再第二次掃描執行。callback 數量目前可能不大，但這個成本發生在每次 settle 後；common case 通常沒有 callback pending。

建議方向:

- `EnqueueCallback(cb)` 改成「若尚未 Enqueued，加入 pending list/array」。
- `InvokeCallbacks()` 直接檢查 pending count，0 時 O(1) return。
- 執行時清 `Enqueued` 並呼叫 callback。
- 要特別處理 re-entrant callback: callback 內可能 `WriteBits()` -> `ProcessQueue()` -> `InvokeCallbacks()`。

預期收益: 中。callback 少但 settle 次數高時，common no-pending case 會更便宜。  
風險: 中。re-entrant 行為要用小測試鎖住。

### P1. `ReadBits` / `WriteBits` 增加 `int[]` 或 `ReadOnlySpan<int>` overload

位置: `WireCore.Handlers.cs:110-131`、`WireCore.Handlers.cs:181-188`、`WireCore.Handlers.cs:229-240`

`ReadBits(IReadOnlyList<int>)` / `WriteBits(IReadOnlyList<int>)` 在 memory/video callback 內頻繁使用。`IReadOnlyList<int>` 會讓 `Count` / indexer 走介面呼叫，`List<int>` 也會保留 managed list 結構。handler attach 時其實已經能把 bus nodes 固定成 `int[]`。

建議方向:

- 增加 `ReadBits(int[] nodes)` 與 `WriteBits(int[] nodes, int value)`，或用 `ReadOnlySpan<int>`。
- `AttachRamLikeHandler()` 內把 `addr`、`dataOut`、必要的 `dataBus` 都轉成 array 後 capture。
- `AttachVideoHandler()` 已有 `hN/vN/pN` array，但仍走 `IReadOnlyList<int>` overload；改呼叫 array/span overload。
- 若 video 很熱，可加固定寬度 helper，例如 `ReadBits5`、`ReadBits6`、`ReadBits9`，但先量測一般 array overload。

預期收益: 中。memory/video callback 比核心 BFS 少，但每 pixel / memory access 都會用。  
風險: 低。

### P1. clock handler 走直接 fast path，保留 generic handler 給其他用途

位置: `WireCore.Handlers.cs:145-150`、`WireCore.Recalc.cs:193-196`

clock 每 half-cycle 都會 toggled。現在 clock 是 closure delegate 掛在 `_handlerChain`，每次 `StepCycle()` 透過 multicast delegate 呼叫。若每 half-cycle 的 handler 幾乎只有 clock，這個抽象成本偏貴。

建議方向:

- `AttachClockHandler()` 設定 `ClockNode = clk` 或 `_clockNode = clk`。
- `StepCycle()` 直接:
  - `if (NodeStates[clk] != 0) SetLow(clk); else SetHigh(clk);`
- `_handlerChain` 保留給非 clock handler，clock 不再進 chain。
- 若仍需要完全泛用，可至少在只有 clock handler 時走 fast path。

預期收益: 中。每 half-cycle 固定成本會下降。  
風險: 低。注意 reset/LoadSystem 後 `ClockNode` 的生命週期。

### P1. `RunFrame()` 不要在迴圈內呼叫 `Step(1)`

位置: `WireCore.System.cs:186-189`、`WireCore.Recalc.cs:191-196`

`RunFrame()` 的 tight loop 每次呼叫 `Step(1)`，`Step(1)` 再進一層 for loop 呼叫 `StepCycle()`。同一 partial class 內可以直接呼叫 `StepCycle()`，或新增 `[MethodImpl(AggressiveInlining)] StepOne()`。

建議方向:

- `RunFrame()` 內改成 `StepCycle();`
- 或新增 public/internal `StepOne()` 給測試工具使用，避免到處 `Step(1)`。
- `Step(int count)` 可加 `[MethodImpl(MethodImplOptions.AggressiveInlining)]`，但直接呼叫 `StepCycle()` 更明確。

預期收益: 小到中。對 `RunFrame()` / tests 常用 `Step(1)` 的情境有幫助。  
風險: 低。

### P2. 把 `IsPureLogic` 合併進 `NodeInfo` 的 padding，並分類 fast kind

位置: `WireCore.FastPath.cs:31-64`、`WireCore.Recalc.cs:110-115`、`WireCore.cs:233-242`

現在每次 `RecalcNode()` 先讀 `IsPureLogic[nn]`，fast node 才讀 `NodeInfos[nn]`；normal node 則讀完 `IsPureLogic` 後再進 BFS 讀 `NodeInfos`。`NodeInfo` 目前 16 bytes，`Flags` 是 byte，理論上有 padding 可放一個 `FastKind` byte，可能不增加 struct size。

建議方向:

- 在 `NodeInfo` 加 `public byte FastKind;`，確認 `Unsafe.SizeOf<NodeInfo>() == 16`。
- `FastKind = 0` normal，`1` gnd-only pure logic，`2` gnd+pwr pure logic，`3` pullup-no-channel。
- `RecalcNode()` 先拿 `ref NodeInfo ns = ref NodeInfos[nn]`，根據 `FastKind` dispatch。
- `RecalcNodeFast` 可接受 `ref NodeInfo ns`，避免重取。
- 對 gnd-only 類型，跳過 `TlistC1pwr` branch。

預期收益: 小到中。少一個 per-node side array load，並讓 pure logic fast path 再分流。  
風險: 中。struct layout 必須鎖住；需要 benchmark，因為 normal path 提前讀 `NodeInfo` 是否有益要看 cache 行為。

### P2. 擴充 lowering: 處理 always-on normal-to-supply short

位置: `WireCore.Lower.cs:27-29`、`WireCore.Lower.cs:58-64`、`WireCore.Lower.cs:101-117`

目前 lowering 明確沒有合併 normal node 到 supply 的 always-on short。這會讓 runtime 仍透過 `TlistC1gnd` / `TlistC1pwr` 反覆掃描永久供電/接地關係。

建議方向:

- 先不要把 node id 直接 remap 成 `Npwr/Ngnd`，因為名稱、callback、外部 drive、diagnostic probe 可能依賴原 node。
- 較安全做法是把該 connected class 的 representative 標成 static `Pwr` 或 `Gnd` flag，並移除對應 always-on supply transistor。
- 若同一 class 同時接 PWR/GND，需保留 ForceCompute/衝突語意，不可簡化。

預期收益: 中，視 netlist 中 permanent supply shorts 多寡而定。  
風險: 中到高。建議先加 lowering 統計: normal-to-vcc/vss always-on transistor 數量、涉及 node 數量，再決定是否實作。

### P2. 試驗 `_inGroup` epoch stamp，取代每次清前一個 group

位置: `WireCore.Group.cs:69-76`、`WireCore.Group.cs:126-132`

目前 `ComputeNodeGroup()` 開頭會清掉前一個 group 的 `_inGroup` flags，成本是上一個 group size 的寫入。epoch stamp 可以用 `ushort*` 或 `int*` seen array 加 `_groupEpoch`，把 clear loop 換成 epoch 比對。

建議方向:

- `_inGroup` 改成 `ushort* _groupSeenEpoch`。
- 每次 group `_groupEpoch++`，`seen[nn] == epoch` 表示已加入。
- epoch overflow 時清整個 array 並重設 epoch。

預期收益: 不確定。可以省 clear loop，但 seen array 從 byte 變 ushort/int，cache footprint 會變大。  
風險: 中。這一定要 A/B benchmark；若 group 通常很小，可能反而變慢。

### P3. headless benchmark/test 可選擇不 attach video handler

位置: `WireCore.System.cs:83-90`、`WireCore.Handlers.cs:205-244`

CLI benchmark 目前 `LoadSystem()` 一律 `AttachVideoHandler()`。若目標是測核心 switch-level throughput，而不是 framebuffer side effect，video callback 會把 pixel read/write 成本混進 benchmark。

建議方向:

- 增加 `WireCore.EnableVideoOutput` 或 `LoadSystem(..., attachVideo: bool)`。
- UI 預設 true；benchmark 可提供 `--no-video` 做純核心量測。
- 注意這是 benchmark/模式分流，不是一般 runtime 等價優化。

預期收益: 對 headless benchmark/test 可能中到高；對需要畫面的 UI 無收益。  
風險: 低，但要避免誤把 no-video 數字和 full simulation 數字混用。

### P3. Reset flattening 可直接產生 `ushort` buffer

位置: `WireCore.cs:165-206`

`Reset()` 目前用 `List<int> tl` 收集 adjacency，再配置 unmanaged `ushort*` 複製。這是 load/reset-time，不是 half-cycle hotpath。

建議方向:

- 先計算 flattened list 長度，直接配置 `ushort* TransistorList` 後填入。
- 或至少讓 `tl` 預估 capacity，減少 build-time resize。

預期收益: 低，主要改善 load/reset time。  
風險: 低。

## 暫不建議投入的方向

- `JsLexer` / parser token substring allocation: 只在 load-time，除非啟動時間成為主要問題，否則不是 hotpath。
- `_allocations.Remove()` / unmanaged allocation tracking: reset/shutdown-time，非 half-cycle hotpath。
- bitset queue hash: 專案註解已記錄 bitset variant 曾因 shift+mask 抵消 cache benefit；不要先回頭做這個。
- IR/codegen/levelize/oblivious/SIMD queue: 本次明確排除，而且 csproj 註解也說這些在 S1 已被移除或實測不佳。

## 建議驗證流程

1. 每次只改一個優化點。
2. `dotnet build -c Release`。
3. 用固定 ROM 跑 `--bench-hc 50000` 與較長 `--bench-hc 200000`，每個至少跑 3 次取中位數。
4. 同一 node numbering 的變更，要比對同一 `t` 的 `NodeStatesChecksum`。
5. 會改 lowering / node numbering 的變更，checksum 不能直接與 no-lower 比；改用既有 test ROM、CPU trace 或行為輸出驗證。
6. 優先加低成本 counters:
   - `RecalcNode` fast/normal 次數。
   - group size histogram。
   - `SetNodeState` flip 次數與 fanout enqueue 次數。
   - callback enqueue/invoke 次數。
   - video callback 次數。

最建議先做 P0 三項，因為它們都在核心 interpreter 熱路徑內，且不需要改變系統架構。
