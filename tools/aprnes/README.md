# AprNes — self-use design-tool reference copy (frame-budgeting fork)

**This is a reference copy, not the canonical AprNes.** AprNes (the user's cycle-accurate
C# NES emulator) has its **own git repo**; this is a source snapshot vendored into AprVisual
so our design tooling — specifically the **AC frame-budgeting** additions below — is captured
alongside the project that consumes it. Build artifacts (`bin/`, `obj/`) and the nested
`.git` are excluded to keep it small (~2.4 MB source).

## Why it lives here
The switch-level **AprVisual.S1/S1A** engine runs at ~6 s wall per frame, so a full 141-test
AccuracyCoin run is ~8 h. AprNes runs the same ROM in **~20 s**. We use AprNes purely to
**measure WHEN each AC sub-test completes**, then budget tight from/to windows so S1A confirms
a specific sub-test by running/resuming ~`[F(i-1), F(i)] + safety` instead of the full ~4925
frames. See `AC_FRAMEMAP_WINDOWS.md`.

## Our additions (see `OUR_CHANGES_ac_budget.patch`)
Two flags in `AprNes/NesCore/TestRunnerCore.cs`:
- **`--wait-ac`** — exit at the frame the AccuracyCoin completion block (`DE B0 61` @ `$07F0`)
  is published, and report the real pass/fail (these ROMs use `$07F0`, not `$6000`).
- **`--ac-frame-map <path>`** — every frame, record the frame each result byte `$0400-$04FF`
  first goes non-zero (= that sub-test completed); dump the table as CSV at the end. This is
  the per-sub-test completion-frame map.

## Reproduce
```
# build (own repo has bin/obj; here rebuild from source)
MSBuild AprNes/AprNes.csproj -p:Configuration=Release -p:Platform=x64

# profile the full 141-test AC -> per-subtest completion frames
AprNes.exe --rom AprAccuracyCoinUnattended/AccuracyCoin.nes --wait-ac \
           --ac-frame-map ac_full141_frames.csv --max-wait 600
```
Output columns: `offset,addr,first_frame` (addr = `$0400 + offset`). Join `addr -> subtest name`
with `tools/testrom/ac_snap_results.py:result_names()`.

## Files
- `ac_full141_frames.csv` — the reference output (141 sub-test completion frames, AprNes run).
- `OUR_CHANGES_ac_budget.patch` — isolates our `--wait-ac` + `--ac-frame-map` additions vs upstream.
- `AC_FRAMEMAP_WINDOWS.md` — the derived frame windows for the undecidable shims' defended tests.
