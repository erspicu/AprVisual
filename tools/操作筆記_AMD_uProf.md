# AMD uProf 操作筆記（Zen2 / Windows / .NET）— 以實機 help 為準

本機已裝 uProf **5.3.521**,補 xperf/ETW 在 Zen2 抓不到 PMC 的洞。語法**以 `AMDuProfCLI <cmd> --help` 實際輸出為準**;
Gemini 給的部分是舊版/錯規格(見下方「Gemini 訂正」)。本筆記已用實機 help dump 校過。

## 安裝 / 環境（2026-06-19 完成）
- CLI:`C:\Program Files\AMD\AMDuProf\bin\AMDuProfCLI.exe`(版本 5.3.521)
- 驅動(自動啟動、Running):`AMDCpuProfiler`、`AMDPowerProfiler`;服務 `AMDProfilerLoadService`
- CPU:Ryzen 7 3700X = Zen2 / Family 0x17 Model 0x71(IBS 支援)
- 安裝程式 = InstallShield,靜默:`AMDuProf-5.3.521.exe /S /v"/qn /norestart"`(大寫 S;錯下 `/install` 會跳說明視窗 exit 1203)

## 三個實機踩到的雷
1. **`--help` 的 `Press any key` pager 直接讀 CONIN$** → 自動化 shell 會卡(redirect/pipe/stdin 都**無法**繞過,實測 timeout 124)。
   取語法:看本筆記,或在**互動式終端機**跑 `--help` 自己按鍵翻頁(可配 `Start-Transcript` 錄到檔)。collect/report/info **執行**時不分頁,可自動化。
2. **啟動程式路徑有空白會被 uProf 自己切斷**(`C:\Program Files\dotnet\dotnet.exe` → 它在 `C:\Program` 斷)。
   → 用 8.3 短路徑:`C:\PROGRA~1\dotnet\dotnet.exe`(`(New-Object -ComObject Scripting.FileSystemObject).GetFile($p).ShortPath`)。
3. **PMU 硬體鎖 `0x80070021`**(IOCTL_START_PROFILER):前一個 collect 被中途 kill 沒釋放鎖。
   → 修:`Get-Process AMDuProfCLI | Kill` + `Restart-Service AMDProfilerLoadService -Force`,再重跑。

## 指令總攬（語法 = 實機 help）
頂層:`AMDuProfCLI [-v|--version] [--help] COMMAND [<opts>] <PROGRAM> [<ARGS>]`
COMMAND:`collect` / `report` / `translate` / `profile` / `info` / `timechart` / `compare,diff`

### collect（採樣;PROGRAM/ARGS 放最後,positional)
- `-o, --output-dir <dir>`(選用;dir 存在→建 auto-named 子目錄,不存在→直接存)
- `--config <name>`:預定義組態。用 `info --list collect-configs` 看清單。已知:`tbp`(time-based)、`assess`(Assess Performance,EBP 一組 PMC)、`hotspots`、`ibs`。可多個 `--config` 疊加。
- `-e, --event <EVENT>`:自訂事件,逗號 key=value:
  - `event=<timer|ibs-fetch|ibs-op>` 或 `<PMU-event>`(如 `pmcx76`、`RETIRED_INST`)
  - `interval=<n>`(預設:ibs/PMU=250000;timer=1ms)、`umask=`、`cmask=`、`user=<0|1>`、`os=<0|1>`、`inv=`、`call-graph`
  - IBS 專屬:`loadstore`、`ibsop-count-control=<0|1>`(0=cycle,1=dispatch)、`ibsop-ldlat`/`ibsop-l3miss`(**Zen5+ 限定,我們 Zen2 不能用**)
  - 可多個 `-e`
- `-a, --system-wide`(SWP)、`-p, --pid <…>`(attach)、`-c, --cpu <0-3>`(限核,IBS SWP 資料量大時用)
- `-g`(callstack 預設值)/ `--call-graph <I:D:S:F>` / `--call-graph-mode fp|fpo`
- `-d, --duration <秒>`、`--interval <n>`(覆寫所有事件間隔)、`--no-inherit`、`-b/--terminate`、`--start-paused`、`--env-var <k=v;…>`、`--limit-data <MB>`

