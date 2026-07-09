# test ROM extra RAM 修復計畫

建檔: 2026-07-09  
目的: 修復 bundled test ROM 搬家後 class-A `$6000` 測試全域 `detection=none` 的問題  
根因證明: `MD/ISSUE/2026-07-09-apu_mixer-timeout-root-cause-proof.md`

## 目標

讓以下兩種用法都能得到一致結果:

```powershell
python .\tools\testrom\run_tests.py --filter apu_mixer --rerun --no-build
```

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --test .\tools\testrom\roms\apu_mixer\triangle.nes
```

也就是說, test mode 不應因 ROM 放在不同目錄而決定 `$6000` work RAM 是否存在。

## 首選修法

在 `src/AprVisual.S1/Test/TestRunner.Test.cs` 的 `RunOneTest()` 中, `WireCore.LoadSystem(rom)` 之前, 將 test mode 對 `$6000` work RAM 的需求明確化:

```csharp
// Test ROMs use the blargg $6000 protocol. Do not depend on ROM path names
// to decide whether cart-extraram is present.
WireCore.ForceExtraRam = true;
```

建議位置:

```csharp
WireCore.PowerUpStateShim = true;
WireCore.RegisterRawIdAliases = true;
WireCore.EnableJoypadHandler = _joypad;
WireCore.ForceExtraRam = true;
```

理由:

- `--test` 的註解與實作本來就宣告會檢查 blargg `$6000` protocol。
- 舊路徑時, 所有 test ROM 已經因 `nes-test-roms` path heuristic 自動掛 extra RAM; 這個修法是在保留舊語意。
- benchmark path 不會進 `RunOneTest()`, 所以不會污染 golden checksum。
- direct CLI `--test tools/testrom/roms/...` 也會被修到, 不只修 `run_tests.py`。
- 未來 ROM 目錄再搬家, 不會再因路徑字串改變而失效。

## 可接受但較弱的替代修法

### 替代 1: 在 `run_tests.py` 指令加 `--extra-ram`

例如:

```python
cmd = ["dotnet", DLL, "--test", rompath, "--extra-ram", ...]
```

缺點:

- 只修官方 Python runner。
- 使用者直接跑 `dotnet ... --test tools/testrom/roms/...` 仍會失敗。
- test mode 的語意仍然藏在外部工具裡。

### 替代 2: 擴充 path heuristic

例如在 `WireCore.LoadSystem()` 加:

```csharp
|| rom.Path.Contains("tools/testrom/roms", StringComparison.OrdinalIgnoreCase)
```

缺點:

- 還是在依賴資料夾名稱。
- Windows / Unix path separator 要小心。
- 下次 ROM 位置改名仍會重演。

## 不建議的修法

不要調高 `maxFrames`。

這次失敗是:

```text
detection=none
budget exhausted, no $6000 signature
```

不是 verdict 晚到。加幀數只會讓錯誤跑更久。

不要把問題歸因到 APU/DMC。

短 class-A CPU instruction test 也能重現 bundled path timeout; 加 `--extra-ram` 立即 PASS。這不是 `apu_mixer` 專屬。

不要先重跑全量回歸。

目前 `$6000` verdict path 是壞的, 全量回歸會產生大量假 timeout, 結果不可用。

## 修復步驟

1. 修改 `src/AprVisual.S1/Test/TestRunner.Test.cs`。
2. 在 `RunOneTest()` 載入 ROM 後、`WireCore.LoadSystem(rom)` 前加入 `WireCore.ForceExtraRam = true;`。
3. 更新 nearby comment, 說明 test ROM `$6000` protocol 不應依賴 path heuristic。
4. `dotnet build .\src\AprVisual.S1 -c Release`。
5. 執行最小驗證。
6. 執行 `apu_mixer` 回歸。
7. 執行 golden checksum 防止 benchmark path 回歸。

## 最小驗證矩陣

### 1. bundled short class-A, 不加 `--extra-ram`

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --test .\tools\testrom\roms\nes_instr_test\rom_singles\11-special.nes `
  --max-frames 40 --pin 4 --reset-hold-extra 1 `
  --test-json .\temp\fix_verify_11_special.json `
  --test-screenshot .\temp\fix_verify_11_special.png `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

期望:

```text
PASS | 11-special.nes | 11-special
detection=6000
frames=11
```

### 2. bundled `apu_mixer/triangle`, 不加 `--extra-ram`

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --test .\tools\testrom\roms\apu_mixer\triangle.nes `
  --max-frames 915 --pin 4 --reset-hold-extra 1 `
  --test-json .\temp\fix_verify_apu_triangle.json `
  --test-screenshot .\temp\fix_verify_apu_triangle.png `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

期望:

```text
PASS
detection=6000
frames ~= 607
```

### 3. runner 跑整組 `apu_mixer`

```powershell
python .\tools\testrom\run_tests.py --filter apu_mixer --rerun --no-build --jobs 4
```

期望:

```text
apu_mixer/dmc      pass, detection=6000
apu_mixer/noise    pass, detection=6000
apu_mixer/square   pass, detection=6000
apu_mixer/triangle pass, detection=6000
```

### 4. class-A smoke

```powershell
python .\tools\testrom\run_tests.py --class A --limit 8 --rerun --no-build --jobs 4
```

期望:

```text
至少不再出現因 $6000 RAM 缺失造成的 detection=none 全域失敗。
```

注意: `run_tests.py` 會用 LPT 排序, `--limit 8` 不是 catalog 前 8 筆, 而是排序後的 8 筆。

### 5. golden checksum

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --benchmark .\AprVisualBenchMark\roms\full_palette.nes `
  --bench-hc 300000 --extra-ram `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

期望:

```text
0x794A43ABDF169ADA
```

## 驗證通過後的處置

1. 將 `MD/ISSUE/2026-07-09-apu_mixer-all-timeout-after-rebuild.md` 視為原始交接紀錄。
2. 將本文件與根因證明文件視為後續診斷結論。
3. 修復 commit message 建議:

```text
fix(test): force cart-extraram for test ROM $6000 protocol
```

4. commit 內容至少應包含:

```text
src/AprVisual.S1/Test/TestRunner.Test.cs
MD/ISSUE/2026-07-09-apu_mixer-timeout-root-cause-proof.md
MD/ISSUE/2026-07-09-testrom-extraram-fix-plan.md
```

5. 在 commit 前檢查不要意外加入既有 dirty report 截圖刪除:

```powershell
git status --short
git diff --stat -- src\AprVisual.S1 MD\ISSUE
```

## 後續防呆建議

### 防呆 1: runner 啟動前跑一個 `$6000` canary

在全量回歸前先跑:

```text
tools/testrom/roms/nes_instr_test/rom_singles/11-special.nes
```

若這顆短測試沒有 `detection=6000`, 直接 abort, 不要跑 8 小時全量。

### 防呆 2: golden checksum canary

全量回歸前跑 300k golden checksum。若不是:

```text
0x794A43ABDF169ADA
```

直接 abort。

### 防呆 3: 結果報表標示 verdict mode

報表中把 `detection=6000` / `detection=screen` / `detection=none` 顯示得更醒目。這次能快速定位, 是因為 `detection=none` 明確指出「簽章完全沒看到」。

### 防呆 4: 減少 path-based semantics

凡是影響模擬組態的條件, 優先由 CLI flag 或 test mode 顯式指定, 不要由資料夾名稱推導。

