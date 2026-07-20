# AprNes:open-bus 溫度相關衰減(已實作)

2026-07-20 在使用者的 AprNes(master-clock 版模擬器,獨立 repo `github.com/erspicu/AprNes`,
在 AprVisual 內位於 `AprNesRef/`、gitignored)實作 **open-bus 溫度相關放電特性**,把 S1a 深入專文 #3
(NES open-bus 溫度計)的物理搬進一個真的、可玩的 master-clock 引擎。

## 做了什麼
把 PPU I/O open-bus 閂鎖(`openbus` 欄位 —— $2002 低 5 位 / 寫入專用 PPU 暫存器讀值)的衰減,
從舊的 `open_bus_decay_timer` 計數器改成 **溫度相關的懶惰時戳模型**:
- 物理:定電流 junction 漏電 = **線性**放電,漏電流 Arrhenius:`I_leak ∝ exp(-Ea/kT)`,
  存活時間 `t(T) = t0·exp(Ea/k·(1/T − 1/Tref))`,Ea=0.56eV。**25°C → 600ms**。
- 只有 PPU 閂鎖衰減;CPU 外部匯流排 `cpubus` 每次取指都重驅動、永不可觀測衰減 ——
  這正是溫度計必須讀 $2002 而非 CPU bus 的原因。

## 舊實作的問題(被取代)
`open_bus_decay_timer` 是 77777-tick 倒數,**只在渲染 dot 遞減**(ppu_dispatch.cs 7 處)→ 約 14.5ms
就衰減完、且 vblank 期間不動 = 太快且不均。新模型用 `NowDots()`(frame_count/scanline/ppu_cycles_x
算的單調 dot 時鐘,跨 vblank,零熱路徑成本)。

## 版控正式版 = `tools/aprnes`(AprVisual 追蹤,已推 commit `463f33c`)
真正被 AprVisual git 管理當工具的是 **`tools/aprnes/`**(141 檔,非 gitignored;`AprNesRef/` 才是
gitignored 的完整工作副本)。相同的 open-bus 改寫已套到 `tools/aprnes/`(逐行與 AprNesRef 版一致,
IO/Main/ppu_dispatch 三檔位元組相同,PPU 僅差一行既有 dmcTrace 診斷),並**移除 unittest 相依**讓它可獨立建置:
- csproj 拔掉 5 個 `..\unittest\NesTestFramework\*.cs` / `AprNesAdapter.cs` 的 `<Compile Include>`;
- `TestRunner.cs` 移除唯一消費者 —— 選用的 `--use-framework` demo(`TryFrameworkPath` + `using NesTestFramework;`);
  完整 `TestRunnerCore` 路徑(--rom/--test/--benchmark/screenshot)不動。
- 建置:net48 x64,需先 NuGet restore(`System.Runtime.CompilerServices.Unsafe` 是 PackageReference,
  這份快照沒 restore 過);之後 `MSBuild /t:Restore` + build 乾淨。ppu_open_bus + 5/5 PPU 回歸綠。

## 程式位置(AprNesRef 本機鏡像 commit `f1b949c`,**未 push**;正式版見上方 tools/aprnes)
- `AprNes/NesCore/PPU.cs`:新增 `OpenBusTempCelsius`(public static double = 25.0,**溫度旋鈕**)、
  `OB_Ea/OB_k/OB_Tref/OB_BaseDecaySec`、`NowDots()`、`RecomputeOpenBusDecay()`、
  `DriveOpenBus(v)`(驅動閂鎖 + 蓋時戳)、`OpenBusDecayed()`(讀時套衰減,過期回 0)。
- 驅動點(改成 `DriveOpenBus`):ppu_w_2000/2001/2003/2004/2005/2006/2007、ppu_r_2004、ppu_r_2007、
  IO.cs $2002 write。讀取點(套 `OpenBusDecayed`):ppu_r_2002 低 5 位、ppu_r_2007 palette 高 2 位、
  IO.cs 寫入專用暫存器讀。$2002 **讀不刷新**(低 5 位續衰減 —— 溫度計的關鍵)。
- `ppu_dispatch.cs`:移除 7 個舊遞減點。`Main.cs`:reset 改初始化新狀態。

