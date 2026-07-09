# apu_mixer / class-A timeout 根因證明

建檔: 2026-07-09  
狀態: 已定位根因, 尚未改程式碼  
關聯文件: `MD/ISSUE/2026-07-09-apu_mixer-all-timeout-after-rebuild.md`

## 結論

`apu_mixer` 四個測試在 `9a7987e` 後全數 timeout, 且 `detection=none`, 不是 DLL 重建造成的 bit-exact core 行為改變, 也不是 ROM 內容損壞。

根因是:

1. `tools/testrom/catalog.json` 的 `romBase` 從 `nes-test-roms-master/checked` 改成 `tools/testrom/roms`。
2. `WireCore.LoadSystem()` 目前只用 ROM 路徑字串判斷是否掛 `cart-extraram`。
3. 新路徑 `tools/testrom/roms/...` 不含 `nes-test-roms` 或 `nes_test`, 因此 test ROM 沒被視為 `isTestRom`。
4. `$6000` 結果協定需要 `cart-extraram`; 沒掛上時, test runner 從頭到尾看不到 blargg 簽章, 所以結果是 `timeout / detection=none`。

簡化版:

```text
catalog romBase 搬家
  -> ROM path heuristic 沒命中
  -> cart-extraram 沒掛
  -> $6000 簽章區不存在
  -> class-A 測試 detection=none
```

## 與原交接文件的關係

原交接文件的症狀紀錄是正確的:

- `apu_mixer` 四筆從 `pass / detection=6000` 變成 `timeout / detection=none`
- `ba33c36 -> 9a7987e` 沒有改 `src/AprVisual.S1`
- `romBase` 是該區間唯一落在測試路徑上的功能性變更
- `detection=none` 不是幀數預算問題

需要修正的是嫌疑排序:

- 原本的嫌疑 A: DLL / preview runtime 重建後行為改變, 現在應該降級。
- 原本的嫌疑 B: `romBase` 改動, 已由最小實驗證實是主因。

## 關鍵程式路徑

### 1. catalog 現在指向 bundled ROM 目錄

`tools/testrom/catalog.json`:

```json
{
 "schema": "aprvisual-testrom-catalog/1",
 "romBase": "tools/testrom/roms",
 "tests": [
```

### 2. `WireCore.LoadSystem()` 用路徑 heuristic 決定是否掛 `$6000` RAM

`src/AprVisual.S1/Sim/WireCore.System.cs`:

```csharp
public static bool ForceExtraRam = false;

public static void LoadSystem(NesRom rom)
{
    _rom = rom;
    bool chrIsRam = rom.ChrRom.Length == 0;
    bool isTestRom = ForceExtraRam
                  || rom.Path.Contains("nes-test-roms", StringComparison.OrdinalIgnoreCase)
                  || rom.Path.Contains("nes_test", StringComparison.OrdinalIgnoreCase);
```

只有 `isTestRom == true` 時, `ComposeSystem()` 才會載入 `cart-extraram`:

```csharp
if (isTestRom)
{
    LoadModuleDef(SystemDefDir, "cart-extraram");
    cartMmu0.SubModules.Add(new SubModuleRef { Prefix = "", Type = "cart-extraram" });
}
```

### 3. runner 組指令時沒有補 `--extra-ram`

`tools/testrom/run_tests.py`:

```python
rombase = os.path.join(REPO, cat["romBase"].replace("/", os.sep))
rompath = os.path.join(rombase, t["suite"].replace("/", os.sep), t["rom"])

cmd = ["dotnet", DLL, "--test", rompath, "--max-frames", str(mf), "--pin", str(core),
       "--reset-hold-extra", "1",
       "--test-json", jpath, "--test-screenshot", spath, "--system-def-dir", SYSTEM_DEF]
```

因此目前 runner 會用 bundled path 跑 test ROM, 但不會設定 `ForceExtraRam`。

## 最小證明實驗

使用同一顆 DLL:

```text
src/AprVisual.S1/bin/Release/net11.0/AprVisual.S1.dll
engineVersion = 9a7987e
LastWriteTime = 2026-07-08 22:59:27
```

測試 ROM:

```text
nes_instr_test/rom_singles/11-special.nes
```

這顆 ROM 是很短的 class-A `$6000` 測試, 用來證明問題不是 `apu_mixer` 專屬。

| 實驗 | ROM 路徑 | `--extra-ram` | 結果 | detection | frames |
|---|---|---:|---|---|---:|
| A | `tools/testrom/roms/nes_instr_test/rom_singles/11-special.nes` | 否 | timeout | none | 40 |
| B | `tools/testrom/roms/nes_instr_test/rom_singles/11-special.nes` | 是 | pass | 6000 | 11 |
| C | `nes-test-roms-master/checked/nes_instr_test/rom_singles/11-special.nes` | 否 | pass | 6000 | 11 |

實驗 A 的 JSON 摘要:

```json
{
  "rom": "11-special.nes",
  "status": "timeout",
  "detection": "none",
  "frames": 40,
  "maxFrames": 40,
  "engineVersion": "9a7987e",
  "resultText": "budget exhausted, no $6000 signature"
}
```

實驗 B 的 JSON 摘要:

