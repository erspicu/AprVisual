# test_ppu_read_buffer #67 diagnosis (TEST_SPHIT_DMA_PPU_BUS)

> Traditional-Chinese master: `2026-07-05-read-buffer-67-dma-openbus.md`.

> #67: `$4014 <- $20` triggers OAM DMA, source = PPU register space ($2000-$20FF,
> i.e. $2000-$2007 mirrored 32 times), read into OAM and then a sprite-0-hit is done. Four failure candidates:
> DMA read incorrect / PPU bus not holding the last transferred value / $2002 read value mismatch / $2004 read modifies OAM.

## Diagnostic conclusion: DMA-read-of-PPU-registers open-bus sampling timing is misaligned (off-by-one)

Using a wla-dx-assembled micro-ROM (`temp/readbuf/`) + the engine's --micro physical OAM cell dump
(`ppu.oam_ram_XX_bN`, b side = true value; a side = inverted value; the attribute byte
at index ≡ 2 mod 4 has only 5 bits, bits 2-4 have no cell → $8A is stored as $82, **this is real 2C02 behavior,
not corruption**), a three-case comparison:

| source | OAM[0..3] with open bus = $0A | correct? |
|---|---|---|
| **CPU direct read** $2000-$2003 | `0A 0A 8A 8A` | ✓ perfect (open-bus completely correct) |
| **DMA from RAM** (filled 0,1,2,3…) | `00 01 02 03` | ✓ perfect (DMA address/write flawless) |
| **DMA from register space** | `0A 8A 82 00` | ✗ **off-by-one shift** |

**Key: direct read OK + DMA-from-RAM OK, only DMA-from-registers is wrong.**
The error = the register read values are **shifted by one slot as a whole**: OAM[0] gets the value of $2001, OAM[1] gets
$2002 ($8A), OAM[2] gets $2003 ($8A → attr-mask $82), OAM[3] gets $2004 ($00).
That is, `OAM[i] = register-read[i+1]`.

## Why it's open-bus sampling timing (not a DMA address bug)

- DMA-from-RAM has no shift (source[i] → OAM[i] correct) → the DMA address generator and the write are **correct**.
  If it were a generic DMA off-by-one, all sprite DMA would break and a large number of tests would crash (in reality most pass).
- The off-by-one appears only when reading "dynamic PPU registers" (open-bus is only driven at the moment of the read) →
  the problem is that the **open-bus value's sampling phase under rapid DMA reads** differs from a direct read, misaligned by one slot.
- Same "fast access within the same wave timing" hard category as double_2007 / even_odd.

## Ruled-out candidates

- "PPU bus not holding the last transferred value": the direct read `0A 0A 8A 8A` proves open-bus holding is **completely correct**.
- "$2002 read value mismatch": direct read $2002=$8A is correct.
- "$2004 read modifies OAM": not yet independently confirmed; but the main symptom is the shift, so attack the shift first.
- OAMADDR write corruption (RP2C02G bug): this is real faithful behavior, but it is overwritten by DMA and does not affect
  #67's final OAM; **note** my initial readback (repeated STA $2003) accidentally triggered it → a false
  `0A 8A 82 00`, which **coincidentally matches** the DMA true result (both because of the shift), but the cause is different.

## Tools (reusable)

- `tools/wla-dx/wla-6502.exe` + `wlalink.exe`: proper assembly (replacing error-prone hand assembly).
- `temp/readbuf/`: d67d (direct read) / d67v (DMA-from-RAM verification) / d67dma (DMA-from-reg)
  + build.sh; work is done inside the NMI handler (VBL set & unread); **must first warmup and wait for 2
  VBLs before writing $2000** (during warmup $2000 writes are ignored, otherwise the NMI does not fire).
- Engine: `--micro-frames N` (default 3, NMI needs ≥~10); MicroDump ends with a physical OAM dump
  (OAM00/OAM10 two lines, b-side).
- `--pin 0`: when the sweep occupies cores 2-14, core 0 is idle, so micro runs here without contention.

## DMA bus trace (--probe-dma, 2026-07-05)

`--probe-dma <rom>` logs ab/rw/cpu.db/ppu.io_db per CPU cycle. d67dma result:

- **DMA address is completely correct**: sequential $2000,$2001,$2002,$2003,$2004,$2005,...
  (get) interleaved with $2004 (put). **Rules out address off-by-one** (the earlier "OAM[i]=reg[i+1]"
  is the net effect, not an address shift).
