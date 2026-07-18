#!/usr/bin/env python3
"""m2_charge_wins.py — who wins when nothing is driving?  (S1a toolbox, M2)

A switch-level engine resolves a *purely floating* transistor group — no GND
path, no VCC/pull-up, no external driver — by letting "the largest capacitance
win": the group takes the remembered state of its biggest node.  But the
engines in this family (Visual6502 -> MetalNES -> AprVisual.S1) never had real
capacitance, so they use a proxy:

    conn(node) = (#times node is a transistor channel terminal c1/c2)
               + (#times node is a transistor gate)          # S1: WireCore.cs

with ties broken by graph-walk order — a structural accident, not physics.

The die itself knows better.  segdefs carries every silicon polygon (layer +
vertex list) and transdefs carries every gate's W×L.  Real node capacitance is
dominated by gate oxide (~1.0 fF/um^2) plus wiring area (metal ~0.03, poly
~0.04, diffusion ~0.10 fF/um^2 — Mead & Conway era NMOS priors).  Only the
*ratios* matter for a verdict, so unit calibration is unnecessary:

    C_phys(node) = sum_polygons area(node, layer) * W[layer]
                 + sum_{t : gate(t)=node} gate_area(t) * W_gate

This script computes both scores for every node, then holds the election that
actually matters: for every *pass-gate pair* (a transistor whose two channel
terminals are both signal nodes — exactly the 2-node floating groups that
dominate the engine's <1% floating branch), it asks: does the engine's
connection-count verdict agree with the physical-capacitance verdict?

Outputs: a console report, a JSON summary, and three SVG figures.

Usage (bring your own Visual6502-style netlist files — not vendored here):
    python m2_charge_wins.py --segdefs visual2a03-segdefs.js \
        --transdefs visual2a03-transdefs.js --nodenames visual2a03-nodenames.js \
        --label 2A03 --outdir out/

Netlist note: use the CORRECTED data/system-def/ netlist (the raw upstream
Visual6502 2A03 dump dropped two real pull-downs, t13032b + t14634b); the raw
is inherently distorted for the APU-decode region.

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m2-charge-wins.html.
"""
import argparse, json, math, os, re, sys
from collections import defaultdict

# --- physical priors (relative fF/unit^2; only ratios matter) ----------------
LAYER_NAMES = {0: "metal", 1: "sw-diff", 2: "in-diode", 3: "gnd-diff", 4: "pwr-diff", 5: "poly"}
LAYER_W = {0: 0.03, 1: 0.10, 2: 0.10, 3: 0.10, 4: 0.10, 5: 0.04}
GATE_W = 1.0   # thin gate oxide dominates node capacitance

RAIL_PWR = {"vcc", "pwr", "vdd"}
RAIL_GND = {"vss", "gnd"}


def parse_nodenames(path):
    names_to_id, id_to_names = {}, defaultdict(list)
    txt = open(path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)", txt):
        nm, nid = m.group(1), int(m.group(2))
        names_to_id.setdefault(nm, nid)
        id_to_names[nid].append(nm)
    return names_to_id, id_to_names


def parse_segdefs(path):
    """node -> {layer: total shoelace area};  plus the '+' pull-up node set."""
    area = defaultdict(lambda: defaultdict(float))
    pullup = set()
    txt = open(path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'\s*,\s*(\d+)\s*,\s*([0-9,\s]+)\]", txt):
        node, pull, layer = int(m.group(1)), m.group(2), int(m.group(3))
        if pull == "+":
            pullup.add(node)
        cs = [int(x) for x in m.group(4).replace(" ", "").split(",") if x]
        xs, ys = cs[0::2], cs[1::2]
        if len(xs) < 3:
            continue
        a = sum(xs[i] * ys[(i + 1) % len(xs)] - xs[(i + 1) % len(xs)] * ys[i] for i in range(len(xs)))
        area[node][layer] += abs(a) / 2.0
    return area, pullup


def parse_transdefs(path):
    """[(name, gate, c1, c2, gate_area)] — gate_area from geom[4] (W*L)."""
    rows = []
    txt = open(path, encoding="utf-8", errors="replace").read()
    pat = re.compile(
        r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"
        r"\s*\[[^\]]*\]\s*,\s*\[([^\]]*)\]\s*(?:,\s*(true|false)\s*)?\]")
    for m in pat.finditer(txt):
        geom = [float(x) for x in m.group(5).replace(" ", "").split(",") if x]
        garea = geom[4] if len(geom) >= 5 else (geom[0] * geom[2] if len(geom) >= 3 else 0.0)
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), garea))
    return rows


