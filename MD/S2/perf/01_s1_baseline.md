# S1 baseline —— S2 要打敗的門檻

> 量測日期 2026-05-31。機器 = 使用者 Windows 11 開發機(x64)。
> 這是 **S2 硬閘門的基準點**:S2 任何設計必須在整機 NES 工作負載上
> **比此數字快 _且_ checksum 不變**,否則不進入後續階段。

## 量測設定

- **工作負載**:`full_palette`(整機 NES,2A03 + 2C02,NROM),300,000 master half-cycles。
- **建置**:Release(C# `dotnet build -c Release`;Rust `cargo build --release`,lto=fat / codegen-units=1)。
- **版本**:C# `6058b1b` / Rust `27ec8cc`(皆 2026-05-31)。
- **方法**:6 輪 interleaved(每輪先 C# 後 Rust),丟第 1 輪 warmup,取 round 2–6 統計。

### 重現指令

```bash
# C#
dotnet src/AprVisual.S1/bin/Release/net10.0/AprVisual.S1.dll \
  --benchmark AprVisualBenchMark/roms/full_palette.nes --bench-hc 300000 \
  --extra-ram --system-def-dir AprVisualBenchMark/data/system-def --log-dir <tmp>

# Rust
experiment/rust-s1/target/release/wire_s1.exe \
  bench AprVisualBenchMark/snapshot/full_palette.aprsnap 300000 <tmp>
```
(等同 `AprVisualBenchMark/run_csharp.bat 300000` / `run_rust.bat 300000`。)

## 原始數據(hc/s)

| round | C# | Rust |
|---|---|---|
| 1 (warmup, 丟) | 78,321 | 77,608 |
| 2 | 77,900 | 77,310 |
| 3 | 74,653 | 75,828 |
| 4 | 77,555 | 77,550 |
| 5 | 77,079 | 75,393 |
| 6 | 69,984 ⚠ | 75,843 |

⚠ C# round 6 = 69,984 明顯離群(較其他低 ~9%),疑似背景干擾;C# 變異度比 Rust 大。

## 統計(round 2–6)

| 引擎 | **中位數** | mean(含離群) | **trimmed-mean** | 範圍 |
|---|---|---|---|---|
| **C# S1** | **77,079** | 75,434 | **76,429** | 69,984 – 77,900 |
| **Rust S1** | **75,843** | 76,385 | **76,327** | 75,393 – 77,550 |

**結論**:兩引擎已幾乎打平(~76–77K hc/s)。先前記憶中「Rust 領先 C# +9.2%」的差距,
被 R-1 dynamic-singleton fast-path(C# +18.6% / Rust +12.5%)+ R4 OR-all(C# +0.6%)抹平;
現在 **C# 中位數略高、但變異較大;Rust 較穩**。S2 門檻取整為:

> **C# ≈ 77K hc/s / Rust ≈ 76K hc/s(300k full_palette),checksum 必須 == `0x794A43ABDF169ADA`。**

## 引擎現況快照(來自 C# run 的啟動輸出)

```
lowering : nodes 15164 -> 14723 (merged 441); transistors 27305 -> 26775 (dropped 530)
fast-path: 3,929 static pure-logic (26.7%) + 10,784 dyn-singleton candidates (73.2%)
           of 14,729 live nodes
```
→ **約 73% 的 live 節點是 dyn-singleton 候選**(印證「平均走訪 1.4 節點、~70% recalc 是
singleton」的稀疏性)。這正是 S2 必須尊重的事實:**事件驅動 + 小走訪是常態**,任何
「批次重算全圖」的設計都會輸(見 [[s4-route-single-instance]] 的 IR/codegen 3–6× 慢)。

## real-time gap

| | 值 |
|---|---|
| NES NTSC real-time | 42,955,000 hc/s |
| C# S1 | 77.1K hc/s → **0.181% real-time → 551.9× 太慢** |
| Rust S1 | 75.8K hc/s → 0.175% real-time → 571.9× 太慢 |
| 一幀模擬耗時 | ~9.2–9.5 秒(real NES = 0.0166 s) |

不期望達 real-time;S2 的成功定義是**清楚、可重現地超越 S1**(理想是結構性的一步,而非雜訊)。
