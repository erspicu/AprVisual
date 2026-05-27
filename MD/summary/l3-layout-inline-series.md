# L3 layout + inline 系列 — 微架構壓榨成果

> 日期:2026-05-26 ~ 2026-05-28
> Branch:`aot-codegen`
> 範圍:WireCore L3 hot data layout 緊密化 + hot method JIT/LLVM 強制 inline + iterative BFS
> 工程量:11 commits、~120 行 code(declaration / cast / annotations / iterative loop)、0 algorithm 變動
> 結果:**C# +26.4% best / +26.6% avg(37,928 → 47,942 hc/s)、Rust +7.7% best(48,622 → 52,382 hc/s)**

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

### C# `aot-codegen`(10 commits)

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
| 8 | `a929ae0` | inline `ComputeNodeGroup` + `RecalcNodeFast` + `RecalcNode` | 46,413 | 45,616 | +23.5% |
| 9 | `73b1f88` | iterative BFS + inline `AddNodeToGroup` | **47,942** | **46,744** | **+26.6%** |

### Rust port(2 commits)

| # | Commit | 變動 | Best (hc/s) | Δ |
|---|---|---|---|---|
| — | (origin) | — | 48,622 | — |
| 1 | `a357cb2` | `transistor_list` i32→u16 | 50,306 | +3.5% |
| 2 | `af5d4f0` | `#[inline(always)]` × 4 hot method | **52,382** | **+7.7%**(10-run avg 52,106) |

### Rust port NOT applied:iterative BFS

C# 上 `73b1f88` 改 iterative BFS + inline AddNodeToGroup 拿 +2.9%,但**Rust 上同樣變動實測 -1.3% avg**(10 runs:recursive 52,106 / iterative 51,446)。 LLVM 對 recursive function 的優化(展開幾層 + tail-recursion 偵測 + cross-recursion register alloc)比 .NET JIT 強很多 ── recursive 在 Rust 已接近最優,iterative 反而打散結構。

**Platform-specific 結論:.NET JIT 的「不能 inline recursive」是真實盲點,LLVM 沒這盲點**。 不要盲目把 C# 的 hot-path 變動 sync 到 Rust ── 必須各自實測。

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

`73b1f88` 又往前推了一步:**recursive `AddNodeToGroup` 改 iterative,然後加 [AggressiveInlining]**,讓 JIT 終於把 BFS body 也 inline 進 chain ── 再 +2.9%。 失敗實驗(後續 "重型" iterative)證明:把 method 變大到 JIT 拒絕 inline,直接 -6%。

關鍵:**inlining 是累加的、不是線性的**。 單獨 inline 一個 hot method 的 gain 通常 +0.5-3%,但全 chain inline 後 cascade 倍增 ── 反之,任何一個 link 大到無法 inline,cascade 整段斷,反向 -5-9%。

## 失敗的嘗試

### iterative BFS 取代 recursive `AddNodeToGroup`(第一次嘗試,-1.7% revert)

Gemini 建議改 BFS 消除 recursion overhead。 第一版實作 bit-identical 但 **慢 1.7%**。 **後來在 `73b1f88` 找到正確姿勢:必須同時加 [AggressiveInlining](recursive 版 JIT 拒絕,iterative 版才能 inline)→ 解鎖 inline cascade 反而 +2.9%**。 第一次失敗的根因是只改 iterative 沒加 inline annotation,失去 inline cascade。

### 「重型」iterative BFS — locals 緩存 + cached flags + monotonic early-out(-6.0% revert)

實驗第 2 次 iterative 變體,加上多個 micro-opt:
- `groupFlags`、`maxState`、`maxConnections` 緩存到 local,迴圈結束 commit 一次
- `EnableIrInterp && IrAbsorbed != null` 等 chain check 每 walk 算一次,inner loop 直接讀 bool
- monotonic-bit early-out:`Gnd` flag 一旦 set,不再掃更多 GND channel

