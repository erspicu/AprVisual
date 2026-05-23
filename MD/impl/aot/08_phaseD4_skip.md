# aot-codegen — Phase D-4 結果:AOT-skip S1 撞同樣天花板(架構性結論)

> 任務 #78 (AOT skip S1 via CodegenOwned + Option D)。
> 接續 `07_phaseD3_inloop.md`(AOT in hot path, 0% overhead)。
> 性質:**Phase D-4 mirrors math-algos Step 3.5 Option D finding** — 架構限制再次撞牆。

---

## 0. 結論 (TL;DR)

🟡 **Phase D-4 重現 math-algos Phase 2.5 Step 3.5 Option D 的撞牆**:

| 指標 | 結果 |
|---|---|
| **AOT-skip vs S1 trace** | ❌ **DIVERGENCE**(checksum 不一致)|
| **AOT-skip vs S1 速度** | ❌ **SLOWDOWN 0.95×**(-5%)|
| 機制工程性 | ✓ AOT delegate + CodegenOwned + Option D BFS-block 都運作 |
| 跟 math-algos Step 3.5 比較 | **完全相同模式** —— mechanism works, correctness breaks, speed degrades |

**根本原因**(跟 math-algos 完全相同):
- Option D BFS-block 把 owned node 當「無限強度 supply rail」
- 即使 AOT 寫 byte-correct 值,unowned 鄰居節點的 group resolution dynamics 變了
- → cascade 影響:non-owned downstream 的 NodeStates 跟 baseline 微差
- → checksum diverge,而且因為 BFS 提早停會丟失 capacitance / largest-group-wins 細節

---

## 1. 測試設計 + 結果

`--aot-skip <ROM>` 跑 4-pass alternating bench:
- pass 1 + 3: AOT-skip mode(`AOT.EvalAll` 前 + `WireCore.Step(1)` 後)+ CodegenOwned 標記 567 nodes
- pass 2 + 4: S1-only(reset state via `WireCore.ClearAotOwnership()`)

```
# pass 1 AOT-skip-S1 :   37,824 hc/s  checksum 0x86121A4AD9BF9A66
# pass 2 S1-only     :   42,278 hc/s  checksum 0xA78725428025D2C9   ← 跟 pass 4 不同 (state leak)
# pass 3 AOT-skip-S1 :   43,965 hc/s  checksum 0x86121A4AD9BF9A66   ← AOT 自己內部一致
# pass 4 S1-only     :   43,013 hc/s  checksum 0x8EE1094C63079B0F   ← 真實 S1 baseline

# === Phase D-4 averaged ===
#   S1-only       avg:   42,642 hc/s
#   AOT-skip-S1   avg:   40,664 hc/s
#   speedup           : SLOWDOWN 0.95×
#   checksums all equal: ✗ DIVERGENCE
```

### 為什麼 S1 pass 2 跟 pass 4 不同 checksum

`_inGroup` / `RecalcList` 等 group-walk scratch state 是 unmanaged byte*,跨 `LoadSystem` cycle 有可能殘留。`ClearAotOwnership` 清了 CodegenOwned 但其他 scratch 沒清。**這不是 D-4 的核心發現,只是 bench harness 問題;真正的 baseline 是 pass 4 (0x8EE...) 配對 Phase D-3 的 baseline 一樣。**

---

## 2. 為什麼 mirrors math-algos Step 3.5 撞牆

