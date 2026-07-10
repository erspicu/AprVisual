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
    python tools/testrom/run_tests.py --no-canary     # skip the pre-flight engine canary (~70 s)

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

# Pre-flight canary (see canary()). CANARY_ROM is the shortest class-A ($6000) test we
# have — it reaches its verdict at frame 11. GOLDEN_CKSUM is the project's bit-exactness
# gate: the NodeStates checksum after 300k half-cycles of full_palette.nes with --extra-ram.
CANARY_ROM    = os.path.join(SCRIPT_DIR, "roms", "nes_instr_test", "rom_singles", "11-special.nes")
CANARY_FRAMES = 40
BENCH_ROM     = os.path.join(REPO, "AprVisualBenchMark", "roms", "full_palette.nes")
BENCH_HC      = 300000
GOLDEN_CKSUM  = "0x794A43ABDF169ADA"

CORES   = [2, 6, 10, 14, 4, 12, 8, 0]   # logical cores, Zen2 3700X (logical 2i = physical i). First 4 = physical
# Lanes 1-7 are distinct physical cores avoiding core 0 (OS noise). The 8th lane has no
# noise-free physical core left: core 0 is used LAST (jobs=8 only) — it still adds ~0.9
# of a core, far better than an SMT sibling (~0.25, and it would slow its pair too).
                                  # 1,3,5,7 (2 per CCX); workers 5-6 add physical 2,6 -> 3 per CCX at 6 jobs.
                                  # Physical 0 (OS) and physical 4 stay free. Measured: 4 jobs ~114 khc/s per
                                  # process (80% of solo 142); 6 jobs trades per-process speed for aggregate.
STAGGER = 10               # seconds between worker starts (netlist compose is the heavy phase)
WALL_GUARD_PER_FRAME = 10  # subprocess kill guard: maxFrames * this + 600s


def key_of(t):
    return f"{t['suite']}/{t['rom']}".replace("/", "__")


