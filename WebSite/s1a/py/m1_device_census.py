#!/usr/bin/env python3
"""m1_device_census.py — the die's strength vocabulary  (S1a toolbox, M1)

A switch-level engine treats every transistor as the same ideal switch, and
resolves every contested group with a fixed priority: a path to GND always
wins.  Real NMOS is *ratioed* logic — every verdict is a fight between
conductances, and conductance is geometry: S = W/L, printed in plain sight
in the netlist's transdefs geometry column [w1, w2, length, #segs, W*L].

This census reads that column (which the engines in this family always
skipped) and asks four questions:

  1. CLASSES  — what kinds of device does the die use, and how many of each?
                (pull-down / to-VCC / pass gate / always-on tie / dead)
  2. VOCABULARY — how many *distinct strengths* exist?  If a handful of W/L
                values cover ~all devices, a small MOSSIM-II-style strength
                lattice is enough for mechanism M1 — no analog solver needed.
  3. THE 4:1 AUDIT — ratioed-NMOS design law: an inverter's pull-down must
                out-drive its depletion load ~4:1.  The extraction folded the
                loads into segdefs '+' flags (no load geometry survives!), so
                we audit from the other side: pull-downs on '+' nodes should
                be systematically stronger than pass gates — and their modal
                strength / 4 is the die's own estimate of the missing load
                strength (the one constant M1 must otherwise calibrate).
  4. FIGHT SITES — where can two *driven* paths oppose (a to-VCC device and a
                to-GND device on the same node, both signal-gated)?  These are
                the candidate ratioed fights the GND-always-wins LUT gets
                wrong by fiat — the LXA-magic family's home turf.

Outputs: console report, JSON summary, three SVG figures.

Usage (bring your own Visual6502-style files — not vendored here):
    python m1_device_census.py --transdefs visual2a03-transdefs.js \
        --segdefs visual2a03-segdefs.js --nodenames visual2a03-nodenames.js \
        --label 2A03 --outdir out/

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m1-strength.html.
"""
import argparse, json, math, os, re, sys
from collections import defaultdict, Counter

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


def parse_pullups(segdefs_path):
    pullup = set()
    txt = open(segdefs_path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'", txt):
        if m.group(2) == "+":
            pullup.add(int(m.group(1)))
    return pullup


def parse_transdefs(path):
    """[(name, gate, c1, c2, w1, w2, length, area)]"""
    rows = []
    txt = open(path, encoding="utf-8", errors="replace").read()
    pat = re.compile(
        r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"
        r"\s*\[[^\]]*\]\s*,\s*\[([^\]]*)\]\s*(?:,\s*(true|false)\s*)?\]")
    for m in pat.finditer(txt):
        g = [float(x) for x in m.group(5).replace(" ", "").split(",") if x]
        w1, w2, ln = (g[0], g[1], g[2]) if len(g) >= 3 else (0, 0, 0)
        area = g[4] if len(g) >= 5 else w1 * ln
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), w1, w2, ln, area))
    return rows


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

    def line(self, x1, y1, x2, y2, stroke=BD, w=1):
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"/>')

    def rect(self, x, y, w, h, fill, opac=1.0, rx=2):
        self.el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" fill="{fill}" fill-opacity="{opac}" rx="{rx}"/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False, rotate=None):
        s = str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
        fw = ' font-weight="700"' if bold else ""
        tr = f' transform="rotate({rotate} {x} {y})"' if rotate is not None else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}"{tr}>{s}</text>')

    def save(self, path):
        self.el.append("</svg>")
        open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_strength_hist(path, label, by_class):
    """log2-binned S=W/L histograms, one row of bars per device class."""
    W, H, L, R, T = 780, 430, 170, 30, 56
    s = Svg(W, H, f"{label}: drive strength S = W/L by device class (log2 bins)")
    classes = [("pulldown", BAD), ("to-vcc", WARN), ("pass", ACC2)]
    all_s = [v for k, _ in classes for v in by_class.get(k, []) if v > 0]
    lo = math.floor(min(math.log2(v) for v in all_s))
    hi = math.ceil(max(math.log2(v) for v in all_s))
    nb = int(hi - lo)
    bw = (W - L - R) / nb
    rowH = (H - T - 40) / len(classes)
    for r, (cls, col) in enumerate(classes):
        vals = by_class.get(cls, [])
        h = [0] * nb
        for v in vals:
            if v > 0:
                h[min(nb - 1, max(0, int(math.log2(v) - lo)))] += 1
        mx = max(h) or 1
        y0 = T + r * rowH + rowH - 6
        for i in range(nb):
            bh = (rowH - 26) * h[i] / mx
            s.rect(L + i * bw + 1, y0 - bh, bw - 2, bh, col, 0.8)
        s.text(L - 10, y0 - rowH / 2 + 12, f"{cls} ({len(vals)})", FG, 12.5, "end", bold=True)
        s.line(L, y0, W - R, y0, MUT)
    for i in range(0, nb + 1, 1):
        s.text(L + i * bw, H - 16, f"2^{int(lo + i)}", MUT, 10.5, "middle")
    s.text((L + W - R) / 2, H - 2, "S = W/L (die units)", FG, 12, "middle")
    s.save(path)


