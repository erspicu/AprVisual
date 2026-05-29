#!/bin/bash
# Rust S1 frame-dump (macOS arm64, full_palette). Arg 1 = frame count (default 50).
# Builds the Rust binary from rust-src/ on first run (needs cargo).
DIR="$(cd "$(dirname "$0")" && pwd)"
N="${1:-50}"
BIN="$DIR/mac/wire_s1"
if [ ! -x "$BIN" ]; then
    echo "# Rust binary not found - building from rust-src/ (needs cargo) ..."
    if ! command -v cargo >/dev/null 2>&1; then
        echo "# ERROR: cargo not found. Install Rust from https://rustup.rs then re-run."
        read -p "Press Enter to close..."; exit 1
    fi
    ( cd "$DIR/rust-src" && cargo build --release ) || { echo "# cargo build failed"; read -p "Press Enter..."; exit 1; }
    mkdir -p "$DIR/mac"
    cp "$DIR/rust-src/target/release/wire_s1" "$BIN"
fi
mkdir -p "$DIR/screenshots/rust"
"$BIN" framedump "$DIR/snapshot/full_palette.aprsnap" "$N" "$DIR/screenshots/rust"
read -p "Press Enter to close..."
