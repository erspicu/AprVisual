# GroupFlags Hotpath 效能分析與優化建議

日期：2026-06-17  
範圍：`C:\ai_project\AprVisual\src\AprVisual.S1\Sim`  
主題：`GroupFlags` 設計在 hotpath 上的成本控制  
限制：不考慮 IR 類策略；此文件只整理策略，不包含程式修改。

## 結論摘要

目前 `GroupFlags` 的設計方向是正確的：用上次解析出的 group driver flags 來判斷「某顆 transistor 關閉後，端點 node 是否一定不需要 enqueue」。問題在於現在的實作把 metadata 成本放在最熱的幾條路徑上，而且寫入範圍大於實際讀取範圍，導致節省的 enqueue / recalc 成本可能被 `GroupFlags` 自己的寫入、讀取與 cache pressure 吃掉。

最值得先做的兩個 A/B：

1. `GroupFlags` 只對 `SetNodeState` 可能讀到的 safe range 寫回，不要全節點寫。
2. 不存完整 flags，改存「以目前 state 來看，turn-off 是否可能造成變化」的預算結果 byte，讓 `SetNodeState` 熱迴圈只讀一個 byte，不再讀 `NodeStates[c]` 做 mask 選擇。

這兩個策略都屬於降低 `GroupFlags` 本身成本，不改變模擬語意；若判斷失敗，保守方向也是「多 enqueue」，不應該造成錯誤 state。

## 目前觀察到的實作

相關位置：

- `Sim\WireCore.cs`
  - `GroupFlags` 是 `byte*`，配置大小為 `NodeCount`。
  - `Reset()` 會配置並填入 `0xFF`，代表 unknown 時不能跳過。
- `Sim\WireCore.FastPath.cs`
  - `RecalcNodeFast()` 在 singleton fast path 寫 `GroupFlags[nn] = flags`。
- `Sim\WireCore.Recalc.cs`
  - B1 pair path 會寫 `GroupFlags[nn]` 與 `GroupFlags[o]`。
  - BFS fallback 會對 `_groupBuf` 內每個 member 寫 `GroupFlags[gm]`。
  - `SetNodeState()` 在 transistor turn-off enqueue 判斷中讀 `GroupFlags[c]`。

目前讀取邏輯集中在 `SetNodeState()` 的關閉導通分支，大意是：

```csharp
if (c >= RangePruneS &&
    (c >= RangePruneB ||
     (GroupFlags[c] & (NodeStates[c] != 0 ? GfMaskFrom1 : GfMaskFrom0)) != 0) &&
    NextHash[c] == 0)
{
    enqueue(c);
}
```

重點是：在 range-prune layout 正常成立時，真正會讀 `GroupFlags[c]` 的區間是 `[RangePruneS, RangePruneB)`。`c < RangePruneS` 直接不 enqueue；`c >= RangePruneB` 屬於 unsafe / memory 類，直接 enqueue，不需要讀 `GroupFlags`。

以目前曾觀察到的 layout 數字為例：

- `A=460`
- `S=1275`
- `B=7532`
- live nodes 約 `14723`

則理論上只有 `[1275, 7532)` 這段約 `6257` 顆 node 需要 `GroupFlags`，大約是 live nodes 的 `42.5%`。若目前所有 fast path / pair / BFS 都全節點寫回，可能有一半以上的 metadata write 是永遠不會被讀取的。

## 為什麼 GroupFlags 可能變慢

### 1. 寫入發生在最熱路徑

`RecalcNodeFast()`、B1 pair path、BFS member writeback 都是目前 sim 的高頻路徑。多一個 `GroupFlags[...] = ...` 看起來便宜，但在這類 tight loop 裡可能造成：

- 多一條 store。
- 額外 cache line touch。
- store buffer 壓力。
- 與 `NodeStates` / `RecalcHash` / queue metadata 競爭 L1/L2。

如果 `GroupFlags` 的 skip rate 不夠高，這條 store 很容易比省下的少量 enqueue 更貴。

### 2. 寫入範圍大於讀取範圍

目前 `GroupFlags` 寫回是以「哪個 node 被 resolve」為準；但讀取是以「哪個 endpoint 在 turn-off fanout 裡被檢查，且落在 `[S,B)`」為準。這兩個集合不相等。

直接結果是：很多 node 的 `GroupFlags` 維護成本沒有回收機會。

### 3. 讀取端仍有 state-dependent mask 成本

目前讀取端需要：

1. 讀 `GroupFlags[c]`
2. 讀 `NodeStates[c]`
3. 根據 state 選 `GfMaskFrom1` 或 `GfMaskFrom0`
4. 做 bit test