def spearman(xs, ys):
    def rank(v):
        order = sorted(range(len(v)), key=lambda i: v[i])
        r = [0.0] * len(v)
        i = 0
        while i < len(order):
            j = i
            while j + 1 < len(order) and v[order[j + 1]] == v[order[i]]:
                j += 1
            avg = (i + j) / 2.0 + 1.0
            for k in range(i, j + 1):
                r[order[k]] = avg
            i = j + 1
        return r
    rx, ry = rank(xs), rank(ys)
    n = len(xs)
    mx, my = sum(rx) / n, sum(ry) / n
    num = sum((rx[i] - mx) * (ry[i] - my) for i in range(n))
    den = math.sqrt(sum((x - mx) ** 2 for x in rx) * sum((y - my) ** 2 for y in ry))
    return num / den if den else 0.0


# --- tiny SVG helpers (dark theme, matches the S1a site) ---------------------
BG, CARD, BD, FG, MUT = "#100f15", "#1a1721", "#2e2634", "#dcd7e3", "#968ba1"
ACC, ACC2, BAD, WARN = "#c792ea", "#7ee0a8", "#ff6b6b", "#ffb454"
FONT = 'font-family="Segoe UI,Roboto,sans-serif"'


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h = w, h
        self.el = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" {FONT}>',
                   f'<rect width="{w}" height="{h}" fill="{BG}"/>',
                   f'<text x="{w/2}" y="26" fill="{FG}" font-size="16" font-weight="700" text-anchor="middle">{title}</text>']

    def line(self, x1, y1, x2, y2, stroke=BD, w=1, dash=None):
        d = f' stroke-dasharray="{dash}"' if dash else ""
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"{d}/>')

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


def fig_scatter(path, label, pts, flip_ids):
    """pts: [(conn, c_phys, node_id)] for non-rail nodes with conn>0, c>0."""
    W, H, L, R, T, B = 760, 540, 70, 20, 50, 60
    s = Svg(W, H, f"{label}: engine proxy vs physical capacitance, one dot per node")
    xs = [math.log10(p[0]) for p in pts]
    ys = [math.log10(p[1]) for p in pts]
    x0, x1 = min(xs), max(xs)
    y0, y1 = min(ys), max(ys)
    def X(v): return L + (v - x0) / (x1 - x0) * (W - L - R)
    def Y(v): return H - B - (v - y0) / (y1 - y0) * (H - T - B)
    for d in range(int(math.floor(x0)), int(math.ceil(x1)) + 1):
        if x0 <= d <= x1:
            s.line(X(d), T, X(d), H - B, BD)
            s.text(X(d), H - B + 18, f"10^{d}", MUT, 11, "middle")
    for d in range(int(math.floor(y0)), int(math.ceil(y1)) + 1):
        if y0 <= d <= y1:
            s.line(L, Y(d), W - R, Y(d), BD)
            s.text(L - 8, Y(d) + 4, f"10^{d}", MUT, 11, "end")
    s.line(L, H - B, W - R, H - B, MUT); s.line(L, T, L, H - B, MUT)
    s.text((L + W - R) / 2, H - 14, "engine proxy: connection count (log)", FG, 13, "middle")
    s.text(16, (T + H - B) / 2, "physical capacitance C_phys (log, relative)", FG, 13, "middle", rotate=-90)
    for conn, c, nid in pts:
        if nid not in flip_ids:
            s.circle(X(math.log10(conn)), Y(math.log10(c)), 1.8, ACC2, 0.28)
    for conn, c, nid in pts:
        if nid in flip_ids:
            s.circle(X(math.log10(conn)), Y(math.log10(c)), 2.6, ACC, 0.85)
    s.circle(W - 235, T + 14, 3, ACC2, 0.6); s.text(W - 225, T + 18, "node (agreeing pairs)", MUT, 12)
    s.circle(W - 235, T + 34, 3.2, ACC, 0.95); s.text(W - 225, T + 38, "node in a verdict-flip pair", FG, 12)
    s.save(path)