def fig_vocab(path, label, strength_counter, total):
    """Pareto: top strength values and their cumulative coverage."""
    W, H, L, R, T, B = 780, 380, 70, 60, 56, 70
    s = Svg(W, H, f"{label}: the strength vocabulary — top W/L values, cumulative coverage")
    top = strength_counter.most_common(14)
    bw = (W - L - R) / len(top)
    mx = top[0][1]
    cum = 0
    for i, (val, cnt) in enumerate(top):
        bh = (H - T - B) * cnt / mx
        s.rect(L + i * bw + 3, H - B - bh, bw - 6, bh, ACC, 0.8)
        s.text(L + i * bw + bw / 2, H - B + 16, f"{val:g}", MUT, 10.5, "middle")
        cum += cnt
        y = H - B - (H - T - B) * (cum / total)
        s.rect(L + i * bw + bw / 2 - 2, y - 2, 4, 4, ACC2, 1.0)
        if i in (0, 4, 9, len(top) - 1):
            s.text(L + i * bw + bw / 2, y - 8, f"{100.0*cum/total:.0f}%", ACC2, 11, "middle", bold=True)
    s.line(L, H - B, W - R, H - B, MUT)
    s.text((L + W - R) / 2, H - 20, "S = W/L value (rank order)", FG, 12, "middle")
    s.text(L + 6, T + 12, "bars: device count · dots: cumulative %", MUT, 11.5)
    s.save(path)


