# `$2007 Stress` 證據索引

## 原始長跑

| 檔案 | 證明內容 | 限制 |
|---|---|---|
| `evidence/last-checkpoints-both-runs.txt` | guard/no-guard 都停在 frame 4480 | 只能定位 frame，沒有 bus state |
| `evidence/noguard-stackoverflow.txt` | 無 guard 時重複約 24,021 層 | 原始 stderr 曾被覆蓋，檔內是轉錄 |
| `evidence/hang-callback-drain.speedscope.json` | guard 後 CPU 留在 callback drain，非 stack recursion | profile 不含邏輯 bus 值 |

## Isolated root-cause

| 檔案 | 證明內容 |
|---|---|
| `evidence/isolated-2007-old-callback-nonconvergence.txt` | `PC=$9CE6, X=$F6`，CHR/VRAM callback 交替，含 watched states |
| `evidence/isolated-2007-rom-data-trigger-removed-still-hangs.txt` | ROM 移除 `d[]` watcher 後仍在同一位置不收斂 |
| `evidence/2007-hang-probe-ab.txt` | 約 4 分鐘的同 DLL guard A/B；disabled 不收斂、enabled 返回 |
| `evidence/isolated-2007-hold-all-full-fail.txt` | broad hold 證明「收斂不等於正確」；完整答案鍵 0/1 |

## Oracle 與窄測

| 檔案 | 證明內容 |
|---|---|
| `evidence/2007-feedback-probe-aprnes-s1.txt` | 卡點後第一個穩定 sample 在 AprNes/S1 都是 `$3C` |
| `evidence/2007-sprite-probe-aprnes-s1.txt` | 最終工作樹在 sprite 邊界三個穩定值為 `$18/$02/$00` |
| `evidence/accuracycoin-2007-diagnostic-rom-sha256.txt` | 正式與所有診斷 ROM 的可重建 hash |

## 證據使用規則

- AprNes oracle 要用 `AprNesRef/AprNes` CLI，不是 `AprNesAvalonia`。
- AprNes 對 AccuracyCoin 另印的 `FAIL(255)` 是找不到 blargg `$6000` signature；裁決應看 `$07F0`
  的 `AC_DONE_HEX`。`DE B0 61 01 01 00` 表示 1/1、0 skipped。
- AccuracyCoin `$2007 Stress` 的類比位置不評分。只比較 ROM 指定的穩定 parity，不能要求 raw dump 全 byte
  與 AprNes 相同。
- `HangProbe` 等診斷 ROM 的 `1/1` 是 convergence contract，不是完整 341-sample answer-key pass。
- 2026-07-12 的 Tail Probe 被使用者中止，沒有保存完整 S1 output，不得引用為 pass/fail 證據。
