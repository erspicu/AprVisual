# AprVisual.S1 可支援測試 ROM 清單（NROM、排除 PAL）

- 掃描日期：2026-07-02（重新驗證版；先前 2026-07-02 早間 session 產出不可靠，本檔為全新掃描結果）
- 來源目錄：`nes-test-roms-master/checked/`（共 184 個可解析 .nes）
- 判定規則：
  - **可支援** = iNES mapper 0（NROM）— AprVisual.S1 的 cartridge scope（見 CLAUDE.md：NROM only）
  - **排除 PAL** = `pal_apu_tests/` 目錄（blargg PAL APU 測試）、header TV flag 標 PAL、或檔名含 pal（`palette` 不算）
  - header 檢查結果：184 個 ROM 中，byte9/byte10 TV flag 非零者 = 0 個；NES 2.0 格式 = 0 個
    （blargg 系列不在 header 標示 PAL，因此 PAL 判定實際依目錄/檔名。）

## 摘要

| 類別 | 數量 |
|---|---|
| **可支援（NROM、非 PAL）** | **139** |
| 排除：非 NROM mapper | 35 |
| 排除：PAL | 10 |
| 合計 | 184 |

## 可支援清單（共 139，依目錄分組）

### `apu_mixer/`（4）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `apu_mixer/dmc.nes` | 2 | 1 | V |
| `apu_mixer/noise.nes` | 2 | 1 | V |
| `apu_mixer/square.nes` | 2 | 1 | V |
| `apu_mixer/triangle.nes` | 2 | 1 | V |

### `apu_reset/`（6）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `apu_reset/4015_cleared.nes` | 2 | 1 | V |
| `apu_reset/4017_timing.nes` | 2 | 1 | V |
| `apu_reset/4017_written.nes` | 2 | 1 | V |
| `apu_reset/irq_flag_cleared.nes` | 2 | 1 | V |
| `apu_reset/len_ctrs_enabled.nes` | 2 | 1 | V |
| `apu_reset/works_immediately.nes` | 2 | 1 | V |

### `apu_test/`（8）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `apu_test/rom_singles/1-len_ctr.nes` | 2 | 1 | V |
| `apu_test/rom_singles/2-len_table.nes` | 2 | 1 | V |
| `apu_test/rom_singles/3-irq_flag.nes` | 2 | 1 | V |
| `apu_test/rom_singles/4-jitter.nes` | 2 | 1 | V |
| `apu_test/rom_singles/5-len_timing.nes` | 2 | 1 | V |
| `apu_test/rom_singles/6-irq_flag_timing.nes` | 2 | 1 | V |
| `apu_test/rom_singles/7-dmc_basics.nes` | 2 | 1 | V |
| `apu_test/rom_singles/8-dmc_rates.nes` | 2 | 1 | V |

### `blargg_apu_2005.07.30/`（11）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `blargg_apu_2005.07.30/01.len_ctr.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/02.len_table.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/03.irq_flag.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/04.clock_jitter.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/05.len_timing_mode0.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/06.len_timing_mode1.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/07.irq_flag_timing.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/08.irq_timing.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/09.reset_timing.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/10.len_halt_timing.nes` | 1 | 0 | H |
| `blargg_apu_2005.07.30/11.len_reload_timing.nes` | 1 | 0 | H |

### `blargg_ppu_tests_2005.09.15b/`（5）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `blargg_ppu_tests_2005.09.15b/palette_ram.nes` | 1 | 0 | H |
| `blargg_ppu_tests_2005.09.15b/power_up_palette.nes` | 1 | 0 | H |
| `blargg_ppu_tests_2005.09.15b/sprite_ram.nes` | 1 | 0 | H |
| `blargg_ppu_tests_2005.09.15b/vbl_clear_time.nes` | 1 | 0 | H |
| `blargg_ppu_tests_2005.09.15b/vram_access.nes` | 1 | 0 | H |

### `branch_timing_tests/`（3）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `branch_timing_tests/1.Branch_Basics.nes` | 1 | 0 | H |
| `branch_timing_tests/2.Backward_Branch.nes` | 1 | 0 | H |
| `branch_timing_tests/3.Forward_Branch.nes` | 1 | 0 | H |

### `cpu_dummy_writes/`（2）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `cpu_dummy_writes/cpu_dummy_writes_oam.nes` | 2 | 1 | V |
| `cpu_dummy_writes/cpu_dummy_writes_ppumem.nes` | 2 | 1 | V |

### `cpu_exec_space/`（2）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `cpu_exec_space/test_cpu_exec_space_apu.nes` | 2 | 1 | V |
| `cpu_exec_space/test_cpu_exec_space_ppuio.nes` | 2 | 1 | V |

