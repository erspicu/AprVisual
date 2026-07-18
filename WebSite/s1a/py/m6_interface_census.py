#!/usr/bin/env python3
"""m6_interface_census.py — where phase can hurt  (S1a toolbox, M6)

The M6 family — dot-339, even_odd, ALERead, BGSerialIn — is one shape: a
cross-chip signal (a CPU write, delayed on the die by ~24 hc) arrives at a
2C02 comparison against a POSITION COUNTER (hpos/vpos), and the zero-delay
engine, having erased the delay, compares it one dot early.  These are the
"P2 cross-chip samplers."  This census enumerates every such site structurally:

  1. INTERFACE ENTRY — the cross-chip nets (io_ce, io_ab, io_db, io_rw) plus the
     write-latched control signals a $2001/$2007 write sets (rendering, bkg/spr
     enables): the signals whose arrival timing the delay island governs.
  2. COUNTER-COMPARATOR CONE — every node whose fan-in support includes a
     position-counter bit (hpos*/vpos*): the places the die asks "are we at dot
     N yet?".  A node that mixes BOTH an interface-controlled enable AND a
     counter bit is phase-sensitive: it is exactly where an early/late arrival
     changes a per-dot decision.
  3. VALIDATION — the 2C02 names its comparators (hpos_eq_339_and_rendering,
     _eq_1_and_rendering, ...), so the structural scan can be checked against
     the campaign's four measured bosses by name.

Ground truth: hpos_eq_339_and_rendering = dot-339; the hpos%8 shifter-reload
boundary = BGSerialIn; the vpos261/hpos338-339 window = even_odd.

Outputs: console report, JSON summary, two SVG figures.

Usage (bring your own Visual6502-style 2C02 files — not vendored here):
    python m6_interface_census.py --transdefs visual2c02-transdefs.js \
        --nodenames visual2c02-nodenames.js --outdir out/

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m6-phase.html.
"""
import argparse, json, os, re, sys
from collections import defaultdict, deque

RAIL = {"vcc", "pwr", "vdd", "vss", "gnd"}
# interface-controlled enables a $2001/$2007 write governs (arrival-timed by the delay island)
ENABLE_HINTS = ("rendering", "bkg_enable", "spr_enable", "bkg_en", "obj_en", "ren_en", "enable")
COUNTER_RE = re.compile(r"^(hpos|vpos)\d")


def parse_nodenames(path):
    n2i, i2n = {}, defaultdict(list)
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)",
                         open(path, encoding="utf-8", errors="replace").read()):
        nm, nid = m.group(1), int(m.group(2))
        n2i.setdefault(nm, nid); i2n[nid].append(nm)
    return n2i, i2n


def parse_transdefs(path):
    rows = []
    pat = re.compile(r"\[\s*'[^']+'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,")
    for m in pat.finditer(open(path, encoding="utf-8", errors="replace").read()):
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

    def circle(self, x, y, r, fill, opac=1.0):
        self.el.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}" fill-opacity="{opac}"/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False):
        fw = ' font-weight="700"' if bold else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}">{self.e(s)}</text>')

    def save(self, path):
        self.el.append("</svg>"); open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_funnel(path, counts):
    W, H = 780, 300
    s = Svg(W, H, "P2 funnel: interface -> counter cone -> phase-sensitive")
    rows = [("cross-chip interface entry nodes", counts["interface"], ACC2),
            ("nodes reached downstream (bounded BFS)", counts["reached"], MUT),
            ("counter-comparator nodes (touch hpos/vpos)", counts["comparators"], WARN),
            ("phase-sensitive (interface-fed + counter)", counts["phase"], BAD)]
    mx = max(c for _, c, _ in rows) or 1
    import math
    y = 62
    for name, c, col in rows:
        w = 10 + (W - 380) * math.log10(c + 1) / math.log10(mx + 1)
        s.rect(320, y, w, 34, col, 0.85)
        s.text(312, y + 22, name, FG, 12, "end")
        s.text(326 + w, y + 22, str(c), FG, 13, "start", bold=True)
        y += 56
    s.save(path)


