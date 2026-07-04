# Unofficial immediate trio (ANC/ALR/ARR/LXA) — ALU input latch race

> English translation of `2026-07-05-immediate-trio-alu-latch-race.md` (Traditional-Chinese master). Update both in sync.

Date: 2026-07-05
Status: **all fixed** — micro-ROM 640/640; all three blargg ROMs (v3-02/v5-03/nes-02)
Passed. Two shims: ALU input latch hold (fixes ANC/ALR/ARR) + LXA magic=$FF
(fixes ATX). Full 141-ROM regression re-run in progress.

## Failure symptoms

Three suites (instr_test-v3 02, instr_test-v5 03, nes_instr_test 02) fail with the same five lines:
`0B/2B AAC`, `4B ASR`, `6B ARR`, `AB ATX`. The official `AND #imm` ($29) and `AXS` ($CB) both pass.

## Microscope toolchain (all newly established)

1. **micro-ROM + `--micro`**: hand-assembled 640 combinations (5 ops × C∈{0,1} × 8 A values × 8 imm values);
   each combination runs `LDA #p; PHA; PLP; LDX #$5A; LDA #a; OP #imm; PHP; STA; PLA; STA` storing
   A/P to $0200+; `--micro` runs 3 frames then dumps u1.ram. The analyzer checks against the documented semantics
   (`temp/micro_imm/gen_rom.py` / `analyze.py`).
2. **`--op-probe <rom> <hexaddr>`**: triggers on an AB hit, then logs per half-cycle
   db/idl/alua/alub/sb/A/ADD + 13 datapath control lines (ACSB/SBADD/0ADD/DBADD/
   nDBADD/ANDS/SUMS/SRS/ORS/ADDSB7/ADDSB06/SBAC/SBDB) + the **firing set of PLA rows**
   (`WireCore.AllNodeNames()` enumerating the `op-*` names).

## Pathology dissection (ANC #$55, A=$FF, vs official AND)

The parts that are correct before the failure:
- **PLA union is fully correct**: $0B firing = AND rows ∪ ASL-A rows (op-T+-ora/and/eor/adc
  + op-T+-asl/rol-a + op-shift etc.), consistent with the 6502 architecture
- **The bus wired-AND emerges**: ACSB+SBDB short together → SB = A&imm = $55 (NMOS low state wins —
  the netlist reproduces it faithfully!)
- IR[7:5]=000 → family decode selects ORS (ORA) ✓ architecturally correct
- alua=alub=$55 (the merged value reaches both sides of the ALU) ✓

The failure point (the φ1→φ2 boundary of the execute cycle, hc 13):
- The `SBADD/DBADD` gate close and the SB/DB bus collapse (55→FF) happen in **the same half-cycle**
- The quiescent settle lets the collapse **pass through the gates that are closing**: alua/alub 55→FF, and ADD locks in
  FF|FF=FF (a self-consistent but wrong fixed point); SBAC writes back → A=FF (the old value)
- Compare AND ($29): alub holds 55 through φ2 → ADD=FF&55=55 → A=55 ✓ (under single-row decode
  the collapse never reaches it)

## Relation to the DMC race: same class, opposite polarity

| | DMC pcm_latch | ALU input latch |
|---|---|---|
| Race | data edge vs clock gate-close edge in the same half-cycle | bus collapse vs select-line gate close in the same half-cycle |
| Real silicon | **data wins** (clock decay keeps conduction overlapping) | **gate wins** (hold time preserves the φ1 value) |
| Binary model | gate closes first → one beat of capture is missed | collapse passes through the gate → latch gets polluted |

→ The race polarity of each latch instance is determined by the real propagation depth; no global rule exists;
the fix = **a documented latch shim table with per-instance polarity**.

## The fix: `WireCore.EnableAluLatchShim()` (hold mode)

Snapshot alua/alub every half-cycle; on the falling edge of `SBADD`/`DBADD`, if the synchronous value was changed, restore the pre-step
snapshot (drive→settle→release) = the intended semantics of the latch's "hold time being satisfied".
Test mode only; the benchmark path is unaffected.

## Results

- micro-ROM 640 combinations: **344 mismatches → 0** (including flags: ANC C=b7, ALR C=b0,
  ARR N=Cin/C=b6/V=b6⊕b5 all correct — the flag paths become correct naturally once the datapath is fixed)
- **Observed LXA magic = $00** (A=X=(A|$00)&imm=A&imm): differs from the common real-hardware $EE/$FF;
  the analyzer infers magic from its own readback, which for LXA is circular reasoning — the blargg checksums were
  recorded on the author's real hardware, so the final verdict on the ATX line comes from actually running the blargg ROMs
- The three blargg ROMs (v3-02 / v5-03 / nes-02) under verification; full instr_test family regression afterwards

## LXA magic shim (finalized 2026-07-05)

Four iterations' worth of engine-physics lessons:
1. Post-hoc forcing of A/X/N/Z (one-shot) → A/X ✓ but P gets flushed: **the flags are an actively-refreshed regenerative loop**;
   a one-shot force is undone within one cycle; and the flags have already been updated by the end of the operand cycle
2. Deferred forcing → ineffective (same cause)
3. Suppressing the ACSB control line → ineffective: **LUT priority = VCC conduction inside the group > external drive-low**;
   an actively driven control line cannot be held down
4. ✅ **One-shot force of A/X + dual-side pair sustained drive of the flags ({p1,#566}, {p7,#1045})
   for 3 cycles, then SetFloat** — the sustained drive lets every phase of the loop capture the new state, and it self-sustains after release
   (an advanced version of the power-up palette dual-side lesson)

Detection: a φ2-fall state machine; note that **at this netlist's sampling point, cpu.sync leads by one beat**
(sync=1 at fall N means cycle N+1 is the opcode fetch).

Evidence: NESdev (magic varies with chip/temperature), the TriCNES source (magic=$FF +
a "supposedly different depending on the CPU's temperature" comment), and the blargg
checksum recordings (its host machine is of the $FF class, as proven by the three ROMs passing). Our bare netlist's $00 =
the binary GND-wins quantization of a ratioed bus fight; the $FF correction = the NTSC G-revision consensus.

## Results summary

- micro-ROM: 344 mismatches → **0/640** (ANC/ALR/ARR/LXA all correct, including flags)
- blargg: v3-02 / v5-03 / nes-02 all **Passed** (originally all three FAIL)
- Expected score: 129/12 → 132/9 (pending confirmation by the full 141-ROM regression)

## TODO

- Full 141-ROM regression (the shim is CPU-domain-wide; guard against collateral damage to official instructions — the three ROMs themselves are already strong canaries)
- Add the LXA magic entry to the report-page dossier
- `#` prefix collision between raw-id aliases and pin aliases (small-id number clashes) — clean up later
