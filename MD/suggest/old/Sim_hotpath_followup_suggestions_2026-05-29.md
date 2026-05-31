# Sim hotpath follow-up 建議

日期: 2026-05-29  
範圍: `C:\ai_project\AprVisual\src\AprVisual.S1\Sim\*.cs`  
前提: 不考慮 IR / codegen / levelize / oblivious / SIMD queue。也不重複 `Sim_hotpath_efficiency_suggestions_2026-05-28.md` 與 `hotpath_review_LIST_2026-05-28.md` 已列過的項目。

## 目前狀態

更新後的 `Sim` 已經套上多個上一輪有效項目:

- `WireCore.Group.cs`: `NodeConnections` 已延後到 floating branch 才讀；`GetNodeValue` runtime ForceCompute mask 已移除。
- `WireCore.Recalc.cs`: `SetNodeState` 已 inline enqueue。
- `WireCore.Handlers.cs`: callback 已改 pending queue；`ReadBits/WriteBits` 已有 `int[]` overload。
- 已測負效益且不應重提: GND/PWR skip scan、clock direct fast path。

本次先建置與簡短量測目前基準:

```powershell
dotnet build -c Release
dotnet run -c Release -- --system-def-dir "C:\ai_project\AprVisual\ref\metalnes-main\data\system-def" --benchmark "C:\ai_project\AprVisual\nes-test-roms-master\不需要測試(偏向展示DEMO)\full_palette\full_palette.nes" --bench-hc 200000
```

結果:

- build: 0 warning / 0 error
- `--bench-hc 50000`: `55,876 hc/s`, checksum `0x933ABE7915AC18BE`
- `--bench-hc 200000`: `64,396 hc/s`, checksum `0x9B103E5E206E4C37`

結論: 大型 obvious hotpath 已經被處理過，剩下建議都要逐項 A/B。若沒有 profiler counter 支持，不建議一次改多項。

## 新建議

### P1. ROM memory handler 不要監看 data bus

位置: `WireCore.Handlers.cs:190-221`

目前 `AttachRamLikeHandler()` 無論 RAM/ROM 都把 `dataBusL` 加進 callback trigger:

```csharp
trigger.AddRange(addrL);
trigger.AddRange(dataBusL);
```

但 ROM 是 read-only，輸出只取決於 `cs + addr`。data bus 改變時，ROM handler 目前仍會被喚醒，然後重讀 address、重讀 ROM byte、再跑一次 `WriteBits(dataOut, value)`。若輸出值相同，`WriteBits` 多半不會 settle，但 callback / ReadBits / 8-bit flag check 成本仍會付。

建議:

- `isRom == true` 時不要 resolve 或加入 `Full("d[]")` trigger。
- ROM trigger 只保留 `cs + addr`。
- RAM 保持現狀，因為 write path 需要 data bus 變化能觸發寫入。

預期收益: 中。PRG ROM / CHR ROM 都是高頻存取來源，移除 data-bus watcher 也會少建 16 個左右 callback fake transistors。  
風險: 低到中。要確認 ROM output 的 persistent `SetHigh/SetLow` drive 不需要靠 data bus 變化重送。以目前註解「data bus release 由 chip select pass transistors 隱含處理」判斷，ROM output 不依賴 data bus。

驗證:

- `--selftest`
- `--bench-hc 200000` checksum
- frame-based benchmark 或 PPU probe，因為 CHR ROM 也會受影響

### P1. memory callback body 依 ROM/RAM 模式拆開，並 capture `byte[]` 與 mask

位置: `WireCore.Handlers.cs:190-221`、`WireCore.Handlers.cs:105-111`

目前 callback 每次都走共同 body:

```csharp
bool writing = !isRom && we != EmptyNode && NodeStates[we] == 0;
if (writing) mem.Write(address, (byte)ReadBits(dataOut));
else         WriteBits(dataOut, mem.Read(address));
```

`isRom`、`we != EmptyNode`、`mem.Read/Write()`、`Data.Length - 1` 都是 attach-time 可固定的資訊。JIT 可能會 inline 部分 instance method，但 closure 內仍有多個不必要 branch / field access。

建議:

- 在 `AttachRamLikeHandler` 內 capture:
  - `byte[] data = mem.Data;`
  - `int mask = data.Length - 1;`
- ROM 或 `we == EmptyNode` 走 read-only callback:
  - selected 後 `WriteBits(dataOut, data[address & mask]);`
- RAM with `/we` 走 read/write callback:
  - selected 後只判斷 `NodeStates[we] == 0`
  - write: `data[address & mask] = (byte)ReadBits(dataOut)`
  - read: `WriteBits(dataOut, data[address & mask])`

