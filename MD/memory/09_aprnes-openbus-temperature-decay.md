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

## 程式位置(commit `f1b949c`,本機 master,**未 push**)
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

關聯:AprVisual 深入專文 `WebSite/s1a/nes-thermometer.html`、記憶 [[openbus-shim-lastbyte-model]]。
⚠️ AprNes 有「LOCAL ONLY, do not push」diagnostic commit 慣例;此 feature commit 目前只在本機,
push 與否待使用者決定。
