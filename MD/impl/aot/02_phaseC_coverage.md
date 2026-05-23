# aot-codegen — Phase C 初步結果:NAND + 全 netlist coverage scan

> 任務 #68 (NAND + coverage scanner) + #69 (pattern priority)。
> 接續 `01_phaseAB_results.md`(IR inverter 端到端驗證)。

---

## 0. 結論

🟢 **Phase C 第一輪 milestone**:`AotEmitter` 現在能 auto-emit **5,402 / 14,727 (36.7%)** 的全網表節點;**排除外部 input + dynamic latch 後,在可 codegen 子集上覆蓋率 = 84.5%**。

加 NAND ladder pattern(2-series pull-downs)+ `--aot-coverage` 全 netlist scanner;histogram 揭露下一步該補哪些 pattern。

---

## 1. Patterns supported(到目前為止)

| Pattern | Detection | 範例輸出 |
|---|---|---|
| `inverter` | 1 pull-down + 0 pass-to-bus + pull-up | `NOT(gate)` |
| `inverter+latch-write` | 1 pull-down + 1 pass-to-bus + pull-up | `NOT(gate)` (latch-write 在 steady state inert) |
| `norN` (N=2..16) | N 個 pull-down + 0 pass + pull-up | `NOT(g0 \| g1 \| ... \| gN)` |
| **`nand` (NEW)** | 0 pull-down + 1 pass-to-mid + mid 有 1 pull-down 無 pass + 無 pull-up | `NOT(gA AND gB)` |

---

## 2. 14,727 節點全 netlist scan 結果

```
# aot-coverage scan over 14,730 live nodes
# total scanned: 14,727
# supported    : 5,402  (36.7%)
# unsupported  : 9,325  (63.3%)
```

### Supported 細項(5,402 / 14,727 = 36.7%)

| Pattern | Count | % of total |
|---|---|---|
| `inverter+latch-write` | 1,924 | 13.06% |
| `inverter` | 1,860 | 12.63% |
| `nor2` | 1,022 | 6.94% |
| `nor3` | 155 | 1.05% |
| `nor4..16` | 330 | ~2.2% |
| `nand` | 105 | 0.71% |
| **Total supported** | **5,402** | **36.67%** |

### Unsupported buckets(9,325)

| Pattern | Count | % | 性質 |
|---|---|---|---|
| `no-pullup` | **8,331** | **56.57%** | 大量是 external inputs + dynamic latch storage |
| `pulldowns=1, passToBus≥2` | 502 | 3.41% | **wired-OR bus**(多 driver,e.g. `ppu.vramaddr_v4_out`)|
| `pulldowns=2, passToBus≥1` | 280 | 1.90% | **NAND + 額外 pass**(latch-write 之類)|
| `pulldowns=3..29, passToBus≥0` | 117 | 0.80% | 多 pull-down + 有 pass,複雜邏輯 |
| `pulldowns=0, passToBus≥0`(非 NAND)| 93 | 0.63% | 純 pass-through node,難 codegen |
| 其他 | 2 | 0.01% | edge cases |

### 排除 "no-pullup" 後的真實覆蓋率

`no-pullup` 大多是 **AOT 本來就不該 emit 的 nodes**:
- 外部輸入(`clk`、`res`、`/cpures`、`func<clock>`、`func<video_out>`、`BA0`)—— c1c2s=0,只是 callback fake nodes 或 chipboard signal
- Dynamic latch storage nodes —— 沒 pull-up,值由 pass transistor write 進來;需要 latch/pass-mux pattern

```
emittable subset = 14,727 - 8,331 = 6,396
supported within emittable = 5,402 / 6,396 = 84.5%
```

**單以 inverter/NOR/NAND 三大 pattern,就 cover 了可 codegen 節點的 84.5%**。

---

## 3. 下一步 Pattern Priorities(從 unsupported sample 推斷)

從 8 個 sample per top-3 unsupported bucket:

### 3.1 `pulldowns=1, passToBus=2+`(502 nodes)—— PPU 多 driver bus

```
ppu./exp_out0..3    : pullups=10, c1c2s=3, gates=0  -> PPU expansion bus output (5-driver wired-OR)
ppu./hvtog_inv      : pullups=4,  c1c2s=3, gates=0
ppu.pal_d0_out      : pullups=5,  c1c2s=3, gates=0  -> palette RAM read bus
ppu.+spr_pixel_*    : pullups=11, c1c2s=3, gates=5  -> sprite hit detection
ppu.vramaddr_v4_out : pullups=14, c1c2s=6, gates=0  -> VRAM scroll register output (5+ drivers!)
```

這些是 **multi-driver bus** —— 多個 transistor 透過 pass-mux 把不同 source 寫到同一個輸出 node。Pattern:
```
output -- [select_A:T1] -- src_A
output -- [select_B:T2] -- src_B
output -- [select_C:T3] -- src_C
output -- [pull-down:T_PD] -- Gnd  (1 個 pull-down or 0 個)
```

Function:`output = (select_A ? src_A : floating) | (select_B ? src_B : floating) | ...`,當所有 select 皆 0 時 floating(pull-up = 1)。

需要新 pattern:`mux_bus`(or `wired_or_select`)。預期 cover ~500 個 node。

### 3.2 `pulldowns=2, passToBus≥1`(280 nodes)

可能是 **NAND + latch-write** 或 **AOI(AND-OR-Invert)+ pass**。需 sample 後判斷。

### 3.3 `pulldowns=0, passToBus≥0` 非 NAND(93 nodes)

純 pass-through node:沒任何 pull-down,只有 pass-transistor 從別處 route 訊號進來。可能是 routing intermediate;codegen 可能不需要(因為下游可以直接讀 source)。先 deprioritize。

---

## 4. CLI

```
--aot-coverage <rom>           掃整 netlist,印 pattern histogram + top-3 unsupported bucket sample
```

---

## 5. 下一步建議

依照 priority + ROI:

1. ⭐ **`mux_bus` pattern**(~502 nodes) —— PPU 大量 multi-driver bus,加完後預估覆蓋率 → 92%
2. **`pulldowns=2 + pass`**(280 nodes) —— NAND with latch-write,加完 → 94%
3. **External input filtering** —— 把 `no-pullup with c1c2s=0` 從 coverage 分母排除,讓報表更準
4. **Block-level emit** —— 從 `Partition.Block` 出發產 block.cs 檔(目前只有 per-node delegate)
5. **Dispatcher 整合** —— event-driven 觸發 block eval

預估 1-4 累積完成後,emittable coverage ~95%,可開始 Phase D(50+ block 自動編出來 + verify)。

---

## 6. 一句話

> **AotEmitter Phase C 第一輪:加 NAND pattern + 全 netlist 84.5% emittable subset coverage(5,402 / 6,396 nodes 用 inverter / NOR / NAND 三大 pattern 即可)。下一個最大槓桿是 `mux_bus`(多 driver wired-OR,~500 nodes)+ `pulldowns=2+pass`(NAND with latch-write,~280 nodes),補完後預估 95%+ 覆蓋率,開始 Phase D。**
