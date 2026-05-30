#!/bin/bash
# Rust S1 benchmark (macOS arm64). Arg 1 = hc count (default 300000).
# Rust can't be cross-compiled from the Windows build host, so it ships as
# source in rust-src/ and is built here on first run (needs cargo / rustup).
DIR="$(cd "$(dirname "$0")" && pwd)"
HC="${1:-300000}"
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
"$BIN" bench "$DIR/snapshot/full_palette.aprsnap" "$HC" "$DIR/log"
read -p "Press Enter to close..."
