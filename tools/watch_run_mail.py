#!/usr/bin/env python3
"""watch_run_mail.py - mail a status summary at every progress checkpoint of a long run.

Usage:
    python tools/watch_run_mail.py <run_dir> [label]

Expects <run_dir>/progress/progress.jsonl (one line per --progress-frames
checkpoint) and <run_dir>/run.log. Sends one email per new checkpoint line
(attaching progress/latest.png when present) and a final email when the run
log shows the AC completion verdict or the log stops growing after a verdict
line. Uses tools/send_mail.py (Gmail app password, default recipient).
"""
import sys, os, time, subprocess

run_dir = sys.argv[1] if len(sys.argv) > 1 else "temp/ac/run5b_joyON"
label = sys.argv[2] if len(sys.argv) > 2 else os.path.basename(run_dir.rstrip("/\\"))
prog = os.path.join(run_dir, "progress", "progress.jsonl")
png = os.path.join(run_dir, "progress", "latest.png")
runlog = os.path.join(run_dir, "run.log")
mailer = os.path.join(os.path.dirname(os.path.abspath(__file__)), "send_mail.py")

def send(subject, body, attach_png):
    cmd = [sys.executable, mailer, "--subject", subject, "--stdin"]
    if attach_png and os.path.exists(png):
        cmd += ["--attach", png]
    try:
        subprocess.run(cmd, input=body.encode("utf-8"), timeout=120)
    except Exception as e:
        print(f"mail failed: {e}", flush=True)

seen = 0
send(f"[{label}] watcher armed", f"run dir: {run_dir}\nmail per progress checkpoint; final mail on completion.", False)
while True:
    time.sleep(60)
    done = False
    tail = ""
    if os.path.exists(runlog):
        with open(runlog, errors="replace") as f:
            tail = f.read()[-3000:]
        if "AccuracyCoin:" in tail or "=== flagship exited" in tail:
            done = True
    lines = []
    if os.path.exists(prog):
        with open(prog, errors="replace") as f:
            lines = f.read().strip().splitlines()
    if len(lines) > seen:
        seen = len(lines)
        body = f"checkpoint {seen} of run [{label}]\n\nlatest progress lines:\n" + "\n".join(lines[-3:])
        # a couple of orientation lines from the run log (Debug_EC transitions etc.)
        body += "\n\nrun.log tail:\n" + tail[-800:]
        send(f"[{label}] checkpoint {seen}", body, True)
    if done:
        send(f"[{label}] RUN COMPLETE", f"final run.log tail:\n{tail}", True)
        print("run complete; watcher exiting", flush=True)
        break
