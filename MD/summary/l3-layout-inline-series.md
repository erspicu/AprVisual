# L3 layout + inline 系列 — 微架構壓榨成果

> 日期:2026-05-26 ~ 2026-05-28
> Branch:`aot-codegen`
> 範圍:WireCore L3 hot data layout 緊密化 + hot method JIT/LLVM 強制 inline
> 工程量:10 commits、~70 行 code(declaration / cast / annotations)、0 algorithm 變動
> 結果:**C# +22.4% best / +23.5% avg(37,928 → 46,413 hc/s)、Rust +7.5% best(48,622 → 52,249 hc/s)**

## TL;DR

從 `s4-route-single-instance` wind-down 時的 ~37K hc/s C# baseline 出發,**完全不改演算法**,只透過:
1. 縮型(int → byte/ushort 在 random-access + cache-pressure 場景)
2. SoA 拆分 NodeInfo 冷 field
3. `[MethodImpl(AggressiveInlining)]` 對 hot method

達成 **C# +23.5% avg / Rust +7.5%**。 整路 bit-identical(NodeStates checksum + framebuffer PNG SHA256 全 match)。 NES real-time 仍 ~340× 遠 ── memory `[[s4-route-single-instance]]` 結論不變,但這 chunk gain 是純技術 micro-opt,值得記錄。

## 起源

session 開始時的 baseline(per memory `rust-port-best-config` 早期):
- C# S1:**~37,928 hc/s** best
- Rust port:**~48,622 hc/s** best

問題:single-instance CPU 限制感覺已 saturated,還有沒有空間?

切入點:**逐個分析 hot path 的 memory access pattern**(`--dead-end-diag` + `--count-events` 量化每個 method 的 call frequency),找出 cache miss 跟 function call overhead 的真實貢獻。

## 系列 commit history

### C# `aot-codegen`(9 commits)

| # | Commit | 變動 | Best (hc/s) | Avg | Δ avg |
|---|---|---|---|---|---|
| 0 | (origin) | — | 37,928 | 36,923 | — |
| 1 | `e6824e5` | `_inGroup` int→byte(58KB→14KB)| 38,534 | 38,018 | +3.0% |
| 2 | `35baec8` | `_groupBuf` int→ushort | 38,591 | 37,874 | +2.6% |
| 3 | `ed3e637` | `TransistorList` int→ushort | 39,056 | 38,587 | +4.5% |
| 4 | `d7111f1` | NodeInfo SoA split | 39,145 | 38,811 | +5.1% |
| 5 | `ca49aa7` | `RecalcHash` int→byte | 39,362 | 39,011 | +5.7% |
| 6 | `4657199` | inline `EnqueueNode` + `GetNodeValue` | 40,652 | 39,253 | +6.3% |
| 7 | `eb89044` | inline `SetNodeState` | 42,271 | 41,529 | +12.5% |
| 8 | `a929ae0` | inline `ComputeNodeGroup` + `RecalcNodeFast` + `RecalcNode` | **46,413** | **45,616** | **+23.5%** |

### Rust port(2 commits)

| # | Commit | 變動 | Best (hc/s) | Δ |
|---|---|---|---|---|
| — | (origin) | — | 48,622 | — |
| 1 | `a357cb2` | `transistor_list` i32→u16 | 50,306 | +3.5% |
| 2 | `af5d4f0` | `#[inline(always)]` × 4 hot method | **52,249** | **+7.5%** |

## 量化原則 — 三條 rule

從這系列的 12 個變動實驗(含失敗 revert 的)歸納:

### Rule 1:**Random-access + cache-pressure 才該縮型**

| 變動 | Access pattern | 結果 |
|---|---|---|
| `_inGroup int→byte`(dedup bitmap)| 隨機 by node ID | ✓ +3.0% |
| `RecalcHash int→byte`(dedup)| 隨機 by node ID | ✓ +0.5% |
| `TransistorList int→ushort`(178K entries)| 隨機 by sub-list offset | ✓ +1.9% |
| `_groupBuf int→ushort` | **順序 write→順序 read** | × noise(prefetcher 已解)|

`_groupBuf` 即使縮小 50%(58→29KB)也 0 收益,因為已是 cache-friendly sequential pattern。 縮型不是普遍解,要看 access pattern。

### Rule 2:**Bitset(ulong + shift/mask)只在 array 已大到 L1d 塞不下才贏**

實測:`RecalcHash int→ulong` bitset 版(58KB×2 → 1.8KB×2)── **shift/mask 開銷蓋過 cache benefit,avg -1.1%**。 直接縮 byte(14KB)是甜蜜點。

### Rule 3:**JIT/LLVM 對 hot method 的 inlining 是「nested cascade」**

最大單次 gain 是 commit `a929ae0`:同時 inline `ComputeNodeGroup` + `RecalcNode`,得到 **+9.8% avg**。 為何這麼大?── 那時 SetNodeState、EnqueueNode、GetNodeValue 都已 inline。 加上後兩個,整條 `ProcessQueueInterp → RecalcNode → ComputeNodeGroup → AddNodeToGroup(recursive,唯一不能 inline)→ SetNodeState → EnqueueNode` chain 被 JIT 壓成一個 straight-line block,**每 dirty node 30M 次/50K hc 的 function call overhead 全消除**。