範例(實機 help):
```
collect --config tbp -o OUT app.exe                              # time-based
collect --config assess -o OUT -d 10 app.exe                     # Assess(EBP 一組 PMC)
collect --config ibs -a -o OUT app.exe                           # 預定義 IBS(SWP)
collect -e event=ibs-op,interval=50000 -o OUT app.exe            # IBS OP 自訂
collect -e event=pmcx76,interval=250000 -e event=pmcxc0,interval=250000,call-graph -o OUT app.exe
collect -e event=RETIRED_INST,interval=250000 -e event=L1_DC_ACCESSES_ALL,user=1,os=0,interval=250000 -o OUT app.exe
```

### report（產報告;**輸出旗標是 `--report-output`,不是 -o**)
- `-i, --input-dir <session-dir>`
- `--report-output <path>`:`.csv` 副檔名→當檔案路徑;否則→當目錄(用預設檔名)。或 `--stdout` 印到 stdout。
  (不帶任何輸出旗標時,預設在 session 目錄產生 `report.csv`。)
- `--detail`(詳細)、`--disasm` / `--disasm-only` / `--disasm-full`(**逐指令組語**,需 --detail,會自動帶上)、`--disasm-style <att|intel>`(預設 intel)
- `-s, --sort-by event=<ibs-op|ibs-fetch|timer|pmcxNN>[,user=,os=]`
- `--view <view-config>`(只報該 view 的事件/指標;`info --list view-configs`,如 `ipc_assess`、`ibs_op_overview`)
- `--group-by <process|module|thread>`(預設 process)、`-p <pid>`、`-g`(印 callstack)
- `--cutoff <n>`(限制 proc/mod/thread/func 筆數,預設 10;`--cutoff 0`=全部)
- `--show-percentage` / `--show-event-count` / `--show-sample-count`(預設開)
- `--imix [--imix-group-by module|thread|function]`(指令 mix)
- `--ascii <ibsop-event-dump|ibsfetch-event-dump>`(IBS 原始樣本 ASCII dump)
- 符號:`--symbol-path` / `--symbol-server <URL>` / `--symbol-cache-dir`、`--bin-path`、`--src-path`
- `--remove-raw-files`(清原始檔回收空間)、`--export-session`、`--retranslate`

範例(實機 help):
```
report -i SESSION                                                # 預設報告 -> report.csv
report -s event=ibs-op -i SESSION                               # 依 IBS op 排序
report --view ipc_assess -i SESSION                            # IPC/CPI view
report -i SESSION --detail --disasm-only --disasm-style intel --report-output OUT.csv   # 逐指令組語
```

### info（不卡 pager,可自動化拿事件/組態清單）
```
info --system
info --list collect-configs        # 有哪些 --config
info --list predefined-events      # -e event=<name> 可用的預定義事件
info --list pmu-events             # raw PMU 事件
info --list view-configs           # report --view 可用
info --collect-config assess_ext   # 某組態細節
info --pmu-event pmcx76            # 某 PMU 事件細節
info --view-config ibs_op_overview
```

## 已驗證可用的「baseline」流程(EBP / `--config assess`)
```powershell
$dotnet=(New-Object -ComObject Scripting.FileSystemObject).GetFile("C:\Program Files\dotnet\dotnet.exe").ShortPath
$env:DOTNET_TieredCompilation="0"
& $cli collect --config assess -o C:\ai_project\AprVisual\temp\uprof_X $dotnet <dll> <benchmark-args>
& $cli report -i C:\ai_project\AprVisual\temp\uprof_X      # -> temp\uprof_X\report.csv
```
- **.NET JIT 符號會自動解析**(不需任何旗標)——`report.csv` 直接看到 `AprVisual.Sim.WireCore::ProcessQueue`(模組 `…aprvisual.s1.dll.jit`)。已實測:ProcessQueue 佔 .jit 模組 ~88% 樣本。
- assess 的 PMC 事件(實名):CYCLES_NOT_IN_HALT(PMCx076)、RETIRED_INST(PMCx0C0)、RETIRED_BR_INST(PMCx0C2)、RETIRED_BR_INST_MISP(PMCx0C3)、L1_DC_ACCESSES_ALL(PMCx029)、L1_DEMAND_DC_REFILLS_ALL(PMCx043)、L2_CACHE_ACCESS_FROM_L1_DC_MISS(PMCx060)、MISALIGNED_LOADS(PMCx047)。
- 衍生:IPC、CPI、%RETIRED_BR_INST_MISP、L1_DC_MISSES(PTI)、%L1_DC_MISSES …
- ⚠️ **取樣間隔太大時衍生比例會被「間隔比」鎖死成假影**(例:IPC 全 1.0、%L1miss 全 10.0)。要可信數字 → 降 interval 或拉長 run(更多樣本)。

