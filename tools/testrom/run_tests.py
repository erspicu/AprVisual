#!/usr/bin/env python3
"""AprVisual.S1 test-ROM batch runner (switch-level engine — slow, plan hours).

Runs the A / A-r / C catalog (tools/testrom/catalog.json, 93 ROMs) through the
S1 headless test mode, N workers in parallel, each worker's exe pinned to its
own physical core (avoiding core 0 = OS noise). Results land in tools/testrom/out/
and the report is rebuilt at the end (WebSite/Report/).

Usage:
    python tools/testrom/run_tests.py                 # 4 workers, full catalog, resume-aware
    python tools/testrom/run_tests.py --jobs 2        # fewer workers
    python tools/testrom/run_tests.py --filter apu    # substring filter on suite/rom
    python tools/testrom/run_tests.py --class A-r     # one class only (A / A-r / C)
    python tools/testrom/run_tests.py --limit 4       # first N pending tests (smoke)
    python tools/testrom/run_tests.py --rerun         # ignore existing results, run everything again
    python tools/testrom/run_tests.py --report-only   # just rebuild WebSite/Report from existing results
    python tools/testrom/run_tests.py --no-build      # skip the dotnet build step

Notes (see MD/testrom_workflow/ for the full workflow doc):
  - primary budget is SIMULATION frames (--max-frames, from catalog); wall time ~5 s/frame.
  - resume: tests with an existing result JSON are skipped unless --rerun.
  - worker i pins its exe to LOGICAL core CORES[i] via S1's --pin (SMT pair = physical core;
    2,6,10,14 = physical 1,3,5,7 — two per CCX, core 0 avoided deliberately).
"""
import argparse, json, os, queue, subprocess, sys, threading, time

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO       = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
DLL        = os.path.join(REPO, "src", "AprVisual.S1", "bin", "Release", "net11.0", "AprVisual.S1.dll")
SYSTEM_DEF = os.path.join(REPO, "AprVisualBenchMark", "data", "system-def")
CATALOG    = os.path.join(SCRIPT_DIR, "catalog.json")
OUT_DIR    = os.path.join(SCRIPT_DIR, "out")

CORES   = [2, 6, 10, 14]   # logical cores (Zen2 3700X: physical 1,3,5,7; core 0 left to the OS)
STAGGER = 20               # seconds between worker starts (netlist compose is the heavy phase)
WALL_GUARD_PER_FRAME = 10  # subprocess kill guard: maxFrames * this + 600s


def key_of(t):
    return f"{t['suite']}/{t['rom']}".replace("/", "__")


def build_engine():
    print("=== dotnet build (Release) ===", flush=True)
    r = subprocess.run(["dotnet", "build", os.path.join(REPO, "src", "AprVisual.S1"), "-c", "Release"],
                       capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout[-2000:], r.stderr[-2000:])
        sys.exit("BUILD FAILED")


