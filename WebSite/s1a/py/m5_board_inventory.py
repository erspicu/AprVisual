#!/usr/bin/env python3
"""m5_board_inventory.py — the board as a circuit  (S1a toolbox, M5)

The S1 node graph is two dies plus the parts between them: an octal latch, an
address decoder, hex inverters, a CIC, controllers, the cartridge.  M5 is the
board-level component library — TTL/CMOS parts hung on the inter-die buses as
first-class devices instead of behavioral hacks.  This census reads the system
definition (nes-001) and its component defs and answers three questions:

  1. INVENTORY   — what parts are on the board, of what kind, how big
                   (transistor count = "is this really a switch-level module,
                   or a behavioral stand-in?").
  2. THE BUS MAP — every connection classified by which boundary it crosses:
                   die<->die (the CPU/PPU interface — the M3/M6 delay island),
                   die<->board, board<->board, or power. The cross-die count is
                   the exact width of the interface the accuracy campaigns kept
                   fighting.
  3. UNDRIVABLE  — which gate-level component modules are STRUCTURALLY
                   undrivable: a storage node whose only path is a pass gate
                   that reverse-drives its own input.  GND-always-wins then
                   pins it forever ("a released controller button can never be
                   written back in") — the known-by-construction reason the
                   CD4021 controller must be modeled behaviorally, not a bug.

Ground truth: the twins u7/u8 (both 74LS368) are the D-class lottery pair M7
also targets; the pslatch/4021 controller chain is the undrivable one.

Outputs: console report, JSON summary, two SVG figures.

Usage (bring your own MetalNES-style system-def dir — not vendored here):
    python m5_board_inventory.py --sysdef path/to/system-def --top nes-001 --outdir out/

Stdlib only.  Part of the AprVisual S1a study — see s1a.html / m5-board.html.
"""
import argparse, json, os, re, sys
from collections import defaultdict, Counter

# component role classification by module type
ROLE = {
    "2a03": "die (CPU)", "2c02": "die (PPU)",
    "SRAM2K": "memory", "SRAM8K": "memory", "ROM8K": "memory", "ROM32K": "memory",
    "74LS373": "board TTL (octal latch)", "74LS139": "board TTL (decoder)",
    "74LS368": "board TTL (hex buffer)", "74HC04": "board CMOS (inverter)",
    "nes-cic1": "board (lockout CIC)", "nes-pad": "controller", "nes-pad-behavioral": "controller",
    "4021": "controller shift reg", "pslatch": "controller latch cell",
}


def strip_comments(txt):
    txt = re.sub(r"/\*.*?\*/", "", txt, flags=re.S)
    txt = re.sub(r"//[^\n]*", "", txt)
    return txt


def load_module(sysdef, name):
    path = os.path.join(sysdef, name + ".js")
    if not os.path.exists(path):
        return None
    return strip_comments(open(path, encoding="utf-8", errors="replace").read())


def extract_array(txt, key):
    """Return the substring inside key's top-level [ ... ], bracket-balanced."""
    m = re.search(re.escape(key) + r"\s*:\s*\[", txt)
    if not m:
        return ""
    i = m.end()          # just past the opening [
    depth, start = 1, i
    while i < len(txt) and depth > 0:
        c = txt[i]
        if c == "[": depth += 1
        elif c == "]": depth -= 1
        i += 1
    return txt[start:i - 1]


def parse_modules(txt):
    """modules: [ [prefix, type, x], ... ]"""
    body = extract_array(txt, "modules")
    out = []
    for row in re.finditer(r"\[\s*\"([^\"]+)\"\s*,\s*\"([^\"]+)\"", body):
        out.append((row.group(1), row.group(2)))
    return out


def parse_connections(txt):
    body = extract_array(txt, "connections")
    out = []
    for row in re.finditer(r"\[\s*\"([^\"]+)\"\s*,\s*\"([^\"]+)\"\s*,?\s*\]", body):
        out.append((row.group(1), row.group(2)))
    return out


def parse_pins(txt):
    m = re.search(r"pins\s*:\s*\[(.*?)\]\s*,", txt, flags=re.S)
    if not m:
        return []
    return [(int(r.group(1)), r.group(2)) for r in re.finditer(r"\[\s*(\d+)\s*,\s*'([^']+)'", m.group(1))]