預期收益: 小到中。單次 callback 省不了核心 BFS，但 ROM/RAM access 很頻繁。  
風險: 低。`Memory.Data` 在現有程式只建立一次，reset 只 `Clear()`，不替換陣列。

### P2. `SetNodeState` 依 `newState` 拆成 high/low 兩個 loop

位置: `WireCore.Recalc.cs:127-157`

目前 fanout loop 內每一個 `(c1,c2)` pair 都測一次 invariant:

```csharp
if (newState == 0 && c2 != npwr && c2 != ngnd && nextHash[c2] == 0)
```

`newState` 在整個 `SetNodeState` call 期間不變。可以在進入 loop 前拆成:

- `newState != 0`: 只 enqueue `c1`
- `newState == 0`: enqueue `c1`，並對非 supply 的 `c2` enqueue

這會移除 fanout 內每 pair 的 `newState == 0` branch。代價是 code size 變大，可能影響 JIT inline cascade，所以必須 A/B。

預期收益: 小到中，取決於 state flip fanout 分布。  
風險: 中。這種複製 hot loop 的改動曾經可能因 JIT code shape 變差而負效益；只建議單獨實測。

### P2. callback target 改成 node-id 直查表

位置: `WireCore.Recalc.cs:118-123`、`WireCore.Handlers.cs:35-51`、`WireCore.cs:153-163`

目前 HasCallback group 寫回後會掃 group，並從 managed build-time `Nodes` list 找 callback:

```csharp
var node = Nodes[_groupBuf[i]];
if (node?.Callback != null) EnqueueCallback(node.Callback);
```

callback group 不算最多，但 video / memory callback target 會經常出現；這段會從 unmanaged hot path 跳回 managed `List<Node?>` / `Node` object graph。

建議:

- Reset 時建立 `CallbackInfo?[] _callbackByNode`，size `NodeCount`。
- 對每個有 callback 的 node 設 `_callbackByNode[nn] = node.Callback`。
- `RecalcNode` 的 callback branch 改讀 array:
  - `var cb = _callbackByNode[_groupBuf[i]]; if (cb != null) EnqueueCallback(cb);`

預期收益: 小。只有 HasCallback group 受益，但能讓 callback enqueue path 比較直。  
風險: 低到中。要確認 `ResetHandlers()` / `FreeUnmanagedMemory()` / `Reset()` 生命週期不留下舊 callback array。

### P3. `InvokeCallbacks` swap 改成手寫 temp，避免 tuple swap 形狀影響

位置: `WireCore.Handlers.cs:74-87`

目前 pending / processing list 交換用 tuple assignment:

```csharp
(_pendingCallbacks, _processingCallbacks) = (_processingCallbacks, _pendingCallbacks);
```

理論上不配置，但 hot callback path 裡可以用手寫 temp 讓 IL/JIT 形狀更直:

```csharp
var tmp = _pendingCallbacks;
_pendingCallbacks = _processingCallbacks;
_processingCallbacks = tmp;
```

預期收益: 很小。  
風險: 低。  
備註: 只有在 callback path 已確定是瓶頸時才值得測。

## 暫不建議重開

以下項目已在前檔提出或實測，這次不重複:

- `NodeConnections` 延後讀取: 已採用。
- `SetNodeState` inline enqueue: 已採用；本次只補充「依 newState 拆 loop」。
- callback pending queue: 已採用。
- `ReadBits/WriteBits int[] overload`: 已採用。
- GND/PWR scan skip: 已兩次負效益，不建議重開。
- clock direct fast path: 已負效益，不建議重開。
- `RunFrame()` 直接 `StepCycle()`: 已提出且暫緩，本次不重列。
- `IsPureLogic` 合進 `NodeInfo` padding / FastKind: 已提出且暫不做，本次不重列。
- normal-to-supply lowering、epoch stamp、headless no-video、Reset flattening: 已提出或被歸為低優先，本次不重列。

## 建議實作順序

1. 先做「ROM 不監看 data bus」。這是本次唯一可能明顯減少 callback 觸發數的項目。
2. 再做 memory callback body specialization，最好和第 1 項分開量測。
3. `SetNodeState` split loop 只單獨測，不要和其他改動混在一起。
4. callback target array 與 tuple swap 放最後；若前兩項已有收益，這兩項可能只剩噪音級。

每一步至少跑:

```powershell
dotnet build -c Release
dotnet run -c Release -- --selftest
dotnet run -c Release -- --system-def-dir "C:\ai_project\AprVisual\ref\metalnes-main\data\system-def" --benchmark "C:\ai_project\AprVisual\nes-test-roms-master\不需要測試(偏向展示DEMO)\full_palette\full_palette.nes" --bench-hc 200000
```

若 `--bench-hc 200000` checksum 不維持目前基準 `0x9B103E5E206E4C37`，直接 revert。
