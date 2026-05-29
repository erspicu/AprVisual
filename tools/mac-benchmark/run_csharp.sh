#!/bin/bash
# C# S1 benchmark (macOS arm64). Arg 1 = hc count (default 300000).
DIR="$(cd "$(dirname "$0")" && pwd)"
HC="${1:-300000}"
chmod +x "$DIR/mac/AprVisual.S1" 2>/dev/null
xattr -dr com.apple.quarantine "$DIR/mac/AprVisual.S1" 2>/dev/null
"$DIR/mac/AprVisual.S1" --benchmark "$DIR/roms/full_palette.nes" --bench-hc "$HC" --system-def-dir "$DIR/data/system-def"
read -p "Press Enter to close..."