def fig_anchors(path, anchors):
    W = 780
    H = 66 + 30 * len(anchors)
    s = Svg(W, H, "The four M6 bosses: does the scan re-find them?")
    y = 58
    for name, found, detail in anchors:
        col = ACC2 if found else BAD
        s.circle(74, y + 6, 6, col, 0.95)
        s.text(90, y + 10, name, FG, 13, "start", bold=True)
        s.text(330, y + 10, detail, MUT, 12)
        y += 30
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--depth", type=int, default=6)
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--top", type=int, default=25)
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    n2i, i2n = parse_nodenames(a.nodenames)
    trans = parse_transdefs(a.transdefs)
    pwr = {n2i[n] for n in ("vcc", "pwr", "vdd") if n in n2i}
    gnd = {n2i[n] for n in ("vss", "gnd") if n in n2i}
    rails = pwr | gnd

    def nm(nid): return "/".join(i2n.get(nid, [])[:2]) or f"n{nid}"

    # influence graph: gate g influences channel nodes c1,c2 (it gates current between them);
    # forward[x] = nodes x can influence (its channel endpoints when x is a gate).
    forward = defaultdict(set)
    support = defaultdict(set)    # support[y] = gates controlling current into y (fan-in)
    for g, c1, c2 in trans:
        if g in rails: continue
        for c in (c1, c2):
            if c not in rails:
                forward[g].add(c); support[c].add(g)
        # a channel also propagates value: c1<->c2 carry each other when g conducts
        if c1 not in rails and c2 not in rails:
            forward[c1].add(c2); forward[c2].add(c1)

    # counter bits
    counters = {nid for nm2, nid in n2i.items() if COUNTER_RE.match(nm2)}
    # comparator nodes: fan-in support (1-2 hop) includes a counter bit
    def support2(y):
        s1 = support.get(y, set())
        s2 = set(s1)
        for x in s1: s2 |= support.get(x, set())
        return s2
    comparators = {y for y in support if support2(y) & counters}

    # interface entry: cross-chip nets + write-latched enables
    interface = set()
    for nm2, nid in n2i.items():
        base = nm2.lstrip("/#~+").split("[")[0]
        if base in ("io_ce", "io_rw") or base.startswith("io_ab") or base.startswith("io_db"):
            interface.add(nid)
        if any(h in nm2 for h in ENABLE_HINTS) and "clear" not in nm2:
            interface.add(nid)
    interface -= rails

    # bounded BFS downstream from interface
    reached, frontier, depth = set(interface), set(interface), 0
    while frontier and depth < a.depth:
        nxt = set()
        for x in frontier:
            for y in forward.get(x, ()):
                if y not in reached and y not in rails:
                    reached.add(y); nxt.add(y)
        frontier = nxt; depth += 1

    phase = comparators & reached
    # rank phase-sensitive nodes by fan-in breadth (bigger cone = more decisions gated)
    ranked = sorted(phase, key=lambda y: -len(support2(y)))

    # validation: name-match the four bosses
    def find_named(substrs):
        hits = [nm2 for nm2 in n2i if all(s in nm2 for s in substrs)]
        return hits
    anchors = []
    for label, subs in [("dot-339 (hpos_eq_339_and_rendering)", ["339", "rendering"]),
                        ("even_odd (skip_dot on odd frames)", ["skip_dot"]),
                        ("BGSerialIn (hpos%8 reload boundary)", ["mod_8"]),
                        ("ALERead ($2007 io_ce interface)", ["io_ce"])]:
        hits = find_named(subs)
        inphase = [h for h in hits if n2i[h] in phase]
        incone = [h for h in hits if n2i[h] in comparators]
        status = bool(inphase or incone or (subs == ["io_ce"] and any(n2i[h] in interface for h in hits)))
        detail = (f"{len(hits)} named; {len(inphase)} phase-sensitive" if hits else "no name match")
        anchors.append((label, status, detail))

    counts = {"interface": len(interface), "reached": len(reached),
              "comparators": len(comparators), "phase": len(phase)}
    fig_funnel(os.path.join(a.outdir, "m6_funnel.svg"), counts)
    fig_anchors(os.path.join(a.outdir, "m6_anchors.svg"), anchors)

    summary = {
        "counters": len(counters), "interface_entry": len(interface),
        "reached_downstream": len(reached), "counter_comparators": len(comparators),
        "phase_sensitive": len(phase),
        "top_phase_sensitive": [{"node": y, "names": nm(y), "fanin": len(support2(y))} for y in ranked[:a.top]],
        "validation": [{"boss": l, "found": f, "detail": d} for l, f, d in anchors],
        "params": {"bfs_depth": a.depth},
    }
    jp = os.path.join(a.outdir, "m6_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[2C02] counters={len(counters)} interface-entry={len(interface)} "
          f"reached={len(reached)} counter-comparators={len(comparators)} phase-sensitive={len(phase)}")
    print(f"  top phase-sensitive interfaces (by fan-in cone):")
    for y in ranked[:min(a.top, 15)]:
        print(f"    fanin={len(support2(y)):>3}  {nm(y)}")
    print(f"  validation vs the four M6 bosses:")
    for l, f, d in anchors:
        print(f"    [{'FOUND' if f else 'miss '}] {l} -- {d}")
    print(f"  wrote {jp} + 2 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
