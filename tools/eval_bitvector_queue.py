#!/usr/bin/env python3
"""
Evaluate Gemini's "topological bit-vector queue (M7), ~15-25%" claim on the REAL netlist.

Claim (consult #3): replace the double-buffered FIFO event queue with a ~1.25 KB bit-vector
indexed by an M7 canonical/topological node id. Benefits:
   (1) branchless OR enqueue with auto-dedup (setting a bit twice is a no-op);
   (2) no queue-overflow logic / bounds checks;
   (3) L1-resident (10k bits ~ 1.25 KB);
   (4) *topological* forward scan -> upstream resolves before downstream ->
       "flattens iterate-to-quiescence" (fewer settle passes).  <-- the big claim.

Benefit (4) REQUIRES a meaningful topological order. In a switch-level netlist the channel
of every pass transistor is BIDIRECTIONAL, so channel-connected nodes are mutually
dependent -> they collapse into strongly-connected components. If the graph is dominated by
one giant SCC, there is NO static topological order and benefit (4) evaporates; only the
order-independent wins (1)(2)(3) survive. This script measures that structure.

(SCC lower bound = channel-connected components: bidirectional channel edges make any two
channel-linked nodes mutually reachable, so they share an SCC; directed gate edges can only
merge more. So the largest channel component is a floor on the largest SCC.)

Usage:  python tools/eval_bitvector_queue.py
"""
import os, re
from collections import defaultdict, Counter

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SD   = os.path.join(REPO, "AprVisualBenchMark", "data", "system-def")

def parse_nodenames(path):
    n2i = {}
    for m in re.finditer(r"['\"]?([\w/#~+.\-\[\]]+)['\"]?\s*:\s*(\d+)",
                         open(path, encoding="utf-8", errors="replace").read()):
        n2i.setdefault(m.group(1), int(m.group(2)))
    return n2i

def parse_transdefs(path):
    rows = []
    pat = re.compile(r"\[\s*'([^']+)'\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,"
                     r"\s*\[[^\]]*\]\s*,\s*\[[^\]]*\]\s*(?:,\s*(?:true|false)\s*)?\]")
    for m in pat.finditer(open(path, encoding="utf-8", errors="replace").read()):
        rows.append((m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4))))
    return rows

def tarjan_scc(adj, nodes):
    """Iterative Tarjan; returns list of SCCs (each a list of node ids)."""
    index = {}; low = {}; onstk = {}; stk = []; sccs = []; idx = [0]
    for start in nodes:
        if start in index: continue
        work = [(start, 0)]
        while work:
            v, pi = work[-1]
            if pi == 0:
                index[v] = low[v] = idx[0]; idx[0] += 1
                stk.append(v); onstk[v] = True
            recurse = False
            neigh = adj.get(v, ())
            i = pi
            while i < len(neigh):
                w = neigh[i]
                if w not in index:
                    work[-1] = (v, i + 1)
                    work.append((w, 0)); recurse = True; break
                elif onstk.get(w):
                    low[v] = min(low[v], index[w])
                i += 1
            if recurse: continue
            if low[v] == index[v]:
                comp = []
                while True:
                    w = stk.pop(); onstk[w] = False; comp.append(w)
                    if w == v: break
                sccs.append(comp)
            work.pop()
            if work:
                pv = work[-1][0]
                low[pv] = min(low[pv], low[v])
    return sccs

def analyze(chip):
    d = os.path.join(SD, chip)
    n2i = parse_nodenames(os.path.join(d, "nodenames.js"))
    tr  = parse_transdefs(os.path.join(d, "transdefs.js"))
    supply = {n2i[k] for k in ("vss","gnd","GND","vcc","VCC") if k in n2i}

    # FULL settle-dependency graph:
    #   channel c1<->c2  (bidirectional: either side can drive the other)
    #   gate    g ->c1, g->c2  (the gate decides whether c1/c2 are in the same group)
    adj = defaultdict(list); nodes = set(); chan_deg = Counter(); nedge = 0
    for _, g, c1, c2 in tr:
        for n in (c1, c2, g):
            if n not in supply: nodes.add(n)
        if c1 not in supply and c2 not in supply:
            adj[c1].append(c2); adj[c2].append(c1); nedge += 1
            chan_deg[c1] += 1; chan_deg[c2] += 1
        if g not in supply:
            for c in (c1, c2):
                if c not in supply: adj[g].append(c); nedge += 1

    sccs = tarjan_scc(adj, nodes)
    sizes = sorted((len(s) for s in sccs), reverse=True)
    return dict(chip=chip, ntr=len(tr), nsig=len(nodes), nedge=nedge,
                nscc=len(sizes), sizes=sizes, chan_deg=chan_deg)

