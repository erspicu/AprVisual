# Rust S1 hotpath 效能改善建議

日期: 2026-05-29  
範圍: `C:\ai_project\AprVisual\experiment\rust-s1\src\*.rs`  
目標: 分析 C# S1 port 到 Rust 的版本是否還有 hotpath 改善策略。  
限制: 不考慮 IR / codegen / levelize / oblivious / chip parallel 等架構型路線。

## 目前基準

專案:

- `Cargo.toml`: release 已開 `lto = "fat"`、`codegen-units = 1`、`opt-level = 3`。
- 核心檔案: `src/wire.rs`
- snapshot loader: `src/snapshot.rs`
- runner: `src/main.rs`

已驗證:

```powershell
cargo build --release
target\release\wire_s1.exe bench C:\ai_project\AprVisual\experiment\rust-poc\snapshot\full_palette.aprsnap 200000
```

結果:

- build 成功。
- `full_palette.aprsnap 200000`: `57,979 hc/s`, checksum `0x9B103E5E206E4C37`。

注意: Rust 從 snapshot 起跑，所以 `t=200000`；C# full system reset 後會有 reset half-cycle offset，但 checksum 可以作 A/B 等價基準。

## 現有 hotpath

主要路徑:

`step_cycle()` -> `set_high/set_low()` -> `process_queue()` -> `recalc_node()` -> `recalc_node_fast()` 或 `compute_node_group()/add_node_to_group()` -> `set_node_state()` -> `invoke_callbacks()/run_mem_handler()` -> `video_pixel_write_if_rising_edge()`

Rust 端已同步的 C# 有效項目:

- `compute_node_group()`: ForceCompute mask 已依賴 snapshot 的 `flags_to_state` LUT，不在 runtime 做。
- `compute_node_group()`: floating tie-break 才讀 `connections`。
- `set_node_state()`: c1 已 inline enqueue，跳過 supply check。
- `target_to_handler`: callback target node id 直接映射 handler index，比 C# managed `Node.Callback` 路徑更直接。

不建議重開:

- iterative BFS: `Cargo.toml` 註解已記錄 C# iterative BFS 在 Rust 端曾是負效益，保留 recursive walk。
- GND/PWR scan skip: C# 已兩次實測負效益，Rust 端也不應先做。
- clock direct fast path: Rust 已直接在 `step_cycle` toggle clock，不是 C# delegate path。

## 建議優先順序

### P0. 拆分 `NodeInfo` hot/cold 欄位

位置: `src/snapshot.rs:25-35`、`src/wire.rs:35-37`、`src/wire.rs:238-350`

目前 Rust 的 `NodeInfo` 是:

```rust
pub struct NodeInfo {
    pub flags: u8,
    pub _pad: [u8; 3],
    pub connections: i32,
    pub tlist_gates: i32,
    pub tlist_c1c2s: i32,
    pub tlist_c1gnd: i32,
    pub tlist_c1pwr: i32,
}
```

這是 24 bytes，且 `add_node_to_group()` 每次 group visit 會 `let ni = self.node_infos[u];` 複製整個 struct。BFS 熱路徑只需要 `flags + tlist_c1c2s/c1gnd/c1pwr`；`connections` 只有 floating tie-break 用，`tlist_gates` 只有 `set_node_state()` 寫回 fanout 用。

C# 端目前已把 cold 欄位拆出:

- hot `NodeInfo`: `Flags + TlistC1c2s + TlistC1gnd + TlistC1pwr`，16 bytes。
- cold arrays: `NodeConnections`、`NodeTlistGates`。

建議 Rust 也在 `WireCore::from_snapshot()` 轉換成:

```rust
#[repr(C)]
#[derive(Clone, Copy)]
struct NodeHot {
    flags: u8,
    _pad: [u8; 3],
    tlist_c1c2s: i32,
    tlist_c1gnd: i32,
    tlist_c1pwr: i32,
}

node_hot: Vec<NodeHot>,
node_connections: Vec<i32>,
node_tlist_gates: Vec<i32>,
```

snapshot format 不必改；loader 仍可先讀舊 `NodeInfo`，再 split 到 `WireCore` 欄位。

預期收益: 高。這會直接降低 BFS 讀取與 copy 的 hot working set，也讓 Rust port 更接近 C# 現狀。  
風險: 中。改動面大，但語意單純。  
驗證: checksum 必須維持 `0x9B103E5E206E4C37`。

### P0. `invoke_callbacks()` 不要用 `mem::take` 丟掉 pending Vec capacity

位置: `src/wire.rs:415-424`

目前:

```rust
while !self.pending_handlers.is_empty() {
    let pending: Vec<i32> = std::mem::take(&mut self.pending_handlers);
    for hi in pending { ... }
}
```

