#!/usr/bin/env python3
# Build thermo.nes: generate the count->temperature table, assemble the PRG with WLA-DX,
# generate an 8KB CHR font, and wrap it all in an iNES header.
import os, subprocess, sys, json, math

HERE = os.path.dirname(os.path.abspath(__file__))
WLA  = os.path.join(HERE, "..", "wla-dx", "wla-6502.exe")
LINK = os.path.join(HERE, "..", "wla-dx", "wlalink.exe")
ASM  = os.path.join(HERE, "thermo.asm")
OBJ  = os.path.join(HERE, "thermo.o")
LNK  = os.path.join(HERE, "link.tmp")
PRG  = os.path.join(HERE, "thermo_prg.bin")
NES  = os.path.join(HERE, "thermo.nes")
CNTS = os.path.join(HERE, "counts_by_degree.json")
INC  = os.path.join(HERE, "thermo_table.inc")

# ---- 8x8 glyphs (5x7 in an 8x8 cell), '#'=pixel. Keyed by CHARACTER so each glyph
#      is stored at CHR tile index == its ASCII code. The ROM then writes plain ASCII
#      bytes straight to the nametable (tile $20 stays blank = space). ----
FONT = {
'0':[" ###  ","#   # ","#  ## ","# # # ","##  # ","#   # "," ###  ","      "],
'1':["  #   "," ##   ","  #   ","  #   ","  #   ","  #   "," ###  ","      "],
'2':[" ###  ","#   # ","    # ","   #  ","  #   "," #    ","##### ","      "],
'3':["##### ","   #  ","  #   ","   #  ","    # ","#   # "," ###  ","      "],
'4':["   #  ","  ##  "," # #  ","#  #  ","##### ","   #  ","   #  ","      "],
'5':["##### ","#     ","#### ","    # ","    # ","#   # "," ###  ","      "],
'6':["  ##  "," #    ","#     ","#### ","#   # ","#   # "," ###  ","      "],
'7':["##### ","    # ","   #  ","  #   "," #    "," #    "," #    ","      "],
'8':[" ###  ","#   # ","#   # "," ###  ","#   # ","#   # "," ###  ","      "],
'9':[" ###  ","#   # ","#   # "," #### ","    # ","   #  "," ##   ","      "],
'-':["      ","      ","      ","##### ","      ","      ","      ","      "],
'.':["      ","      ","      ","      ","      ","  ##  ","  ##  ","      "],
'C':[" ###  ","#   # ","#     ","#     ","#     ","#   # "," ###  ","      "],
'D':["####  ","#   # ","#   # ","#   # ","#   # ","#   # ","####  ","      "],
'E':["##### ","#     ","#     ","#### ","#     ","#     ","##### ","      "],
'G':[" ###  ","#   # ","#     ","#  ## ","#   # ","#   # "," #### ","      "],
'I':["##### ","  #   ","  #   ","  #   ","  #   ","  #   ","##### ","      "],
'L':["#     ","#     ","#     ","#     ","#     ","#     ","##### ","      "],
'R':["####  ","#   # ","#   # ","####  ","# #   ","#  #  ","#   # ","      "],
'S':[" #### ","#     ","#     "," ###  ","    # ","    # ","####  ","      "],
'U':["#   # ","#   # ","#   # ","#   # ","#   # ","#   # "," ###  ","      "],
}

def glyph_bytes(rows):
    # 8 plane-0 bytes (bit7 = leftmost pixel), then 8 zero plane-1 bytes (=> color 1)
    p0 = []
    for r in range(8):
        row = (rows[r] + "        ")[:8]
        b = 0
        for c in range(8):
            if row[c] == '#':
                b |= (0x80 >> c)
        p0.append(b)
    return bytes(p0) + bytes(8)

def build_chr():
    chr_rom = bytearray(8192)          # 512 tiles * 16 bytes; zero = blank tile ($20 space)
    for ch, rows in FONT.items():
        tile = ord(ch)                 # glyph lives at tile index == ASCII code
        off  = tile * 16
        chr_rom[off:off+16] = glyph_bytes(rows)
    return bytes(chr_rom)

