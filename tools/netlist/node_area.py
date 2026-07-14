#!/usr/bin/env python3
"""node_area.py - per-node polygon area from a Visual6502-style segdefs file.

Shoelace area per polygon, summed per node and split by layer. This is the
data source for the capacitance-proxy upgrades (floating-group tie-break,
canonical ordering, strength LUT) recorded for the accuracy epoch:
Visual6502 resolved floating groups by walk order, MetalNES/S1 by connection
count; layered area is the next rung. Load-time only -- the hot path would
just compare a precomputed key.

Usage:
    python tools/netlist/node_area.py <segdefs.js> [name1 name2 ...]
    python tools/netlist/node_area.py <segdefs.js> --top 20
"""
import re, sys
from collections import defaultdict

LAYERS = {0: "metal", 1: "sw-diff", 2: "in-diode", 3: "gnd-diff", 4: "pwr-diff", 5: "poly"}

def node_areas(segdefs_path):
    txt = open(segdefs_path, encoding="utf-8", errors="replace").read()
    area = defaultdict(lambda: defaultdict(float))
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'\s*,\s*(\d+)\s*,\s*([0-9,\s]+)\]", txt):
        node = int(m.group(1)); layer = int(m.group(3))
        cs = [int(x) for x in m.group(4).replace(" ", "").split(",") if x]
        xs, ys = cs[0::2], cs[1::2]
        a = sum(xs[i] * ys[(i + 1) % len(xs)] - xs[(i + 1) % len(xs)] * ys[i] for i in range(len(xs)))
        area[node][layer] += abs(a) / 2.0
    return area

def main():
    seg = sys.argv[1]
    area = node_areas(seg)
    print(f"nodes with geometry: {len(area)}")
    args = sys.argv[2:]
    if args[:1] == ["--top"]:
        n = int(args[1]) if len(args) > 1 else 20
        for node, la in sorted(area.items(), key=lambda kv: -sum(kv[1].values()))[:n]:
            per = " ".join(f"{LAYERS[k]}={v:.0f}" for k, v in sorted(la.items()))
            print(f"  id={node:<6} total={sum(la.values()):>10.0f}  {per}")
    elif args:
        # resolve names via a nodenames.js sitting next to segdefs, if present
        import os
        names = {}
        nn = os.path.join(os.path.dirname(seg), "nodenames.js")
        if os.path.exists(nn):
            for m in re.finditer(r"([\w/#~]+)\s*:\s*(\d+)", open(nn, encoding="utf-8", errors="replace").read()):
                names.setdefault(m.group(1), int(m.group(2)))
        for a in args:
            nid = names.get(a, int(a) if a.isdigit() else None)
            if nid is None or nid not in area:
                print(f"  {a}: no geometry"); continue
            la = area[nid]
            per = " ".join(f"{LAYERS[k]}={v:.0f}" for k, v in sorted(la.items()))
            print(f"  {a:<12} id={nid:<6} total={sum(la.values()):>10.0f}  {per}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
