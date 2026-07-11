# AccuracyCoin `$2007 Stress` 現況與交接

> 日期: 2026-07-12
>
> 狀態: **卡死根因已證明，候選修正與快速驗證已建立；完整答案鍵尚未重跑，因此不能宣告結案。**

## 1. 結論先行

原本 frame 4480 的無聲卡死不是一般效能問題，也不是 callback re-entrancy guard 失效。
AccuracyCoin 在 `$2007 Stress` 故意讓 PPU `ALE` 與外部 `/RD` 同時有效，形成:

```text
CHR ROM output -> PPU AD bus -> transparent octal latch -> CHR ROM low address
```

真機靠傳播延遲與驅動強度得到類比結果；二值 callback 模型會遇到無固定點映射。
實際卡點的 CHR page 0 內容是:

```text
ROM[$003C] = $06
ROM[$0006] = $3C
```

因此 callback 在 `$003C -> $0006 -> $003C` 間永久震盪。

目前來源包含四層處理:

1. `--callback-drain-limit` 在診斷模式把永久空轉改成含 CPU/callback/node 現場的 exception。
2. read-only ROM callback 不再 watch 自己驅動的 `d[]`；這與 MetalNES ROM handler 的依賴一致。
3. AccuracyCoin test mode 只在 `ALE + /RD` 且 ROM low-byte 映射為長度大於 1 的 cycle 時 hold 舊輸出。
4. NROM CIRAM A10 依 iNES header 接線；AccuracyCoin 是 horizontal mirroring，應接 PPU A11，不是固定 PPU A10。

第 3、4 項是目前的**候選行為修正**。快速 Sprite Probe 的三個穩定值已與 AprNes 對齊，
但完整 341-sample `$2007 Stress` 尚未用此最終候選重跑。

## 2. 已證明的事

| 事實 | 證據 |
|---|---|
| 舊版完整 AccuracyCoin 在 frame 4480 停住 | 兩次獨立 checkpoint 與原始 handoff |
| re-entrancy guard 只消除 stack overflow，沒有消除狀態震盪 | guard 版 CPU trace 仍停在 callback drain |
| isolated ROM 在 `PC=$9CE6, X=$F6` 重現 | callback limit exception，`t=210137244` |
| 震盪 callback 是 CHR ROM 與 VRAM，watch BA/BD bus | exception 的 top/recent callback 清單 |
| 移除 ROM `d[]` watcher 仍在相同 PC/X 卡住 | `isolated-2007-rom-data-trigger-removed-still-hangs.txt` |
| 快速 Hang Probe 可在約 4.3 分鐘重現；guard 關閉卡死、開啟完成 | `2007-hang-probe-ab.txt` |
| 廣泛 hold 所有 `ALE + /RD` 雖可完成，完整答案鍵仍 `0/1 FAIL` | `isolated-2007-hold-all-full-fail.txt` |
| `$F6` 後一筆穩定樣本未被 broad hold 污染 | AprNes `$3C,$3C`；S1 `$06,$3C`，第二筆一致 |
| background-to-sprite 邊界目前三個穩定值正確 | AprNes/S1 的 offset 0/2/4 都是 `$18/$02/$00` |

## 3. 快速 ROM

診斷 ROM 位於 `AprAccuracyCoinUnattended`，都保留正常 power-on、menu 初始化、timing pre-test、
`RunTest` 與 `$07F0` 完成區塊。它們不是另外重寫的近似測試。

| ROM | 用途 | S1 約略成本 |
|---|---|---:|
| `AccuracyCoin_2007Stress.nes` | 原始 341-sample 答案鍵，唯一完整裁決 | 約 33 分鐘 |
| `AccuracyCoin_2007Sequence.nes` | 先跑同一 PPU Misc page 的前置項目 | 未重跑 |
| `AccuracyCoin_2007HangProbe.nes` | 直接移到原 `X=$F6` 卡點，只驗收斂 | 約 4.3 分鐘 |
| `AccuracyCoin_2007FeedbackProbe.nes` | 卡點及下一筆穩定讀值 | 約 4.6 分鐘 |
| `AccuracyCoin_2007TailProbe.nes` | 從卡點掃到 dot 340，共 95 samples | 2026-07-12 執行中止，沒有可用 verdict |
| `AccuracyCoin_2007SpriteProbe.nes` | `$05FF-$0603` 的 5-sample 邊界 | 約 4.6 分鐘 |

不要用 Probe 的 `1/1` 當成完整 Stress pass。Hang/Feedback/Tail/Sprite Probe 會在擷取目標後發布
convergence verdict；真正的資料值要看 `--ac-dump-work`。

## 4. 目前驗證矩陣

### 2026-07-12 最終工作樹已完成

| 驗證 | 結果 |
|---|---|
| `dotnet build ... -c Release` | PASS，0 warnings / 0 errors |
| `--selftest` | PASS，包含 callback non-convergence detector |
| 300,000 hc golden checksum | `0x794A43ABDF169ADA`，與 baseline 相同 |
| 正式 `AccuracyCoin.nes` 重建 | SHA-256 `63C6F0DDE6B312964184240E722418C50F5AC48682E785556B9749FA90CD3CA3`，byte-identical |
| AprNes Sprite Probe | raw `18 02 02 00 00`；穩定 offset 0/2/4 = `18/02/00` |
| S1 Sprite Probe | raw `18 C0 02 02 00`；穩定 offset 0/2/4 = `18/02/00`，frame 53，274 s |
| `11-special.nes` smoke test | PASS，frame 11，`detection=6000` |

類比 offset 1/3 不評分，因此 AprNes 與 S1 在那些位置不同不是 failure。

### 尚未完成

- 最終 cycle-aware guard + iNES mirroring 的完整 `AccuracyCoin_2007Stress.nes`。
- `AccuracyCoin_2007Sequence.nes`。
- S1 正式 141-test `AccuracyCoin.nes`。
- 全部 test-ROM regression。先前 golden 與窄測通過不能替代完整回歸。

## 5. 下一步

1. 先跑完整 isolated ROM，這是最小的正式 acceptance gate:

```powershell
dotnet src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll `
  --test AprAccuracyCoinUnattended/AccuracyCoin_2007Stress.nes `
  --system-def-dir AprVisualBenchMark/data/system-def `
  --ac-verdict --ac-dump-work --callback-drain-limit 2000 `
  --max-frames 450 --max-wait 2700 --pin
```

2. 必須得到 `AccuracyCoin: 1/1 passed`；只有「不再卡死」不算成功。
3. 若 isolated pass，再跑 Sequence；兩者都 pass 後才值得支付正式 141-test 的約 6.9 小時成本。
4. 完整回歸通過前，不更新網站 AccuracyCoin 成績，不寫 S1 `141/141`。

## 6. Repository 邊界

- AprVisual: simulator、CLI、證據與本交接文件。
- AprAccuracyCoinUnattended: compile-time diagnostic wrappers 與生成 ROM。
- AprNesRef: 行為 oracle。應使用 `AprNesRef/AprNes` 的 CLI，不是 Avalonia GUI。
- AprNesRef 目前有 `local-only` commit 與工作樹修改，本次不提交或推送它們。

詳細設計與風險見 `2026-07-12-AccuracyCoin-2007Stress-fix-design.md`；證據來源見
`2026-07-12-AccuracyCoin-2007Stress-evidence-index.md`。
