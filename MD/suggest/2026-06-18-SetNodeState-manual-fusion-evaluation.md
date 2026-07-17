# SetNodeState 手動融合評估

日期：2026-06-18  
分支：`main`  
範圍：`C:\ai_project\AprVisual\src\AprVisual.S1\Sim`  
主題：評估是否把 `SetNodeState` 直接拆進呼叫端，以取代 C# `[AggressiveInlining]`。

## 結論

不建議全面把 `SetNodeState` 手動 hardcode inline 到所有呼叫點。

目前 `SetNodeState` 已標記 `[MethodImpl(MethodImplOptions.AggressiveInlining)]`，而 `ProcessQueue` 的註解也明確指出目前設計預期是 `RecalcNode / RecalcNodeFast / ComputeNodeGroup / AddNodeToGroup / SetNodeState` 都被 JIT inline 進熱迴圈。也就是說，單純把 method body 貼到呼叫點，大概率省不到 method call overhead。

真正可能有收益的不是「手動 inline 本身」，而是「針對特定呼叫場景做融合」，讓多次 state writeback 共用 queue locals、range boundary、`RecalcListNextCount` writeback 等上下文。

保守估計收益：`0% ~ 2%`。  
風險：code size 增大、I-cache 壓力上升、JIT register allocation 變差、維護成本上升。

## 目前呼叫形狀

### RecalcNodeFast

位置：`Sim\WireCore.FastPath.cs`

目前形狀：

```csharp
if (flags != 0) SetNodeState(nn, FlagsToState[flags]);
```

這個呼叫點只處理單一 node，而且已經先排除 `flags == 0` floating hold no-op case。若 JIT 已 inline，手動拆開幾乎只是在複製 `SetNodeState` body，收益最低。

### B1 pair path

位置：`Sim\WireCore.Recalc.cs`

目前形狀：

```csharp
SetNodeState(nn, v);
SetNodeState(o, v);
```

這裡比 `RecalcNodeFast` 有機會，因為兩次 `SetNodeState` 共享同一個 resolved value `v`，且兩次 enqueue 都寫同一組 global queue：

- `RecalcListNext`
- `RecalcHashNext`
- `RecalcListNextCount`
- `RangePruneS`
- `RangePruneA`
- `RangePruneB`

如果做 pair-specialized fusion，可以把這些 locals hoist 一次，處理 `nn` 再處理 `o`，最後只回寫一次 `RecalcListNextCount`。

但必須保留原本順序：

```text
SetNodeState(nn, v) -> SetNodeState(o, v)
```

這個順序會影響下一 wave enqueue append order，也就是 pop order / Gauss-Seidel 語意。不能為了合併而重排。

### BFS group writeback

位置：`Sim\WireCore.Recalc.cs`

目前形狀：

```csharp
for (int i = 0; i < _groupCount; i++)
    SetNodeState(_groupBuf[i], newState);
```

這是最像有收益的呼叫點。原因是 `_groupCount` 可能大於 1，且每個 member 都共用相同 `newState` 與同一組 queue state。

可行方向是做 group-specialized writeback：

```text
hoist queue locals
for each group member:
    if NodeStates[gm] != newState:
        NodeStates[gm] = newState
        walk NodeTlistGates[gm]
        enqueue downstream nodes
write back RecalcListNextCount once
```

這可能減少：

- 每次呼叫 `SetNodeState` 時重複載入 `RecalcListNext`
- 每次呼叫重複載入 `RecalcHashNext`
- 每次呼叫重複讀寫 `RecalcListNextCount`
- 每次呼叫重複載入 range boundary

但如果 JIT 已經把大部分東西 inline 且留在暫存器，實際收益可能很小。

## 可能省到的成本

### 1. Static field load / store

`SetNodeState` 內會讀：

- `NodeStates`
- `NodeTlistGates`
- `RecalcListNext`
- `RecalcHashNext`
- `RecalcListNextCount`
- `TransistorList`
- `RangePruneS`
- `RangePruneA`
- `RangePruneB`

手動融合 pair/group writeback 有機會把部分 static field load hoist 到外層，尤其是 queue pointers 與 range boundaries。

### 2. `RecalcListNextCount` 回寫頻率