def build_table():
    # count->temperature lookup table at 0.1 C resolution, from the per-degree
    # emulator measurements (counts_by_degree.json). Emitted as three SoA byte arrays
    # (thr_lo / thr_mid / thr_hi), 512 entries each, DECREASING (cold = big count).
    # index i (0..511) == temperature in tenths of a degree C, i.e. 0.0 .. 51.1 C.
    # thr[i] is the count at temperature (i*0.1 - 0.05), so the ROM's radix search
    # ("largest i with thr[i] >= measured count") rounds to the nearest 0.1 C.
    if not os.path.exists(CNTS):
        sys.exit("  missing %s (run the per-degree measurement first)" % os.path.basename(CNTS))
    counts = {int(k): v for k, v in json.load(open(CNTS)).items()}
    Tlo, Thi = min(counts), max(counts)
    seq = [counts[T] for T in range(Tlo, Thi + 1)]
    for i in range(1, len(seq)):            # force strictly decreasing (kills warm-end ties)
        if seq[i] >= seq[i - 1]:
            seq[i] = seq[i - 1] - 1
    mono = {T: seq[T - Tlo] for T in range(Tlo, Thi + 1)}

    def count_at(theta):                    # interpolate in (1/T_K, ln count)
        a = max(Tlo, min(Thi - 1, int(math.floor(theta))))
        b = a + 1
        xa, xb = 1.0 / (a + 273.15), 1.0 / (b + 273.15)
        ya, yb = math.log(mono[a]), math.log(mono[b])
        x = 1.0 / (theta + 273.15)
        return math.exp(ya + (yb - ya) * (x - xa) / (xb - xa))

    N = 512
    table = [max(0, min(0xFFFFFF, int(round(count_at(i * 0.1 - 0.05))))) for i in range(N)]

    def emit(name, sel):
        rows = [".db " + ",".join("$%02X" % sel(table[j]) for j in range(i, i + 16))
                for i in range(0, N, 16)]
        return name + ":\n" + "\n".join(rows) + "\n"

    with open(INC, "w") as f:
        f.write("; auto-generated by build.py -- 0.1 C count->temperature table (do not edit)\n")
        f.write("; index i (0..511) == temperature in tenths of a degree C (0.0 .. 51.1)\n")
        f.write(emit("thr_lo",  lambda v: v & 0xFF))
        f.write(emit("thr_mid", lambda v: (v >> 8) & 0xFF))
        f.write(emit("thr_hi",  lambda v: (v >> 16) & 0xFF))
    print("  generated thermo_table.inc (512 x 3 bytes, %d..%d C source)" % (Tlo, Thi))

def run(cmd):
    print("  $", " ".join(os.path.basename(c) if c.endswith(".exe") else c for c in cmd))
    r = subprocess.run(cmd, cwd=HERE, capture_output=True, text=True)
    if r.stdout.strip(): print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr.strip() or r.stdout.strip()); sys.exit("  ASSEMBLE/LINK FAILED")

def main():
    # 0. generate the count->temperature lookup table (.inc, included by thermo.asm)
    build_table()
    # 1. assemble
    run([WLA, "-o", OBJ, ASM])
    # 2. link to a raw 16KB PRG binary
    with open(LNK, "w") as f:
        f.write("[objects]\n%s\n" % os.path.basename(OBJ))
    run([LINK, "-b", os.path.basename(LNK), os.path.basename(PRG)])
    prg = open(PRG, "rb").read()
    if len(prg) != 16384:
        # pad/truncate to 16KB just in case
        prg = (prg + b"\x00" * 16384)[:16384]
    # 3. CHR font
    chr_rom = build_chr()
    # 4. iNES header: NES^Z, 1x16KB PRG, 1x8KB CHR, mapper 0, horizontal mirroring
    header = bytes([0x4E,0x45,0x53,0x1A, 1, 1, 0x00, 0x00, 0,0,0,0,0,0,0,0])
    with open(NES, "wb") as f:
        f.write(header); f.write(prg); f.write(chr_rom)
    print("  wrote %s  (%d bytes = 16 hdr + %d prg + %d chr)" %
          (os.path.basename(NES), 16+len(prg)+len(chr_rom), len(prg), len(chr_rom)))

if __name__ == "__main__":
    main()