def fig_verdicts(path, label, v, total):
    W, H = 760, 300
    s = Svg(W, H, f"{label}: pass-gate pair verdicts — engine proxy vs physics ({total} pairs)")
    rows = [("agree (same winner)", v["agree"], ACC2),
            ("FLIP (physics reverses the winner)", v["flip"], BAD),
            ("engine tie -> walk-order lottery, physics is decisive", v["tie_engine"], WARN),
            ("both tie (no geometry either)", v["both_tie"], MUT)]
    mx = max(c for _, c, _ in rows) or 1
    y = 70
    for name, cnt, col in rows:
        w = 8 + (W - 320) * cnt / mx
        s.rect(240, y, w, 30, col, 0.85)
        s.text(232, y + 20, name, FG, 12.5, "end")
        s.text(246 + w, y + 20, f"{cnt}  ({100.0*cnt/total:.1f}%)", FG, 13, "start", bold=True)
        y += 52
    s.save(path)


def fig_decisive(path, label, d_flip, d_agree):
    W, H, L, R, T, B = 760, 360, 70, 20, 50, 70
    s = Svg(W, H, f"{label}: how decisive is the physics verdict?  d = |Ca-Cb| / (Ca+Cb)")
    bins = 20
    def histo(ds):
        h = [0] * bins
        for d in ds:
            h[min(bins - 1, int(d * bins))] += 1
        n = float(len(ds)) or 1.0
        return [x / n for x in h]
    hf, ha = histo(d_flip), histo(d_agree)
    mx = max(hf + ha) or 1.0
    bw = (W - L - R) / bins
    def Y(v): return H - B - v / mx * (H - T - B)
    for i in range(bins):
        s.rect(L + i * bw + 1, Y(ha[i]), bw - 2, H - B - Y(ha[i]), ACC2, 0.30)
        s.rect(L + i * bw + bw * 0.25, Y(hf[i]), bw * 0.5, H - B - Y(hf[i]), BAD, 0.85)
    s.line(L, H - B, W - R, H - B, MUT)
    for i in range(0, bins + 1, 4):
        s.text(L + i * bw, H - B + 18, f"{i/bins:.1f}", MUT, 11, "middle")
    s.text((L + W - R) / 2, H - 30, "d  (0 = dead heat, 1 = one side utterly dominates)", FG, 13, "middle")
    s.rect(L + 10, T + 8, 14, 10, ACC2, 0.30); s.text(L + 30, T + 17, "agreeing pairs (fraction)", MUT, 12)
    s.rect(L + 10, T + 28, 14, 10, BAD, 0.85); s.text(L + 30, T + 37, "FLIPPED pairs (fraction)", FG, 12)
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--segdefs", required=True)
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--label", default="die")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--top", type=int, default=20)
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    names_to_id, id_to_names = parse_nodenames(a.nodenames)
    area, pullup = parse_segdefs(a.segdefs)
    trans = parse_transdefs(a.transdefs)
    pwr = {names_to_id[n] for n in RAIL_PWR if n in names_to_id}
    gnd = {names_to_id[n] for n in RAIL_GND if n in names_to_id}
    rails = pwr | gnd

    # engine proxy + physical capacitance
    conn = defaultdict(int)
    cphys = defaultdict(float)
    for node, per_layer in area.items():
        cphys[node] += sum(v * LAYER_W.get(l, 0.05) for l, v in per_layer.items())
    always_on = always_off = 0
    pass_pairs = {}
    for name, gate, c1, c2, garea in trans:
        conn[gate] += 1; conn[c1] += 1; conn[c2] += 1
        cphys[gate] += garea * GATE_W
        if gate in pwr:
            always_on += 1; continue
        if gate in gnd:
            always_off += 1; continue
        if c1 == c2 or c1 in rails or c2 in rails:
            continue
        key = (min(c1, c2), max(c1, c2))
        pass_pairs[key] = pass_pairs.get(key, 0) + 1

    # the election
    verdicts = {"agree": 0, "flip": 0, "tie_engine": 0, "both_tie": 0}
    flips, ties = [], []
    flip_ids = set()
    for (x, y), ngates in pass_pairs.items():
        cx, cy = conn[x], conn[y]
        px, py = cphys[x], cphys[y]
        e = 0 if cx == cy else (-1 if cx > cy else 1)     # engine winner: -1=x, 1=y, 0=tie
        rel = abs(px - py) / (px + py) if (px + py) else 0.0
        p = 0 if rel < 1e-9 else (-1 if px > py else 1)   # physics winner
        if e == 0 and p == 0:
            verdicts["both_tie"] += 1
        elif e == 0:
            verdicts["tie_engine"] += 1
            ties.append((rel, x, y, ngates))
        elif p == 0 or e == p:
            verdicts["agree"] += 1
        else:
            verdicts["flip"] += 1
            flips.append((rel, x, y, ngates))
            flip_ids.add(x); flip_ids.add(y)
    total = sum(verdicts.values()) or 1
    flips.sort(reverse=True); ties.sort(reverse=True)

    pts = [(conn[n], cphys[n], n) for n in set(conn) | set(cphys)
           if n not in rails and conn[n] > 0 and cphys[n] > 0]
    rho = spearman([math.log10(p[0]) for p in pts], [math.log10(p[1]) for p in pts])

    def nm(nid):
        return "/".join(id_to_names.get(nid, [])[:2]) or f"n{nid}"

    lab = a.label
    fig_scatter(os.path.join(a.outdir, f"m2_{lab}_scatter.svg"), lab, pts, flip_ids)
    fig_verdicts(os.path.join(a.outdir, f"m2_{lab}_verdicts.svg"), lab, verdicts, total)
    # decisiveness of the agreeing pairs, for the histogram's comparison series
    d_agree = []
    for (x, y), _ in pass_pairs.items():
        cx, cy = conn[x], conn[y]
        px, py = cphys[x], cphys[y]
        if cx != cy and (px + py) and abs(px - py) / (px + py) >= 1e-9:
            e = -1 if cx > cy else 1
            p = -1 if px > py else 1
            if e == p:
                d_agree.append(abs(px - py) / (px + py))
    fig_decisive(os.path.join(a.outdir, f"m2_{lab}_decisive.svg"), lab, [f[0] for f in flips], d_agree)

    top_flips = [{"a": x, "a_names": nm(x), "b": y, "b_names": nm(y),
                  "conn": [conn[x], conn[y]], "c_phys": [round(cphys[x], 1), round(cphys[y], 1)],
                  "decisiveness": round(rel, 3), "parallel_gates": ng}
                 for rel, x, y, ng in flips[:a.top]]
    summary = {
        "label": lab, "nodes_seen": len(pts), "transistors": len(trans),
        "always_on_gate_pwr": always_on, "always_off_gate_gnd": always_off,
        "pullup_nodes": len(pullup), "pass_gate_pairs": total, "verdicts": verdicts,
        "flip_rate_pct": round(100.0 * verdicts["flip"] / total, 2),
        "lottery_rate_pct": round(100.0 * verdicts["tie_engine"] / total, 2),
        "spearman_log_conn_vs_log_cphys": round(rho, 4),
        "layer_weights": {LAYER_NAMES[k]: v for k, v in LAYER_W.items()}, "gate_weight": GATE_W,
        "top_flips": top_flips,
        "top_engine_ties": [{"a": x, "a_names": nm(x), "b": y, "b_names": nm(y),
                             "decisiveness": round(rel, 3)} for rel, x, y, ng in ties[:a.top]],
    }
    jp = os.path.join(a.outdir, f"m2_{lab}_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[{lab}] transistors={len(trans)}  nodes(plotted)={len(pts)}  pass-gate pairs={total}")
    print(f"  verdicts: agree={verdicts['agree']} ({100.0*verdicts['agree']/total:.1f}%)  "
          f"FLIP={verdicts['flip']} ({100.0*verdicts['flip']/total:.1f}%)  "
          f"engine-tie={verdicts['tie_engine']} ({100.0*verdicts['tie_engine']/total:.1f}%)  "
          f"both-tie={verdicts['both_tie']}")
    print(f"  spearman(log conn, log C_phys) = {rho:.4f}")
    print(f"  top flips (physics most decisive first):")
    for rel, x, y, ng in flips[:a.top]:
        w = x if cphys[x] > cphys[y] else y
        l = y if w == x else x
        print(f"    d={rel:.3f}  physics-winner {nm(w)} (conn={conn[w]}, C={cphys[w]:.0f})  "
              f"beats engine-winner {nm(l)} (conn={conn[l]}, C={cphys[l]:.0f})  gates={ng}")
    print(f"  wrote {jp} + 3 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
