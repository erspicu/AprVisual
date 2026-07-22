# S1A per-cycle 機制 step 強制 inline — 死路(2026-07-22)

> 問題:S1A 的 benchmark hotpath,把 5 個 per-cycle 機制 step 宣告 `[AggressiveInlining]`,
> 能不能讓 JIT 完整融合進 `StepCycle`、並更快?
> 結論:**機械上成功(JIT 真的全 inline 了,含迴圈),但效能是 no-op(−0.19%,埋在 noise 裡)。不值得,已 revert。**

## 現況(default)

`StepCycle` 每個 master half-cycle 呼叫 6 個 method,JIT 預設**全部 `call`、不 inline**:

```
; Assembly listing for method AprVisual.Sim.WireCore:StepCycle() (FullOpts)
       call  ProcessQueue()          ← settle 迴圈(內部 per-node inline cascade 早已做滿)
       call  M2DecayStep()           ← 以下 5 個 = S1A 機制層
       call  M6xPhaseStep()
       call  TestShimChainStep()
       call  AleReadMuxStep()
       call  OamDmaPpuBusShimStep()
```

核心 per-node hotpath(`ProcessQueue → RecalcNode → ComputeNodeGroup → AddNodeToGroup →
GetNodeValue → SetNodeState → EnqueueNode`)在 [[l3-layout-inline-series]] 時代就已 `[AggressiveInlining]`
折成 straight-line block —— 那才是熱的地方(每半週期數萬次 per-node)。

## 實驗:給 5 個 step 加 `[AggressiveInlining]`

加屬性、rebuild、再 disasm:**5 個 step 全部 inline 進 `StepCycle`**(連含迴圈的 `M6xPhaseStep`/
`M2DecayStep`/`TestShimChainStep` 也 inline —— `StepCycle` 裡剩下的 `call` 都是那些 step body 的
**深層**呼叫:`M6xWindowOk`、各 `*ShimStep`、`Mux*`、`OamDma*`)。checksum `0x41244C26C45EDD32` 不變(bit-exact)。

> **更新舊假設**:以前認為「.NET JIT 對含迴圈的 method 拒絕 inline」。**.NET 11 JIT 已不然** ——
> `[AggressiveInlining]` 下含迴圈的 method 照樣 inline。老 note 的「recursive 才是真盲點」仍成立
> (`AddNodeToGroup` 當年要改 iterative 才能 inline),但「有迴圈就不 inline」已過時。

## 效能:interleaved-paired(釘核 14,12 輪,300k hc,Zen2)

| | median hc/s |
|---|---|
| before(default) | 129,548 |
| after(全 inline) | 129,298 |
| **delta** | **−0.19%** |

run-to-run 散布 ~10%(120.6K–132.5K),−0.19% 完全埋在 noise 裡 = **無可量測收益,方向還略負**。

## 為什麼沒用(機制)

- 這 5 個 step **每半週期只呼叫 1 次**(不是 per-node)。省下的 call/ret ≈ 5 × ~3ns = 15ns;
  一個半週期 ~7.8µs → 省 ~0.2% 上限。而 step 的 **body 工作量不變**(inline 不會讓它更省)。
- 反而 `StepCycle` 從 6-call 的小函式膨脹成內含 5 個 step body 的巨型函式 → i-cache footprint 變大,
  抵掉那 0.2%。淨效果 ≈ 0 或略負,和實測一致。
- 對照 [[l3-layout-inline-series]]:inline cascade 有效是因為那條 chain **每半週期跑數萬次 per-node**;
  per-half-cycle 的機制層頻率低 4~5 個數量級,inline 它沒有 cascade 可言。

## 工具備忘(下次別重造)

```powershell
$env:DOTNET_TieredCompilation="0"     # 直接拿 FullOpts tier(不要 tier0)
$env:DOTNET_JitDisasm="StepCycle"     # 子字串比對方法名;可用 "StepCycle ProcessQueue ..."
& AprVisual.S1A.exe --benchmark full_palette.nes --bench-hc 2000 --extra-ram --system-def-dir ...
$env:DOTNET_JitDisasm=$null; $env:DOTNET_TieredCompilation=$null   # ⚠ 用 =$null 清,別用 Remove-Item Env:\(sandbox 擋)
```

- `DOTNET_JitDisasm` **在 net11 release runtime 可用**(會印 FullOpts 組語到 stdout)。
- 看「什麼 inline 進去」= 數 `StepCycle` disasm 裡剩幾個 `call [AprVisual...]`。
- benchmark 用 interleaved-paired(交替兩個 exe),Zen2 sub-1% 難分 → 若要權威數字用 Pi5 鎖頻。

## 結論

**死路,已 revert。** S1A 全武裝的 ~10% overhead 在**機制 body 的實際工作**(M2Decay 走衰減島、
M6xPhase 走相位表、影像 callback…),不在 call overhead —— 強制 inline 動不到那塊。要壓那 10%
得減少 body 工作(演算法),不是 inline。
