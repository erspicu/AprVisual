#!/usr/bin/env python3
"""Turn an --ac-verdict run into timing and throughput numbers.

Two inputs, both produced by the run itself:
  shots/progress.jsonl   one checkpoint every N frames -> a throughput TIME SERIES
  AccuracyCoin.json      the verdict + the authoritative totals

The time series is there to describe the spread, not to hunt for machine drift. Cost per
frame varies by a factor of ~1.4 across the suite simply because different tests toggle
different numbers of netlist nodes (rendering on vs a blanked screen), and the behavioural
test-mode shims ride along unoptimised. That variance is the workload, and it is expected.
Intervals are differenced rather than averaged from t=0, so one region cannot smear the next.

Runs against a partial run too (verdict pending), which is how it gets tested before
the 8-hour run lands.

  python tools/testrom/ac_report.py --dir tools/testrom/out/ac [--md out.md]
"""
import argparse, json, os, statistics as st, sys

NES_REALTIME_HC_S = 42_954_552.0   # 1.789773 MHz CPU * 24 master half-cycles (TestRunner.Bench.cs)
FPS               = 60.0988
REGRESSION_KHCS   = 111.1          # 147-ROM sweep, weighted per-test mean (report card)


def load(d):
    jl = os.path.join(d, "shots", "progress.jsonl")
    cps = []
    if os.path.isfile(jl):
        for ln in open(jl, encoding="utf-8"):
            ln = ln.strip()
            if not ln:
                continue
            try:
                cps.append(json.loads(ln))
            except json.JSONDecodeError:
                pass          # a torn last line while the engine is mid-write
    cps.sort(key=lambda c: c["frame"])
    res = None
    rj = os.path.join(d, "AccuracyCoin.json")
    if os.path.isfile(rj):
        res = json.load(open(rj, encoding="utf-8"))
    return cps, res


def intervals(cps):
    """Per-interval rates. Differenced, so each sample is independent of the ones before it."""
    out = []
    for a, b in zip(cps, cps[1:]):
        dw, df, dh = b["wallSec"] - a["wallSec"], b["frame"] - a["frame"], b["hc"] - a["hc"]
        if dw > 0 and df > 0:
            out.append({"frame": b["frame"], "hcs": dh / dw, "spf": dw / df})
    return out


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")   # the markdown has arrows/×; cp950 consoles choke
    except Exception:
        pass
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "out", "ac"))
    ap.add_argument("--md", default=None, help="also write the markdown to this file")
    a = ap.parse_args()

    cps, res = load(os.path.abspath(a.dir))
    if not cps and not res:
        sys.exit(f"no data under {a.dir}")

    L = []
    add = L.append

    if res:
        frames = res["frames"]; wall = res["wallSeconds"]; hc = res["halfCycles"]
        add(f"## Verdict\n")
        add(f"- **{res['status'].upper()}** — {res.get('resultText','')}")
        add(f"- detection: `{res.get('detection')}` (CPU RAM $07F0–$07F5, not the screen)")
        add(f"- engine: `{res.get('engineVersion')}` ({res.get('commitDate')})\n")
    else:
        last = cps[-1]
        frames, wall, hc = last["frame"], last["wallSec"], last["hc"]
        add(f"## Verdict\n\n- _run still in flight_ — numbers below are for the first {frames:,} frames\n")

    rate = hc / wall
    add("## Totals\n")
    add("| | |")
    add("|---|---|")
    add(f"| frames simulated | {frames:,} |")
    add(f"| console time simulated | {frames/FPS:.1f} s |")
    add(f"| half-cycles | {hc:,} ({hc/1e9:.2f} G) |")
    add(f"| wall time | {wall/3600:.2f} h ({wall:,.0f} s) |")
    if res and res.get("loadSeconds"):
        add(f"| netlist load | {res['loadSeconds']:.1f} s |")
    add(f"| **mean throughput** | **{rate/1000:,.1f} K hc/s** |")
    add(f"| mean cost per frame | {wall/frames:.2f} s |")
    add(f"| vs NES real-time | {100*rate/NES_REALTIME_HC_S:.3f}% → **{NES_REALTIME_HC_S/rate:,.0f}× too slow** |")
    add(f"| vs 147-ROM sweep ({REGRESSION_KHCS} K hc/s) | {100*(rate/1000)/REGRESSION_KHCS - 100:+.1f}% |")
    add("")

    # Reported over the auto-run region only (Debug_EC == $FF), so the boot/menu frames -- a different
    # workload, run once -- don't get mixed into the suite's own distribution.
    boot = [c for c in cps if c["debugEc"] != 255]
    auto = [c for c in cps if c["debugEc"] == 255]
    if boot and auto:
        bi, ai = intervals(boot), intervals(auto)
        if bi and ai:
            add(f"_Boot/menu ({boot[-1]['frame']} frames, rendering on): {st.median(x['spf'] for x in bi):.2f} s/frame. "
                f"Auto-run (screen blanked): {st.median(x['spf'] for x in ai):.2f} s/frame. "
                f"The stability figures below cover the auto-run only._\n")

    iv = intervals(auto) or intervals(cps)
    if iv:
        hcs = sorted(x["hcs"] for x in iv)
        spf = sorted(x["spf"] for x in iv)
        add(f"## Throughput stability ({len(iv)} intervals, differenced, auto-run only)\n")
        add("| | K hc/s | s/frame |")
        add("|---|---|---|")
        add(f"| min | {hcs[0]/1000:,.1f} | {spf[0]:.2f} |")
        add(f"| median | {st.median(hcs)/1000:,.1f} | {st.median(spf):.2f} |")
        add(f"| max | {hcs[-1]/1000:,.1f} | {spf[-1]:.2f} |")
        add(f"| spread (max/min) | {hcs[-1]/hcs[0]:.2f}× | |")
        add(f"| stdev | {st.pstdev(hcs)/1000:,.1f} | {st.pstdev(spf):.3f} |")
        add("")
        add("_The spread is workload, not machine. Cost per frame tracks how many netlist nodes toggle, so a "
            "test that leaves rendering on runs ~1.4× slower per frame than one that blanks the screen — and "
            "the behavioural test-mode shims are carried unoptimised on top. The mean is the honest headline; "
            "the median describes a screen-off frame._\n")

    md = "\n".join(L)
    print(md)
    if a.md:
        open(a.md, "w", encoding="utf-8").write(md + "\n")
        print(f"\n[written] {a.md}", file=sys.stderr)


if __name__ == "__main__":
    main()