## IBS 能拿到的指標(我們真正要的)
IBS report 欄位含:**`IBS_DC_MISS_LAT` / `AVE_DC_MISS_LAT`(DC miss 平均 load 延遲,cycle)**、`IBS_TAG_TO_RET`/`AVE_TAG_TO_RET`、
`IBS_LOAD_DC_HIT/MISS`、`IBS_LOAD_L2_HIT/MISS`、`IBS_BANK_CONF_LOAD`、`IBS_FORWARDED`/`IBS_STLF_CANCELLED`、DTLB hit/miss、
資料來源(L3/local-cache/local-DRAM…)。→ 對準熱路徑那條 `movzx [NodeStates+gate]` 看 load 延遲。

## ⚠️ 未解 TODO:raw IBS-op 報告是空的
`collect -e event=ibs-op,interval=50000`(無 -a)跑完 exit=0,但 **session 沒產生 .prd、report 各表全空**(0 樣本可歸因)。
推測:raw IBS 是 SWP 但沒帶起該程序的 JIT 符號/歸因。**下次先試 `--config ibs`**(預定義,應自帶符號處理;help 範例 `collect --config ibs -a -o OUT app`),
或 `report --group-by process -p <dotnet-pid>`,或 `report --ascii ibsop-event-dump` 看原始樣本有沒有進來。EBP(assess)路線目前是好的。

## Gemini 訂正(它撈到舊規格,以下是錯的,別再用)
- ❌ `--ibs-op-maxcnt 100000` → 實際 **`-e event=ibs-op,interval=N`**
- ❌ `--jnc`(.NET 符號旗標)→ **不存在**;.NET 符號 EBP 下**自動**解析
- ❌ report `-o <dir>` → 實際 **`--report-output <path>`**(或 `--stdout`)
- ❌ 「`--config assess` = IBS」→ assess 是 **EBP 一組 PMC**,不是 IBS;IBS 用 `-e event=ibs-op` 或 `--config ibs`
- ❌ 「redirect/pipe 可繞過 `--help` pager(isatty)」→ 實測**不行**(讀 CONIN$)
- ✅ 對的:`DOTNET_TieredCompilation=0` 對短跑有助於符號 rundown;`report --disasm` 看逐指令

來源:實機 `AMDuProfCLI --help` dump(temp/uprof_help_dump.txt)+ 實測。事件碼/組態以 `info --list …` 為準。

---

# Baseline 量測（2026-06-19,full_palette 1M hc,interval=50000)

## 能力盤點(實測後定論)
| 資訊 | 狀態 |
|---|---|
| 逐指令 **cycle-樣本密度**(哪條最熱) | ✅ 可信(但有 **skid**,見下) |
| 哪個函式主導 | ✅ ProcessQueue ≈ 88% 的 `.jit` 模組樣本 |
| 相對 cycle A/B(同指令/同函式樣本數比較) | ✅ **抗熱噪**(cycle-based,非 wall-clock 秒) |
| IPC / CPI / %L1-miss / 任何「比值」 | ❌ **假影** — cycles 與 retired 同 interval → 樣本數被逼相等 → 比值恆 ≈1.0(別信) |
| IBS 精確 load 延遲(`AVE_DC_MISS_LAT`) | ❌ **IBS 不歸因 .NET JIT 程式碼**(raw 與 `--config ibs` 都空表、無 .prd) |
| 原始碼行 | ❌ JIT 無 PDB("DEBUG INFO NOT FOUND");組語級夠用,對照自家 disasm |

> **skid**:AMD EBP 取樣會落在「卡住的 load」**之後一兩條**指令上。所以熱圖頂端常是一堆 `jnz/jz` 分支 —— 真正的成本是**它前一條的 load**。讀熱圖要往前看一兩條。(IBS 才精確無 skid,但 IBS 不吃 JIT。)

## Baseline 結果:NodeStates[gate] 是 #1 cycle 成本(量測背書)
ProcessQueue 逐指令 top(cycle-樣本):
- **0x88 `jnz`(237 樣本)= 消費 `cmp byte [NodeStates+gate]` 的分支** → 即相依 load `NodeStates[gate]`,**量測證實第一名**(之前只有 disasm 結構推論)。
- 其餘多為 dispatch/BFS 分支(skid)+ flags `or` 累積 + nn 索引擴展。
- 完整熱圖:`temp/base_disasm.csv`(report --disasm-only 產出)。