def count_transdefs(txt):
    """count transistor rows (component defs use named-string refs: ['t','LE','/LE',2])."""
    body = extract_array(txt, "transdefs")
    return len(re.findall(r"\[\s*'[^']*'\s*,", body))


def submodule_types(txt):
    """types of this module's direct sub-modules."""
    body = extract_array(txt, "modules")
    return [r.group(1) for r in re.finditer(r"\[\s*\"[^\"]+\"\s*,\s*\"([^\"]+)\"", body)]


UNDRIVABLE_CELLS = {"4021", "pslatch"}   # gate-level pass-gate latches that reverse-drive their input


def contains_undrivable(sysdef, typ, cache, depth=0):
    """True if this component type or any sub-module (transitively) is a reverse-driven latch cell."""
    if typ in UNDRIVABLE_CELLS:
        return True
    if depth > 6:
        return False
    mt = cache.get(typ) if typ in cache else cache.setdefault(typ, load_module(sysdef, typ))
    if not mt:
        return False
    return any(contains_undrivable(sysdef, st, cache, depth + 1) for st in submodule_types(mt))


def owner(expr):
    """top-level instance prefix of a connection endpoint expression."""
    e = expr.strip().lstrip("*").lstrip(".")
    if e in ("vss", "vcc", "clk") or e.startswith("func<"):
        return "power/clock"
    return e.split(".")[0] if "." in e else e


# --- SVG (dark theme, escaped) ----------------------------------------------
BG, CARD, BD, FG, MUT = "#100f15", "#1a1721", "#2e2634", "#dcd7e3", "#968ba1"
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

    def line(self, x1, y1, x2, y2, stroke=BD, w=1):
        self.el.append(f'<line x1="{x1:.1f}" y1="{y1:.1f}" x2="{x2:.1f}" y2="{y2:.1f}" stroke="{stroke}" stroke-width="{w}"/>')

    def rect(self, x, y, w, h, fill, opac=1.0, rx=3, stroke=None):
        s = f' stroke="{stroke}" stroke-width="1.5"' if stroke else ""
        self.el.append(f'<rect x="{x:.1f}" y="{y:.1f}" width="{w:.1f}" height="{h:.1f}" fill="{fill}" fill-opacity="{opac}" rx="{rx}"{s}/>')

    def text(self, x, y, s, fill=MUT, size=12, anchor="start", bold=False):
        fw = ' font-weight="700"' if bold else ""
        self.el.append(f'<text x="{x:.1f}" y="{y:.1f}" fill="{fill}" font-size="{size}"{fw} text-anchor="{anchor}">{self.e(s)}</text>')

    def save(self, path):
        self.el.append("</svg>")
        open(path, "w", encoding="utf-8").write("\n".join(self.el))


def fig_boundaries(path, counts):
    W, H, L, R, T, B = 780, 320, 70, 30, 56, 60
    s = Svg(W, H, "Board wiring: nets by the boundary they cross")
    rows = [("die <-> die (CPU/PPU interface)", counts.get("die-die", 0), BAD),
            ("die <-> board part", counts.get("die-board", 0), WARN),
            ("board <-> board", counts.get("board-board", 0), ACC2),
            ("power / clock fan-out", counts.get("power", 0), MUT)]
    mx = max(c for _, c, _ in rows) or 1
    y = 66
    for name, c, col in rows:
        w = 8 + (W - 360) * c / mx
        s.rect(300, y, w, 34, col, 0.85)
        s.text(292, y + 22, name, FG, 12.5, "end")
        s.text(306 + w, y + 22, str(c), FG, 13, "start", bold=True)
        y += 56
    s.save(path)


