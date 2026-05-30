AprVisual S1 — portable cross-platform benchmark package
=========================================================

This folder is self-contained and portable. Copy it anywhere and run.

Layout
  win/   AprVisual.S1.exe   C# engine, self-contained win-x64 (no .NET install needed)
         wire_s1.exe        Rust engine, native win-x64
  mac/   AprVisual.S1       C# engine, self-contained macOS arm64 (no .NET install needed)
         wire_s1            Rust engine — built on first ./run_rust.sh (see below)
  rust-src/                 Rust engine SOURCE (portable; Mac builds from here)
  data/system-def/          netlist .js modules (C# input)
  snapshot/                 .aprsnap snapshots (Rust input)
  roms/                     full_palette.nes test ROM (C# input)
  screenshots/              frame-dump PNG output lands here

Windows (double-click or run in cmd)
  run_csharp.bat  [hc]      C# benchmark   (default 300000 hc)
  run_rust.bat    [hc]      Rust benchmark
  shot_csharp.bat [frames]  C# frame-dump  (default 50 frames -> screenshots\csharp)
  shot_rust.bat   [frames]  Rust frame-dump

macOS Apple Silicon (Terminal)
  chmod +x *.sh             (once)
  ./run_csharp.sh  [hc]
  ./run_rust.sh    [hc]
  ./shot_csharp.sh [frames]
  ./shot_rust.sh   [frames]

Notes
  * C#: binaries bundle the .NET 10 runtime (self-contained + trimmed, ~12-13 MB).
    No .NET install required on Windows or Mac.
  * C# on Mac is unsigned; the .sh scripts auto-run
    `xattr -dr com.apple.quarantine` on it. If Gatekeeper still blocks it, run:
        xattr -dr com.apple.quarantine mac/AprVisual.S1
  * Rust on Mac: cannot be cross-compiled from the Windows build host, so it
    ships as source in rust-src/. The first ./run_rust.sh or ./shot_rust.sh
    runs `cargo build --release` automatically — install Rust from
    https://rustup.rs first. The built binary is cached in mac/wire_s1.
  * Rust on Windows is prebuilt (win/wire_s1.exe).
  * Each run ends with a real-time-gap PERFORMANCE block: hc/s, % of NES NTSC
    real-time, "Nx too slow", and seconds to render one frame.