這段在 `SetNodeState()` 的 transistor fanout unrolled loop 裡，屬於非常熱的 inner loop。若 branch / data dependency 拉長 critical path，即使 skip 正確，也可能吃掉收益。

### 4. 存完整 flags 過度通用

`SetNodeState()` turn-off skip 其實不需要完整 group flags。它只需要知道一件事：

> 以 endpoint 目前 state 來看，關掉這條 transistor 後，這個 endpoint 是否可能因 supply / pullup / force / callback 等因素需要重算？

完整 flags 可以推導這件事，但每次在 hotpath 推導太貴。

## 建議策略

## P0：先加 DEBUG counter，確認成本與收益

建議先用 DEBUG 或可關閉的 profiling counter 量下面幾項，不要先猜：

- `GroupFlags` 寫入總數。
- `GroupFlags` 寫入中，落在 `[S,B)` 的數量。
- `GroupFlags` 寫入來源：
  - `RecalcNodeFast`
  - B1 pair path
  - BFS fallback
- `SetNodeState` turn-off endpoint 檢查總數。
- 落在 `[S,B)` 且真的讀 `GroupFlags` 的次數。
- `GroupFlags` 判斷為可 skip 的次數。
- 因 `c >= B` 直接 enqueue 的次數。
- 因 `c < S` 直接 skip 的次數。

這些 counter 可以回答核心問題：

> `GroupFlags` 每省一次 enqueue，平均付出了幾次 write 與幾次 read？

如果 write/read 比例太差，就需要限制維護範圍或只對高 ROI node 啟用。

## P1：只對 `[RangePruneS, RangePruneB)` 寫 GroupFlags

這是最優先嘗試的策略。

目前 `GroupFlags` 只有 `[S,B)` 會被讀，所以 writeback 可以改成：

```csharp
int s = RangePruneS;
int b = RangePruneB;
if ((uint)(nn - s) < (uint)(b - s))
    GroupFlags[nn] = (byte)flags;
```

B1 pair path 對 `nn`、`o` 各自判斷；BFS 對每個 `gm` 判斷。

正確性理由：

- `[0,S)` 在 turn-off path 直接 skip，不讀 `GroupFlags`。
- `[B,NodeCount)` 在 turn-off path 屬於 unsafe / memory 類，直接 enqueue，不讀 `GroupFlags`。
- 因此不維護這兩段的 `GroupFlags` 不會影響決策。

預期效果：

- 若目前 layout 類似 `S=1275, B=7532, live=14723`，理論上可砍掉約 `57%` 的 `GroupFlags` metadata write。
- 實際收益取決於 hot nodes 分布；如果 hot nodes 多集中在 `[S,B)`，收益會小一些，但此策略仍是低風險 A/B。

注意事項：

- `GroupFlags` 初始填 `0xFF` 可以先保留，避免測試初期引入 unknown-state 差異。
- 若後續要進一步減少 Reset 成本，可只填 `[S,B)`，但這不是 steady-state hotpath 的第一優先。

## P2：改存 TurnOffCanChange，而不是完整 flags

目前 hot read 端每次都用：

```csharp
GroupFlags[c] & (NodeStates[c] != 0 ? GfMaskFrom1 : GfMaskFrom0)
```

建議把這個結果提前在 writeback 時計算，讓 hot read 端變成：

```csharp
TurnOffCanChange[c] != 0
```

概念：

```csharp
byte canChange = (byte)(((flags & (newState != 0 ? GfMaskFrom1 : GfMaskFrom0)) != 0) ? 1 : 0);
TurnOffCanChange[nn] = canChange;
```

如果要避免每次 writeback 做 branch / mask select，可用 LUT：

```csharp
// index = (flags << 1) | state
TurnOffCanChangeLut[index]
```

其中 `flags` 是 byte，`state` 是 0/1，所以 LUT 最多 512 entries。

正確性理由：

- `SetNodeState()` turn-off 判斷只需要「以 endpoint 目前 state 來看，turn-off 是否可能改變它」。
- 每次 group 被 resolve 時，程式本來就知道 `flags` 與 resolved `newState`。
- 若某 node 的 state 改變，必須經過 resolve/writeback；此時同步更新 `TurnOffCanChange` 即可保持一致。
- 初始 unknown 可填 `1`，代表保守 enqueue，不跳過。

預期效果：

- `SetNodeState()` fanout hot loop 少一次 `NodeStates[c]` load。
- 少一次 state-dependent mask select。
- `GroupFlags` 語意變成 one-byte predicate，比完整 flags 更貼近用途。

需要檢查的邊界：

