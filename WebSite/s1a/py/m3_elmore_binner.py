#!/usr/bin/env python3
"""m3_elmore_binner.py — every net gets a clock  (S1a toolbox, M3)

The engine settles instantly; the die does not.  Five hardware anchors from the
accuracy campaigns (24 hc cross-chip, 16 hc $2001->BG-reload, 16/18 hc rise/fall
on dot-339, ~24 hc $4015->DMC) measure what zero-delay settling erases.  This
script asks how far plain first-order physics can go toward *predicting* those
numbers from the die itself — Elmore delay over the geometry we already parse:

    tau(net) ~= ( R_driver + R_wire/2 ) * C_net

  - C_net    : the M2 census formula — polygon area x layer weight + gate W*L
               (m2_charge_wins.py, toolbox #1)
  - R_driver : 1/S from the M1 census — S = W/L of the strongest pull-down
               (fall) or the audit-derived depletion load (rise)
               (m1_device_census.py, toolbox #2)
  - R_wire   : sheet-resistance x squares, squares from each polygon's
               rectangle-equivalent L/W (area + perimeter -> quadratic)

Era priors (Mead & Conway 1980, ~3-6 um NMOS): sheet R poly ~25, diff ~15,
metal ~0.05 ohm/sq; R_on ~ 20 kohm / S.  Absolute farads are unknowable from
polygons alone, so results are reported in GATE UNITS (median driven net = 1.0)
— ranking and binning are scale-free, and the article regresses the scale
against the five measured anchors.

It also computes each net's rise:fall asymmetry.  Plain ratioed-NMOS nets must
rise ~4-7x SLOWER than they fall (weak depletion load vs strong pull-down); a
ratio near 1 is the signature of a symmetric totem / super-buffer stage.  The
dot-339 anchor measured 16/18 — rise FASTER — which plain NMOS cannot do: the
census quantifies exactly how impossible that is (the inversion-parity /
super-buffer prediction from the geometry consult, made falsifiable).

Outputs: console report, JSON summary, three SVG figures.

Usage (bring your own Visual6502-style files — not vendored here):
    python m3_elmore_binner.py --segdefs visual2c02-segdefs.js \
        --transdefs visual2c02-transdefs.js --nodenames visual2c02-nodenames.js \
        --label 2C02 --load-s 0.95 --outdir out/

Netlist note: use the CORRECTED data/system-def/ netlist (the raw upstream
Visual6502 2A03 dump dropped two real pull-downs, t13032b + t14634b); the raw
is inherently distorted for the APU-decode region.

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m3-delay.html.
"""
import argparse, json, math, os, re, sys
from collections import defaultdict

RAIL_PWR = {"vcc", "pwr", "vdd"}
RAIL_GND = {"vss", "gnd"}

# --- era priors (relative; one global scale absorbed by anchor regression) ---
LAYER_CW = {0: 0.03, 1: 0.10, 2: 0.10, 3: 0.10, 4: 0.10, 5: 0.04}   # C per unit^2
GATE_CW = 1.0
SHEET_R = {0: 0.05, 1: 15.0, 2: 15.0, 3: 15.0, 4: 15.0, 5: 25.0}    # ohm / square
RON_K = 20000.0                                                      # R_on ~= 20k / S


def parse_nodenames(path):
    names_to_id, id_to_names = {}, defaultdict(list)
    txt = open(path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)", txt):
        nm, nid = m.group(1), int(m.group(2))
        names_to_id.setdefault(nm, nid)
        id_to_names[nid].append(nm)
    return names_to_id, id_to_names


def parse_segdefs(path):
    """node -> per-layer [area, squares];  plus the '+' pull-up node set."""
    geom = defaultdict(lambda: defaultdict(lambda: [0.0, 0.0]))
    pullup = set()
    txt = open(path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'\s*,\s*(\d+)\s*,\s*([0-9,\s]+)\]", txt):
        node, layer = int(m.group(1)), int(m.group(3))
        if m.group(2) == "+":
            pullup.add(node)
        cs = [int(x) for x in m.group(4).replace(" ", "").split(",") if x]
        xs, ys = cs[0::2], cs[1::2]
        n = len(xs)
        if n < 3:
            continue
        a2 = sum(xs[i] * ys[(i + 1) % n] - xs[(i + 1) % n] * ys[i] for i in range(n))
        area = abs(a2) / 2.0
        per = sum(math.hypot(xs[(i + 1) % n] - xs[i], ys[(i + 1) % n] - ys[i]) for i in range(n))
        # rectangle-equivalent: L,W roots of x^2 - (P/2)x + A = 0  ->  squares = L/W
        h = per / 4.0
        d = h * h - area
        sq = 1.0 if d <= 0 else ((h + math.sqrt(d)) ** 2) / area
        g = geom[node][layer]
        g[0] += area
        g[1] += sq          # series-ish accumulation along the run
    return geom, pullup


def parse_transdefs(path):
    rows = []
    txt = open(path, encoding="utf-8", errors="replace").read()
    pat = re.compile(
        r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"
        r"\s*\[[^\]]*\]\s*,\s*\[([^\]]*)\]\s*(?:,\s*(true|false)\s*)?\]")
    for m in pat.finditer(txt):
        g = [float(x) for x in m.group(5).replace(" ", "").split(",") if x]
        ln = g[2] if len(g) >= 3 else 0
        area = g[4] if len(g) >= 5 else (g[0] * ln if len(g) >= 3 else 0)
        S = area / (ln * ln) if ln > 0 else 0.0
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), S, area))
    return rows


