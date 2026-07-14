#!/usr/bin/env python3
"""Geometric completeness audit: find poly-crossing sites in segdefs that have
no corresponding transistor row in transdefs (extraction misses).

Signature of a transistor in Visual6502-style data: the diffusion strip is
SPLIT at the crossing, leaving two diffusion polygons (source/drain) whose
edges abut opposite edges of the poly gate strip with an overlapping span.

v1 heuristics (rectilinear, edge-abutment based):
  - edges quantized from polygon vertex lists; only axis-aligned edges used
  - candidate channel: two diffusion edge-abutments on opposite sides of the
    same poly polygon, opposing edge pair parallel and 4..14 units apart
    (channel length range covering the [_,_,6,..] geometries seen in 2A03),
    with >= 4 units of span overlap
  - skip if either diffusion net == poly net (butting contact, not a channel)
  - a transdefs row matches if {gate,c1,c2} == {poly-net, d1-net, d2-net}
    (ids), regardless of position
Calibration: run against the RAW upstream transdefs -- must rediscover both
known holes (t13032b / r4015-a1, and MetalNES's t14634b).
"""
import re, sys, os
from collections import defaultdict

def parse_segdefs(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    segs = []
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'\s*,\s*(\d+)\s*,\s*([0-9,\s]+)\]", txt):
        node = int(m.group(1)); layer = int(m.group(3))
        cs = [int(x) for x in m.group(4).replace(" ", "").split(",") if x]
        pts = list(zip(cs[0::2], cs[1::2]))
        segs.append((node, layer, pts))
    return segs

def parse_transdefs(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    rows = []
    for m in re.finditer(r"\[\s*'?([\w-]+)'?\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", txt):
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4))))
    return rows

def edges_of(pts):
    """Axis-aligned edges as ('v', x, y0, y1) or ('h', y, x0, x1), outward side unknown."""
    out = []
    n = len(pts)
    for i in range(n):
        (x0, y0), (x1, y1) = pts[i], pts[(i + 1) % n]
        if x0 == x1 and y0 != y1:
            out.append(('v', x0, min(y0, y1), max(y0, y1)))
        elif y0 == y1 and x0 != x1:
            out.append(('h', y0, min(x0, x1), max(x0, x1)))
    return out

def main():
    data = sys.argv[1] if len(sys.argv) > 1 else "ref/drive-download-20260505T172337Z-3-001"
    seg_path = os.path.join(data, "visual2a03-segdefs.js")
    trans_path = os.path.join(data, "visual2a03-transdefs.js")
    if not os.path.exists(seg_path):   # allow direct file paths too
        seg_path, trans_path = sys.argv[1], sys.argv[2]

    segs = parse_segdefs(seg_path)
    rows = parse_transdefs(trans_path)
    print(f"segdefs polygons: {len(segs)}, transdefs rows: {len(rows)}")

    DIFF = (1, 3, 4)   # switched / grounded / powered diffusion
    POLY = 5

    # index diffusion edges by (orient, coordinate) for O(1) opposite lookup
    diff_edges = defaultdict(list)   # ('v', x) -> [(y0, y1, net)]
    for node, layer, pts in segs:
        if layer not in DIFF: continue
        for (o, c, a, b) in edges_of(pts):
            diff_edges[(o, c)].append((a, b, node))

    # transistor triple set for matching
    tri = set()
    for name, g, c1, c2 in rows:
        tri.add((g, frozenset((c1, c2))))

    # for every poly polygon, find opposing-edge diffusion abutment pairs
    cands = []
    seen_sites = set()
    for node, layer, pts in segs:
        if layer != POLY: continue
        pe = edges_of(pts)
        vs = [(c, a, b) for (o, c, a, b) in pe if o == 'v']
        hs = [(c, a, b) for (o, c, a, b) in pe if o == 'h']
        for (edges, orient) in ((vs, 'v'), (hs, 'h')):
            for i in range(len(edges)):
                cL, aL, bL = edges[i]
                for j in range(len(edges)):
                    if i == j: continue
                    cR, aR, bR = edges[j]
                    w = cR - cL
                    if not (4 <= w <= 14): continue          # channel length window
                    lo, hi = max(aL, aR), min(bL, bR)
                    if hi - lo < 4: continue                  # span overlap
                    for (a1, b1, n1) in diff_edges.get((orient, cL), ()):
                        if min(b1, hi) - max(a1, lo) < 4 or n1 == node: continue
                        for (a2, b2, n2) in diff_edges.get((orient, cR), ()):
                            if min(b2, hi) - max(a2, lo) < 4 or n2 == node: continue
                            s = max(a1, a2, lo); e = min(b1, b2, hi)
                            if e - s < 4: continue
                            key = (node, n1, n2, cL, cR, s // 8, orient)
                            if key in seen_sites: continue
                            seen_sites.add(key)
                            if (node, frozenset((n1, n2))) not in tri:
                                site = (cL, cR, s, e) if orient == 'v' else (s, e, cL, cR)
                                cands.append((node, n1, n2, orient, site))
    # dedupe by unordered diffusion pair + rough location
    uniq = {}
    for (g, n1, n2, o, site) in cands:
        k = (g, frozenset((n1, n2)), site[0] // 16, site[2] // 16)
        uniq.setdefault(k, (g, n1, n2, o, site))
    print(f"candidate un-extracted crossings: {len(uniq)}")
    for (g, n1, n2, o, site) in sorted(uniq.values(), key=lambda r: r[4]):
        print(f"  gate={g:<6} d1={n1:<6} d2={n2:<6} {o} site={site}")
    return 0

if __name__ == "__main__":
    sys.exit(main())
