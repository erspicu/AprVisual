#!/bin/bash
# C# S1 frame-dump (macOS arm64, full_palette). Arg 1 = frame count (default 50).
DIR="$(cd "$(dirname "$0")" && pwd)"
N="${1:-50}"
chmod +x "$DIR/mac/AprVisual.S1" 2>/dev/null
xattr -dr com.apple.quarantine "$DIR/mac/AprVisual.S1" 2>/dev/null
mkdir -p "$DIR/screenshots/csharp"
"$DIR/mac/AprVisual.S1" --frame-dump "$DIR/roms/full_palette.nes" --frame-count "$N" --out-dir "$DIR/screenshots/csharp" --extra-ram --system-def-dir "$DIR/data/system-def"
read -p "Press Enter to close..."
