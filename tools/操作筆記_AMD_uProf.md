# AMD uProf 操作筆記（Zen2 / Windows / .NET）

本機已安裝,用來補 xperf/ETW 在 Zen2 抓不到 PMC 的洞。目的:用 **IBS** 把記憶體 stall cycle
歸因到「特定指令/原始碼行」(例如熱路徑那條 `movzx reg, byte ptr [NodeStates + gate]` 相依 load)。

## 安裝資訊（已完成 2026-06-19）
- 版本:**5.3.521**;CLI:`C:\Program Files\AMD\AMDuProf\bin\AMDuProfCLI.exe`
- 驅動(已 Running):`AMDCpuProfiler`、`AMDPowerProfiler`;服務 `AMDProfilerLoadService`
- CPU:Ryzen 7 3700X = **Zen2 / Family 17h**(IBS 支援)
- 安裝程式類型 = **InstallShield**(不是 WiX/Burn)。靜默安裝語法:
  ```
  AMDuProf-5.3.521.exe /S /v"/qn /norestart"      # 大寫 S;/v 後面是傳給 msiexec 的參數
  ```
  (錯下 `/install`/`/layout` 會跳 InstallShield 說明視窗,exit 1203。)

## ⚠️ 重要陷阱:CLI 的 `--help` pager 會卡自動化 shell
- `AMDuProfCLI <cmd> --help` 會印 `Press any key...` 分頁,**直接讀主控台 CONIN$**。
- **實測**:導向檔案(`> f.txt`)、stdin 導 /dev/null、pipe 都**無法**繞過(會卡到 timeout 124)。
  → Gemini 宣稱「redirect/pipe 可繞過(isatty)」對 5.3.521 **不成立**。
- 取得語法的可靠方式:① 看本筆記;② AMD 線上文件 docs.amd.com 57368;③ 真的在互動式
  終端機手動跑 `--help` 自己按鍵。**自動化 shell 不要跑 `--help`。**
- 注意:這只影響 `--help`。真正 `collect`/`report`/`info` 執行時不分頁,可正常自動化。

## 前提
- IBS / PMU / ETW(.NET 符號)採樣**都需要系統管理員權限**的終端機。
- .NET 短程式(5–10s):先設 `DOTNET_TieredCompilation=0` 強制 RyuJIT 直接最佳化編譯,
  避免 tiered 重編譯讓 ETW 符號 rundown 抓不到方法。

## 1) 看支援的設定 / 事件
```powershell
AMDuProfCLI.exe info --list-profiles      # 預定義 config:assess / ebp / tbp / timechart ...
AMDuProfCLI.exe info --list-events        # Zen2(Family 17h)所有 raw PMU 事件
```
（這兩個是「執行」不是 `--help`,不會卡 pager。若仍分頁,改用 GUI 或線上文件。）

## 2) IBS 採樣(我們的主力)—— config `assess`（含 IBS Op,帶 load 延遲/資料來源）
```powershell
$APP = "dotnet C:\path\AprVisual.S1.dll --benchmark rom.nes --bench-hc 400000 --extra-ram --system-def-dir DIR"
AMDuProfCLI.exe collect --config assess --ibs-op-maxcnt 100000 --jnc --launch $APP -o C:\uProf_IBS
```
- `--config assess` = 開 IBS Op + Fetch 採樣
- `--ibs-op-maxcnt 100000` = IBS Op 取樣間隔(越小樣本越多;100k 是 5–10s 跑高解析的好起點)
- `--jnc` = 開 .NET/CoreCLR ETW provider 解析 JIT 符號（**必加**,否則只看到 Unknown module）
- `--launch` = 只 profile 啟動的程序 + 子程序
- `-o` = 輸出 session 目錄

## 3) Event-based(EBP）—— 指定 raw Zen2 事件
```powershell
AMDuProfCLI.exe collect --config ebp `
  --event event=0x76,umask=0x00,user=1,os=0 `   # CPU cycles (not halted)
  --event event=0xc0,umask=0x00,user=1,os=0 `   # retired instructions
  --event event=0x41,umask=0x00,user=1,os=0 `   # L1 DC misses  (※待 info --list-events 核對)
  --event event=0x87,umask=0x02,user=1,os=0 `   # dispatch/backend stall  (※待核對)
  --jnc --launch $APP -o C:\uProf_EBP
```
> ⚠️ 事件碼(0x76/0xc0/0x41/0x87)是 Gemini 給的 Family 17h 值,**用前先 `info --list-events` 核對**;
> 0xc0=retired instr、0x76=cycles 大致可信,L1-miss/stall 那兩個要確認。

## 4) .NET 符號
- 旗標 = **`--jnc`**(已含在上面)。
- 短跑務必 `DOTNET_TieredCompilation=0`(見前提)。
- 我們 benchmark 本來就常用 `DOTNET_TieredCompilation=0`(配 JitDisasm),剛好一致。

## 5) 產報告（從 session 的 .cxperf 檔）
```powershell
# A. 函式級熱點(CSV)
AMDuProfCLI.exe report -i C:\uProf_IBS\AMDuProf-assess-*.cxperf --report detail --format csv -o C:\uProf_IBS\Reports
# B. 指令/組語級(看到那條 movzx)—— 用 --func 過濾熱方法,免得檔案爆大
AMDuProfCLI.exe report -i C:\uProf_IBS\AMDuProf-assess-*.cxperf --report asm --func "ProcessQueue" --format csv -o C:\uProf_IBS\Reports
```

## 6) 讀 IBS:找「最高延遲的 load」
ASM CSV 報告裡,在 disassembly 旁會有 IBS 欄位。對準那條 `movzx reg, byte ptr [reg+reg]`(=NodeStates[gate]):
- **`IbsOpDcLoadLat`**(Data Cache Load Latency)= **關鍵指標**:該 load 從 dispatch 到資料回來的平均 cycle。
  L1 還高(>4–5)就是相依 stall / bank conflict。
- `IbsOpDcAcc` = 存取 DC 次數;`IbsOpDcMiss` = miss L1 次數(我們預期 ~0,因 L1-resident)。
- miss 時 `Data Source` / `IbsOpDcMissLat` 顯示來源(L2/L3/DRAM)與 miss 延遲。

## 我們的工作流(把「記憶體軸」變可測量)
1. 系統管理員終端機,`DOTNET_TieredCompilation=0`。
2. `collect --config assess --jnc --launch <benchmark>`。
3. `report --report asm --func ProcessQueue --format csv`。
4. 看 `IbsOpDcLoadLat` 對準 `NodeStates[gate]` 那條,量出真實 stall。
5. 改了之後(例如 Gemini 的設計 2 跨-pop 預載)再量一次,比 load 延遲 / backend stall 有沒有降
   —— 這比熱機 wall-clock A/B 可信得多。

來源:Gemini 3.1-pro(temp/gemini_uprof_answer.txt)+ 本機實測修正(pager 那條)。
事件碼與確切旗標以 `info --list-events` / docs.amd.com 57368 為準。
