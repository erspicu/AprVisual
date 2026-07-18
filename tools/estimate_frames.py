#!/usr/bin/env python3
"""estimate_frames.py -- estimate a test ROM's frame budget (and switch-level wall time)
by running the fast AprNes oracle first.

The switch-level S1 engine runs at ~7 s/frame, so a test that needs N frames to reach
its verdict costs ~N x 7 s of wall time. Guessing --max-frames blindly either truncates
the test (too low) or wastes hours (too high) and risks a timeout that isn't calibrated.
AprNes runs the same ROM at ~60 fps (~400x faster) and reports the exact frame the verdict
lands on. This wraps that into a budget suggestion.

Detection:
  - AccuracyCoin ROMs (no $6000 protocol): --pass-on-stable reports "Screen stable at frame N".
  - blargg ROMs ($6000 protocol): --wait-result reports PASS/FAIL and the frame.

Usage:
  python tools/estimate_frames.py <rom.nes> [<rom2.nes> ...]
  python tools/estimate_frames.py --sec-per-frame 7 --margin 1.25 <rom.nes>
  python tools/estimate_frames.py --aprnes path/to/AprNes.exe <rom.nes>

Prints, per ROM: completion frame, suggested --max-frames, estimated S1 wall time.
"""
import argparse, os, re, subprocess, sys

DEFAULT_APRNES = "ref/AprNes/bin/Release/AprNes.exe"
STABLE_RE = re.compile(r"stable at frame (\d+)", re.I)
TIMEOUT_RE = re.compile(r"[Tt]imeout at frame (\d+)")


def run_aprnes(exe, rom, max_wait):
    exe = os.path.abspath(exe); rom = os.path.abspath(rom)
    try:
        p = subprocess.run([exe, "--rom", rom, "--wait-result", "--pass-on-stable",
                            "--max-wait", str(max_wait)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace",
                           timeout=max_wait + 30)
    except subprocess.TimeoutExpired:
        return None, "aprnes-wallclock-timeout", ""
    out = (p.stdout or "") + (p.stderr or "")
    verdict = "?"
    m = re.search(r"^(PASS|FAIL\(\d+\))", out, re.M)
    if m:
        verdict = m.group(1)
    fm = STABLE_RE.search(out)
    if fm:
        return int(fm.group(1)), verdict + " (stable)", out
    tm = TIMEOUT_RE.search(out)
    if tm:
        return int(tm.group(1)), verdict + " (ran to max-wait -- no stable verdict; raise --max-wait)", out
    return None, verdict + " (no frame reported)", out


def fmt_time(sec):
    if sec < 90:
        return f"{sec:.0f} s"
    if sec < 5400:
        return f"{sec/60:.1f} min"
    return f"{sec/3600:.2f} h"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("roms", nargs="+")
    ap.add_argument("--aprnes", default=DEFAULT_APRNES)
    ap.add_argument("--sec-per-frame", type=float, default=7.0, help="S1 switch-level wall per frame (default 7)")
    ap.add_argument("--margin", type=float, default=1.5, help="safety multiplier on the frame budget + timeout (default 1.5)")
    ap.add_argument("--max-wait", type=int, default=180, help="AprNes wall-clock cap in seconds (default 180)")
    a = ap.parse_args()

    if not os.path.exists(a.aprnes):
        print(f"error: AprNes not found at {a.aprnes} (pass --aprnes)", file=sys.stderr)
        return 2

    print(f"{'ROM':<42} {'verdict@AprNes':<30} {'frame':>6} {'max-frames':>11} {'~S1 wall':>9} {'timeout(1.5x)':>13}")
    print("-" * 116)
    for rom in a.roms:
        name = os.path.basename(rom)
        if not os.path.exists(rom):
            print(f"{name:<42} {'(file not found)':<30}")
            continue
        frame, verdict, _ = run_aprnes(a.aprnes, rom, a.max_wait)
        if frame is None:
            print(f"{name:<42} {verdict:<30}")
            continue
        budget = int(frame * a.margin) + 10          # --max-frames budget (1.5x + 10)
        wall = frame * a.sec_per_frame               # honest estimate at the true frame
        timeout = wall * a.margin                     # timeout carries the 1.5x safety on top
        print(f"{name:<42} {verdict:<30} {frame:>6} {budget:>11} {fmt_time(wall):>9} {fmt_time(timeout):>13}")
    print("-" * 116)
    print(f"max-frames = frame x {a.margin:g} + 10 ; ~S1 wall = frame x {a.sec_per_frame:g}s ; "
          f"timeout = ~S1 wall x {a.margin:g} (1.5x safety, per user).  Set the runner timeout from the last column.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