def run_one(t, core, rombase):
    k = key_of(t)
    rompath = os.path.join(rombase, t["suite"].replace("/", os.sep), t["rom"])
    jpath   = os.path.join(OUT_DIR, "results", k + ".json")
    spath   = os.path.join(OUT_DIR, "screenshots", t["suite"].replace("/", os.sep), t["rom"].replace(".nes", ".png"))
    lpath   = os.path.join(OUT_DIR, "logs", k + ".log")
    os.makedirs(os.path.dirname(jpath), exist_ok=True)
    os.makedirs(os.path.dirname(spath), exist_ok=True)
    os.makedirs(os.path.dirname(lpath), exist_ok=True)

    if not os.path.isfile(rompath):
        return ("SKIP", k, "rom not found")

    mf = t.get("maxFrames", 900)
    cmd = ["dotnet", DLL, "--test", rompath, "--max-frames", str(mf), "--pin", str(core),
           "--test-json", jpath, "--test-screenshot", spath, "--system-def-dir", SYSTEM_DEF]
    if t.get("expectedCrcs"):
        cmd += ["--expected-crc", ",".join(t["expectedCrcs"])]
    if t.get("class") == "B":
        cmd += ["--screen-verdict"]

    guard = mf * WALL_GUARD_PER_FRAME + 600
    t0 = time.time()
    try:
        with open(lpath, "w", encoding="utf-8") as lf:
            p = subprocess.run(cmd, stdout=lf, stderr=subprocess.STDOUT, timeout=guard)
        rc = p.returncode
    except subprocess.TimeoutExpired:
        return ("GUARD", k, f"wall guard {guard}s exceeded (killed)")
    mins = (time.time() - t0) / 60
    if os.path.isfile(jpath):
        try:
            st = json.load(open(jpath, encoding="utf-8"))["status"].upper()
        except Exception:
            st = f"RC{rc}"
        return (st, k, f"{mins:.0f} min")
    return (f"RC{rc}", k, f"{mins:.0f} min, no result json")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--filter", default=None, help="substring filter on suite/rom")
    ap.add_argument("--class", dest="cls", default=None, help="A / A-r / C")
    ap.add_argument("--limit", type=int, default=0, help="run only the first N pending tests")
    ap.add_argument("--rerun", action="store_true", help="ignore existing result JSONs")
    ap.add_argument("--report-only", action="store_true")
    ap.add_argument("--no-build", action="store_true")
    args = ap.parse_args()

    if args.report_only:
        subprocess.run([sys.executable, os.path.join(SCRIPT_DIR, "build_report.py")], check=True)
        return

    cat = json.load(open(CATALOG, encoding="utf-8"))
    rombase = os.path.join(REPO, cat["romBase"].replace("/", os.sep))
    tests = cat["tests"]
    if args.filter:
        tests = [t for t in tests if args.filter.lower() in f"{t['suite']}/{t['rom']}".lower()]
    if args.cls:
        tests = [t for t in tests if t["class"] == args.cls]
    def has_final(t):
        p = os.path.join(OUT_DIR, "results", key_of(t) + ".json")
        if not os.path.isfile(p):
            return False
        try:
            return json.load(open(p, encoding="utf-8")).get("status") in ("pass", "fail")
        except Exception:
            return False
    if not args.rerun:
        before = len(tests)
        tests = [t for t in tests if not has_final(t)]
        if before - len(tests):
            print(f"(resume: {before - len(tests)} tests already have pass/fail results — skipped; use --rerun to redo)")
    if args.limit > 0:
        tests = tests[:args.limit]

    if not tests:
        print("nothing to run")
        subprocess.run([sys.executable, os.path.join(SCRIPT_DIR, "build_report.py")], check=True)
        return

    if not args.no_build:
        build_engine()
    if not os.path.isfile(DLL):
        sys.exit(f"engine dll not found: {DLL}")

    jobs = min(args.jobs, len(CORES))
    q = queue.Queue()
    for t in tests:
        q.put(t)

    print(f"=== {len(tests)} tests, {jobs} workers on logical cores {CORES[:jobs]} "
          f"(stagger {STAGGER}s) ===", flush=True)
    t_start = time.time()
    lock = threading.Lock()
    tally = {"PASS": 0, "FAIL": 0, "TIMEOUT": 0, "OTHER": 0}

    def worker(idx):
        time.sleep(idx * STAGGER)
        core = CORES[idx]
        while True:
            try:
                t = q.get_nowait()
            except queue.Empty:
                return
            k = f"{t['suite']}/{t['rom']}"
            with lock:
                print(f"[{time.strftime('%H:%M:%S')}] w{idx}(core{core}) START {k} "
                      f"(class {t['class']}, budget {t.get('maxFrames', 900)}f, {q.qsize()} queued)", flush=True)
            st, kk, note = run_one(t, core, rombase)
            with lock:
                tally[st if st in tally else "OTHER"] = tally.get(st if st in tally else "OTHER", 0) + 1
                print(f"[{time.strftime('%H:%M:%S')}] w{idx}(core{core}) {st:7s} {k}  ({note})", flush=True)

    threads = [threading.Thread(target=worker, args=(i,), daemon=True) for i in range(jobs)]
    for th in threads:
        th.start()
    for th in threads:
        th.join()

    hrs = (time.time() - t_start) / 3600
    print(f"\n=== DONE in {hrs:.1f} h: {tally} ===")
    subprocess.run([sys.executable, os.path.join(SCRIPT_DIR, "build_report.py")], check=True)


if __name__ == "__main__":
    main()
