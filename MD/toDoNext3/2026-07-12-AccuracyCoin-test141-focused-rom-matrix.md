# AccuracyCoin test 141 focused ROM 驗證矩陣

> 只收錄 helper machine code 已檢查、且實際抵達 target 的有效 run。所有 ROM 都保留正常 AccuracyCoin boot/menu timing pre-test，最後以 `$07F0` completion block 判定。

## 核心矩陣

| ROM | 前置狀態 / 目的 | AprNes | AprVisual.S1 | Frames | Wall s | Half-cycles |
|---|---|---:|---:|---:|---:|---:|
| `AccuracyCoin_InternalDataBus.nes` | isolated target，另做 `--no-shims` 對照 | PASS | PASS | 24 | 135.3 | 17,093,639 |
| `AccuracyCoin_CPUBehavior2Sequence.nes` | final page items 0-4 | PASS | PASS | 96 | 約 510 | 68,554,615 |
| `AccuracyCoin_InternalDataBusDmcPrime.nes` | `DMASync_50CyclesRemaining` 連續 33 次 | PASS | PASS | 25 | 約 135 | 約 17.8M |
| `AccuracyCoin_InternalDataBusRepeat.nes` | target 連續 16 次，CPU/APU phase sweep | PASS | PASS | 53 | 337.9 | 37,820,864 |
| `AccuracyCoin_InternalDataBusImplicitAbort.nes` | immediate predecessor: Implicit DMA Abort | PASS | PASS | 32 | 170.7 | 22,811,528 |
| `AccuracyCoin_InternalDataBusDmcChain.nes` | Explicit Abort, Implicit Abort, DMC Channel, Controller Clocking | PASS | PASS | 51 | 323.2 | 36,391,408 |
| `AccuracyCoin_InternalDataBusDmcPages.nes` | DMA + APU pages，共 19 項 | PASS | PASS | 170 | 1,181.5 | 121,444,520 |
| `AccuracyCoin_InternalDataBus2007Tail.nes` | `$2007 Stress` 最後 95 samples | PASS | PASS | 145 | 753.5 | 103,576,664 |
| `AccuracyCoin_InternalDataBus2007Stress.nes` | 完整 341-sample `$2007 Stress` | PASS | PASS | 391 | 2,019.3 | 279,401,720 |
| `AccuracyCoin_InternalDataBusPpuTail.nes` | `ALE + Read`, `Hybrid Addresses` | PASS | PASS | 119 | 663.9 | 84,993,455 |
| `AccuracyCoin_InternalDataBusFormalTail.nes` | final page outer post-VBlank wait，有 marker | PASS | PASS | 102 | 560.7 | 72,843,008 |
| `AccuracyCoin_InternalDataBusFormalTailExact.nes` | 同上，無 marker | PASS | PASS | 103 | 577.1 | 73,557,736 |
| `AccuracyCoin_InternalDataBusCondensedTail.nes` | 最後 95 Stress samples + ALE/Hybrid + final page，outer waits，無 marker | PASS | PASS | 331 | 1,971.9 | 236,517,159 |

`PASS` 在此代表 completion block 顯示 `AccuracyCoin: 1/1 passed, 0 skipped`，即 target `result_InternalDataBus == 1`。前置測試的某些 success code 不一定是 `$01`；它們能正常返回與 target 通過才是這組診斷的主要判據。

## 最有價值的 ROM

### 最快 target gate

```powershell
dotnet src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll `
  --test AprAccuracyCoinUnattended/AccuracyCoin_InternalDataBus.nes `
  --system-def-dir AprVisualBenchMark/data/system-def `
  --ac-verdict --callback-drain-limit 2000 `
  --max-frames 50 --max-wait 600 --pin
```

預期: 24 frames，`1/1 passed`。

### 高價值濃縮正式尾段

```powershell
dotnet src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll `
  --test AprAccuracyCoinUnattended/AccuracyCoin_InternalDataBusCondensedTail.nes `
  --system-def-dir AprVisualBenchMark/data/system-def `
  --ac-verdict --callback-drain-limit 2000 `
  --max-frames 420 --max-wait 3000 --pin
```

預期: 約 331 frames，`1/1 passed`。此 ROM 的 source flags:

```asm
AC_DIAG_IDB = 1
AC_DIAG_IDB_PPU_STRESS = 1
AC_DIAGNOSTIC_2007_TAIL_PROBE = 1
AC_IDB_FORMAL_PPU_TAIL = 1
AC_IDB_FORMAL_TAIL = 1
```

marker 預設關閉。PPU page items 6-8 與 CPU page items 0-3 都在 `RunTest` 後多做一次 `WaitForVBlank`，再進 unchanged item 4。

## 組譯完整性 gate

每次修改 diagnostic branch 後至少執行:

```powershell
cd AprAccuracyCoinUnattended
./nesasm.exe AccuracyCoin_InternalDataBusCondensedTail.asm
./nesasm.exe AccuracyCoin.asm
Get-FileHash AccuracyCoin.nes -Algorithm SHA256
```

production hash 必須是:

```text
63C6F0DDE6B312964184240E722418C50F5AC48682E785556B9749FA90CD3CA3
```

另外檢查 `.fns` 與 bytes:

- `TEST_InternalDataBus = $EC4A`
- condensed helper 約在 `$ECB6-$ECDF`
- helper 在 `$ED40` 前有 `RTS`
- `DPCM_Sample_05 = $ED40`，該處仍是 `$05` sample bytes

NESASM 3.01 對跨 `.org` 覆寫不一定報錯，也可能截斷長 symbol，因此只看 assembler exit code 不足以證明診斷 ROM 有效。

## 不應重跑的舊產物

- 早期 `ac_idb_pputail` 的 24-frame PASS: helper 被 `$ED40` sample 覆寫，無效。
- 早期 `ac_idb_repeat16` 的 24-frame PASS: 同上，無效。
- `ac_idb_pputail_valid` 的 50-frame timeout: 有效 machine code，但只是預算不足，停在 `ALE + Read`，不是 bug 重現。後續 119-frame run 已正常通過。
- 早期長 label 版 DMC-prime: symbol collision，無效。

