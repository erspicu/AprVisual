# S1 資料快取(L-cache)miss 熱點分析 — 哪幾個 array、在哪幾個 method(含硬體 IBS 實測)

> 日期:2026-06-20 · 引擎:`src/AprVisual.S1`(main) · ROM:`full_palette.nes` `--extra-ram` · 硬體:① Raspberry Pi 5 / Cortex-A76(L1d=64KB, L2=512KB/core, L3=2MB) ② AMD Ryzen 7 3700X / Zen2(L1d=**32KB**, L2=512KB/core, L3=16MB/CCX) · checksum golden

## 方法(三路互補)
- **Footprint**(`--array-footprint`,opt-in,Release 可用):啟動時印各熱路徑非託管陣列的 base+size → 哪些超過 L1d/L2。
- **Pi perf stat 分層**:`L1-dcache / l2d_cache / LLC` 的 loads + misses → **L1/L2/L3 各層 miss 總量**。
- **Zen2 AMD IBS 實測 per-array**(新):IBS-op `loadstore` 取樣每筆 load 的 **data 線性位址 `IbsDcLinAd` + `IbsDcMiss`(L1)+ `IbsL2Miss`(L2)+ 延遲**,用同一次 run 的 footprint 位址**分桶到各陣列** → 真正硬體量到「哪個陣列在 miss」。
  - ⚠️ IBS 一度在這台採 0 樣本;查出是 **x2APIC 擋住 IBS 的 EILVT 中斷**(PMC 正常但 IBS 不觸發)。修法:`bcdedit /set x2apicpolicy Disable` + `uselegacyapicmode Yes` + 重開機 → IBS 正常。詳見 `tools/操作筆記_AMD_uProf.md`。

## 量測一:陣列 footprint(NodeCount=14,730)
| 陣列 | 大小 | A76 L1d 64KB | Zen2 L1d 32KB | 熱路徑角色 |
|---|--:|---|---|---|
| **NodeInfos** | 230 KB(熱集 129KB)| 超出 | 超出 | 每 pop 讀(dispatch/解析/BFS)|
| **TransistorList** | 232 KB | 超出 | 超出 | turn-on enqueue + overflow walk |
| **TransistorListOff** | 92 KB | 超出 | 超出 | turn-off enqueue |
| RecalcList / Next | 57.5 KB ×2 | ~相當 | 超出 | work-list,**串流式**(順序)|
| NodeTlistGates / Off | 57.5 KB ×2 | ~相當 | 超出 | SetNodeState 取 sublist 偏移 |
| NodeStates | 14.4 KB | **進 L1d** | **進 L1d** | 到處讀 gate 狀態(讀最多)|
| IsPureLogic | 14.4 KB | 進 L1d | 進 L1d | 每次 dispatch 讀 |
| RecalcHash / Next | 14.4 KB ×2 | 進 L1d | 進 L1d | dedup 點陣圖(散讀)|
| FlagsToState | 0.2 KB | 常駐 | 常駐 | 解析 LUT |
NodeInfos 熱集(co-activity)= 7,542 節點 / 2,074 lines / **129KB**(理想 117KB)→ packing 已近最佳。

## 量測二:perf stat 分層(Pi A76,400k hc)— L1 / L2 / L3
| 層級 | 數量 | 率 |
|---|--:|--:|
| L1-dcache-loads | 15,890,804,878 | — |
| **L1-dcache-load-misses** | **203,560,006** | **1.28%** of L1 loads(≈6.2 MPKI)|
| l2d_cache(L2 存取)| 574,199,336 | — |
| **l2d_cache_refill(L2 miss)** | **33,647,712** | **5.9%** of L2 |
| LLC-loads(L3)| 71,484,189 | — |
| **LLC-load-misses(L3→DRAM)** | **34,269,851** | 47.9% of L3,但 **僅 86 loads/hc** |

階梯:15.9G L1 讀 → **203.6M miss L1(1.28%)** → ~170M 命中 L2(便宜 ~15c)→ **33.6M miss L2** → **34.3M 到 DRAM(~100c)= 86/hc**。

