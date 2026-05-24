# S1 效能測試彙整 — 數據、方法、加速機制

> 日期:2026-05-25
> Branch:`aot-codegen`(reference HEAD `54e58f5`)
> 適用:S1 switch-level engine(`src/AprVisual/Sim/WireCore.*.cs`),Rust port(`experiment/rust-poc/`)

## TL;DR

| 配置 | hc/s | × C# baseline | 安全性 |
|---|---|---|---|
| **C# baseline** | **36,578** | 1.000× | ✅ bit-identical(ref)|
| C# `--fast-path` | 37,935 | 1.044× | ✅ bit-identical |
| C# `--prune-merge --fast-path`(safe,topology-fix)| 37,105 | 1.022× | ✅ bit-identical |
| **Rust port baseline** | **50,433** | **1.379×** | ✅ bit-identical |
| Rust + `--fast-path` | 50,499 | 1.381× | ✅ bit-identical |
| Rust + 雙 flag(safe)| 47,572 | 1.300× | ✅ bit-identical |
| **C# `--prune-merge --fast-path`(broken,unsafe)** | **53,788** | **1.471×** ⚠️ | ❌ **PPU 黑屏** |

**真正可拿出 demo 的最高 safe 加速 = Rust port baseline ~1.38×**,跟 broken `--prune-merge` 的 1.48× 同數量級但**正確 + bit-identical**。

---

## 1. 測試方法論

### 1.1 bench-hc(throughput)

```powershell
dotnet run -c Release --project src/AprVisual -- `
  --benchmark <rom>.nes --bench-hc <N> `
  --system-def-dir ref/metalnes-main/data/system-def `
  [--fast-path] [--prune-merge] [--rcm] [--levelize] ...
```

`N` = master half-cycles。 1 NTSC frame ≈ 714,732 hc。 標準量測 N=50,000(~0.07 frame,涵蓋一段 CPU+PPU 真實 activity)。

輸出三條關鍵 line:
```
# rate: 36,578 hc/s (27.34 µs/hc)              ← 主要量
# load (compose netlist + power-on settle): 0.40 s  ← 不計入 rate
# NodeStates checksum @ t=50192: 0x54C4D6220C6C569E ← 等價性驗證
```

### 1.2 NodeStates checksum 等價性

FNV-1a 64-bit hash over `byte[] NodeStates`,在 bench 結束後計算。 兩個 run **相同 checksum** ⇔ **per-node bit-identical**。 任何優化標榜 "bit-identical" 必須通過這個 hash 對比。

不夠:`observably-identical` 或 `blargg-PASS`(可能 CPU 結果一樣但 PPU 內部 state 偏差),歷史上吃過虧(見 §7c)。

### 1.3 screenshot(visual equivalence)

```powershell
dotnet run -c Release --project src/AprVisual -- `
  --screenshot <rom>.nes --frames <N> --out <path>.png `
  --system-def-dir ref/metalnes-main/data/system-def `
  [--fast-path] [--prune-merge]
```

跑 N 個 NES frame 後 dump PPU framebuffer 為 PNG。 `--frames 50` 之 full_palette.nes 顯示完整 64 色 palette grid(`MD/summary/full_palette_f50.png`,8,283 bytes)── reference 圖案。

**bench-hc + checksum 不夠涵蓋 visual ROM 的問題**(見 §7c):一定要至少跑一個 visual ROM screenshot 驗等價性。

### 1.4 噪聲控制

- Release build(`-c Release`),`PlatformTarget=x64`
- `ServerGC=true`、`TieredPGO=true`
- ×2-3 trial 取中位數
- Rust:`lto=fat`、`codegen-units=1`、`opt-level=3`
- 跟 OS 背景 process 競爭時 ±2-3% noise(CPU thermal / Turbo boost / 背景 task)── 任何小於 ~5% 的差距視為雜訊

---

## 2. ROM 選擇

