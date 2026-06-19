# Pi 5 (ARM Cortex-A76) perf-analysis workflow

Installed 2026-06-19: `perf` 6.18 (+ kernel auto-upgraded to 6.18.34, activates on next reboot),
`cpupower`, `objdump` (binutils 2.44). .NET 11 at `~/.dotnet`. Test S1 (`~/aprvisual/src/AprVisual.S1`).

## Frequency lock (record current → lock → restore)
    ~/pi_freq_lock.sh        # records governor to ~/.cpufreq_baseline, sets performance (pinned 2.4 GHz)
    <run tests>
    ~/pi_freq_restore.sh     # restores recorded governor (reboot also restores default ondemand)
(scripts also in this dir; copy to ~ on the Pi)

## Cycles / IPC / i-cache / d-cache / branch (sudo for PMU; paranoid=2)
    sudo perf stat -e cycles,instructions,L1-dcache-loads,L1-dcache-load-misses,L1-icache-load-misses,branch-misses \
      env DOTNET_ROOT=$HOME/.dotnet $HOME/.dotnet/dotnet "<S1.dll>" --benchmark "<rom>" --bench-hc 400000 --extra-ram --system-def-dir "<sd>"
(use `env`, NOT `bash -c` — the bundled ROM path has parens that break a shell; quote all paths)

## IL size + ARM-native JIT code size
    DOTNET_JitDisasmSummary=1 DOTNET_TieredCompilation=0 $HOME/.dotnet/dotnet "<S1.dll>" --benchmark "<rom>" --bench-hc 5000 ... \
      | grep "WireCore:ProcessQueue()"
## ARM disassembly of a method
    DOTNET_JitDisasm='*ProcessQueue*' DOTNET_TieredCompilation=0 $HOME/.dotnet/dotnet "<S1.dll>" ...
## Per-function / per-instruction (with .NET JIT symbols — better than Windows uProf)
    DOTNET_PerfMapEnabled=1 ... then  sudo perf record -g -- env ... dotnet <dll> ... ;  sudo perf report / perf annotate

## Baseline (2026-06-19, S1 400k hc @ locked 2.4 GHz, kernel 6.12, golden 0x9174E19D961CB6E5)
IPC 2.02 · L1-dcache miss 1.16% (197M/17.0G) · L1-icache miss 38.7M · branch-miss 247M · ProcessQueue IL 130 / ARM-native 4692 B.
NB perf gives REAL counter IPC (2.02), unlike the x64 uProf sample-based ~1.0 artifact.
