#!/usr/bin/env python3
"""m4_latch_scan.py — every latch on two dies  (S1a toolbox, M4)

The largest shim family in the accuracy campaigns — DL, DmcLatch, Dmc4015Abort,
FrameIrq, Dbl2007, OamDmaPpuBus, and (as the M2 retirement experiments proved)
the irreducible transient halves of OpenBus and OamBlankEdge — is one physical
phenomenon: a *transparent latch* whose closing edge races the data it is
holding, at a granularity the settle-to-quiescence engine cannot express.

Unlike strengths (M1: priors) or delays (M3: anchors), latches are PURE
TOPOLOGY.  This scan needs zero physical assumptions — four graph fingerprints
over connectivity the netlist states exactly:

  1. PASS-FED STORAGE  — a node with observers (it gates something), no direct
     rail driver, whose only channel connections are signal-to-signal pass
     devices: the dynamic latch cell.  1 feed = classic latch, >=2 = mux-fed
     (register-file style).
  2. CROSS-COUPLED PAIRS — node a gates a pull-down of node b and vice versa:
     the SR / regenerative pair.  With a pull-up = static latch; without =
     dynamic regenerative cell (OAM's famous no-pull-up cells).
  3. THE CLOSING-EDGE RACE (P1) — for each pass-fed cell, does the enable's
     fan-in cone intersect the data's fan-in cone?  Shared support means one
     trigger can close the gate AND change the data in the same settle wave —
     the exact race the campaigns kept stepping on.
  4. PASS-FEEDBACK — the cell's own value re-enters its data cone (loops
     through the pass network).

Ground truth: the campaigns hand-located these sites the hard way (idl* = DL,
dor* = the OpenBus glitch culprit, alua*/alub* = the ALU latches, spr_d7_int,
oam_write_disable).  The scan validates itself against them: a detector that
cannot re-find the known mines has no business proposing new ones.

Outputs: console report, JSON summary, three SVG figures.

Usage (bring your own Visual6502-style files — not vendored here):
    python m4_latch_scan.py --transdefs visual2a03-transdefs.js \
        --segdefs visual2a03-segdefs.js --nodenames visual2a03-nodenames.js \
        --label 2A03 --watch "idl0,dor0,alua0,alub0" --outdir out/

Netlist note: use the CORRECTED data/system-def/ netlist (the raw upstream
Visual6502 2A03 dump dropped two real pull-downs, t13032b + t14634b); the raw
is inherently distorted for the APU-decode region.

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m4-latch.html.
"""
import argparse, json, math, os, re, sys
from collections import defaultdict

RAIL_PWR = {"vcc", "pwr", "vdd"}
RAIL_GND = {"vss", "gnd"}
CONE_DEPTH = 2          # bounded fan-in depth for the race fingerprint (short causal paths = the P1 race)
CONE_CAP = 500          # max nodes per cone
CLK_FANOUT = 200        # nodes gating more devices than this are clock-class (excluded from shared support)


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
    rows = []
    txt = open(path, encoding="utf-8", errors="replace").read()
    pat = re.compile(r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,")
    for m in pat.finditer(txt):
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4))))
    return rows


BG, CARD, BD, FG, MUT = "#100f15", "#1a1721", "#2e2634", "#dcd7e3", "#968ba1"
ACC, ACC2, BAD, WARN = "#c792ea", "#7ee0a8", "#ff6b6b", "#ffb454"
FONT = 'font-family="Segoe UI,Roboto,sans-serif"'


class Svg:
    def __init__(self, w, h, title):
        self.w, self.h = w, h
        self.el = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" {FONT}>',
                   f'<rect width="{w}" height="{h}" fill="{BG}"/>',
                   f'<text x="{w/2}" y="26" fill="{FG}" font-size="16" font-weight="700" text-anchor="middle">{self.esc(title)}</text>']

    @staticmethod
    def esc(s):
        return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")

    def line(self, x1, y1, x2, y2, stroke=BD, w=1):
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"/>')

    def rect(self, x, y, w, h, fill, opac=1.0, rx=2):
        self.el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" fill="{fill}" fill-opacity="{opac}" rx="{rx}"/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False):
        fw = ' font-weight="700"' if bold else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}">{self.esc(s)}</text>')

    def save(self, path):
        self.el.append("</svg>")
        open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_classes(path, label, rows):
    W = 780
    H = 70 + 44 * len(rows)
    s = Svg(W, H, f"{label}: latch structures found by pure topology")
    mx = max(c for _, c, _ in rows) or 1
    y = 60
    for name, cnt, col in rows:
        w = 8 + (W - 360) * cnt / mx
        s.rect(300, y, w, 26, col, 0.85)
        s.text(292, y + 18, name, FG, 12.5, "end")
        s.text(306 + w, y + 18, f"{cnt:,}", FG, 13, "start", bold=True)
        y += 44
    s.save(path)


