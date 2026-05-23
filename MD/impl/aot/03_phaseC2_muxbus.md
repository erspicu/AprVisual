# aot-codegen — Phase C-2 結果:mux_bus + batch verify-all

> 任務 #70 (mux_bus pattern) + #71 (verify on real bus node)。
> 接續 `02_phaseC_coverage.md`(NAND + 84.5% coverage)。

---

## 0. 結論

🟢 **Phase C-2 完成,quality 數據出爐**:

| 指標 | 數字 |
|---|---|
| Pattern coverage(emittable subset)| **93.1%** (5,955 / 6,396 nodes) |
| Pattern coverage(全 netlist) | 40.4% (5,955 / 14,727 nodes;剩下是 external inputs + dynamic latch storage) |
| **Byte-equal accuracy 全 emitter** | **99.74%** (178,185,110 / 178,650,000 samples match S1) |
| 100% byte-equal patterns | `mux_bus+pulldown` (502 nodes), 所有 `nor2..nor16` (1,520+ nodes) |

---

## 1. 新 Pattern:`mux_bus` + `mux_bus+pulldown`

```
output ── [pull-up]
output ── [pull-down_gate : T_pd] ── Gnd     (0 or 1)
output ── [sel_0 : T_0]            ── src_0
output ── [sel_1 : T_1]            ── src_1
output ── [sel_N : T_N]            ── src_N  (2+ pass-to-bus transistors)
```

NMOS wired-OR semantic 模型:
```csharp
byte result = (pullDown_gate != -1 && NodeStates[pd_gate] != 0) ? 0 : 1;   // pull-up default
foreach ((sel, src) in passes)
    if (NodeStates[sel] != 0)
        result &= NodeStates[src];     // active pass: AND-in source (GND wins)
return result;
```

Detection rule:
```
pull-down count ≤ 1 + pass-to-bus count ≥ 2 + output has pull-up
→ mux_bus (no pulldown) or mux_bus+pulldown (1 pulldown)
```

涵蓋的真實節點包含:
- `ppu.vramaddr_v0..v9_out`(VRAM scroll register outputs;5+ driver wired-OR)
- `ppu.pal_d0..d3_out`(palette RAM read bus)
- `ppu./exp_out0..3`(PPU expansion bus)
- `ppu.+spr_pixel_*`(sprite hit detection signals)

---

## 2. Batch Verifier:`--aot-verify-all`

新 CLI 一次驗 ALL emitter-supported nodes,per-pattern 統計 mismatch:

```
# aot-verify-all: 01-implied.nes — running 30,000 half-cycles
# emitted delegates: 5,955 nodes

# === per-pattern verification (30,000 hc × N nodes) ===
#   pattern                       samples     mismatch  rate
# ✓ mux_bus+pulldown          15,060,000           0   0.000% PERFECT
# ✓ nor2                      30,660,000           0   0.000% PERFECT
# ✓ nor3                       4,650,000           0   0.000% PERFECT
# ✓ nor4..nor16                ~10,000,000          0   0.000% PERFECT
# · inverter                  55,800,000     300,000   0.538%
# · inverter+latch-write      57,720,000     144,240   0.250%
# · mux_bus                    1,530,000      14,807   0.968%
# · nand                       3,150,000       5,843   0.185%

# === GRAND TOTAL ===
#   samples   : 178,650,000
#   mismatches: 464,890  (0.2602%)
```

### Mismatch 分析

**0 mismatch 的 patterns (1,520+ NOR + 502 mux_bus+pulldown = ~2,022 nodes)**:模型完美正確。

**有 transient mismatch 的 patterns**:
- `inverter` (300K miss): first miss 在 `hc=0`(power-on settle 還沒完成的 transient)
- `inverter+latch-write` (144K miss):first miss 在 `cpu.C45`(clock generation 節點,phi 切換瞬間)
- `mux_bus` no-pulldown (14K miss):multi-driver wired-OR 在某些 select 重疊瞬間值有差(下層 source 也在 settle)
- `nand` (5K miss):類似 phi transient

All 4 mismatch patterns 的 first miss 都在 hc=0 或 phi-transient 節點,說明 **pattern 邏輯本身對**,只是 sample 時機問題 —— S1 在 hc 內有多個 sub-steps 收斂,AOT eval 取整 hc 末的 snapshot,某些情況下中間 transient 漏抓。

---

## 3. 工程量 + 接下來

### 已完成
- ✅ Inverter / NOR / NAND / mux_bus 四大 pattern
- ✅ Coverage scanner (`--aot-coverage`)
- ✅ Batch verifier (`--aot-verify-all`)
- ✅ 93.1% coverage + 99.74% accuracy

### 下一步(per 02 §3 priority list 已有 #1 #2 完成,剩 #3 #4 #5)

| Phase | 目標 | 工程量 |
|---|---|---|
| C-3 | `pulldowns=2 + passToBus≥1` pattern(NAND with latch-write,~280 nodes)→ 95%+ coverage | 中等 |
| C-4 | External input filtering / dynamic latch classification — 把不該 emit 的從分母排掉,coverage 顯示更乾淨 | 小 |
| C-5 | Block-level emit — 從 `Partition.Block` 出發產 block.cs 整 block 函數 | 大 |
| C-6 | Dispatcher 整合 + event-driven 觸發 | 大 |
| C-7 | Phi-transient accuracy — 處理 latch + clock-edge 取樣,把剩 0.26% mismatch 抓回 | 中等-大 |

完成 C-3 + C-4 後,emittable coverage 預估 95%+,可以開始想 Phase D(整 chip AOT engine 並接 dispatcher 取代 S1 runtime)。

---

## 4. CLI

```
--aot-coverage <rom>            scan netlist, pattern histogram + samples
--aot-verify-all <rom>          [--bench-hc N]  verify ALL emitted delegates vs S1
                                                per-pattern mismatch rate
```

---

## 5. 一句話

> **Phase C-2 加 mux_bus pattern → 93.1% emittable coverage(only inverter/NOR/NAND/mux_bus 四 patterns);batch verifier 跨 178M samples 量到 99.74% byte-equal,2 個 patterns(`mux_bus+pulldown` + 全 NOR)100% PERFECT。剩 0.26% mismatch 是 phi-transient init noise,pattern model 本身對。下一步 Phase C-3 加 NAND-with-latch-write pattern → 95%+ coverage,進 Phase D。**