| ROM | 用途 | 為何選 |
|---|---|---|
| `nes-test-roms-master/checked/instr_test-v5/rom_singles/01-basics.nes` | bench-hc baseline | blargg CPU 測試,32 KB PRG,NROM mapper 0,CPU 真實執行 |
| `nes-test-roms-master/choose/full_palette.nes` | visual + bench | PPU 端到端顯示全 64 色 palette grid,frame 48-50 出現完整圖樣;能驗 render pipeline 是否壞 |
| `nes-test-roms-master/choose/Super Mario Bros. (World).nes` | demo | NES 經典,frame 90+ 看到 title screen |

**避開:**
- `AccuracyCoin.nes`(需要 gamepad,不適合 headless)
- 部分 blargg ROM 沒寫 `$6000` PASS/FAIL signature

---

## 3. 加速機制 — C# 端

依時間順序,跑過七個策略(`MD/impl/math-algos/`):

### 3a. G — RCM ordering(`--rcm`)

把 node id 重排成 cache-friendly 順序(Reverse Cuthill-McKee on transistor 圖)。 預期 cache locality 改善。

**結果:1.04× boot / 1.00× steady-state**。 NodeStates 才 ~15 KB,早已塞進 L1d,cache 不是瓶頸。

### 3b. Y — SIMD queue(`--simd-queue`)

`AddNodeToGroup` 的 c1c2s walk 用 manual unrolled 4-pair + memory-level parallelism(同時發 4 個 byte load)。 預期 byte gather 速度。

**結果:1.00×**(沒上沒下)。 C# 沒有 `Avx2.GatherVector256<byte>`,要做就得把 NodeStates 轉 `int*`(4× 記憶體,弄壞 L1 friendliness)。 而且 byte* 的不規則 walk 本來就難 vectorize。

### 3c. X — Oblivious eval(`--oblivious`)

放棄 BFS dirty-set,改成「每半週期把全 14k node 都重 eval 一次到 fixpoint」。 預期 cache-streaming 友善 + branch elimination。

**結果:0.008×**(慢 121×)。 證明 BFS 的 O(D) 演算法在 D=600~ 對 N=14,723 是 essential,不能換 O(N)。

### 3d. #1 — Merge-pruning(`--prune-merge` 原版,**unsafe!**)

當 gate 從 0→1(transistor 導通,c1/c2 merge):若 `NodeStates[c1] == NodeStates[c2]` 跳過 enqueue。 理由:「equal-value merge 不會改變任何 node 的 resolved value」。