def build_engine():
    print("=== dotnet build (Release) ===", flush=True)
    # decode dotnet's output as UTF-8 with replacement — the default locale codec (cp950 on
    # this box) chokes on box-drawing/non-ASCII bytes in the build log and crashes the reader
    # thread with a scary (but harmless) traceback.
    r = subprocess.run(["dotnet", "build", os.path.join(REPO, "src", "AprVisual.S1"), "-c", "Release"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        print((r.stdout or '')[-2000:], (r.stderr or '')[-2000:])
        sys.exit("BUILD FAILED")


def canary():
    """Pre-flight the engine before spending hours on it. Aborts the run on mismatch.

    Two checks, both trivial next to a full sweep (~70 s together):
      1. the golden checksum — the project's bit-exactness gate.
      2. one short class-A ROM — proves the blargg $6000 verdict path works at all.

    Check 2 exists because of 2026-07-09: relocating the ROMs made LoadSystem()'s
    path heuristic miss, cart-extraram ($6000 work RAM) was never mounted, and EVERY
    class-A test reported detection=none — for a whole 6-hour sweep before anyone
    noticed. A verdict path that is silently dead looks exactly like a slow test.
    See MD/testrom/00_test-fix-knowledge-base.md 3.4.
    """
    print("=== canary: bit-exact checksum + $6000 verdict path ===", flush=True)

    if os.path.isfile(BENCH_ROM):
        r = subprocess.run(["dotnet", DLL, "--benchmark", BENCH_ROM, "--bench-hc", str(BENCH_HC),
                            "--extra-ram", "--system-def-dir", SYSTEM_DEF],
                           capture_output=True, text=True, encoding="utf-8", errors="replace",
                           timeout=900)
        out = (r.stdout or "") + (r.stderr or "")
        if GOLDEN_CKSUM in out:
            print(f"  [ok]   golden checksum {GOLDEN_CKSUM} @ {BENCH_HC//1000}k hc", flush=True)
        else:
            got = next((w.rstrip(",") for w in out.split() if w.startswith("0x")), "(none printed)")
            # ASCII only: this message is read on a broken build, often through a cp950 console.
            sys.exit(f"CANARY FAILED: golden checksum is {got}, expected {GOLDEN_CKSUM}.\n"
                     f"  This build is NOT bit-exact; its results and timings are not comparable.\n"
                     f"  Refusing to start the batch. (Bypass with --no-canary if you know why.)")
    else:
        print(f"  [skip] benchmark ROM not found: {BENCH_ROM}", flush=True)

    if os.path.isfile(CANARY_ROM):
        jp = os.path.join(OUT_DIR, "_canary.json")
        os.makedirs(OUT_DIR, exist_ok=True)
        if os.path.isfile(jp):
            os.remove(jp)
        subprocess.run(["dotnet", DLL, "--test", CANARY_ROM, "--max-frames", str(CANARY_FRAMES),
                        "--reset-hold-extra", "1", "--test-json", jp, "--system-def-dir", SYSTEM_DEF],
                       capture_output=True, timeout=900)
        try:
            d = json.load(open(jp, encoding="utf-8"))
        except Exception:
            d = {}
        if d.get("status") == "pass" and d.get("detection") == "6000":
            print(f"  [ok]   $6000 verdict path (11-special: detection=6000, frame {d.get('frames')})",
                  flush=True)
        else:
            sys.exit(f"CANARY FAILED: the $6000 verdict path is dead "
                     f"(status={d.get('status')}, detection={d.get('detection')}).\n"
                     f"  This almost always means cart-extraram ($6000 work RAM) was not mounted.\n"
                     f"  It is NOT a frame-budget problem: raising --max-frames only runs the\n"
                     f"  failure for longer. Every class-A test would report detection=none.\n"
                     f"  See MD/testrom/00_test-fix-knowledge-base.md 3.4.")
    else:
        print(f"  [skip] canary ROM not found: {CANARY_ROM}", flush=True)


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
    # cap the budget at 1.5x the historical verdict frame (typicalFrames): a working run
    # reaches its verdict well within this, while a hung/regressed run is killed ~1.5x
    # instead of running the full (generous) maxFrames. Measured at the pinned K=1 alignment.
    tf = t.get("typicalFrames")
    if tf:
        mf = min(mf, int(tf * 1.5) + 5)
    # --reset-hold-extra 1: CPU/PPU clock-phase alignment. The netlist's power-on settles into one
    # of the 4 real-hardware CPU-PPU alignments; blargg's NMI-edge tests pass only on some of them.
    # K=1 selects a passing alignment (measured 2026-07-04: K∈{1,3} pass 05-nmi_timing, K∈{0,5} fail).
    # Engine default stays 0 — the benchmark/golden-checksum path is untouched.
    cmd = ["dotnet", DLL, "--test", rompath, "--max-frames", str(mf), "--pin", str(core),
           "--reset-hold-extra", "1",
           "--test-json", jpath, "--test-screenshot", spath, "--system-def-dir", SYSTEM_DEF]
    if t.get("expectedCrcs"):
        cmd += ["--expected-crc", ",".join(t["expectedCrcs"])]
    if t.get("class") == "B":
        cmd += ["--screen-verdict"]
    if t.get("passMarker"):
        cmd += ["--pass-marker", t["passMarker"]]
    if t.get("input"):
        cmd += ["--input", t["input"]]
    if t.get("needsJoypad"):
        # behavioral joypad + u7/u8 tie-rewire (controller / exec_space tests). It is a
        # load-time graph change, so it is scoped per-test: enabling it globally re-rolled
        # the alignment lottery and regressed the ppu_vbl_nmi family.
        cmd += ["--joypad"]
    if t.get("class") in ("B", "C"):
        # B/C verdicts read VRAM directly and can fire before the ROM enables rendering —
        # give the ROM 60 extra frames to present its result screen before the screenshot.
        cmd += ["--shot-delay", "60"]

    guard = mf * WALL_GUARD_PER_FRAME + 600
    t0 = time.time()
    try:
        with open(lpath, "w", encoding="utf-8") as lf:
            p = subprocess.run(cmd, stdout=lf, stderr=subprocess.STDOUT, timeout=guard)
        rc = p.returncode
    except subprocess.TimeoutExpired:
        return ("GUARD", k, f"wall guard {guard}s exceeded (killed)")
    t1 = time.time()
    mins = (t1 - t0) / 60
    if os.path.isfile(jpath):
        try:
            r = json.load(open(jpath, encoding="utf-8"))
            st = r["status"].upper()
            # Enrich with complete timing info so future statistics don't depend on
            # file mtimes: process start/end (wall clock; start includes spawn+load,
            # so elapsedSeconds - wallSeconds ~ load + screenshot overhead), and the
            # pinned core (= worker lane) for concurrency/Gantt analysis.
            iso = lambda ts: time.strftime("%Y-%m-%dT%H:%M:%S", time.localtime(ts))
            r.update(startedAt=iso(t0), finishedAt=iso(t1),
                     startedEpoch=round(t0, 3), finishedEpoch=round(t1, 3),
                     elapsedSeconds=round(t1 - t0, 1), core=core)
            with open(jpath, "w", encoding="utf-8") as fp:
                json.dump(r, fp, indent=1, ensure_ascii=False)
        except Exception:
            st = f"RC{rc}"
        return (st, k, f"{mins:.0f} min")
    return (f"RC{rc}", k, f"{mins:.0f} min, no result json")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=7)   # 7 clean physical cores; core 0 (8th lane) is opt-in via --jobs 8
    ap.add_argument("--filter", default=None, help="substring filter on suite/rom; comma-separated = OR (one combined batch)")
    ap.add_argument("--class", dest="cls", default=None, help="A / A-r / C")
    ap.add_argument("--limit", type=int, default=0, help="run only the first N pending tests")
    ap.add_argument("--rerun", action="store_true", help="ignore existing result JSONs")
    ap.add_argument("--report-only", action="store_true")
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--no-canary", action="store_true",
                    help="skip the pre-flight canary (golden checksum + $6000 verdict path, ~70 s)")
    args = ap.parse_args()

    if args.report_only:
        subprocess.run([sys.executable, os.path.join(SCRIPT_DIR, "build_report.py")], check=True)
        return

    cat = json.load(open(CATALOG, encoding="utf-8"))
    rombase = os.path.join(REPO, cat["romBase"].replace("/", os.sep))
    tests = cat["tests"]
    if args.filter:
        # Comma-separated substrings, OR semantics — run several families as ONE batch so all
        # workers stay busy (separate sequential --filter invocations leave cores idle).
        pats = [f.strip().lower() for f in args.filter.split(",") if f.strip()]
        tests = [t for t in tests if any(pat in f"{t['suite']}/{t['rom']}".lower() for pat in pats)]
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

    # Longest-processing-time-first (LPT) scheduling: dispatch the slowest tests first so
    # they run concurrently with the short ones and the tail doesn't drag out the makespan.
    # Order by expected runtime (typicalFrames, falling back to maxFrames), longest first.
    tests.sort(key=lambda t: t.get("typicalFrames") or t.get("maxFrames", 900), reverse=True)

    if not tests:
        print("nothing to run")
        subprocess.run([sys.executable, os.path.join(SCRIPT_DIR, "build_report.py")], check=True)
        return

    if not args.no_build:
        build_engine()
    if not os.path.isfile(DLL):
        sys.exit(f"engine dll not found: {DLL}")
    if not args.no_canary:
        canary()   # never let a 6-hour sweep start on a dead verdict path

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
