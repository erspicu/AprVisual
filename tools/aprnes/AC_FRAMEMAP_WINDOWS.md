# AC sub-test frame windows — S1 range-run budget (from AprNes)

Generated 2026-07-19 by `AprNes --wait-ac --ac-frame-map` on the full 141-test
`AccuracyCoin.nes` (completed at AprNes frame 4819; the switch-level S1A run8 completed
at 4925 — a ~100-frame offset absorbed by the safety margin). Frame numbers are AprNes's;
treat as **approximate** for S1A (sequence is identical, positions drift slightly).

## The 4 shims still UNDECIDABLE in isolation, and where their defenders complete in-suite

| shim | in-suite defended sub-test | AprNes frame | early-stop window `[F(i-1)−40, F(i)+25]` | S1A run-to cost (~6 s/frame) |
|---|---|---|---|---|
| **DL** | **OpenBus** | **52** | [9, 77] | ⚡ **~8 min** (0→80) |
| | ControllerClocking | 1481 | [1439, 1506] | ~2.5 h |
| | PPUOpenBus | 1611 | [1447, 1636] | ~2.7 h |
| | InternalDataBus | 4812 | [4769, 4837] | ~8 h (or resume) |
| **Dmc4015Abort** | **ExplicitDMAAbort** | **1347** | [1303, 1372] | ~2.3 h (or resume) |
| | ImplicitDMAAbort | 1357 | [1307, 1382] | ~2.3 h |
| | InternalDataBus | 4812 | [4769, 4837] | ~8 h (or resume) |
| **OamBlankEdge** | Address2004_Behavior | 3827 | [3650, 3852] | ~6.5 h (or resume ~3650) |
| | StaleSpriteShiftRegs | 4133 | [3972, 4158] | ~7 h (or resume ~3972) |
| **dot-339** | **StaleSpriteShiftRegs** | **4133** | [3972, 4158] | resume ~3972→4158 (~20 min) |

## How to use for a mechanism/shim confirmation
The window gives the **budget** (how far to run); whether a given sub-test **discriminates**
the shim in-suite (its control FAILs there) is what the range-run **verdict** answers.
Strategy = **cheapest-first**: try each shim's earliest defended sub-test with a run-to /
snapshot-resume; escalate to a later one only if it doesn't discriminate.
- **Early defenders** (DL@OpenBus f52, Dmc4015Abort@ExplicitAbort f1347): run S1A `0→F+safety`.
- **Late defenders** (dot-339 / OamBlankEdge @ StaleSprite f4133): run one snapshotted AC once,
  then `--resume` from a snapshot just before the window (clean iff the shim/mechanism has not
  fired before it; localized-firing shims qualify — see MD/S1a decidability notes).