**結果(數字)**:1.32×(C# 50,000 hc bench)。 NodeStates checksum 跟 baseline 不同(`0xF393...` vs `0x54C4...`),被宣稱「observably-identical」── 在 blargg CPU 測試 ROM 上 PASS/FAIL 跟 baseline 一致。

**真相**:**對 PPU visual ROM 渲染全黑**。 Cross-coupled latches(D-FF、6T SRAM、PPU vpos/hpos counter)有兩個合法 stable state,merge 改變 group topology + 可能讓 cell 鎖到 inverted edge。 PPU vpos counter 一錯永遠錯(CPU 不能直接寫)→ vblank state machine desync → `rendering_disabled` 永遠拉著。

**修復方法**(Gemini r3,2026-05-24):用 **topological equivalence** 取代 digital equality。 給每個 `ComputeNodeGroup` BFS walk 一個新 `_nextGroupID`,所有 walked nodes 共享。 skip iff `NodeGroupIDs[c1] == NodeGroupIDs[c2]`(已物理 tied,merge 真的是 no-op)。

**Fixed 版結果:1.022× combined / 0.99× 單做** ── topology check 比 digital eq 嚴格太多,skip rate 大幅下降,overhead 占優。 **「1.32×」的速度數字實質是不可達的**,safe 版基本沒幫助。

### 3e. 策略二 — fast-path(`--fast-path`)

Pure-logic-gnd node(有 PullUp、無 HasCallback/ForceCompute/Pwr/Gnd、TlistC1c2s 為空)的 group 必為 `{nn}` 自己。 O(1) 直接 read flags + 任何 TlistC1gnd/C1pwr ON channel → FlagsToState LUT。

**結果:單做 1.04× / 跟其他疊加 ~1.05×**(3,408 nodes classified,23.1% live nodes)。 真正 **bit-identical**(checksum 跟 baseline 完全一樣)。

per-recalc 常數本來就極小(L1 一個 cache line 內結束),所以削常數天花板低。 但 cheap + 安全,值得開。

### 3f. 策略三 — Levelize / Glitch diag(`--levelize`,結論棄用)

軟性 level-sort(gate-only acyclic 部分排 topo level,每 wave 內按 level 排)。 預期消除 glitch tax(每 node 每半週期重算 1.14 次)。

**結果:`-15%`**(counting-sort overhead 把好處吃掉)。 棄用,只留 diagnostic 量出 glitch tax = ~12% D(`glitch factor 1.138`)── 削這 12% 需要拓樸層級化 = 部分 S2 = 出 branch 範圍。

### 3g. 結論

C# safe 加速天花板 = **`--fast-path` 的 1.04×**。 1.32× / 1.37× 是 unsafe-only(不能 render),修了 safe 變成 1.02×。

---

## 4. 加速機制 — Rust port(`experiment/rust-poc/`)

PoC 把 `WireCore.Recalc.cs` + `Group.cs` + `Handlers.cs` 的 settle/group/memory-handler 核心 port 成 Rust。 繞過 `.js` 模組解析,用 C# 跑完 `LoadSystem + ResetNes` 後 dump binary snapshot,Rust 載入後 step。

### 4a. 範圍

**已 cover:**
- ✅ `process_queue` + `recalc_node`(BFS dirty-set settle)
- ✅ `add_node_to_group` + `compute_node_group` + flags-to-state LUT
- ✅ `set_node_state` + transistor propagation(gate fanout)
- ✅ Memory handler dispatch(`*func<ram>` / `*func<rom>` 的 callback-target-node 機制)
- ✅ Clock toggle handler + `step` / `run_frame`(until vblank rising)
- ✅ Video pixel write(pclk1 rising → hpos/vpos/pal_ptr → NES palette → ARGB)
- ✅ PNG output via `png` crate
- ✅ `--fast-path` 完整 port(`ClassifyPureLogicNodes` + `RecalcNodeFast`)
- ✅ `--prune-merge` topology-group-ID fix 完整 port

**沒 cover(不影響 baseline 比較):**
- ❌ `.js` parser / module composer ── **永遠不 port**(改用 C# snapshot)
- ❌ IR / codegen dispatcher / levelize / RCM ordering / simd-queue
- ❌ winit GUI(只 headless PNG)

### 4b. Cross-language 結果(`full_palette.nes`,50K hc,×3 trial)

| 配置 | C# hc/s | Rust hc/s | Ratio | Checksum 一致 |
|---|---|---|---|---|
| baseline | 37,160 | 50,065 | **1.347×** | ✅ `0x933ABE7915AC18BE` |
| `--fast-path` | 37,935 | 50,499 | **1.331×** | ✅ same |
| `--prune-merge`(safe)| ~36,000 估 | 46,771 | **~1.30×** | ✅ same |
| `--fast-path --prune-merge`(safe)| 37,105 | 47,572 | **1.282×** | ✅ same |

Rust 對 C# 的優勢:**1.28-1.35×**,隨 flag 越多差距收斂(fast-path 內 loop 短到 LLVM 跟 RyuJIT 差距減少)。

### 4c. Cross-flag 終極驗證

Rust + 雙 flag vs Rust 無 flag,跑 50-frame full_palette screenshot:

| | wall time | NodeStates checksum | PNG size | PNG bytes |
|---|---|---|---|---|
| Rust baseline | 728 s | `0x4190EA4BA952686E` | 17,663 | (ref)|
| Rust `--fast-path --prune-merge` | 766 s | `0x4190EA4BA952686E` | 17,663 | **same** ✓ |

`cmp` exit 0 ── **PNG byte-identical(連 zlib 壓縮輸出都一樣)**。 35.7M hc 跑下來最終 framebuffer 連 metadata 都不差一個 bit。 這是「flag 開關 truly bit-identical」最強的證據,比 checksum match 更狠。

### 4d. 為什麼 Rust 比 C# 快這麼多

PoC 前估 1.1-1.3×,實測 1.35×,比預期樂觀 ~10pp。 假設(未經 microbench 分離):

- **LLVM aliasing analysis 比 RyuJIT 緊**:雖然 C# `unsafe` + `byte*` 已經繞掉 bounds check,但 RyuJIT 對 pointer aliasing 不會像 LLVM 那樣激進 vectorize / reorder
- **LTO + codegen-units=1** 讓 cross-function inlining 程度高(`recalc_node` → `compute_node_group` → `add_node_to_group` 整條鏈可以 inline + fuse)
- **Recursive `add_node_to_group`** ABI 開銷較低(Rust 沒 GC 寫屏障,沒 method 多型 vtable)
- 24-byte NodeInfo packed 兩語言同樣優化,不是差異點

---

## 5. Branch 說明

Repository(`github.com/erspicu/AprVisual`):

| Branch | 狀態 | 內容 |
|---|---|---|
| **`main`** | stable base | 最早 commit 起點。 S1 engine 完整版(`WireCore.*`)、Renderer、Test。 後面所有實驗 branch 都從這分出來。 包含 S4 路線完成的 LLVM-MCJIT / CUDA / HLSL bit-sliced emit / Verilog emit / GPU-D3D11 等(per memory:S4 done,real-time-on-CPU unreachable)|
| **`cpu-opt`** | **parked** | 早期 CPU 優化嘗試(event-driven β path)。 跑出 ~2-3×,離 840× real-time 還很遠,被 abandon。 per memory:re-scoped 之後改走 `math-algos` |
| **`math-algos`** | **research-complete,not merging** | 用「不重寫 S2/IR/codegen 純削 S1 D 大小」的演算法級實驗。 7 個策略(G/Y/X/#1/策略二/策略三 等),最高 1.37×(unsafe,後修為 1.02× safe)。 結論:結構排程死路、pruning 天花板 1.37× unsafe / ~1.04× safe。 完整 wind-down 記錄 `MD/impl/math-algos/00_INDEX.md` |
| **`aot-codegen`** | **active**(HEAD) | 把 math-algos 工具(Partition + Dispatcher + Option D BFS-block + AluBlock C++)forward-merge 過來,加 AOT codegen 路線:Phase A-E 試圖把 S1 部分 evaluation pre-compile 成 .cs / native code。 Phase E-4a 顯示「ROM handler wired but 0 fires」── 跟 math-algos Step 3.5 撞到同類「architecture ceiling」。 後續加入:Rust PoC(`experiment/rust-poc/`),`--prune-merge` topology-fix,full_palette / SMB visual 驗證 |
| **`s1-results-showcase`** | snapshot(`914cca8`)| 為「成果展示」凍結的版本,branch 自 aot-codegen 1d6fd17 之前。 純 doc / chat-log commit,不包含後續 prune-merge bug debug 跟 Rust PoC。 origin 上保留,本地 working tree 已切回 aot-codegen |

### 5a. 為何不 merge 回 main

per `MEMORY.md`:

> **math-algos**:Phase 2.5 codegen runtime-accelerator FAILED at Step 3.5。 S1 group-resolution architecture is the ceiling(CodegenOwned can't block S1 BFS reach;Option D BFS-block works but owned set caps at 62 named ALU mids;saving 0.4% < dispatcher overhead 5% = net -3.2%)。 NOT merging to main。 Tools kept in branch for potential AOT-compiler pivot

> **整體**:S4 done,real-time-on-CPU confirmed unreachable。 correctness 全 done(S1 ≡ S2/S3 IR ≡ S4 backends:C#-JIT/interpreter/LLVM-MCJIT/GPU-D3D11 all verified + fixed-K SCC + S0/S1/W1 bus model + bit-sliced/Verilog/HLSL emit);但每個 AOT-batch backend 都比 S1 慢 3-6×(batch re-evals ~14.7k nodes/half-cycle 當 ~hundreds change;codegen 不能 beat algorithmic redundancy),event-driven β 卡 ~2-3× < ~840× 需要的 real-time。 **real-time NOT 可達 through this pipeline**

### 5b. 當前實驗(aot-codegen)的價值

雖然 main 還是「stable base 含 S4 路線完成」,aot-codegen 的後續 commit 仍有實質貢獻:

1. **`--prune-merge` 修正**(commit `18986e7`)── 原 math-algos 的 1.32× 是 unsafe-only,visual ROM 黑屏。 Gemini r3 topology-group-ID 修法落地,雖速度回到 1.02× safe,但 doc 跟 code 都修正了 over-claim。
2. **Rust PoC**(commits `6416974` / `5097dae` / `cd781bd` / `54e58f5`)── 證實 Rust port 對 S1 inner loop 給 1.35× over C# baseline,bit-identical(連 50-frame screenshot PNG byte-equal)。 是「語言層級 vs 演算法層級」分離量測。
3. **這份 doc**(`MD/summary/s1-performance.md`)── 整體效能成果彙整。

aot-codegen 的 AOT codegen 主線(Phase A-E)研究價值 per memory note ── 沒解到 real-time,但完成「correctness 全 chain」(S1 ≡ S2 IR ≡ S4 各 backend 都驗過 byte-equal),這是學術 / 教育 / 可重現性的關鍵 deliverable。

---

## 6. 歷史與當前 commit 紀錄

| Commit | 內容 | Branch |
|---|---|---|
| `d762c18` | math-algos Phase 1 收官:1.37× peak(unsafe!),原始 broken `--prune-merge` | math-algos |
| `289f2be` | aot-codegen Phase E-4a 結果 | aot-codegen |
| `914cca8` | ref/suggest/ 整理(showcase 起點)| aot-codegen |
| `s1-results-showcase` | s1 成果 snapshot branch(基於 `914cca8`)| (orphan)|
| `1d6fd17` | 第一次 prune-merge 嘗試修(partial,not enough)| aot-codegen |
| `18986e7` | Gemini r3 topology-group-ID fix(可 render 但變 1.02×)| aot-codegen |
| `6416974` | Rust PoC v1:bench-hc 1.37× over C# | aot-codegen |
| `5097dae` | Rust PoC + video handler + PNG | aot-codegen |
| `cd781bd` | Rust PoC + fast-path + prune-merge(完整 port)| aot-codegen |
| `54e58f5` | Rust + 雙 flag 50-frame screenshot(byte-identical PNG)| aot-codegen |

---

## 7. 教訓

### 7a. 「observably-identical」是危險聲明

`--prune-merge` 原始版號稱 `observably-identical`,bench 用 blargg `01-basics.nes`(純 CPU 測試,不依賴 PPU rendering),blargg PASS。 但在 `full_palette.nes` 渲染完全壞掉。 教訓:**等價性驗證集要至少涵蓋一個 visual ROM**,而且最好用 PNG screenshot diff 而不是 checksum diff(因為 NodeStates 不同也可能視覺一樣,反之亦然)。

### 7b. 「unsafe 加速」的數字仍然有 academic value

`--prune-merge` 1.32-1.48× 不能用來 render visual,但**在純 CPU workload(blargg / instr_test 等)上速度真實**,academic 角度說「達成 1.37× hc/s on 01-basics.nes」沒錯 ── 只是 caveat 必須講清楚:**workload-specific,visual sim 不適用**。

### 7c. Cross-coupled latch 的 stable-state 模糊性是 hard problem

D-FF / 6T SRAM / latches 有兩個合法 stable state。 任何依賴「digital value 不變則 simulation 不變」的優化都會在 power-on settle 或 clock-edge transition 時翻車。 修法只有兩個:
1. 完全不 skip(失去優化)
2. 用 **topological equivalence**(同 group 才 skip;Gemini r3 方案)── 安全但 skip rate 大降

### 7d. 語言對效能的影響有限,但不為零

- 全 baseline algo:Rust ~1.35× over C#
- 加 fast-path:Rust ~1.33× over C#(差距收窄)
- 加雙 flag:Rust ~1.28× over C#(差距更收窄,因為 inner loop 短到 RyuJIT 也能優化)

**結論**:port 整個 S1 到 Rust 能拿 ~37% 速度,**但不能突破 real-time 的 ~840× gap**(那是 paradigm 問題,需要 IR + codegen + GPU batching,main branch S4 已探索)。

Rust port 的價值在:**packaging**(cross-platform,Linux/Mac/WASM/Android 都能跑)、**no GC noise**、**LLVM AOT**。 若未來 open-source release S1,Rust 是合理選項。

---

## 8. 重現

```powershell
# C# bench-hc
dotnet build -c Release src/AprVisual/AprVisual.csproj
dotnet run -c Release --project src/AprVisual --no-build -- `
  --benchmark nes-test-roms-master/checked/instr_test-v5/rom_singles/01-basics.nes `
  --bench-hc 50000 `
  --system-def-dir ref/metalnes-main/data/system-def

# C# screenshot(full_palette frame 50)
dotnet run -c Release --project src/AprVisual --no-build -- `
  --screenshot nes-test-roms-master/choose/full_palette.nes `
  --frames 50 --out MD/summary/full_palette_f50.png `
  --system-def-dir ref/metalnes-main/data/system-def `
  --fast-path

# Rust:export snapshot,build,bench
dotnet run -c Release --project src/AprVisual --no-build -- `
  --benchmark nes-test-roms-master/choose/full_palette.nes `
  --export-snapshot experiment/rust-poc/snapshot/full_palette.aprsnap `
  --system-def-dir ref/metalnes-main/data/system-def

cd experiment/rust-poc
cargo build --release
./target/release/wire_realbench.exe bench snapshot/full_palette.aprsnap 50000
./target/release/wire_realbench.exe bench snapshot/full_palette.aprsnap 50000 --fast-path
./target/release/wire_realbench.exe bench snapshot/full_palette.aprsnap 50000 --fast-path --prune-merge

# Rust screenshot
./target/release/wire_realbench.exe shot snapshot/full_palette.aprsnap 50 ../../MD/summary/full_palette_f50_rust.png
```

兩邊的 NodeStates checksum **必須一致**;不一致就是 simulation diverge,結果無效。

---

## 9. 檔案地圖

- `src/AprVisual/Sim/WireCore.Recalc.cs` ── settle / processQueue / recalcNode / setNodeState
- `src/AprVisual/Sim/WireCore.Group.cs` ── BFS group walker
- `src/AprVisual/Sim/WireCore.FastPath.cs` ── 策略二(`--fast-path`)
- `src/AprVisual/Sim/WireCore.PruneMerge.cs` ── #1 topology-group-ID fix(`--prune-merge`)
- `src/AprVisual/Sim/WireCore.Handlers.cs` ── clock / memory / video handlers
- `src/AprVisual/Test/SnapshotExporter.cs` ── `--export-snapshot` CLI(for Rust PoC)
- `experiment/rust-poc/src/wire.rs` ── Rust port 的 WireCore 核心
- `experiment/rust-poc/src/snapshot.rs` ── binary loader(v2 format)
- `experiment/rust-poc/src/main.rs` ── bench / shot CLI
- `MD/impl/math-algos/00_INDEX.md` ── 完整 math-algos branch 歷史(7 個策略 + Gemini consultation 記錄)
- `MD/impl/math-algos/01_results.md` ── 詳細數據(含 §7 後加的 prune-merge fix 修正)
- `MD/note/rust-poc-result.md` ── Rust PoC 寫作概覽
- `MD/summary/full_palette_f48.png` ── C# baseline reference render
- `MD/summary/full_palette_f50.png` ── C# `--fast-path` render
- `MD/summary/full_palette_f50_rust.png` ── Rust baseline render
- `MD/summary/full_palette_f50_rust_flags.png` ── Rust 雙 flag render(byte-identical 對 ↑)
