# S1 Test-ROM Detection-Method Filter (139 → Graded List)

> Traditional-Chinese master: `2026-07-02-s1-testrom-detection-filter.md`.

- Date: 2026-07-02
- Parent set: the 139 ROMs from `2026-07-02-aprvisual-s1-supported-testroms.en.md` (NROM, non-PAL)
- Purpose: S1 is slow (~5 s/frame), so it **does not adopt the "90 consecutive frames of a stable screen" verdict**;
  it only accepts (1) the `$6000` protocol, and (2) methods that produce a result from a single per-frame read. The 90-frame-stable type is still kept on the list (flagged), pending a later decision.
- Evidence sources (double verification):
  1. **Static scan**: whether the PRG contains `STA $6000/$6001` (op `8D 00 60` / `8D 01 60`) = it writes the blargg protocol
  2. **AprNes measurement** (`site/report/results.json`): the `result_text` content tells which verdict path is actually taken——
     `$6004` rich text = it takes the `$6000` protocol; `(screen: ...)` = AprNes uses 90-frame stability + on-screen text; `(screen CRC:)` = CRC comparison

## Summary

| Class | Detection method | Count | S1 recommendation |
|---|---|---|---|
| **A** | `$6000` protocol (read one byte per frame, stop as soon as a result appears) | **83** | **Preferred core test set** |
| **A-r** | `$6000` protocol + requires an automatic soft reset (`$6000=$81`) | **8** | Preferred, but S1 must first verify pulling the res line mid-run |
| **B** | On-screen text (AprNes uses 90-frame stability; S1 can instead **scan the nametable per frame for terminal text**) | **46** | Usable——scanning 960 bytes per frame is a negligible cost |
| **C** | Screen CRC comparison (against a known set of valid CRCs) | **2** | Usable——compare when the CRC text appears; a CRC table is attached |
| | Total | **139** | |

> The B/C classes are implemented in AprNes as "read the verdict only after the screen has been stable for 90 frames," but that is a general shortcut behavioral-layer simulators use for speed;
> the result text of these ROMs is **terminal** (after `Passed`/`Failed`/the `$0X` code is printed it halts in a dead loop),
> so S1 can directly scan the nametable per frame for the terminal marker, without waiting for stability——which satisfies the "one decision per frame" acceptance condition.

## A: `$6000` protocol (preferred core) (83)

### `apu_mixer/` (4)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `apu_mixer/dmc.nes` | ✓ | dmc channel mixing test |
| `apu_mixer/noise.nes` | ✓ | noise channel mixing test |
| `apu_mixer/square.nes` | ✓ | square channel mixing test |
| `apu_mixer/triangle.nes` | ✓ | triangle channel mixing test |

### `apu_test/` (8)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `apu_test/rom_singles/1-len_ctr.nes` | ✓ | 1-len_ctr |
| `apu_test/rom_singles/2-len_table.nes` | ✓ | 2-len_table |
| `apu_test/rom_singles/3-irq_flag.nes` | ✓ | 3-irq_flag |
| `apu_test/rom_singles/4-jitter.nes` | ✓ | 4-jitter |
| `apu_test/rom_singles/5-len_timing.nes` | ✓ | 5-len_timing |
| `apu_test/rom_singles/6-irq_flag_timing.nes` | ✓ | 6-irq_flag_timing |
| `apu_test/rom_singles/7-dmc_basics.nes` | ✓ | 7-dmc_basics |
| `apu_test/rom_singles/8-dmc_rates.nes` | ✓ | 8-dmc_rates |

### `cpu_dummy_writes/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `cpu_dummy_writes/cpu_dummy_writes_oam.nes` | ✓ | TEST: cpu_dummy_writes_oam |
| `cpu_dummy_writes/cpu_dummy_writes_ppumem.nes` | ✓ | TEST: cpu_dummy_writes_ppumem |

### `cpu_exec_space/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `cpu_exec_space/test_cpu_exec_space_apu.nes` | ✓ | TEST: test_cpu_exec_space_apu |
| `cpu_exec_space/test_cpu_exec_space_ppuio.nes` | ✓ | TEST:test_cpu_exec_space_ppuio |