目前 `SetNodeState` 內部每次有 fanout walk 就：

```csharp
int nextCount = RecalcListNextCount;
...
RecalcListNextCount = nextCount;
```

pair/group fusion 可以把多個 node 的 enqueue 合併到同一個 `nextCount` local，最後回寫一次。

這是最有價值的融合點。

### 3. `newState` specialization

`SetNodeState` 已經用：

```csharp
if (newState == 0) { ... } else { ... }
```

在呼叫端融合後，某些場景可能讓 JIT 更容易看出 `newState` 是同一個 local，尤其 pair path 的 `v` 對兩個 node 相同。但 `v` 仍是 runtime value，不是 compile-time constant，所以不應期待 branch 完全消失。

## 可能沒有收益的原因

### 1. JIT 可能已經做了主要 inline

目前 `SetNodeState` 標記 aggressive inline，且專案註解也表示它預期已進入熱迴圈。若實際已 inline，手動複製 body 不會省 call，只會增加 code size。

### 2. Hot loop 已經很大

`ProcessQueue` 註解提到強制 inline `ProcessQueue` 本身曾量到 `-1.4%`，原因是 code bloat。這代表目前程式已經接近「多塞 code 可能傷 I-cache / codegen」的區域。

把 `SetNodeState` 再複製到多個呼叫點，尤其是 `RecalcNodeFast`、B1 pair、BFS fallback 都各放一份，可能讓 instruction footprint 變差。

### 3. 大部分成本可能不在 call boundary

`SetNodeState` 真正貴的地方通常是：

- `NodeTlistGates[nn]` fanout walk
- `TransistorList` sequential scan
- `nextHash[c]` random-ish byte checks
- `NodeStates[c1] != NodeStates[c2]` turn-on prune check

這些成本不會因為手動 inline 消失。

## 建議實驗順序

### P1：只做 B1 pair fusion

先不要動 BFS group。B1 pair path 結構固定、語意邊界清楚、影響範圍小。

實驗版本：

- 保留現有 `SetNodeState`。
- 新增或手動展開 pair-specialized writeback。
- 嚴格保留 `nn` 再 `o` 的處理順序。
- queue locals hoist 一次。
- `RecalcListNextCount` 最後回寫一次。

預期收益：很小，但風險最低。  
若無收益，不建議繼續擴大。

### P2：BFS group writeback fusion

若 P1 有穩定收益，再做 BFS group writeback fusion。

這裡要注意：

- group member 順序必須維持 `_groupBuf[0.._groupCount)`。
- callback enqueue 邏輯仍必須在 group writeback 後。
- 不要改變 `SetNodeState` 對 no-change node 的 early return 語意。
- 不要改變 next-wave enqueue order。

預期收益：比 pair fusion 稍高，但 code size 與維護風險也較高。

### P3：不要先動 RecalcNodeFast

`RecalcNodeFast` 只呼叫一次 `SetNodeState`，且已經先做 `flags != 0` guard。這裡手動 inline 最容易只有 code bloat，建議最後才試。

## 驗證方式

建議用固定命令：

```powershell
dotnet run -c Release -- --pin --system-def-dir "C:\ai_project\AprVisual\ref\metalnes-main\data\system-def" --benchmark "C:\ai_project\AprVisual\nes-test-roms-master\不需要測試(偏向展示DEMO)\full_palette\full_palette.nes" --bench-hc 400000 --extra-ram
```

至少比較：

- H/s
- checksum
- load time
- `renumber` locality-keyed count

checksum 必須一致。  
建議做 interleaved A/B，不要只跑單次，因為先前測試已看到即使用 `--pin` 仍可能有幾個百分點的波動。

## 最終建議

手動 hardcode inline 不是主要槓桿。若要探索，應該把目標定義成：

> 專用化 pair/group writeback，減少多次 `SetNodeState` 連續呼叫時的 queue state reload/writeback。

優先順序：

1. B1 pair fusion。
2. BFS group writeback fusion。
3. 不建議先動 `RecalcNodeFast`。

如果 P1 沒有穩定超過 `~0.5%` 的收益，建議停止。這個區域很可能已經被 JIT inline 得足夠好，繼續手動展開會用可維護性換很小、甚至負的效能差。
