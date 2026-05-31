# Netlist 純 BFS 模擬優化建議彙整 - 2026-05-29

## 目標與範圍

本文件依據下列資料整理：

- C# 版：`AprVisual.S1`
- Rust 版：`rust-s1`
- 既有建議文件：`20260529/*.docx`

限制條件：

- 不走 IR / codegen / dispatcher。
- 保留純 node / transistor switch-level BFS 模擬語意。
- 優先考慮 branchless、bitwise、cache locality、AOT 前置整理。
- 所有修改必須以固定 half-cycle 的 `NodeStates checksum` 做 bit-identical A/B 驗證。

目前文件中的基準描述是 C# 約 64K hc/s、Rust 約 67K hc/s。兩者接近，表示主要瓶頸已不是語言抽象，而是資料相依分支、L1/L2 cache miss、store/load buffer 壓力與圖走訪工作量。

## 原始碼現況

### C# 版已具備的優化

- `AprVisual.S1/Sim/WireCore.Native.cs` 使用 `NativeMemory.AlignedAlloc(..., 64)`，熱陣列 64-byte aligned。
- `WireCore.cs` 已將熱資料拆成 `NodeStates`、`NodeInfos`、`TransistorList`、`RecalcHash`、`_inGroup` 等 unmanaged array。
- `TransistorList` 已壓成 `ushort*`，並使用 index 0 的 sentinel 0。
- `NodeInfo` 只保留 hot fields：`Flags`、`TlistC1c2s`、`TlistC1gnd`、`TlistC1pwr`。
- `NodeConnections`、`NodeTlistGates` 已拆成 cold arrays。
- `FlagsToState[256]` 已把 group flags 到 resolved state 的決策查表化。
- `WireCore.Lower.cs` 已做 always-on normal-normal short merge、`gate == vss` dead transistor drop、self-loop/drop/dedupe、dense renumber。
- `WireCore.FastPath.cs` 已有 pure-logic O(1) fast path。
- `WireCore.Group.cs` 的 C# 版使用 iterative BFS，避免遞迴。
- `WireCore.Recalc.cs` 已有 double-buffer recalc queue、byte hash、inline enqueue、SetHigh/SetLow no-op skip。

### Rust 版已具備的優化

- `rust-s1/src/wire.rs` 使用 `NodeHot` 拆分 hot fields，cold fields 分別放在 `node_connections`、`node_tlist_gates`。
- 大量使用 `unsafe` / `get_unchecked` 移除 bounds checks。
- `Cargo.toml` release profile 已設定 `lto = "fat"`、`codegen-units = 1`、`opt-level = 3`。
- pure-logic fast path hardcoded on。
- 保留 recursive `add_node_to_group`，註解中記錄 Rust iterative BFS 曾比 recursive 慢約 1.3%，因此 Rust 版不能直接照 C# 改法假設會更快。

## 熱點對照

| 區域 | C# 位置 | Rust 位置 | 現況問題 |
|---|---|---|---|
| BFS group walk | `WireCore.Group.cs:85` | `wire.rs:283` | `NodeStates[gate]`、`in_group`、GND/PWR early break 都是資料相依分支 |
| state fanout enqueue | `WireCore.Recalc.cs:131` | `wire.rs:359` | `nextHash == 0`、`newState == 0`、supply check 在熱迴圈內 |
| pure logic fast path | `WireCore.FastPath.cs:73` | `wire.rs:177` | GND/PWR gate 掃描用 `if + break` |
| queue enqueue | `WireCore.Recalc.cs:45` | `wire.rs:237` | supply guard 與 duplicate guard 是分支 |
| handler bit read/write | `WireCore.Handlers.cs:133` | `wire.rs:206`、`run_mem_handler` | `ReadBits` 仍用 `if state != 0` 疊 bit |
| Rust memory alignment | C# 已 aligned | Rust `Vec<T>` | `Vec<u8>` 不保證 64-byte 起始位址 |

## 建議優先順序

### P0 - 低風險、先做

1. `SetNodeState` loop unswitch

   目前 C# / Rust 都在 fanout loop 內判斷 `newState == 0`。此判斷在單次 `SetNodeState` 中是常數，應外提成兩個 loop：

   - `newState == 0`：enqueue `c1` 與 `c2`。
   - `newState == 1`：只 enqueue `c1`，直接跳過 `c2`。

   這不是 full branchless，但會先移除熱迴圈內一個高頻分支，語意風險低。

