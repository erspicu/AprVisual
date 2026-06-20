# S1 資料快取(L-cache)miss 熱點分析 — 哪幾個 array、在哪幾個 method

> 日期:2026-06-20 · 引擎:`src/AprVisual.S1`(main) · 量測:`full_palette.nes` 400,000 half-cycles `--extra-ram` · 硬體:Raspberry Pi 5 / Cortex-A76(L1d=64KB, L2=512KB/core, L3=2MB shared) · checksum golden `0x9174E19D961CB6E5`

## 方法
- **Footprint(`[array-footprint]` DEBUG dump)**:每個熱路徑非託管陣列的 base + byte size → 哪些超過 L1d/L2。
- **perf stat 分層**(Pi):`L1-dcache-loads/-misses`、`l2d_cache/_refill`、`LLC-loads/-misses` → 各層 miss 總量。
- **熱集**(`[co-activity]`):實際被碰到的 NodeInfos 子集大小。
- **方法存取頻率**(`[hotpath-calls]` / `[branch-dist]`,已 committed)→ 哪個 method 碰哪個 array、碰幾次。
- ⚠️ **per-array 硬體取樣(ARM SPE)在此 Pi kernel 不可用**:`# CONFIG_ARM_SPE_PMU is not set` + 無 device-tree SPE 節點 + `perf mem` 回「no PMU supports the memory events」。要開需重編 kernel(`CONFIG_ARM_SPE_PMU=y`)+ 補 BCM2712 DT 的 SPE PMU 中斷節點 —— 非 apt 可裝、有開不了機風險、DT 那關不確定,故未做。下面以 footprint + 熱集 + 方法頻率 + perf-stat 分層歸因(嚴謹,但精確 per-array % 是推論非直接取樣)。

## 量測一:陣列 footprint(NodeCount=14,730)
| 陣列 | 大小 | vs L1d(64KB)| 熱路徑角色 |
|---|--:|---|---|
| **NodeInfos** | **230.2 KB**(熱集 **129 KB**)| **超出 3.6×** | 每 pop 讀 `NodeInfos[nn]`(dispatch/解析/BFS)|
| **TransistorList** | **231.9 KB** | **超出 3.6×** | turn-on enqueue + overflow group walk |
| **TransistorListOff** | **91.9 KB** | **超出 1.4×** | turn-off enqueue |
| RecalcList / Next | 57.5 KB ×2 | 略小/相當 | work-list,**串流式**(順序 pop/append)|
| NodeTlistGates / Off | 57.5 KB ×2 | 略小 | SetNodeState 取 sublist 偏移(每次寫一次)|
| NodeStates | 14.4 KB | **進得了 L1d** | 到處讀(gate 狀態)—— 存取最多但 L1 常駐 |
| IsPureLogic | 14.4 KB | **進得了 L1d** | 每次 dispatch 讀 |
| RecalcHash / Next | 14.4 KB ×2 | **進得了 L1d** | dedup 點陣圖(散讀)|
| FlagsToState | 0.2 KB | L1 常駐 | 群組解析 LUT |

熱集(co-activity):被 pop 過的節點 7,542 個,落在 **2,074 條 cache line = 129 KB 的 NodeInfos**(理想 1,886 lines = 117 KB)→ renumber 的 NodeInfos packing 已接近最佳(~10% 額外),**沒什麼可再縮**。

## 量測二:perf stat 分層(400k hc)
| 層級 | 數量 | miss 率 |
|---|--:|--:|
| L1-dcache-loads | 15,890,804,878 | — |
| **L1-dcache-load-misses** | **203,560,006** | **1.28%** of L1 loads |
| l2d_cache(L2 存取)| 574,199,336 | — |
| l2d_cache_refill(L2 miss)| 33,647,712 | 5.9% of L2 |
| LLC-loads(L3)| 71,484,189 | — |
| LLC-load-misses(到 DRAM)| 34,269,851 | 47.9% of L3 |

