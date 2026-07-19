---
name: ppualereadfeedback-not-a-behavioral-shim
description: PpuAleReadFeedback 是載入期觸發清單建構(近正確物理模型),不是行為 shim；不適合便宜推 PROVEN
metadata:
  type: project
---

# PpuAleReadFeedback ≠ 行為 shim(評估結論,2026-07-19)

Source: `src/AprVisual.S1A/Sim/WireCore.Handlers.cs:600-636`（CHR-ROM 讀取 callback 註冊）；
旗標宣告 `Handlers.cs:87 PpuAleReadFeedbackShim`；武裝 `TestRunner.Test.cs:136`（只在 `_acVerdict`）。

## 它做什麼
MetalNES 原版 ROM handler 只看 `cs|rw|a[]`,**不看 d[]**（ROM 輸出不能依賴自己驅動的資料匯流排,
否則在 PPU 多工 AD 匯流排上形成不收斂環）。這個 guard 是在 `RegisterCallback` 前**把 `ppu.ale` +
`ppu.rd` 加進 CHR-ROM 讀取的觸發清單** → 讓 CHR-ROM 讀取在真正的 ALE/RD 時序邊沿開火。

## 為什麼不適合便宜推 PROVEN(對比 LXA/FrameIrq)
1. **載入期圖建構,不是 per-cycle step** → env-gate step 模板不適用（要改在建 callback 那層）;
   踩到 [[socket-pattern-dut-immutability]]「載入期改圖=重擲對齊彩票」。
2. **拔掉不會給乾淨單測 FAIL** —— 會讓 CHR 讀取時序在一堆渲染測試微幅飄,三臂很難成立;而且
   AC-based、defender 未知(很可能後段幀,中高成本)。
3. **它幾乎已經是正確物理模型** —— ALE/RD 閘控 OE 是真時序,不是行為造假。歸類成「shim」有點冤。

## 建議(未動手)
不丟便宜 promote-verify 流水線;該做的是在總帳把它**正名為結構性建模**而非行為 shim。要真 PROVEN
得靠 AC 獵 defender(中高成本),留過夜批次。對比 [[s1a-analog-refactor-on-hold]] 的 M 軸機制化。
