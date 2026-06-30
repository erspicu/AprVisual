# Aggressive Lowering Strategy 4 Macro Study - 2026-07-01

## 背景

前一份 study 測過策略 1/2/3/5 的 aggressive lowering 潛力。

初版 1/2/3/5 直接 destructive lowering 會讓 `full_palette.nes` 第 20 frame 變成單色灰畫面，CPU 也卡在錯誤流程。後來改成 memory-protected 版本：

- no-pullup / floating node 保留
- callback node 保留
- forceCompute node 保留
- named anchor node 保留
- 若同一個 c1c2 pass-channel component 內有上述任一類，整個 component 保護起來

結果 `full_palette.nes` 第 20 frame 可以正常出圖。

這次進一步測策略 4：

> 將 pullup + GND-only pulldown network 替換成 macro evaluator。

## 實作方式

新增 opt-in 實驗旗標：

```text
--aggressive-lower4
```

和前一版一起使用：

```text
--aggressive-lower --aggressive-lower4
```

實作位置：

- `src/AprVisual.S1/Sim/WireCore.LowerMacro.cs`
- `src/AprVisual.S1/Sim/WireCore.Recalc.cs`
- `src/AprVisual.S1/Sim/WireCore.System.cs`
- `src/AprVisual.S1/Test/TestRunner.cs`

目前策略 4 只處理比較保守的 subset：

- output node 有 pullup
- output node 沒有 callback
- output node 不在 forceCompute list
- output node 的 channel 只連到 GND
- pulldown transistor 的 gate 集合被收集成 macro inputs

原本：

```text
output -- transistor(gate=input) -- GND
```

改成：

```text
output state = any(input gates high) ? 0 : 1
```

pulldown transistor rows 會從 build-time transistor list 移除。
output node 仍然保留在 `NodeStates`。

## 測試命令

第 20 frame 圖像測試：

```powershell
dotnet .\bin\Release\net11.0\AprVisual.S1.dll `
  --screenshot C:\ai_project\AprVisual\AprVisualBenchMark\roms\full_palette.nes `
  --frames 20 `
  --out C:\ai_project\AprVisual\MD\lowering\full_palette_frame20_aggressive_1235_plus4_macro.png `
  --extra-ram `
  --aggressive-lower `
  --aggressive-lower4 `
  --system-def-dir C:\ai_project\AprVisual\AprVisualBenchMark\data\system-def
```

400k half-cycle benchmark / checksum / reduction 統計：

```powershell
dotnet .\bin\Release\net11.0\AprVisual.S1.dll `
  --benchmark C:\ai_project\AprVisual\AprVisualBenchMark\roms\full_palette.nes `
  --bench-hc 400000 `
  --extra-ram `
  --aggressive-lower `
  --aggressive-lower4 `
  --system-def-dir C:\ai_project\AprVisual\AprVisualBenchMark\data\system-def
```

Build：

```text
dotnet build -c Release
0 warnings, 0 errors
```

## 第 20 Frame 結果

輸出圖：

```text
C:\ai_project\AprVisual\MD\lowering\full_palette_frame20_aggressive_1235_plus4_macro.png
```

結果：

- 沒有破圖。
- 畫面和前一張 `1/2/3/5 memory-protected` 的正常 palette 圖一致。
- frame log 顯示 CPU 沒有卡在初版 destructive lowering 的錯誤狀態。

代表策略 4 的保守 macro subset 至少在 `full_palette.nes` 第 20 frame 沒有立即破壞畫面層行為。

## Checksum

目前 checksum 是完整 `NodeStates` array 的 FNV hash，因此比畫面正確更嚴格。

400k half-cycle 結果：

```text
baseline checksum:
0x9174E19D961CB6E5

1/2/3/5 memory-protected checksum:
0x39152FA3874E8F58

1/2/3/5 + strategy4 macro checksum:
0x196C07D10F09AD87
```

結論：

- 策略 4 版本不是 S1 node-level bit-exact。
- 畫面正確不代表完整 internal node state 相同。
- 若仍要求 current checksum 相同，這條不能進主線。

## 節點與連通減少量

以 safe lowering 後為基準：

```text
safe lowering:
nodes       14,723
transistors 26,775
```

套 `1/2/3/5 memory-protected`：

```text
nodes       14,723 -> 14,665
delta       -58 nodes = -0.39%

transistors 26,775 -> 26,616
delta       -159 rows = -0.59%
```

再套策略 4 pulldown macro：

```text
nodes       14,665 -> 14,665
delta       0 nodes

transistors 26,616 -> 20,339
delta       -6,277 rows = -23.6%
```

整組 `1/2/3/5 + 4` 相對 safe-lowering baseline：

```text
nodes       14,723 -> 14,665
delta       -58 nodes = -0.39%

transistors 26,775 -> 20,339
delta       -6,436 rows = -24.0%
```

策略 4 覆蓋範圍：

```text
macro outputs:          3,368 nodes
named macro outputs:    1,323 nodes
removed pulldown rows:  6,277
watched gate nodes:     3,553
max inputs per output:  16
```

## 重要解讀

策略 4 幾乎不減 node。

它保留 output node，因為這些 output node 仍然需要存在於 `NodeStates`，供後續 gate、handler、video/cpu/ppu path 使用。

策略 4 真正減少的是「連通」：

- 原本每個 output node 透過多顆 pulldown transistor 連到 GND。
- macro 化後，這些 transistor rows 被拔掉。
- output 的值改由 macro evaluator 依照 input gate states 計算。

因此，從模型結構看：

```text
node reduction:       很小，約 0.39%
connection reduction: 很大，約 24%
```

這說明前面 census 的判斷是對的：

- 1/2/3/5 主要是 topology lowering，剩餘可刪 node 不多。
- 策略 4 是真正的大空間，但它不是單純 node deletion，而是 transistor network replacement。

## 效能觀察

目前策略 4 實作不是效能版。

400k hc 粗測：

```text
baseline:
131.2K hc/s

1/2/3/5 memory-protected:
131.1K hc/s

1/2/3/5 + strategy4 macro:
119.4K ~ 121.2K hc/s
```

策略 4 目前變慢，原因不是概念必然錯，而是 prototype 用 managed arrays / macro maps / extra enqueue path 實作，成本偏高。

目前這份實作主要用來確認：

- 策略 4 能不能大幅減少 transistor rows。
- 第 20 frame 是否立即破圖。
- macro subset 是否有後續研究價值。

## 結論

這次策略 4 study 的結論：

1. `1/2/3/5 memory-protected + strategy4 macro` 可以跑出 `full_palette.nes` 第 20 frame，沒有破圖。
2. 但 checksum 不同，不是 S1 bit-exact。
3. node 幾乎沒有再減少；整組只少 58 nodes，約 0.39%。
4. transistor rows / 連通大幅減少；整組少 6,436 rows，約 24.0%。
5. 策略 4 覆蓋 3,368 個 pullup/GND-only output nodes，拔掉 6,277 條 pulldown rows。
6. 目前 prototype 效能變慢，之後若要工程化，需要改成低成本資料結構與 hotpath-friendly implementation。

務實判斷：

- 若目標是維持 S1 checksum，策略 4 不可直接進主線。
- 若目標是探索 S2 / macro-level / logic-equivalent 方向，策略 4 是目前看起來最有量的降低連通方向。
- 它不是 node-reduction 技術，而是 transistor-network reduction / macro replacement 技術。
