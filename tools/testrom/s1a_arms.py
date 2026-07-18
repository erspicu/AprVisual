#!/usr/bin/env python3
"""S1A isolated-ROM ARM runner for shim-retirement RE-VERIFICATION.

Why this exists: run_tests.py hard-wires the *S1* DLL and knows nothing about the
S1A-only mechanism envs (M4_EDGE / M6X / M2_DECAY ...). To re-verify whether a
mechanism can replace a shim we must run the *S1A* engine with per-arm env, on
the shim's defending ROM, at the CORRECT recipe (K=1 + the ROM's catalog flags).
Getting the flags wrong is exactly the K=0 false-alarm trap (see MD/memory/03) --
so this harness copies run_tests.py's per-ROM flag construction verbatim and only
swaps the DLL to S1A and injects each arm's extra environment.

An "arm" = one (ROM x env) run. A shim's re-verification is typically 3 arms:
  base  : default env (shim armed)            -> expect PASS  (sanity)
  ctrl  : NO_<shim>_SHIM (shim off, no mech)  -> FAIL => decidable, PASS => undecidable
  mech  : mechanism on (auto-supersedes shim) -> expect PASS  (mechanism replaces shim)

Arms run concurrently, each pinned to its own physical core (SMT-sibling-free),
K=1, applying the ROM's catalog flags. Verdicts are read from each arm's
--test-json and printed as a table (+ saved to out/<outdir>/summary.json).

Usage:
    python tools/testrom/s1a_arms.py jobs.json [--outdir s1a_reverify] [--jobs 6]
    python tools/testrom/s1a_arms.py --print-catalog dmc_basics   # inspect flags

jobs.json = [
  {"label":"dmc_ctrl", "rom":".../7-dmc_basics.nes", "env":{"NO_DMC_SHIM":"1"}},
  {"label":"dmc_mech", "rom":".../7-dmc_basics.nes", "env":{"M4_EDGE":"1"}},
  ...
]
Each arm may also set "flags" (list, appended), "max_frames" (int override),
"expect" ("pass"/"fail", for the printed OK/!! column), and "note".
"""
import argparse, json, os, queue, subprocess, sys, threading, time

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO       = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
# THE S1A DLL -- the whole point of this harness. run_tests.py uses S1.
DLL        = os.path.join(REPO, "src", "AprVisual.S1A", "bin", "Release", "net11.0", "AprVisual.S1A.dll")
SYSTEM_DEF = os.path.join(REPO, "AprVisualBenchMark", "data", "system-def")
CATALOG    = os.path.join(SCRIPT_DIR, "catalog.json")
OUT_ROOT   = os.path.join(SCRIPT_DIR, "out")

# Physical cores 3,5,7,1,6,2 (logical 2i), core 0 (OS) and core 8=phys4 avoided so a
# full AC run can share the box on phys4 without an SMT collision. Same layout as
# run_tests.py's 6-lane set. Even logical ids only = one thread per physical core.
CORES = [6, 10, 14, 2, 12, 4]
WALL_GUARD_PER_FRAME = 10
RESUME = False   # set by --resume; skip arms that already have a complete verdict


def load_catalog():
    """basename(rom) -> catalog entry, for per-ROM flag lookup."""
    cat = json.load(open(CATALOG, encoding="utf-8"))
    tests = cat["tests"] if isinstance(cat, dict) and "tests" in cat else cat
    idx = {}
    for t in tests:
        idx[os.path.basename(t["rom"])] = t
        idx[f"{t['suite']}/{t['rom']}".replace('\\', '/')] = t
    return idx