2. Branchless enqueue with hash shield

   將 `if hash == 0` 改成：

   ```c
   isNew = hash[node] ^ 1;
   list[count] = node;
   hash[node] = 1;
   count += isNew;
   ```

   前置條件：

   - `hash` 必須嚴格維持 0/1。
   - `Npwr` / `Ngnd` 在 `RecalcHash` 與 `RecalcHashNext` 都永久設為 1，避免 supply check。
   - `RecalcList` / `RecalcListNext` 建議配置 `NodeCount + 1`，因為 branchless duplicate enqueue 會在 `count` 不前進時仍寫 `list[count]`。若 queue 已滿而下一次是 duplicate，沒有額外 sentinel slot 可能越界。

3. Branchless `RecalcNodeFast`

   對 `TlistC1gnd` / `TlistC1pwr` 掃描時，把 `if state != 0 break` 改成 OR-all：

   ```c
   anyGnd |= NodeStates[gate];
   flags |= anyGnd << 5;
   anyPwr |= NodeStates[gate];
   flags |= anyPwr << 4;
   ```

   適合 pure-logic fast path，因為這類 list 通常短。若某些 list 很長，需保留 early-break 版本做 A/B。

4. Branchless `ReadBits`

   目前 C# `ReadBits` 與 Rust `read_bits` 都用：

   ```c
   if (NodeStates[nodes[i]] != 0) v |= 1 << i;
   ```

   可直接改成：

   ```c
   v |= NodeStates[nodes[i]] << i;
   ```

   Rust 對應：

   ```rust
   v |= (*self.node_states.get_unchecked(nn as usize) as u32) << i;
   ```

   前置條件是 `NodeStates` 永遠 0/1。這個改動風險低，handler、video、memory address/data path 都會受益。

### P1 - 中風險、需分支驗證

1. Branchless `AddNodeToGroup`

   核心公式：

   ```c
   shouldAdd = NodeStates[gate] & (inGroup[other] ^ 1);
   inGroup[other] |= shouldAdd;
   groupBuf[groupCount] = other;
   RecalcHash[other] &= shouldAdd ^ 1;
   groupCount += shouldAdd;
   ```

   注意事項：

   - `_groupBuf` / `group_buf` 也建議配置 `NodeCount + 1`，理由同 queue。
   - 不建議在 `shouldAdd == 0` 時仍無條件讀 `NodeInfos[other].Flags`，這可能造成額外 cache miss，抵消 branchless 的收益。
   - 更好的結構是「enqueue 時只標記與入隊，flags 在 pop 該 node 時 OR」。現行 C# `AddNodeOrApplyDriver` 會在 enqueue 時讀一次 `NodeInfos[nn]`，之後 pop 又讀一次；改成 pop 時 OR flags 可減少一次 hot struct 讀取。
   - Rust 版之前 iterative BFS 實測曾慢於 recursive，因此 Rust branchless BFS 必須獨立做 variant，不能直接取代 baseline。

2. `in_group` generation counter

   現況每次 `compute_node_group` 會清掉上一輪 group 的 `in_group`。可改成 byte generation stamp：

   - `in_group[node] == currentGeneration` 表示在本 group。
   - 每次 group 計算前 `currentGeneration++`。
   - generation 溢位時才整個 `in_group` 清 0。

   對 branchless BFS，`notInGroup` 可用整數 zero-test 轉 0/1。若使用 `!=`，JIT/LLVM 通常會產生 `setcc`，但仍需看組語或實測。

3. Rust 64-byte aligned allocation

   `Vec<u8>` 只保證 element alignment，不保證 64-byte 起始位址。對 `node_states`、`recalc_hash`、`recalc_hash_next`、`in_group`、`group_buf`、`transistor_list` 可考慮 aligned owner。

   重要校正：

   - 不要把 `NodeHot` 寫成 `#[repr(align(64))]` 後放進 `Vec<NodeHot>`，那會讓每個元素 stride 變成 64 bytes，熱資料膨脹 4 倍，通常會變慢。
   - 正確做法是 aligned allocation，不是 per-element 64-byte wrapper。
   - 可用 crate 或自建 `AlignedVec<T>`，維持元素 stride 不變，只保證 allocation base address 64-byte aligned。

