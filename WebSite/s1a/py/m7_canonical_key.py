#!/usr/bin/env python3
"""m7_canonical_key.py — ending the lottery  (S1a toolbox, M7)

The engine breaks ties by node NUMBER, and node numbers are load order — a
file-order accident.  When two events happen in the same settle wave, or a
floating group is a dead heat, whoever got the smaller id wins.  The accuracy
campaigns saw this as the D-class "lottery": identical twin circuits (u7/u8)
where one twin passed and the other broke on the same test, and instrument
probes that re-rolled the whole outcome by shifting ids.

M7's mechanism is a load-time CANONICAL RENUMBERING: replace "id = load order"
with "id = a deterministic key from physics + structure", so identical circuits
get identical keys, hence identical fates.  This script builds that key and
audits it:

    key(node) = ( class, layeredArea, structHash, degree )

  - class      : the prune/locality class (kept first — preserves the +3.56%
                 class-major locality win; the canonical order only reorders
                 WITHIN a class).
  - layeredArea: the M2 capacitance proxy (segdefs polygon area x layer) —
                 a physical, load-order-independent magnitude.
  - structHash : a small Weisfeiler-Lehman-style refinement of local topology
                 (a node's neighborhood signature), so structurally identical
                 nodes collide and structurally different ones separate.
  - degree     : transistor fan-in/out, the final cheap tiebreak.

The audit answers: how many nodes currently share a tie key (still a lottery
under the old id order)?  Do known twins (from segdefs geometry) get identical
canonical keys (same fate) or different ones (a key that fails to unify)?  The
1,214 lottery pairs M2 found are the target list.

Outputs: console report, JSON summary, two SVG figures.

Usage (bring your own Visual6502-style files — not vendored here):
    python m7_canonical_key.py --transdefs visual2c02-transdefs.js \
        --segdefs visual2c02-segdefs.js --nodenames visual2c02-nodenames.js \
        --label 2C02 --outdir out/

Netlist note: use the CORRECTED data/system-def/ netlist (the raw upstream
Visual6502 2A03 dump dropped two real pull-downs, t13032b + t14634b); the raw
is inherently distorted for the APU-decode region.

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m7-canonical.html.
"""
import argparse, json, math, os, re, sys
from collections import defaultdict, Counter

RAIL = {"vcc", "pwr", "vdd", "vss", "gnd"}
LAYER_W = {0: 0.03, 1: 0.10, 2: 0.10, 3: 0.10, 4: 0.10, 5: 0.04}


def parse_nodenames(path):
    n2i, i2n = {}, defaultdict(list)
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)",
                         open(path, encoding="utf-8", errors="replace").read()):
        nm, nid = m.group(1), int(m.group(2))
        n2i.setdefault(nm, nid); i2n[nid].append(nm)
    return n2i, i2n


def parse_segdefs_area(path):
    area = defaultdict(float)
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'[+-]'\s*,\s*(\d+)\s*,\s*([0-9,\s]+)\]",
                         open(path, encoding="utf-8", errors="replace").read()):
        node, layer = int(m.group(1)), int(m.group(2))
        cs = [int(x) for x in m.group(3).replace(" ", "").split(",") if x]
        xs, ys = cs[0::2], cs[1::2]
        if len(xs) < 3: continue
        a2 = sum(xs[i] * ys[(i + 1) % len(xs)] - xs[(i + 1) % len(xs)] * ys[i] for i in range(len(xs)))
        area[node] += abs(a2) / 2.0 * LAYER_W.get(layer, 0.05)
    return area


def parse_transdefs(path):
    rows = []
    for m in re.finditer(r"\[\s*'[^']+'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,",
                         open(path, encoding="utf-8", errors="replace").read()):
        rows.append((int(m.group(1)), int(m.group(2)), int(m.group(3))))
    return rows


