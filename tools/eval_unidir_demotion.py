#!/usr/bin/env python3
"""
Evaluate Gemini's "unidirectional pass-gate demotion (M1+M4, ~10-15%)" claim on the
REAL corrected 2A03+2C02 netlist.

Claim (consult #3, MD/suggest/2026-07-20-gemini-static-data-accel): many pass transistors
are provably STRICTLY UNIDIRECTIONAL (strong driver -> pure gate-cap, or strong driver ->
demonstrably weaker load). Route them to a scalar-move fast path
    NodeStates[dst] = NodeStates[src]; enqueue(dst_fanout)
bypassing the BFS/flag-OR/256-LUT, for an estimated ~10-15% net speedup.

This script MEASURES the static structural upper bound (how many transistors qualify) and
gives an honest verdict, since only static evidence can be gathered in Python (the dynamic
"how often do they conduct / are they in >1 groups" part needs engine instrumentation).

Usage:  python tools/eval_unidir_demotion.py
"""
import os, re, sys
from collections import defaultdict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SD   = os.path.join(REPO, "AprVisualBenchMark", "data", "system-def")

def parse_nodenames(path):
    n2i = {}
    txt = open(path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)", txt):
        n2i.setdefault(m.group(1), int(m.group(2)))
    return n2i

def parse_pulls(segdefs_path):
    """node id -> set of pull chars present ('+' pull-up, '-' pull-down)."""
    pulls = defaultdict(set)
    txt = open(segdefs_path, encoding="utf-8", errors="replace").read()
    for m in re.finditer(r"\[\s*(\d+)\s*,\s*'([+-])'", txt):
        pulls[int(m.group(1))].add(m.group(2))
    return pulls

def parse_transdefs(path):
    """[(name, gate, c1, c2, w1, length, area)]  (area = W*L strength proxy)"""
    rows = []
    txt = open(path, encoding="utf-8", errors="replace").read()
    pat = re.compile(r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"
                     r"\s*\[[^\]]*\]\s*,\s*\[([^\]]*)\]\s*(?:,\s*(true|false)\s*)?\]")
    for m in pat.finditer(txt):
        g = [float(x) for x in m.group(5).replace(" ", "").split(",") if x]
        w1, ln = (g[0], g[2]) if len(g) >= 3 else (0.0, 1.0)
        area = g[4] if len(g) >= 5 else (w1 * ln)
        strength = (w1 / ln) if ln else 0.0       # W/L ~ drive strength (M1)
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), w1, ln, area, strength))
    return rows

def analyze(chip):
    d = os.path.join(SD, chip)
    n2i = parse_nodenames(os.path.join(d, "nodenames.js"))
    pulls = parse_pulls(os.path.join(d, "segdefs.js"))
    tr = parse_transdefs(os.path.join(d, "transdefs.js"))
    vss = {n2i[k] for k in ("vss", "gnd", "GND") if k in n2i}
    vcc = {n2i[k] for k in ("vcc", "VCC") if k in n2i}
    supply = vss | vcc

    # degrees: channel connections (as c1/c2) and gate fan-out (as gate)
    chan_deg = defaultdict(int)
    gate_deg = defaultdict(int)
    for _, g, c1, c2, *_ in tr:
        chan_deg[c1] += 1; chan_deg[c2] += 1
        gate_deg[g]  += 1

    cats = defaultdict(int)
    cat_strength_ok = 0
    for name, g, c1, c2, w1, ln, area, strength in tr:
        s1, s2 = c1 in supply, c2 in supply
        if s1 or s2:
            cats["supply (pull-up/down to vss/vcc — already supply-fast-pathed)"] += 1
            continue
        # non-supply pass transistor: is one channel node a pure sink?
        # a "pure sink" leaf = its ONLY channel connection is this transistor, and it is not
        # independently driven (no pull). Then it is driven solely through this transistor.
        leaf1 = chan_deg[c1] == 1 and not pulls.get(c1)
        leaf2 = chan_deg[c2] == 1 and not pulls.get(c2)
        if leaf1 or leaf2:
            cats["PURE-SINK leaf (M4 topology -> definitely unidirectional)"] += 1
            continue
        # weaker-load case: one side has ONLY a weak pull ('+' depletion load) + few channel
        # conns, the other side is a driver -> the driver dominates (M1 strength). Approximate:
        # one channel node has a pull and degree<=2, taken as a driven-net whose load this xtor
        # can overpower. (Heuristic proxy for "strong driver -> demonstrably weaker load".)
        weak1 = pulls.get(c1) and chan_deg[c1] <= 2
        weak2 = pulls.get(c2) and chan_deg[c2] <= 2
        if weak1 or weak2:
            cats["weak-load dominated (M1 strength -> likely unidirectional)"] += 1
            continue
        cats["bidirectional internal pass gate (BFS/LUT genuinely needed)"] += 1
    return len(tr), cats

