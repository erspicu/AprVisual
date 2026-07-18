#!/usr/bin/env python3
"""m3_inversion_parity.py -- test the falsifiable 16/18 prediction  (S1a toolbox, M3 follow-up)

The M3 Elmore study left a falsifiable prediction. The dot-339 campaign MEASURED
rendering-enable propagation at 16 hc rise / 18 hc fall -- rise FASTER than fall.
Ratioed NMOS says that is backwards: the weak depletion pull-up charges slowly, so
a plain net rises 4-7x SLOWER than it falls (both dies' medians, 6.4x/3.9x, confirm).
Only ~0.45% of nets can rise as fast as they fall.

So the measured "16/18" cannot be the raw behaviour of the observed node -- unless
the observation point sits an ODD number of logic inversions from the switching
source. Across an odd inversion count, what looks like the observed node's "rise"
is really the source's "fall" (inverted), so the asymmetry flips: an odd parity
turns a genuine 18-rise/16-fall (slow-rise NMOS) into the measured 16-rise/18-fall.

This script tests that prediction structurally. It builds the inverting-drive graph
(node X gates a pull-down of node Y => X->Y is one inversion) and computes the
shortest inverting-path PARITY between a switching source and an observation node.

HONEST RESULT (measured 2026-07-18): the shortest-inverting-path parity is NOT a
reliable test -- it is source/observe-dependent. rendering_1 -> hpos_eq_339... gives
13 inversions (ODD, would "confirm"); bkg_enable -> the same node gives 4 (EVEN,
would "refute"); bkg_enable -> rendering_1 gives 6 (EVEN). Because the switch-level
graph has many short cross-paths, the graph-shortest path is not the signal path, so
its parity does not settle the question. --sweep reports the spread and an honest
"inconclusive" verdict. A real answer needs dynamic signal-path tracing (which stage
actually toggles when), not a static shortest-path proxy -- a documented method limit.

Usage (bring your own Visual6502-style 2C02 files -- not vendored here):
    python m3_inversion_parity.py --transdefs visual2c02-transdefs.js \
        --nodenames visual2c02-nodenames.js \
        --source rendering_1 --observe hpos_eq_339_and_rendering

Stdlib only. Part of the AprVisual S1a study -- see s1a.html / m3-delay.html.
"""
import argparse, json, os, re, sys
from collections import defaultdict, deque

RAIL = {"vcc", "pwr", "vdd", "vss", "gnd"}


def parse_nodenames(path):
    n2i, i2n = {}, defaultdict(list)
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)",
                         open(path, encoding="utf-8", errors="replace").read()):
        nm, nid = m.group(1), int(m.group(2))
        n2i.setdefault(nm, nid); i2n[nid].append(nm)
    return n2i, i2n


