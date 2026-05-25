# Rust port 效能總結

> 日期:2026-05-26
> 量測對象:`experiment/rust-poc/wire_realbench`(Rust 1.85+,LTO=fat,codegen-units=1,opt-level=3)
> Snapshot:`experiment/rust-poc/snapshot/full_palette.aprsnap`(v4 format,由 C# 端 `--export-snapshot` 產出)
> Bench 模式:`bench <snapshot> <hc_count>`,500K master half-cycles,best-of-3

## TL;DR

**Rust port 單實例 CPU 峰值:48,622 hc/s**(LUT snapshot + `--fast-path`)。

比 C# S1 baseline(37,795 hc/s)快約 **29%**。 仍距 NES real-time(840× = 17.8M hc/s)約 **365 倍**,**單實例 CPU 上跑 real-time 不可達** ── 跟 [memory `s4-route-single-instance`](../../C--ai-project-AprVisual/memory/s4-route-single-instance.md)結論一致。

## 完整數據

`full_palette.nes` 500K hc,best-of-3:

| Config | hc/s | μs/hc | vs baseline | Checksum |
|---|---|---|---|---|
| baseline | 47,761 | 20.93 | — | `0x2682907736914E31` |
| `--fast-path` | **48,589** | 20.58 | +1.7% | 同 ✓ |
| `--prune-merge` | 44,552 | 22.45 | **-6.7%** | 同 ✓ |
| `--fast-path --prune-merge` | 45,775 | 21.84 | -4.2% | 同 ✓ |
| LUT snap + `--fast-path` | **48,622** | 20.57 | +1.8%(peak) | `0xEECDAD3EFCCCBD49`(LUT 自己 baseline) |

所有設定 bit-identical 跟 baseline checksum 一致(LUT 例外因為 snapshot 不同 ── 但 LUT 本身在 C# 也有 bit-identical 驗證)。

## 各設定討論

### 1. `--fast-path`(+1.7%,小贏)

Per-node O(1) RecalcNode for pure-logic-gnd nodes,跳過 group DFS。 移植自 C# 端 math-algos 策略二。 大概 28% 節點被分類為 pure-logic(symmetric pull-down 結構)。

Rust 上的收益小是因為 baseline 已經很快;C# 上同設定 +6% 左右(C# group walk 較慢,O(1) 旁路效益顯著)。

### 2. `--prune-merge`(**-6.7%,反向**!)

這是 surprise。 C# 上 prune-merge 給 1.37× speedup(memory `[[math-algos-branch-charter]]` Phase 1 結果);Rust 上反而 **慢 6.7%**。

**推測原因**:Rust 的 group walk + SetNodeState 本來就接近 metal speed,維護 NodeGroupIDs 每次 walk 都要 ratify(`_nextGroupID++` + per-member assignment)的相對 overhead 比例變大,超過 skip 的省下。

C# 跟 Rust 在 prune-merge 上的反向結果,提醒一個普遍原則:**演算法 ROI 隨平台基準速度反向變化** ── baseline 越快,追加複雜性的 break-even 點越高。

### 3. `--fast-path --prune-merge`(-4.2%,fp+pm 合用比純 fp 還差)

Combined 更糟,因為 fast-path 已經把 pure-logic node 拉出 group DFS 範圍 ── pm 能 skip 的 case 已經被 fp 處理掉了,剩下的 pm overhead 純 overhead。

不建議 Rust 默認加 pm,無論單獨或跟 fp 合用。

### 4. LUT snapshot(+0.07%,微邊際)

`full_palette_lut.aprsnap` 把 74HC04 / 74LS368 替換為 behavioral LUT callback(74LS139 留 transistor 級,LUT 化會 render 黑)。 chip-diag 顯示 TTL 整體 ~0.6% group-member work,LUT 化頂多省這 0.6% 的一部分。

實測 LUT + fp = 48,622 vs baseline snap + fp = 48,589 → **+0.07%**。 邊際內。

不建議為了 0.07% 維護兩份 snapshot。 future:如果 LUT framework 有其他用途(e.g. multi-instance bit-sliced)再保留。

### 5. `--parallel`(per-chip 平行,**15× 慢**)

memory `[[per-chip-parallel-dead-end]]` 已記:rayon::join 同步開銷 10.5µs/call,per-wave work too small。 未在本次量測重跑,結果與 2026-05-25 一致。

## 跟 C# / Real-time 對照

| Sim | 設定 | hc/s | 相對 baseline | 距 real-time(17.8M hc/s)|
|---|---|---|---|---|
| C# S1 | baseline | 37,795 | 1.00× | **470×** 慢 |
| C# S1 | --fast-path | ~40K | ~1.06× | 445× |
| **Rust port** | --fast-path | **48,589** | **1.29×** | **366×** |
| **Rust port** | LUT + --fast-path | **48,622** | **1.29×** | **366×** |
| (target for real-time)| — | 17,800,000 | 471× | 1.0× |

要 real-time 還差 ~366×。 已實證(memory):
- AOT batch backends(C#-JIT / LLVM-MCJIT / GPU-D3D11)── **3-6× 慢於 S1**,batch redundancy 算法上不可超越
- event-driven β(cpu-opt branch)── **capped 2-3×**,還差 100×+
- per-chip parallel ── **15× 慢**,sync 開銷
- bit-parallel BFS ── **156× 慢**,小 walk 拖死

**結論**:single-instance real-time 此 pipeline 上不可達。 兩條合理路徑(都不該由這 repo 做):
1. 不同算法路徑(behavioral / cycle-accurate emulator,不是 switch-level)── 需要重新建模,non-goal
2. Multi-instance bit-sliced GPU(S4 已完成 PoC,64× lane parallelism 攤平 batch redundancy)── 適合「同時跑很多 NES」use case

## 量測重現

```powershell
cd C:/ai_project/AprVisual/experiment/rust-poc
cargo build --release --bin wire_realbench
./target/release/wire_realbench.exe bench `
    ./snapshot/full_palette.aprsnap 500000 --fast-path
```

預期輸出:
```
# bench-hc: (rust port) — 500000 master half-cycles
# simulated: 500000 master half-cycles in 10.3 s
# rate: ~48,500 hc/s (~20.6 µs/hc)
# NodeStates checksum @ t=500000: 0x2682907736914E31  (A/B equivalence: must match the C# baseline run)
```

## 已知限制

- SMB snapshot(`smb.aprsnap`)為 v3 格式,新 Rust loader 要 v4 ── 跑 SMB bench 需要先重 export(C# `--export-snapshot smb.nes`)
- bench 不算 power-on reset 時間(snapshot 已是 settled state)
- chip-diag 在 Rust 端只在 `--parallel` 路徑會計數,序列 path 不收集
