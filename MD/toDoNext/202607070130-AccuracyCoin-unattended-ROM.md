# AccuracyCoin 無人值守 ROM 修改說明

## 目的

將 `C:\ai_project\AprVisual\AC_ref\AccuracyCoin.nes` 改成無人值守版本：

- 開機後不需要手把輸入。
- 自動跑完 ROM 內所有可自動執行的測試。
- 跑完後停在最終結果表。
- 在 CPU RAM 寫入固定 completion block，方便 emulator 或測試腳本判斷已完成並讀取總結。

## 核心做法

AccuracyCoin 原本就有「在頁面標題按 Start 後跑完整 ROM 測試」的既有流程：

```asm
AutomaticallyRunEveryTestInROM
```

這次修改沒有重寫測試迴圈，而是直接重用這個既有 subroutine。這樣可以保留原 ROM 對每個測試、跳過 DRAW 類測試、統計 pass/fail、繪製最終結果表的原始行為。

## 修改檔案

主要修改：

```text
C:\ai_project\AprVisual\AC_ref\AccuracyCoin.asm
```

重組輸出：

```text
C:\ai_project\AprVisual\AC_ref\AccuracyCoin.nes
```

## 重要修改點

### 1. 不在 early boot 直接跑測試

舊版曾在 `ReloadMainMenu` 裡、`DMASync` 後直接呼叫：

```asm
JSR AutomaticallyRunEveryTestInROM
```

這雖然能自動跑，但會跳過原本主選單畫面完成、開 rendering、進入 NMI 後按 Start 的路徑。`$2007 Stress Test` 對 CPU/PPU 進入時機很敏感，這種早期直呼叫可能造成 page 19 倒數第三項失敗。

目前修正版不改 `ReloadMainMenu` 的原本初始化流程，讓 ROM 先照原始互動版完成：

```asm
JSR LoadSuiteMenu
JSR DrawPageNumber
JSR WaitForVBlank
JSR ResetScroll
JSR EnableFullRendering
JSR EnableNMI
INC <Debug_EC ; 09 -> 0A
```

### 2. 在主選單 NMI 入口自動觸發

原本 `NMI_Routine` 開頭是：

```asm
JSR ReadController1
```

現在改成同樣 3 bytes 長度的：

```asm
JSR AC_UnattendedOrReadController1
```

這樣不會推動 bank 3 後方的精準時序 helper 位址。`AC_UnattendedOrReadController1` 會檢查 `$EC(Debug_EC)`：

```asm
AC_UnattendedOrReadController1:
	LDA <Debug_EC
	CMP #$0A
	BEQ AC_UnattendedRun
	JMP ReadController1
```

效果：

- `$EC != $0A` 時，直接 tail-call 回原本 `ReadController1`，維持原行為。
- `$EC == $0A` 時，代表主選單初始化已完成，從 NMI 裡自動跑 `AutomaticallyRunEveryTestInROM`。
- 這更接近使用者在主選單頂部按 Start 的原始 141/141 通過路徑。

### 3. Completion 寫入與停機

自動跑完後，helper 寫 completion block，關閉 NMI，然後停在 infinite loop：

```asm
AC_UnattendedRun:
	LDA #$FF
	STA <Debug_EC
	JSR AutomaticallyRunEveryTestInROM
	LDA #$DE
	STA $7F0
	LDA #$B0
	STA $7F1
	LDA #$61
	STA $7F2
	LDA <PostAllPassTally
	STA $7F3
	LDA <PostAllTestTally
	STA $7F4
	LDA <AllTestMenuTotalSkipped
	STA $7F5
	JSR DisableNMI
AC_Halt:
	JMP AC_Halt
```

### 4. 注意 early bank 空間限制

`ReloadMainMenu` 位於 `$8000-$8100` 的早期開機區塊，後面固定：

```asm
.org $8100
TableTable:
```

這段空間很緊。若直接在 `ReloadMainMenu` 插入太多程式碼，會把 `VerifyJSRBehavior` 推到 `$8100` 之後，造成它和 `TableTable` 重疊。實際症狀會是開機早期把測試表資料當程式碼執行，畫面可能只剩一片灰。

目前修正版不再在這段插入 unattended 主邏輯。重組後確認：

```text
VerifyJSRBehavior = $80E2
TableTable        = $8100
```

兩者不再重疊。

另確認：

```text
NMI_Routine                    = $F6D3
AC_UnattendedOrReadController1 = $DF99
AC_Halt                        = $DFCA
```

## Completion Block

跑完後可讀 CPU RAM：

```text
$07F0-$07F2 = DE B0 61   ; completion magic
$07F3       = pass tally
$07F4       = test tally
$07F5       = skipped tally
$0300-$04FF = 原本每個 test 的詳細結果資料
```

建議 emulator 自動化流程：

1. 載入 `AccuracyCoin.nes`。
2. 持續執行 CPU/PPU。
3. 輪詢 CPU RAM `$07F0-$07F2`。
4. 看到 `DE B0 61` 後，讀 `$07F3-$07F5` 和 `$0300-$04FF`。
5. 此時 ROM 已停在 `AC_Halt` infinite loop。

## 組譯方式

在此目錄執行：

```powershell
cd C:\ai_project\AprVisual\AC_ref
.\nesasm.exe AccuracyCoin.asm
```

已驗證輸出：

```text
NES Assembler (v3.01)

pass 1
pass 2
```

目前重組出的 `AccuracyCoin.nes` SHA256：

```text
63C6F0DDE6B312964184240E722418C50F5AC48682E785556B9749FA90CD3CA3
```

## 注意事項

- 這版 ROM 會自動跑全部測試，不會停在主選單。
- 全測試執行期間原 ROM 會關閉 rendering，所以短時間看到灰/空畫面是正常的；完成時才會顯示結果表並寫入 `$07F0-$07F5`。
- 若 emulator 卡住，可以先看 CPU RAM `$00EC`，原 ROM 註解中 `Debug_EC` 用來判斷主選單初始化卡在哪個階段。
- `$07F0` 一帶在原 menu highlight helper 中也曾被臨時使用；這版 completion block 是在所有測試和結果表完成後才寫入，並且立刻停機，所以不會再被後續 UI 流程覆蓋。
- 如果要恢復互動版，把 `NMI_Routine` 開頭改回 `JSR ReadController1`，並移除 bank 2 末端的 `AC_UnattendedOrReadController1` / `AC_UnattendedRun` / `AC_Halt` helper 即可。