def parse_transdefs(path):
    rows = []
    for m in re.finditer(r"\[\s*'[^']+'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,",
                         open(path, encoding="utf-8", errors="replace").read()):
        rows.append((int(m.group(1)), int(m.group(2)), int(m.group(3))))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--transdefs", required=True)
    ap.add_argument("--nodenames", required=True)
    ap.add_argument("--source", default="rendering_1")
    ap.add_argument("--observe", default="hpos_eq_339_and_rendering")
    ap.add_argument("--sweep", action="store_true",
                    help="try several plausible dot-339 endpoint pairs and report the parity spread + honest verdict")
    ap.add_argument("--outdir", default=".")
    a = ap.parse_args()

    n2i, i2n = parse_nodenames(a.nodenames)
    trans = parse_transdefs(a.transdefs)
    gnd = {n2i[n] for n in ("vss", "gnd") if n in n2i}
    pwr = {n2i[n] for n in ("vcc", "pwr", "vdd") if n in n2i}
    rails = gnd | pwr

    def nm(nid): return "/".join(i2n.get(nid, [])[:2]) or f"n{nid}"

    # inverting-drive edges: gate g pulls node y LOW (g high -> y low) = one inversion.
    # pass edges (both channel ends are signals) carry the value WITHOUT inverting.
    inv_adj = defaultdict(list)     # g -> [y]  (inverting)
    pass_adj = defaultdict(list)    # x -> [y]  (non-inverting, through a pass channel)
    for g, c1, c2 in trans:
        if g in rails:              # always-on / dead: a connection, treat as pass (non-inverting)
            if c1 not in rails and c2 not in rails:
                pass_adj[c1].append(c2); pass_adj[c2].append(c1)
            continue
        if c1 in gnd or c2 in gnd:
            y = c2 if c1 in gnd else c1
            if y not in rails:
                inv_adj[g].append(y)         # g -> y is inverting
        elif c1 in pwr or c2 in pwr:
            pass                              # pull-up load: not a signal-driven inversion
        else:
            pass_adj[c1].append(c2); pass_adj[c2].append(c1)

    def shortest_inversions(src, obs):
        INF = 10 ** 9
        best = {src: 0}; dq = deque([src])
        while dq:
            x = dq.popleft(); d = best[x]
            for y in pass_adj.get(x, ()):
                if d < best.get(y, INF): best[y] = d; dq.appendleft(y)
            for y in inv_adj.get(x, ()):
                if d + 1 < best.get(y, INF): best[y] = d + 1; dq.append(y)
        return best.get(obs)

    if a.sweep:
        pairs = [("rendering_1", "hpos_eq_339_and_rendering"),
                 ("bkg_enable", "hpos_eq_339_and_rendering"),
                 ("spr_enable", "hpos_eq_339_and_rendering"),
                 ("bkg_enable", "rendering_1"),
                 ("rendering_1", "hpos_eq_339_and_rendering'")]
        print("[2C02] inversion-parity SWEEP over plausible dot-339 endpoints:")
        results = []
        for s, o in pairs:
            si, oi = n2i.get(s), n2i.get(o)
            if si is None or oi is None:
                print(f"  {s:<14} -> {o:<32}  (endpoint not in nodenames)"); continue
            inv = shortest_inversions(si, oi)
            if inv is None:
                print(f"  {s:<14} -> {o:<32}  no inverting path"); continue
            par = "ODD" if inv % 2 else "EVEN"
            results.append(par); print(f"  {s:<14} -> {o:<32}  {inv:>3} inversions  ->  {par}")
        odd = results.count("ODD"); even = results.count("EVEN")
        verdict = ("INCONCLUSIVE -- parity is source/observe-dependent" if odd and even
                   else "consistently ODD (weak support)" if odd and not even
                   else "consistently EVEN (weak counter)" if even and not odd else "no data")
        print(f"  spread: {odd} ODD / {even} EVEN  ->  {verdict}")
        print("  conclusion: shortest-path parity CANNOT settle the 16/18 prediction; "
              "the graph-shortest path is not the signal path. Needs dynamic signal-path tracing.")
        return 0

    src = n2i.get(a.source)
    obs = n2i.get(a.observe)
    if src is None or obs is None:
        print(f"error: source '{a.source}' or observe '{a.observe}' not in nodenames "
              f"(src={src}, obs={obs})", file=sys.stderr)
        return 2

    # 0/1 BFS over a graph with edge-weights = inversion count (inv edge=1, pass edge=0).
    # State = (node, parity); we want the min inversions to reach obs, and the parity.
    INF = 10 ** 9
    best = {src: 0}
    dq = deque([src])
    parent = {src: None}
    edgekind = {src: None}
    # deque as a simple 0-1 BFS: pass edges to front, inv edges to back
    while dq:
        x = dq.popleft()
        d = best[x]
        for y in pass_adj.get(x, ()):        # non-inverting: same distance
            if d < best.get(y, INF):
                best[y] = d; parent[y] = x; edgekind[y] = "pass"; dq.appendleft(y)
        for y in inv_adj.get(x, ()):         # inverting: +1
            if d + 1 < best.get(y, INF):
                best[y] = d + 1; parent[y] = x; edgekind[y] = "inv"; dq.append(y)

    if obs not in best:
        print(f"[2C02] no inverting/pass path from {a.source} to {a.observe} in the driver graph.")
        print("  (the AND-gate structure may route through a complement node; try --observe with the ' complement)")
        return 0

    invcount = best[obs]
    parity = "ODD" if invcount % 2 == 1 else "EVEN"
    # reconstruct the path
    path = []
    y = obs
    while y is not None:
        path.append((nm(y), edgekind.get(y)))
        y = parent.get(y)
    path.reverse()

    predicted = (parity == "ODD")
    print(f"[2C02] inversion parity from '{a.source}' to '{a.observe}':")
    print(f"  shortest inverting-path inversions = {invcount}  -> parity {parity}")
    print(f"  PREDICTION (16/18 rise-faster-than-fall requires ODD inversions): "
          f"{'CONFIRMED' if predicted else 'REFUTED (points at a super-buffer instead)'}")
    print(f"  path ({len(path)} nodes, inv-edges marked *):")
    for i, (name, kind) in enumerate(path):
        mark = " *inv" if kind == "inv" else ""
        print(f"    {i:>2}  {name}{mark}")

    summary = {
        "source": a.source, "observe": a.observe,
        "inversions": invcount, "parity": parity,
        "prediction_16_18_needs_odd": True,
        "confirmed": predicted,
        "path": [{"node": nm_, "edge": k} for nm_, k in path],
    }
    os.makedirs(a.outdir, exist_ok=True)
    jp = os.path.join(a.outdir, "m3_inversion_parity.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    print(f"  wrote {jp}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