### `cpu_interrupts_v2/` (5)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `cpu_interrupts_v2/rom_singles/1-cli_latency.nes` | ✓ | 1-cli_latency |
| `cpu_interrupts_v2/rom_singles/2-nmi_and_brk.nes` | ✓ | NMI BRK 00 |
| `cpu_interrupts_v2/rom_singles/3-nmi_and_irq.nes` | ✓ | NMI BRK |
| `cpu_interrupts_v2/rom_singles/4-irq_and_dma.nes` | ✓ | 0 +0 |
| `cpu_interrupts_v2/rom_singles/5-branch_delays_irq.nes` | ✓ | test_jmp |

### `instr_misc/` (4)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `instr_misc/rom_singles/01-abs_x_wrap.nes` | ✓ | 01-abs_x_wrap |
| `instr_misc/rom_singles/02-branch_wrap.nes` | ✓ | 02-branch_wrap |
| `instr_misc/rom_singles/03-dummy_reads.nes` | ✓ | 03-dummy_reads |
| `instr_misc/rom_singles/04-dummy_reads_apu.nes` | ✓ | 04-dummy_reads_apu |

### `instr_test-v3/` (15)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `instr_test-v3/rom_singles/01-implied.nes` | ✓ | 01-implied |
| `instr_test-v3/rom_singles/02-immediate.nes` | ✓ | 02-immediate |
| `instr_test-v3/rom_singles/03-zero_page.nes` | ✓ | 03-zero_page |
| `instr_test-v3/rom_singles/04-zp_xy.nes` | ✓ | 04-zp_xy |
| `instr_test-v3/rom_singles/05-absolute.nes` | ✓ | 05-absolute |
| `instr_test-v3/rom_singles/06-abs_xy.nes` | ✓ | 06-abs_xy |
| `instr_test-v3/rom_singles/07-ind_x.nes` | ✓ | 07-ind_x |
| `instr_test-v3/rom_singles/08-ind_y.nes` | ✓ | 08-ind_y |
| `instr_test-v3/rom_singles/09-branches.nes` | ✓ | 09-branches |
| `instr_test-v3/rom_singles/10-stack.nes` | ✓ | 10-stack |
| `instr_test-v3/rom_singles/11-jmp_jsr.nes` | ✓ | 11-jmp_jsr |
| `instr_test-v3/rom_singles/12-rts.nes` | ✓ | 12-rts |
| `instr_test-v3/rom_singles/13-rti.nes` | ✓ | 13-rti |
| `instr_test-v3/rom_singles/14-brk.nes` | ✓ | 14-brk |
| `instr_test-v3/rom_singles/15-special.nes` | ✓ | 15-special |

### `instr_test-v5/` (16)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `instr_test-v5/rom_singles/01-basics.nes` | ✓ | 01-basics |
| `instr_test-v5/rom_singles/02-implied.nes` | ✓ | 02-implied |
| `instr_test-v5/rom_singles/03-immediate.nes` | ✓ | 03-immediate |
| `instr_test-v5/rom_singles/04-zero_page.nes` | ✓ | 04-zero_page |
| `instr_test-v5/rom_singles/05-zp_xy.nes` | ✓ | 05-zp_xy |
| `instr_test-v5/rom_singles/06-absolute.nes` | ✓ | 06-absolute |
| `instr_test-v5/rom_singles/07-abs_xy.nes` | ✓ | 07-abs_xy |
| `instr_test-v5/rom_singles/08-ind_x.nes` | ✓ | 08-ind_x |
| `instr_test-v5/rom_singles/09-ind_y.nes` | ✓ | 09-ind_y |
| `instr_test-v5/rom_singles/10-branches.nes` | ✓ | 10-branches |
| `instr_test-v5/rom_singles/11-stack.nes` | ✓ | 11-stack |
| `instr_test-v5/rom_singles/12-jmp_jsr.nes` | ✓ | 12-jmp_jsr |
| `instr_test-v5/rom_singles/13-rts.nes` | ✓ | 13-rts |
| `instr_test-v5/rom_singles/14-rti.nes` | ✓ | 14-rti |
| `instr_test-v5/rom_singles/15-brk.nes` | ✓ | 15-brk |
| `instr_test-v5/rom_singles/16-special.nes` | ✓ | 16-special |

