#!/usr/bin/env python3
"""Apply AprVisual's 2A03 netlist patches to a local (gitignored) data dir.

The Visual2A03-derived data under AprVisualBenchMark/data/system-def/2a03/ is
licensed material and stays out of git, so the curation lives here as an
idempotent patcher plus the provenance record 2a03_patches.md (a tracked copy
of the data dir's PATCHES.md). Run after refreshing the data from upstream:

    python tools/netlist/apply_2a03_patches.py [data_dir]

The module definition (2a03.js) loads transdefs_named.js -- that file is the
live netlist; transdefs.js is kept in sync as the id-only mirror.
"""
import sys, os

DATA = sys.argv[1] if len(sys.argv) > 1 else "AprVisualBenchMark/data/system-def/2a03"

COMMENT = """\
// AprVisual patch (2026-07-14): restore the R4015 read-decode a1=0 input that the
// polygon-to-transistor extraction dropped. Geometry is fully present in segdefs
// (node 10975 diffusion finger (6840..6856, 7338..7348) abutting the _ab1 poly column
// x[6856..6862], vss on the far side -- vertex-identical to extracted sibling t12933),
// but no raw transdefs row exists, so /r4015 fired on $x17 reads too: APU status leaked
// onto the internal bus during $4017/joy2 reads, spuriously clearing the frame-IRQ flag
// (AccuracyCoin APURegActivation err6). Silicon truth: BreakNES APUSim regs.cpp
// pla[4] = NOR6(nREGRD, nA0, A1, nA2, A3, nA4). See tools/netlist/2a03_patches.md and
// MD/testrom/2026-07-14-APURegActivation-err6."""

# (filename, anchor row, patched row) -- anchor = the t13032 sibling in each format
PATCHES = [
    ("transdefs_named.js",
     "['t13032'        ,13213           ,10975           ,'vss'           ,[6774,6780,7338,7348],[10,10,6,1,60],false],",
     "['t13032b'       ,'_ab1'          ,10975           ,'vss'           ,[6856,6862,7338,7348],[10,10,6,1,60],false],"),
    ("transdefs.js",
     "['t13032',13213,10975,10001,[6774,6780,7338,7348],[10,10,6,1,60],false],",
     "['t13032b',10055,10975,10001,[6856,6862,7338,7348],[10,10,6,1,60],false],"),
]

def main():
    rc = 0
    for fname, anchor, row in PATCHES:
        path = os.path.join(DATA, fname)
        with open(path, encoding="utf-8") as f:
            s = f.read()
        if "t13032b" in s:
            print(f"  {fname}: already patched")
            continue
        if anchor not in s:
            print(f"  {fname}: ANCHOR NOT FOUND -- upstream format changed, patch by hand")
            rc = 1
            continue
        s = s.replace(anchor, anchor + "\n" + COMMENT + "\n" + row, 1)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(s)
        print(f"  {fname}: patched (t13032b)")
    return rc

if __name__ == "__main__":
    sys.exit(main())
