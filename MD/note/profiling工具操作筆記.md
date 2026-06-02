# .NET / S1 引擎 profiling 工具操作筆記(本次摸索整理)

> 對 AprVisual.S1(C#)做「method 大小 / 熱點 / i-cache·d-cache·branch PMU」分析時實際用過的工具、指令、與踩過的坑。
> 環境:Windows 11、.NET 10、AMD Ryzen 7 3700X(Zen 2)。同樣適用其他 .NET 專案。

---

## 0. 該用哪個工具(速查)

| 你要量的 | 工具 | 需 admin | 一句話 |
|---|---|---|---|
| 各 method **機器碼大小** | `DOTNET_JitDisasmSummary`(內建環境變數) | 否 | 最可靠、零安裝 |
| **哪個 method 吃 CPU**(per-method) | `dotnet-trace`(EventPipe) | 否 | 但拆不開 inline 的熱迴圈(見 §2 坑) |
| **硬體 PMU**:i-cache/d-cache miss、branch mispredict、IPC | **PerfView**(ETW + PMC) | **是** | per-method,要提權;見 §3 坑 |

---

## 1. Method 機器碼大小 — `DOTNET_JitDisasmSummary`

```powershell
$env:DOTNET_TieredCompilation="0"      # 強制 FullOpts,避免 tier0/tier1 兩種大小混淆
$env:DOTNET_JitDisasmSummary="1"
dotnet <App>.dll <args> > jitsizes.txt 2>&1
$env:DOTNET_JitDisasmSummary=""; $env:DOTNET_TieredCompilation=""
```
輸出每個被 JIT 的 method 一行:`JIT compiled <Class>:<Method>(...) [FullOpts, IL size=N, code size=M]`。
解析 `code size=(\d+)` + method 名,排序加總即可。

**重點/坑**:
- **被 `[AggressiveInlining]` inline 掉的 method 不會單獨出現** —— 它的碼折進呼叫者。S1 的熱迴圈
  `RecalcNode`/`ComputeNodeGroup`/`AddNodeToGroup`/`GetNodeValue`/`SetNodeState`/`RecalcNodeFast`/`ReadBits`/
  `WriteBits`/`EnqueueNode` 全 inline 進 **`ProcessQueueInterp`(4,593 B)**;所以「熱迴圈大小」要看 inlining 根
  (ProcessQueueInterp),不是個別 method。這正是判斷 **i-cache 足跡**的依據(4.6 KB ≪ 32 KB L1i → 非 i-cache bound)。
- 跑短一點即可(幾千 hc),只要熱路徑都被呼叫過、FullOpts 會在首次呼叫就全最佳化 JIT。

---

## 2. per-method CPU 取樣 — `dotnet-trace`(無需 admin)

```powershell
# profile 名稱:預設就是 cpu-sampling(別寫 --profile cpu-sampling,這版會報「不適用」)
dotnet-trace collect -o trace.nettrace -- dotnet <App>.dll <args>
dotnet-trace report trace.nettrace topN -n 25      # 依 exclusive/inclusive 列 top method
```
（`dotnet-trace` 是 global tool:`dotnet tool install -g dotnet-trace`。）

**重點/坑**:
- **要跑夠久**讓熱迴圈淹過一次性的載入工作。S1 載入(parse `.js` 網表 + compose + lowering + power-on)很重;
  500k hc 的 profile 會被 `JsLexer`/`ComposeSystem`/`LowerNetlist` 佔滿。用 **5M hc** 後 `Step` inclusive ~99%。
- **拆不開 inline 的 unsafe 熱迴圈**:整條熱路徑 inline 成一個 `ProcessQueueInterp`,EventPipe 取樣器把時間幾乎
  全歸到 `Step`(inclusive)、各 method exclusive 散落 <0.2% —— **不是沒熱點,是取樣器無法歸屬 inline 子框架**。
  要 per-method 正確歸屬 → 用 PerfView(§3,ETW + JIT 符號);要看 inline 內部「fast-path vs group-BFS」比例 →
  用引擎自帶的 `--profile` 演算法計數(ceiling.html:69.5% / 30.5%)。

---

## 3. 硬體 PMU(i-cache miss 等)— PerfView(需 admin)

PerfView 已放在 `tools/perfview/PerfView.exe`(gitignored)。封裝好的一鍵腳本:**`tools/profile_s1_perfview.bat`**
(右鍵以系統管理員執行,或雙擊會自我提權)。下面是手動指令 + **本次踩到的三個坑**。

### 坑 A:UAC 過濾 token(看似 admin 其實沒提權)
即使帳號是系統管理員,一般啟動的行程拿的是 **UAC 過濾的非提權 token**。檢查:
```powershell
whoami /groups | findstr /i "Administrators"     # 顯示 "Group used for deny only" = 沒提權
```
ETW kernel / PMC session **需要完整提權 token**。解法:`.bat` 用 `net session` 偵測,沒提權就
`powershell Start-Process -Verb RunAs '%~f0'` 自我提權(會跳 UAC,按「是」)。

### 坑 B:`ListCpuCounters` 的輸出寫進 PerfView 的 LOG,不是 stdout —— 一定要用 `-LogFile`
**這個坑害我一度誤判「本機沒有任何 PMC 計數器」。** 把 stdout 重導(`> file` / `*> file` / `| Out-Null`)會拿到
**空的**,因為清單印在 PerfView 自己的 log。正確做法:
```
tools\perfview\PerfView.exe -AcceptEula -LogFile:listcounters.txt ListCpuCounters
type listcounters.txt
```
本機(Zen 2 + Win11)實際開放的計數器(節錄):**`IcacheMisses`、`ICMiss`、`ICFetch`、`DcacheMisses`、
`DcacheAccesses`、`BranchMispredictions`、`InstructionRetired`、`TotalCycles`、`CacheMisses`(LLC)、`TotalIssues`**。
→ **i-cache/d-cache/branch PMU 在這台機器上是拿得到的**(不需要 AMD uProf)。

### 坑 C:`-NoGui` collect 過程「黑視窗看似卡住」是正常的
PerfView `-NoGui` 採集時幾乎不輸出;事後還有 **symbol merge**(看起來像沒在動,但在跑);**首次以系統管理員身分
啟動還會重新解壓自己**(~20 秒)。別急著關。`.bat` 已把它拆成「先快列計數器並顯示 → 暫停 → 再跑慢的採集」。

### 採集指令(含硬體計數器,per-method)
在 `AprVisualBenchMark\` 下(讓 data dir 解析),系統管理員執行:
```
tools\perfview\PerfView.exe -AcceptEula -NoGui ^
  -CpuCounters:"IcacheMisses:65536,DcacheMisses:65536,BranchMispredictions:65536" -ThreadTime run ^
  "dotnet <ROOT>\src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll --benchmark roms\full_palette.nes --bench-hc 1000000"
```
- `-CpuCounters:"<Name>:<interval>,..."` —— Name 用 `ListCpuCounters` 的 Source Name;interval = 每 N 個事件取一樣本
  (rare 的事件用小一點才有足夠樣本;frequent 的用大一點省 overhead)。Zen 2 PMU 槽有限,一次別開太多(~3–4 個安全)。
- `-ThreadTime` = 同時收時間軸 CPU stacks。
- 產出 `s1_perf.etl.zip`。

### 在 PerfView GUI 看
雙擊 `s1_perf.etl.zip` → PerfView 開啟 → 左側該 trace 底下會有多個 stack 視圖:
- **`CPU Stacks`** — CPU 時間。
- **`IcacheMisses Stacks`** / **`DcacheMisses Stacks`** / **`BranchMispredictions Stacks`** — 各計數器的 per-method 分布。
開啟後選 dotnet 子行程;比較 `IcacheMisses`(預期極低)vs `DcacheMisses`(預期主導)。
注意:樣本仍歸到 inline 後的 `ProcessQueueInterp`;要看其**內部來源行**,用 PerfView 的右鍵 **"Goto Source"**(需 PDB)。

---

## 4. 一句話心得
- 量 method 大小 → `DOTNET_JitDisasmSummary`(注意 inline 會折疊)。
- 量「誰吃 CPU」→ 優先 PerfView(ETW,歸屬正確);`dotnet-trace` 方便但對 inline 熱迴圈歸屬差。
- 量硬體 cache/branch → PerfView + `-CpuCounters`,**務必提權 + 用 `-LogFile` 看計數器清單**。
- 看 inline 熱迴圈內部比例 → 不要靠取樣,用引擎自帶的演算法計數(`--profile` / ceiling.html)。
