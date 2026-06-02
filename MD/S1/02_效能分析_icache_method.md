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
3. **真正的 bound 是資料 / 記憶體延遲**(§A 的 `NodeInfos` 460 KB、`TransistorList` 225 KB 的指標追逐 + 不規則
   分支),不是指令快取。per-event ≈ **~82 CPU cycle/recalc**,卡在 load-use latency + branch mispredict
   (見 `WebSite/ceiling.html`)。
4. **真實硬體 i-cache miss 計數在這台機器上拿不到 —— 已實測確認(非權限問題,見 §5)**:即使經 UAC 提權,
   PerfView `ListCpuCounters` 回報這台 Ryzen 7 3700X + Windows 11 對 ETW 開放的 **PMC 計數器 = 0**(AMD-on-Windows
   的 OS/驅動限制)。唯一路徑是 **AMD uProf**(本機未裝)。但 i-cache 的結論**靠 §2 指令足跡就已定論**(熱迴圈
   4.6 KB ≪ 32 KB L1i → 非 i-cache bound),PMU 只會再確認。

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
- **真正瓶頸**:§A 資料 —— `NodeInfos`(460 KB)單獨就 > L2(512 KB),`TransistorList`(225 KB);雖然每半週期
  working set(~604 活節點 / 平均群 2.25)遠小於全表,但 BFS 是**指標追逐 + 不規則分支**,~82 cycle/recalc 卡在
  load-use latency 與 branch mispredict —— 這才是該攻的維度(且 ceiling.html 已論證在此抽象下已到天花板)。
- ∴ **對 hc/s 而言,i-cache 不是問題;不需要為 i-cache 做任何事。**

---

## 5. 真實硬體 i-cache miss / IPC —— 實測結論:**此機器無法取得(非權限問題)**

**已實際嘗試(含 UAC 提權)**,結論明確:

- 行程 token 是 **UAC 過濾的非提權 token**(`whoami /groups`:`BUILTIN\Administrators` = *"Group used for deny
  only"*);帳號雖是 admin,行程沒提權。經 `Start-Process -Verb RunAs` UAC 同意後**成功提權**執行 PerfView。
- 但提權後 `PerfView ListCpuCounters` 回報 **「Cpu Counters available on machine.」後接空清單** —— **這台
  Ryzen 7 3700X + Windows 11 對 ETW 開放的 PMC 計數器數量 = 0**。
- ∴ **無論是否提權,都無法經 PerfView / Windows-ETW 取得硬體 i-cache miss(或任何 PMU)計數** —— 這是
  **AMD-on-Windows 的 OS/驅動限制**(Windows 內建 PMC 對 AMD 支援有限,常回空清單),不是權限問題。
- **唯一路徑 = AMD uProf**(自帶 PMU 驅動,繞過 Windows ETW-PMC 抽象,能讀 L1i refill / front-end stall / IPC)。
  本機未安裝;若要原始計數,需安裝 uProf 後對同一條 bench 命令做 profile。
- **但結論不受影響**:依 §2/§4 的**指令足跡**(熱迴圈 4.6 KB ≪ 32 KB L1i),S1 **非 i-cache bound** 已是定論;
  PMU 量測只會再確認,不會推翻。

### (參考)若日後在「有開放 PMC 的機器」或裝了 uProf:
PerfView(系統管理員)路徑:

**(1) 先看這台 Windows 對 Zen 2 開放哪些硬體計數器**(L1i-miss 在 AMD-on-Windows 不一定有):
```
tools\perfview\PerfView.exe -AcceptEula ListCpuCounters
```

**(2a) 乾淨的 per-method CPU 取樣**(PerfView 的 JIT 符號解析會把 §3a dotnet-trace 拆不開的熱迴圈正確歸屬):
```
tools\perfview\PerfView.exe -AcceptEula -NoGui -ThreadTime run ^
  "dotnet src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll --benchmark AprVisualBenchMark\roms\full_palette.nes --bench-hc 5000000"
```
產生 `PerfViewData.etl.zip` → 用 PerfView GUI 開 → **CPU Stacks** → 選 dotnet 子行程 → 看 ProcessQueueInterp 內的
exclusive 分布。

**(2b) 若 (1) 列出了 instruction-cache / I-cache-refill 之類的計數器**,加 `-CpuCounters`:
```
tools\perfview\PerfView.exe -AcceptEula -NoGui -CpuCounters:"<counter-name>:10000" -ThreadTime run "dotnet ... --bench-hc 5000000"
```
（`<counter-name>` 用 (1) 列出的名字;`:10000` 是每 N 事件取一樣本。)

**誠實提醒**:Windows 的 PMC 抽象在 AMD 上開放的計數器有限,**L1 指令快取 miss 很可能不在清單上**。若要完整 PMU
(L1i refill、front-end stall、IPC),**AMD uProf** 才是這顆 Zen 2 的對應工具(本機未安裝;可另行安裝後對同一條
bench 命令做 profile)。不過依 §2/§4 的足跡分析,**結論(非 i-cache bound)不依賴這個量測**。

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
