# S1 測試 ROM 判定方式過濾（139 → 分級名單）

- 日期：2026-07-02
- 母集合：`2026-07-02-aprvisual-s1-supported-testroms.md` 的 139 個（NROM、非 PAL）
- 目的：S1 速度慢（~5 秒/幀），**不採用「連續 90 幀穩定畫面」的判定**；
  只接受 (1) `$6000` 協定、(2) 每幀判讀一次即可出結果的方式。90 幀穩定型仍列入名單（標記），供後續決定。
- 證據來源（雙重驗證）：
  1. **靜態掃描**：PRG 內是否有 `STA $6000/$6001`（op `8D 00 60` / `8D 01 60`）＝有寫 blargg 協定
  2. **AprNes 實測**（`site/report/results.json`）：`result_text` 內容判別實際判定路徑——
     `$6004` 富文字＝走 `$6000` 協定；`(screen: ...)`＝AprNes 用 90 幀穩定＋畫面文字；`(screen CRC:)`＝CRC 比對

## 摘要

| 分級 | 判定方式 | 數量 | S1 建議 |
|---|---|---|---|
| **A** | `$6000` 協定（每幀讀一 byte，出結果即停） | **83** | **首選核心測試集** |
| **A-r** | `$6000` 協定＋需自動軟重設（`$6000=$81`） | **8** | 首選，但 S1 需先驗證跑到一半拉 res 線 |
| **B** | 畫面文字（AprNes 走 90 幀穩定；S1 可改**每幀掃 nametable 終端文字**） | **46** | 可用——每幀掃 960 bytes 成本可忽略 |
| **C** | 畫面 CRC 比對（已知合法 CRC 集合） | **2** | 可用——CRC 文字出現即比對，附 CRC 表 |
| | 合計 | **139** | |

> B/C 類在 AprNes 的實作是「畫面穩定 90 幀後才判讀」，但那是行為層模擬器求快的通用作法；
> 這些 ROM 的結果文字是**終端性**的（`Passed`/`Failed`/`$0X` 碼印出後停在死迴圈），
> 所以 S1 可以每幀直接掃 nametable 找終端標記，不需等穩定——符合「每幀判斷一次」的接受條件。

## A：`$6000` 協定（首選核心）（83）

### `apu_mixer/`（4）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `apu_mixer/dmc.nes` | ✓ | dmc channel mixing test |
| `apu_mixer/noise.nes` | ✓ | noise channel mixing test |
| `apu_mixer/square.nes` | ✓ | square channel mixing test |
| `apu_mixer/triangle.nes` | ✓ | triangle channel mixing test |

### `apu_test/`（8）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `apu_test/rom_singles/1-len_ctr.nes` | ✓ | 1-len_ctr |
| `apu_test/rom_singles/2-len_table.nes` | ✓ | 2-len_table |
| `apu_test/rom_singles/3-irq_flag.nes` | ✓ | 3-irq_flag |
| `apu_test/rom_singles/4-jitter.nes` | ✓ | 4-jitter |
| `apu_test/rom_singles/5-len_timing.nes` | ✓ | 5-len_timing |
| `apu_test/rom_singles/6-irq_flag_timing.nes` | ✓ | 6-irq_flag_timing |
| `apu_test/rom_singles/7-dmc_basics.nes` | ✓ | 7-dmc_basics |
| `apu_test/rom_singles/8-dmc_rates.nes` | ✓ | 8-dmc_rates |

### `cpu_dummy_writes/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `cpu_dummy_writes/cpu_dummy_writes_oam.nes` | ✓ | TEST: cpu_dummy_writes_oam |
| `cpu_dummy_writes/cpu_dummy_writes_ppumem.nes` | ✓ | TEST: cpu_dummy_writes_ppumem |

### `cpu_exec_space/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `cpu_exec_space/test_cpu_exec_space_apu.nes` | ✓ | TEST: test_cpu_exec_space_apu |
| `cpu_exec_space/test_cpu_exec_space_ppuio.nes` | ✓ | TEST:test_cpu_exec_space_ppuio |

### `cpu_interrupts_v2/`（5）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `cpu_interrupts_v2/rom_singles/1-cli_latency.nes` | ✓ | 1-cli_latency |
| `cpu_interrupts_v2/rom_singles/2-nmi_and_brk.nes` | ✓ | NMI BRK 00 |
| `cpu_interrupts_v2/rom_singles/3-nmi_and_irq.nes` | ✓ | NMI BRK |
| `cpu_interrupts_v2/rom_singles/4-irq_and_dma.nes` | ✓ | 0 +0 |
| `cpu_interrupts_v2/rom_singles/5-branch_delays_irq.nes` | ✓ | test_jmp |

### `instr_misc/`（4）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `instr_misc/rom_singles/01-abs_x_wrap.nes` | ✓ | 01-abs_x_wrap |
| `instr_misc/rom_singles/02-branch_wrap.nes` | ✓ | 02-branch_wrap |
| `instr_misc/rom_singles/03-dummy_reads.nes` | ✓ | 03-dummy_reads |
| `instr_misc/rom_singles/04-dummy_reads_apu.nes` | ✓ | 04-dummy_reads_apu |

### `instr_test-v3/`（15）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `instr_test-v5/`（16）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `instr_timing/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `instr_timing/rom_singles/1-instr_timing.nes` | ✓ | Instruction timing test |
| `instr_timing/rom_singles/2-branch_timing.nes` | ✓ | 2-branch_timing |

