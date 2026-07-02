#!/usr/bin/env python3
"""Frame-calibration sweep using the AprNesRef behavioral emulator (fast oracle).

Runs every S1-supported ROM (mapper 0, non-PAL — 139) through the instrumented
AprNesRef (--calib-json) and merges the per-ROM records into
tools/testrom/calibration_ref.json. The record answers, for each ROM:

  - verdictFrame      when AprNes reached its verdict ($6000 or 90-frame stability)
  - firstMarkerFrame  when the terminal screen marker FIRST became visible
                      (for B-class: this is where S1's per-frame nametable scan
                       would stop — typically ~190 frames earlier than stability)
  - lastChangeFrame   last frame the screen still changed (ambiguity check:
                       marker visible while screen still changing = risky ROM)
  - finalCrc          on-screen CRC (auto-builds the C-class expected-CRC table)
  - passed/resultCode AprNes ground truth for the comparison column

Usage: python tools/testrom/calibrate_ref.py [--jobs 4] [--filter substr]
"""
import argparse, json, os, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO       = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
REF        = os.path.join(REPO, "AprNesRef")
EXE        = os.path.join(REF, "AprNes", "bin", "Debug", "AprNes.exe")
ROMBASE    = os.path.join(REPO, "nes-test-roms-master", "checked")
OUT_JSON   = os.path.join(SCRIPT_DIR, "calibration_ref.json")
TMP_DIR    = os.path.join(SCRIPT_DIR, "out", "calib")

# expected CRCs (lets the 2 C-class ROMs exit early instead of running to max-wait)
EXPECTED_CRCS = {
    "dmc_dma_during_read4/dma_2007_read.nes":    "159A7A8F,5E3DF9C4",
    "dmc_dma_during_read4/double_2007_read.nes": "85CFD627,F018C287,440EF923,E52F41A5",
}


def supported_roms():
    import glob
    roms = []
    for path in sorted(glob.glob(os.path.join(ROMBASE, "**", "*.nes"), recursive=True)):
        with open(path, "rb") as fp:
            b = fp.read(16)
        rel = os.path.relpath(path, ROMBASE).replace("\\", "/")
        d = rel.split("/")[0]
        if len(b) < 16 or b[:4] != b"NES\x1a":
            continue
        mapper = (b[6] >> 4) | (b[7] & 0xF0)
        # S1 scope: NROM + CNROM. read_joy3 (m3) excluded — needs controller input S1 doesn't inject.
        if mapper not in (0, 3) or d in ("pal_apu_tests", "read_joy3"):
            continue
        roms.append(rel)
    return roms


def run_one(rel):
    rompath = os.path.join(ROMBASE, rel.replace("/", os.sep))
    cpath = os.path.join(TMP_DIR, rel.replace("/", "__") + ".json")
    os.makedirs(os.path.dirname(cpath), exist_ok=True)
    cmd = [EXE, "--rom", rompath, "--wait-result", "--max-wait", "60", "--calib-json", cpath]
    if rel in EXPECTED_CRCS:
        cmd += ["--expected-crc", EXPECTED_CRCS[rel]]
    try:
        subprocess.run(cmd, capture_output=True, timeout=300)
    except subprocess.TimeoutExpired:
        return (rel, None, "wall timeout")
    if not os.path.isfile(cpath):
        return (rel, None, "no calib json")
    try:
        return (rel, json.load(open(cpath, encoding="utf-8")), "")
    except Exception as e:
        return (rel, None, f"bad json: {e}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--filter", default=None)
    args = ap.parse_args()

    if not os.path.isfile(EXE):
        sys.exit(f"AprNesRef exe not found: {EXE} (build it first)")

    roms = supported_roms()
    if args.filter:
        roms = [r for r in roms if args.filter.lower() in r.lower()]
    print(f"=== calibrating {len(roms)} ROMs, {args.jobs} threads ===", flush=True)

    t0 = time.time()
    # --filter runs MERGE into the existing file (a filtered sweep must not clobber the full one)
    merged = {}
    if args.filter and os.path.isfile(OUT_JSON):
        merged = json.load(open(OUT_JSON, encoding="utf-8")).get("roms", {})
    with ThreadPoolExecutor(max_workers=args.jobs) as ex:
        for rel, rec, err in ex.map(run_one, roms):
            if rec is None:
                print(f"  ERR  {rel}: {err}", flush=True)
                merged[rel] = {"error": err}
            else:
                rec.pop("schema", None)
                rec.pop("rom", None)
                merged[rel] = rec
                print(f"  {'PASS' if rec.get('passed') else 'FAIL':4s} {rel}: verdict f{rec.get('verdictFrame')} "
                      f"marker f{rec.get('firstMarkerFrame')} ({rec.get('firstMarkerKind')}) "
                      f"det={rec.get('detection')}", flush=True)

    with open(OUT_JSON, "w", encoding="utf-8") as fp:
        json.dump({"schema": "aprvisual-calibration-ref/1",
                   "source": "AprNesRef (instrumented AprNes, --calib-json)",
                   "generated": time.strftime("%Y-%m-%d %H:%M:%S"),
                   "roms": merged}, fp, indent=1, ensure_ascii=False)

    n = len(merged)
    ok = sum(1 for v in merged.values() if "error" not in v)
    print(f"=== done: {ok}/{n} calibrated in {time.time()-t0:.0f}s -> {OUT_JSON} ===")


if __name__ == "__main__":
    main()
