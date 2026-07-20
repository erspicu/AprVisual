# NES open-bus thermometer test ROM

A tiny NROM test ROM that turns the PPU open-bus decay into a **thermometer**, plus the
AprNes CLI hooks to drive it headlessly. This is the runnable companion to the study in
`WebSite/s1a/nes-thermometer.html`.

## What it does

The PPU I/O open-bus latch (`$2002` low 5 bits) is held only by parasitic capacitance and
bleeds to 0 through junction leakage. That leakage is Arrhenius in temperature
(`I_leak ∝ exp(-Ea/kT)`), so the **decay time** scales strongly with temperature. The ROM:

1. primes the latch to `$1F` (writes `$FF` to `$2003` / OAMADDR — drives all 8 latch bits
   without enabling NMI or rendering),
2. tight-polls `$2002`, keeping the low 5 bits, and counts loop iterations until the first
   bit drops (a 24-bit counter in zero page `$10..$12`),
3. renders that count as a 6-digit hex number and leaves it at `$0010` for `--dump-mem`.

Warmer → faster decay → **smaller** count; colder → slower → **bigger** count.

## Build

```
python tools/thermo_rom/build.py
```

Assembles `thermo.asm` with WLA-DX (`tools/wla-dx/wla-6502.exe` + `wlalink.exe`), generates
an 8 KB CHR font (hex glyphs `0-F` at tiles `$30-$3F`), and wraps it as `thermo.nes`
(iNES: 16 KB PRG + 8 KB CHR, mapper 0).

## Run (headless AprNes, temperature knob)

Two new AprNes CLI flags (in `tools/aprnes/NesCore/TestRunnerCore.cs`):

- `--openbus-temp <°C>` — sets the PPU open-bus decay temperature (`NesCore.OpenBusTempCelsius`).
- `--dump-mem <hexaddr>` — prints 8 bytes + the little-endian u24 at that CPU address at end of run.

```
AprNes.exe --rom thermo.nes --openbus-temp 25 --time 8 \
           --dump-mem 0010 --timed-screenshots "shot_25c.png:2.0"
```

`--timed-screenshots "<path>:<seconds>"` captures a specific frame (frame 120 ≈ 2.0 s).
Note: cold runs decay slowly, so the measurement may not finish by frame 120 (screen stays
black until it does) — screenshot a later frame for cold temperatures.

## Verified results (this build)

| °C | count (u24) | hex on screen | ratio vs 25 °C | Arrhenius predicted |
|---:|------------:|:-------------:|---------------:|--------------------:|
|  0 |     437,249 | `06AC01`      | 7.42 | 7.35 |
| 10 |     187,803 | `02DD9B`      | 3.19 | 3.17 |
| 20 |      85,381 | `014D85`      | 1.45 | 1.45 |
| 25 |      58,950 | `00E646`      | 1.00 | 1.00 |
| 30 |      40,778 | `009F4A`      | 0.69 | 0.70 |
| 40 |      19,317 | `004B75`      | 0.33 | 0.35 |
| 50 |       9,391 | `0024AF`      | 0.16 | 0.19 |

A clean monotonic thermometer — **46× count range across 0–50 °C**, matching the Arrhenius
law. `shot_25c.png` / `shot_50c.png` / `shot_0c_late.png` are frame captures of the readout.

## Files

- `thermo.asm` — the 6502 source (WLA-DX syntax).
- `build.py` — assemble + font + iNES wrap.
- `thermo.nes` — the built ROM (committed for convenience).
- `shot_*.png` — sample frame captures.

Build intermediates (`thermo.o`, `link.tmp`, `thermo_prg.bin`) are gitignored.