- **open-bus (io_db) collapses during DMA**: on the cycle that reads $2002, io_db shows no VBL ($80),
  and from ~cyc11 (around $2004) onward io_db is $00 all the way → write-only reads ($2005/$2006) get $00
  instead of the held value. → the true cause = the **open-bus value's driving/sampling phase under rapid DMA reads**
  differs from a direct read (direct read is completely correct), making the value written into OAM shifted/zeroed.
- Todo: switch to a cleaner sampling point (phi2 falling edge or after settle) and re-measure io_db, to confirm
  the "io_db one cycle late" hypothesis + whether "$2002-during-DMA not merging VBL" is the same root cause;
  then decide on a shim (test mode, instrumentation-grade like double_2007).
- Canaries: all sprite DMA tests (sprite_hit/dma_ram/dma_rom), oam_read,
  the already-passing dma_2007_read/write (also DMA×PPU-reg).

**Tool deposit**: `--probe-dma <rom>` (detect $4014 write → trace per CPU cycle);
`--micro-frames N`; physical OAM dump (b-side). All in the engine's diagnostic surface, does not affect the default path.


## Important suspicion: our own shim may be polluting DMA (added 2026-07-05)

The phi2-falling trace (--probe-dma, **without** test-mode shims) shows the put cycles write to
OAM = $0A $0A $8A $8A $00...; but the --micro OAM dump (**with** DmcLatch/ALU/FrameIrq/
**Dbl2007** shims) dumps = $0A $8A $82 $00. **The two disagree** → strong suspicion that one of our own
test-mode shims is accidentally triggered during DMA and modifies db.

- DMA reads $2007 at cyc15; **Dbl2007Shim** watches for $2007 reads and does InstClampLow on cpu.db —
  DMA's $2007 read very likely triggers it, polluting the DMA data stream. The gatekeeping (ab = $2007 mirror + R/W + RDY)
  may all hold during DMA → false clamp.
- Next step (high priority): (a) add a --micro flag to disable individual shims, and A/B one by one to find the polluter;
  or (b) add a "not during OAM-DMA" condition to Dbl2007Shim (and others) gatekeeping (detect $4014
  DMA active / cpu halted). If it really is Dbl2007 firing by mistake, scoping it out may directly fix part of #67,
  and it would be a regression we introduced ourselves (not real hardware timing).
- Also need to separate out: $2000 first read returns $A9 (not open-bus $0A), $2002-during-DMA not merging VBL,
  open-bus collapsing to $00 at ~cyc10 — which of these are shim pollution and which are true netlist timing must be
  determined by re-measuring the OAM dump in a clean --micro **with all test shims turned off**.


## Shim pollution ruled out → true netlist behavior (2026-07-05 finalized)

Added `--no-shims` (turns off all test-mode shims). d67dma OAM dump:
- all shims ON: `0A 8A 82 00`
- **--no-shims (all off): `0A 8A 82 00` (exactly the same)**

→ **our shims are not the pollution source**; the earlier trace put-cycle ($0A $0A $8A $8A) disagreeing with the dump
was my trace sampling model being wrong (the put data was not at the point I was reading), **the physical OAM dump is the true value**.

**#67 is a true netlist/integration-layer bug**, unrelated to the test infrastructure: during register-space DMA the PPU
open-bus (io_db) behaves incorrectly ——
- $2000 first read returns a stale `$A9` (= the opcode of the previous non-PPU bus activity `LDA #$20`);
  suggests io_db is polluted by a **non-PPU access** (on real hardware: a ROM instruction fetch does not go through the PPU /CS, io_db should stay $0A).
- $2002-during-DMA does not merge VBL ($0A instead of $8A).
- io_db collapses to `$00` midway.
Same "fast access × same-wave timing" hard category as double_2007/even_odd. **Deep fix**, needs a dedicated
focus (recommend, as in prior cases, first consulting Gemini on the io_db-during-DMA mechanism, then deciding the shim/fix point).

**Canaries**: sprite_hit_tests, oam_read, dma_2007_read/write, cpu_dummy_writes_ppumem.
**Deposited tools**: --probe-dma / --micro-frames / --no-shims / physical OAM dump /
tools/wla-dx assembly / temp/readbuf micro-ROM group.
