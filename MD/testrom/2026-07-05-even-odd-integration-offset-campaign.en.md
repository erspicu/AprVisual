# even_odd Integration-Offset Fix Campaign — Measurement Log (closed 2026-07-06)

> Traditional-Chinese master: `2026-07-05-even-odd-integration-offset-campaign.md`.
> **Closed (2026-07-06)**: fixed, `10-even_odd_timing` now **PASS** (output `08 08 09 07`).
> The approach is a **narrow-window write-delay shim** (global, `--ppu-write-delay 16`, only in the
> pre-render `vpos=261, hpos=338..339` window it delays the `$2001` background-enable/disable
> transition by 16 hc; the disable side clamps the complementary node `/bkg_enable`) — filling in the
> ~1-dot cross-chip write-path offset at alignment 7, going green simultaneously with the NMI-edge
> family → full regression **145/1 (99.3%)** at the time (current baseline: **146/1** on 147 tests). See knowledge base §3.1 #13 and the
> [final fix record](../toDoNext/202607062345-10-even_odd_timing修復紀錄.en.md).
> Arbitrating the offset at its source by cross-comparing the absolute `$2002` master-clock delay via
> PPUSim (rather than compensating with a shim) remains listed as follow-up research.
> **What follows is the investigation measurement log from that time (retained for traceability).**
>
> Original goal: arbitrate and correct the ~1-dot absolute phase offset of the dual-netlist
> integration, so that the NMI family and 10-even_odd pass together under the same alignment (→ 144/1).
> Qualitative basis: Gemini consult `tools/knowledgebase/q/a_even_odd_doctrine_20260705.md`.

## Key Inference (2026-07-05)

**Scanning K shifts the read and write grid points simultaneously → mutual exclusion holds for all K →
the offset is a K-invariant = the differential error of "write path − read path".** Pure phase rotation
(reachable by K) has been exhaustively enumerated and ruled out; the candidate = the transport-delay
asymmetry of cross-chip access: the read response (PPU→CPU) must depart before the CPU samples =
effective sampling is "too early"; the write request (CPU→PPU) arrives at the φ2 tail = taking effect
is "too late". On real hardware the two skew in opposite directions; we idealize both to 0 (completing
in the same wave).

## Our Netlist Measurements (--probe-vbl / --probe-2001, default K)

### M1 Read side ($2002, 05-nmi_timing)
- `vbl_flag` rises at **vpos=241, hpos=1** (t=657643) — consistent with the NESdev real-hardware
  record (fidelity of the in-die internal position ✓).
- $2002 read: ab=2002 at hpos 15 (φ2 start), `/r2002` pulled low on the same dot,
  at hpos 16 `read_2002_output_vblank_flag`=1, io_db7=1, db=$80,
  the flag is read-cleared at the same time (vbl_flag 1→0). The read "assign" span ≈ dots 15-18.

### M2 Write side ($2001, 10-even_odd_timing)
- Write strobe: `/w2001` low on the write φ2, `write_2001_reg` pulses, `bkg_enable`
  **flips on the same dot** (e.g. dot 45, dot 337).
- Take-effect chain: bkg_enable → `rendering_1..4` pipeline (in place within a few dots) →
  `hpos_eq_339_and_rendering`.
- **skip scene (B4)**: write at dot 337 → bkg 337 flips high → r4 in place → at dot 339
  `h339`+`skip_dot` fire simultaneously → **hpos 339→0 (skipping 340)**.
- **odd/even contrast (B1)**: the only difference `even_frame_toggle`=1 → no skip (339→340→0).
  The odd/even mechanism is entirely correct inside the netlist; the skip condition = h339 ∧ (evenT=0).

### Node roster (2C02, all named)
`skip_dot` (4427), `even_frame_toggle` (4932), `/w2001` (4116),
`write_2001_reg` (4117), `bkg_enable` (11779), `rendering_1..4`
(4725,5506,4396,5660), `hpos_eq_339_and_rendering` (1386),
`vbl_flag` (4994), `set_vbl_flag` (1364), `read_2002_output_vblank_flag`
(3929), `/r2002` (3926).

## Tools

- `--probe-2001 <rom>`: window A = enable write ($2001 bit3=1) → /w2001/wreg/bkg/
  rendering pipeline per-hc; window B ×10 = the skip window at pre-render dot 336..3
  (h339/skip_dot/even_frame_toggle/hpos sequence).
- `--probe-vbl <rom>` (existing): flag-set dot + $2002 read chain.

## PPUSim Arbitration Result (task 2, completed 2026-07-05)

harness: `temp/ppusim_harness/ppu_truth.cpp` (clang++ directly compiling emu-russia
RP2C02G; friend-class `UnitTest` punches through to read `hv_fsm->INT_FF`/`h`/`v`/
`regs->bge_latch`). Same-scenario measurement:

| Quantity | PPUSim (RP2C02G) | Our netlist | Verdict |
|---|---|---|---|
| VBL flag set position | **V=241, H=1** | **V=241, H=1** (--probe-vbl) | **dot-for-dot identical** |
| frame alternation | 714736 / 714728 hc (difference **8 hc = 1 dot**) | skip@339 (339→0 skips 340) | both correct |
| dot-skip decision point | dot 339 | dot 339 (--probe-2001) | identical |

**Core conclusion: the two chips are dot-for-dot consistent in isolation** (VBL both at (241,1), skip
both at 339). Therefore the ~1-dot offset is **not inside either die**, but is the **differential
transport delay** of CPU↔PPU communication — and necessarily on the **write path** (even_odd failure
code 3 "skip too late relative to enabling BG", X=7 vs 8; the read-path NMI family currently PASSes and
must not be disturbed).

## Fix: $2001 write-effect delay (task 3, implementation complete, pending verification)

- **Veto global clock skew**: measured 1-hc PPU-clock delay → all tests detection=none
  (CPU/PPU I/O desynced); and in theory "moving read+write together" = just another K, cannot break
  the complementary lock.
- **Adopt differential write delay**: `--ppu-write-delay N` (opt-in, default 0) uses instrument-grade
  InstClamp to clamp every `bkg_enable`/`spr_enable` transition for N hc (= the register loads N hc
  late). Zero graph footprint, read path untouched. **probe verification**: at delay=8, the bkg rise is
  moved from t=4231704 to 4231712 (exactly 1 dot), selftest ALL PASS.
- New engine API: `InstClampHigh` (symmetric to InstClampLow, presses Pwr); `InstRelease`
  changed to clear Gnd/Pwr simultaneously.
- **Pending verification**: under K=1, sweep even_odd delay∈{4,8,12,16} + 05-nmi delay∈{4,8,16}
  (7-core parallel) — find the N that makes even_odd PASS while NMI keeps PASSing.