`mem::take` 會把 `pending_handlers` 換成新的 empty Vec；原本有 capacity 的 Vec 變成 local `pending`，迴圈結束 drop。這會讓下一輪 pending push 重新配置，等於 callback path 仍可能有配置/釋放。

C# 端採用的是 swap-and-drain 兩個 list，零配置 snapshot。Rust 也應同樣做:

```rust
pending_handlers: Vec<i32>,
processing_handlers: Vec<i32>,
```

`invoke_callbacks()`:

```rust
while !self.pending_handlers.is_empty() {
    std::mem::swap(&mut self.pending_handlers, &mut self.processing_handlers);
    for i in 0..self.processing_handlers.len() {
        let hi = self.processing_handlers[i];
        ...
    }
    self.processing_handlers.clear();
}
```

預期收益: 中。memory/video callback 會反覆進入；避免 allocator 參與 hotpath。  
風險: 低。保留 re-entrant callback 行為，新 pending 會落在 swapped 後的 empty pending list。

### P1. 對最內層 transistor/node array access 使用受控 `get_unchecked`

位置:

- `src/wire.rs:143-167` `recalc_node_fast`
- `src/wire.rs:238-283` `add_node_to_group`
- `src/wire.rs:310-350` `set_node_state`
- `src/wire.rs:384-413` `process_queue`
- `src/wire.rs:426-461` `run_mem_handler`

Rust 目前只在部分 queue hash/list 操作用 `unsafe get_unchecked`。但幾個最熱掃描仍有 safe indexing:

- `self.transistor_list[p]`
- `self.transistor_list[p + 1]`
- `self.node_states[gate as usize]`
- `self.node_infos[u]`

這些索引大多來自 snapshot 的 flattened adjacency；LLVM 不一定能證明 gate id 一定在 `node_states` 範圍內，因此可能保留 bounds checks。C# 端是 unsafe pointer，沒有這層成本。

建議:

- 在 snapshot load 後加一個 debug/diagnostic validation，確認所有 node id / tlist index 合法。
- hot loop 內改用小 helper 或局部 unsafe:
  - `*self.transistor_list.get_unchecked(p)`
  - `*self.node_states.get_unchecked(gate as usize)`
  - `*self.node_hot.get_unchecked(u)`
- 不要一次全專案改；先從 `add_node_to_group()` 與 `recalc_node_fast()` 開始。

預期收益: 中到高，取決於 LLVM 目前消掉了多少 checks。  
風險: 中。snapshot 若壞掉會 UB；需要 debug validation 和 checksum A/B。

### P1. `group_buf` 改成 `Vec<u16>`

位置: `src/wire.rs:48-52`、`src/wire.rs:121-123`、`src/wire.rs:238-307`、`src/wire.rs:360-380`

Rust 目前:

```rust
pub group_buf: Vec<i32>
```

node id 已經限制在 `< 65K`，`transistor_list` 也已用 `u16`。C# 的 `_groupBuf` 已是 `ushort*`。`group_buf` 是每個 group walk 都會寫入、清除、再讀回的 scratch buffer，改成 `Vec<u16>` 可把 footprint 從約 58KB 降到 29KB。

建議:

- `group_buf: Vec<u16>`
- push 時 `self.group_buf[gc] = nn as u16`
- 讀取時 `let nn = self.group_buf[i] as usize` 或 `as i32`

預期收益: 中。尤其在 group clear / writeback / callback scan 反覆讀寫時有 cache 效益。  
風險: 低到中。要 assert `node_count <= u16::MAX as usize`。

### P1. memory handler 分 ROM/RAM 專用路徑並預存 mask

位置: `src/snapshot.rs:37-45`、`src/wire.rs:426-461`

目前每次 `run_mem_handler()` 都做:

- `let is_rom = self.handlers[h].is_rom`
- `let writing = !is_rom && we >= 0 && node_states[we] == 0`
- `let mask = self.memories[mem_idx].len() - 1`
- 透過 `self.handlers[h].addr[i]` / `data_out[i]` 重複索引

建議:

- 在 `from_snapshot()` 對 handler 預先算好:
  - `kind: RomRead | RamRw | RamReadOnly`
  - `mem_mask: usize`
- `run_mem_handler()` 第一層按 kind 分三條專用路徑:
  - ROM: selected 後讀 address -> `v = mem[address & mask]` -> drive data_out
  - RAM RW: selected 後判斷 `/we`
  - RAM read-only: 不判斷 `/we`
- address/data_out 可以先 borrow 成 slice 做讀取；輸出 loop 因為會呼叫 `set_high_queued/set_low_queued`，可用 index 或後續 flat-array 設計。

預期收益: 小到中。memory callback 是常見熱點，但不如 BFS 熱。  
風險: 低。

### P1. 需要從 snapshot/exporter 端移除 ROM data-bus trigger

位置: Rust 端會受益，但觸發源在 snapshot 生成端。