def fig_race(path, label, sizes):
    W, H, L, R, T, B = 780, 360, 70, 30, 56, 70
    s = Svg(W, H, f"{label}: closing-edge race fingerprint — shared enable/data support")
    bins = [0, 1, 2, 3, 5, 9, 17, 33, 10 ** 9]
    labels = ["0", "1", "2", "3-4", "5-8", "9-16", "17-32", "33+"]
    h = [0] * (len(bins) - 1)
    for v in sizes:
        for i in range(len(bins) - 1):
            if bins[i] <= v < bins[i + 1]:
                h[i] += 1; break
    mx = max(h) or 1
    bw = (W - L - R) / len(h)
    for i in range(len(h)):
        col = MUT if i == 0 else BAD if i <= 2 else WARN
        bh = (H - T - B) * h[i] / mx
        s.rect(L + i * bw + 4, H - B - bh, bw - 8, bh, col, 0.85)
        s.text(L + i * bw + bw / 2, H - B + 18, labels[i], MUT, 11, "middle")
        s.text(L + i * bw + bw / 2, H - B - bh - 6, f"{h[i]:,}", FG, 11.5, "middle")
    s.line(L, H - B, W - R, H - B, MUT)
    s.text((L + W - R) / 2, H - 22, "|enable-cone  ∩  data-cone|   (0 = clean latch; 1-4 = tightest races, red)", FG, 12, "middle")
    s.save(path)