預期 +1-2%,**實測 -6.0%**。 原因:method body 從 ~30 LOC 膨脹到 ~80 LOC,**JIT 拒絕 inline**,失去 `73b1f88` 解鎖的 inline cascade。 micro-opt 內部省下的 instruction 抵不過 inline cascade -5-9% 的損失。

**Inline cascade dominates micro-opt**。 任何讓 method 大到 JIT 拒絕 inline 的「優化」都會反向。

### 手動 inline EnqueueNode + 消除 c2 重複檢查(-0.5% revert)

Gemini r6 建議。 預期 +0.5-2%,實測 **-0.5% avg**。 .NET 10 JIT 已比文章假設聰明 ── `[AggressiveInlining]` + constant folding `CountEvents=false` + commoning Npwr/Ngnd 比較全自動。 手動 inline 反而增加 instruction footprint。

revert。 教訓:**JIT 已做得很好,手動寫 inline 反而抑制 JIT 自己的優化機會**。

### Bit-pack 4 個 state array 進 NodeInfo.Flags(未試)

Gemini r6 建議。 前提錯誤:那 4 個 array(`CodegenOwned`、`IrClass`、`IsPureLogic`、`DeadEndSkippable`)的讀取都 gate 在對應 enable flag 後,default config 下根本不讀。 不是 hot path,bit-pack 沒收益。

跳過。

### 函數指標消除 `if (EnableXxx)` 分支(未試)

Gemini r6 建議。 `static bool` flag 永遠 false 的 branch predictor 命中率 ~100%,well-predicted false 1-2 cycle 全帶。 改 `delegate*<int, void>` 反而:indirect call 預測難、組合爆炸、失去 runtime A/B。

跳過。

## 對齊比較數字(2026-05-28 終態,73b1f88 + af5d4f0)

| Mode | C# best | C# avg | Rust best | Rust avg | Rust > C# |
|---|---|---|---|---|---|
| default | TBD | TBD | TBD | TBD | — |
| + `--fast-path` 10-run | **47,942** | **46,744** | **52,382** | **52,106** | **+9.2%** |

C# 從 inline series 之前的 37,928 → 47,942 = **+26.4% best**
Rust 從 inline series 之前的 48,622 → 52,382 = **+7.7% best**

### 副作用:C# fast-path 效益消失

inline 系列之前,C# `--fast-path` 給 +2.9%。 現在 +0.8% best / -0.7% avg ── **埋沒在 noise 內**。 推論:當所有 hot method inline 後,group walk 路徑已跟 fast-path 一樣便宜。 fast-path 的 `IsPureLogic[nn]` 額外 array read 反而沒省到。

Rust 仍從 fast-path 拿 +1.6%(LLVM 對 group walk 的 inlining 不像 .NET JIT 那麼激進)。

未來可考慮:C# 上預設關 `--fast-path`,或重新評估其維護成本。

## Platform-specific finding(.NET JIT vs LLVM)

C# 跟 Rust 對 hot path 變動的反應**不一定一致**:

| 變動 | C# 結果 | Rust 結果 |
|---|---|---|
| `TransistorList` 縮型 | +1.9% | +3.5% |
| inline `RecalcNode`(大 body) | +9.8%(nested cascade)| +7.5% |
| iterative BFS + inline `AddNodeToGroup` | **+2.9% 贏** | **-1.3% 輸** |

關鍵差異:**.NET JIT 對 recursive function 直接拒絕 inline**(無論 [AggressiveInlining]) ── C# 改 iterative 才能解鎖 inline cascade。 **LLVM 對 recursive 處理好得多**(展開幾層 + tail-recursion 偵測 + cross-recursion register alloc),Rust recursive 已接近最優,改 iterative 反而打散結構。

**結論:不要盲目把 C# 的 hot-path 變動 sync 到 Rust ── 必須各自實測**。

## Real-time 距離

- 當前 best:Rust 52,382 hc/s
- NES real-time:17.8M hc/s
- 距離:**~340×**

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
