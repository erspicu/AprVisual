# aot-codegen — Phase C-3 結果:norN+pass + 97.9% emittable coverage

> 任務 #72 (norN+pass pattern)。
> 接續 `03_phaseC2_muxbus.md`(mux_bus + 93.1% coverage)。

---

## 0. 結論 (TL;DR)

🟢 **Phase C-3 完成,接近 emittable subset 天花板**:

| 指標 | C-2 | **C-3** | Δ |
|---|---|---|---|
| Pattern coverage(emittable subset)| 93.1% | **97.9%** | **+4.8 pp** |
| Pattern coverage(全 netlist)| 40.4% | 42.5% | +2.1 pp |
| Total supported nodes | 5,955 | **6,260** | +305 |
| Byte-equal accuracy | 99.74% | **99.75%** | +0.01 pp |
| 100% PERFECT patterns | 17 / 18 | **24 / 31** | +7 perfect |

**剩 136 個 emittable unsupported nodes**(都是 edge case — `pulldowns=0` 純 pass-through、`pulldowns≥3 + pass≥2` 複雜 AOI/OAI 結構)。

---

## 1. 新 Pattern:`norN+pass`(N=2..16)

```
output ── [pull-up]
output ── [g0 : T0] ── Gnd
output ── [g1 : T1] ── Gnd      (N 個 pull-down gates)
...
output ── [gN : TN] ── Gnd
output ── [latch_w_gate : T_pass] ── other_node    (0 or 1 個 pass-to-bus)
```

Detection:`pullDownGates.Count >= 2 && passToBusCount <= 1`
Eval:`NOT(g0 | g1 | ... | gN)`(忽略 dormant pass)
Pattern label:`norN`(無 pass)/ `norN+pass`(1 pass)

Coverage 增量:
- `nor2+pass`: 267 nodes
- `nor3+pass`: 34 nodes
- `nor4..nor16+pass`: ~80 nodes
- Total +381 nodes

---

## 2. 重要設計教訓:Pattern boundary 不能太貪

**第一個嘗試**: 把 inverter 從 `passToBus≤1` 放寬到 `passToBus≤2`,順便覆蓋 359 nodes(原本歸 mux_bus+pulldown)。

**結果**:Coverage 漲到 43.0%,但:
- `inverter+pass2`(359 nodes):**2.302% miss rate**(WORSE than 0%)
- 把 100% PERFECT 的 mux_bus 領域吃進來變壞

**修正**:revert,inverter 只認 `passToBus≤1`;mux_bus+pulldown 保持 502 nodes 100% PERFECT。

同理,nor+pass 第一版 `passToBus≤2`:nor6+pass 的 8 個 nodes 跑出 **5.58% miss**,證明第二個 pass 不是 dormant。Narrow 到 `passToBus≤1`。

**Lesson**:當 pattern 有歧義(同樣 topology 多種 semantic),寧可少 cover 也不要 over-eager。100% PERFECT 子集比 95% accurate 全集更值錢。

---

## 3. 最終 Pattern table(C-3 後)

| Pattern | Detection | Nodes | Accuracy |
|---|---|---|---|
| `inverter` | 1 pulldown + 0 pass + pullup | 1,860 | 99.46% |
| `inverter+latch-write` | 1 pulldown + 1 pass + pullup | 1,924 | 99.75% |
| `nor2..nor16`(無 pass)| N pulldown + 0 pass + pullup | 1,608+ | **100% PERFECT (all variants)** |
| `nor2+pass..nor16+pass` | N pulldown + 1 pass + pullup | 381 | 99.5-100%(部分 100% perfect)|
| `nand` | 0 pulldown + 1 pass-to-mid(mid 1 pulldown + 無 pull-up)| 105 | 99.81% |
| `mux_bus` | 0 pulldown + 2+ pass + pullup | 51 | 99.03% |
| `mux_bus+pulldown` | 1 pulldown + 2+ pass + pullup | 502 | **100% PERFECT** |
| **Total** | | **6,260** | **99.75% overall** |

---

## 4. Verify-all 完整 breakdown(30K hc × 6,260 nodes = 187.8M samples)

