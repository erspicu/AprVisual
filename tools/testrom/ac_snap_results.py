#!/usr/bin/env python3
"""Read AccuracyCoin's in-flight result table straight out of an engine snapshot.

The .sav format (WireCore.Snapshot.cs) carries every behavioral memory buffer in its MEMS
section; u1.ram is the NES's internal CPU RAM, and AccuracyCoin keeps one result byte per
test at $0400-$04FF. So the latest snapshot answers "which tests have run, and what did
each one score?" without touching the running engine.

With --oracle <AC_RESULTS_HEX dump from AprNes> it also diffs every filled byte against
the behavioural emulator's completed run (base $0300), flagging silent mismatches.

  python tools/testrom/ac_snap_results.py [--snap FILE|--dir SNAPDIR] [--oracle temp/ac/t200.txt]
"""
import argparse, glob, os, re, struct, sys

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
ASM  = os.path.join(REPO, "AprAccuracyCoinUnattended", "AccuracyCoin.asm")


def read_7bit(b, o):
    """BinaryWriter.Write(string) length prefix: LEB128 varint."""
    n = shift = 0
    while True:
        v = b[o]; o += 1
        n |= (v & 0x7F) << shift
        if not v & 0x80: return n, o
        shift += 7


def parse_snapshot(path):
    b = open(path, "rb").read()
    if b[:8] != b"APRSNAP1": sys.exit(f"{path}: bad magic")
    o = 8
    ver, frame = struct.unpack_from("<Ii", b, o); o += 8
    time, = struct.unpack_from("<q", b, o); o += 8
    node_count, transistor_count, rom_crc = struct.unpack_from("<iiI", b, o); o += 12
    o += 11          # config bools
    o += 8           # PpuWriteDelayHc + ResetHoldExtraHc
    mems = {}
    while o < len(b) - 4:
        tag = b[o:o+4].decode("ascii", "replace"); o += 4
        if tag == "END!": break
        (ln,) = struct.unpack_from("<i", b, o); o += 4
        body = b[o:o+ln]; o += ln
        if tag == "MEMS":
            (cnt,) = struct.unpack_from("<i", body, 0); p = 4
            for _ in range(cnt):
                slen, p = read_7bit(body, p)
                name = body[p:p+slen].decode("utf-8"); p += slen
                (mlen,) = struct.unpack_from("<i", body, p); p += 4
                mems[name] = body[p:p+mlen]; p += mlen
    return frame, time, mems


def result_names():
    names = {}
    for ln in open(ASM, encoding="utf-8", errors="replace"):
        m = re.match(r"\s*(result_\w+)\s*=\s*\$([0-9A-Fa-f]+)", ln)
        if m: names[int(m.group(2), 16)] = m.group(1)[7:]
    return names


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--snap")
    ap.add_argument("--dir", default=os.path.join(REPO, "tools", "testrom", "out", "ac", "snaps"))
    ap.add_argument("--oracle", default=os.path.join(REPO, "temp", "ac", "t200.txt"))
    a = ap.parse_args()
    try: sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception: pass

    snap = a.snap or max(glob.glob(os.path.join(a.dir, "state_f*.sav")), key=os.path.getmtime)
    frame, time, mems = parse_snapshot(snap)
    ram = mems.get("u1.ram")
    if ram is None: sys.exit(f"u1.ram not in snapshot (memories: {', '.join(mems)})")

    oracle = None
    if a.oracle and os.path.isfile(a.oracle):
        m = re.search(r"AC_RESULTS_HEX:([0-9A-F]+)", open(a.oracle).read())
        if m:
            h = m.group(1)
            oracle = bytes(int(h[i:i+2], 16) for i in range(0, len(h), 2))   # base $0300

    names = result_names()
    filled = [(addr, ram[addr]) for addr in range(0x400, 0x500) if ram[addr] != 0]

    print(f"snapshot : {os.path.basename(snap)}  (frame {frame:,}, t={time:,})")
    # AccuracyCoin grades 141 tests (the completion block $07F4 / PostAllTestTally counts them;
    # "141/141" is that number). The result table $0400-$0492 also carries slots for the 5 DRAW
    # info tests, so `filled` can run a little past 141 late in the run -- that's expected, the
    # denominator here is the graded total. (147 = the *blargg* suite count, unrelated to AC.)
    print(f"progress : {len(filled)} / 141 result bytes filled")
    ec = ram[0x0EC]
    magic = ram[0x7F0:0x7F3].hex().upper()
    print(f"Debug_EC : ${ec:02X}   completion magic @$07F0: {magic}{'  <-- DONE' if magic=='DEB061' else ''}")
    print()
    mismatch = 0
    print(f"  {'addr':5} {'result':6} {'oracle':6}  name")
    for addr, val in filled:
        oval = oracle[addr - 0x300] if oracle and addr - 0x300 < len(oracle) else None
        mark = ""
        if oval is not None and oval != val:
            mark = "  <-- DIFFERS from AprNes"; mismatch += 1
        print(f"  ${addr:03X}  ${val:02X}    {'$%02X' % oval if oval is not None else '  ?'}   {names.get(addr,'?')}{mark}")
    print()
    if oracle:
        print(f"verdict vs oracle: {len(filled)-mismatch}/{len(filled)} match, {mismatch} differ")
        print("(a differing byte is not automatically a FAIL -- some tests have multiple accepted")
        print(" 'success codes' -- but every difference deserves a look)")


if __name__ == "__main__":
    main()