BG, CARD, BD, FG, MUT = "#100f15", "#1a1721", "#2e2634", "#dcd7e3", "#968ba1"
ACC, ACC2, BAD, WARN = "#c792ea", "#7ee0a8", "#ff6b6b", "#ffb454"
FONT = 'font-family="Segoe UI,Roboto,sans-serif"'


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h = w, h
        self.el = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" {FONT}>',
                   f'<rect width="{w}" height="{h}" fill="{BG}"/>',
                   f'<text x="{w/2}" y="26" fill="{FG}" font-size="16" font-weight="700" text-anchor="middle">{title}</text>']

    def line(self, x1, y1, x2, y2, stroke=BD, w=1):
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"/>')

    def rect(self, x, y, w, h, fill, opac=1.0, rx=2):
        self.el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" fill="{fill}" fill-opacity="{opac}" rx="{rx}"/>')

    def circle(self, x, y, r, fill, opac=1.0):
        self.el.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{r}" fill="{fill}" fill-opacity="{opac}"/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False, rotate=None):
        s = str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        fw = ' font-weight="700"' if bold else ""
        tr = f' transform="rotate({rotate} {x} {y})"' if rotate is not None else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}"{tr}>{s}</text>')

    def save(self, path):
        self.el.append("</svg>")
        open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_tau_hist(path, label, taus, bins_edges):
    W, H, L, R, T, B = 780, 380, 70, 30, 56, 70
    s = Svg(W, H, f"{label}: fall delay per driven net (gate units, log2 bins)")
    lgs = [math.log2(t) for t in taus if t > 0]
    lo, hi = math.floor(min(lgs)), math.ceil(max(lgs))
    nb = int((hi - lo) * 2)
    h = [0] * nb
    for v in lgs:
        h[min(nb - 1, max(0, int((v - lo) * 2)))] += 1
    mx = max(h) or 1
    bw = (W - L - R) / nb
    for i in range(nb):
        c = 2 ** (lo + (i + 0.5) / 2)
        col = MUT if c < bins_edges[0] else ACC2 if c < bins_edges[1] else WARN if c < bins_edges[2] else BAD
        bh = (H - T - B) * h[i] / mx
        s.rect(L + i * bw + 1, H - B - bh, bw - 2, bh, col, 0.85)
    for d in range(lo, hi + 1, 1):
        s.text(L + (d - lo) * 2 * bw, H - B + 16, f"{2**d:g}", MUT, 10.5, "middle")
    s.line(L, H - B, W - R, H - B, MUT)
    s.text((L + W - R) / 2, H - 20, "tau_fall (gate units; median driven net = 1)", FG, 12, "middle")
    s.rect(L + 8, T + 6, 12, 10, MUT, .85); s.text(L + 26, T + 15, f"< {bins_edges[0]:g} negligible", MUT, 11.5)
    s.rect(L + 178, T + 6, 12, 10, ACC2, .85); s.text(L + 196, T + 15, f"< {bins_edges[1]:g} ordinary", MUT, 11.5)
    s.rect(L + 330, T + 6, 12, 10, WARN, .85); s.text(L + 348, T + 15, f"< {bins_edges[2]:g} slow", MUT, 11.5)
    s.rect(L + 452, T + 6, 12, 10, BAD, .85); s.text(L + 470, T + 15, "beyond: delay-island candidates", FG, 11.5)
    s.save(path)