### `cpu_interrupts_v2/`（5）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `cpu_interrupts_v2/rom_singles/1-cli_latency.nes` | 2 | 1 | V |
| `cpu_interrupts_v2/rom_singles/2-nmi_and_brk.nes` | 2 | 1 | V |
| `cpu_interrupts_v2/rom_singles/3-nmi_and_irq.nes` | 2 | 1 | V |
| `cpu_interrupts_v2/rom_singles/4-irq_and_dma.nes` | 2 | 1 | V |
| `cpu_interrupts_v2/rom_singles/5-branch_delays_irq.nes` | 2 | 1 | V |

### `cpu_reset/`（2）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `cpu_reset/ram_after_reset.nes` | 2 | 1 | V |
| `cpu_reset/registers.nes` | 2 | 1 | V |

### `cpu_timing_test6/`（1）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `cpu_timing_test6/cpu_timing_test.nes` | 1 | 0 | H |

### `dmc_dma_during_read4/`（5）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `dmc_dma_during_read4/dma_2007_read.nes` | 2 | 0 | V |
| `dmc_dma_during_read4/dma_2007_write.nes` | 2 | 0 | V |
| `dmc_dma_during_read4/dma_4016_read.nes` | 2 | 0 | V |
| `dmc_dma_during_read4/double_2007_read.nes` | 2 | 0 | V |
| `dmc_dma_during_read4/read_write_2007.nes` | 2 | 0 | V |

### `instr_misc/`（4）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `instr_misc/rom_singles/01-abs_x_wrap.nes` | 2 | 1 | V |
| `instr_misc/rom_singles/02-branch_wrap.nes` | 2 | 1 | V |
| `instr_misc/rom_singles/03-dummy_reads.nes` | 2 | 1 | V |
| `instr_misc/rom_singles/04-dummy_reads_apu.nes` | 2 | 1 | V |

### `instr_test-v3/`（15）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `instr_test-v3/rom_singles/01-implied.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/02-immediate.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/03-zero_page.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/04-zp_xy.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/05-absolute.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/06-abs_xy.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/07-ind_x.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/08-ind_y.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/09-branches.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/10-stack.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/11-jmp_jsr.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/12-rts.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/13-rti.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/14-brk.nes` | 2 | 1 | V |
| `instr_test-v3/rom_singles/15-special.nes` | 2 | 1 | V |

### `instr_test-v5/`（16）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `instr_test-v5/rom_singles/01-basics.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/02-implied.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/03-immediate.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/04-zero_page.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/05-zp_xy.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/06-absolute.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/07-abs_xy.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/08-ind_x.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/09-ind_y.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/10-branches.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/11-stack.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/12-jmp_jsr.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/13-rts.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/14-rti.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/15-brk.nes` | 2 | 1 | V |
| `instr_test-v5/rom_singles/16-special.nes` | 2 | 1 | V |

### `instr_timing/`（2）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `instr_timing/rom_singles/1-instr_timing.nes` | 2 | 1 | V |
| `instr_timing/rom_singles/2-branch_timing.nes` | 2 | 1 | V |

### `nes_instr_test/`（11）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `nes_instr_test/rom_singles/01-implied.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/02-immediate.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/03-zero_page.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/04-zp_xy.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/05-absolute.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/06-abs_xy.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/07-ind_x.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/08-ind_y.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/09-branches.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/10-stack.nes` | 2 | 1 | V |
| `nes_instr_test/rom_singles/11-special.nes` | 2 | 1 | V |

### `oam_read/`（1）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `oam_read/oam_read.nes` | 2 | 1 | V |

### `ppu_open_bus/`（1）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `ppu_open_bus/ppu_open_bus.nes` | 2 | 1 | V |

### `ppu_vbl_nmi/`（10）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `ppu_vbl_nmi/rom_singles/01-vbl_basics.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/02-vbl_set_time.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/03-vbl_clear_time.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/04-nmi_control.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/05-nmi_timing.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/06-suppression.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/07-nmi_on_timing.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/08-nmi_off_timing.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/09-even_odd_frames.nes` | 2 | 1 | V |
| `ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes` | 2 | 1 | V |