def catalog_flags(entry):
    """Reproduce run_tests.py's per-ROM flags EXACTLY (minus --pin/--test-json,
    which the runner adds per arm). Returns (flags_list, max_frames)."""
    mf = entry.get("maxFrames", 900)
    tf = entry.get("typicalFrames")
    if tf:
        mf = min(mf, int(tf * 1.5) + 5)
    flags = []
    if entry.get("expectedCrcs"):
        flags += ["--expected-crc", ",".join(entry["expectedCrcs"])]
    if entry.get("class") == "B":
        flags += ["--screen-verdict"]
    if entry.get("passMarker"):
        flags += ["--pass-marker", entry["passMarker"]]
    if entry.get("input"):
        flags += ["--input", entry["input"]]
    if entry.get("needsJoypad"):
        flags += ["--joypad"]
    if entry.get("class") in ("B", "C"):
        flags += ["--shot-delay", "60"]
    return flags, mf


def resolve_rom(spec):
    """A job 'rom' may be an absolute path, a repo-relative path, or a basename we
    search for under the known ROM roots."""
    if os.path.isabs(spec) and os.path.isfile(spec):
        return spec
    p = os.path.join(REPO, spec)
    if os.path.isfile(p):
        return p
    base = os.path.basename(spec)
    for root in ("AprNesRef/nes-test-roms-master/checked", "AprNesRef/unittest/roms",
                 "tools/testrom/roms", "AprAccuracyCoinUnattended", "checked"):
        rr = os.path.join(REPO, root)
        if not os.path.isdir(rr):
            continue
        for dirpath, _dirs, files in os.walk(rr):
            if base in files:
                return os.path.join(dirpath, base)
    return None


def run_arm(job, core, outdir):
    label = job["label"]
    rom = resolve_rom(job["rom"])
    if rom is None:
        return {"label": label, "status": "NO_ROM", "detail": job["rom"], "core": core}
    cat = CAT.get(os.path.basename(rom))
    flags, mf = catalog_flags(cat) if cat else ([], job.get("max_frames", 900))
    if job.get("max_frames"):
        mf = job["max_frames"]
    jpath = os.path.join(outdir, label + ".json")
    spath = os.path.join(outdir, label + ".png")
    lpath = os.path.join(outdir, label + ".log")

    # --resume: an interrupted batch (e.g. the host process restarted) leaves some arms
    # done and some not. Skip any arm that already has a complete verdict so a re-launch
    # only runs the missing ones -- never re-run the whole set.
    if RESUME and os.path.isfile(jpath):
        try:
            r = json.load(open(jpath, encoding="utf-8"))
            if r.get("status"):
                return {"label": label, "core": core, "status": r["status"].upper(),
                        "resultCode": r.get("resultCode"), "detection": r.get("detection"),
                        "resultText": r.get("resultText", ""), "frames": r.get("frames"),
                        "hc": r.get("halfCycles"), "rom": os.path.basename(rom),
                        "env": job.get("env", {}), "expect": job.get("expect"), "cached": True}
        except Exception:
            pass

    cmd = ["dotnet", DLL, "--test", rom, "--max-frames", str(mf), "--pin", str(core),
           "--reset-hold-extra", "1",
           "--test-json", jpath, "--test-screenshot", spath, "--system-def-dir", SYSTEM_DEF]
    cmd += flags
    cmd += job.get("flags", [])

    env = dict(os.environ)
    env.update({k: str(v) for k, v in job.get("env", {}).items()})

    for stale in (jpath, spath):
        try:
            os.remove(stale)
        except OSError:
            pass

    guard = mf * WALL_GUARD_PER_FRAME + 600
    t0 = time.time()
    try:
        with open(lpath, "w", encoding="utf-8") as lf:
            lf.write(f"# cmd: {' '.join(cmd)}\n# extra-env: {job.get('env', {})}\n\n")
            lf.flush()
            p = subprocess.run(cmd, stdout=lf, stderr=subprocess.STDOUT, timeout=guard, env=env)
        rc = p.returncode
    except subprocess.TimeoutExpired:
        return {"label": label, "status": "GUARD", "detail": f"{guard}s", "core": core,
                "mins": round((time.time() - t0) / 60, 1)}
    mins = round((time.time() - t0) / 60, 2)
    out = {"label": label, "core": core, "rc": rc, "mins": mins,
           "rom": os.path.basename(rom), "env": job.get("env", {}), "expect": job.get("expect")}
    if os.path.isfile(jpath):
        try:
            r = json.load(open(jpath, encoding="utf-8"))
            out["status"] = r.get("status", "?").upper()
            out["resultCode"] = r.get("resultCode")
            out["detection"] = r.get("detection")
            out["resultText"] = r.get("resultText", "")
            out["frames"] = r.get("frames")
            out["hc"] = r.get("halfCycles")
        except Exception as e:
            out["status"] = "BADJSON"
            out["detail"] = str(e)
    else:
        out["status"] = "NOJSON"
    return out


