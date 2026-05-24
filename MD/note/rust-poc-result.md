# Rust PoC — 1.37-1.39× over C# baseline, bit-identical

> 日期:2026-05-25
> Branch:`aot-codegen`
> 路徑:`experiment/rust-poc/`

## TL;DR

把 `WireCore.Recalc.cs` + `WireCore.Group.cs` + `WireCore.Handlers.cs` 的 settle/group/memory-handler 核心 port 成 Rust(~600 行),C# 同時加 `--export-snapshot` CLI dump 完整 runtime state(post-LoadSystem)成 binary blob。 Rust 載 blob 跑 bench-hc。

**結果**(aot-codegen HEAD,bench-hc 50K,×2 trial,均跑 baseline 設定無任何 flag):

| ROM | C# baseline hc/s | Rust port hc/s | Speedup | Checksum 一致 |
|---|---|---|---|---|
| `01-basics.nes` | 36,578 | 50,433 | **1.379×** | ✅ `0x54C4D6220C6C569E` |
| `full_palette.nes` | 37,160 | 51,780 | **1.393×** | ✅ `0x933ABE7915AC18BE` |

Rust 三 trial 雜訊 ±0.5%(50,403 / 50,433 / 50,497 hc/s),Ratio 穩在 **~1.37-1.39×**。

## 跟先前估計對比

PoC 前我估 1.1-1.3×。 實測 1.37-1.39× ── 比預期樂觀 ~10%。

可能原因:
- LLVM 的 aliasing analysis + autovectorization 對這種 byte/int pointer-chase pattern 比 RyuJIT 緊
- LTO + codegen-units=1 讓 cross-function inline 程度高
- Recursive `add_node_to_group` 的 ABI 開銷少
- 24-byte NodeInfo packed 對 Rust 跟 C# 都一樣,所以非這條

## 範圍

**Rust port 已 cover:**
- ✅ Recalc(`process_queue` + `recalc_node`)
- ✅ Group(`add_node_to_group` + `compute_node_group` + flags-to-state LUT)
- ✅ SetNodeState + transistor-list propagation
- ✅ Memory handler dispatch(RAM/ROM via fake-target-node callback mechanism)
- ✅ Clock toggle / Step(N)

**沒 port(也不需要為這次比較)**:
- ❌ Fast-path(`--fast-path`)── 但是 bit-identical,加上應該 ~1.04× 再乘 1.39 = 1.45×
- ❌ Prune-merge(`--prune-merge` topology-group fix)── 跟 fast-path 同樣可以加,共同 ~1.42-1.46×
- ❌ IR / codegen dispatcher / fast-path / levelize
- ❌ Video handler(框架沒實作,bit-identical checksum 已足以驗等價性)
- ❌ `.js` 模組解析 / module composer ── **永遠不會**port,改用 C# 出的 snapshot

## Snapshot binary format

由 `src/AprVisual/Test/SnapshotExporter.cs` 寫,`experiment/rust-poc/src/snapshot.rs` 讀。

```
Magic "APRSNAP\0" (8 bytes)
Version uint32 (=1)
NodeCount i32, TransistorListLen i32, Npwr i32, Ngnd i32
ClockNode i32, ResetNode i32, PpuVblankNode i32
NodeStates byte[NodeCount]
NodeInfos: per node 24 bytes (i32 Flags, i32 Connections, 4× i32 Tlist*)
TransistorList i32[TLen]
FlagsToState byte[256]
NumMemories i32
For each Memory:
  NameLen i32, Name UTF-8 bytes, Size i32, Data bytes
NumHandlers i32
For each MemHandler:
  IsRom u8, MemoryIndex i32, Cs i32, We i32, Target i32
  AddrLen i32, Addr i32[AddrLen]
  DataOutLen i32, DataOut i32[DataOutLen]
```

Little-endian, 全 packed,沒 padding。

## 一個 debug pitfall(記下來避免重撞)

C# `NodeFlags` enum 的 bit 位置是:
- `State = 1<<0`, `PullUp = 1<<1`, `SetHigh = 1<<2`, `SetLow = 1<<3`,
- `Pwr = 1<<4`, `Gnd = 1<<5`, `ForceCompute = 1<<6`, `HasCallback = 1<<7`

Rust port 必須**完全 match** ── `FlagsToState` LUT 是用這個 layout build 的,256-entry indexed by `(flags >> shifts)`,不能換 bit 位置。 我第一版用了直覺的「Gnd=0, Pwr=1, ...」順序,checksum 完全錯,跑出 47M hc/s 的不可能數字(等同 simulation 完全沒跑)。

## 工程成本回顧

- C# `SnapshotExporter.cs`(~150 行)+ 1 個 `FindCallbackTargetByName` helper:**2 hr**
- Rust `snapshot.rs`(loader, ~140 行):**1 hr**
- Rust `wire.rs`(core port, ~250 行):**2 hr**
- Rust `main.rs`(bench harness, ~30 行):**15 min**
- Debug flag bit-position 問題:**30 min**

**總計 ~6 小時**(原估 1 天 = 8 小時,提前完成)。

## 對「該不該全 port S1 到 Rust」的判斷

1.37-1.39× 是 real,而且**完全 bit-identical**(無 correctness 風險)。

**做完整 port 的收益:**
- 多 ~37% 速度,可拉到 ~50K hc/s baseline,加 fast-path 拉 ~52K hc/s
- LLVM AOT,沒 GC noise,更可預測 latency
- 跨 platform(Linux/Mac/Web/Android 都能跑)
- 工程訓練價值

**做完整 port 的成本:**
- Recalc / Group 已 port = 簡單部份完了
- 還沒 port 的:`.js` parser(~1500 行)、 video handler(~150 行)、 ResetNes flow / SetHigh/SetLow flags 細節、IR / fast-path / prune-merge(可選)
- WinForms / GDI rendering 要換(`winit + softbuffer` 或 `pixels` crate,~500 行)
- 總計 ~3-5 整天

**判斷**:1.37× 不夠戲劇性突破 real-time gap(那是 ~840×),但**作為 packaging value 跟 cross-platform value 是值得的**,而且 1.37 比 fast-path 的 1.04 大很多,**比任何 safe C# 算法優化都猛**。 如果未來要把 S1 開源 release,Rust port 是合理選項。

**但如果只是要再榨 perf 上限**:port 完整 Rust 還是不夠,要的是 IR + codegen + GPU paradigm(main 已做過)── 那條路有 ~10×-100× 量級空間,而 Rust port 只有 ~1.4×。

## 跑法

```powershell
# 一次 export(C# 端跑,要選 ROM)
dotnet run -c Release --project src/AprVisual -- `
  --benchmark <rom.nes> `
  --export-snapshot experiment/rust-poc/snapshot/<name>.aprsnap `
  --system-def-dir ref/metalnes-main/data/system-def

# Rust bench
cd experiment/rust-poc
cargo build --release
./target/release/wire_realbench.exe snapshot/<name>.aprsnap 50000
```

兩邊的 NodeStates checksum 必須一致 ── 不一致就是 simulation diverge,結果無效。