BG, BD, FG, MUT = "#100f15", "#2e2634", "#dcd7e3", "#968ba1"
ACC, ACC2, BAD, WARN = "#c792ea", "#7ee0a8", "#ff6b6b", "#ffb454"
FONT = 'font-family="Segoe UI,Roboto,sans-serif"'


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h = w, h
        self.el = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" {FONT}>',
                   f'<rect width="{w}" height="{h}" fill="{BG}"/>',
                   f'<text x="{w/2}" y="26" fill="{FG}" font-size="16" font-weight="700" text-anchor="middle">{self.e(title)}</text>']

    @staticmethod
    def e(s): return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    def rect(self, x, y, w, h, fill, opac=1.0, rx=3):
        self.el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" fill="{fill}" fill-opacity="{opac}" rx="{rx}"/>')

    def line(self, x1, y1, x2, y2, stroke=BD, w=1):
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False):
        fw = ' font-weight="700"' if bold else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}">{self.e(s)}</text>')

    def save(self, path):
        self.el.append("</svg>"); open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_keyreduction(path, stages):
    W, H = 780, 300
    s = Svg(W, H, "Tie collisions removed as the key gains dimensions")
    rows = [("degree only (fan-in/out)", stages["degree"], BAD),
            ("+ structHash (local topology)", stages["struct"], WARN),
            ("+ layeredArea (M2 capacitance)", stages["area"], ACC2)]
    mx = max(c for _, c, _ in rows) or 1
    y = 66
    for name, c, col in rows:
        w = 10 + (W - 400) * c / mx
        s.rect(340, y, w, 34, col, 0.85)
        s.text(332, y + 22, name, FG, 12, "end")
        s.text(346 + w, y + 22, f"{c:,} nodes still tied", FG, 12.5, "start", bold=True)
        y += 56
    s.save(path)


