# AprVisual

**Turning a Visual6502-style switch-level NES netlist into an analyzable, verifiable, executable logic model — and ultimately a bit-parallel kernel that simulates many NES instances at once.**

The interesting part isn't the simulator; it's the **translation pipeline**:

```
silicon connectivity (segdefs / transdefs / nodenames)
   →  transistor + wire graph
   →  switch-level event simulation  (S1 — reference semantics)
   →  boolean / sequencing IR        (S2 — per-node next-state expressions)
   →  optimized, mostly-acyclic IR   (S3 — loop/SCC dissolution, coverage push)
   →  bit-sliced codegen (C# / LLVM) (S4 — many instances in parallel; NVPTX → GPU)
```

Every stage is gated by a **per-node equivalence check** against the previous one, so the fast backends are provably the same logic as the raw switch-level simulation.

Scope: the NES-001 board — the **2A03** (a modified 6502 + the APU) and the **2C02** (PPU) — plus the board's support TTL, NROM cartridges only.

---

## Status

| Stage | What | State |
|---|---|---|
| **S1** | C# rewrite of the MetalNES switch-level engine (`WireCore`: `.js` module loader, instancing, recalc/processQueue, the 256-entry group-resolution LUT, behavioral RAM/ROM handlers, system load, tracing) | ✅ done — passes the blargg test ROMs |
| **S2** | Netlist → boolean IR: `Expr` records (`NodeRef` / `Hold` / `Prev` / `Mux` / `And` / `Or` / `Not` / `Const`), `DriveAnalysis` (per-node pull-down / pull-up / transmission-gate ports), `NextStateModel`, `SccModel` (cross-coupled latch recovery), and the `IrEngine` interpreter — *checking mode* (verify IR ≡ S1 per node, per half-cycle) and *driving mode* (the IR drives the netlist, S1 only fills in the residue) | ✅ done — the equivalence gate passes in both modes |
| **S3** | Whole-system optimization → a fast backend. **Done so far:** Node Aliasing (fold out buffer/inverter nodes) and a general size-1/2 SCC solver (two-step algebraic fixpoint) — IR driving-coverage **46% → ~85%** (12.5k of 14.7k nodes), residual = 843 nodes in 56 small SCCs (counters / shift registers / phase rings) + ~1.4k genuine multi-driver buses, all still handled by the S1 engine. A topological-loop breaker (γ.2) is parked (it over-cuts; the residue will instead be compiled as fixed-K micro-iteration blocks). **Next:** S4 codegen (codegen + GPU is the S4 link of the chain) — IR → an executable bit-sliced model. | 🚧 in progress |
| **S4** | Emit C++ / Verilog / a CUDA bit-sliced kernel from the IR; per-node equivalence with the CPU IR. (The LLVM-IR path retargets straight to NVPTX.) | ⬚ not started |

See [`MD/impl/S3/00_S3_效率與優化.md`](MD/impl/S3/00_S3_效率與優化.md) for the live status snapshot, the firing-by-firing log, and the α/γ designs (`01`–`02`); the S4 codegen draft is at `MD/impl/S4/00_codegen_設計.md`.

---

## Build & run

Requires the **.NET 10 SDK** with the Windows Desktop workload (the project is `net10.0-windows`, WinForms, x64, `AllowUnsafeBlocks`).

```sh
dotnet build AprVisual.sln                                                  # from the repo root

dotnet run --project src/AprVisual -- --help
dotnet run --project src/AprVisual -- --rom path\game.nes                   # window: live 256x240 switch-level sim
dotnet run --project src/AprVisual -- --test path\test.nes                  # headless: run to the blargg $6000 signature → PASS/FAIL + exit code
dotnet run --project src/AprVisual -- --test-dir path\nes-test-roms\
dotnet run --project src/AprVisual -- --selftest                            # unit tests (graph / drive analysis / IR / …)
dotnet run --project src/AprVisual -- --trace path\test.nes --cycles 8      # per-cycle CPU-state trace
dotnet run --project src/AprVisual -- --trace-cmp path\test.nes [--engine ir]  # the equivalence gate: IR vs S1, per node, per half-cycle
dotnet run --project src/AprVisual -- --dump-scc                            # IR coverage + the residual-SCC anatomy
dotnet run --project src/AprVisual -- --benchmark path\test.nes [--engine ir]
```

