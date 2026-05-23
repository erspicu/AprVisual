# aot-codegen — Phase E-1 結果:latch patterns,88.8% total coverage

> 任務 #79 (latch_storage pattern)。
> 接續 `08_phaseD4_skip.md`(D-4 撞牆,user 選 Route B 完整 pivot)。
> 性質:Route B Phase E-1 第一塊 — node coverage 從 40% 一跳 88.8%。

---

## 0. 結論

🟢 **Phase E-1**:加 3 個 latch patterns(latch_simple / latch_complex / latch_pd_pass),AOT coverage **40.4% → 88.8%**(+7,117 nodes)。剩 1,655 unsupported(11%)。

---

## 1. No-pullup inventory(designed E-1 around this)

`--aot-nopullup-scan` 把 8,331 個 no-pullup nodes 分類:

```
external_drive (c=0,g>=1) :    26 (0.3%)   handler-driven (clk/res/BA0)
external_anon  (c=0,g=0)  :    46 (0.6%)   callback fake nodes
latch_simple   (c=1)      : 1,375 (16.5%)   1 pass-transistor
latch_complex  (c=2)      : 5,644 (67.7%)   2-pass dynamic latch ★ HUGE
latch_or_bus   (c>=3)     : 1,240 (14.9%)   multi-pass routing/bus
```

67.7% 都是 2-pass latch! 這對 6502 / 2C02 微結構符合預期:典型 master-slave dynamic latch + 1 pulldown + 1 write-pass。

---

## 2. 3 個新 patterns

### latch_simple (1,312 nodes)
```
node O -- [gate G : T] -- source S
```
Eval:
```csharp
return ns[G] != 0 ? ns[S] : ns[O];   // transparent or hold
```

### latch_pd_pass (4,739 nodes) ★ 最大宗
```
node O -- [pdGate : T1] -- Gnd       (pulldown)
node O -- [writeGate : T2] -- src    (latch-write pass)
```
Eval:
```csharp
if (ns[writeGate] != 0) return ns[src];   // write
if (ns[pdGate] != 0)    return 0;          // pulldown
return ns[O];                              // hold
```

這是 NMOS dynamic-node 的典型 topology:write-pass loads from source on phi-active edge,pulldown 在另一相態下強制 0,沒驅動時 hold previous。

### latch_complex (761 nodes)
```
node O -- [gA : T1] -- src_A
node O -- [gB : T2] -- src_B    (兩個 source mux)
```
Eval:
```csharp
bool actA = ns[gA] != 0, actB = ns[gB] != 0;
if (actA && actB) return (byte)(ns[srcA] & ns[srcB]);   // GND wins (NMOS wire-OR)
if (actA) return ns[srcA];
if (actB) return ns[srcB];
return ns[O];   // hold
```

---

## 3. 量測

```
# aot-coverage scan over 14,727 live nodes
# supported    : 13,072  (88.8%)  ← 從 40.4% 跳到 88.8%
# unsupported  :  1,655  (11.2%)

# top patterns:
#   latch_pd_pass    : 4,739 (32.2%)  ★ 最大 contributor
#   inverter+lw      : 1,924 (13.1%)
#   inverter         : 1,860 (12.6%)
#   latch_simple     : 1,312 ( 8.9%)
#   nor2             : 1,022 ( 6.9%)
#   latch_complex    :   761 ( 5.2%)
#   mux_bus+pulldown :   502 ( 3.4%)
#   ... 其餘 patterns
```

### 剩餘 1,655 unsupported

- `unsupported(no-pullup, c>=3)`: 1,312 → latch_or_bus(多 pass routing/bus)
- `unsupported(latch2_other)`: 142 → 含 pull-up 或 pwr-edge 的 latch2 variant
- `unsupported(latch2_gate_supply)`: ~50 → gate=Npwr/Ngnd 永久 on/off
- 其他 emittable 但 pattern 不 match: ~150

進攻這 1,655 個的 ROI 低(需要多 pass routing pattern + 各種 edge case)。**Phase E-2 直接開做 AotRuntime,這 11% 留 fallback。**

### Verification 注意

`--aot-verify-all` 對 latch 的 0% miss 是 **tautological**:
- `latch.Eval(snapshot)` 在 gate 不 active 時 return `snapshot[O]`
- 跟 `snapshot[O]` 自己比 trivially equal
- Real verification 需要 Phase E-2 AotRuntime 真實 step 跑出來 propagation

也就是說 latch patterns 的 BYTE-CORRECTNESS 還沒被嚴格驗證,只是 framework 不爆炸。

---

## 4. Route B 接下來 Phase 路線

| Phase | 目標 | 工程量 |
|---|---|---|
| ✅ E-1 | latch patterns → 88.8% coverage | small (this session) |
| ⏭ E-2 | AotRuntime 骨架:own NodeStates + fixed-point Step() | 中等 |
| ⏭ E-3 | 加 clock/reset propagation | 中等 |
| ⏭ E-4 | 加 memory handlers (RAM/ROM I/O) | 中等-大 |
| ⏭ E-5 | 小程式驗證(5-10 hc match S1)| 中等 |
| ⏭ E-6 | 完整 ROM trace verify | 大 |
| ⏭ E-7 | Perf baseline(AotRuntime hc/s vs S1)| 小 |

**E-2 是關鍵 milestone** —— 一旦 AotRuntime 跑得起來,就可以用它驗證 latch patterns 的 byte-correctness。

---

## 5. CLI

```
--aot-nopullup-scan <rom>    inventory no-pullup nodes by topology
--aot-coverage <rom>          全 netlist 覆蓋率(現在 88.8%)
--aot-verify-all <rom>        per-pattern accuracy(latch 部分是 tautological)
```

---

## 6. 一句話

> **Phase E-1:加 3 個 latch patterns(simple / complex / pd_pass)cover 7,117 no-pullup nodes,AOT 覆蓋率 40.4% → 88.8%。Verification 對 latch 是 tautological(需 E-2 AotRuntime 真實 step 才能嚴格驗 byte-correctness)。下一步 E-2 開始建 AotRuntime 取代 S1 runtime。**