## 量測三:Zen2 IBS 實測 per-array(2 次 run 合併,user-mode in-array ≈ 271 loads / 15 L1-miss / 5 L2-miss)
| 陣列 | loads | load% | **L1-miss** | **L2-miss** | avg miss-lat | 判讀 |
|---|--:|--:|--:|--:|--:|---|
| **NodeStates** | 115 | 42% | **~0** | 0 | ~9c | 讀最多但 **L1 常駐 → 幾乎不 miss**(印證 footprint)|
| **TransistorList + …Off** | 108 | 40% | **12(≈80%)** | **4(≈80%)** | 21–87c | **真正的 L1/L2 miss 熱點**(散在各節點 sublist,92–232KB)|
| NodeInfos | 13 | 5% | 1 | 0 | 9c | 偶發 |
| RecalcHash / Next | 30 | 11% | 1 | 1 | 8c | dedup 散讀,少量 |
| FlagsToState / IsPureLogic | ~7 | 3% | 0 | 0 | — | 常駐 |
> in-array 只佔 user-mode load 樣本的 ~0.3%(其餘是 .NET heap 44% / stack 20% / JIT 等)—— **Gemini 確認這對 .NET 熱迴圈是正常**(指標解參考、stack spill、loop control 主導 load 數;陣列元素 load 本就是少數)。故在 in-array 內正規化。miss 樣本偏少(統計噪音 ±15%),但**兩次獨立 run 模式一致**:NodeStates 讀多不 miss、transistor 清單 miss。

## 結論:哪幾個 array、哪幾個 method
1. **NodeStates(14KB)= 讀取量第一,但 L1 常駐 → 幾乎不貢獻 miss**(IBS 實測 ~0 miss、延遲 ~9c)。這就是為什麼 L1-miss 率只有 1.28%(billions 的 load 大多打在常駐的 NodeStates)。在 `RecalcNodeFast` / `AddNodeToGroup` 讀。
2. **transistor 鄰接清單(TransistorList 232KB + TransistorListOff 92KB)= 真正的 L1/L2 miss 熱點**(IBS:佔 in-array miss ~80%、延遲 21–87c)。散在各節點的 sublist、超過 L1d/L2 的工作集。發生在 **`SetNodeState` 的 turn-on / turn-off enqueue 迴圈**(讀 `TransistorList[+NodeTlistGates[nn]]` / `TransistorListOff[+NodeTlistGatesOff[nn]]` 的 quad)+ `AddNodeToGroup` overflow。
3. **NodeInfos(230KB)**:每 pop 讀,偶發 miss;熱集 129KB 已近最佳 packing。
4. **L3 / DRAM**:僅 86 loads/hc 到 DRAM。**熱工作集大多裝得進 L2(L2 命中率 ~94%)**,故 L1-miss 絕大多數是便宜的 L2 命中(~15c);只有超過 L2 的少量尾巴(transistor 清單散讀 + 冷啟動)才到 L3/DRAM。完整足跡(~850KB)仍 > L2(512KB),所以 L3 尾巴非零、但對穩態不是主角。

## 總判:快取 miss 不是瓶頸
L1d miss 僅 1.28%、到 DRAM 僅 86 loads/hc。backend stall(19.5%)主要是**相依鏈上 L1/L2 命中的 load-to-use 延遲**(~4–15c × 序列鏈),**不是 cache miss**。若要動 miss,唯一有意義的對象是 transistor 清單(散讀),但那是 enqueue 的本質結構,且 miss 量本來就小 → 報酬有限。呼應記憶體延遲為架構天花板的結論。

## 工具 / 重現
- `--array-footprint`(opt-in;`TestRunner`):印各陣列 base+size。
- Pi:`sudo perf stat -e L1-dcache-loads,L1-dcache-load-misses,l2d_cache,l2d_cache_refill,LLC-loads,LLC-load-misses -- <Release run>`。
- Zen2 IBS:`AMDuProfCLI collect -e event=ibs-op,loadstore,interval=50000 -o OUT <exe> … --array-footprint`(**free-run,不要用 `--affinity`/`-c`** —— 會因核編號錯位 + .NET 執行緒擠壓毀掉位址對應;`os=0` 需 Zen6+)→ `report --ascii ibsop-event-dump` → `tools/ibs_bucket.py` 依 footprint 分桶。樣本要足(想要 ~1000 in-array 需跑數分鐘)。
