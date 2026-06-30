# Aggressive Lowering Census Study - 2026-07-01

## 背景

這次 study 的目的不是做安全的 S1 bit-exact lowering，而是先暫時放寬限制：

- 先不管目前 `NodeStatesChecksum()` 是否一致。
- 甚至先不管是否能證明邏輯層等價。
- 只評估前面討論的物理/電路直覺策略 1/2/3/5，如果做成 aggressive lowering，還能大約刪掉多少 node / transistor row。
- 第 4 類 precharge/evaluate macro 也一起 census，但它本質上比較像 macro/behavioral replacement，不是單純 topology lowering。

為了測量，新增了一個診斷用 CLI：

```text
--aggressive-lower-census <rom>
```

實作位置：

- `src/AprVisual.S1/Sim/WireCore.LowerCensus.cs`
- `src/AprVisual.S1/Test/TestRunner.cs`

這個 census 不會真的改壞 netlist，也不會套用 destructive lowering；它只在 build-time netlist 上掃描候選並輸出規模。

## 測試條件

主要測試命令：

```powershell
dotnet .\bin\Release\net11.0\AprVisual.S1.dll `
  --aggressive-lower-census C:\ai_project\AprVisual\AprVisualBenchMark\roms\full_palette.nes `
  --extra-ram `
  --system-def-dir C:\ai_project\AprVisual\AprVisualBenchMark\data\system-def
```

另外也跑了一次不帶 `--extra-ram` 的版本，結果差異很小，結論不變。

Build 驗證：

```text
dotnet build -c Release
0 warnings, 0 errors
```

## Baseline

在目前既有 safe lowering + handlers 之後：

```text
live nodes:   14,729
normal nodes: 14,727
transistors:  26,887
```

既有 safe lowering 統計：

```text
lowering: nodes 15164 -> 14723 (merged 441)
transistors 27305 -> 26775 (dropped 530: connections + dead gate==vss + dups)
```

## Strategy 1 - Rail Clamp / Constant Fold

測到的 aggressive constant candidates：

```text
const low nodes:      84
const high nodes:      5
const conflict nodes:  0
```

若把這些 constant nodes 摺到 rail：

```text
nodes:       14,729 -> 14,640
delete/merge:   89 nodes = 0.60%

transistors: 26,887 -> 26,667
drop:           220 rows = 0.82%
```

結論：

- 數量很小。
- 這類很容易動到 rail/floating/tie-break 行為。
- 即使完全無視 checksum，單靠這類也沒有大幅效能空間。

## Strategy 2 - Series Stack Compression

測到的 pure degree-2 midpoint：

```text
pure degree-2 midpoints: 695 nodes = 4.72% of normal nodes
unnamed:                 673
supply-touch:            458
same-gate fusable:         0
```

理論 row saving：

```text
replace 2 channel rows with 1 macro row per midpoint
upper bound ~= 695 transistor rows
```

結論：

- 這是 1/2/3/5 裡最大的一塊 topology-looking 空間。
- 但 `same-gate` 直接安全融合 subset 是 0。
- 一般 series compression 會碰到 midpoint charge storage / dynamic node / capacitance tie-break。
- 幾乎可以確定會破目前 checksum。

## Strategy 3 - Mutually-Exclusive Gate Pruning

用簡單 clock/phase 名稱 heuristic 掃：

```text
phase-exclusive pure bridges: 0
incident rows touched:        0
```

Heuristic 包含：

```text
clk0/clk1
pclk0/pclk1
phi1/phi2
```

結論：

- 目前在這份 netlist 形狀下，沒有抓到可用候選。
- 若要深入做，會需要 phase invariant / formal timing knowledge。
- 這已經不太像簡單 lowering。

## Strategy 4 - Precharge/Evaluate Or Pulldown Macro

這類不是單純刪 node，而是把 transistor pull-down/evaluate network 變成 macro evaluator。

測到：

```text
strict pullup + GND-only singleton outputs:
  nodes: 3,398
  pulldown rows replaceable: 6,359 = 23.65%

