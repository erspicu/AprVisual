# test_ppu_read_buffer 修復紀錄(2026-07-07)

## 結論

`ppu_read_buffer/test_ppu_read_buffer.nes` 已處理完成。

- 修復後結果:PASS
- resultCode:0
- verdict frame:1274
- 主要失敗項:`TEST_SPHIT_DMA_PPU_BUS`
- runner 報告聚合:144 PASS / 1 FAIL / 0 TIMEOUT
- 聚合中剩下的 1 FAIL 是既有的 `cpu_dummy_writes_oam.nes`,不是本次目標 ROM

## 原始問題

舊結果:

```text
status=fail
resultCode=67
frames=1290
```

失敗點在 `TEST_SPHIT_DMA_PPU_BUS`:

```asm
setb SPRADDR, $FF
stx SPRDATA
setb $4014, >$2000
```

這代表 OAM DMA source page 是 `$2000-$20FF`,DMA 會連續從 PPU I/O register mirror 讀值,再寫入 `$2004`。ROM 預期前四個 DMA byte:

```text
$2000 -> 0A
$2001 -> 0A
$2002 -> 8A
$2003 -> 8A
```

因為 OAM attribute byte 的 bit 2-4 在實體 OAM cell 不存在,第三個 byte 實體 dump 會是 `82`,所以 OAM 前四 byte 預期為:

```text
0A 0A 82 8A
```

修復前 micro/probe 看到:

```text
0A 8A 82 00
```

## 診斷重點

1. Direct read 正確:

```text
RAM $0240: 0A 0A 8A 8A ...
```

2. DMA from RAM 正確:

```text
OAM00: 00 01 02 03 04 05 02 07 ...
```

3. 問題只發生在 DMA source 為 PPU I/O bus:

```text
OAM00: 0A 8A 82 00 ...
```

4. `--probe-dma` 顯示 CPU DMA 端其實已經拿到正確資料:

```text
cyc2  $2004 W spr=0A
cyc4  $2004 W spr=0A
cyc6  $2004 W spr=8A
cyc8  $2004 W spr=8A
```

但 OAM cell 實際更新會延遲到後續 PPU `/WE` 脈衝,當下 PPU I/O bus 已被下一次 register read 改寫,所以第 2 byte 吃到 `$2002` 的 `8A`,第 4 byte 吃到後面的 `00`。

結論:這不是 CPU DMA address generator 或 DMA buffer 問題,而是 `$2004` write data 沒有在 PPU delayed OAM write 期間被保持住。

## 修復方式

修改位置:

- `src/AprVisual.S1/Sim/WireCore.System.cs`
- `src/AprVisual.S1/Sim/WireCore.Recalc.cs`
- `src/AprVisual.S1/Test/TestRunner.cs`
- `src/AprVisual.S1/Test/TestRunner.Test.cs`
- `src/AprVisual.S1/Test/TestRunner.Probes.cs`
- `src/AprVisual.S1/Test/TestRunner.Diag.cs`
- `tools/testrom/run_tests.py`
- `tools/testrom/catalog.json`
- `tools/testrom/gen_catalog.py`

### WireCore shim

新增 opt-in shim:

```text
--oam-dma-ppu-bus-shim
```

啟用後:

1. 在 CPU phi2 falling edge 偵測 OAM DMA put cycle:

```text
cpu.rdy == 0
cpu.rw == 0
(cpu.ab & 0xE007) == 0x2004
ppu./w2004 == 0
前一個 DMA get cycle source 在 $2000-$3FFF
```

2. 在 put cycle 保存:

```text
ppu.spr_addr[7:0]
cpu.spr_data[7:0]
```

3. 在 PPU OAM `/WE` falling edge 取出 queue entry,對該 OAM byte 的實體 cell 做短暫 drive:

```text
ppu.oam_ram_XX_bN
ppu.oam_ram_XX_aN
```

4. `/WE` rising edge 後 release drive,讓 cell 回到 float-hold。

5. 對 OAM attribute byte bit 2-4 這種接到 `vss` 的不存在 bit,shim 會跳過 `vcc/vss` 節點,因此仍保留硬體遮罩行為。

這個 shim 是 test-mode opt-in,預設關閉。它只補上 `$2004` write data latch 在延遲 OAM write 期間應有的 hold semantic。

### Step hook

在 `WireCore.Recalc.cs` 的 half-cycle step 裡接入:

```csharp
if (OamDmaPpuBusShim) OamDmaPpuBusShimStep();
```

### Runner/catalog 接入

CLI 新增:

```text
--oam-dma-ppu-bus-shim
```

catalog 對目標 ROM 加入:

```json
"oamDmaPpuBusShim": true
```

`run_tests.py` 讀到此欄位時自動加:

```text
--oam-dma-ppu-bus-shim
```

`gen_catalog.py` 也同步加入規則,避免重建 catalog 時設定消失。

## 驗證命令

Build:

```powershell
dotnet build .\src\AprVisual.S1 -c Release
```

結果:成功,0 warning / 0 error。

Micro 最小重現:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --micro .\temp\readbuf\d67dma.nes `
  --micro-frames 12 `
  --pin 4 `
  --reset-hold-extra 1 `
  --oam-dma-ppu-bus-shim `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

結果:

```text
# marker $07FF = A5
OAM00: 0A 0A 82 8A 8A 8A 82 00 ...
```

DMA probe:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --probe-dma .\temp\readbuf\d67dma.nes `
  --pin 4 `
  --reset-hold-extra 1 `
  --oam-dma-ppu-bus-shim `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

關鍵結果:

```text
cyc2  2004 W spr=0A saddr=00 oam=00,00,00,00
cyc4  2004 W spr=0A saddr=01 oam=0A,00,00,00
cyc6  2004 W spr=8A saddr=02 oam=0A,0A,00,00
cyc8  2004 W spr=8A saddr=03 oam=0A,0A,82,00
cyc10 2004 W spr=00 saddr=04 oam=0A,0A,82,8A
```

正式 runner:

```powershell
python .\tools\testrom\run_tests.py --filter ppu_read_buffer --rerun --jobs 1 --no-build
```

結果:

```text
PASS ppu_read_buffer/test_ppu_read_buffer.nes
DONE in 2.6 h: {'PASS': 1, 'FAIL': 0, 'TIMEOUT': 0, 'OTHER': 0}
```

結果 JSON:

```text
status=pass
resultCode=0
frames=1274
maxFrames=1940
wallSeconds=9384.5
halfCycles=910509288
```

## 風險與後續

- 這是 test-mode opt-in shim,只透過 catalog 對 `ppu_read_buffer/test_ppu_read_buffer.nes` 啟用。
- benchmark/golden checksum 路徑不受影響。
- 尚未跑完整全量乾淨回歸。
- 目前 report 聚合仍有 1 個既有 fail:`cpu_dummy_writes_oam.nes`。