## 溫度旋鈕對照(已用 Python 驗算,程式數學一致)
| 溫度 | 衰減週期 | | 溫度 | 衰減週期 |
|---|---|---|---|---|
| −20°C | 28.9 s | | 30°C | 419 ms |
| 0°C | 4.41 s | | 40°C | 211 ms |
| 20°C | 870 ms | | 50°C | 111 ms |
| **25°C** | **600 ms**(=3,221,591 dots=36.1 幀) | | | |

「以後可調整或代入」= 改 `PPU.OpenBusTempCelsius` 即可(或接 UI/config/CLI)。

## 驗證
- `ppu_open_bus.nes`(本身就測衰減)+ `ppu_read_buffer` **PASS**。
- 20/20 PPU 回歸(vbl_nmi 全套、sprite_hit 全套)全綠。net48 x64 build 乾淨(EXITCODE=0)。

## 溫度計 test ROM + headless CLI(commit `3c82300`,已推)
`tools/thermo_rom/` —— 可跑的溫度計 demo(WLA-DX 組譯,`tools/wla-dx/wla-6502.exe`):
- ROM:$FF→$2003 prime open-bus latch → tight-poll $2002 數迴圈到低 5 位衰減(24-bit 計數 @ zeropage $10-$12)
  → 螢幕顯示 6 位十六進位(CHR 內建 0-F 字型 @ tile $30-$3F)。越熱越快衰減=數字越小。
- `build.py`:組 PRG + 生成 8KB CHR 字型 + iNES 包裝 → `thermo.nes`。
- **AprNes CLI 新增兩旗標**(`tools/aprnes/NesCore/TestRunnerCore.cs`):
  `--openbus-temp <°C>`(設 `NesCore.OpenBusTempCelsius`)、`--dump-mem <hexaddr>`(收尾印 8 byte + u24 LE)。
  截圖:`--timed-screenshots "<path>:<秒>"`(frame 120 ≈ 2.0s;冷溫量測慢、frame 120 可能還沒量完=黑屏,要截更晚的幀)。
- **驗證**(截圖 + dump,實測 vs Arrhenius 預測比值差幾 %):
  0°C=437249(06AC01)·10°C=187803·20°C=85381·25°C=58950(00E646)·30°C=40778·40°C=19317·50°C=9391(0024AF)。
  0–50°C 跨 **46 倍**、單調、對得上物理。截圖 `tools/thermo_rom/shot_*.png`。
- ⚠️ Git Bash(MSYS)會把含 `:` 的 `--timed-screenshots` 路徑 mangle → 用 **PowerShell** 跑截圖那類指令。

## 攝氏顯示升級(commit `25efe0f`)+ 深入專文 #4(commit `476ee7c`)
- ROM 改成顯示 `NN.N DEGREE CELSIUS`(不再是 hex)。count→°C 反推**不用 6502 乘除法/浮點**:
  build.py 從逐度實測 `counts_by_degree.json` 產生 0.1°C 查表 `thermo_table.inc`(SoA thr_lo/mid/hi,512 筆,
  index i == 溫度 tenths;thr[i]=count(i·0.1−0.05°C)),ROM 用 **9 步 2 冪次二分搜**(read_thr 讀 SoA)+ **連減法 BCD**。
  字型改 ASCII 索引(tile==ASCII)。做法依 Gemini 諮詢(`MD/suggest/2026-07-20-gemini-count-to-celsius`)。
- round-trip:0/10/20/25/30/40°C 精確;**暖端 tie**(≥~40°C 衰減快、count 粗、相鄰度同值)~±0.5°C(43&44→43.5、50&51→50.5),
  冷端真 0.1°C。範圍 0.0–51.1°C clamp。截圖 `tools/thermo_rom/c_*.png` + `WebSite/s1a/img/c_*.png`。
- 專文 #4 = `WebSite/s1a/aprnes-thermometer-tool.html`(工具/用法/ROM/截圖/count→°C/誠實極限),s1a.html 深入專文區有卡片。
- ⚠️ **wla-6502 對 `lda label,x` 硬選 zp,X**(即使 label=$C000)→ 前向參照失敗;改用零頁間接 `(ptr),y`;資料放程式前面。

關聯:AprVisual 深入專文 `WebSite/s1a/nes-thermometer.html`、記憶 [[openbus-shim-lastbyte-model]]。
⚠️ AprNes 有「LOCAL ONLY, do not push」diagnostic commit 慣例;此 feature commit 目前只在本機,
push 與否待使用者決定。
