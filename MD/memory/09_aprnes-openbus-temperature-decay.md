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

## NESdev 網友糾錯 → 已修正(commit `04b2309`;裁判記錄 `MD/suggest/2026-07-20-gemini-nesdev-critique-referee`)
一位 NESdev 審閱者 5 點質疑**全部正確**(我當裁判 + Gemini 驗過)。已修 ROM + 專文 #3/#4:
- **ROM open-bus 存取改慣例**:prime **寫 `$2002`**(唯讀→無副作用,原本寫 $2003/OAMADDR)、poll **讀 `$2001`**(寫入專用→完整 8 位 open bus,原本讀 $2002+AND #$1F 只取低 5 位)。loop 少 2 cycle → **重測校準表**(25°C count 58950→**66268**)、round-trip 重驗(50°C 現在乾淨 50.0)。
- **CPU open bus 機制更正**:open bus = 匯流排本身;指令抓取是把值**覆寫**掉(非「re-charge $FF」)。
- **circular proof**:模擬器 decay=f(T)、ROM 反推 T=f⁻¹→round-trip 只證軟體管線、**不證物理模型**;#4 已明講、收回「驗證成功」。
- **Arrhenius 一階**(G-R↔擴散折點、C(V)、Vth 漂 −2mV/°C、VDD)+ **無 ground truth 校準**(自體發熱、無片上溫度感測)已補進誠實極限。
- ⚠️ 我驗證時抓到 Gemini 一處過度:質疑 5 的「boot 幾 ms 內 T_J 已 >>25°C」量級誇大(整體 +30-40°C 要幾分鐘),核心「無 ground truth」不變。

## 整數 0–100°C 重設計 + NowDots 非單調 bug 修正(2026-07-21)
使用者要「放棄小數點、擴大範圍」→ 改成整數 **0–100°C** 顯示(`NN DEGREE CELSIUS`)。
表 512 筆 0.1°C → **128 筆整數**(index i == 整數度,0..100 真值 + 101..127 padding),
搜尋 9 步 → **7 步**、格式化 tenths → 百/十/個位連減 + 前導零消隱。

**暖端 bug(重點)**:整數化後 75~91°C 全部卡在 count=1231(相鄰 17 度同值)。差點被當「衰減快=解析度差的真物理」寫掉,但**根因是我加的 `NowDots()` 公式**:
- 舊式 `frame_count*89342 + scanline*341 + ppu_cycles_x` 數學上要求 frame_count 在 SL261→0 進位;
  但引擎 `frame_count++` 在 **SL240**(`ppu_new.cs` PpuPhase_FrameRender,host 端渲染邊界)→
  `NowDots()` 在 SL240 暴跳一整幀(89342 dots)= **非單調** → 衰減週期≈1幀(暖端)時被提早觸發。
- **不是引擎核心 bug**:frame_count 是 host 計數器,遊戲讀不到(NES 無 frame counter 暫存器,只能靠 NMI/$2002 VBLANK 感知),SL240 進位對 AC/blargg/遊戲**完全透明**、不該動。
- **修法**(`PPU.cs` NowDots 一行):`(long)mcCycleCount * 3`。`mcCycleCount` = 每 CPU cycle ++
  (`Main.cs:714` MasterClockTickUnrolledNTSC 頂端,DMA-steal 也算),NTSC ×3 = PPU dots,單調。
  只讀依賴、不碰遊戲可見時序 → AC-safe。三方確認(使用者質疑 + Gemini nowdots-confirm/asm-analysis + 我讀 code)。
- **驗證**:`ppu_open_bus` + `ppu_read_buffer` **PASS**(25°C 預設下衰減週期 3.2M dots,SL240 跳躍<3% 可忽略);
  重測 0–100°C = **101/101 每度獨立、嚴格單調、無 tie**(cold/hot 跨 587×);round-trip **0 誤差 / 101**。
- **校準敘事翻盤**:舊「single-Arrhenius R²=0.99954、結構化殘差=lack-of-fit、暖端 ±0.5 量化」= 這 bug 的假影。
  乾淨資料:**R²=1.000000、Ea 精確回收 0.560eV**(正是注入的 OB_Ea),殘差=±1圈量化(0.05%);
  唯一誤差 = 整數捨入 ±0.5°C(全範圍一致)。強化「circular round-trip」重點(回收注入的模型)。
- **建置**:net48 用 **VS2022 完整 MSBuild**(`-t:Restore,Build`);SDK `dotnet build` 對內嵌 .resx 卡 MSB3822/3823。
- **已更新**:`tools/thermo_rom/{thermo.asm,build.py,README.md,counts_by_degree.json}`、`make_calib_svg.py`(0-100/R²=1.0/held-out 一致±0.5)、
  三張 calib SVG、專文 #4(round-trip 表/校準診斷/where-rough 全改;四模擬器 survey 保留但標註=較早 0.0-51.1 十進位 build)、
  網站 `img/c_{0,25,50,75,100}.png` + `thermo/{thermo.nes,thermo.asm,thermo-src.zip,aprnes-custom.zip(換含修正的新 exe)}`。
- Gemini 諮詢:`MD/suggest/2026-07-20-gemini-{warm-quantization,asm-analysis,nowdots-confirm}-reply.txt`。

關聯:AprVisual 深入專文 `WebSite/s1a/nes-thermometer.html`、記憶 [[openbus-shim-lastbyte-model]]。
⚠️ AprNes 有「LOCAL ONLY, do not push」diagnostic commit 慣例;此 feature commit 目前只在本機,
push 與否待使用者決定。
