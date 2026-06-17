# 手動融合 SetNodeState / RecalcNode 的失敗分析 — JIT inline cascade 才是王道

分支:`manual-fuse-setnodestate`(實驗紀錄,**不併 main**)
日期:2026-06-18
機器:AMD Ryzen 7 3700X(Zen 2, x64)、.NET 11、Release、`--pin`、interleaved-paired、full_palette @400k/1M
動機:既然 S1 已壓榨到比 C++ 版還快,連編譯器優化都想介入 —— 試把 `SetNodeState` / 整個 `RecalcNode` 手動融合進熱迴圈,賭能不能再快一點。

## 結論一句話

**全部失敗,而且越「融合」越慢(−11% → −25%)。根因不是 I-cache,是「手動 inline 親手打斷了 JIT 自動疊成的最佳 inline cascade」。** S1 比 C++ 快的關鍵之一,正是 JIT 把 `ProcessQueue ← RecalcNode ← RecalcNodeFast ← SetNodeState ← ComputeNodeGroup` 整條疊成**一顆零-call、跨整條鏈一起配置暫存器的巨型熱 method**;任何手動展開都會破壞它。

## 五個變體(全部 bit-exact:300k `0x794A43ABDF169ADA` / 400k `0x9174E19D961CB6E5` / 1M `0x6D4CCBCE2E9CD599`)

| # | 做法 | vs main | paired |
|---|---|---|---|
| 1 | per-node helper `SetNodeStateFused`,`[AggressiveInlining]` | **−10.91%** | 0/15 |
| 2 | 同上但 `[NoInlining]`(強制 out-of-line,最小 footprint) | **−18.28%** | 0/12 |
| 3 | loop-inside 獨立 method(整個 group/pair 一次 call) | **−22.09%** | 0/12 |
| 4 | 完全手動 inline、無 helper(pair 4 份 walk、group loop) | **−13.72%** | 0/10 |
| 5 | **統一寫回 ProcessQueue**(singleton/pair/group 共用一份 off/on walk + wave 層級 nextCount) | **−25.74%** | 0/15 |

> 變體 2(NoInlining,footprint 最小)比變體 1(inlined)還**慢** → 第一個「不是 I-cache」的訊號:縮小 footprint 反而更慢,代表瓶頸在 call overhead,不在 I-fetch。

## 鐵證:method native code size(`DOTNET_JitDisasmSummary=1 DOTNET_TieredCompilation=0`)

| 版本 | `ProcessQueue` | `RecalcNode` | 說明 |
|---|---|---|---|
| **main** | **5409 bytes** | (不存在,被 inline) | 整條 cascade 全 inline 進 ProcessQueue;RecalcNode / RecalcNodeFast / SetNodeState / ComputeNodeGroup / AddNodeToGroup 都不是獨立 method |
| 變體 4 | 210 bytes | 5204 bytes(IL=2418) | RecalcNode 的 IL 被手動展開到 2418,**超過 JIT inline 門檻** → ProcessQueue 再也吃不下它 → 每個 pop 一次真 call |
| 變體 5 | **3578 bytes**(IL=1880) | 5204(僅供冷的 first-touch capture) | 統一寫回把熱 method 砍小到 3578 < 5409,**達成「IL code < 5409」門檻** |

## 根本原因

### (A) 手動 inline 打斷 JIT 的 inline cascade
JIT 的 inline 啟發式是看**「被呼叫者的 IL 大小」**。原本 `RecalcNode` 的 IL 很小(裡面只是對 `SetNodeState` 等的 call),所以 JIT 能把它 inline 進 `ProcessQueue`,並在編譯這顆大 method 時遞迴把 `SetNodeState` 也一起 inline → 得到 5409 bytes、零 per-pop call、跨整條鏈統一暫存器配置的最佳熱 method。
我一手動把 walk 展開進 `RecalcNode` 的 IL(變 2418),就讓它**超過門檻、不再被 inline** → `ProcessQueue → RecalcNode` 每個 pop 一次 call(每幀數百萬次)+ 失去跨 method 最佳化 = 變體 4 的 −13.7%。

### (B) 統一寫回的「泛化稅」打在最熱的 singleton 上(變體 5,−25.7%)
變體 5 雖然把熱 method **縮小**到 3578 bytes,卻是**最慢**的。原因:約 **70% 的 pop 是 singleton**,main 把它走**最短直路**(`RecalcNodeFast → SetNodeState`,JIT 為此路徑專門特化 + 配暫存器)。統一寫回把 singleton 也丟進泛化的 buffer+loop:
- `wb2[0]=nn`(store)→ goto → `for(m=0;m<1;m++)` 一元素迴圈 → `wbBuf[m]`(load,store→load round-trip)→ 指標間接 → 分支 → walk;
- ~10 個 wave 層級 local(curList/curHash/nextList/nextHash/nextCount/nodeStates/rS/rA/rB/wb2)被迫**整個迴圈活著** → 暫存器壓力 / spill;
- goto 把三條路徑併到同一個 WRITEBACK 標籤 → JIT **無法再為 singleton 特化**。
這筆 per-pop 泛化稅 × 70% pop × 數百萬次 = −25%。**程式碼變小,但每個 pop 的工作變多。**

### (C) I-cache 被排除(這是使用者問的重點)
- 我能精確量的「I-cache footprint」= 熱 method 大小:main 5409 → 變體 5 **3578(小 34%)**。
- footprint **變小** + 效能 **−25%** ⇒ **I-cache miss 不可能是元兇**;若瓶頸是 I-fetch,砍小 34% 應該變快才對。
- 硬體 PMC 實測嘗試:本機有 admin + `xperf`,`IcacheMisses` 是可用 PMC source,但 **AMD Ryzen 在 xperf 下的 PMC 取樣(PmcInterrupt)抓不到任何事件(0 rows,已知 AMD/ETW 限制)**;counting mode(`-Pmc ... CSWITCH`)有抓到資料但 xperf CLI 無法乾淨萃取 per-process 計數。**但 footprint 數據已足以定論,不需 PMC。**

## 教訓

1. **「IL code < 5409」這個門檻達成了(3578),但跟速度負相關** —— 它量錯了東西。本引擎是 **per-pop 執行成本 + 記憶體延遲**綁定(尤其熱 singleton 的相依載入鏈),**不是 I-fetch / code-size** 綁定。小程式 ≠ 快。
2. **在 C# / RyuJIT 手動 inline 是反效果** —— 它的 inline cascade 啟發式建立在「小 IL method + call」之上;手動展開 IL 等於把 JIT 的最佳化權力奪走再做爛。**正確姿勢是:把 method 寫小、標 `[AggressiveInlining]`、讓 JIT 自己疊。**
3. 與既有記憶反模式一致:「method 太大 → 丟失 inline cascade ≈ −6%」、「compiler micromanagement」。這次是同一條,且更極端(−25%)。

## 處置

`manual-fuse-setnodestate` 分支保留五個變體與本分析作為實驗紀錄,**不併入 main**。main 的 `SetNodeState`/`RecalcNode`/`ProcessQueue` 維持原狀(JIT 自動 cascade,5409 bytes,最快)。