def worker(jobq, results, outdir, core):
    # each worker OWNS one physical core for the whole run, so at most len(CORES) arms
    # ever run at once and never two on the same core (a job-index core assignment
    # collides once cached/fast arms let workers race ahead). Correctness is unaffected
    # either way -- verdicts are deterministic -- but this keeps the pinning clean.
    while True:
        try:
            job = jobq.get_nowait()
        except queue.Empty:
            return
        print(f"  [core {core:>2}] start {job['label']}", flush=True)
        r = run_arm(job, core, outdir)
        results.append(r)
        ok = ""
        if r.get("expect") and r["status"] in ("PASS", "FAIL"):
            got = "pass" if r["status"] == "PASS" else "fail"
            ok = "  OK" if got == r["expect"] else "  <<< UNEXPECTED"
        print(f"  [core {core:>2}] {r['status']:>6} {job['label']}  "
              f"({r.get('resultText', r.get('detail', ''))}) {r.get('mins', '?')}min{ok}", flush=True)
        jobq.task_done()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("jobs", nargs="?", help="jobs JSON file")
    ap.add_argument("--outdir", default="s1a_reverify")
    ap.add_argument("--jobs-n", type=int, default=len(CORES), dest="jobsn")
    ap.add_argument("--print-catalog", help="print catalog flags for a rom substring and exit")
    ap.add_argument("--resume", action="store_true", help="skip arms that already have a complete verdict")
    args = ap.parse_args()

    global CAT, RESUME
    CAT = load_catalog()
    RESUME = args.resume

    if args.print_catalog:
        for k, e in CAT.items():
            if args.print_catalog in k and "/" not in k:
                fl, mf = catalog_flags(e)
                print(f"{k}: class={e.get('class')} maxFrames={mf} flags={fl}")
        return

    if not os.path.isfile(DLL):
        sys.exit(f"S1A dll not found (build it first): {DLL}")
    jobs = json.load(open(args.jobs, encoding="utf-8"))
    outdir = os.path.join(OUT_ROOT, args.outdir)
    os.makedirs(outdir, exist_ok=True)

    cores = CORES[:args.jobsn]
    print(f"== S1A arms: {len(jobs)} arms, {len(cores)} lanes {cores}, K=1, DLL=S1A ==", flush=True)
    jobq = queue.Queue()
    for job in jobs:
        jobq.put(job)
    results = []
    threads = [threading.Thread(target=worker, args=(jobq, results, outdir, cores[k])) for k in range(len(cores))]
    for t in threads:
        t.start()
        time.sleep(2)   # stagger: netlist compose is the heavy startup phase
    for t in threads:
        t.join()

    results.sort(key=lambda r: r["label"])
    json.dump(results, open(os.path.join(outdir, "summary.json"), "w"), indent=2)
    print("\n== SUMMARY ==", flush=True)
    print(f"{'label':<22}{'status':<8}{'result':<40}{'exp':<6}{'ok'}")
    for r in results:
        exp = r.get("expect") or ""
        ok = ""
        if exp and r["status"] in ("PASS", "FAIL"):
            got = "pass" if r["status"] == "PASS" else "fail"
            ok = "OK" if got == exp else "<<<UNEXPECTED"
        rt = (r.get("resultText") or r.get("detail") or "")[:38]
        print(f"{r['label']:<22}{r['status']:<8}{rt:<40}{exp:<6}{ok}")
    print(f"\nsummary -> {os.path.join(outdir, 'summary.json')}")


if __name__ == "__main__":
    main()
