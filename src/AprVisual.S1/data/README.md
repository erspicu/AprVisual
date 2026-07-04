# data/ — runtime module definitions

`data/system-def/` holds the MetalNES-format `.js` module definitions the engine
loads at startup (`--system-def-dir` overrides the location).

**Not vendored** (licensing): the netlist data (Visual2A03/2C02 segdefs/transdefs/
nodenames) and the MetalNES-lineage board/TTL defs must be supplied locally — e.g.
from `ref/metalnes-main/data/system-def/` — into a local directory such as
`AprVisualBenchMark/data/system-def/` (gitignored).

**Tracked here**: only modules authored by this project — see
`system-def/README.md` (currently `nes-pad-behavioral.js`, the behavioral
controller connector used in test mode).