C# follow-up 已指出 ROM handler 不應監看 data bus。Rust snapshot 的 callback fake transistors 已經在 snapshot 中，因此 Rust `src` 本身無法單方面移除這些 triggers；必須在 C# SnapshotExporter 或 snapshot 生成前的 C# handler attachment 修正，重新輸出 `.aprsnap`。

建議:

- ROM handler trigger 只保留 `cs + addr`。
- RAM handler 才保留 data bus trigger。
- 重新輸出 snapshot 後用 Rust bench A/B。

預期收益: 中。減少 ROM callback 喚醒，也會減少 snapshot 中 callback fake transistor 帶來的 propagation 工作。  
風險: 低到中。需確認 ROM output 不依賴 data bus 變化重送。

### P2. handler node lists 改成 fixed/small bus 結構

位置: `src/snapshot.rs:37-45`、`src/wire.rs:426-461`

`MemHandlerSpec` 目前每個 handler 有兩個 `Vec<i32>`:

```rust
addr: Vec<i32>,
data_out: Vec<i32>,
```

實際寬度通常是 address 13/15/16 bit，data 8 bit。可改為 fixed small struct，避免 Vec pointer chasing 與 len/index metadata:

```rust
struct Bus16 { len: u8, nodes: [u16; 16] }
struct Bus8  { len: u8, nodes: [u16; 8] }
```

或把所有 handler buses flatten 到一個 `Vec<u16>`，handler 存 offset/len。

預期收益: 小到中。比 memory handler specialization 更侵入。  
風險: 中。snapshot loader 轉換與所有 callsite 要一起改。

### P2. video node lists 改成 fixed arrays / unrolled readers

位置: `src/wire.rs:169-193`

`video_pixel_write_if_rising_edge()` 對 hpos/vpos/pal_ptr/pal_ram 都走通用 `read_bits(&[i32])`。寬度是固定格式:

- hpos: 9 bits
- vpos: 9 bits
- palette pointer: 5 bits
- palette RAM entry: 6 bits

建議:

- snapshot load 後把 video nodes 轉成 `[u16; 9]`、`[u16; 5]`、`[[u16; 6]; 32]`，並記錄 missing entry mask。
- 寫專用 `read9/read5/read6`，必要時手動 unroll。

預期收益: 小。只影響 pclk1 rising edge pixel write，不是每個 propagation node。  
風險: 低到中。

### P2. `set_node_state()` 依 `new_state` 拆 loop

位置: `src/wire.rs:310-350`

目前 fanout loop 內每 pair 都測:

```rust
if new_state == 0 && c2 != npwr && c2 != ngnd { ... }
```

`new_state` 在整個 call 內固定。可拆成 high loop / low loop，high loop 只 enqueue c1，low loop enqueue c1+c2。

預期收益: 小到中。  
風險: 中。Rust/C# 都有 JIT/LLVM code shape 風險，必須單獨 A/B。

### P3. release 設定可測 `target-cpu=native` 與 `panic=abort`

位置: `Cargo.toml` / `.cargo/config.toml`

目前 release profile 已經很強，但仍可測:

```toml
[profile.release]
panic = "abort"
strip = "symbols"
```

以及本機 benchmark 用:

```powershell
$env:RUSTFLAGS="-C target-cpu=native"
cargo build --release
```

`target-cpu=native` 可能讓 LLVM 對 branch/layout/register allocation 做更貼近本機 CPU 的選擇，但會降低 binary 可攜性。這不是程式碼策略，應獨立量測。

預期收益: 不確定，小到中。  
風險: 低；主要是可攜性。

## 不建議優先處理

- `snapshot.rs` loader 的 allocation / `skip()` 配置: load-time，不是 half-cycle hotpath。
- `main.rs` args collect / PNG encode: bench/shot 外圍，不是 simulation hotpath。
- recursive -> iterative group walk: Cargo 註解已記錄 Rust 端負效益。
- per-chip parallel / bit-parallel BFS / prune-merge / LUT-TTL: 既有 dead-end，不重開。

## 建議實作順序

1. `invoke_callbacks()` swap-and-drain，最小改動，先確認是否有可見收益。
2. `NodeInfo` hot/cold split，這是最像 C# 成功路線且 Rust 目前尚未真正同步的結構性改善。
3. 對 `add_node_to_group()` / `recalc_node_fast()` 加受控 unchecked access。
4. `group_buf: Vec<u16>`。
5. memory handler specialization + mask precompute。
6. 其餘 fixed bus / video unroll / split loop 只在 profiler 顯示對應區塊仍熱時做。

每一步都單獨跑:

```powershell
cargo build --release
target\release\wire_s1.exe bench C:\ai_project\AprVisual\experiment\rust-poc\snapshot\full_palette.aprsnap 200000
```

驗收:

- checksum 必須維持 `0x9B103E5E206E4C37`。
- 建議至少 5-run median；Rust 的單次數字容易受 CPU thermal / background task 影響。