```
=== per-pattern verification ===
✓ All nor2..nor16 (no pass)        : 0 mismatches (PERFECT)
✓ mux_bus+pulldown                 : 0 mismatches (PERFECT)
✓ Most nor*+pass variants          : 0 mismatches (PERFECT)
· inverter                         : 300,000 / 55.8M = 0.538%   phi-transient
· inverter+latch-write             : 144,240 / 57.7M = 0.250%   phi-transient
· nand                             : 5,843   / 3.15M = 0.185%   phi-transient
· mux_bus (no pulldown, 51 nodes)  : 14,807  / 1.5M  = 0.968%   wired-OR transient
· nor2+pass                        : 17,869  / 8.0M  = 0.223%   latch-write transient
· nor3+pass                        : 2,496   / 1.0M  = 0.245%
· nor4+pass                        : 2,568   / 480K  = 0.535%
· nor5+pass                        : 24      / 330K  = 0.007%

GRAND TOTAL: 473,432 / 187,800,000 = 0.2521% miss = 99.7479% accurate
```

幾乎所有 mismatch 都集中在 phi-transient(latch 寫入瞬間) + first-hc init noise。可在 Phase C-7 用「sample only at phi-stable 邊界」清掉。

---

## 5. 剩 136 個 unsupported nodes 的分類

```
unsupported(pulldowns=0,...)             :     42  (純 pass-through:無 pulldown + 無 NAND mid → 通常是 routing intermediate,通常不需 emit)
unsupported(pulldowns=2,...)             :     13  (NAND-like + pass≥2,真的多 driver)
unsupported(pulldowns=3..29,...)        :    ~80  (複雜 AOI/OAI;少數但複雜)
unsupported(其他)                       :      1  (邊角)
total                                   :    136  / 6,396 emittable = 2.1%
```

進攻這 136 個的 cost-benefit 不高(占 emittable 2.1%,涉及多種 AOI/OAI 變體 pattern detection)。**建議 Phase D 直接開做**(整 chip dispatcher + 多 block 驗證),把這 136 nodes 留 S1 fallback 接住。

---

## 6. Phase C 完整成果統計

| Phase | 新 patterns | Coverage | Accuracy |
|---|---|---|---|
| A | inverter (hand) | n/a | IR inverter 100% |
| B | auto-emit | n/a | same |
| C-1 | nand + per-arity nor | 84.5% | 99.7% |
| C-2 | mux_bus | 93.1% | 99.74% |
| **C-3** | norN+pass | **97.9%** | **99.75%** |

**Phase C 總計加了 5 大 pattern family + 一個 batch verifier + coverage scanner,從 0% 推到 97.9% emittable coverage。**

---

## 7. 下一步建議

按 ROI 排序:

1. ⭐ **Phase C-5 — Block-level emit**:從 `Partition.Block`(Step 3 工具產出的 30 個 codegen-attractive blocks)出發,為每個 block 產一個 `.cs` 檔含整 block eval。準備 Phase D 的 source code。
2. ⭐ **Phase C-6 — Dispatcher 整合**:用 math-algos 的 `Dispatcher.cs`(bitmask polling),event-driven 觸發各 block eval。
3. **Phase C-7 — phi-transient accuracy**:把剩 0.25% mismatch 抓回 100%(sample only at phi-stable instants)。
4. **Phase C-8 — AOI/OAI patterns**:剩 136 nodes 內的複雜結構,可能 cover 50-100 個。
5. **Phase D — 50+ block trace verify**:在 C-5 + C-6 完成後,跑完整 ROM trace,驗 AOT engine ≡ S1。

C-5 + C-6 是 Phase D 的 prerequisite。完成後就有 demo-able "AOT engine running NES" 的東西。

---

## 8. 一句話

> **Phase C-3 narrow 完成 norN+pass pattern(N=2..16,passToBus≤1)→ emittable coverage 97.9%(6,260 / 6,396 nodes,只剩 136 個 edge-case unsupported)+ accuracy 99.75%。重要教訓:pattern boundary 不能 over-eager,100% PERFECT 子集比 95% all 更值。下一步進 Phase C-5(block-level emit)+ C-6(dispatcher 整合)鋪 Phase D 路。**
