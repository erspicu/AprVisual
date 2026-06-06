# Golden checksums(bit-exact 驗證基準)

> 任何改動後要確認「逐節點 bit-exact」,就跑對應設定、比對下表的 `NodeStates` checksum(FNV-1a over 全節點,見 `WireCore.Recalc.cs:NodeStatesChecksum`)。**值相同 = bit-identical to golden。**
> checksum 是設定相依的:換 ROM / 換 `--bench-hc` 長度 / 換旗標 / 換節點編號(`--rcm`)都會不同值。下表都是 `--extra-ram`、預設節點編號、`full_palette.nes`。

## 跑法

```
dotnet run --project src/AprVisual.S1 -c Release -- --benchmark <rom> --bench-hc <N> --extra-ram --system-def-dir <data\system-def>
# 輸出: "# NodeStates checksum @ t=...: 0x........"
```

## 基準表(full_palette.nes,--extra-ram)

| 設定 | golden checksum | 備註 |
|---|---|---|
| `--bench-hc 300000` | **`0x794A43ABDF169ADA`** | 專案的標準驗證點;C# 與 Rust 共用 |
| `--bench-hc 1000000` | **`0x6D4CCBCE2E9CD599`** | 較長跑,多驗一點活動 |

> 主驗證點是 **300k = `0x794A43ABDF169ADA`**(歷史上所有「bit-exact」宣稱都是對這個值)。1M 那筆是 2026-06-07 為了驗證 same-state turn-on prune 額外加跑的。

## 中途快照(同一次跑,小 hc;偵錯分歧從何時開始時可用)

這些是 `--no-prune`(=golden 行為)在小 hc 的 checksum,debug 用(找「分歧從哪個 hc 開始」):

| `--bench-hc` | golden checksum |
|---|---|
| 100  | `0x0717C4A0CC4578C2` |
| 500  | `0x30983EF993844192` |
| 2000 | `0x13AC672AF6ECC796` |
| 20000 | `0xBA26FCE2281E90B8` |

## 注意

- C#(`src/AprVisual.S1`)與 Rust(`experiment/rust-s1`)在 300k 應給**同一個** `0x794A43ABDF169ADA`(演算法對齊政策)。
- 若改了會影響節點編號的東西(lowering / `--rcm`),checksum 會整批不同 —— 那是「節點順序變了」不是「錯了」,要用相同設定的新 baseline 重新確立。
- 不是每個 test ROM 都寫 `$6000` PASS/FAIL;行為驗證用 blargg 類 ROM,逐節點 bit-exact 驗證用上表 checksum。
