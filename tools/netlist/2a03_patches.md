# 2A03 netlist curation record

This transdefs.js is MetalNES's curated copy of Quietust's Visual2A03 raw data,
plus AprVisual's own patches. Every deviation from the raw upstream
(`visual2a03-transdefs.js`, 10,946 rows) is listed here. Policy: patches restore
devices whose geometry already exists in segdefs (extraction misses), each with
independent silicon-side corroboration; nothing is invented.

| device | origin | what / why |
|---|---|---|
| `t14634b` | MetalNES (inherited, undocumented upstream) | gate=#11444 pulls #11466 (ACLK phase) to vss. Geometry verified present in segdefs (poly #11444 crossing between #11466 sw-diff and vss gnd-diff at (4770..4784, 5084..5090)). |
| `t12397`/`t12398` c1↔c2 swap | MetalNES (inherited) | NMOS pass-device terminals are interchangeable; semantically neutral. |
| `t13032b` | **AprVisual 2026-07-14** | Restores the R4015 read-decode a1=0 input (gate=_ab1/10055 pulls product term #10975 to vss). Without it /r4015 fires on $x17 reads too: APU status leaks onto the internal bus during $4017/joy2 reads, spuriously clearing the frame-IRQ flag (AccuracyCoin APURegActivation err6). Evidence: segdefs geometry complete at (6856..6862, 7338..7348); BreakNES `APUSim/regs.cpp pla[4] = NOR6(nREGRD, nA0, A1, nA2, A3, nA4)`; hardware behavior ($4017 reads do not clear frame IRQ). Full forensics: `MD/testrom/2026-07-14-APURegActivation-err6-R4015解碼缺管.md`. |

File note: the module definition (`2a03.js`) loads **`transdefs_named.js`** —
that file is the live netlist. `transdefs.js` is kept in sync as the id-only
mirror; both carry the `t13032b` row. (An earlier patch attempt edited only
`transdefs.js` and silently changed nothing — checking which file `*_files`
references is step zero of any netlist patch.)

Checksum note: measured after the patch went live, the golden NodeStates
checksums are UNCHANGED at both calibration points (300k `0x794A43ABDF169ADA`,
1M `0x6D4CCBCE2E9CD599`, full_palette --extra-ram) — the benchmark ROM never
exercises the APU register read decode, and outside APU-read windows the term
was already held low by the read-enable input, so the added pulldown is
electrically invisible there.
