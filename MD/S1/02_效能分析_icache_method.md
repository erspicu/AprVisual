# S1(C#)效能分析:i-cache footprint / method 大小 / 熱點分布

> 目的:檢視 S1(C#)金本位引擎的指令快取(i-cache)壓力、各 method 機器碼大小、與熱點分布。
> 環境:**AMD Ryzen 7 3700X**(Zen 2;L1i 32 KB、L1d 32 KB/core、L2 512 KB/core、L3 32 MB)、Windows 11、
> .NET 10、Release build。Workload:`full_palette` 300k–5M master half-cycle。

---

## 0. 結論(先講)

1. **S1 完全不是 i-cache bound。** 整條熱迴圈 —— `RecalcNode` / `ComputeNodeGroup` / `AddNodeToGroup` /
   `GetNodeValue` / `RecalcNodeFast` / `SetNodeState` / `EnqueueNode` / `ReadBits` / `WriteBits` / `ProcessQueue`
   —— **全部被 inline 進單一一個 method `ProcessQueueInterp`(機器碼 4,593 B)**;`Step`(27 B)/`StepCycle`(47 B)
   只是外層 wrapper。**熱指令工作集 ≈ 4.6 KB,僅 32 KB L1i 的 1/7**,穩穩常駐 L1i。
2. **對照組**:oblivious 編譯版(Escape-1,`study.html` §4–5)把 ~6,000 節點展成 ~700 KB 直線碼、每半週期掃 6.5
   次 → 遠超 32 KB L1i → **front-end / i-cache thrash,反而比直譯器慢 2×**。S1 正好相反:小程式碼、大資料。
3. **真正的 bound 是資料 / 記憶體延遲**(§A 的 `NodeInfos` 230 KB〔2026-06-02 由 460→230〕、`TransistorList` 225 KB 的指標追逐 + 不規則
   分支),不是指令快取。per-event ≈ **~82 CPU cycle/recalc**,卡在 load-use latency + branch mispredict
   (見 `WebSite/ceiling.html`)。
4. **真實硬體 i-cache miss 計數在這台機器上拿得到**(更正:見 §5)。這台 Ryzen 7 3700X + Win11 經 PerfView
   `ListCpuCounters`(elevated)確認**有開放 PMC**,含 `IcacheMisses` / `ICMiss` / `ICFetch` / `DcacheMisses` /
   `BranchMispredictions` / `InstructionRetired` / `TotalCycles` 等。用 `tools\profile_s1_perfview.bat`(系統管理員)
   採集、PerfView GUI 看 per-method。預期:hot loop(4.6 KB,§2)的 **IcacheMisses 極低、DcacheMisses 主導** ——
   會用真實 PMU 數據再次坐實「非 i-cache bound、而是資料/記憶體延遲 bound」。

---

## 1. 工具與方法

| 量測 | 工具 | 需 admin? | 取得 |
|---|---|---|---|
| **各 method 機器碼大小** | `DOTNET_JitDisasmSummary=1` + `DOTNET_TieredCompilation=0`(內建,無外掛) | 否 | ✅ 本次取得 |
| **per-method CPU 取樣** | `dotnet-trace collect`(EventPipe) | 否 | ⚠️ 取得但歸屬不佳(見 §3) |
| **i-cache miss / IPC(PMU)** | PerfView(ETW+PMC)或 AMD uProf | **是** | ❌ 環境非 elevated;§5 給命令 |

PerfView 已下載到 `tools/perfview/PerfView.exe`(23.6 MB,已 gitignore;它是 ETW+PMC 工具,對 JIT 符號解析比
dotnet-trace 好,但 collect 需 admin)。

---

## 2. Method 機器碼大小(`DOTNET_JitDisasmSummary`,FullOpts)

全部 48 個 `WireCore.*` method 合計 **37,657 B**。但要分「**熱路徑**」與「**載入期(只跑一次)**」:

### 熱路徑(每半週期執行)
| Method | 機器碼 | 說明 |
|---|---|---|
| **`ProcessQueueInterp`** | **4,593 B** | **唯一的熱迴圈**;`RecalcNode`/`ComputeNodeGroup`/`AddNodeToGroup`/`GetNodeValue`/`RecalcNodeFast`/`SetNodeState`/`EnqueueNode`/`ReadBits`/`WriteBits`/`ProcessQueue` 全 inline 於此 |
| `StepCycle` | 47 B | 外層:toggle clk、跑 handler chain、呼叫 ProcessQueue |
| `Step` | 27 B | 迴圈呼叫 StepCycle |
| `InvokeCallbacks` | 187 B | settle 後觸發記憶體 handler callback(rare;pending=0 時 O(1) 返回) |
| handler closures(RAM/ROM/video) | 各 <300 B | 由 callback 觸發,~記憶體存取頻率(遠低於 recalc) |

→ **熱指令工作集 ≈ 4.6 KB + 數百 B ≈ 5–6 KB ≪ 32 KB L1i。**

### 載入期(`LoadSystem` 只跑一次,佔 37.7 KB 的大宗,但與 hc/s 無關)
`LowerNetlist` 5,707 / `Reset` 4,731 / `AddInstance` 4,545 / `ResolveNodes` 1,434 / `ClassifyPureLogicNodes` 1,393 /
`LoadModuleDef` 1,347 / `AttachRamLikeHandler` 1,273 / `AddConnection` 1,182 / … 這些是 parse `.js` 網表、組裝
26,775 個 transistor、lowering、power-on settle 的程式碼,**載入時各跑一次後就不再進 hot path**。

---

## 3. 熱點分布(per-method usage)

### 3a. dotnet-trace CPU 取樣(5M hc,熱迴圈佔 99% wall)+ 其限制
`dotnet-trace`(EventPipe sample profiler)結果:`WireCore.Step` **inclusive 99.15%**,但所有 method 的 **exclusive
都 <0.21%、散落**(InvokeCallbacks 0.21% / RecalcNode 0.11% / AddNodeToGroup 0.02% …)。

**這不是「沒有熱點」,而是取樣器的限制**:整條熱迴圈是**單一個被大量 inline 的 unsafe method**(`ProcessQueueInterp`,
§2),EventPipe 取樣器無法把 inline 進去的子框架(RecalcNode/AddNodeToGroup/…)拆開歸屬,於是時間幾乎全歸到
`Step`(inclusive)而 exclusive 無處落 → 看起來「散落」。**這正是 PerfView(ETW + JIT 符號)比 dotnet-trace 好用的
地方**(它能把 JIT 位址解析回正確 method);可惜需 admin(§5)。

### 3b. 權威 work-profile(引擎自帶 `--profile` 儀器化,記錄於 `WebSite/ceiling.html`)
真正可信的「工作分布」來自引擎自己數每次 recalc / 每次 BFS visit(full_palette 300k,181.4M recalcs):

| 指標 | 數值 |
|---|---|
| 總 recalc | 181,429,313(~604.6 / 半週期) |
| **fast-path**(singleton,O(1)) | **69.5%**(S1 已最優,碰不得) |
| **group-BFS**(多節點群解) | **30.5%** |
| BFS 平均群大小 | 2.25 節點 |
| per-recalc 成本 | **~82 CPU cycle**(@ ~4 GHz,48.4M recalc/s) |
| BFS 工作分散度 | top-10 節點僅佔 1.2%、top-200 佔 21.6%(極擴散) |

→ 「usage」實質上就是 `ProcessQueueInterp` 內的 **fast-path(70%)+ group-BFS 群解(30%)**;時間花在
**記憶體依賴載入 + 不規則分支**,不是指令抓取。

---

## 4. i-cache 結論

- **足跡角度(決定性)**:熱指令 ~4.6 KB ≪ 32 KB L1i → 熱迴圈整個常駐 L1i,**front-end 不會 stall 在抓指令**。
- **對照**:study.html §5 的 oblivious 編譯版 ~700 KB 直線碼掃 6.5×/hc → i-cache thrash、比直譯器慢 2×。**S1 是
  「小程式碼跑大資料」的相反極端。**
- **真正瓶頸**:§A 資料 —— `NodeInfos`(2026-06-02 砍半 460→**230 KB**)、`TransistorList`(225 KB);雖然每半週期
  working set(~604 活節點 / 平均群 2.25)遠小於全表,但 BFS 是**指標追逐 + 不規則分支**,~82 cycle/recalc 卡在
  load-use latency 與 branch mispredict —— 這才是該攻的維度(且 ceiling.html 已論證在此抽象下已到天花板)。
- ∴ **對 hc/s 而言,i-cache 不是問題;不需要為 i-cache 做任何事。**

---

## 5. 真實硬體 i-cache miss / IPC —— 可取得(本機 PMC 已確認開放)

**更正先前判斷**:一度誤判「本機無 PMC」,那是**擷取方式的錯**(PerfView 把計數器清單寫進它自己的 log/檔,
不是 stdout;先前用 `*>` / `Out-Null` 把它丟掉了)。用 `-LogFile` 正確擷取後(elevated),清單完整,**這台
Ryzen 7 3700X + Win11 對 ETW 開放下列 PMC**(`ListCpuCounters`):

```
Timer  TotalIssues  BranchInstructions  DcacheMisses  IcacheMisses  BranchMispredictions
IcacheIssues  DcacheAccesses  TotalCycles  CacheMisses  InstructionRetired
ICFetch  ICMiss  FRRetiredx86Instructions  FRRetiredBranches  FRRetiredBranchesMispredicted  DCAccess
```

即 **`IcacheMisses`(L1 指令快取 miss,正是要的)、`DcacheMisses`、`BranchMispredictions`、`InstructionRetired`/
`TotalCycles`(可算 IPC)** 全都有。

### 取得方式(系統管理員)
用 **`tools\profile_s1_perfview.bat`**(右鍵以系統管理員執行,或雙擊自我提權)。它:
1. `ListCpuCounters` → `temp\perf\listcounters.txt`(確認可用計數器)。
2. 採集 **`-CpuCounters:"IcacheMisses:65536,DcacheMisses:65536,BranchMispredictions:65536"` + `-ThreadTime`**
   跑 bench(1M hc)→ `temp\perf\s1_perf.etl.zip`。
3. PerfView GUI 開該 .etl,左側會有多個 stack 視圖:`CPU Stacks`(時間)、**`IcacheMisses Stacks`**、
   **`DcacheMisses Stacks`**、`BranchMispredictions Stacks` —— 各自看 per-method 分布。

### 5a. 真實測得結果(2026-05-29 捕獲,`pmc_all.csv` 513 MB,full_palette 600k hc)

> **版本註記**:此捕獲是 **NodeInfo 32B→16B 之前**的引擎(當時 `NodeInfos` 460 KB,非現在 230 KB)。
> 16B 改動會**降低** D-cache miss,故今日絕對值應更低;但「D 主導、I 可忽略」的**分布結論不變**。

每個計數器都用同一取樣間隔 1/65536,樣本數可直接相比。取 **`AprVisual.S1.dll` 本身**(占本行程
90.1% cycles,排除 coreclr/jit/kernel 啟動雜訊):

| 計數器 | 樣本數 | 每 1000 cycles | 約每幾 cycle 一次 |
|---|---|---|---|
| **DcacheMisses** | 46,348 | **50.4** | **~20 cycle** |
| BranchMispredictions | 5,577 | 6.06 | ~165 cycle |
| **IcacheMisses** | **705** | **0.77** | ~1,300 cycle |
| TotalCycles(基準) | 920,290 | — | — |

- **D-cache : I-cache = 65.7 : 1** —— 引擎熱碼大約**每 20 個 cycle 就吃一次 D-cache miss**,而 I-cache miss
  要 ~1300 cycle 才一次。**這就是「非 i-cache bound、記憶體延遲 bound」的硬體實證**,坐實 §2 的足跡論證。
- 次要瓶頸是 branch mispredict(~6/1000cyc),仍比 D-cache miss 少 8.3×。
- 全行程(含所有模組)版本:D 50.9 / I 1.44 / B 7.0(每 1000 cyc),D:I = 35.4:1 —— 結論一致。
- **直接釘死前述「把閒置 i-cache 借來用(codegen 拓樸進 i-cache)」的提案**(§7a):i-cache 之所以閒,正是因為
  它的 miss 比 D-cache 少 66×;一旦把 data 變成 code,就是把便宜的 D-data 換成會 miss 的 I-fetch,那 705 會爆掉。

**限制**:此捕獲的 managed 符號未解析(全歸到 `AprVisual.S1.dll!Unknown`),故無 **per-method** D-miss 分布;
要看是哪一行(`AddNodeToGroup` 的 NodeInfos/TransistorList vs `SetNodeState` vs `ComputeNodeGroup`),需重新
捕獲並合併 managed PDB,在 PerfView GUI 開 `DcacheMisses Stacks` + "Goto Source"。CPU 取樣(§1 `long_topN.txt`)
已知熱碼全 inline 在 `ProcessQueueInterp`。

**預期(原始,現已由 5a 證實)**:`IcacheMisses` 極低、`DcacheMisses` 主導(§A 的 NodeInfos/TransistorList 指標追逐)。

> 註:行程 token 是 UAC 過濾的非提權 token(`whoami /groups`:`BUILTIN\Administrators` = *deny only*),所以
> 一般工具呼叫拿不到 PMC;.bat 會自我提權(UAC 同意後)取得完整 token 再採集。

### 5b. 現引擎(16B)per-method 結果(2026-06-03 捕獲,full_palette 1M hc,`--extra-ram`)

修好 `tools\profile_s1_perfview.bat`(STEP 2 改跑 apphost `.exe`)後重新捕獲現引擎,並用自寫的
`tools\etl-analyze`(TraceEvent 3.2.2,從 CLR rundown 解析 managed 符號,無需 PDB)做 **per-method** 歸屬。
這次只抓 `DcacheMisses`/`IcacheMisses`/`BranchMispredictions`(未抓 `TotalCycles`,故無 per-1000-cycle;
但有了上一份缺的 per-method 分布)。本行程樣本(間隔 1/65536):

| 計數器 | 本行程樣本 | `ProcessQueueInterp` 占比 | 該方法樣本 |
|---|---|---|---|
| **DcacheMisses** | 43,230 | **96.9%** | 41,870 |
| BranchMispredictions | 5,875 | 89.6% | 5,263 |
| **IcacheMisses** | **224** | 23.2% | 52 |

- **整個引擎就是一個方法**:所有熱碼(`AddNodeToGroup`/`RecalcNode`/`SetNodeState`/`ComputeNodeGroup`)
  都 inline 進 `WireCore.ProcessQueueInterp`,它一個人吃掉 **96.9% 的 D-cache miss、89.6% 的 branch mispredict**。
  其後是非 inline 的呼叫點(`RecalcNode` 0.3% / `InvokeCallbacks` 0.1% / video handler / `Step`),都 <0.5%。
- **熱方法內 D-cache : I-cache = 41,870 : 52 ≈ 805 : 1**;全行程 193:1。I-cache miss 全行程才 224 個樣本,
  貼著雜訊底 → **熱碼指令完全常駐 L1i**。這比 5a 的 65.7:1 更極端地確認「i-cache 幾乎不 miss、純 D-cache/記憶體
  延遲 bound」,且是**現引擎、且歸屬到那一個方法**的證據。
- D : branch-mispredict ≈ 7.4:1 —— branch 仍是遙遠的第二名。
- 絕對量級:43,230 樣本 ×65,536 ≈ 2.83×10⁹ 次 D-cache miss / 1M hc ≈ **~2,830 次/half-cycle**;以每 half-cycle
  約 600 個節點變化估,**約每個節點變化吃 ~4.7 次 D-cache miss**(NodeInfo 記錄 + 數次 `NodeStates[gate]` 隨機 gather)。
- **再次釘死 §7a**:要拿來「借」的 i-cache,在熱方法裡 1M hc 只 miss 52 次;把拓樸 data 編成 code 只會把這 52 炸大。

> 量法限制:跨兩份捕獲(5a 600k hc/4 counters/無 --extra-ram vs 5b 1M hc/3 counters/--extra-ram)變因太多,
> **不宜**用兩者絕對值相減論「16B 省了多少」;5b 的價值是**現引擎的 per-method 分布 + D:I 比**,結論與 5a 同向且更強。
> 重現:`tools\profile_s1_perfview.bat`(系統管理員)→ `etlanalyze temp\perf\s1_perf.etl.zip AprVisual`。

### (參考)等效的手動 PerfView 指令(系統管理員):

**(1) 列計數器:**
```
tools\perfview\PerfView.exe -AcceptEula -LogFile:listcounters.txt ListCpuCounters
```

**(2a) 乾淨的 per-method CPU 取樣**(PerfView 的 JIT 符號解析會把 §3a dotnet-trace 拆不開的熱迴圈正確歸屬):
```
tools\perfview\PerfView.exe -AcceptEula -NoGui -ThreadTime run ^
  "dotnet src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll --benchmark AprVisualBenchMark\roms\full_palette.nes --bench-hc 5000000"
```
產生 `PerfViewData.etl.zip` → 用 PerfView GUI 開 → **CPU Stacks** → 選 dotnet 子行程 → 看 ProcessQueueInterp 內的
exclusive 分布。

**(2b) 加硬體計數器(本機已確認可用)**:
```
tools\perfview\PerfView.exe -AcceptEula -NoGui ^
  -CpuCounters:"IcacheMisses:65536,DcacheMisses:65536,BranchMispredictions:65536" -ThreadTime run ^
  "dotnet src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll --benchmark roms\full_palette.nes --bench-hc 1000000"
```
（在 `AprVisualBenchMark\` 下執行,讓 data dir 解析;`:65536` = 每 N 個事件取一樣本。`profile_s1_perfview.bat` 已封裝。)

> 更正:先前曾誤判「需 AMD uProf / 本機無 PMC」—— 那是擷取方式的錯(PerfView 把清單寫進自己的 log,不是 stdout)。
> 用 `-LogFile` 正確擷取後,Windows-ETW 在這台 Zen 2 上**確實**開放 `IcacheMisses` 等計數器,PerfView 即可取得。
> 結論(非 i-cache bound)仍由 §2 足跡定論,PMU 用來坐實。

---

## 6. 重現方式(本報告的指令)

```powershell
# 各 method 機器碼大小
$env:DOTNET_TieredCompilation="0"; $env:DOTNET_JitDisasmSummary="1"
dotnet src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll --benchmark <rom> --bench-hc 8000 > jitsizes.txt
#  → 解析 "WireCore:<method> ... code size=<N>"

# per-method CPU 取樣(EventPipe,無 admin;對 inline 熱迴圈歸屬有限)
dotnet-trace collect -o s1.nettrace -- dotnet <dll> --benchmark <rom> --bench-hc 5000000
dotnet-trace report s1.nettrace topN -n 25
```

> 註:本報告針對 S1(C#);`experiment/rust-s1`(Rust)的對應分析未含於此。引擎核心演算法與資料結構審查見
> `MD/S1/01_資料結構審查.md`;為何此抽象已到 ~80K hc/s 天花板見 `WebSite/ceiling.html`。

---

## 7. 兩個「借用閒置 i-cache」提案的評估與實測(2026-06-03)

觀察為真:熱迴圈僅 ~4.6 KB,L1i(32 KB)大量閒置,而 L1d 被 §A 隨機存取打爆。由此衍生兩個提案,結論都是**否**:

**(7a) codegen 把靜態拓樸塞進 i-cache** —— 把 `TransistorList`(節點 ID)烤成立即數的直線碼。
這**就是已實測的 oblivious / macro codegen**(見 `WebSite/study.html §5`):解譯 sweep 慢 45×、Roslyn 編譯版**慢 84×**,冒煙證據正是 **i-cache thrash**(~6000 節點展成 ~700 KB 直線碼 ≫ 32 KB L1i)。三個結構性原因:
- 活動**擴散**(`ceiling.html`:top-200 節點僅佔 21.6% BFS 工作)→ 沒有「小而熱」子集可挑,要覆蓋就得編幾千節點 → 爆 L1i。
- 搬錯目標:`TransistorList` 是**循序、可預取、便宜**,且 96% 節點根本不讀它(payload 已 inline);真正貴的是 `NodeStates[gate]`/`NodeInfos` 的**隨機 gather**,而那**必須留在 D-cache**(動態狀態)→ 搬掉便宜的、留下貴的、還多塞幾百 KB code = 淨負。
- per-node dispatch 打爛 BTB。
→ 不投入。i-cache 閒置是「小解譯器跑大資料」這個對的設計的**結果**,不是浪費。

**(7b) software prefetch(`Sse.Prefetch0`)** —— 在 `ProcessQueueInterp` dequeue 迴圈預取**下一個** dirty 節點的 `NodeInfo`(`RecalcList[i+1]`,230 KB 隨機存取陣列)。
唯一低風險、且先前沒測過的一招,故實測。bit-exact(checksum 不變)。
**interleaved-paired 20 輪@full_palette 300k:median −1.36% / trimmean −1.09% / prefetch 只贏 5/20 → 淨負,已還原。**
原因正是 small-walk 通病:`RecalcNode` 多走 O(1) fast-path(69.5% singleton),dequeue 迭代間工作太少 → 來不及藏延遲;`prefetcht0` 每輪一個 uop + cache 污染(下一個 `NodeInfo` 往往已駐留)反而蓋過。同 §A small-N anti-pattern 家族。

---

## 8. 攻「序列化 D-cache miss 相依鏈」—— MLP 探針 + 兩個外部建議評估(2026-06-03)

§5b 把瓶頸釘成「`ProcessQueueInterp` 內 96.9% D-miss、序列化 pointer-chase、latency-bound」。唯一的物理攻擊面是
**MLP**(讓多個獨立 dirty 節點的 `NodeInfos` miss 同時在飛)。逐項實測 / 評估,結論皆**否**:

**(8a) 距離調校 software prefetch —— MLP 探針**:§7b 失敗用的是 distance=1;這次拉開到 **D=8** 預取
`NodeInfos[RecalcList[i+8]]`,直接測「有沒有未被硬體榨取的 MLP」。bit-exact。
**interleaved-paired 20 輪:median −5.45% / TM −4.89% / 只贏 1/20**(比 D=1 更糟)。
→ **硬體 OoO 已經自己重疊好這些獨立 miss**(`RecalcList` 循序掃 → 位址可提前算出 → 多發 load 已在飛);軟體 prefetch 純粹多指令+污染+搶 load port。**這條序列鏈對隨機存取樣式已在硬體物理地板。** 連帶把「兩段式
branchless gather」降到極低勝率(其機制就是榨 MLP,而 MLP 已被硬體榨乾;且我們的分支可預測、不卡 OoO 視窗)。

**(8b) 外部建議「SoA 把 Flags 拆成獨立陣列」**:
- **已做**:csproj 已是 SoA split(hot struct + NodeConnections + NodeTlistGates),且分類熱位元組早已是獨立陣列
  `IsPureLogic[nn]`(14.7 KB,常駐 L1)。
- **前提被 8a 推翻**:該建議假設「讀 `Flags[nn]` 順便預取 nn+1..nn+63」——只在**循序存取**成立;我們是散亂
  `RecalcList[i]`,8a 已實測 prefetch-ahead = −5.45%。
- **拆出去反而更糟**:16B 打包(實測 −7% D-miss,見 `03_NodeInfo_32B_vs_16B`)就是要「一發 miss 取得整節點」;
  把 Flags 拆走 → group 節點變 `Flags[nn]` + `NodeInfos[nn]` 兩發 miss。→ un-do 勝利。

**(8c) 外部建議「函數指標查表消滅分支」**:
- **已實測死路**:math-algos 分支 Dispatcher = +5% 開銷 / 淨 −3.2%(見 `MD/impl/math-algos`)。
- **拆掉熱方法 inline**:96.9% 樣本在 `ProcessQueueInterp` 是因為一切 inline 成 4.6 KB 單一方法;
  `delegate* unmanaged` 是硬呼叫邊界,不可 inline → 毀掉這個 codegen 基礎。
- **可預測直接分支 → 不可預測間接呼叫**:cls 分支高度可預測(70% 同路)、branch 本就次要(D:branch≈8:1);
  per-node 散亂函數指標讓 BTB 無法預測,且 8-byte 指標讀比 1-byte `IsPureLogic` 更佔快取 → 反向收益。

> 綜合:兩個外部建議是「高效能 C/C++ 通用黑魔法」,抽象上沒錯,但都假設**循序存取**與**昂貴分支**——本引擎兩者皆否
> (散亂存取 + 便宜可預測分支 + 已 inline 單一熱方法)。`~80K hc/s` 是這顆隨機存取 switch-level 引擎在單核的
> **記憶體延遲物理地板**,由 PMU(§5b)、prefetch 探針(8a)、與既有 dead-ends 三方共同定論。