broad pullup nodes with any GND evaluate path:
  nodes: 6,234
  touched rows: 10,152 = 37.76%

clocked precharge/evaluate-looking nodes:
  nodes: 522
  touched rows: 1,624
```

結論：

- 這是唯一看起來有大量 transistor row 可以替換的方向。
- 但這已經是 macro/behavioral optimization，不是 S1 topology lowering。
- 若未來允許 S2-style abstraction，這類可能才是真正的大空間。
- 在目前 S1 bit-exact checksum 標準下，不應視為安全 lowering。

## Strategy 5 - Unobservable Island Elimination

Strict 定義：

- unnamed
- no gate use
- no callback
- no pullup
- no rail touch
- no forceCompute

測到：

```text
strict islands:
  components: 12
  nodes:      15
  rows:        3
```

Aggressive 定義：

- unnamed
- no gate use
- no callback
- no forceCompute
- 允許 pullup / rail touch

測到：

```text
aggressive state-only islands:
  components: 59
  nodes:      62 = 0.42%
  rows:       64 = 0.24%
```

結論：

- 真正可刪空間非常小。
- 就算完全不管 checksum，也不像是值得做的大方向。
- 對 checksum 仍會破，因為完整 `NodeStates` array 會不同。

## 粗略 Union Upper Bound

只看 1/2/5 這些比較像 topology lowering 的項目：

```text
candidate nodes covered by 1/2/5:
  844 / 14,727 normal nodes = 5.73%
```

若粗估 transistor row：

```text
strategy 1 drop:        220 rows
strategy 2 row saving:  695 rows
strategy 5 aggressive:   64 rows
rough total:            979 rows ~= 3.6% of 26,887
```

如果把 strategy 4 的 macro touched rows 也算進去：

```text
transistor rows touched/replaced by 1/2/4/5:
  11,517 / 26,887 = 42.83%
```

但這個 42.83% 不能解讀成「可以直接刪 42.83% transistor」。
它代表大量 transistor row 位於可被 macro evaluator 觸及的 pullup/pulldown network；要吃到這個空間，必須改模型。

## Checksum 風險

目前 checksum 是完整 `NodeStates` array 的 FNV hash，不是只看 CPU/PPU/RAM/FrameBuffer 等外部可觀測狀態。

因此：

| 策略 | 是否會破目前 checksum | 原因 |
|---|---:|---|
| 1. rail clamp / constant fold | 高機率會 | 會把 node 摺成 rail/constant，改變 internal node state/topology |
| 2. series stack compression | 幾乎必破 | 會消掉 midpoint；midpoint 可能保存 charge/state |
| 3. mutually-exclusive pruning | 目前不會 | 目前候選數是 0 |
| 5. unobservable island elimination | 會 | 即使邏輯不可觀測，完整 NodeStates checksum 仍會不同 |

所以 1/2/3/5 如果全使用並真的重建 netlist，預期會破目前 checksum。

唯一可能維持 checksum 的方式是做 ghost-state / virtual checksum compatibility，把被刪 node 的 state 另外重建回來。但這會增加 bookkeeping，通常和效能目的相衝突，也不保證 dynamic/floating state 可正確重建。

## 總結

這次 census 的結論：

1. 現有 safe lowering 之外，單純 topology lowering 的可刪空間不大。
2. Strategy 1 約 `0.6% nodes / 0.8% rows`。
3. Strategy 2 表面有 `695 midpoint nodes`，但沒有 same-gate safe subset，風險高。
4. Strategy 3 目前候選為 0。
5. Strategy 5 幾乎沒有量，strict 只有 15 nodes。
6. 真正大的數字在 Strategy 4，但那是 macro/behavioral optimization，不是 S1 bit-exact lowering。

務實判斷：

- 若仍以 S1 bit-exact / current checksum 為標準，這條 aggressive lowering 不適合繼續投入。
- 若未來開一條不要求 node-level checksum 的 branch，最值得探索的是 Strategy 4 的 pullup/pulldown network macro 化。
- 1/2/5 比較適合保留為 destructive census / documentation，不適合當作主線效能方向。