def fig_fights(path, label, ratios):
    """Histogram of log2(S_up / S_down) at driver-vs-driver fight sites."""
    W, H, L, R, T, B = 780, 360, 70, 30, 56, 70
    s = Svg(W, H, f"{label}: driver-vs-driver fight sites — up-strength : down-strength")
    if not ratios:
        s.text(W / 2, H / 2, "no fight sites found", MUT, 14, "middle")
        s.save(path); return
    lgs = [math.log2(r) for r in ratios]
    lo, hi = math.floor(min(lgs)), math.ceil(max(lgs))
    nb = max(1, int((hi - lo) * 2))          # half-octave bins
    h = [0] * nb
    for v in lgs:
        h[min(nb - 1, max(0, int((v - lo) * 2)))] += 1
    mx = max(h) or 1
    bw = (W - L - R) / nb
    for i in range(nb):
        c = lo + (i + 0.5) / 2
        col = BAD if abs(c) <= 1 else ACC2   # within 2x = a real fight
        bh = (H - T - B) * h[i] / mx
        s.rect(L + i * bw + 1, H - B - bh, bw - 2, bh, col, 0.85)
    x_eq = L + (0 - lo) * 2 * bw
    s.line(x_eq, T, x_eq, H - B, WARN, 2)
    s.text(x_eq, T - 4, "1:1", WARN, 11.5, "middle", bold=True)
    for d in range(lo, hi + 1):
        s.text(L + (d - lo) * 2 * bw, H - B + 16, f"{2**d if d >= 0 else f'1/{2**-d}'}", MUT, 10.5, "middle")
    s.line(L, H - B, W - R, H - B, MUT)
    s.text((L + W - R) / 2, H - 20, "S_up / S_down (red = within 2x — GND-wins is a coin-toss call here)", FG, 12, "middle")
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--segdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--label", default="die")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--top", type=int, default=20)
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    names_to_id, id_to_names = parse_nodenames(a.nodenames)
    pullup = parse_pullups(a.segdefs)
    trans = parse_transdefs(a.transdefs)
    pwr = {names_to_id[n] for n in RAIL_PWR if n in names_to_id}
    gnd = {names_to_id[n] for n in RAIL_GND if n in names_to_id}
    rails = pwr | gnd

    def nm(nid):
        return "/".join(id_to_names.get(nid, [])[:2]) or f"n{nid}"

    # 1+2: classify and build the strength vocabulary
    by_class = defaultdict(list)          # class -> [S]
    vocab = Counter()                     # S value -> device count (signal-gated only)
    up_at = defaultdict(list)             # node -> [S] via to-VCC device (signal-gated)
    down_at = defaultdict(list)           # node -> [S] via to-GND device (signal-gated)
    counts = Counter()
    for name, g, c1, c2, w1, w2, ln, area in trans:
        S = (area / (ln * ln)) if ln > 0 else 0.0     # = W_eff/L (area = W*L)
        if g in pwr:   counts["always-on"] += 1; continue
        if g in gnd:   counts["always-off"] += 1; continue
        vocab[round(S, 2)] += 1
        if c1 in gnd or c2 in gnd:
            counts["pulldown"] += 1; by_class["pulldown"].append(S)
            n = c2 if c1 in gnd else c1
            if n not in rails: down_at[n].append(S)
        elif c1 in pwr or c2 in pwr:
            counts["to-vcc"] += 1; by_class["to-vcc"].append(S)
            n = c2 if c1 in pwr else c1
            if n not in rails: up_at[n].append(S)
        else:
            counts["pass"] += 1; by_class["pass"].append(S)

    total_sig = sum(vocab.values())
    top_vocab = vocab.most_common(14)
    cov = 0; cov_at = {}
    for i, (v, c) in enumerate(vocab.most_common()):
        cov += c
        if i + 1 in (5, 10, 14): cov_at[i + 1] = round(100.0 * cov / total_sig, 1)
    # the lattice view: half-octave strength classes (what a MOSSIM-II-style LUT actually needs)
    lattice = Counter()
    for v, c in vocab.items():
        if v > 0: lattice[round(math.log2(v) * 2)] += c
    lat_cov = 0; lat_at = {}
    for i, (_, c) in enumerate(lattice.most_common()):
        lat_cov += c
        if i + 1 in (4, 6, 8): lat_at[i + 1] = round(100.0 * lat_cov / total_sig, 1)

    # 3: the 4:1 audit — direct pull-downs on '+' (load-flagged) nodes
    pd_on_pullup = [max(v) for n, v in down_at.items() if n in pullup]
    pass_S = by_class["pass"]
    def median(v): v = sorted(v); return v[len(v)//2] if v else 0
    modal_pd = Counter(round(x, 2) for x in pd_on_pullup).most_common(1)[0] if pd_on_pullup else (0, 0)
    implied_load = modal_pd[0] / 4.0

    # 4: driver-vs-driver fight sites
    fights = []
    for n in set(up_at) & set(down_at):
        su, sd = max(up_at[n]), max(down_at[n])
        if su > 0 and sd > 0:
            fights.append((su / sd, n, su, sd, len(up_at[n]), len(down_at[n])))
    fights.sort(key=lambda f: abs(math.log2(f[0])))
    close = [f for f in fights if 0.5 <= f[0] <= 2.0]

    lab = a.label
    fig_strength_hist(os.path.join(a.outdir, f"m1_{lab}_classes.svg"), lab, by_class)
    fig_vocab(os.path.join(a.outdir, f"m1_{lab}_vocab.svg"), lab, vocab, total_sig)
    fig_fights(os.path.join(a.outdir, f"m1_{lab}_fights.svg"), lab, [f[0] for f in fights])

    summary = {
        "label": lab, "transistors": len(trans), "classes": dict(counts),
        "signal_gated": total_sig,
        "distinct_strengths": len(vocab),
        "vocab_top": [{"S": v, "count": c} for v, c in top_vocab],
        "vocab_coverage_pct": cov_at,
        "halfoctave_lattice": {"classes": len(lattice), "coverage_pct": lat_at},
        "strength_stats": {cls: {"n": len(v), "median": round(median(v), 2),
                                 "min": round(min(v), 2), "max": round(max(v), 2)}
                           for cls, v in by_class.items() if v},
        "audit_4to1": {
            "pullup_nodes_with_direct_pulldown": len(pd_on_pullup),
            "modal_pulldown_S": modal_pd[0], "modal_count": modal_pd[1],
            "median_pulldown_on_pullup_S": round(median(pd_on_pullup), 2),
            "median_pass_S": round(median(pass_S), 2),
            "separation_x": round(median(pd_on_pullup) / median(pass_S), 2) if pass_S else None,
            "implied_depletion_load_S": round(implied_load, 3),
        },
        "fight_sites": {"total": len(fights), "within_2x": len(close),
                        "top_closest": [{"node": n, "names": nm(n), "S_up": round(su, 2), "S_dn": round(sd, 2),
                                         "ratio": round(r, 2), "n_up": nu, "n_dn": nd}
                                        for r, n, su, sd, nu, nd in fights[:a.top]]},
    }
    jp = os.path.join(a.outdir, f"m1_{lab}_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[{lab}] transistors={len(trans)}  classes: " +
          "  ".join(f"{k}={v}" for k, v in counts.most_common()))
    print(f"  strength vocabulary: {len(vocab)} distinct S values; "
          f"top-5 cover {cov_at.get(5,0)}%  top-10 {cov_at.get(10,0)}%  top-14 {cov_at.get(14,0)}%")
    print(f"  half-octave lattice: {len(lattice)} classes; top-4 cover {lat_at.get(4,0)}%  "
          f"top-6 {lat_at.get(6,0)}%  top-8 {lat_at.get(8,0)}%")
    print(f"  4:1 audit: {len(pd_on_pullup)} '+'-nodes with direct pulldowns; "
          f"modal pulldown S={modal_pd[0]} (x{modal_pd[1]}); median {median(pd_on_pullup):.2f} "
          f"vs pass median {median(pass_S):.2f} (separation {summary['audit_4to1']['separation_x']}x)")
    print(f"  implied depletion-load strength (modal/4) = {implied_load:.3f}")
    print(f"  fight sites (driver-vs-driver): {len(fights)} total, {len(close)} within 2x:")
    for r, n, su, sd, nu, nd in fights[:a.top]:
        print(f"    ratio={r:5.2f}  {nm(n)}  S_up={su:.2f}({nu} dev)  S_dn={sd:.2f}({nd} dev)")
    print(f"  wrote {jp} + 3 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