### `sprdma_and_dmc_dma/`（2）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma.nes` | 2 | 1 | V |
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma_512.nes` | 2 | 1 | V |

### `sprite_hit_tests_2005.10.05/`（11）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `sprite_hit_tests_2005.10.05/01.basics.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/02.alignment.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/03.corners.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/04.flip.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/05.left_clip.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/06.right_edge.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/07.screen_bottom.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/08.double_height.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/09.timing_basics.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/10.timing_order.nes` | 1 | 0 | H |
| `sprite_hit_tests_2005.10.05/11.edge_timing.nes` | 1 | 0 | H |

### `sprite_overflow_tests/`（5）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `sprite_overflow_tests/1.Basics.nes` | 1 | 0 | H |
| `sprite_overflow_tests/2.Details.nes` | 1 | 0 | H |
| `sprite_overflow_tests/3.Timing.nes` | 1 | 0 | H |
| `sprite_overflow_tests/4.Obscure.nes` | 1 | 0 | H |
| `sprite_overflow_tests/5.Emulator.nes` | 1 | 0 | H |

### `vbl_nmi_timing/`（7）

| ROM | PRG×16K | CHR×8K | Mirror |
|---|---|---|---|
| `vbl_nmi_timing/1.frame_basics.nes` | 1 | 0 | H |
| `vbl_nmi_timing/2.vbl_timing.nes` | 1 | 0 | H |
| `vbl_nmi_timing/3.even_odd_frames.nes` | 1 | 0 | H |
| `vbl_nmi_timing/4.vbl_clear_timing.nes` | 1 | 0 | H |
| `vbl_nmi_timing/5.nmi_suppression.nes` | 1 | 0 | H |
| `vbl_nmi_timing/6.nmi_disable.nes` | 1 | 0 | H |
| `vbl_nmi_timing/7.nmi_timing.nes` | 1 | 0 | H |

## 排除：非 NROM（35）

| ROM | Mapper |
|---|---|
| `apu_test/apu_test.nes` | 1 |
| `blargg_nes_cpu_test5/cpu.nes` | 1 |
| `blargg_nes_cpu_test5/official.nes` | 1 |
| `cpu_interrupts_v2/cpu_interrupts.nes` | 1 |
| `instr_misc/instr_misc.nes` | 1 |
| `instr_test-v3/all_instrs.nes` | 1 |
| `instr_test-v3/official_only.nes` | 1 |
| `instr_test-v5/all_instrs.nes` | 1 |
| `instr_test-v5/official_only.nes` | 1 |
| `instr_timing/instr_timing.nes` | 1 |
| `ppu_vbl_nmi/ppu_vbl_nmi.nes` | 1 |
| `cpu_dummy_reads/cpu_dummy_reads.nes` | 3 |
| `ppu_read_buffer/test_ppu_read_buffer.nes` | 3 |
| `read_joy3/count_errors.nes` | 3 |
| `read_joy3/count_errors_fast.nes` | 3 |
| `read_joy3/test_buttons.nes` | 3 |
| `read_joy3/thorough_test.nes` | 3 |
| `mmc3_irq_tests/1.Clocking.nes` | 4 |
| `mmc3_irq_tests/2.Details.nes` | 4 |
| `mmc3_irq_tests/3.A12_clocking.nes` | 4 |
| `mmc3_irq_tests/4.Scanline_timing.nes` | 4 |
| `mmc3_irq_tests/5.MMC3_rev_A.nes` | 4 |
| `mmc3_irq_tests/6.MMC3_rev_B.nes` | 4 |
| `mmc3_test/1-clocking.nes` | 4 |
| `mmc3_test/2-details.nes` | 4 |
| `mmc3_test/3-A12_clocking.nes` | 4 |
| `mmc3_test/4-scanline_timing.nes` | 4 |
| `mmc3_test/5-MMC3.nes` | 4 |
| `mmc3_test/6-MMC6.nes` | 4 |
| `mmc3_test_2/rom_singles/1-clocking.nes` | 4 |
| `mmc3_test_2/rom_singles/2-details.nes` | 4 |
| `mmc3_test_2/rom_singles/3-A12_clocking.nes` | 4 |
| `mmc3_test_2/rom_singles/4-scanline_timing.nes` | 4 |
| `mmc3_test_2/rom_singles/5-MMC3.nes` | 4 |
| `mmc3_test_2/rom_singles/6-MMC3_alt.nes` | 4 |

## 排除：PAL（10）

| ROM | 判定依據 |
|---|---|
| `pal_apu_tests/01.len_ctr.nes` | dir |
| `pal_apu_tests/02.len_table.nes` | dir |
| `pal_apu_tests/03.irq_flag.nes` | dir |
| `pal_apu_tests/04.clock_jitter.nes` | dir |
| `pal_apu_tests/05.len_timing_mode0.nes` | dir |
| `pal_apu_tests/06.len_timing_mode1.nes` | dir |
| `pal_apu_tests/07.irq_flag_timing.nes` | dir |
| `pal_apu_tests/08.irq_timing.nes` | dir |
| `pal_apu_tests/10.len_halt_timing.nes` | dir |
| `pal_apu_tests/11.len_reload_timing.nes` | dir |

## 附註

- `palette` 字樣（如 `blargg_ppu_tests/palette_ram.nes`、`power_up_palette.nes`）指 PPU 調色盤，與 PAL 電視規格無關，不排除。
- 可支援 ≠ 已驗證通過：本清單只依 mapper/規格判定「S1 載得起來」，實際 PASS/FAIL 需逐一執行（blargg $6000 協定或畫面判讀）。
- blargg $6000 結果協定需要 cartridge $6000-$7FFF extra RAM;S1 對 test ROM 已自動啟用（`WireCore.System.cs` 的 isTestRom / `--extra-ram`）。
