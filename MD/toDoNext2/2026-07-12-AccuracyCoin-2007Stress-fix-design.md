# `$2007 Stress` 候選修正設計

> 這是設計與限制文件，不是完整 pass 證明。最終 isolated Stress 尚未重跑。

## 1. Non-convergence detector

`WireCore.InvokeCallbacks()` 的 production path 保持原本 drain loop。只有
`--callback-drain-limit N` 大於 0 時才走 instrumented branch；超過 dispatch budget 且仍有工作時，
exception 包含 CPU state、pending/processing 數量、top callbacks、最近 16 個 callbacks 與 watched node states。

證據配置只在最後 512 dispatches 開始配置，避免一般 drain 因診斷功能產生 allocation。
`--selftest` 有一個自行翻轉 watched node 的 callback，證明 detector 會停止真正的無窮 re-enqueue。

這一層是可保留的診斷修正，與選擇哪種 `$2007` 類比模型無關。

## 2. Read-only ROM callback 依賴

舊 handler 對 RAM/ROM 一律 watch `cs`, `/we`, address 與 data bus。read-only ROM 的輸出只依賴
chip select、bank 與 address；watch 自己驅動的 `d[]` 會讓一次 ROM output 反過來重新 enqueue 自己。

目前規則:

```text
ROM: cs + address (+ control)
RAM: cs + /we + address + data
```

這與 MetalNES `handler_rom` 的依賴方向一致。實驗也證明只移除 ROM `d[]` watcher 仍會在相同
`PC=$9CE6, X=$F6` 卡住，因此它是必要的依賴修正，不是完整根因修復。

## 3. Cycle-aware CHR feedback guard

guard 只在以下條件全成立時啟用:

1. test runner 使用 `--ac-verdict`。
2. 沒有 `--no-shims` 或 `--no-ppu-ale-read-feedback-shim`。
3. handler 是 `cart.chr` read-only ROM。
4. `ALE` high 且實體 `/RD` low。
5. 固定 upper address 後，`low -> ROM[page|low]` 最後進入長度大於 1 的 cycle。

cycle 判斷使用 Floyd tortoise-hare，最多 256 個 low-byte 狀態，不配置記憶體。若映射收斂到
`ROM[page|low] == low` 的 fixed point，正常 callback settle 繼續；只有非固定 cycle 才 hold 上一次輸出。

為什麼不能 hold 每一個 overlap: broad hold 版雖解除卡死，完整 isolated Stress 是 `0/1 FAIL`。
AccuracyCoin 明確只忽略類比不穩定 bytes，其他 sprite/dummy fetch 的穩定 bytes 仍必須精確。

目前 guard 的適用面刻意只限 AccuracyCoin verdict path。完整驗證前不應擴成所有 ROM 或 benchmark 預設。

## 4. NROM mirroring

stock cart definition 原本固定把 `edge.ciram_a10` 接到 `edge.ppu_a10`。NROM 必須依 iNES header:

| iNES mirroring | CIRAM A10 source |
|---|---|
| horizontal | PPU A11 |
| vertical | PPU A10 |

`WireCore.ComposeSystem()` 現在只替換既有 connection 的 source，不增減 connection，避免改變 node allocation
與 callback ordering。AccuracyCoin header 是 horizontal；Sprite Probe 的穩定邊界值在目前接線下與 AprNes 一致。

## 5. 診斷介面

| 參數 | 用途 |
|---|---|
| `--callback-drain-limit N` | 無窮 callback drain 轉成證據 exception |
| `--no-ppu-ale-read-feedback-shim` | 同一 DLL 做 guard A/B |
| `--ac-dump-work` | verdict 後 dump CPU RAM `$0500-$06FF` |
| `--ppu-memory-trace lo hi` | CPU PC 範圍內記錄 CHR/VRAM callback |
| `--ppu-memory-trace-x XX` | 另以 CPU X 篩選 trace |

PPU memory trace 預設關閉，最多保留 512 records。它目前在 memory handler 有一個快速 early-return 分支；
golden checksum 相同，但尚未做獨立的 before/after performance campaign。

## 6. Rejected approaches

| 方案 | 結果 |
|---|---|
| 增大 thread stack | 只延後 stack overflow，不解決 cycle |
| re-entrancy guard 當完整修復 | 消除遞迴崩潰，但 drain 仍永久震盪 |
| 任意截斷 settle/drain 後繼續 | 會保留任意 phase，沒有正確性依據 |
| ROM 不 watch `d[]` | 正確但單獨不足，相同 PC/X 仍卡住 |
| 所有 `ALE + /RD` 一律 hold | 可收斂但完整答案鍵 `0/1 FAIL` |
| 用 Probe `1/1` 代替答案鍵 | Probe 只表示執行返回，資料仍須比對 |

## 7. Acceptance gates

候選修正要稱為完成，至少必須依序滿足:

1. Release build 與 selftest。
2. golden checksum `0x794A43ABDF169ADA`。
3. Hang、Feedback、Sprite probes 的穩定資料。
4. 完整 isolated `$2007 Stress` `1/1`。
5. PPU Misc Sequence `1/1`。
6. 正式 AccuracyCoin 141-test 完成。
7. 既有 test-ROM regression 無回歸。

目前只完成第 1-3 項。