The chip/board netlists are loaded from `--system-def-dir <dir>` (see *Netlist data* below).

---

## Repository layout

```
src/AprVisual/        the C# project (single WinExe; see src/AprVisual/README.md)
  Sim/WireCore.*.cs     S1 — the switch-level engine (unsafe, unmanaged hot data, one static partial class)
  Sim/Logic/*.cs        S2/S3 — Expr, NetlistGraph, DriveAnalysis, NextStateModel, SccModel, NodeAlias, IrEngine
  Render/*.cs           GDI blit of an unmanaged ARGB framebuffer
  Rom/NesRom.cs         minimal iNES parser (NROM)
  Test/TestRunner.cs    CLI: --rom / --test / --selftest / --trace / --trace-cmp / --dump-* / --benchmark
MD/                   all planning & design docs — Traditional Chinese
  struct/               the plan (08 = the operative roadmap) + original analysis
  note/                 research notes on MetalNES (the S1 reference)
  impl/S1, impl/S2, impl/S3, impl/S4   the implementation designs + firing-by-firing progress logs
data/                 runtime data drop point (system-def/) — see below
ref/                  read-only third-party reference material — gitignored, not vendored (see below)
tools/                helper scripts (e.g. tools/knowledgebase — querying Gemini for design reviews)
```

There is no separate test project — `--selftest` is the unit-test entry point, `--trace-cmp` is the equivalence gate.

---

## Netlist data

