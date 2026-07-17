#!/usr/bin/env python3
"""Watch a long --ac-verdict run and mail progress, so an 8-hour black box becomes observable.

The engine (AprVisual.S1 --progress-frames N --progress-dir DIR) appends one JSON line per
checkpoint to DIR/progress.jsonl and keeps DIR/latest.png as the current screen. This script
turns that into mail:

  * every EVERY_FRAMES simulated frames  -> a progress mail with the latest screenshot attached
  * when the run finishes                -> a final mail with the verdict + the final screen
  * if the process dies without a verdict -> an alert (a silent death must not look like "still running")

Deliberately frame-driven, not clock-driven: the interesting axis is simulated progress. A stalled
engine therefore sends nothing, and the liveness check below is what catches that.

Usage:
  python tools/testrom/ac_watch.py --dir tools/testrom/out/ac --pid 1234 [--every-frames 600]
"""
import argparse, json, os, subprocess, sys, time
from datetime import datetime, timezone

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO       = os.path.abspath(os.path.join(SCRIPT_DIR, "..", ".."))
SEND_MAIL  = os.path.join(REPO, "tools", "send_mail.py")

EXPECT_FRAME = 4870        # AprNes-measured completion frame (bisected on the $07F0 magic)
POLL_SEC     = 60
STALL_SEC    = 15 * 60     # no new checkpoint for this long, with the process alive => warn once


def mail(subject, body, attach=None):
    cmd = [sys.executable, SEND_MAIL, "--subject", subject, "--body", body, "--html"]
    if attach:
        files = [a for a in attach if os.path.isfile(a)]
        if files:
            cmd += ["--attach"] + files
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8",
                           errors="replace", timeout=180)
        ok = r.returncode == 0
        print(f"[mail] {'sent' if ok else 'FAILED'}: {subject}", flush=True)
        if not ok:
            print((r.stdout or "") + (r.stderr or ""), flush=True)
    except Exception as e:
        print(f"[mail] EXC: {e}", flush=True)


def alive(pid):
    if not pid:
        return True
    out = subprocess.run(["tasklist", "/FI", f"PID eq {pid}"], capture_output=True,
                         text=True, errors="replace").stdout
    return str(pid) in out


def tail_checkpoint(path):
    """Last complete JSON line, or None. Tolerates a torn final line mid-write."""
    if not os.path.isfile(path):
        return None
    try:
        with open(path, encoding="utf-8") as f:
            lines = [ln for ln in f.read().splitlines() if ln.strip()]
    except OSError:
        return None
    for ln in reversed(lines):
        try:
            return json.loads(ln)
        except json.JSONDecodeError:
            continue
    return None


def fmt_eta(cp):
    frame, spf = cp["frame"], cp["secPerFrame"]
    left = max(0, EXPECT_FRAME - frame) * spf
    eta  = datetime.now().timestamp() + left
    return (f"{frame:,} / ~{EXPECT_FRAME:,} frames ({100.0*frame/EXPECT_FRAME:.1f}%)",
            f"{spf:.2f} s/frame", f"{left/3600:.1f} h left",
            datetime.fromtimestamp(eta).strftime("%m-%d %H:%M"))


def progress_body(cp, note=""):
    pct, spf, left, eta = fmt_eta(cp)
    ec = cp["debugEc"]
    ec_s = f"${ec:02X}" + (" (auto-run in progress)" if ec == 0xFF else " (still in menu init!)")
    return (f"{note}\n\n"
            f"**AccuracyCoin unattended, on AprVisual.S1**\n\n"
            f"- progress: {pct}\n"
            f"- simulated: {cp['simSec']:.1f} s of console time\n"
            f"- speed: {spf} ({cp['hc']:,} half-cycles, {cp['wallSec']/3600:.2f} h wall)\n"
            f"- Debug_EC: {ec_s}\n"
            f"- estimate: {left}, done around **{eta}**\n\n"
            f"The attached screenshot is the emulated screen at this frame.\n"
            f"Final verdict comes from CPU RAM $07F0-$07F5, not from the picture.\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--pid", type=int, default=0)
    ap.add_argument("--every-frames", type=int, default=600)
    ap.add_argument("--result-json", default=None)
    ap.add_argument("--final-shot", default=None)
    a = ap.parse_args()

    d       = os.path.abspath(a.dir)
    jsonl   = os.path.join(d, "progress.jsonl")
    latest  = os.path.join(d, "latest.png")
    result  = a.result_json or os.path.join(d, "AccuracyCoin.json")
    shot    = a.final_shot or os.path.join(d, "AccuracyCoin.png")

    print(f"[watch] dir={d} pid={a.pid} every={a.every_frames} frames", flush=True)
    sent_bucket = -1
    last_frame, last_change = None, time.time()
    stall_warned = False

    while True:
        time.sleep(POLL_SEC)

        # 1. finished? the result JSON is the authority, not the screen.
        if os.path.isfile(result):
            time.sleep(3)   # let the engine finish writing json + png
            try:
                r = json.load(open(result, encoding="utf-8"))
            except Exception as e:
                r = {"status": f"(unreadable: {e})"}
            st  = r.get("status", "?")
            txt = r.get("resultText", "")
            body = (f"**AccuracyCoin unattended -- run finished**\n\n"
                    f"- status: **{st.upper()}**\n"
                    f"- result: {txt}\n"
                    f"- detection: {r.get('detection')}\n"
                    f"- frames: {r.get('frames'):,} | sim {r.get('simSeconds')} s\n"
                    f"- wall: {(r.get('wallSeconds') or 0)/3600:.2f} h | {r.get('halfCycles',0):,} hc\n"
                    f"- engine: {r.get('engineVersion')}\n\n"
                    f"Config: no cart-extraram (open bus preserved), global test-mode shims on, K=1.\n")
            mail(f"[AC] finished: {st.upper()} -- {txt}", body,
                 [shot if os.path.isfile(shot) else latest])
            print("[watch] done", flush=True)
            return

        # 2. died without a verdict? a silent death must not read as "still running".
        if not alive(a.pid):
            cp = tail_checkpoint(jsonl)
            where = f"last checkpoint: frame {cp['frame']:,}" if cp else "no checkpoint was ever written"
            mail("[AC] ALERT: process gone, no verdict",
                 f"The run exited without producing {os.path.basename(result)}.\n\n{where}\n",
                 [latest])
            print("[watch] process gone", flush=True)
            return

        cp = tail_checkpoint(jsonl)
        if not cp:
            continue

        # 3. stalled? alive but not advancing.
        if cp["frame"] != last_frame:
            last_frame, last_change, stall_warned = cp["frame"], time.time(), False
        elif not stall_warned and time.time() - last_change > STALL_SEC:
            stall_warned = True
            mail("[AC] WARNING: no progress",
                 f"Alive, but no new checkpoint for {STALL_SEC//60} min.\n\n" + progress_body(cp), [latest])

        # 4. routine progress, every N simulated frames.
        bucket = cp["frame"] // a.every_frames
        if bucket > sent_bucket:
            sent_bucket = bucket
            pct, *_ = fmt_eta(cp)
            mail(f"[AC] progress: {pct}", progress_body(cp), [latest])


if __name__ == "__main__":
    main()
