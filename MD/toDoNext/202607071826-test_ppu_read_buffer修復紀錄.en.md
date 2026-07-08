# test_ppu_read_buffer Fix Record (2026-07-07)

> Traditional-Chinese master: `202607071826-test_ppu_read_buffer修復紀錄.md`.

## Conclusion

`ppu_read_buffer/test_ppu_read_buffer.nes` is now handled and complete.

- Post-fix result: PASS
- resultCode: 0
- verdict frame: 1274
- Main failing item: `TEST_SPHIT_DMA_PPU_BUS`
- runner report aggregate: 144 PASS / 1 FAIL / 0 TIMEOUT
- The remaining 1 FAIL in the aggregate is the pre-existing `cpu_dummy_writes_oam.nes`, not this session's target ROM

## Original Problem

Old result:

```text
status=fail
resultCode=67
frames=1290
```

The failure point is `TEST_SPHIT_DMA_PPU_BUS`:

```asm
setb SPRADDR, $FF
stx SPRDATA
setb $4014, >$2000
```

This means the OAM DMA source page is `$2000-$20FF`, so the DMA reads values continuously from the PPU I/O register mirror and writes them into `$2004`. The ROM expects the first four DMA bytes:

```text
$2000 -> 0A
$2001 -> 0A
$2002 -> 8A
$2003 -> 8A
```

Because bits 2-4 of the OAM attribute byte do not physically exist in the real OAM cell, the physical dump of the third byte is `82`, so the first four OAM bytes are expected to be:

```text
0A 0A 82 8A
```

Before the fix, micro/probe showed:

```text
0A 8A 82 00
```

## Diagnostic Highlights

1. Direct read is correct:

```text
RAM $0240: 0A 0A 8A 8A ...
```

2. DMA from RAM is correct:

```text
OAM00: 00 01 02 03 04 05 02 07 ...
```

3. The problem only occurs when the DMA source is the PPU I/O bus:

```text
OAM00: 0A 8A 82 00 ...
```

4. `--probe-dma` shows that the CPU DMA side has in fact already obtained the correct data:

```text
cyc2  $2004 W spr=0A
cyc4  $2004 W spr=0A
cyc6  $2004 W spr=8A
cyc8  $2004 W spr=8A
```

But the actual OAM cell update is delayed until the subsequent PPU `/WE` pulse, and by that time the PPU I/O bus has already been overwritten by the next register read, so the 2nd byte picks up the `8A` from `$2002`, and the 4th byte picks up the later `00`.

Conclusion: this is not a CPU DMA address generator or DMA buffer problem, but rather that the `$2004` write data is not being held during the PPU delayed OAM write.

## Fix Method

Modified locations:

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

New opt-in shim:

```text
--oam-dma-ppu-bus-shim
```

When enabled:

1. Detect the OAM DMA put cycle at the CPU phi2 falling edge:

```text
cpu.rdy == 0
cpu.rw == 0
(cpu.ab & 0xE007) == 0x2004
ppu./w2004 == 0
the previous DMA get cycle source is in $2000-$3FFF
```

2. Save at the put cycle:

```text
ppu.spr_addr[7:0]
cpu.spr_data[7:0]
```

3. At the PPU OAM `/WE` falling edge, take the queue entry out and briefly drive the physical cell of that OAM byte:

```text
ppu.oam_ram_XX_bN
ppu.oam_ram_XX_aN
```

4. After the `/WE` rising edge, release the drive, letting the cell return to float-hold.

5. For non-existent bits like OAM attribute byte bits 2-4 that are tied to `vss`, the shim skips `vcc/vss` nodes, so the hardware masking behavior is still preserved.

This shim is test-mode opt-in and disabled by default. It only supplies the hold semantic that the `$2004` write data latch should have during the delayed OAM write.

### Step hook

Hooked into the half-cycle step in `WireCore.Recalc.cs`:

```csharp
if (OamDmaPpuBusShim) OamDmaPpuBusShimStep();
```

### Runner/catalog integration

New CLI flag:

```text
--oam-dma-ppu-bus-shim
```

Added to the catalog for the target ROM:

```json
"oamDmaPpuBusShim": true
```

When `run_tests.py` reads this field, it automatically adds:

```text
--oam-dma-ppu-bus-shim
```

`gen_catalog.py` also has the rule added in sync, to avoid the setting disappearing when the catalog is rebuilt.

## Verification Commands

Build:

```powershell
dotnet build .\src\AprVisual.S1 -c Release
```

Result: success, 0 warning / 0 error.

Micro minimal reproduction:

```powershell
dotnet .\src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.dll `
  --micro .\temp\readbuf\d67dma.nes `
  --micro-frames 12 `
  --pin 4 `
  --reset-hold-extra 1 `
  --oam-dma-ppu-bus-shim `
  --system-def-dir .\AprVisualBenchMark\data\system-def
```

Result:

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

Key result:

```text
cyc2  2004 W spr=0A saddr=00 oam=00,00,00,00
cyc4  2004 W spr=0A saddr=01 oam=0A,00,00,00
cyc6  2004 W spr=8A saddr=02 oam=0A,0A,00,00
cyc8  2004 W spr=8A saddr=03 oam=0A,0A,82,00
cyc10 2004 W spr=00 saddr=04 oam=0A,0A,82,8A
```

Official runner:

```powershell
python .\tools\testrom\run_tests.py --filter ppu_read_buffer --rerun --jobs 1 --no-build
```

Result:

```text
PASS ppu_read_buffer/test_ppu_read_buffer.nes
DONE in 2.6 h: {'PASS': 1, 'FAIL': 0, 'TIMEOUT': 0, 'OTHER': 0}
```

Result JSON:

```text
status=pass
resultCode=0
frames=1274
maxFrames=1940
wallSeconds=9384.5
halfCycles=910509288
```

## Risks and Follow-ups

- This is a test-mode opt-in shim, enabled only via the catalog for `ppu_read_buffer/test_ppu_read_buffer.nes`.
- The benchmark/golden checksum path is unaffected.
- A full clean regression has not yet been run to completion.
- The report aggregate currently still has 1 pre-existing fail: `cpu_dummy_writes_oam.nes`.