4. Rust raw pointer iteration

   `get_unchecked(index)` 可改成 raw pointer `p = p.add(1/2)`。LLVM 有時可自行最佳化 index form，但 raw pointer form較能穩定產生簡短 addressing。此項需要看實測。

### P2 - AOT 圖整理與 cache locality

1. Clock-phase partitioning

   在 C# `Reset()` flatten `TlistC1c2s` 時，依 gate 分段：

   - clock-low/off 可整段 skip 的 edges。
   - clock-high/on 可整段 skip 的 edges。
   - 其他一般 gate。

   Runtime BFS 依當前 clock state 選擇掃描範圍。此做法仍是純 transistor graph traversal，不是 IR。

   風險：

   - 必須正確辨識 clock / phi node，例如 `clk`、`cpu.clk0`、`ppu.clk0`、`ppu.pclk1` 等實際語意不同。
   - 若某些 clock-derived node 不是單純相位常數，不能當作固定 skip 條件。
   - 需在 snapshot format 補充 per-node offset 或新 hot fields，Rust 才能使用。

2. Adjacency sorting by probability / degree

   對 `TlistC1gnd`、`TlistC1pwr`：

   - 若保留 early-break 版本，把最常為 1 的 gate 放前面。
   - 若使用 OR-all branchless，排序幫助較小，但可改善 prefetch/locality。

   可用 warm-up 統計每個 gate 的 high ratio 或 toggle count，再重排 sub-list。

3. Hypergraph / cache-line-aware renumbering

   目標不是降低圖論 bandwidth 本身，而是讓 BFS 常讀到的 `NodeStates[gate]` 靠近同一 cache line。

   可嘗試：

   - 以 transistor endpoints 與 gate references 建 hypergraph。
   - 目標函數是減少跨 64-byte block 的 `NodeStates` 讀取。
   - 先用 lightweight heuristic，不一定要直接導入 METIS。

4. Software prefetch

   對長 `TlistC1c2s` 可嘗試提前讀未來 gate 並 prefetch `NodeStates[futureGate]`。

   實務條件：

   - 只對長 list 啟用，例如長度大於 8 或 16。
   - prefetch distance 需測 4 / 8 / 16。
   - 短 list 啟用 prefetch 通常只會增加指令成本。

### P3 - 高風險或長期項目

1. Warm-up constant propagation

   用固定 reset / warm-up workload 找長期不變 node，再視為 VCC/GND 重新 lowering。風險是「某 ROM 或某狀態才會翻轉」的 node 被誤判常數，必須有非常嚴格的白名單或形式分析。

2. Macro-node / graph condensation

   例如串聯 pull-down stack 可壓成一條 compound edge。這不是傳統 IR codegen，但已引入 compound condition，語意風險接近局部 IR。若要做，應只在可形式證明內部節點沒有外部可觀測 callback / capacitance / alternate path 時套用。

3. Non-temporal framebuffer store

   Framebuffer 是寫後短期不讀的資料，理論上可用 streaming store 避免污染 L1。不過目前 pixel write 是 callback 觸發、寫入分散，且 GDI/display 端可能很快讀取。此項優先度低，必須實測。

## Branchless 專題整理

### 可安全 branchless 化的前置 invariant

- `NodeStates` 僅允許 0/1。
- `RecalcHash`、`RecalcHashNext`、`_inGroup` / `in_group` 若走 XOR/OR 技巧，也必須僅允許 0/1，或改成 generation stamp 後使用明確 zero/equality test。
- branchless queue 寫入需要額外 sentinel capacity，至少 `NodeCount + 1`。
- supply hash shield 必須同時套在 current/next hash buffer，因為 recalc queue 會 swap。
- 每個 branchless 變體都必須保留 checksum A/B 測試，不能只看 hc/s。

### `SetNodeState` 建議落地順序

1. 先做 loop unswitch，不改 enqueue 語意。
2. 加入 supply hash shield。
3. 將 enqueue 改為 XOR branchless。
4. 將 `EnqueueNode` 改成同一套 branchless helper。
5. A/B checksum 與 hc/s。

這一區比 `AddNodeToGroup` 更適合先做，因為它不改 BFS 結構，風險較低。

### `AddNodeToGroup` 建議落地順序