## 可信的量測流程(取代熱機 wall-clock)
```powershell
$dotnet=(New-Object -ComObject Scripting.FileSystemObject).GetFile("C:\Program Files\dotnet\dotnet.exe").ShortPath
$env:DOTNET_TieredCompilation="0"
# 1) collect(4 事件 @ 50000;PMU 鎖卡住先 Restart-Service AMDProfilerLoadService -Force)
& $cli collect `
  -e event=CYCLES_NOT_IN_HALT,interval=50000 -e event=RETIRED_INST,interval=50000 `
  -e event=L1_DC_ACCESSES_ALL,interval=50000 -e event=L2_CACHE_ACCESS_FROM_L1_DC_MISS,interval=50000 `
  -o temp\uprof_X $dotnet <dll> --benchmark <rom> --bench-hc 1000000 --extra-ram --system-def-dir <sd>
# 2) 逐指令熱圖
& $cli report -i temp\uprof_X --detail --disasm-only --disasm-style intel --report-output temp\X_disasm.csv
```
**A/B 判讀(記憶體軸實驗)**:改一版 → 同樣 collect → 比 **(a) 0x88 那條(NodeStates[gate] 消費者)的 cycle-樣本數**、**(b) ProcessQueue 總 cycle-樣本數**。兩者都降 = 真的省到 cycle(抗熱噪);只看「比值欄」會被假影騙。
- 樣本數越多越穩:目前 1M hc + interval 50000 → ProcessQueue ~2775 樣本(夠函式級;逐指令熱者數十~數百)。要更穩可降 interval 或拉長 run。

## 還想要 IBS 精確延遲的話(代價大,非必要)
NativeAOT 編 S1(熱碼變真 PE 模組,IBS 才歸因得到;但 −5.5% 且是不同產物,量完丟)/ 搬熱迴圈到原生 C harness / `report --ascii ibsop-event-dump` 撈原始 IP 手動對應 JIT 位址區間。

## ⚠️ IBS 在這台採 0 樣本 → x2APIC 假設 + 修法(2026-06-20)
**症狀**:`collect -e event=ibs-op,...`(single-app / `-a` / `--config ibs` 全試過)exit 0 但**採 0 樣本**:raw `*.prd` ~600B(只 header)、report 的 FUNCTION/PROCESS 資料列全空、`report --ascii ibsop-event-dump` 只有表頭 0 列。**6/19 和 6/20 兩次都一樣**(非一次性)。
**關鍵對比**:**PMC/EBP 取樣正常**(`--config assess` / `-e event=CYCLES_NOT_IN_HALT,RETIRED_INST,L1_DC_ACCESSES_ALL,...` → disasm 報告有 221-224 筆真實逐指令樣本)。→ 是 **IBS 專屬失效**,不是 Zen2 不支援(Zen2 Family 17h 有 IBS)、不是 VBS(VBS 已關)、不是全 profiling 壞。
**Gemini 診斷(高度可能)**:PMC 走標準 LVT PMI;**IBS 走另一個 Extended-Interrupt LVT(EILVT)**。Windows 預設 **x2APIC** 模式下,uProf driver 若無法在 x2APIC 程式化 EILVT,IBS arm 了但中斷永不觸發 → 正好 0 樣本。本機 `bcdedit /enum {current}` 原本無 APIC 設定 = 預設 x2APIC,吻合。
**修法(2026-06-20 已套用,待重開驗證)**:
```
bcdedit /set x2apicpolicy Disable
bcdedit /set uselegacyapicmode Yes   # 重開機後生效;還原 = bcdedit /deletevalue 兩者
```
8 核/16 緒用 xAPIC 足夠(x2APIC 只 >255 邏輯核才需要),可逆無功能風險。
**後備**(若無效):BIOS SVM off / IOMMU=Disabled / Local APIC Mode=Compatibility;關 Windows 核心隔離的「易受攻擊驅動程式封鎖清單」;清 AMDCpuProfiler.sys 降版 uProf 4.2。
**若 IBS 終究救不回**:AMD 無其他 data-address 來源(IBS 唯一);退路 = PMC 找 L1-miss 的 RIP → 反組譯看 `[reg+offset]` → 由 struct layout 反推 array。per-array 也可用 footprint+perf-stat+存取頻率推論(見 MD/note/2026-06-20-L-cache-miss熱點分析.md)。