def fig_asym(path, label, ratios, anchor=18.0 / 16.0):
    W, H, L, R, T, B = 780, 380, 70, 30, 56, 70
    s = Svg(W, H, f"{label}: rise/fall asymmetry per net — and the impossible anchor")
    lgs = [math.log2(r) for r in ratios if r > 0]
    lo, hi = math.floor(min(lgs)), math.ceil(max(lgs))
    nb = max(1, int((hi - lo) * 2))
    h = [0] * nb
    for v in lgs:
        h[min(nb - 1, max(0, int((v - lo) * 2)))] += 1
    mx = max(h) or 1
    bw = (W - L - R) / nb
    for i in range(nb):
        c = lo + (i + 0.5) / 2
        col = ACC2 if abs(c) <= 0.6 else ACC     # near-symmetric = totem/super-buffer band
        bh = (H - T - B) * h[i] / mx
        s.rect(L + i * bw + 1, H - B - bh, bw - 2, bh, col, 0.85)
    xa = L + (math.log2(16.0 / 18.0) - lo) * 2 * bw    # dot-339: rise FASTER (16 vs 18)
    s.line(xa, T, xa, H - B, BAD, 2.5)
    s.text(xa + 6, T + 14, "dot-339 anchor: rise/fall = 16/18", BAD, 12, "start", bold=True)
    s.text(xa + 6, T + 32, "no plain net lives here -> odd inversion / super-buffer", BAD, 11)
    for d in range(lo, hi + 1):
        s.text(L + (d - lo) * 2 * bw, H - B + 16, f"{2**d:g}", MUT, 10.5, "middle")
    s.line(L, H - B, W - R, H - B, MUT)
    s.text((L + W - R) / 2, H - 20, "tau_rise / tau_fall (green band: symmetric totem stages)", FG, 12, "middle")
    s.save(path)