| 對照 | math-algos Step 3.5 Option D | aot-codegen Phase D-4 |
|---|---|---|
| 機制 | BFS-block + dispatcher writes owned values | BFS-block + AOT writes owned values |
| Owned 數量 | 8 → 62 → 133 nodes(ALU 區）| 567 nodes(36 partition blocks)|
| 結果 | trace 64-line diff,speedup -3.2% | checksum 不同,speedup -5% |
| 根因 | Option D 把 owned node 當 supply,破壞 capacitance / largest-group-wins | **完全相同** |
| Gemini r3 §Q3 警告 | "capacitance 判定錯誤" | 真的發生 |

**Phase D-4 的 567 nodes 比 Step 3.5 的 62 nodes 更多(因為 partition 覆蓋更廣),但同樣撞同個牆**。Owned 多到能 cover 9% emittable nodes,理論上能省 9% S1 work,但 BFS-block 改變 group dynamics 的副作用比 saving 大。

---

## 3. 為什麼 D-3 沒撞牆但 D-4 撞了

| 模式 | AOT 何時 call | S1 如何處理 owned nodes | 結果 |
|---|---|---|---|
| **D-3** AOT-in-loop | step 之後 | 正常 BFS(沒 CodegenOwned)→ S1 算出 owned 的「真實值」→ AOT 直接 overwrite | ✓ 100% byte-equal,0% overhead |
| **D-4** AOT-skip-S1 | step 之前 | BFS-block(有 CodegenOwned)→ 把 AOT 值當 supply → group walk 中途停 | ❌ 改變 group dynamics → divergence |

關鍵 insight:**「shadow + overwrite」 (D-3) 是 architecturally safe 但沒 speedup;「skip + replace」 (D-4) 有 saving 機會但破壞 group dynamics**。

---

## 4. 跟 math-algos Step 3.5 同樣結論:這條 architecture 路無 speedup

Gemini r3 §Q5 戰略 pivot 是「**放棄 S1,純 AOT engine**」—— 不是「AOT 跟 S1 並存」。我們現在是 hybrid:
- D-3: AOT shadow S1(整 chip 仍 by S1)
- D-4: AOT cover 9% + S1 cover 91% → BFS 跨界破壞

要真正 speedup,要實作 Gemini 戰略的完整版:**100% AOT engine,不需要 S1 跑 runtime,S1 只當 Oracle 驗證**。但這需要解決:
- Latch 模型(no-pullup mids 怎麼處理)
- Clock/phi propagation(目前在 S1)
- Memory handlers(I/O callbacks,目前在 S1)
- 整 chip 14k nodes 的 AOT(目前只有 6.2k emittable)
- 136 個 unsupported nodes 的解法

工程量:大型重啟級別,類似 MetalNES 從零寫 simulator 的工程量。

---

## 5. Phase D 至此的成果評估

| Phase | 結果 | 是否真實 speedup |
|---|---|---|
| ✅ D-1 | Mass emit master .cs(115KB,36 blocks)| n/a |
| ✅ D-2 | Roslyn compile + load(100% byte-equal,567 nodes)| n/a |
| ✅ D-3 | AOT-in-loop(0% overhead,checksum identical)| ✓ correctness ❌ speedup |
| ⚠️ D-4 | AOT-skip-S1(checksum divergence,slowdown 5%)| ❌ both |

**Pipeline 完整可用(Phase D-2 證實),但「AOT 取代 S1 跑得更快」這層在 hybrid 模式下不可達。**

---

## 6. 兩個方向選擇

### 路線 A — 接受 hybrid 不能 speedup,改善 D-3 用途
- AOT-in-loop 模式(0 overhead,byte-equal)還是有用 ——
  - **multi-instance batch sim**:多個 NES 平行跑,每個用 AOT 取代部分 work,reduce CPU per instance
  - **debugger / oracle**:用 AOT 預測下一個 cycle 的 expected values,catch S1 bug
  - **regression test**:AOT delegate 是「不變的 truth」,S1 改動時可 catch

### 路線 B — 走 Gemini r3 §Q5 完整 pivot(100% AOT,放棄 S1 runtime)
工程量大(latch model + clock/phi + memory + 全 chip cover)。但這是唯一能 真正 speedup 的路。要做要規劃幾週工程。

### 路線 C — Branch wind-down
跟 math-algos 一樣,把 aot-codegen 標 Research-Complete:
- 完整 demo 過 pipeline(Gemini r3 §Q5 6 階段都 done)
- 證明了 mechanism works
- 確認了「hybrid 模式不能 speedup」的架構結論
- 工具(AotEmitter / AotBlockBuilder / AotRoslynLoader)留 branch 上供 cherry-pick
- Memory 補一筆「兩條 codegen 路徑都驗證撞同個架構牆」

---

## 7. 一句話

> **Phase D-4 完整重現 math-algos Step 3.5 Option D 的撞牆 ——「AOT 取代 S1」hybrid 模式下,BFS-block 改 group dynamics 破壞 correctness + 沒 speedup;這是 S1 group-resolution architecture 的固有限制,跟 owned nodes 多少無關。要真實 speedup 必須走 Gemini r3 §Q5 完整 pivot(100% AOT,放棄 S1 runtime),工程量大。給 user 三個選擇:用 D-3 模式 / 走完整 pivot / wind down。**