### `instr_timing/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `instr_timing/rom_singles/1-instr_timing.nes` | ✓ | Instruction timing test |
| `instr_timing/rom_singles/2-branch_timing.nes` | ✓ | 2-branch_timing |

### `nes_instr_test/` (11)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `nes_instr_test/rom_singles/01-implied.nes` | ✓ | 01-implied |
| `nes_instr_test/rom_singles/02-immediate.nes` | ✓ | 02-immediate |
| `nes_instr_test/rom_singles/03-zero_page.nes` | ✓ | 03-zero_page |
| `nes_instr_test/rom_singles/04-zp_xy.nes` | ✓ | 04-zp_xy |
| `nes_instr_test/rom_singles/05-absolute.nes` | ✓ | 05-absolute |
| `nes_instr_test/rom_singles/06-abs_xy.nes` | ✓ | 06-abs_xy |
| `nes_instr_test/rom_singles/07-ind_x.nes` | ✓ | 07-ind_x |
| `nes_instr_test/rom_singles/08-ind_y.nes` | ✓ | 08-ind_y |
| `nes_instr_test/rom_singles/09-branches.nes` | ✓ | 09-branches |
| `nes_instr_test/rom_singles/10-stack.nes` | ✓ | 10-stack |
| `nes_instr_test/rom_singles/11-special.nes` | ✓ | 11-special |

### `oam_read/` (1)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `oam_read/oam_read.nes` | ✓ | ---------------- |

### `ppu_open_bus/` (1)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `ppu_open_bus/ppu_open_bus.nes` | ✓ | ppu_open_bus |

### `ppu_vbl_nmi/` (10)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `ppu_vbl_nmi/rom_singles/01-vbl_basics.nes` | ✓ | 01-vbl_basics |
| `ppu_vbl_nmi/rom_singles/02-vbl_set_time.nes` | ✓ | T+ 1 2 |
| `ppu_vbl_nmi/rom_singles/03-vbl_clear_time.nes` | ✓ | 00 V |
| `ppu_vbl_nmi/rom_singles/04-nmi_control.nes` | ✓ | 04-nmi_control |
| `ppu_vbl_nmi/rom_singles/05-nmi_timing.nes` | ✓ | 00 4 |
| `ppu_vbl_nmi/rom_singles/06-suppression.nes` | ✓ | 00 - N |
| `ppu_vbl_nmi/rom_singles/07-nmi_on_timing.nes` | ✓ | 00 N |
| `ppu_vbl_nmi/rom_singles/08-nmi_off_timing.nes` | ✓ | 03 - |
| `ppu_vbl_nmi/rom_singles/09-even_odd_frames.nes` | ✓ | 00 01 01 02  |
| `ppu_vbl_nmi/rom_singles/10-even_odd_timing.nes` | ✓ | 08 08 09 07  |

### `sprdma_and_dmc_dma/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma.nes` | ✓ | T+ Clocks (decimal) |
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma_512.nes` | ✓ | T+ Clocks (decimal) |

## A-r: `$6000` protocol + automatic soft reset (8)

### `apu_reset/` (6)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `apu_reset/4015_cleared.nes` | ✓ | 4015_cleared |
| `apu_reset/4017_timing.nes` | ✓ | Delay after effective $4017 write: 8 |
| `apu_reset/4017_written.nes` | ✓ | 4017_written |
| `apu_reset/irq_flag_cleared.nes` | ✓ | irq_flag_cleared |
| `apu_reset/len_ctrs_enabled.nes` | ✓ | len_ctrs_enabled |
| `apu_reset/works_immediately.nes` | ✓ | works_immediately |

### `cpu_reset/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `cpu_reset/ram_after_reset.nes` | ✓ | ram_after_reset |
| `cpu_reset/registers.nes` | ✓ | A  X  Y  P  S |

## B: On-screen-text type (S1 switches to scanning the nametable per frame) (46)

### `blargg_apu_2005.07.30/` (11)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `blargg_apu_2005.07.30/01.len_ctr.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/02.len_table.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/03.irq_flag.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/04.clock_jitter.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/05.len_timing_mode0.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/06.len_timing_mode1.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/07.irq_flag_timing.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/08.irq_timing.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/09.reset_timing.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/10.len_halt_timing.nes` | ✗ | (screen: $01 = passed) |
| `blargg_apu_2005.07.30/11.len_reload_timing.nes` | ✗ | (screen: $01 = passed) |