def fig_top(path, label, top):
    W = 780
    H = 66 + 26 * len(top)
    s = Svg(W, H, f"{label}: slowest driven nets (fall, gate units)")
    mx = top[0][0]
    y = 56
    for tau, nid, name in top:
        w = 8 + (W - 320) * tau / mx
        s.rect(250, y, w, 18, BAD if tau > 8 else WARN, 0.85)
        s.text(242, y + 14, name[:30], FG, 12, "end")
        s.text(256 + w, y + 14, f"{tau:.1f}", FG, 12, "start", bold=True)
        y += 26
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--segdefs", required=True)
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--label", default="die")
    ap.add_argument("--load-s", type=float, default=0.75,
                    help="depletion-load strength S (from the M1 audit: 2A03~0.58, 2C02~0.95)")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--top", type=int, default=18)
    ap.add_argument("--watch", default="",
                    help="comma list of node names to report individually")
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    names_to_id, id_to_names = parse_nodenames(a.nodenames)
    geom, pullup = parse_segdefs(a.segdefs)
    trans = parse_transdefs(a.transdefs)
    pwr = {names_to_id[n] for n in RAIL_PWR if n in names_to_id}
    gnd = {names_to_id[n] for n in RAIL_GND if n in names_to_id}
    rails = pwr | gnd

    def nm(nid):
        return "/".join(id_to_names.get(nid, [])[:2]) or f"n{nid}"

    # C_net (M2 formula) + R_wire (sheet x squares) + drive strengths (M1 logic)
    cnet = defaultdict(float)
    rwire = defaultdict(float)
    for node, per_layer in geom.items():
        for layer, (area, sq) in per_layer.items():
            cnet[node] += area * LAYER_CW.get(layer, 0.05)
            rwire[node] += SHEET_R.get(layer, 15.0) * sq
    s_dn = defaultdict(float)      # strongest direct pull-down per net
    s_up = defaultdict(float)      # strongest direct to-VCC driver per net
    chan = defaultdict(list)       # channel graph (signal-signal pass edges)
    for name, g, c1, c2, S, area in trans:
        if g in pwr or g in gnd:
            continue
        cnet[g] += area * GATE_CW
        if c1 in gnd or c2 in gnd:
            n = c2 if c1 in gnd else c1
            if n not in rails: s_dn[n] = max(s_dn[n], S)
        elif c1 in pwr or c2 in pwr:
            n = c2 if c1 in pwr else c1
            if n not in rails: s_up[n] = max(s_up[n], S)
        else:
            chan[c1].append(c2); chan[c2].append(c1)

    # drive depth: min pass-hops from any directly-driven net (BFS over channel edges)
    driven = set(s_dn) | set(pullup)
    depth = {n: 0 for n in driven}
    frontier = list(driven)
    while frontier:
        nxt = []
        for u in frontier:
            for v in chan.get(u, ()):
                if v not in depth and v not in rails:
                    depth[v] = depth[u] + 1
                    nxt.append(v)
        frontier = nxt

    # Elmore per driven net, in raw ohm x C-units; normalize to gate units later
    taus_fall, taus_rise, ratios = {}, {}, []
    for n in driven:
        if n in rails or cnet[n] <= 0:
            continue
        r_dn = RON_K / s_dn[n] if s_dn.get(n, 0) > 0 else None
        r_up = RON_K / s_up[n] if s_up.get(n, 0) > 0 else (RON_K / a.load_s if n in pullup else None)
        c = cnet[n]
        if r_dn: taus_fall[n] = (r_dn + rwire[n] / 2.0) * c
        if r_up: taus_rise[n] = (r_up + rwire[n] / 2.0) * c
        if r_dn and r_up:
            ratios.append(taus_rise[n] / taus_fall[n])

    med = sorted(taus_fall.values())[len(taus_fall) // 2]
    for d in (taus_fall, taus_rise):
        for k in d: d[k] /= med
    bins_edges = (0.5, 2.0, 8.0)
    binned = [0, 0, 0, 0]
    for t in taus_fall.values():
        binned[0 if t < 0.5 else 1 if t < 2 else 2 if t < 8 else 3] += 1
    top = sorted(((t, n, nm(n)) for n, t in taus_fall.items()), reverse=True)[:a.top]
    n_sym = sum(1 for r in ratios if 0.66 <= r <= 1.5)
    med_ratio = sorted(ratios)[len(ratios) // 2] if ratios else 0
    chain2 = sum(1 for v in depth.values() if v >= 2)

    lab = a.label
    fig_tau_hist(os.path.join(a.outdir, f"m3_{lab}_tau.svg"), lab, list(taus_fall.values()), bins_edges)
    fig_asym(os.path.join(a.outdir, f"m3_{lab}_asym.svg"), lab, ratios)
    fig_top(os.path.join(a.outdir, f"m3_{lab}_top.svg"), lab, top)

    watch = [w.strip() for w in a.watch.split(",") if w.strip()]
    watch_out = []
    for w in watch:
        nid = names_to_id.get(w)
        if nid is None:
            watch_out.append({"name": w, "found": False}); continue
        watch_out.append({"name": w, "found": True,
                          "tau_fall": round(taus_fall.get(nid, -1), 2),
                          "tau_rise": round(taus_rise.get(nid, -1), 2),
                          "depth": depth.get(nid, -1),
                          "C": round(cnet.get(nid, 0), 1), "R_wire": round(rwire.get(nid, 0), 1)})

    summary = {
        "label": lab, "driven_nets": len(taus_fall), "priors": {
            "sheet_R": SHEET_R, "layer_C": LAYER_CW, "Ron_over_S": RON_K, "load_S": a.load_s},
        "gate_unit_note": "tau normalized: median driven net = 1.0",
        "bins": {"edges_gate_units": bins_edges, "counts": {
            "negligible_lt0.5": binned[0], "ordinary_0.5-2": binned[1],
            "slow_2-8": binned[2], "island_gt8": binned[3]},
            "island_pct": round(100.0 * binned[3] / len(taus_fall), 2)},
        "asymmetry": {"nets_with_both_paths": len(ratios),
                      "median_rise_over_fall": round(med_ratio, 2),
                      "near_symmetric_0.66-1.5": n_sym,
                      "anchor_dot339_rise_over_fall": round(16.0 / 18.0, 3),
                      "nets_at_or_below_anchor": sum(1 for r in ratios if r <= 16.0 / 18.0)},
        "pass_chain": {"nets_depth_ge2": chain2,
                       "note": "chain nets need N(N+1)/2 correction — flagged, not solved (v1)"},
        "top_slowest_fall": [{"tau": round(t, 1), "node": n, "names": s} for t, n, s in top],
        "watch": watch_out,
    }
    jp = os.path.join(a.outdir, f"m3_{lab}_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[{lab}] driven nets={len(taus_fall)}  (load_S={a.load_s})")
    print(f"  bins (gate units): <0.5: {binned[0]}   0.5-2: {binned[1]}   2-8: {binned[2]}   >8: {binned[3]} "
          f"({summary['bins']['island_pct']}% island candidates)")
    print(f"  asymmetry: {len(ratios)} nets with both paths; median rise/fall = {med_ratio:.2f}; "
          f"near-symmetric (totem band) = {n_sym}; at/below dot-339's 16/18 = "
          f"{summary['asymmetry']['nets_at_or_below_anchor']}")
    print(f"  pass-chain nets (depth>=2, N^2-risk): {chain2}")
    print(f"  slowest driven nets (fall):")
    for t, n, s in top[:min(len(top), 12)]:
        print(f"    tau={t:7.1f}  {s}")
    for w in watch_out:
        if w.get("found"):
            print(f"  [watch] {w['name']}: tau_fall={w['tau_fall']} tau_rise={w['tau_rise']} "
                  f"depth={w['depth']} C={w['C']} Rw={w['R_wire']}")
        else:
            print(f"  [watch] {w['name']}: not found")
    print(f"  wrote {jp} + 3 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
