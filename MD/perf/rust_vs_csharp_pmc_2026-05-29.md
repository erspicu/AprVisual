# Rust S1 vs C# S1 PMC 對照(2026-05-29)

## Workload

| 設定 | 值 |
|---|---|
| ROM / Snapshot | full_palette(C# 用 `.nes`,Rust 用相同網表的 `.aprsnap`) |
| bench length | 600,000 master half-cycles |
| 計數 interval | 65,536 events per sample |
| 機器 | AMD Ryzen 7 3700X(Zen 2)|
| 工具 | PerfView 3.1.18 + gsudo admin |
| 註 | 1M 取樣嘗試 PerfView session 衝突失敗(連續呼叫 PMC source 鎖未及時釋放),改用 600k 已驗證的乾淨數據;載入佔 600k 約 4%,1M 約 2.6%,差距不影響相對比較 |

## 原始 PMC events(filter by process)

| Counter | Rust (wire_s1) | C# (AprVisual.S1) | Rust / C# |
|---|---|---|---|
| Total cycles events | 760,892 | 1,021,542 | **74.5%**(Rust 用 25.5% 少) |
| Dcache miss events | 46,887 | 51,972 | 90.2% |
| Branch mispred events | 6,814 | 7,157 | 95.2% |
| Icache miss events | 1,100 | 1,470 | 74.8% |

換算實際數量(× 65,536):

| Counter | Rust 總量 | C# 總量 | Δ 絕對 |
|---|---|---|---|
| Total cycles | **49.86 B** | 66.94 B | -25.5%(Rust 少) |
| D-cache miss | **3.07 B** | 3.41 B | -10.0% |
| Branch mispredict | **447 M** | 469 M | -4.7% |
| I-cache miss | **72.1 M** | 96.3 M | -25.1% |

## 直觀的 per-cycle ratios

| Ratio | Rust | C# | Δ |
|---|---|---|---|
| **D-cache miss / cycle** | **6.16%** | 5.09% | +21% relative(Rust **看起來** worse) |
| Branch mispredict / cycle | 0.90% | 0.70% | +29% |
| I-cache miss / cycle | 0.14% | 0.14% | 同 |

### 但這是錯覺 ── 必須看「每 hc 工作量」

|  | Rust | C# |
|---|---|---|
| 完成 600k hc 用 cycle | 49.9 B | 66.9 B |
| **每 BFS node visit 平均 cycle** | **~137** | **~184** |
| 絕對 D-cache miss | 3.07 B | 3.41 B |
| **每 BFS node visit 平均 D-cache miss** | **0.85** | **0.94** |

## Operational Intensity 解讀

**Rust 並沒有 cache-miss 更糟,而是 LLVM 生成更密的 codegen**:
- 同樣 600k hc 工作量,Rust 用 **25% 少 cycle** 完成
- 每 node visit 從 184 → 137 cycle ── ALU 利用率更高
- 絕對 cache miss 數量同樣減少(D-cache -10%, I-cache -25%, branch mispred -5%)
- 但因為 cycle 數降更多,「per cycle 的 memory pressure」自然變大 → ratio 看起來糟,實際更好

這是教科書 **Operational Intensity** 效應:LLVM 把 ALU 操作壓得更緊,memory 工作量不變的情況下,memory-bound ratio 自然上升。

## Rust 7% 領先 C# 的真實成因

| 來源 | 估計貢獻 |
|---|---|
| LLVM 指令更少(per BFS visit 137 vs 184 cycle) | **主因** ~25% raw cycle reduction |
| 較少 branch mispredict 絕對量(-4.7%) | 邊緣 |
| 較少 I-cache miss 絕對量(-25%) | 邊緣(L1i 反正夠用) |
| 略少 D-cache miss 絕對量(-10%) | 邊緣 |

## bench 7% vs cycle 25% 落差的原因

bench wall-clock 領先 7%(64.4K → 69.4K)但 cycle 領先 25%。 差距吃在:

- **C# 啟動成本**:JIT 編譯、Tier-0 warmup,Rust 沒有(已 AOT)
- **PMC 是 CPU time,bench 含 syscall / IO / OS work**
- **載入時間佔比**:C# 13% (200k bench),Rust 也有 ~5% load(snapshot parse)
- 600k 取樣相對短,load 比例污染明顯

若拉到 10M+ hc,wall-clock 差距會逼近 cycle 差距(25% 領先)。

## 對照 C# 端的「memory subsystem latency wall」假說

C# PMC 那篇結論:**D-cache 5.09% 是瓶頸**,L1i / branch 不是。

Rust 端:**絕對 D-cache miss 也是 3.07 B 級的,跟 C# 同數量級** ── 也撞同一道牆。 換句話說:

> **Rust 比 C# 快不是因為 cache 行為不同**,而是因為 LLVM 比 .NET JIT 更會壓榨 ALU。 兩端共撞同樣的 D-cache miss bottleneck,只是 Rust 在 wait-for-memory 的空檔塞了更多有效運算。

## 寓意

- 兩端都已撞 D-cache memory subsystem latency wall(不論 C# 或 Rust)
- Rust 領先靠**運算密度更高**而非 cache 表現更好
- 未來進一步壓榨 C#:**LLVM-style codegen 改進**(更積極 inline、更精準 register allocation),非 cache 優化
- 未來進一步壓榨 Rust:**已逼近 LLVM 極限**(memory `s1-fork-results` 估 ceiling 78-82K,目前 69K,還有 ~15%)
- **任何能讓兩端 D-cache miss 同時降的改動**(改變熱資料佈局、配 NodeStates 跟其他陣列到不同 L1d set)── 都是真實 ROI;但結構性都靠近 IR

## 補充:1M 取樣嘗試失敗的紀錄

兩次連續 1M 取樣都報 `WMI 資料區塊或事件通知已啟用` / `PerfViewSession is not active`。 PMC counter sources 鎖未在 PerfView 退出後即時釋放,連續呼叫(間隔 5 秒)仍碰撞。 可能需要:

- 連續呼叫間隔 30+ 秒
- 或先 `wpr -cancel` + `logman -ets stop`
- 或重啟 PerfView 之 host(因為 ETW sessions 是 kernel-level register)

600k 數據已乾淨可信,本次未強行重跑 1M(差距太小,結論不變)。