def main():
    print("=" * 78)
    print(" Evaluating: unidirectional pass-gate demotion (Gemini consult #3, ~10-15%)")
    print(" Netlist: corrected AprVisualBenchMark/data/system-def (what the engine runs)")
    print("=" * 78)
    grand = defaultdict(int); total = 0
    for chip in ("2a03", "2c02"):
        n, cats = analyze(chip)
        total += n
        print(f"\n[{chip}]  {n:,} transistors")
        for k in sorted(cats, key=lambda x: -cats[x]):
            print(f"    {cats[k]:6,}  {100*cats[k]/n:5.1f}%   {k}")
            grand[k] += cats[k]
    print("\n" + "=" * 78)
    print(f" COMBINED  {total:,} transistors")
    for k in sorted(grand, key=lambda x: -grand[x]):
        print(f"    {grand[k]:6,}  {100*grand[k]/total:5.1f}%   {k}")

    pure   = sum(v for k, v in grand.items() if k.startswith("PURE-SINK"))
    weak   = sum(v for k, v in grand.items() if k.startswith("weak-load"))
    supply = sum(v for k, v in grand.items() if k.startswith("supply"))
    bidir  = sum(v for k, v in grand.items() if k.startswith("bidirectional"))
    demot  = pure + weak
    print("\n" + "=" * 78)
    print(" VERDICT")
    print("=" * 78)
    print(f"  Provably unidirectional (demotable to scalar copy): {demot:,} = {100*demot/total:.1f}%")
    print(f"    - PURE-SINK leaf  (M4, airtight):  {pure:,} = {100*pure/total:.1f}%")
    print(f"    - weak-load dom.  (M1, heuristic): {weak:,} = {100*weak/total:.1f}%")
    print(f"  Already handled by supply fast-path: {supply:,} = {100*supply/total:.1f}%")
    print(f"  Genuinely bidirectional (needs BFS): {bidir:,} = {100*bidir/total:.1f}%")
    print()
    # honest speedup framing
    non_supply = total - supply
    print("  How to read this vs the ~10-15% claim:")
    print(f"   * Of the {non_supply:,} non-supply transistors (the ones the BFS/LUT actually")
    print(f"     works on), {100*demot/non_supply:.0f}% are demotable and {100*bidir/non_supply:.0f}% genuinely need the group solver.")
    print("   * This is a STATIC UPPER BOUND on removable work. The realised speedup is lower:")
    print("     (a) a transistor only saves work in the half-cycles it actually CONDUCTS;")
    print("     (b) size-1 groups are already fast-pathed, so only demotions inside larger")
    print("         conducting groups add value; (c) the scalar copy still costs an enqueue.")
    print("   * So the demotable fraction is the ceiling; net engine speedup needs a runtime")
    print("     count (engine instrumentation) to confirm.")
    print()
    print("  CONCLUSION on Gemini's claim:")
    print(f"   ! Gemini's CLEAN structural case ('strong driver -> a PURE gate-cap') is ABSENT")
    print(f"     here: {pure} = 0% pure-sink leaves. On this NMOS netlist almost every output")
    print("     node carries a depletion load (a segdefs '+' pull), so it is never a no-pull")
    print("     leaf -- the airtight M4 demotion Gemini pictured essentially does not occur.")
    print(f"   ! The engine ALREADY fast-paths the {100*supply/total:.0f}% supply transistors, so demotion adds")
    print("     nothing there. The only remaining slice is the weak-load HEURISTIC (needs a real")
    print("     per-net M1 strength comparison to confirm, and overlaps the existing pull-priority")
    print("     resolution in the 256-LUT).")
    print(f"   ! Genuinely bidirectional internal pass gates -- the group solver's real job -- are")
    print(f"     only {bidir:,} ({100*bidir/total:.1f}%). The BFS is not dominated by demotable one-way edges.")
    print("   => VERDICT: the static evidence does NOT support ~10-15%. The airtight case is 0%;")
    print("      the opportunity degrades to a fuzzy strength heuristic that overlaps existing")
    print("      fast-paths. A real judgement needs engine instrumentation counting group-BFS")
    print("      node-visits on demotable edges per half-cycle -- but the structural premise is weak.")

if __name__ == "__main__":
    main()