def main():
    print("=" * 80)
    print(" Evaluating: topological bit-vector queue (Gemini consult #3, ~15-25%)")
    print(" Netlist: corrected AprVisualBenchMark/data/system-def (what the engine runs)")
    print("=" * 80)

    tot_sig = tot_nodes_in_giant = 0
    all_deg = Counter()
    for chip in ("2a03", "2c02"):
        a = analyze(chip)
        giant = a["sizes"][0] if a["sizes"] else 0
        singles = sum(1 for s in a["sizes"] if s == 1)
        levelizable = sum(a["sizes"][1:])   # everything outside the giant SCC is orderable
        print(f"\n[{chip}]  {a['ntr']:,} transistors, {a['nsig']:,} signal nodes (vss/vcc excluded)")
        print(f"    dependency graph (channel<->  +  gate->): {a['nedge']:,} edges, {a['nscc']:,} SCCs")
        print(f"    LARGEST SCC: {giant:,} nodes = {100*giant/a['nsig']:.1f}% of signal nodes (NOT topologically orderable)")
        print(f"    top-5 SCC sizes: {a['sizes'][:5]}")
        print(f"    levelizable (outside the giant SCC): {levelizable:,} = {100*levelizable/a['nsig']:.1f}%  ({singles:,} are size-1)")
        tot_sig += a["nsig"]; tot_nodes_in_giant += giant
        all_deg.update(a["chan_deg"])

    print("\n" + "=" * 80)
    frac = 100 * tot_nodes_in_giant / tot_sig
    print(f" COMBINED: largest SCC holds {tot_nodes_in_giant:,} / {tot_sig:,}"
          f" signal nodes = {frac:.1f}%  (these have NO static topological order)")
    print(f" APPLICABLE fraction for the topological-order win: {100-frac:.1f}% (the levelizable rest)")

    # dedup proxy: high channel-degree nodes get re-reached by multiple paths per settle
    degs = sorted(all_deg.values(), reverse=True)
    hi = sum(1 for x in degs if x >= 4)
    print(f" dedup proxy: {hi:,} nodes have channel-degree >=4 (multi-path -> duplicate enqueues"
          f" the bit-set would collapse); median degree {degs[len(degs)//2]}")

    # bit-vector L1 footprint
    total_nodes = tot_sig
    print(f" bit-vector footprint: ~{total_nodes:,} nodes -> {total_nodes//8/1024:.2f} KB (fits L1 easily)")

    print("\n" + "=" * 80)
    print(" VERDICT")
    print("=" * 80)
    print(f"  Benefit (4) 'topological order -> fewer settles' — the BIG part of ~15-25%:")
    if frac > 60:
        print(f"    UNDERCUT. {frac:.0f}% of signal nodes live in ONE giant bidirectional SCC, so")
        print("    there is NO static topological order over them. M7 gives a *deterministic* id,")
        print("    not an *upstream-before-downstream* one; scanning it does not guarantee fewer")
        print("    settle passes inside the giant SCC. This matches the project's standing result")
        print("    that no auto-derivable static DAG exists (the reason the perf era concluded).")
    else:
        print(f"    PLAUSIBLE: only {frac:.0f}% is in the giant SCC; the rest is levelizable and would")
        print("    benefit from topological scan order.")
    print()
    print("  Order-INDEPENDENT benefits (1)(2)(3) survive regardless of the SCC wall:")
    print("    (1) branchless OR enqueue + auto-dedup — real; magnitude depends on the runtime")
    print(f"        duplicate-enqueue rate (proxy: {hi:,} high-degree nodes get re-reached).")
    print("    (2) no overflow/bounds logic — real but tiny (a few instructions saved).")
    print(f"    (3) L1-resident {total_nodes//8/1024:.2f} KB bitset — real, BUT the current FIFO working set")
    print("        per half-cycle is small (mean conducting group ~1.4); a sparse bitset must be")
    print("        CTZ-scanned across all its words even when few bits are set, so it can LOSE to a")
    print("        tiny dense FIFO when the active set is small. This is the key risk to prototype.")
    print()
    print("  => VERDICT: the head-line 'topological ordering' win is largely undercut by the")
    print(f"     {frac:.0f}%-giant-SCC structure (same wall that ended the perf era). What remains is the")
    print("     dedup + bookkeeping wins, whose net value hinges on (a) how often nodes are re-enqueued")
    print("     per settle and (b) whether the sparse-bitset scan beats the small FIFO — both RUNTIME")
    print("     questions. A minimal prototype should measure duplicate-enqueue rate + settle-pass")
    print("     count first; the ~15-25% is optimistic given the SCC wall, but the dedup angle is the")
    print("     one worth a cheap runtime probe (unlike the unidirectional-demotion idea, which the")
    print("     netlist structure kills outright).")

if __name__ == "__main__":
    main()
