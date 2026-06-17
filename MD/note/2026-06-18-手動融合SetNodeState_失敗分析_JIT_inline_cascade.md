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

## 附:Gemini 雙模型判決(2026-06-18)

把上述問題(IL 砍很小卻 −25%、有沒有活局)同時問 `gemini-2.5-pro` 與 `gemini-3.1-pro-preview`。
log:`tools/knowledgebase/message/20260618_021953.txt`(2.5)、`...022057.txt`(3.1)。

**兩個模型一致:手動 fusion 從根本上贏不了 JIT 的 auto-inline cascade。**

- **code size 是假議題**(3.1 的關鍵硬體論點):main 那顆 5.4KB 巨型 method 完美塞進 Zen2 的 32KB L1I + **4K-op 微指令快取**,CPU 最愛這種「直線、特化、被 store buffer 吸收」的肥碼;換成小碼(分支 + store→load forwarding + 暫存器飢餓)正是 CPU 討厭的形狀。→ 印證本文「I-cache 被排除」。
- **wave 層級 nextCount hoist = 純暫存器稅,該 revert**:Zen2 只有 14 GPR,`count++` 走 44-entry store queue 零成本;hoist 反而逼 spill 掉真正的指標。量化規則:熱迴圈 >8 個熱 local 時,別 hoist 可預測的 static counter。→ 印證本文根因 (B)。
- **唯一還活的結構招 = Hybrid**:singleton+pair 走直線 inline、把 `<2%` 的 BFS group 丟去 `[MethodImpl(NoInlining)]` 冷 method,讓 BFS 的 locals 不再毒害快路徑的 regalloc。但兩模型都說這**只是控制暫存器壓力,預期中性、不是 win**。
- `ref`-threading queue cursor:破壞 enregistration / 逼 spill,別做。
- **兩模型都把「真正贏點」指向打 dependent-load 鏈(MLP)**:軟體預取 `Sse.Prefetch0`、或 wave 內 scalar interleaving(一次 pop 2 個、重疊兩條獨立 load);2.5 另提 SoA→AoS。

**⚠ 但對到我們的歷史,這些「贏點」大多是已驗證死路**(Gemini 不知道):
- 軟體預取:2026-06-10 pop-loop prefetch **已試 → 負面**(見 latency-hiding 筆記)。
- 純 locality / AoS-as-locality:**已試 → 中性**(熱工作集本就 L1 常駐;瓶頸是 dependent 鏈不是 cache miss)。
- 唯一**沒試過**:wave 內「一次 2 pop」scalar interleaving(靠 OoO 重疊兩條獨立節點的 load,不靠 prefetch 指令)。但群平均 1.4 節點 + wave 內 dependent 鏈 ⇒ 期望值低。

**綜合結論:這條 hot-path 微優化路線沒有真正的活局。** `main` 的 JIT auto-cascade 即全域最優;此分支記為死路。
