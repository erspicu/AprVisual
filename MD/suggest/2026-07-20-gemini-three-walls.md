# Gemini 諮詢:三個「牆」的建設性建議(PowerUpState / OpenBus / DL,2026-07-20)

完整回覆見 `2026-07-20-gemini-three-walls-reply.txt`。三個都拿到可建方案,重點:

## CASE 1 — PowerUpState / reset-hold
- 「精確開機值」= 本質不可達(intrinsic Vth mismatch + 電源 ramp)—— 確認。
- **但可建**:
  - **Topological State Finder**:掃網表找交叉耦合環(RAM/OAM/reg),看下拉 vs depletion 上拉的 W/L
    對稱性 → 對稱=coin-flip、不對稱=有偏好初態(強制之)。**Monte Carlo init**:對稱環隨機灌 0/1、
    settle 到 quiescent → 得到的 local minima = 物理可達的開機態集合。把「假設一個值」升成「界定可達集」。
  - **CPU/PPU divider 相位 = 真 coin-flip**,且**可在網表直接驗**:trace reset pad 的 fan-out,
    若沒有閘路徑到 clock divider latch → 相位就是開機當下鎖定的,沒有 reset 對齊機制。**← 可查證!**
  - M7 對此無用(只給執行決定論,不給值)。

## CASE 2 — OpenBus(last byte)——⭐ 第一性原理驗證我方 600ms
- 死晶粒網表推不出精確 hold = 不可能;**behavioral replay + timestamp decay 就是對的抽象層**(確認)。
- **驗證**:C ≈ 20pF(pad 3 + DIP40 lead 2 + PCB trace 5" 10 + edge 3 + ROM gate 2)、I_leak ≈ 10–100pA。
  `t = C·ΔV/I = 20pF × 3.5V / 100pA = 0.7s`。**我方 ~600ms 經驗值幾乎完美吻合第一性原理**(700ms)。
- 實測驗證:真 NES + 高阻示波器抓 open-bus droop 曲線。

## CASE 3 — DL——⭐ 重要更正:M7 不對,正解是 inertial delay
- **M7 對 DL 是 unsound**:只把 glitch 變決定論(藏症狀、病還在,order-dependence 仍存)。
- **理論正解 = 兩階段 settle = delta-cycle(Evaluate→Update,重複到零變化才進下個 hc)**。
- **便宜的局部做法 = 「settle guard」= Inertial Delay Filtering**:input 節點在整個 hc compute
  期若變動過,就把 latch 更新**延到 quiescence 才取值**(不用全晶片跑兩遍)。**← 這其實正是現在 DL shim
  在做的事**,只是可正名為標準 inertial-delay 技術,而非「架構級的貴」。
- IRSIM 的做法:用 RC/Elmore 排事件,input 在 R·C 到期前又變 → pending event 被 squash(= inertial delay,
  短於傳播延遲的 glitch 過不去)。我方零延遲引擎難 bolt-on Elmore,故 localized inertial delay(settle guard)最划算。
- **Bonus(可能從根拔掉)**:確保 wire_compute 的 contention 按**驅動強度**(M1)解 —— enhancement 下拉必即刻
  贏 depletion 上拉,別經過中間 X/high-Z 態誤觸 latch。很多零延遲 NMOS glitch 在強度優先解好後就消失。

## 對專案的意義
- **OpenBus**:CEILING 標籤正確,但**衰減常數現在有第一性原理背書(700ms ≈ 我方 600ms)** → 可寫進網站。
- **DL**:special-cases 說「架構級的貴」可**修正/補充** —— 正解是 inertial delay(標準技術),現行 shim 已是其
  localized 版;且 M1 強度優先 contention 可能從根解決。這比「架構級」樂觀。**M7 對 DL unsound** 要更正。
- **PowerUpState**:divider 相位 coin-flip **可在網表驗**(trace reset fan-out);Monte Carlo init 可界定可達集。
