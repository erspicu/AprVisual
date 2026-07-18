#!/usr/bin/env python3
"""gen_m5_interface.py — M5 die-layout highlights: the CPU<->PPU interface on the 2C02.

M5 (the board census) is a board-level mechanism, so it has no per-die node set of its
own. Its die-relevant teaching point IS highlightable, though: the ENTIRE CPU<->PPU
conversation crosses just four die-die buses (~13 signals) — io_db[7:0], io_ab[2:0],
io_rw, int — while a whole separate bus (ab/db + control) faces the board's VRAM and the
'373 latch. This resolves those signal names to 2C02 node ids and emits the highlight .js.

Usage: python gen_m5_interface.py --nodenames .../2c02/nodenames.js --out m5_2c02_nodes.js
Stdlib only. Layout data is CC-BY-NC-SA (Visual 2C02).
"""
import argparse, re, json, sys


def parse_nodenames(path):
    name_to_id = {}
    for m in re.finditer(r"['\"]?([\w/#~+.\-]+)['\"]?\s*:\s*(\d+)", open(path, encoding="utf-8", errors="replace").read()):
        name_to_id.setdefault(m.group(1), int(m.group(2)))
    return name_to_id


def collect(name_to_id, patterns):
    ids, hit = [], []
    for pat in patterns:
        rx = re.compile("^(?:" + pat + ")$")
        for nm, nid in name_to_id.items():
            if rx.match(nm):
                ids.append(nid); hit.append(nm)
    return sorted(set(ids)), sorted(set(hit))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args()
    n = parse_nodenames(a.nodenames)

    # the four die-die buses = the entire CPU<->PPU interface
    iface_ids, iface_nm = collect(n, [r"io_db\d", r"io_ab\d", r"io_rw", r"io_ce", r"int", r"_int"])
    # the board-facing side: PPU<->VRAM address/data bus (the other, larger bus)
    vram_ids, vram_nm = collect(n, [r"ab\d+", r"db\d+", r"_?ab\d+", r"_?db\d+"])
    vram_ids = sorted(set(vram_ids) - set(iface_ids))
    # board control lines the '373 / decoders gate
    ctrl_ids, ctrl_nm = collect(n, [r"_?rd", r"_?wr", r"ale", r"_?res\d?", r"r_?w"])

    dump = {
        "chip": "2C02", "mechanism": "M5",
        "categories": {
            "cpu_ppu": {"label": "CPU↔PPU interface — the 4 die-die buses (io_db/io_ab/io_rw/int)",
                        "color": "#ff2d55", "nodes": iface_ids},
            "vram_bus": {"label": "PPU↔VRAM address/data bus (board-facing)",
                         "color": "#5ac8fa", "nodes": vram_ids},
            "control": {"label": "board control lines (rd/wr/ale/res — gated by the '373 + decoders)",
                        "color": "#ffb454", "nodes": ctrl_ids},
        },
        "validated": [{"name": nm, "node": n[nm], "status": "found"}
                      for nm in ["io_db0", "io_ab0", "io_rw", "int"] if nm in n],
    }
    payload = ("// AprVisual S1a — M5 interface highlight (gen_m5_interface.py). Do not edit.\n"
               "window.DV_HIGHLIGHTS = " + json.dumps(dump, ensure_ascii=False, separators=(",", ":")) + ";\n")
    open(a.out, "w", encoding="utf-8").write(payload)
    print(f"M5: cpu_ppu={len(iface_ids)} ({', '.join(iface_nm)}) | vram_bus={len(vram_ids)} | control={len(ctrl_ids)}")
    print(f"  wrote {a.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
