# data/

Runtime data for AprVisual (S1).

```
data/
  system-def/    .js module definitions — the board + chip netlists the WireCore parser loads:
                   nes-001.js            top-level NES-001 board (sub-modules + connections)
                   2a03.js  2a03/        the 2A03 CPU module wrapper + its nodenames/segdefs/transdefs
                   2c02.js  2c02/        the 2C02 PPU (deferred — 2A03 first per MD/struct/08)
                   cart-mmu0.js + cart-* cartridge MMU + PRG/CHR ROM/RAM + extra RAM modules
                   74HC04.js 74LS139.js 74LS373.js 74LS368.js  support TTL chips (modeled as transistors)
                   SRAM2K.js ROM32K.js ...                       memory modules (memory:{} + behavioral handler)
                   nes-pad.js pslatch.js 4021.js nes-cic1.js     controllers (4021 shift register) + CIC
```

These are **not vendored** here. Source: `ref/metalnes-main/data/system-def/` (gitignored — see
the licence note in `MD/note/05`), or write our own equivalents. The parser (`WireCore.Parse.cs`,
not yet implemented) reads the MetalNES `.js` format described in `MD/note/02`.

Test ROMs (for `--test` / `--test-dir`) come from a nesdev test-ROM collection, also not vendored
(see `nes-test-roms-master/` in `.gitignore`).
