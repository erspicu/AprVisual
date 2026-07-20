# NES open-bus thermometer test ROM

A tiny NROM test ROM that turns the PPU open-bus decay into a **thermometer** and prints
the temperature in whole degrees Celsius over **0–100 °C** (e.g. `25 DEGREE CELSIUS`), plus
the AprNes CLI hooks to drive it headlessly. This is the runnable companion to
`WebSite/s1a/nes-thermometer.html`.

## What it does

The PPU I/O open-bus latch (read via a write-only register) is held only by parasitic
capacitance and bleeds to 0 through junction leakage. That leakage is Arrhenius in
temperature (`I_leak ∝ exp(-Ea/kT)`), so the **decay time** scales strongly with
temperature. The ROM:

1. primes the latch to `$FF` by **writing `$2002`** (PPUSTATUS is read-only, so the write is
   ignored as a register op but still fills the latch — with no side effect),
2. tight-polls a **write-only register (`$2001`)**, which returns the full 8-bit open bus,
   and counts loop iterations until the first bit drops (a 24-bit decay count in zero page
   `$10..$12`),
3. inverts count → temperature with a precomputed **integer-°C lookup table** and a 7-step
   power-of-two binary search (no 6502 multiply/divide/float), and
4. prints `NN DEGREE CELSIUS` on screen (the whole-degree result is also at `$0013`).

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
           --dump-mem 0013 --timed-screenshots "c_25.png:5.0"
```

`--timed-screenshots "<path>:<seconds>"` captures a specific frame (frame ≈ 5 s here).
`--dump-mem 0013` reads the whole-degree result (e.g. `25` = 25 °C); `--dump-mem 0010` reads
the raw 24-bit decay count. Note: cold runs decay slowly (0 °C ≈ 4.4 s of emulated time to
finish), so give `--time` enough headroom (`--time 6` covers the whole 0–100 °C range).

⚠️ Use **PowerShell**, not Git Bash, for `--timed-screenshots`: MSYS mangles the `path:sec`
argument (the `:` looks like a unix path-list separator).

## Verified round-trip (set °C → displayed °C)

**All 101 integer degrees 0–100 °C read back exactly (0 mismatches / 101).** A sample:

| set °C | displayed | | set °C | displayed |
|---:|:---:|---|---:|:---:|
| 0  | `0`  | | 60  | `60`  |
| 10 | `10` | | 75  | `75`  |
| 25 | `25` | | 90  | `90`  |
| 50 | `50` | | 100 | `100` |

Screenshots: `c_0.png`, `c_25.png`, `c_50.png`, `c_75.png`, `c_100.png`.

The per-degree counts span **587×** (0 °C = 492,590 loops → 100 °C = 838), strictly
monotonic with no ties — so the inversion is a clean per-degree table. Fitting
`ln(count)` vs `1/T` gives **R² = 1.000000, Ea = 0.560 eV** — i.e. the measurement recovers
the exact activation energy the emulator was given. That is a perfect but **circular**
round-trip: it validates the software pipeline, not the physics on real silicon (see the
study's "Corrections & thanks").

> **History.** An earlier build blurred above ~44 °C (adjacent degrees sharing a count). The
> cause was the emulator's `NowDots()` helper deriving time from `frame_count/scanline/x`,
> where `frame_count++` fires at scanline 240 (a host-side render boundary) — a
> non-monotonic clock that jumped a whole frame and tripped the decay early once the decay
> period fell near one frame. Fixed by deriving `NowDots()` from the monotonic CPU-cycle
> counter (`mcCycleCount × 3`); this touches only the open-bus helper, not any game-visible
> timing, so AccuracyCoin/blargg are unaffected (`ppu_open_bus` + `ppu_read_buffer` still PASS).

## Running on other emulators

The ROM initialises its own palette (`$3F00` = black, `$3F01` = white) **and clears the
attribute table to palette 0**, so the text renders correctly regardless of each emulator's
undefined power-on palette RAM (an earlier version left some glyphs on an uninitialised
palette → invisible/black on some emulators).

More importantly, the thermometer **needs a model of PPU open-bus decay** — it times how long
the open-bus latch takes to fall. Most emulators (and the custom AprNes with this build's
temperature model) have it; **an emulator that never decays open bus has nothing to measure**,
so the poll loop would run forever. The ROM guards against that: after ~2M loops with no bit
dropping it gives up and shows **`-- DEGREE CELSIUS`** instead of hanging on a black screen.
The same `--` appears for temperatures below the calibrated range (colder than ~0 °C).

Used this way the ROM is also a **decay-model probe**: an emulator that clamps the count to a
fixed fast value reads back as a spuriously hot fixed temperature; one that never decays reads
`--`; only a genuine Arrhenius decay tracks the knob. See deep dive #4 for the four-emulator
survey.

## Honest limits (this is a technical demo, not a precision instrument)

- **Resolution.** Every whole degree 0–100 °C maps to a distinct count, so the round-trip is
  per-degree exact. The only residual error is the ±0.5 °C of rounding an analogue
  temperature to an integer display — and it is **uniform** cold-to-hot (no warm-end
  degradation). 0.1 °C would be false precision, so the ROM shows whole degrees only.
- **Range.** The table covers 0–100 °C; outside it clamps (and reads `--` when colder, or when
  open bus never decays).
- **Per-model calibration.** The count depends on the emulator's exact loop timing, so the
  table is calibrated to *this* build (`tools/aprnes`). On real hardware the absolute offset
  differs per console (a one-point calibration fixes it; the Arrhenius *slope* is universal).
- **Circular round-trip.** The emulator sets decay = f(T) and the ROM inverts it, so recovering
  T proves the software math, not that the model holds on real silicon.
- **Die, not room.** On real silicon the PPU self-heats +30–40 °C, so this reads the *die*
  temperature unless you cold-boot and read within a few seconds. In the emulator the knob is
  literal ambient. See the study for the full discussion.

## Files

- `thermo.asm` — the 6502 source (WLA-DX syntax, heavily commented as a tutorial).
- `build.py` — table generation + assemble + CHR font + iNES wrap.
- `counts_by_degree.json` — the per-degree emulator measurements the table is built from.
- `calib_stats.py` / `make_calib_svg.py` — calibration diagnostics (fit R²/Ea, residuals,
  held-out check standards) and the SVG plots used in the study.
- `thermo.nes` — the built ROM (committed for convenience).
- `c_*.png` — sample frame captures of the Celsius readout.

Build intermediates (`thermo.o`, `link.tmp`, `thermo_prg.bin`, `thermo_table.inc`) are gitignored.