階梯:15.9G L1 讀 → **203.6M miss L1(1.28%)** → ~170M 命中 L2 → **33.6M miss L2** → **34.3M 到 DRAM**(= 86/hc,~100 cycle 才貴)。

## 歸因:哪幾個 array、哪幾個 method
**L1-miss(203.6M)的來源(依 footprint > L1d × 存取頻率):**
1. **NodeInfos —— #1(估 ~一半)**。熱集 129KB = 2× L1d,且**每 pop 都讀 `NodeInfos[nn]`**(散讀)。存取點:
   - `RecalcNodeFast`(128M 次,O(1) 解析讀 NodeInfos[nn])
   - `RecalcNode` cls2 inline 掃描(讀 NodeInfos[nn])
   - `AddNodeToGroup`(BFS 每個群組節點讀 `NodeInfos[o]`,27.8M walks × 深度1.25 ≈ 35M node-visits)
   → 約 ~180M NodeInfos 結構載入,工作集 2× L1d → 大量 L1-miss(命中 L2)。
2. **TransistorList + TransistorListOff —— #2(估 ~四成)**。都 > L1d,**散在各節點的 sublist**:
   - `SetNodeState` turn-on enqueue:讀 `TransistorList + NodeTlistGates[nn]` 的 quad(~108.5M transistor 讀)
   - `SetNodeState` turn-off enqueue:讀 `TransistorListOff + NodeTlistGatesOff[nn]`(~半數 SetNodeState,~110M+)
   - `AddNodeToGroup` overflow(~4% 節點)讀 TransistorList
3. **NodeStates / IsPureLogic / RecalcHash —— 低 miss**。各 14KB **進得了 L1d**;雖然 NodeStates 是 15.9G 次讀的大宗(這就是 L1-miss 率只有 1.28% 的原因 —— 絕大多數讀的是 L1 常駐的 NodeStates),但它常駐 → 幾乎不 miss。
4. **RecalcList/Next —— 低 miss**。57KB 但**順序存取** → 硬體預取吃掉。

**到 DRAM 的 34.3M(最貴的)**:總熱足跡(NodeInfos 230 + TransistorList 232 + Off 92 + lists 230 + …)≈ 736 KB **超過 L2(512KB)** → 溢出;這 34M 多半是 NodeInfos/TransistorList 超出 L2 的散讀 + 冷啟動。

## 結論
- **快取 MISS 不是瓶頸**:L1d miss 僅 1.28%、真正到 DRAM 的只有 86 loads/hc。backend stall(19.5%)主要是**相依鏈上 L1/L2 命中的 load-to-use 延遲**(~4–15 cycle × 序列鏈),不是 miss。
- **若論 miss,主嫌是超過 L1d 的三個 array**:**NodeInfos(#1)**、**TransistorList/Off(#2)**;發生在 **`RecalcNodeFast` / `AddNodeToGroup`(NodeInfos)** 與 **`SetNodeState` enqueue(TransistorList/Off)**。
- NodeInfos 熱集已近最佳 packing(2074 vs 理想 1886 lines),**縮無可縮**;這也呼應先前 density-renumber 中性、記憶體延遲為架構天花板的結論。

## Caveat / 為何不做軟體 per-array 計數器
軟體「每 array 存取計數」會被 **JIT 的 CSE / 暫存器快取**混淆(一個邏輯存取 ≠ 一次硬體 load),會給出假精確。真正的 ground truth 是上面的 **perf-stat 分層總量 + footprint + 方法頻率**;精確 per-array 硬體 miss 拆分需要 **ARM SPE(本 Pi kernel 沒開,需重編)或 x86 AMD IBS**。

## 工具
- `[array-footprint]`(`#if DEBUG`,TestRunner;Release byte-identical):各陣列 base+size。
- perf:`sudo perf stat -e L1-dcache-loads,L1-dcache-load-misses,l2d_cache,l2d_cache_refill,LLC-loads,LLC-load-misses -- <Release run>`。
- 搭配 `[hotpath-calls]`(方法呼叫次數)、`[co-activity]`(熱集 line 數)。