1. C# 先把 `_groupFlags |= ns.Flags` 移到 pop 時執行，減少 enqueue 時的第二次 `NodeInfo` 讀取。
2. 保留目前 iterative BFS，將 normal channel 的 `if NodeStates[gate]` 改成 `shouldAdd` branchless。
3. GND/PWR 先只在 fast path 改 OR-all，BFS 主路徑可另外測 OR-all vs early-break。
4. Rust 版先不要直接改 recursive baseline；另開 iterative branchless 變體測試。

### 哪些 branch 不建議強行消除

- `ProcessQueue` 的 `if RecalcHash[nn] != 0`：這個 branch 擋掉的是昂貴的 `RecalcNode`，即使可 branchless 執行，也可能因重算大量已被 group 吃掉的 node 而變慢。
- callback / memory handler 中的 `cs` guard：這是高階行為分支，若 chip select 多數時間 inactive，early return 很便宜。
- frame bounds check：pixel 寫入如果座標大多在畫面外或 visible window 有明確區段，應先從 callback 觸發頻率與資料流下手，不要單純 branchless 化。

## Bitwise 專題整理

### 已經適合的 bit layout

- `NodeFlags` 用 byte flags，且 `Gnd = 1 << 5`、`Pwr = 1 << 4`，可直接用 `state << 5` / `state << 4` 合成 flags。
- `FlagsToState[256]` 已將多層 if 決策查表化。
- `NodeStates` 用 byte 而非 bitset 是合理的，因為 BFS 大量是 random single-node load；bitset 的 shift/mask 可能比 byte load 更貴。

### 追加 bitwise 作法

1. Branchless bit gather for `ReadBits`

   這是最直接的 bitwise 改法：

   ```c
   value |= NodeStates[node] << bitIndex;
   ```

   適用 address bus、data bus、PPU hpos/vpos/palette pointer。

2. Branchless `SetBitQueued`

   `WriteBits` 可避免每 bit 分支選 `SetHighQueued` 或 `SetLowQueued`，改成直接從 bit 產生 flags：

   ```c
   bit = (value >> i) & 1;
   highMask = -bit & SetHigh;
   lowMask = -(bit ^ 1) & SetLow;
   newFlags = (oldFlags & ~(SetHigh | SetLow)) | highMask | lowMask;
   changed |= oldFlags ^ newFlags;
   branchless_enqueue_if_changed(node, changedBit);
   ```

   注意 `changed` 到 `ProcessQueue()` 仍需要一個外層 branch，這個 branch 不在 per-bit 熱迴圈內，保留即可。

3. NodeState bitset mirror for long gate lists

   可保留 byte `NodeStates` 作為主資料，再增加 `ulong[] NodeStateBits` mirror。`SetNodeState` 同步更新 bit：

   ```c
   word = node >> 6;
   mask = 1UL << (node & 63);
   bits[word] = (bits[word] & ~mask) | (-(ulong)newState & mask);
   ```

   AOT 對長 `TlistC1gnd` / `TlistC1pwr` 建立 `(wordIndex, mask)` list，runtime 用：

   ```c
   any |= NodeStateBits[wordIndex] & mask;
   ```

   適用條件：

   - gate list 長度夠長，例如大於 8 或 16。
   - 多個 gate 落在少數 `ulong` word。
   - 對短 list 不應使用，因為 byte load 更便宜。

4. Contiguous node packing fast path

   若 AOT renumber 後某些 bus 或 gate list 是連續 node id，可用 unaligned 32/64-bit load 加 mask 一次讀多個 state byte。這比泛用 SIMD gather 便宜，但只適用於連續或近連續 node。

5. 避免全面 bitset 化 `RecalcHash` / `_inGroup`

   原始碼註解已提到 hash bitset 變體曾因 shift/mask 成本抵銷 cache benefit。建議保留 byte hash，除非有新資料顯示 bitset mirror 在某個長 list 類型上有明確收益。

## C# 版具體建議摘要

1. `WireCore.Recalc.cs`

   - `Reset()` 初始化 `RecalcHash[Npwr/Ngnd]` 與 `RecalcHashNext[Npwr/Ngnd]` 為 1。
   - `RecalcList` / `RecalcListNext` 配置 `NodeCount + 1`。
   - `SetNodeState` 做 loop unswitch。
   - fanout enqueue 改成 `isNew = nextHash[id] ^ 1`。
   - `EnqueueNode` 改成 branchless helper，或至少讓 hot internal enqueue 先 branchless。