### `blargg_ppu_tests_2005.09.15b/` (5)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `blargg_ppu_tests_2005.09.15b/palette_ram.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/power_up_palette.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/sprite_ram.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/vbl_clear_time.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/vram_access.nes` | ✗ | (screen: $01 = passed) |

### `branch_timing_tests/` (3)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `branch_timing_tests/1.Branch_Basics.nes` | ✗ | (screen: Passed) |
| `branch_timing_tests/2.Backward_Branch.nes` | ✗ | (screen: Passed) |
| `branch_timing_tests/3.Forward_Branch.nes` | ✗ | (screen: Passed) |

### `cpu_timing_test6/` (1)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `cpu_timing_test6/cpu_timing_test.nes` | ✗ | (screen: Passed) |

### `dmc_dma_during_read4/` (3)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `dmc_dma_during_read4/dma_2007_write.nes` | ✗ | (screen: Passed) |
| `dmc_dma_during_read4/dma_4016_read.nes` | ✗ | (screen: Passed) |
| `dmc_dma_during_read4/read_write_2007.nes` | ✗ | (screen: Passed) |

### `sprite_hit_tests_2005.10.05/` (11)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `sprite_hit_tests_2005.10.05/01.basics.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/02.alignment.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/03.corners.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/04.flip.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/05.left_clip.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/06.right_edge.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/07.screen_bottom.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/08.double_height.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/09.timing_basics.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/10.timing_order.nes` | ✗ | (screen: Passed) |
| `sprite_hit_tests_2005.10.05/11.edge_timing.nes` | ✗ | (screen: Passed) |

### `sprite_overflow_tests/` (5)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `sprite_overflow_tests/1.Basics.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/2.Details.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/3.Timing.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/4.Obscure.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/5.Emulator.nes` | ✗ | (screen: Passed) |

### `vbl_nmi_timing/` (7)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `vbl_nmi_timing/1.frame_basics.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/2.vbl_timing.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/3.even_odd_frames.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/4.vbl_clear_timing.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/5.nmi_suppression.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/6.nmi_disable.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/7.nmi_timing.nes` | ✗ | (screen: Passed) |

## C: CRC-comparison type (2)

### `dmc_dma_during_read4/` (2)

| ROM | Static $6000 write | AprNes measured marker |
|---|---|---|
| `dmc_dma_during_read4/dma_2007_read.nes` | ✗ | (screen CRC: 5E3DF9C4) |
| `dmc_dma_during_read4/double_2007_read.nes` | ✗ | (screen CRC: 85CFD627) |

### Set of valid CRCs for class C (taken from the AprNes TestCatalog)

- `dmc_dma_during_read4/dma_2007_read.nes`: 159A7A8F, 5E3DF9C4
- `dmc_dma_during_read4/double_2007_read.nes`: 85CFD627, F018C287, 440EF923, E52F41A5

## Cross-verification

The static scan (whether the PRG has `STA $6000/$6001`) and the AprNes measured verdict path are **fully consistent**, with no contradictions.

## S1 implementation notes

- Class A: S1's `CheckUnitTest()` already implements the `$6000` signature + text read; calling it once per frame is enough, and it stops immediately once a result appears——the most economical in simulation time.
- Class A-r (apu_reset/cpu_reset): the protocol returns `$6000=$81` to request a soft reset (wait 6 frames → pull res). S1 has a real res node, but "resetting mid-run" is not yet verified; it is recommended to test one in isolation first.
- Class B: each frame, read nametable 0 from `u4.ram` (960 bytes, the tile value is ASCII) and look for the `Passed`/`Failed`/`$0X` terminal marker; to guard against transient misreads you can require 2-3 consecutive frames to be identical (still far cheaper than 90 frames).
- Class C: same scan as B; after finding an isolated 8-digit hex string, compare it against the set of valid CRCs.
- For the timeout ceiling it is recommended to use the **number of simulated frames** (not wall-clock): most class-A ROMs produce a result within 2-5 s of simulation (120-300 frames); for B/C you can start by capturing 600-900 frames and observing.
