---
name: ppualereadfeedback-assessment
description: PpuAleReadFeedback = P4 類比回授 break(載入期觸發 + 執行期 hold),有真 defender 可判定；升級成本低~中
metadata:
  type: project
---

# PpuAleReadFeedback 評估(修正版,2026-07-19)

Source: `Handlers.cs:608-624`(載入期觸發 add）+ `Handlers.cs:331-343`(執行期 hold gate）;
旗標 `Handlers.cs:87`;武裝 `TestRunner.Test.cs:136`（只在 `_acVerdict`）;專用 OFF 旗標
`TestRunner.cs:148 --no-ppu-ale-read-feedback-shim`（`_noPpuAleReadFeedbackShim`）。

⚠️ 更正:初評只看載入期、誤判為「純觸發清單精修 / 近 no-op」。看完執行期後 = 真的 P4 回授 break。

## 它做兩件事
1. **載入期**:把 `ppu.ale`+`ppu.rd` 加進 CHR-ROM 讀取 callback 的觸發清單。
2. **執行期**(331-343):當 `ppu.ale` HIGH 且 `ppu.rd` LOW（ALE 與外部 /RD 同時 active）**且**
   `HasNonTrivialRomFeedbackCycle()` 為真 → **HOLD**（ROM 不驅動 AD、直接 return),打斷
   ROM→AD→透明閂鎖→ROM 類比回授環。真板靠類比延遲/驅動強度解。
3. 註解明講「**AccuracyCoin deliberately creates a cycle where ALE and external /RD are active
   together**」→ defender 是某顆故意製造這個重疊環的 AC 子測試 → **可判定,非 no-op**。

## 實作複雜性(升級機制)= 低~中(~20 行)
- 有現成 OFF 旗標 → A/B 好做。env-gate 要閘**兩處**(比 LXA/FrameIrq 多一處):載入期觸發 add +
  執行期 hold gate,都改 `(shim || mech)`。`HasNonTrivialRomFeedbackCycle()` **本身就是機制邏輯**
  (物理環偵測,非 magic 強壓)→ 不用重寫。mech ≡ shim 逐位相同(同 code path)。

## 驗證 cost = 中(折進今晚 ctrl-all 近乎免費)
- ctrl = `--no-ppu-ale-read-feedback-shim`,今晚 ctrl-all 順手加一臂 → 免費探 defender。
- defender AC-based、可能後段幀;但折進本來要跑的 ctrl-all → **零額外 core**。
- 掉測試 → 補三臂推 PROVEN(實作 ~20 行);沒掉 → 重新歸類。

## 結論(修正先前「不適合便宜推」)
比想像適合:有專用 OFF 旗標、有真執行期 feedback-break、predicate 已是機制邏輯、defender 可折進
ctrl-all 免費找。唯二比 LXA/FrameIrq 麻煩:defender 可能後段幀(折過夜批次即可)+ env-gate 多閘一處。
關聯 [[socket-pattern-dut-immutability]](載入期加觸發=圖變更,但 mech≡shim 同路徑不重擲)、
[[s1a-analog-refactor-on-hold]] M4 P4 家族。