### `nes_instr_test/`（11）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `oam_read/`（1）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `oam_read/oam_read.nes` | ✓ | ---------------- |

### `ppu_open_bus/`（1）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `ppu_open_bus/ppu_open_bus.nes` | ✓ | ppu_open_bus |

### `ppu_vbl_nmi/`（10）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `sprdma_and_dmc_dma/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma.nes` | ✓ | T+ Clocks (decimal) |
| `sprdma_and_dmc_dma/sprdma_and_dmc_dma_512.nes` | ✓ | T+ Clocks (decimal) |

## A-r：`$6000` 協定＋自動軟重設（8）

### `apu_reset/`（6）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `apu_reset/4015_cleared.nes` | ✓ | 4015_cleared |
| `apu_reset/4017_timing.nes` | ✓ | Delay after effective $4017 write: 8 |
| `apu_reset/4017_written.nes` | ✓ | 4017_written |
| `apu_reset/irq_flag_cleared.nes` | ✓ | irq_flag_cleared |
| `apu_reset/len_ctrs_enabled.nes` | ✓ | len_ctrs_enabled |
| `apu_reset/works_immediately.nes` | ✓ | works_immediately |

### `cpu_reset/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `cpu_reset/ram_after_reset.nes` | ✓ | ram_after_reset |
| `cpu_reset/registers.nes` | ✓ | A  X  Y  P  S |

## B：畫面文字型（S1 改每幀掃 nametable）（46）

### `blargg_apu_2005.07.30/`（11）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `blargg_ppu_tests_2005.09.15b/`（5）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `blargg_ppu_tests_2005.09.15b/palette_ram.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/power_up_palette.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/sprite_ram.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/vbl_clear_time.nes` | ✗ | (screen: $01 = passed) |
| `blargg_ppu_tests_2005.09.15b/vram_access.nes` | ✗ | (screen: $01 = passed) |

### `branch_timing_tests/`（3）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `branch_timing_tests/1.Branch_Basics.nes` | ✗ | (screen: Passed) |
| `branch_timing_tests/2.Backward_Branch.nes` | ✗ | (screen: Passed) |
| `branch_timing_tests/3.Forward_Branch.nes` | ✗ | (screen: Passed) |

### `cpu_timing_test6/`（1）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `cpu_timing_test6/cpu_timing_test.nes` | ✗ | (screen: Passed) |

### `dmc_dma_during_read4/`（3）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `dmc_dma_during_read4/dma_2007_write.nes` | ✗ | (screen: Passed) |
| `dmc_dma_during_read4/dma_4016_read.nes` | ✗ | (screen: Passed) |
| `dmc_dma_during_read4/read_write_2007.nes` | ✗ | (screen: Passed) |

### `sprite_hit_tests_2005.10.05/`（11）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
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

### `sprite_overflow_tests/`（5）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `sprite_overflow_tests/1.Basics.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/2.Details.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/3.Timing.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/4.Obscure.nes` | ✗ | (screen: Passed) |
| `sprite_overflow_tests/5.Emulator.nes` | ✗ | (screen: Passed) |

### `vbl_nmi_timing/`（7）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `vbl_nmi_timing/1.frame_basics.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/2.vbl_timing.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/3.even_odd_frames.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/4.vbl_clear_timing.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/5.nmi_suppression.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/6.nmi_disable.nes` | ✗ | (screen: Passed) |
| `vbl_nmi_timing/7.nmi_timing.nes` | ✗ | (screen: Passed) |

## C：CRC 比對型（2）

### `dmc_dma_during_read4/`（2）

| ROM | 靜態 $6000 寫入 | AprNes 實測標記 |
|---|---|---|
| `dmc_dma_during_read4/dma_2007_read.nes` | ✗ | (screen CRC: 5E3DF9C4) |
| `dmc_dma_during_read4/double_2007_read.nes` | ✗ | (screen CRC: 85CFD627) |

### C 類的合法 CRC 集合（取自 AprNes TestCatalog）

- `dmc_dma_during_read4/dma_2007_read.nes`：159A7A8F, 5E3DF9C4
- `dmc_dma_during_read4/double_2007_read.nes`：85CFD627, F018C287, 440EF923, E52F41A5

## 交叉驗證

靜態掃描（PRG 有無 `STA $6000/$6001`）與 AprNes 實測判定路徑**完全一致**，無矛盾。

## S1 實作備註

- A 類:S1 的 `CheckUnitTest()` 已實作 `$6000` 簽章＋文字讀取,每幀呼叫一次即可,出結果立即停——最省模擬時間。
- A-r 類（apu_reset/cpu_reset）:協定會回 `$6000=$81` 要求軟重設(等 6 幀 → 拉 res)。S1 有真實 res 節點,但「執行中途重設」尚未驗證,建議先單獨測一個。
- B 類:每幀從 `u4.ram` 讀 nametable 0(960 bytes,tile 值即 ASCII),找 `Passed`/`Failed`/`$0X` 終端標記;為防瞬時誤判可要求連續 2-3 幀相同(成本仍遠低於 90 幀)。
- C 類:同 B 的掃法,找到孤立 8 位 hex 字串後與合法 CRC 集合比對。
- 逾時上限建議用**模擬幀數**(非 wall-clock):A 類多數在模擬 2-5 秒(120-300 幀)內出結果;B/C 類可先抓 600-900 幀觀察。
