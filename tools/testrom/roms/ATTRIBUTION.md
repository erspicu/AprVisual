# Bundled test ROMs — attribution & provenance

These are **third-party NES test ROMs**, redistributed here so anyone can clone this repo and
reproduce AprVisual.S1's hardware test-ROM validation (see
[`../../../MD/testrom/2026-07-08-testrom-toolchain-tutorial.en.md`](../../../MD/testrom/2026-07-08-testrom-toolchain-tutorial.en.md)).
They are **not** original work of this project.

## Source

- Collection: **[christopherpow/nes-test-roms](https://github.com/christopherpow/nes-test-roms)**
  (a widely-mirrored aggregation of the community's NES test ROMs).
- Original authors: **blargg** (the great majority — CPU/APU/PPU/OAM/interrupt/timing suites) and
  other NESdev contributors. Each suite's original `readme.txt` is bundled alongside its ROMs in
  this directory for the author's own description, requirements, and terms.

## What's here

- Only the **147 ROMs this project actually tests** (a subset of the full collection), grouped by
  suite exactly as in the upstream `checked/` tree.
- Scope: **NROM / CNROM, NTSC** only — AprVisual.S1's cartridge scope.
- `catalog.json`'s `romBase` points at this directory, so the runner finds them with no extra setup.

## Terms

These test ROMs have long been distributed freely for hardware and emulator verification; blargg's
suites in particular are published for exactly this purpose. They are included here under that intent,
with attribution, for reproducibility. All rights remain with their original authors. If you are an
author and would prefer a ROM not be redistributed here, please open an issue and it will be removed.