2. `WireCore.FastPath.cs`

   - `RecalcNodeFast` 的 GND/PWR list 改 OR-all。
   - 可移除 `if Tlist != 0`，利用 `TransistorList[0] == 0` sentinel，但需 A/B。若空 list 比例很高，外層 branch 可能比多一次 sentinel load 更快。

3. `WireCore.Group.cs`

   - 先把 group flags 的 OR 移到 pop 時做，降低 `NodeInfo` 重複讀取。
   - branchless normal-channel enqueue 作為獨立測試。
   - GND/PWR OR-all 不一定全面替代 early-break，建議依 list length 或 profiling 決定。

4. `WireCore.Handlers.cs`

   - `ReadBits(int[])` 與 `ReadBits(IReadOnlyList<int>)` 改成 bitwise accumulate。
   - 若 memory/video callback 很熱，再做 branchless `WriteBits` / `SetBitQueued`。

## Rust 版具體建議摘要

1. `rust-s1/src/wire.rs`

   - `from_snapshot` 建立 `recalc_hash` / `recalc_hash_next` 時，先設 `npwr` / `ngnd` shield。
   - `recalc_list` / `recalc_list_next` / `group_buf` 配置 `nc + 1`。
   - `set_node_state` 做 loop unswitch 與 branchless enqueue。
   - `recalc_node_fast` 改 OR-all。
   - `read_bits` 改成 `v |= state << i`。

2. Rust BFS

   - 不直接覆蓋 recursive `add_node_to_group` baseline。
   - 新增 iterative branchless variant，用同一 snapshot 跑 bench/checksum。
   - 若 iterative branchless 沒贏，保留 recursive，僅採用 `set_node_state` / `read_bits` / fast path branchless。

3. Rust memory

   - 實作 aligned allocation 時，要維持 element stride，不要讓 `NodeHot` 或 `u8` 每個元素 64-byte 對齊。
   - local benchmark 可用 `RUSTFLAGS="-C target-cpu=native"`，但 release artifact 若需跨機器執行，不應硬綁 native。

## 建議驗證矩陣

每個變體至少記錄：

- C# hc/s。
- Rust hc/s。
- fixed half-cycle count。
- `NodeStates checksum`。
- pure-logic fast path count。
- `TransistorList` length / node count。
- 是否同一 ROM / snapshot / lowering 設定。

建議測試順序：

| 順序 | 變體 | 預期風險 | 預期收益 |
|---|---|---:|---:|
| 1 | `ReadBits` branchless | 低 | 小到中 |
| 2 | `SetNodeState` loop unswitch | 低 | 中 |
| 3 | supply hash shield + branchless enqueue | 中 | 中 |
| 4 | `RecalcNodeFast` OR-all | 低到中 | 小到中 |
| 5 | C# `AddNodeToGroup` branchless enqueue | 中到高 | 中到高 |
| 6 | Rust iterative branchless BFS variant | 高 | 不確定 |
| 7 | Rust aligned allocation | 中 | 小到中 |
| 8 | clock-phase partitioning | 中到高 | 高，但需正確 clock 分析 |
| 9 | bitset mirror for long lists | 高 | 只在長 list 有機會 |

## 最終建議

短期最務實的路線是先不要碰 IR，也不要先做大型圖分割。先從「每次狀態變更都會走到」的 `SetNodeState` 與「handler bit read」下手，因為這兩者風險低、可單點 A/B、也同時適用 C# 與 Rust。

Branchless BFS 是有潛力的，但必須加上 sentinel capacity、防止不必要 `NodeInfo` 讀取，且 Rust 版要尊重現有 recursive baseline 的實測結果。全面 OR-all、全面 branchless、全面 prefetch 都不應一次套上去，應依 list length、gate high probability、cache locality 做局部策略。

非 IR 的下一個大方向是 AOT 資料重排：clock-phase partitioning、gate probability sorting、cache-line-aware renumbering。這些能減少 BFS 真的要掃的 edge 與 cache miss，比單純把所有 `if` 改成位元運算更可能突破目前 64K-67K hc/s 的平台。
