# AprVisual.S1 Sim 程式碼評價與 hotpath 檢查

日期:2026-06-11  
範圍:`C:\ai_project\AprVisual\src\AprVisual.S1\Sim` 內 C# code。  
限制:不考慮 IR/codegen 類方案,只看目前 event-driven switch-level S1 引擎本身。

## 結論

這份 `Sim` 程式碼已經是高強度 performance-engineering 等級。以 C# 開關級 NES netlist simulator 來看,水準約 **8.5/10 到 9/10**。

高分原因不是「用了 unsafe」本身,而是整套 hotpath 有完整閉環:

- 熱資料改成 unmanaged SoA,避免 managed object graph。
- `NodeInfo` 壓成 16 bytes,小 fanout payload inline,高 fanout 才走 `TransistorList`。
- `ProcessQueue -> RecalcNode -> AddNodeToGroup -> SetNodeState` 的瓶頸定位清楚。
- 每個優化幾乎都有 checksum / benchmark / Debug profiler / revert 記錄。
- 近期的 `range-prune` 把 hotpath 的 `PruneMask[c]` 查表改成 id 區間比較,是很成熟的「刪相依載入鏈」做法。

主要弱點也很明顯:

- 複雜度已經偏高,尤其是 two-pass auto-renumber + range verification。正確性靠 runtime 驗證守住,但維護門檻高。
- 註解很多且有歷史脈絡,對熟悉專案的人是優點,對新維護者則需要先讀筆記才能判斷哪些結論已失效。
- hotpath 幾乎已到「每改一點都要 A/B」的區間。直覺型 micro-opt 大多會是雜訊或負值。
- 即使目前已經比昨天快很多,仍離 real-time 差約 397x；這是 switch-level event-driven 模型本身的天花板,不是普通 C# 寫法問題。

## 目前實測

Release build:

```text
dotnet build -c Release
build ok, 0 warning, 0 error
engine version: e50bd3a (2026-06-11)
```

Benchmark:

```text
dotnet run -c Release -- --system-def-dir C:\ai_project\AprVisual\ref\metalnes-main\data\system-def --benchmark C:\ai_project\AprVisual\nes-test-roms-master\不需要測試(偏向展示DEMO)\full_palette\full_palette.nes --bench-hc 200000 --extra-ram
```

結果:

```text
rate: 108,146 hc/s (9.25 us/hc)
checksum @ t=200192: 0x9B103E5E206E4C37
range-prune VERIFIED
renumber: AUTO class-major permutation (14,724 ids, A=460 S=1275 B=7532)
load: 1.56 s
```

對照昨天看到的 P-5 pinned 版本約 `90.9K hc/s`,目前 range-prune 版本已經明顯更好。

Debug 50k profile:

```text
pops=21,195,797
no-change=17,003,495 (80.2%)
waste:
  FloatSingle 7.5%
  FloatMulti  12.2%
  PullUp      41.9%
  Supply      38.0%
  Other        0.4%

settle passes:
  mean=12.10, max=45, p99=33, p99.9=34

BFS depth:
  mean=1.13, p90=2, p99=3, p99.9=4, max=14

co-activity:
  per-hc distinct nodes=361.0
  NodeInfo lines=118.8
  ideal lines=90.2
  headroom=1.32x
```

Payload hist:

```text
live nodes 14,729
inline 13,814 / overflow 915
InlineCap=6
payload=3: 6,924 (50.1% of inline)
payload=6:   628 (4.5% of inline)
```

`NodeInfo` 16-byte / `InlineCap=6` 目前仍合理,不建議為了提高 inline cap 撐回 32 bytes。

## 目前 hotpath 形狀

主要熱路徑:

```text
StepCycle
  -> ProcessQueue
    -> RecalcNode
      -> RecalcNodeFast 或 ComputeNodeGroup/AddNodeToGroup
      -> SetNodeState
        -> walk NodeTlistGates
        -> enqueue endpoints into RecalcListNext
```

現在最重要的新結構是 `range-prune`:

- `ClassifyPruneTaint()` / `ClassifyTurnOffSkip()` 仍建立 `PruneMask` 作為 ground truth。
- `ApplyRenumber()` 用 prune class 作 major key 重新編 node id。
- `ClassifyTurnOffSkip()` 每次 Reset 後驗證 range 推導是否等於真實 `PruneMask`。
- Release 驗證通過後釋放 `PruneMask`。
- `SetNodeState` 熱迴圈不再讀 `PruneMask[c]`,只用 `RangePruneA/S/B` 做比較。

這是目前最好的方向:刪掉位在 carried dependency chain 上的 random load。

## 還值得 A/B 的方向

### 1. 用 `--co-profile` profile key 覆蓋 auto renumber

優先度:P1,預期小正到中性。

現在 auto renumber 的 locality key 是 clk signal-flow BFS 近似。Debug co-activity 顯示 range-prune 後仍有 `1.32x` NodeInfo line headroom,已經不大,但不是零。

既有 6/11 筆記提過實測 profile key 可能再多約 +1.7%。如果目標 workload 固定,可以重新測一次:

1. Debug 跑 `--co-profile <file>`。
2. Release 用 `--renumber <file>`。
3. 跟 auto class-major baseline 做 interleaved-paired A/B。

注意:這會綁 profile/netlist/workload,不適合當無條件預設,但適合當「固定 ROM 或固定測試集」的可選模式。

