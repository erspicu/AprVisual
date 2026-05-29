AprVisual S1 — macOS (Apple Silicon / arm64) benchmark package
================================================================

Contents
  csharp/AprVisual.S1     C# S1 engine, self-contained arm64 Mach-O
                          (the .NET 10 runtime is bundled — no install needed)
  rust-src/               Rust S1 engine SOURCE (built on first run; see below)
  data/system-def/        netlist .js modules (C# input)
  snapshot/               .aprsnap snapshots (Rust input)
  roms/                   full_palette.nes test ROM (C# input)
  screenshots/            frame-dump PNG output lands here

  run_csharp.sh   N       C# benchmark, N = hc count (default 200000)
  run_rust.sh     N       Rust benchmark (builds Rust on first run)
  shot_csharp.sh  N       C# frame-dump, N = frame count (default 50)
  shot_rust.sh    N       Rust frame-dump (builds Rust on first run)

How to run (Terminal)
  cd /path/to/AprVisualBenchMarkMac
  chmod +x *.sh                 # make the scripts executable (once)
  ./run_csharp.sh               # default 200000 hc
  ./run_rust.sh 200000
  ./shot_csharp.sh 50           # 50 frames -> screenshots/csharp
  ./shot_rust.sh 50

Notes
  * C#: the binary is unsigned. If macOS Gatekeeper blocks it, the scripts
    already run `xattr -dr com.apple.quarantine` on it; if you still get
    "cannot be opened", run once:
        xattr -dr com.apple.quarantine csharp/AprVisual.S1
  * Rust: cannot be cross-compiled from the Windows build host, so it ships
    as source. The first run_rust.sh / shot_rust.sh call runs
    `cargo build --release` automatically — you need Rust installed
    (https://rustup.rs). After that the binary is cached in rust/.
  * Output ends with a real-time-gap PERFORMANCE block (hc/s, % of NES
    NTSC real-time, "Nx too slow", s/frame).