- `RecalcNodeFast()` 的 `flags == 0` floating hold case：若 node 落在 `[S,B)`，應寫 `TurnOffCanChange[nn] = 0`，因為它沒有 active driver，turn-off isolation 不需要重算。
- BFS fallback 若 `newState` 是由 capacitance tie-break 得出，仍可用該 `newState` 預算。
- 任何直接改 `NodeStates` 而不走 resolve/writeback 的路徑，都必須同步更新此 predicate；若沒有這類路徑，風險低。

## P3：P1 + P2 合併

最佳形態不是「全節點完整 flags」，而是：

```text
只在 [S,B) 維護 TurnOffCanChange byte
```

turn-off endpoint hot loop 可簡化為：

```csharp
if (c >= s &&
    (c >= b || TurnOffCanChange[c] != 0) &&
    nextHash[c] == 0)
{
    enqueue(c);
}
```

這同時降低：

- write 次數。
- read 端依賴。
- bitmask 判斷。
- `NodeStates` 額外讀取。

如果 `GroupFlags` 目前確實讓 H/s 從 13xK 掉到約 12xK，這是最可能把成本壓回去的組合。

## P4：只對高 ROI node 啟用 GroupFlags / TurnOffCanChange

如果 P1 + P2 仍然不划算，代表 `[S,B)` 內仍有大量低收益 node。下一步可以用 warmup capture 選出高 ROI subset。

候選條件：

- endpoint turn-off check 次數高。
- `TurnOffCanChange == 0` 的 skip 次數高。
- 本身被 resolve/writeback 次數低於 skip 收益。

不要用 per-node bitset 在 hot loop 裡判斷，因為又會多一次 metadata read。比較好的方向是 renumber / partition：

```text
[S, G)  safe but GroupFlags disabled：一律 enqueue
[G, B)  safe and GroupFlags enabled：讀 TurnOffCanChange
```

hot loop 變成：

```csharp
if (c >= s &&
    (c < g || c >= b || TurnOffCanChange[c] != 0) &&
    nextHash[c] == 0)
{
    enqueue(c);
}
```

不過這會多一個 range compare，所以只有在低 ROI node 很多時才值得。正確性仍然安全，因為關掉 GroupFlags 只是多 enqueue，不會少算。

## P5：不建議的方向

### 不建議把 flag pack 回 NodeStates

之前已經看過類似 mask-tax 問題。`NodeStates` 是極熱資料，塞更多語意進去很可能讓所有 state read/write 都付費。`GroupFlags` 的問題應該用「縮小維護範圍」與「預算 predicate」解，不建議污染 `NodeStates`。

### 不建議 lazy compute flags on read

turn-off endpoint read 本來就是為了避免 enqueue / recalc。如果在 read side 才回頭推導 group flags，幾乎一定比直接 enqueue 更貴。

### 不建議 per-transistor metadata

目前問題是 metadata 成本已經偏高，再把資料掛到 transistor 粒度會增加記憶體 footprint 與 cache 壓力，除非 counter 證明某一小群 transistor 有極高重複收益。

## 建議實驗順序

1. Baseline：保留目前實作，只加 counter，確認 write/read/skip 比例。
2. A/B P1：只在 `[S,B)` 寫目前的 `GroupFlags`。
3. A/B P2：全範圍改存 `TurnOffCanChange`，確認 read side 簡化的收益。
4. A/B P1+P2：只在 `[S,B)` 維護 `TurnOffCanChange`。
5. 若仍低於 13xK H/s，再做 P4 高 ROI subset。

每一步都要用固定 ROM / 固定 half-cycle 檢查：

- H/s。
- checksum。
- enqueue count。
- recalc pop count。
- `GroupFlags` 或 `TurnOffCanChange` read count。
- skip count。

建議用目前已跑過的 200k hc 做對照，並保留 checksum，例如曾觀察到：

- `200000 hc`
- 約 `126,750 hc/s`
- checksum `0x9B103E5E206E4C37`

若 checksum 不一致，該策略直接視為失敗；若 checksum 一致但 H/s 沒上升，代表 skip 節省仍不足以支付 metadata 成本。

## 最終建議

優先實作順序：

1. `GroupFlags` writeback 加 `[S,B)` range guard。
2. 把 `GroupFlags` 語意改成 `TurnOffCanChange` byte，初始值填 `1`。
3. 用 512-entry LUT 在 writeback 時計算 predicate。
4. 保留原本完整 `GroupFlags` 版本用編譯旗標或短期 branch 方便 A/B。
5. 若 P1+P2 還不夠，再做 high ROI subset；不要一開始就引入 subset，避免 range 邏輯與資料結構變複雜。

判斷標準很簡單：`GroupFlags` 不是不能用，而是不能讓所有 node 都為少數可 skip 的 case 付費。把成本限制在會讀、而且讀了真的常常能跳過的 node 上，才有機會把效能拉回 13xK H/s 以上。
