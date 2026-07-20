# NES open-bus thermometer test ROM

A tiny NROM test ROM that turns the PPU open-bus decay into a **thermometer** and prints
the temperature in Celsius (e.g. `25.0 DEGREE CELSIUS`), plus the AprNes CLI hooks to drive
it headlessly. This is the runnable companion to `WebSite/s1a/nes-thermometer.html`.

## What it does

The PPU I/O open-bus latch (`$2002` low 5 bits) is held only by parasitic capacitance and
bleeds to 0 through junction leakage. That leakage is Arrhenius in temperature
(`I_leak ∝ exp(-Ea/kT)`), so the **decay time** scales strongly with temperature. The ROM:

1. primes the latch to `$1F` (writes `$FF` to `$2003` / OAMADDR — drives all 8 latch bits
   without enabling NMI or rendering),
2. tight-polls `$2002`, keeping the low 5 bits, and counts loop iterations until the first
   bit drops (a 24-bit decay count in zero page `$10..$12`),
3. inverts count → temperature with a precomputed **0.1 °C lookup table** and a 9-step
   power-of-two binary search (no 6502 multiply/divide/float), and
4. prints `NN.N DEGREE CELSIUS` on screen (the temperature tenths are also at `$0013`).

Warmer → faster decay → smaller count; colder → slower → bigger count.

## Build

```
python tools/thermo_rom/build.py
```

Generates the count→temperature table (`thermo_table.inc`, from `counts_by_degree.json`),
assembles `thermo.asm` with WLA-DX (`tools/wla-dx/wla-6502.exe` + `wlalink.exe`), builds an
8 KB CHR font (ASCII-indexed: each glyph sits at tile == its ASCII code), and wraps it as
`thermo.nes` (iNES: 16 KB PRG + 8 KB CHR, mapper 0).

## Run (headless AprNes, temperature knob)

Two AprNes CLI flags (in `tools/aprnes/NesCore/TestRunnerCore.cs`):

- `--openbus-temp <°C>` — sets the PPU open-bus decay temperature (`NesCore.OpenBusTempCelsius`).
- `--dump-mem <hexaddr>` — prints 8 bytes + the little-endian u24 at that CPU address at exit.

```
AprNes.exe --rom thermo.nes --openbus-temp 25 --time 6 \
           --dump-mem 0013 --timed-screenshots "c_25.png:2.0"
```

`--timed-screenshots "<path>:<seconds>"` captures a specific frame (frame 120 ≈ 2.0 s).
`--dump-mem 0013` reads the temperature-in-tenths result (e.g. `250` = 25.0 °C).
Note: cold runs decay slowly, so the measurement may not finish by frame 120 (the screen
stays black until it does) — screenshot a later frame for cold temperatures.

⚠️ Use **PowerShell**, not Git Bash, for `--timed-screenshots`: MSYS mangles the `path:sec`
argument (the `:` looks like a unix path-list separator).

## Verified round-trip (set °C → displayed °C)

| set °C | displayed | | set °C | displayed |
|---:|:---:|---|---:|:---:|
| 0  | `0.0`  | | 30 | `30.0` |
| 10 | `10.0` | | 40 | `40.0` |
| 20 | `20.0` | | 43 | `43.5` |
| 25 | `25.0` | | 50 | `50.5` |

Screenshots: `c_0.png`, `c_25.png`, `c_50.png`, …

## Running on other emulators

The ROM initialises its own palette (`$3F00` = black, `$3F01` = white) **and clears the
attribute table to palette 0**, so the text renders correctly regardless of each emulator's
undefined power-on palette RAM (an earlier version left some glyphs on an uninitialised
palette → invisible/black on some emulators).

More importantly, the thermometer **needs a model of PPU open-bus decay** — it times how long
the `$2002` low bits take to fall. Most emulators (and the custom AprNes with this build's
temperature model) have it; **an emulator that never decays open bus has nothing to measure**,
so the poll loop would run forever. The ROM guards against that: after ~2M loops with no bit
dropping it gives up and shows **`--.- DEGREE CELSIUS`** instead of hanging on a black screen.
The same `--.-` appears for temperatures below the calibrated range (colder than ~0 °C).

## Honest limits (this is a technical demo, not a precision instrument)

- **Warm-end resolution.** When decay is fast (warm), the loop runs few times, so the count
  is coarse. Above ~40 °C, adjacent degrees can share a count, so the 0.1 digit there is not
  real resolution (≈ ±0.5 °C — 43 and 44 °C both read `43.5`). The cold end (0–~30 °C) is
  genuinely ~0.1 °C.
- **Range.** The table covers 0.0–51.1 °C; outside it clamps (and reads `--.-` when colder).
- **Per-model calibration.** The count depends on the emulator's exact loop timing, so the
  table is calibrated to *this* build (`tools/aprnes`). On real hardware the absolute offset
  differs per console (a one-point calibration fixes it; the Arrhenius *slope* is universal).
- **Die, not room.** On real silicon the PPU self-heats +30–40 °C, so this reads the *die*
  temperature unless you cold-boot and read within a few seconds. In the emulator the knob is
  literal ambient. See the study for the full discussion.

## Files

- `thermo.asm` — the 6502 source (WLA-DX syntax, heavily commented as a tutorial).
- `build.py` — table generation + assemble + CHR font + iNES wrap.
- `counts_by_degree.json` — the per-degree emulator measurements the table is built from.
- `thermo.nes` — the built ROM (committed for convenience).
- `c_*.png` — sample frame captures of the Celsius readout.

Build intermediates (`thermo.o`, `link.tmp`, `thermo_prg.bin`, `thermo_table.inc`) are gitignored.
