# 參考:測試參數盤點整修(**使用者自行檢視,別自動動手**)

**狀態更新(2026-07-18 深夜):使用者說「先放棄參數精簡化,我再慢慢看過」——
這件事改由使用者自己手動檢視,不是我的自動任務。除非使用者明確要求,否則別自動去砍參數。
本檔保留當「使用者手動檢視時的分類參考」。Task #5 已刪。**

原始裁示(2026-07-18,保留當背景):等 AC 141 + 147 重跑完畢、S1 基礎重新認證後,盤點
71 個 CLI 旗標 + env + shim,很多可能已沒必要(一次性戰役診斷探針、TEMP 除錯 env、被機制取代的)。
現成 checklist = `WebSite/s1-cli.html` / `s1a-cli.html`(source 驗證版,列全 71 旗標 + env)。

## 順序 + 驗證強度(2026-07-18 使用者放寬)
1. **先**等測試完畢(AC 141 + 147 重跑,確立乾淨基礎)—— **別在跑的時候動**(rebuild 會鎖 DLL)。
2. **後**盤點 → 分類 → 移除。**驗證強度分兩級**:
   - **一次性戰役診斷探針 / TEMP 除錯 env** = 獨立模式,不碰熱路徑、不碰 shim/判定邏輯 →
     **直接移除,不用逐一重驗**(砍它們不可能動到金 checksum 或測試判決)。全部砍完最後跑一次
     金 checksum + 冒煙即可。
   - **會動到引擎 / shim / 判定共用碼的** = 才需要逐一重驗金 checksum + AC/147 不退步。

## 盤點清單(現成起點 = 剛建的 CLI 參考頁 + source)
- **權威清單**:`WebSite/s1-cli.html` / `WebSite/s1a-cli.html`(2026-07-18 從 `Test/TestRunner.cs`
  逐項驗證)列了全部 71 旗標 + env。用它當 checklist 逐一分類。
- **分三類**:
  - **承重(絕不動)**:`--ac-verdict --joypad --reset-hold-extra --callback-drain-limit --max-frames
    --system-def-dir --pin --snapshot-* --progress-* --test-json --test-screenshot --benchmark --bench-hc
    --test --test-dir` + env `ALEREAD_MUX / MUX_HC`(掛牌配方要用的,見 [[ac-test-toolchain-and-verdict-flow]]、
    `MD/memory/00`)。
  - **偶爾有用(留)**:`--no-shims` 及各 `--no-*-shim` / `NO_*_SHIM`(A/B 隔離用)、`--resume`、
    `--dump-node/--dump-module/--dump-system/--names`(靜態診斷)。
  - **候選移除(戰役一次性 / TEMP)**:一堆單次戰役的診斷探針(`--op-probe --bus-trace --probe-dma
    --probe-2001 --probe2002 --probe-vbl --rdy-probe --phase-probe --ppu-memory-trace* --micro
    --ac-dump-work` 等)、TEMP 除錯 env(`OB_DEBUG`(含硬編 Time 窗)、`LAE_DEBUG / ODMA_DEBUG /
    PB_DEBUG / PWD_DEBUG / OE_DEBUG / MUX_DBG / PC_WIN`)、可能被機制取代的旗標。**逐一確認是否還有引用/
    價值再砍。**

## 注意
- S1 與 S1A 現在 **71 旗標完全相同**,差異全在 env(S1A 多 11 個機制 env)。整修時兩 fork 分開處理:
  S1 是金標準(動了要全驗)、S1A 的機制 env 不能砍(還在研究中)。
- 這條與 [[s1a-verdicts-need-reverification]]、[[write-important-to-memory-immediately]] 同一批
  「測試完成後」的收尾工作。CLI 參考頁本身也要隨整修同步更新。
