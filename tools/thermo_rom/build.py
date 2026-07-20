#!/usr/bin/env python3
# Build thermo.nes: assemble PRG with WLA-DX, generate an 8KB CHR font, wrap in iNES.
import os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
WLA  = os.path.join(HERE, "..", "wla-dx", "wla-6502.exe")
LINK = os.path.join(HERE, "..", "wla-dx", "wlalink.exe")
ASM  = os.path.join(HERE, "thermo.asm")
OBJ  = os.path.join(HERE, "thermo.o")
LNK  = os.path.join(HERE, "link.tmp")
PRG  = os.path.join(HERE, "thermo_prg.bin")
NES  = os.path.join(HERE, "thermo.nes")

# ---- 8x8 glyphs for hex digits 0-F (5x7 in an 8x8 cell), '#'=pixel ----
FONT = {
0x0:[" ###  ","#   # ","#  ## ","# # # ","##  # ","#   # "," ###  ","      "],
0x1:["  #   "," ##   ","  #   ","  #   ","  #   ","  #   "," ###  ","      "],
0x2:[" ###  ","#   # ","    # ","   #  ","  #   "," #    ","##### ","      "],
0x3:["##### ","   #  ","  #   ","   #  ","    # ","#   # "," ###  ","      "],
0x4:["   #  ","  ##  "," # #  ","#  #  ","##### ","   #  ","   #  ","      "],
0x5:["##### ","#     ","#### ","    # ","    # ","#   # "," ###  ","      "],
0x6:["  ##  "," #    ","#     ","#### ","#   # ","#   # "," ###  ","      "],
0x7:["##### ","    # ","   #  ","  #   "," #    "," #    "," #    ","      "],
0x8:[" ###  ","#   # ","#   # "," ###  ","#   # ","#   # "," ###  ","      "],
0x9:[" ###  ","#   # ","#   # "," #### ","    # ","   #  "," ##   ","      "],
0xA:[" ###  ","#   # ","#   # ","##### ","#   # ","#   # ","#   # ","      "],
0xB:["#### ","#   # ","#   # ","#### ","#   # ","#   # ","#### ","      "],
0xC:[" ###  ","#   # ","#     ","#     ","#     ","#   # "," ###  ","      "],
0xD:["#### ","#   # ","#   # ","#   # ","#   # ","#   # ","#### ","      "],
0xE:["##### ","#     ","#     ","#### ","#     ","#     ","##### ","      "],
0xF:["##### ","#     ","#     ","#### ","#     ","#     ","#     ","      "],
}

def glyph_bytes(rows):
    # 8 plane-0 bytes (bit7 = leftmost pixel), then 8 zero plane-1 bytes
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
    chr_rom = bytearray(8192)          # 512 tiles * 16 bytes, zero = blank tile
    for nib, rows in FONT.items():
        tile = 0x30 + nib              # hex-digit glyph lives at tile $30+N
        off  = tile * 16
        chr_rom[off:off+16] = glyph_bytes(rows)
    return bytes(chr_rom)

def run(cmd):
    print("  $", " ".join(os.path.basename(c) if c.endswith(".exe") else c for c in cmd))
    r = subprocess.run(cmd, cwd=HERE, capture_output=True, text=True)
    if r.stdout.strip(): print(r.stdout.strip())
    if r.returncode != 0:
        print(r.stderr.strip() or r.stdout.strip()); sys.exit("  ASSEMBLE/LINK FAILED")

def main():
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