def fig_inventory(path, parts):
    W = 780
    H = 70 + 30 * len(parts)
    s = Svg(W, H, "Board inventory: parts by transistor count (log)")
    import math
    mx = max(math.log10(p[2] + 1) for p in parts) or 1
    y = 58
    for prefix, typ, ntr, role, drivable in parts:
        col = ACC2 if "die" in role else BAD if "controller" in role and not drivable else ACC if "board" in role else WARN
        w = 6 + (W - 420) * math.log10(ntr + 1) / mx
        s.rect(360, y, w, 20, col, 0.85)
        s.text(352, y + 15, f"{prefix} ({typ})", FG, 12, "end")
        tag = f"{ntr} tr" + ("" if drivable else "  · undrivable")
        s.text(366 + w, y + 15, tag, BAD if not drivable else FG, 11.5, "start", bold=not drivable)
        y += 30
    s.save(path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--sysdef", required=True, help="MetalNES-style system-def directory")
    ap.add_argument("--top", default="nes-001")
    ap.add_argument("--outdir", default=".")
    a = ap.parse_args()
    os.makedirs(a.outdir, exist_ok=True)

    top = load_module(a.sysdef, a.top)
    if top is None:
        print(f"error: {a.top}.js not found in {a.sysdef}"); return 1
    modules = parse_modules(top)
    conns = parse_connections(top)

    # per-module transistor count (own + descendants) + undrivable classification
    parts, undrivable_names = [], []
    cache = {}
    def total_tr(typ, depth=0):
        if depth > 6: return 0
        mt = cache.get(typ) if typ in cache else cache.setdefault(typ, load_module(a.sysdef, typ))
        if not mt: return 0
        return count_transdefs(mt) + sum(total_tr(st, depth + 1) for st in submodule_types(mt))
    for prefix, typ in modules:
        ntr = total_tr(typ)
        role = ROLE.get(typ, "other")
        # undrivable: contains a gate-level pass-gate latch that reverse-drives its input
        # (the 4021/pslatch controller chain) — known-by-construction, must be behavioral.
        drivable = not contains_undrivable(a.sysdef, typ, cache)
        parts.append((prefix, typ, ntr, role, drivable))
        if not drivable:
            undrivable_names.append(f"{prefix} ({typ})")

    # boundary census
    def die_of(o):
        for prefix, typ in modules:
            if o == prefix:
                if typ in ("2a03", "2c02"): return "die"
                if o == "power/clock": return "power"
                return "board"
        return "power" if o == "power/clock" else "board"
    counts = Counter()
    cross_die_nets = []
    for f, t in conns:
        of, ot = owner(f), owner(t)
        kf, kt = die_of(of), die_of(ot)
        if of == "power/clock" or ot == "power/clock" or kf == "power" or kt == "power":
            counts["power"] += 1
        elif kf == "die" and kt == "die":
            counts["die-die"] += 1
            if of != ot: cross_die_nets.append((f, t))
        elif kf == "die" or kt == "die":
            counts["die-board"] += 1
        else:
            counts["board-board"] += 1

    # twins: modules sharing a type (D-class lottery candidates, e.g. u7/u8)
    bytype = defaultdict(list)
    for prefix, typ in modules:
        bytype[typ].append(prefix)
    twins = {t: ps for t, ps in bytype.items() if len(ps) > 1}

    role_counts = Counter(role for _, _, _, role, _ in parts)

    fig_boundaries(os.path.join(a.outdir, "m5_boundaries.svg"), counts)
    fig_inventory(os.path.join(a.outdir, "m5_inventory.svg"), parts)

    summary = {
        "top": a.top, "modules": len(modules), "connections": len(conns),
        "roles": dict(role_counts),
        "boundary_census": dict(counts),
        "cross_die_interface_nets": [{"a": f, "b": t} for f, t in cross_die_nets],
        "cross_die_width": len(cross_die_nets),
        "parts": [{"prefix": p, "type": t, "transistors": n, "role": r, "drivable": d}
                  for p, t, n, r, d in parts],
        "structurally_undrivable": undrivable_names,
        "twins": twins,
    }
    jp = os.path.join(a.outdir, "m5_summary.json")
    json.dump(summary, open(jp, "w", encoding="utf-8"), indent=1, ensure_ascii=False)

    print(f"[{a.top}] {len(modules)} modules, {len(conns)} connections")
    print(f"  roles: " + "  ".join(f"{k}={v}" for k, v in role_counts.most_common()))
    print(f"  boundary census: die<->die={counts['die-die']} (interface width {len(cross_die_nets)} nets), "
          f"die<->board={counts['die-board']}, board<->board={counts['board-board']}, power={counts['power']}")
    print(f"  parts by transistor count:")
    for p, t, n, r, d in sorted(parts, key=lambda x: -x[2]):
        print(f"    {p:<12} {t:<16} {n:>6} tr   {r}" + ("" if d else "   [STRUCTURALLY UNDRIVABLE]"))
    print(f"  twins (D-class lottery candidates): " + ", ".join(f"{t}: {'/'.join(ps)}" for t, ps in twins.items()))
    print(f"  structurally undrivable (must be behavioral): {', '.join(undrivable_names) or 'none'}")
    print(f"  wrote {jp} + 2 SVGs in {a.outdir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