關鍵:**inlining 是累加的、不是線性的**。 單獨 inline 一個 hot method 的 gain 通常 +0.5-3%,但全 chain inline 後 cascade 倍增。

## 失敗的嘗試

### iterative BFS 取代 recursive `AddNodeToGroup`(-1.7% revert)

Gemini 建議改 BFS 消除 recursion overhead。 實作後 bit-identical 但 **慢 1.7%**:
- recursion 在現代 CPU 上很便宜(return-address stack predictor)
- DFS → BFS 改變 cache locality:DFS 走 channel 連續鄰居,BFS 跳一層才回,**cache miss 增加**
- inlined `PushNode` 4 個 conditional branch 在 2 個 call site 重複,branch prediction 分散

revert。 教訓:**消除 function call 不一定贏 cache locality 損失**。

### 手動 inline EnqueueNode + 消除 c2 重複檢查(-0.5% revert)

Gemini r6 建議。 預期 +0.5-2%,實測 **-0.5% avg**。 .NET 10 JIT 已比文章假設聰明 ── `[AggressiveInlining]` + constant folding `CountEvents=false` + commoning Npwr/Ngnd 比較全自動。 手動 inline 反而增加 instruction footprint。

revert。 教訓:**JIT 已做得很好,手動寫 inline 反而抑制 JIT 自己的優化機會**。

### Bit-pack 4 個 state array 進 NodeInfo.Flags(未試)

Gemini r6 建議。 前提錯誤:那 4 個 array(`CodegenOwned`、`IrClass`、`IsPureLogic`、`DeadEndSkippable`)的讀取都 gate 在對應 enable flag 後,default config 下根本不讀。 不是 hot path,bit-pack 沒收益。

跳過。

### 函數指標消除 `if (EnableXxx)` 分支(未試)

Gemini r6 建議。 `static bool` flag 永遠 false 的 branch predictor 命中率 ~100%,well-predicted false 1-2 cycle 全帶。 改 `delegate*<int, void>` 反而:indirect call 預測難、組合爆炸、失去 runtime A/B。

跳過。

## 對齊比較數字(2026-05-28 終態)

| Mode | C# best | C# avg | Rust best | Rust avg | Rust > C# |
|---|---|---|---|---|---|
| default | 46,135 | 45,786 | 50,686 | 50,573 | **+10.5%** |
| + `--fast-path` | 46,517 | 45,476 | 52,231 | 51,404 | **+12.2%** |

### 副作用:C# fast-path 效益消失

inline 系列之前,C# `--fast-path` 給 +2.9%。 現在 +0.8% best / -0.7% avg ── **埋沒在 noise 內**。 推論:當所有 hot method inline 後,group walk 路徑已跟 fast-path 一樣便宜。 fast-path 的 `IsPureLogic[nn]` 額外 array read 反而沒省到。

Rust 仍從 fast-path 拿 +1.6%(LLVM 對 group walk 的 inlining 不像 .NET JIT 那麼激進)。

未來可考慮:C# 上預設關 `--fast-path`,或重新評估其維護成本。

## Real-time 距離

- 當前 best:Rust 52,249 hc/s
- NES real-time:17.8M hc/s
- 距離:**~341×**

跟 inline 系列之前(~366×)比,gap 又收窄了一點,但 single-instance CPU 上仍**遠遠不可達 real-time**。 memory `[[s4-route-single-instance]]` 的結論不變:此 pipeline 上 real-time 需要算法改變或 multi-instance bit-sliced GPU(S4 已實作)。

## 工程價值

純 micro-architectural 優化,**0 algorithm 變動、~70 行 code、整路 bit-identical**:
- NodeStates checksum `0x933ABE7915AC18BE`(C#)/ `0x2682907736914E31`(Rust)整個系列 match
- full_palette.nes frame 50 screenshot SHA256 `F80D24888DF4614EB729BB6575607C49C5A00FD29CD85120E1AABA3B42C65C92` ── 系列前後完全相同
- 全部 commit 都用 5-runs A/B verify
- 一次失敗(iterative BFS 跟 manual inline)立刻 revert,不混進 main perf trajectory

每個 commit 都可獨立評估、可獨立 revert。 適合作 future micro-arch 優化的範本。

## Bench 重現

```powershell
# C#
src/AprVisual/bin/x64/Release/net10.0-windows/AprVisual.exe `
    --benchmark <rom.nes> --bench-hc 50000 `
    --system-def-dir ref/metalnes-main/data/system-def

# Rust
experiment/rust-poc/target/release/wire_realbench.exe `
    bench experiment/rust-poc/snapshot/full_palette.aprsnap 500000 --fast-path
```

預期數字(這台機器,2026-05-28):
- C#:~46K hc/s best
- Rust:~52K hc/s best

## 相關文件

- [Rust port performance](rust-port-performance.md) ── 2026-05-26 寫的 Rust 早期數字(本系列前)
- [S1 performance](s1-performance.md) ── 更早的 S1 throughput writeup
- Memory `[[rust-port-best-config]]` ── 已更新到 af5d4f0 的 52K 數字
- Memory `[[s4-route-single-instance]]` ── wind-down 結論