The 2A03/2C02 switch-level netlists (`segdefs` / `transdefs` / `nodenames`) and the MetalNES `.js` module wrappers are **not vendored here**. They live under `ref/metalnes-main/data/system-def/` (gitignored) and are passed via `--system-def-dir ref/metalnes-main/data/system-def`. The netlist data ultimately derives from **Visual2A03 / Visual2C02** (CC-BY-NC-SA), wrapped by **[MetalNES](https://github.com/trzy/MetalNES)** (MIT). Test ROMs (a nesdev test-ROM collection) are likewise not vendored. The format the parser consumes is described in `MD/note/02`.

---

## How it's verified

S1's switch-level engine is the reference semantics (itself a port of MetalNES's `wire_compute`, which is a port of Visual6502's `chipsim.js`). Every layer above it is checked, per node, per half-cycle, against S1:

- `--selftest` — hand-built tiny netlists (inverter, NAND, pass transistor, dynamic latch, cross-coupled cell, …) against their truth tables, plus the graph/drive/IR passes.
- `--trace-cmp <rom>` — runs S1 and verifies `EvalExpr(NextExpr[v]) == S1's value` for every observable node, every half-cycle (the S2.6 equivalence gate).
- `--trace-cmp <rom> --engine ir` — same, but with the IR *driving* the netlist (the S2.4/S2.5 bridge).
- `--trace <rom> --cycles N` — a per-CPU-cycle state trace; used to confirm a change doesn't perturb S1's behavior byte-for-byte.

---

## Notes

- The design docs in `MD/` are in Traditional Chinese; code, identifiers, comments and commit messages are in English.
- Built on the shoulders of [Visual6502](http://www.visual6502.org/) / Visual2A03 / Visual2C02 and [MetalNES](https://github.com/trzy/MetalNES) — see *Netlist data* for the licensing situation. Don't redistribute the `ref/` contents without checking their licences.

---
---

# AprVisual（中文說明）

**把 Visual6502 式的開關層級 NES 網表，變成可分析、可驗證、可執行的邏輯模型 —— 最終做成一個「位元平行（bit-parallel）」的 kernel，一次模擬大量 NES 實例。**

重點不是「又一個 NES 模擬器」，而是這條**翻譯管線（translation pipeline）**：

```
矽連線資訊（segdefs / transdefs / nodenames）
   →  電晶體 + 導線 圖
   →  開關層級事件模擬          (S1 —— 參考語意 / golden reference)
   →  布林 / 時序 IR            (S2 —— 每個 node 的 next-state 表達式)
   →  最佳化、(幾乎)無環的 IR    (S3 —— 拆解 SCC/迴路、把覆蓋率往上推)
   →  bit-sliced codegen (C# / LLVM)  (S4 —— 多實例平行；LLVM IR 直接 retarget NVPTX → GPU)
```

每一層都用「**逐 node 等價檢查**」對照上一層，所以快速後端在邏輯上**可證明**跟原始開關層級模擬一模一樣。

範圍：NES-001 主機板 —— **2A03**（改版 6502 + APU）、**2C02**（PPU）、板上的膠合 TTL；只 NROM 卡帶。

## 目前進度

| 階段 | 內容 | 狀態 |
|---|---|---|
| **S1** | 用 C# 重寫 MetalNES 的開關層級引擎（`WireCore`：`.js` 模組載入器、實例化、recalc/processQueue、256-entry 群組解析 LUT、行為式 RAM/ROM handler、系統載入、tracing）| ✅ 完成 —— 過 blargg 測試 ROM |
| **S2** | 網表 → 布林 IR：`Expr` records（`NodeRef`/`Hold`/`Prev`/`Mux`/`And`/`Or`/`Not`/`Const`）、`DriveAnalysis`（每個 node 的 pull-down / pull-up / 傳輸閘寫埠）、`NextStateModel`、`SccModel`（cross-coupled latch 復原）、`IrEngine` 直譯器 —— *checking 模式*（驗證 IR ≡ S1，逐 node 逐半週期）+ *driving 模式*（IR 驅動網表、S1 只補殘餘）| ✅ 完成 —— 兩種模式的等價 gate 都過 |
| **S3** | whole-system 最佳化 → 快速後端。**已完成**：Node Aliasing（折掉 buffer/inverter node）、通用 size-1/2 SCC solver（兩步代數定點）—— IR driving coverage **46% → ~85%**（14723 個 node 裡 12.5k 個 driving-evaluated），殘餘 = 843 個 node 在 56 個小 SCC（計數器 / 移位暫存器 / phase ring）+ ~1.4k 個真·多驅動匯流排 node，這些目前還是交給 S1。topological-loop breaker（γ.2）暫停（會 over-cut；殘餘改用 fixed-K 微型迭代 block 在 codegen 階段處理）。**下一步**：codegen —— IR → 可執行的 bit-sliced 模型。| 🚧 進行中 |
| **S4** | 從 IR emit C++ / Verilog / CUDA bit-sliced kernel；逐 node 跟 CPU IR 等價（LLVM IR 路線直接 retarget NVPTX）| ⬚ 未開始 |

詳細現況快照、firing-by-firing 進度日誌、各步驟設計 → 見 [`MD/impl/S3/00_S3_效率與優化.md`](MD/impl/S3/00_S3_效率與優化.md)、`01`–`02`；S4 codegen 設計草稿在 `MD/impl/S4/00_codegen_設計.md`。

## 為什麼是「又慢又肥」的中間形狀（設計取捨）

GPU bit-slicing 要求：(1) 不能 event-driven / 不能動態 queue（SIMT 討厭 branch divergence）→ 改成無條件 brute-force flat program；(2) 不能有環 / 不能有「跑到收斂才停」的迴圈（迭代次數 data-dependent → divergence）→ 依賴圖必須 acyclic，剩下的環變成固定 K 次的 micro-block；(3) 只能純 bitwise（`& | ^ ~`，`Mux(c,a,b)` → `(c&a)|(~c&b)`），這樣 32/64 個 instance 才能塞進一個 word。

代價：這個中間階段在「單一實例 + CPU」上**確實又慢又肥**（brute-force 比 S1 event-driven 多做 ~10–100× 的 node-work；size-2 代換會複製子表達式）。但 brute-force 的成本**不隨並行度長** —— CPU 上 bit-slice（`ulong` = 64 個 NES、AVX-512 更多）、GPU 上幾千個，每實例吞吐量 = 單實例成本 ÷ 並行度 → 遠快過「把 S1 跑 N 次」。而且那個「肥」大多會被 LLVM/C++ `-O3` 對巨型 static-SSA block 的 const-folding / DCE / instruction-combining 吃掉。CPU 上的 IR 直譯器本來就不需要贏 S1 —— 它的角色是「正確、可驗證的中間表示」，真正的速度來自並行。

## 編譯 & 執行

需要 **.NET 10 SDK** + Windows Desktop workload（專案是 `net10.0-windows`、WinForms、x64、`AllowUnsafeBlocks`）。

```sh
dotnet build AprVisual.sln                                                  # 在 repo root

dotnet run --project src/AprVisual -- --help
dotnet run --project src/AprVisual -- --rom path\game.nes                   # 視窗：即時 256x240 開關層級模擬
dotnet run --project src/AprVisual -- --test path\test.nes                  # headless：跑到 blargg $6000 簽章 → PASS/FAIL + exit code
dotnet run --project src/AprVisual -- --test-dir path\nes-test-roms\
dotnet run --project src/AprVisual -- --selftest                            # 單元測試（graph / drive analysis / IR / …）
dotnet run --project src/AprVisual -- --trace path\test.nes --cycles 8      # 逐 cycle 的 CPU 狀態 trace
dotnet run --project src/AprVisual -- --trace-cmp path\test.nes [--engine ir]  # 等價 gate：IR vs S1，逐 node 逐半週期
dotnet run --project src/AprVisual -- --dump-scc                            # IR 覆蓋率 + 殘餘 SCC 解剖
dotnet run --project src/AprVisual -- --benchmark path\test.nes [--engine ir]
```

晶片/主機板網表用 `--system-def-dir <dir>` 載入（見下「網表資料」）。

## Repo 結構

```
src/AprVisual/        C# 專案（單一 WinExe；細節見 src/AprVisual/README.md）
  Sim/WireCore.*.cs     S1 —— 開關層級引擎（unsafe、unmanaged hot data、一個 static partial class）
  Sim/Logic/*.cs        S2/S3 —— Expr、NetlistGraph、DriveAnalysis、NextStateModel、SccModel、NodeAlias、IrEngine
  Render/*.cs           GDI blit（unmanaged ARGB framebuffer）
  Rom/NesRom.cs         極簡 iNES parser（NROM）
  Test/TestRunner.cs    CLI：--rom / --test / --selftest / --trace / --trace-cmp / --dump-* / --benchmark
MD/                   所有規劃 & 設計文件 —— 繁體中文
  struct/               計畫（08 = 操作中的路線圖）+ 原始分析
  note/                 對 MetalNES（S1 參考）的研究筆記
  impl/S1, impl/S2, impl/S3, impl/S4   各階段的實作設計 + firing-by-firing 進度日誌
data/                 runtime 資料投放點（system-def/）—— 見下
ref/                  唯讀的第三方參考材料 —— gitignored、不 vendor（見下）
tools/                輔助腳本（例如 tools/knowledgebase —— 拿設計去問 Gemini 做 review）
```

沒有獨立的測試專案 —— `--selftest` 是單元測試入口、`--trace-cmp` 是等價 gate。

## 網表資料

2A03/2C02 的開關層級網表（`segdefs` / `transdefs` / `nodenames`）和 MetalNES 的 `.js` 模組外殼**不 vendor 在這裡**。它們放在 `ref/metalnes-main/data/system-def/`（gitignored），用 `--system-def-dir ref/metalnes-main/data/system-def` 指過去。網表資料最終衍生自 **Visual2A03 / Visual2C02**（CC-BY-NC-SA），由 **[MetalNES](https://github.com/trzy/MetalNES)**（MIT）包裝。測試 ROM（一份 nesdev 測試 ROM 集）同樣不 vendor。parser 吃的格式描述在 `MD/note/02`。

## 怎麼驗證的

S1 的開關層級引擎是參考語意（它本身是 MetalNES `wire_compute` 的移植，而那又是 Visual6502 `chipsim.js` 的移植）。它上面的每一層都逐 node、逐半週期跟 S1 對照：

- `--selftest` —— 手刻的小網表（反相器、NAND、傳輸閘、dynamic latch、cross-coupled cell…）對照真值表，加上 graph/drive/IR 各 pass。
- `--trace-cmp <rom>` —— 跑 S1、驗證 `EvalExpr(NextExpr[v]) == S1 的值` for 每個 observable node、每半週期（S2.6 等價 gate）。
- `--trace-cmp <rom> --engine ir` —— 同上，但讓 IR *驅動*網表（S2.4/S2.5 bridge）。
- `--trace <rom> --cycles N` —— 逐 CPU cycle 的狀態 trace；用來確認改動不會 byte-for-byte 擾動 S1 的行為。

## 備註

- `MD/` 的設計文件是繁體中文；程式碼、識別字、註解、commit message 是英文。
- 站在 [Visual6502](http://www.visual6502.org/) / Visual2A03 / Visual2C02 和 [MetalNES](https://github.com/trzy/MetalNES) 的肩膀上 —— 授權狀況見「網表資料」。不要在沒檢查授權的情況下散布 `ref/` 的內容。