def fig_valid(path, label, results):
    W = 780
    H = 70 + 34 * len(results)
    s = Svg(W, H, f"{label}: can the scanner re-find the campaign's known sites?")
    y = 58
    for name, status, detail in results:
        col = ACC2 if status == "found" else WARN if status == "partial" else BAD
        s.rect(60, y, 14, 14, col, 0.95, rx=7)
        s.text(84, y + 12, name, FG, 13, "start", bold=True)
        s.text(280, y + 12, detail, MUT, 12)
        y += 34
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--segdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--label", default="die")
    ap.add_argument("--watch", default="")
    ap.add_argument("--outdir", default=".")
    ap.add_argument("--top", type=int, default=15)
    ap.add_argument("--dump-nodes", default=None,
                    help="also write per-category node-id lists (for the die-layout viewer)")
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

    # per-node structure
    observers = defaultdict(int)         # node -> #devices it gates
    direct_drive = defaultdict(int)      # node -> #devices tying it to a rail
    pass_edges = defaultdict(list)       # node -> [(enable_gate, data_terminal)]
    chan = defaultdict(list)             # node -> [(gate, other)]  (channel adjacency, signal-signal)
    pull_low = defaultdict(set)          # gate g -> {nodes g pulls low}
    for name, g, c1, c2, in trans:
        if g in rails:                   # always-on tie / dead — not latch machinery
            continue
        observers[g] += 1
        if c1 in gnd or c2 in gnd:
            n = c2 if c1 in gnd else c1
            if n not in rails:
                direct_drive[n] += 1
                pull_low[g].add(n)
        elif c1 in pwr or c2 in pwr:
            n = c2 if c1 in pwr else c1
            if n not in rails:
                direct_drive[n] += 1
        else:
            pass_edges[c1].append((g, c2)); pass_edges[c2].append((g, c1))
            chan[c1].append((g, c2)); chan[c2].append((g, c1))

    # 1. storage cells: pure pass-fed (dynamic) + gated-drive (pass feed alongside refresh drivers)
    cells, gated = {}, {}
    for n, edges in pass_edges.items():
        if n in rails: continue
        if direct_drive.get(n, 0) > 0 or n in pullup:
            gated[n] = edges                    # pass feed + refresh source (driver or pull-up) — DL/ALU style
        elif observers.get(n, 0) > 0 or len(edges) >= 2:
            cells[n] = edges                    # pure dynamic: holds by charge alone (pcm/OAM style)
    single = {n: e for n, e in cells.items() if len(e) == 1}
    muxfed = {n: e for n, e in cells.items() if len(e) >= 2}

    # 2. cross-coupled pairs
    pairs_static, pairs_dyn = [], []
    seen = set()
    for a2 in pull_low:
        for b in pull_low[a2]:
            if b in pull_low and a2 in pull_low[b]:
                key = (min(a2, b), max(a2, b))
                if key in seen: continue
                seen.add(key)
                (pairs_static if (a2 in pullup or b in pullup) else pairs_dyn).append(key)

    # 3. the race fingerprint: bounded fan-in cones
    def fanin(start):
        out, frontier, depth = {start}, [start], 0
        while frontier and depth < CONE_DEPTH and len(out) < CONE_CAP:
            nxt = []
            for x in frontier:
                for (g, o) in chan.get(x, ()):
                    for p in (g, o):
                        if p not in out and p not in rails:
                            out.add(p); nxt.append(p)
                for g2 in (d for d in pull_low if x in pull_low[d]):
                    if g2 not in out and g2 not in rails:
                        out.add(g2); nxt.append(g2)
            frontier = nxt; depth += 1
        return out

    def fanin_excl(start, excl):
        # fan-in of `start` whose first hop may not step onto `excl` (kills the trivial
        # data<->cell pass edge); deeper re-entry of `excl` counts as genuine feedback
        out, frontier = {start}, []
        for (g, o) in chan.get(start, ()):
            for p in (g, o):
                if p != excl and p not in rails and p not in out:
                    out.add(p); frontier.append(p)
        for g2 in (d for d in pull_low if start in pull_low[d]):
            if g2 != excl and g2 not in rails and g2 not in out:
                out.add(g2); frontier.append(g2)
        depth = 1
        while frontier and depth < CONE_DEPTH and len(out) < CONE_CAP:
            nxt = []
            for x in frontier:
                for (g, o) in chan.get(x, ()):
                    for p in (g, o):
                        if p not in out and p not in rails:
                            out.add(p); nxt.append(p)
                for g2 in (d for d in pull_low if x in pull_low[d]):
                    if g2 not in out and g2 not in rails:
                        out.add(g2); nxt.append(g2)
            frontier = nxt; depth += 1
        return out

    clocky = {n for n, c in observers.items() if c > CLK_FANOUT}
    race_sizes, races, feedback = [], [], []
    for n, edges in cells.items():
        shared_all = set()
        fb = False
        for (en, data) in edges:
            fe = fanin(en)
            fd = fanin_excl(data, n)
            if n in fd: fb = True
            shared_all |= (fe & fd) - clocky - {n}
        race_sizes.append(len(shared_all))
        if shared_all: races.append((len(shared_all), n))
        if fb: feedback.append(n)
    races.sort()

    # 4. validate against the campaign's known sites
    watch = [w.strip() for w in a.watch.split(",") if w.strip()]
    valid = []
    for w in watch:
        nid = names_to_id.get(w)
        if nid is None:
            valid.append((w, "missed", "name not in nodenames")); continue
        if nid in cells:
            r = next((s for s, x in races if x == nid), None)
            valid.append((w, "found", f"pure pass-fed cell ({len(cells[nid])} feed); shared support {r if r is not None else 0}"))
        elif nid in gated:
            src = f"{direct_drive.get(nid, 0)} driver" + (" + pull-up" if nid in pullup else "")
            valid.append((w, "found", f"gated-drive latch ({len(gated[nid])} pass feed + {src})"))
        elif any(nid in k for k in pairs_static + pairs_dyn):
            valid.append((w, "found", "cross-coupled pair member"))
        else:
            valid.append((w, "missed", f"drive={direct_drive.get(nid,0)} pass={len(pass_edges.get(nid,[]))} obs={observers.get(nid,0)}"))

    lab = a.label
    fig_classes(os.path.join(a.outdir, f"m4_{lab}_classes.svg"), lab, [
        ("single-feed dynamic cell", len(single), ACC),
        ("mux-fed cell (register file)", len(muxfed), ACC2),
        ("gated-drive latch (feed + refresh)", len(gated), FG),
        ("cross-coupled static (SR + pull-up)", len(pairs_static), WARN),
        ("cross-coupled dynamic (no pull-up)", len(pairs_dyn), BAD),
        ("pass-feedback loops", len(feedback), MUT)])
    fig_race(os.path.join(a.outdir, f"m4_{lab}_race.svg"), lab, race_sizes)
    fig_valid(os.path.join(a.outdir, f"m4_{lab}_valid.svg"), lab, valid)

    tight = [(s, n) for s, n in races if s <= 4]
    summary = {
        "label": lab, "transistors": len(trans),
        "cells": {"pass_fed_total": len(cells), "single_feed": len(single), "mux_fed": len(muxfed),
                  "gated_drive": len(gated)},
        "cross_coupled": {"static": len(pairs_static), "dynamic": len(pairs_dyn)},
        "race": {"cells_with_shared_support": len(races), "tight_1to4": len(tight),
                 "no_shared_support": race_sizes.count(0)},
        "pass_feedback_cells": len(feedback),
        "tightest_races": [{"shared": s, "node": n, "names": nm(n)} for s, n in races[:a.top]],
        "validation": [{"name": w, "status": st, "detail": d} for w, st, d in valid],
        "params": {"cone_depth": CONE_DEPTH, "cone_cap": CONE_CAP, "clk_fanout_excl": CLK_FANOUT},
    }
    jp = os.path.join(a.outdir, f"m4_{lab}_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    # optional: per-category node-id lists for the die-layout viewer (m*-layout.html).
    # Same detection as above — this is a projection of it onto the layout, nothing new.
    if a.dump_nodes:
        tight_nodes = sorted({n for s, n in races if s <= 4})
        dump = {
            "chip": lab, "mechanism": "M4",
            "categories": {
                "pairs_dyn": {"label": "cross-coupled dynamic (no pull-up — OAM cells)",
                              "color": "#ff6b6b", "nodes": sorted({x for k in pairs_dyn for x in k})},
                "pairs_static": {"label": "cross-coupled static (SR + pull-up)",
                                 "color": "#ffb454", "nodes": sorted({x for k in pairs_static for x in k})},
                "gated": {"label": "gated-drive latch (feed + refresh)",
                          "color": "#7ee0a8", "nodes": sorted(gated.keys())},
                "single": {"label": "single-feed dynamic cell",
                           "color": "#c792ea", "nodes": sorted(single.keys())},
                "muxfed": {"label": "mux-fed cell (register file)",
                           "color": "#5ac8fa", "nodes": sorted(muxfed.keys())},
                "races": {"label": "tight closing-edge race (shared support 1-4)",
                          "color": "#ff2d55", "nodes": tight_nodes},
                "feedback": {"label": "pass-feedback loop",
                             "color": "#968ba1", "nodes": sorted(feedback)},
            },
            "validated": [{"name": w, "node": names_to_id.get(w), "status": st}
                          for w, st, d in valid if names_to_id.get(w) is not None],
        }
        # write a browser-loadable .js (global) so file:// preview works without a server;
        # a .json path still writes raw JSON (for tooling).
        payload = json.dumps(dump, ensure_ascii=False, separators=(",", ":"))
        if a.dump_nodes.endswith(".js"):
            payload = ("// AprVisual S1a — M4 highlight node-ids (generated by "
                       "m4_latch_scan.py --dump-nodes). Do not edit.\nwindow.DV_HIGHLIGHTS = "
                       + payload + ";\n")
        open(a.dump_nodes, "w", encoding="utf-8").write(payload)
        total = sum(len(c["nodes"]) for c in dump["categories"].values())
        print(f"  dumped {total} category node-ids -> {a.dump_nodes}")

    print(f"[{lab}] devices={len(trans)}  pass-fed cells={len(cells)} "
          f"(single {len(single)} / mux {len(muxfed)}) + gated-drive {len(gated)}  "
          f"cross-coupled: {len(pairs_static)} static + {len(pairs_dyn)} dynamic")
    print(f"  race fingerprint: {len(races)} cells share enable/data support "
          f"({len(tight)} tight 1-4); {race_sizes.count(0)} clean; pass-feedback {len(feedback)}")
    print(f"  tightest races:")
    for s, n in races[:min(a.top, 10)]:
        print(f"    shared={s:3d}  {nm(n)}")
    for w, st, d in valid:
        print(f"  [validate] {w}: {st.upper()} -- {d}")
    print(f"  wrote {jp} + 3 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
