# tools/cpp-naive — C++ port of the original visual6502 algorithm

`naive.cpp` is a faithful C/C++ transliteration of the original visual6502 `chipsim.js`
recursive group-walk — the C++ sibling of `src/AprVisual.etc/Sim/NaiveSim.cs` (C# naive) and
`tools/visual6502-node` (the JavaScript original). It exists to nail down the **language axis**
of the cross-CPU comparison:

```
JS naive  →  C# naive  →  C++ naive     (same algorithm, three languages — pure language)
C# naive  →  AprVisual (event-driven + prunes)   (same language — pure algorithm)
```

To guarantee identical netlist data (no `.js` parser in C++, no chance of a different netlist), it
loads the netlist **exported by the C# tool** rather than parsing the raw `.js`:

```sh
# 1) export the raw naive netlist (identical nodes/transistors/pins to the C# naive)
dotnet run -c Release --project src/AprVisual.etc -- --export-netlist tools/cpp-naive/6502.netlist.txt --chip 6502
# 2) build
clang++ -O3 -std=c++17 tools/cpp-naive/naive.cpp -o tools/cpp-naive/naive.exe
# 3) run (NOP sled)
tools/cpp-naive/naive.exe 6502 tools/cpp-naive/6502.netlist.txt 1000000 50000 5
```

## Result (2026-06-15, this machine, NOP sled)

| Chip | JS naive | C# naive | **C++ naive** | C++/C# | AprVisual (C# ours) |
|------|---------:|---------:|--------------:|-------:|--------------------:|
| 6502 | 249  | 17,914 | **26,239** | 1.46× | 149,736 |
| 6800 | 149  | 12,582 | **17,137** | 1.36× | 88,313  |
| z80  | 166  | 12,255 | **18,452** | 1.51× | 62,952  |

**The language headline:** at the *same* naive algorithm, native C++ is only **~1.4–1.5× faster than
managed C#** — so C# is not the bottleneck; the slow JS original is purely the **interpreter**
(~70–85× slower than C#). The algorithmic work of the project (naive → AprVisual, same language) is
the ~5–8× factor. Full write-up: `MD/note/2026-06-15-…前置研究.md` §8.8.

Build artifacts (`*.netlist.txt`, `naive.exe`) are git-ignored (licensed-derived data + local binary).