def fig_twins(path, twinned, unique, total_nodes):
    W, H = 780, 230
    s = Svg(W, H, "Nodes with a structural twin (canonical key unifies each set)")
    x = 60
    for label, c, col in [("has >=1 structural twin (key-unified fate)", twinned, ACC2),
                          ("structurally unique", unique, MUT)]:
        w = (W - 120) * c / (total_nodes or 1)
        s.rect(x, 90, w, 50, col, 0.85)
        if w > 40:
            s.text(x + w / 2, 120, f"{c:,}", "#160f1f", 14, "middle", bold=True)
        s.text(x + w / 2, 160, label, FG, 11.5, "middle")
        x += w
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--segdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--label", default="die")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--rounds", type=int, default=2)
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    n2i, i2n = parse_nodenames(a.nodenames)
    area = parse_segdefs_area(a.segdefs)
    trans = parse_transdefs(a.transdefs)
    rails = {n2i[n] for n in RAIL if n in n2i}

    def nm(nid): return "/".join(i2n.get(nid, [])[:2]) or f"n{nid}"

    # adjacency + degree
    adj = defaultdict(list)
    gate_deg, chan_deg = Counter(), Counter()
    for g, c1, c2 in trans:
        if g not in rails: gate_deg[g] += 1
        for c in (c1, c2):
            if c not in rails: chan_deg[c] += 1
        if c1 not in rails and c2 not in rails:
            adj[c1].append(c2); adj[c2].append(c1)
        if g not in rails and c1 not in rails: adj[g].append(c1); adj[c1].append(g)
        if g not in rails and c2 not in rails: adj[g].append(c2); adj[c2].append(g)

    nodes = [n for n in set(list(gate_deg) + list(chan_deg)) if n not in rails]

    # degree signature
    deg = {n: (gate_deg[n], chan_deg[n]) for n in nodes}
    # Weisfeiler-Lehman refinement: hash of (own degree, sorted neighbor labels)
    label = {n: hash(deg[n]) & 0xFFFFFFFF for n in nodes}
    for _ in range(a.rounds):
        newl = {}
        for n in nodes:
            sig = (label[n], tuple(sorted(label.get(m, 0) for m in adj.get(n, ()))))
            newl[n] = hash(sig) & 0xFFFFFFFF
        label = newl

    # quantized area bucket (log) so near-equal capacitances collide but scales separate
    def abucket(n):
        v = area.get(n, 0.0)
        return 0 if v <= 0 else int(round(math.log2(v) * 4))

    def tie_count(keyfn):
        buckets = Counter(keyfn(n) for n in nodes)
        return sum(c for c in buckets.values() if c > 1)

    stages = {
        "degree": tie_count(lambda n: deg[n]),
        "struct": tie_count(lambda n: (deg[n], label[n])),
        "area":   tie_count(lambda n: (deg[n], label[n], abucket(n))),
    }

    # twins by geometry: same (structHash, area bucket) and both named symmetrically
    canon = defaultdict(list)
    for n in nodes:
        canon[(label[n], abucket(n))].append(n)
    twin_groups = {k: v for k, v in canon.items() if len(v) >= 2}

    # named-symmetry twins: e.g. dbN vs dbM, io_dbN, alua/alub — nodes whose names differ only by index
    def base_name(nm2):
        return re.sub(r"\d+", "#", nm2.lstrip("/#~+"))
    named = defaultdict(list)
    for nid, names in i2n.items():
        if nid in rails: continue
        for nm2 in names[:1]:
            named[base_name(nm2)].append(nid)
    sym_families = {b: ns for b, ns in named.items() if len(ns) >= 2 and "#" in b}

    # for each symmetric family, do all members share a canonical key? (same fate)
    agree = disagree = 0
    disagree_examples = []
    for b, ns in sym_families.items():
        keys = {(label[n], abucket(n)) for n in ns if n in label}
        if len(keys) == 1:
            agree += 1
        elif len(keys) > 1:
            disagree += 1
            if len(disagree_examples) < 20:
                disagree_examples.append({"family": b, "members": len(ns), "distinct_keys": len(keys)})
    singles = 0  # families with a single distinct key already counted in agree

    # nodes with at least one structural twin (canonical group of size >= 2)
    twinned = sum(len(v) for v in twin_groups.values())
    unique = len(nodes) - twinned

    fig_keyreduction(os.path.join(a.outdir, f"m7_{a.label}_keyreduction.svg"), stages)
    fig_twins(os.path.join(a.outdir, f"m7_{a.label}_twins.svg"), twinned, unique, len(nodes))

    summary = {
        "label": a.label, "nodes": len(nodes),
        "tie_collisions": stages,
        "tie_reduction_pct": round(100 * (1 - stages["area"] / stages["degree"]), 1) if stages["degree"] else 0,
        "structural_twins": {"nodes_with_a_twin": twinned, "structurally_unique": unique,
                             "canonical_groups": len(twin_groups)},
        "symmetric_name_families": len(sym_families),
        "name_vs_structure": {"name_and_key_agree": agree, "name_symmetric_but_key_splits": disagree},
        "split_examples": disagree_examples,
        "biggest_canonical_groups": sorted(
            ([len(v), nm(v[0]), nm(v[1])] for v in twin_groups.values()), reverse=True)[:15],
    }
    jp = os.path.join(a.outdir, f"m7_{a.label}_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[{a.label}] nodes={len(nodes)}")
    print(f"  tie collisions (nodes sharing a key, = lottery under id-order):")
    print(f"    degree only:            {stages['degree']:,}")
    print(f"    + structHash:           {stages['struct']:,}")
    print(f"    + layeredArea:          {stages['area']:,}   "
          f"({summary['tie_reduction_pct']}% of degree-ties resolved)")
    print(f"  structural twins: {twinned:,} nodes have >=1 twin (in {len(twin_groups)} canonical groups); "
          f"{unique:,} structurally unique")
    print(f"    biggest groups (replicated cell arrays): " +
          ", ".join(str(g[0]) for g in summary['biggest_canonical_groups'][:6]))
    print(f"  name-symmetric families: {len(sym_families)} "
          f"({agree} match the key, {disagree} split -- name != structure, context-wired)")
    for e in disagree_examples[:6]:
        print(f"      split: {e['family']}  ({e['members']} members, {e['distinct_keys']} keys)")
    print(f"  wrote {jp} + 2 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