### 2. 針對 PullUp/Supply no-change 做新的診斷,不要直接剪

優先度:P1 研究,P0 不建議直接實作。

Debug waste 剩下最大的是:

- PullUp no-change:41.9%
- Supply no-change:38.0%

這表示 P-1/P-2/P-3/P-4/range-prune 已經把比較容易證明的 float-hold 類剪掉,剩下多是「被隱藏 driver 拉回同值」的重算。

這塊看起來很誘人,但 P-5 dominant/pinned 已經證明:只要需要每次解析維護 runtime fact,維護成本會吃掉 skip 收益。後續如果要碰,應先加診斷回答:

- no-change PullUp/Supply 的來源 gate 分布是 turn-on 還是 turn-off?
- 這些節點是否存在純 static subset,完全不需要 runtime metadata?
- 如果需要讀 `NodeStates[c]` 或維護 per-node pin/power fact,是否又落回 P-5 的負結果?

只有找到「純靜態分類 + range encoding」的 subset 才值得做。

### 3. ROM/read-only handler 不監看 `d[]` 可在新版重測

優先度:P2。

目前 `AttachRamLikeHandler()` 仍無條件:

```csharp
trigger.AddRange(addrL);
trigger.AddRange(dataBusL);
```

對 read-only handler,輸出只依賴 `cs + addr`,理論上 `d[]` 變動不需要喚醒 ROM callback。這項以前測過是雜訊/退回,不能當新發現。不過目前 handler/context/range-prune baseline 都已變,可低成本重測:

```csharp
bool readOnly = isRom || we == EmptyNode;
trigger.AddRange(addrL);
if (!readOnly) trigger.AddRange(dataBusL);
```

驗證要包含:

- checksum
- CHR ROM / PPU path
- frame dump 或 screenshot,因 framebuffer 不在 `NodeStatesChecksum` 內

### 4. `DoVideo` 的 setup-time invariant 拆快慢路

優先度:P3。

`DoVideo()` 熱路徑仍有:

```csharp
FrameBuffer != null
_vidPalRamOk[slot] != 0
```

在 `LoadSystem -> ResetNes(full:true)` 後,framebuffer 一般已配置；palette RAM 32 slots 若全都有 6 bits,也可以在 attach 時變成 handler kind 或 bool flag,讓 hot path 少 branch。

預期收益小,因為主瓶頸仍是 settle/BFS,但風險低。動 video 必須用 PNG/frame dump 驗證,不只看 checksum。

### 5. `ProcessQueue` current list/hash 局部化可小測

優先度:P3。

`AddNodeToGroup` 已經把指標 hoist 到 locals,但 `ProcessQueue` 內層仍直接讀靜態欄位:

```csharp
int nn = RecalcList[i];
if (RecalcHash[nn] != 0) ...
```

可 A/B 改為 swap 後建立 local:

```csharp
int* list = RecalcList;
byte* hash = RecalcHash;
int count = RecalcListCount;
```

再用 `list[i]` / `hash[nn]`。這類可能被 JIT 自動處理,也可能因 method 巨大而有一點 register allocation 差異。預期小到雜訊,但改動可局部。

## 不建議再花時間的方向

### 1. P-5 dominant / pinned

已完整調查並寫在 `2026-06-10-dominant-bypass-1bit-pinned-結論.md`。skip 本身有收益,但 runtime fact 維護有約 7% 地板,最佳淨值仍是負。不要在目前 hotpath 重新塞 pinned metadata。

### 2. `IsPureLogic` range/fold

`2026-06-11-範圍剪枝與cls-range-範圍化公式的適用條件.md` 已記錄:cls-range 中性偏負。原因是 `IsPureLogic` load 不在 carried dependency chain 上,CPU 推測執行能藏掉它。不要為了少一個分類 array 破壞現有形狀。

### 3. `NodeInfo` 撐大或提高 inline cap

payload hist 不支持。現在 inline 已有 93.8%,payload=6 只佔 inline 的 4.5%。32-byte `NodeInfo` 會直接加大 BFS 工作集,風險高。

### 4. 提早停止 settle

`2026-06-08-提早停止settle的正確性實驗.md` 已經確認不正確。Debug profile 也顯示 settle 尾端雖少,但不是可捨棄誤差。

### 5. 純 locality renumber / prefetch / queue ushort 化

6/11 range-prune 筆記已整理:純 locality 改善或 prefetch 不是主要槓桿。真正有效的是刪 dependency chain 的載入,不是讓資料更靠近但仍然要等。

## 維護評價

這專案已經不是「再隨手優化幾個 if」的階段。之後每一個 hotpath 變更應該遵守:

1. 先提出它刪掉的是哪一條 carried dependency。
2. 若只是少一個普通 load/branch,先假設收益是零。
3. 必須同時跑 checksum 與 interleaved-paired benchmark。
4. 中性就退,不要累積複雜度。
5. 動 handler/video 要補 framebuffer/PNG 類驗證,checksum 不涵蓋畫面。

總評:目前 S1 C# 引擎的軟體工程品質很高,尤其是「負結果有記錄、可退回、可驗證」這點很少見。下一階段的主要風險不是缺少優化點,而是每個新剪枝都可能把正確性模型推得更複雜；值得做的只剩能明確縮短相依鏈、且可以用 runtime verifier 或 golden checksum 守住的方案。