```json
{
  "rom": "11-special.nes",
  "status": "pass",
  "detection": "6000",
  "frames": 11,
  "maxFrames": 40,
  "halfCycles": 7802056,
  "engineVersion": "9a7987e",
  "resultText": "11-special\n\nPassed"
}
```

實驗 C 的 JSON 摘要:

```json
{
  "rom": "11-special.nes",
  "status": "pass",
  "detection": "6000",
  "frames": 11,
  "maxFrames": 40,
  "halfCycles": 7802056,
  "engineVersion": "9a7987e",
  "resultText": "11-special\n\nPassed"
}
```

這三個結果形成完整因果鏈:

- 同一顆 DLL
- 同一顆 ROM
- bundled path 不加 extra RAM 會失敗
- bundled path 加 extra RAM 會成功
- 舊路徑不加 extra RAM 也會成功, 因為舊路徑命中 `nes-test-roms` heuristic

因此根因不是 DLL, 而是新路徑讓 `cart-extraram` 沒被掛上。

## 已排除事項

### 不是幀數預算

失敗是 `detection=none`, 代表 runner 從未看到 `$6000` 簽章, 不是 verdict 晚到。

### 不是 apu_mixer 專屬

`nes_instr_test/rom_singles/11-special.nes` 也能在 bundled path 不加 extra RAM 時重現 timeout, 所以 blast radius 至少涵蓋 class-A `$6000` 測試。

### 不是 ROM 內容損壞

`apu_mixer` 的 bundled ROM 與舊 `nes-test-roms-master/checked` ROM 已確認 MD5 相同。更重要的是, 同一個 bundled ROM 只要加 `--extra-ram` 就能 PASS。

### 不是 bit-exact core checksum 壞掉

用現有 `9a7987e` DLL 跑 golden benchmark:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --benchmark .\AprVisualBenchMark\roms\full_palette.nes `
  --bench-hc 300000 --extra-ram `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

得到 checksum:

```text
0x794A43ABDF169ADA
```

這與既有 300k golden checksum 一致, 所以 benchmark/core path 沒有被這次問題破壞。

### `P=$04` shim log 不是本次主因

失敗與成功的實驗都會出現:

```text
# [shim] Z flag post-reset inject: cpu.p1=0 P=$04
```

但加 `--extra-ram` 或使用舊路徑後仍可 PASS, 所以這行 log 不是 `detection=none` 的直接原因。若要追 CPU P flag 顯示值, 應另開 issue, 不要和本次 `$6000` RAM 漏掛混在一起。

## 影響範圍

受影響的是「透過 `tools/testrom/catalog.json` + bundled `tools/testrom/roms` 執行, 且依賴 `$6000` protocol 的 class-A 測試」。

因此不應只看 `apu_mixer`。以下類型都可能受影響:

- `apu_mixer`
- `nes_instr_test`
- `instr_test-v3/v5`
- `instr_timing`
- `cpu_interrupts`
- 其他 class-A `$6000` verdict 測試

不依賴 `$6000` 的 screen verdict 測試可能仍會 PASS, 例如 `cpu_timing_test6`, 這也解釋了為什麼當時只看到 screen 判定的測試通過。

## 正確修復方向

不要再讓 test-mode `$6000` RAM 依賴 ROM path 字串。

建議在 test mode 進入 `WireCore.LoadSystem(rom)` 前明確設定:

```csharp
WireCore.ForceExtraRam = true;
```

也就是說, `--test` / `--test-dir` 的語意應該是:

```text
test mode always provides the $6000 work RAM needed by the blargg protocol
```

這比在 heuristic 補 `tools/testrom/roms` 更穩, 因為未來 ROM 位置再搬家時不會重演。

次佳修法是在 `tools/testrom/run_tests.py` 對 class-A 或所有 test ROM 指令加 `--extra-ram`, 但這只修官方 runner, 不能修使用者直接執行 `AprVisual.S1 --test tools/testrom/roms/...` 的情境。

## 修復後最低驗證

1. 用 bundled path, 不加 `--extra-ram`, 直接跑短 class-A:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --test .\tools\testrom\roms\nes_instr_test\rom_singles\11-special.nes `
  --max-frames 40 --pin 4 --reset-hold-extra 1 `
  --test-json .\temp\verify_11_special.json `
  --test-screenshot .\temp\verify_11_special.png `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

期望:

```text
PASS, detection=6000, frames=11
```

2. 跑 `apu_mixer/triangle` bundled path, 不加 `--extra-ram`:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --test .\tools\testrom\roms\apu_mixer\triangle.nes `
  --max-frames 915 --pin 4 --reset-hold-extra 1 `
  --test-json .\temp\verify_apu_triangle.json `
  --test-screenshot .\temp\verify_apu_triangle.png `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

期望:

```text
PASS, detection=6000, frames ~= 607
```

3. 跑整組 `apu_mixer`:

```powershell
python .\tools\testrom\run_tests.py --filter apu_mixer --rerun --no-build --jobs 4
```

期望四筆全 PASS, 且 detection 均為 `6000`。

4. 再跑 golden checksum, 確認 benchmark path 沒被 test-mode 修法污染:

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

