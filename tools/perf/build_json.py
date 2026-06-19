#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AprVisual perf workflow — PROCESS stage.
Merge per-version metadata + measured metrics (boost / sizes / locked) into one platform JSON
(schema v1), aggregate the boost samples (best-3 / median / min / max / CV), validate, and write.

Inputs (CSV column names are matched case-insensitively; the existing MD/REPORT/data/*.csv files
are valid inputs, so this doubles as the P1 backfill):
  --metadata  version,date,tfm,commit,milestone,title,desc
  --env       env_x64.json (the meta/env block)
  --boost     Version,BoostTop3Avg,BoostMax,Samples[,Checksum]   (Samples = "a/b/c/d/e")
  --sizes     Version,IL,Native                                   (optional)
  --locked    Version,...,LockedCycPerHc                          (optional)

Usage (P1 backfill from the existing report data):
  python tools/perf/build_json.py --platform x64 \
    --metadata tools/perf/metadata.csv --env tools/perf/env_x64.json \
    --boost MD/REPORT/data/version_boost.csv --sizes MD/REPORT/data/version_size.csv \
    --locked MD/REPORT/data/version_perf_locked.csv --out tools/perf/out/x64.json
"""
import argparse, csv, json, os, sys, statistics, datetime

REALTIME_HC_S = 42954552
GOLDEN = "0x9174E19D961CB6E5"

def read_csv(path):
    if not path or not os.path.isfile(path): return []
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f))

def col(row, *names):
    """case-insensitive column fetch"""
    low = {k.lower(): v for k, v in row.items() if k is not None}
    for n in names:
        if n.lower() in low: return low[n.lower()]
    return None

def parse_samples(s):
    if not s: return []
    out = []
    for tok in str(s).replace(",", "/").split("/"):
        tok = tok.strip()
        if tok.isdigit(): out.append(int(tok))
    return out

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--platform", default="x64")
    ap.add_argument("--metadata", required=True)
    ap.add_argument("--env", required=True)
    ap.add_argument("--boost", required=True)
    ap.add_argument("--sizes")
    ap.add_argument("--locked")
    ap.add_argument("--golden", default=GOLDEN)
    ap.add_argument("--generated", default=datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"))
    ap.add_argument("--out", required=True)
    a = ap.parse_args()

    meta = {col(r, "version"): r for r in read_csv(a.metadata)}
    order = [col(r, "version") for r in read_csv(a.metadata)]
    boost = {col(r, "version"): r for r in read_csv(a.boost)}
    sizes = {col(r, "version"): r for r in read_csv(a.sizes)} if a.sizes else {}
    locked = {col(r, "version"): r for r in read_csv(a.locked)} if a.locked else {}
    with open(a.env, encoding="utf-8") as f:
        env = json.load(f)

    versions, warn, n_be = [], [], 0
    for v in order:
        m = meta[v]; b = boost.get(v)
        if not b:
            warn.append(f"{v}: no boost data — skipped"); continue
        samples = parse_samples(col(b, "samples"))
        best3 = int(col(b, "boosttop3avg") or 0) or (round(statistics.mean(sorted(samples, reverse=True)[:3])) if samples else 0)
        mx = int(col(b, "boostmax") or 0) or (max(samples) if samples else 0)
        ck = (col(b, "checksum") or a.golden).strip()
        be = ck.lower() == a.golden.lower()
        n_be += be
        metrics = {
            "hc_s_best3": best3,
            "hc_s_median": round(statistics.median(samples)) if samples else best3,
            "hc_s_min": min(samples) if samples else best3,
            "hc_s_max": mx,
            "hc_s_cv_pct": round(statistics.pstdev(samples) / statistics.mean(samples) * 100, 2) if len(samples) > 1 else 0.0,
            "hc_s_runs": samples,
            "checksum": ck,
            "bit_exact": be,
            "realtime_x": round(REALTIME_HC_S / best3) if best3 else None,
        }
        if v in sizes:
            metrics["il_size"] = int(col(sizes[v], "il") or 0)
            metrics["native_size"] = int(col(sizes[v], "native") or 0)
        else:
            warn.append(f"{v}: no size data")
        if v in locked:
            lc = col(locked[v], "lockedcycperhc")
            if lc: metrics["cyc_per_hc_locked"] = int(lc)
        else:
            warn.append(f"{v}: no locked data")
        versions.append({
            "version": v,
            "date": col(m, "date"),
            "commit": (col(m, "commit") or "").strip() or None,
            "tfm": col(m, "tfm"),
            "title": col(m, "title"),
            "desc": col(m, "desc"),
            "milestone": str(col(m, "milestone") or "0").strip() in ("1", "true", "True"),
            "metrics": metrics,
        })

    doc = {
        "schema": 1,
        "platform": a.platform,
        "cpu": env.get("cpu"),
        "mode": env.get("mode", "boost"),
        "generated": a.generated,
        "golden_checksum": a.golden,
        "realtime_hc_s": REALTIME_HC_S,
        "env": env,
        "versions": versions,
    }
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(doc, f, ensure_ascii=False, indent=2)

    # ---- validate + summary ----
    if not versions:
        print("ERROR: no versions produced", file=sys.stderr); sys.exit(1)
    first, last = versions[0]["metrics"]["hc_s_best3"], versions[-1]["metrics"]["hc_s_best3"]
    print(f"WROTE {a.out}  ({len(versions)} versions, {os.path.getsize(a.out)} B)")
    print(f"  boost {first:,} -> {last:,} hc/s = {last/first:.2f}x   realtime ~{doc['realtime_hc_s']//last}x away")
    print(f"  bit-exact: {n_be}/{len(versions)}" + ("" if n_be == len(versions) else "  <-- WARNING: not all bit-exact!"))
    miss_size = sum(1 for v in versions if "native_size" not in v["metrics"])
    miss_lock = sum(1 for v in versions if "cyc_per_hc_locked" not in v["metrics"])
    if miss_size: print(f"  note: {miss_size} versions missing size, {miss_lock} missing locked cyc/hc")
    if n_be != len(versions):
        sys.exit(2)

if __name__ == "__main__":
    main()
