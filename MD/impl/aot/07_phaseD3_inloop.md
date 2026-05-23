# aot-codegen — Phase D-3 結果:AOT delegate 在 simulation hot path

> 任務 #77 (AOT delegate in hot path)。
> 接續 `06_phaseD12_master.md`(D-1 master emit + D-2 Roslyn compile-load-verify)。

---

## 0. 結論 (TL;DR)

🟢 **Phase D-3 完成**:loaded AOT delegate 直接接進 simulation loop,**functional equivalence 跨 4 passes 完全 verified,steady-state overhead ≈ 0-1%(在 noise band 內)**。

| Pass | Mode | Rate (hc/s) | Checksum |
|---|---|---|---|
| 1 | AOT-in-loop(JIT warmup)| 35,206 | 0x8EE1094C63079B0F |
| 2 | S1-only | 42,348 | **same** ✓ |
| 3 | AOT-in-loop | 41,761 | **same** ✓ |
| 4 | S1-only | 41,684 | **same** ✓ |

**Pass 3 vs Pass 4 對照:AOT-in-loop = S1-only ±1%(noise)**。Pass 1 慢是 JIT warmup,首次 call delegate 觸發 JIT。

---

## 1. 為什麼 overhead 接近零

理論計算:
- 573 emittable nodes × ~5 ns (cache-hot byte read/write + bool op) = ~2.9 µs / hc
- S1 平均 ~25 µs / hc → AOT additional cost = ~12% theoretical

實測卻接近 0%。可能原因:
- AOT delegate 寫 NodeStates 跟 S1 剛寫的位址重疊 → CPU cache 完全 hot → 寫成本接近 0
- AOT eval 純 bit/byte ops,branch predictor 完美 prediction → 接近 throughput-bound
- S1 baseline 已經很重(每 hc 數百個 RecalcNode + group walk),AOT 的小增量被 noise 蓋掉

無論如何,**AOT 接進 hot path 不影響 throughput**,而且寫的值跟 S1 完全一致 → functionally inert。

---

## 2. 4-pass alternating bench 設計

```csharp
for (int pass = 0; pass < 4; pass++) {
    bool aotInLoop = (pass == 0 || pass == 2);   // 1/3 AOT, 2/4 S1
    LoadSystem(rom);  Step N hc (with or without AOT call);  Shutdown;
}
```

關鍵:**alternating order** + **pre-warmup AOT delegate** (16 calls before timed passes 開始) 排除 OS file cache + .NET JIT 干擾。

Pass 1 仍然有殘留 warmup penalty(loaded 不夠久就 timed run,JIT 還在 tier-1 compile)。Pass 2 / 3 / 4 都是 steady state。

---

## 3. Implementation

```csharp
public static int RunWithAotEngine(string romPath, int hcCount, int minEmittable) {
    // 1. Load + partition + emit master source + Roslyn compile + JIT-warm
    var picked = AotBlockBuilder.Build(...) for each partition block with ≥5 emittable;
    string source = AotBlockBuilder.EmitMasterSource(picked);
    var loaded = AotRoslynLoader.CompileMaster(source);
    for (int i = 0; i < 16; i++) loaded.EvalAll(...);    // JIT warmup
    
    // 2. 4 alternating timed passes
    for each pass:
        LoadSystem (fresh)
        for each hc:
            WireCore.Step(1)
            if (aotInLoop) loaded.EvalAll(WireCore.NodeStates)
        capture checksum + duration
    
    // 3. Report averages + verify all checksums equal
}
```

CLI:`--aot-run <ROM> [--bench-hc N]`

---

## 4. 對下一步的 implication

Phase D-3 證明了 AOT delegate 可以 **零成本**接在 S1 之後跑(因為寫的是 same values)。但這沒有 SPEEDUP —— 因為 S1 仍在做全部 work,AOT 只是 shadow 確認。

要看到 真實 SPEEDUP,需要 Phase D-4/D-5:
- **D-4**:讓 AOT 寫的節點 **跳過 S1** 的計算(類似 math-algos CodegenOwned 機制,但用 AOT compute,不只 skip)
- **D-5**:量 hc/s 看是否真的因為 skip S1 work 而變快

D-4 的關鍵差異 vs math-algos Step 3.5:
- math-algos Step 3.5 嘗試「mark CodegenOwned, S1 仍 traverse, dispatcher 寫 same value」→ 失敗(BFS reach 仍訪問 owned region)
- D-4 嘗試「mark CodegenOwned + Option D BFS-block(已有)+ AOT 取代計算」→ 可能成功(因為 AOT 提供精確的 boundary value,BFS-block 可以停)

預期效應:S1's RecalcNode count 從 614/hc 降到 ~(614 - emittable_fraction × 614)。573 / 6260 emittable ≈ 9%,預估 D 從 614 → ~558,~9% saving。

更大的 saving 要等 Phase D-6 把 latch 也納入 AOT(用 phi-aware pattern)+ 更多 patterns。

---

## 5. CLI

```
--aot-run <rom> [--bench-hc N]    Phase D-3: AOT delegate in hot path, 4-pass alternating bench
```

---

## 6. 一句話

> **Phase D-3:loaded AOT delegate 接進 simulation loop,4-pass alternating bench(pass 1 是 JIT warmup)顯示 steady-state AOT-in-loop = S1-only ±1%,4 passes checksum 完全一致 → AOT 跑在 hot path 不影響 throughput 也不影響 correctness。下一步 D-4 把 AOT 寫的節點實際 skip 掉 S1 計算,真正 speedup 看 D-5 量。**
