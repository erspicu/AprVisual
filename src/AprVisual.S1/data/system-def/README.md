# system-def (tracked subset)

The full system-def module set (netlist data + MetalNES-format board defs) is
NOT vendored (licensing — see the repo README / CLAUDE.md); supply it locally,
e.g. under AprVisualBenchMark/data/system-def/ (gitignored).

This directory tracks only modules AUTHORED BY THIS PROJECT:

- `nes-pad-behavioral.js` — connector-only controller variant used by the
  behavioral joypad handler (WireCore.EnableJoypadHandler, test mode).
  Copy it next to the rest of your system-def set.
