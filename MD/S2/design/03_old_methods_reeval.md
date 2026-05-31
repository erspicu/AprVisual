# S2:舊方法重評估(哪些值得新模型重試)

> 使用者觀察(2026-05-31):舊 dead-ends 多是舊模型實作的,可能是**實作瑕疵**而非**根本
> 不可行**;新模型重做說不定不同。前提:**評估過、真的值得才試**。
> 本文是我的 triage。判準與結論在 §1 / §5。

## 0. 判準(核心)

把每個 dead-end 的**失敗根因**分類:

- **🔴 根本**(physics / 架構 / 正確性):重做也不會變 → naive 重測無意義,除非換 paradigm。
- **🟢🟡 實作/方法論**(量測偏差 / 次優實作 / 可解耦的正確性陷阱):新實作有機會翻盤 → 值得重測。

**先例(支持使用者直覺)**:
- **R-1** dynamic-singleton 推翻了記憶中「72–75K ceiling 已榨乾」的假設(+18.6%)——
  舊結論「便宜的 singleton 已抓完」其實只抓了 static singleton,~51% dynamic singleton 仍走完整 BFS。
- **R4** OR-all 推翻了 2026-05-29 batched −3.07% 的「dead-end」(interleaved 重量為 +0.6%)。

→ 結論:**「舊說死」不等於「根本死」;判準是根因類型,不是當年的數字。**

---

## 1. 🟢 值得重測(實作/方法論瑕疵,非根本)

### O1 — 2026-05-29 那批 batched sub-3% 負面結果,改用 interleaved-paired 重驗 ★★ 最高優先
- **舊根因類型**:**方法論偏差**(batched A/B 有時間漂移偏差;sub-2% 不可信)。
- **記憶已明載**([[hotpath-ceiling-and-antipatterns]] / [[interleaved-paired-bench]]):「R4 翻盤後,
  其他 2026-05-29 batched sub-3% negatives 現在可疑,需 interleaved-paired 重驗」。
- **成本/翻盤**:**近零風險**(只是重新量已實作過的候選)、翻盤可能性高。
- **來源**:`MD/suggest/00_open_proposals_summary.md` §2 + `MD/suggest/netlist_non_ir_optimization_review_20260529.md`
  + 對應 git log。逐一抽出當年判 −1~−3% 的候選,interleaved-paired ×雙引擎重測。
- **可順手做**:S2 跑 bench 時一併重測,不需額外架構。

### O2 — prune/merge:把「走訪移除」與「電容計數」解耦後重測 ★
- **舊結果**:破 bit-exact(dead-end #8 / L8)。
- **舊根因**:丟死電晶體 → 降低 `NodeConnections`(= floating tie-break 的電容權重)→ checksum 變。
- **我的新觀察(可能的實作瑕疵)**:舊實作把**「從 walk 表移除」**和**「從電容計數移除」**綁在一起。
  若**解耦** —— 從 c1c2/gnd/pwr walk 表移除「恆 OFF / 確定 dead」的電晶體,但 `NodeConnections`
  **保留原值** —— 理論上可 **bit-exact 且省走訪**。這正是「舊模型實作缺陷」型的候選。
- **dead 的嚴格定義**(才安全):`gate == vss`(恆 OFF,現在 lowering 已在丟)、重複邊。
  gate 會變動的**不能**移。安全子集可能很小,但風險低、先驗 checksum 即知。
- **與 S2-A 相容**:S2-A 本就保留 `NodeConnections`,這個 decouple 思路一致,可疊在 S2-A 上。

### O3 — RCM → 動態 co-activation 重排(**已納入 S2-C**,見 02)
- 舊 RCM 無增益,根因:**靜態 bandwidth ≠ 動態局部性**。Gemini consult 的 S2-C 用 **runtime-ON
  拓樸**重做,正是「同目標、更好方法」。已在候選 list,不重複。

---

## 2. 🟡 邊際(可低優先重測,但預期仍負;實作敏感、ceiling 低)

| 舊方法 | 舊結果 / 根因 | 重測角度 | 我的預期 |
|---|---|---|---|
| **O4 Counter-fastpath** | −6%;SetNodeState walk 比 fast-path 頻繁 10×,counter store 蓋過省下的 scan(機制真實) | 是否有「只在 gate flip 時 O(1) 更新、不在每次 walk」的計數?但 SetNodeState 本就是 walk,難避 | 仍負,低優先 |
| **O5 LUT-TTL** | C# post-inline-cascade 負;Rust 從未贏 | 實作敏感,但 ceiling 低 | 仍負,低優先 |
| **O6 SIMD-queue(改 gather)** | marginal/負;queue 散射 | SIMD gather 載入?但走訪 1.4 節點太短 | 仍負,低優先 |

---

## 3. 🔴 根本死路(需換 paradigm,naive 重測無意義)

| 舊方法 | 倍率 | 根本原因(重做不會變) |
|---|---|---|
| per-chip parallel | 15× 慢 | 每波工作太小 = 稀疏性物理。除非 multi-wave-per-sync 換 paradigm |
| bit-parallel dense BFS | 156× 慢 | 1.4-node 平均走訪 = 物理;平行 overhead 壓垮 |
| dead-end-skip / micro-skips | 破 CPU | observer 模型正確性陷阱(leaf state 經 group walk 流到 ppu.db);真正封閉子集 <1% |
| batch IR / 全程式 codegen | 3–6× 慢 | 摧毀事件驅動稀疏性(批次重算 ~14.7K)+ i-cache 爆掉。Gemini 也駁回 6%-fringe bytecode |
| levelize / topo-order | 不可行 | 94% 雙向 pass-transistor SCC |

> 這些的共同點:根因是**物理(走訪太小)、架構(SCC / 批次破稀疏)、或正確性(observer / 電容)**,
> 不是程式寫得好不好。新模型重寫也救不回。**除非先改變 paradigm**(例如 multi-wave-per-sync、
> chip-independent stepping),否則不重做。

---

## 4. 與 S2 主線的關係

- **S2 主線仍是 S2-A**(新方法:節點鄰接內聯,非舊方法)。見 [`02_architecture_candidates.md`](02_architecture_candidates.md)。
- 舊方法值得「順手重測」的只有 **O1**(零風險、高翻盤)與 **O2**(需小心驗 bit-exact);**O3 已是 S2-C**。
- 其餘(🟡 邊際 / 🔴 根本)不主動重做。

## 5. 結論

使用者直覺正確、且有先例(R-1、R4)。但**值得重測的是「方法論/實作瑕疵」型,不是「根本」型**。
具體就 **O1（最高優先,零風險）** 與 **O2（中,需驗 bit-exact）** 兩項;其餘根本死路維持封存。
所有重測一律走驗證政策(整機 NES + interleaved-paired + 雙引擎;見 `00_INDEX.md`)。
