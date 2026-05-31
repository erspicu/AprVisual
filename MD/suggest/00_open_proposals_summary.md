# suggest/ 總摘要 — 待測試清單 / 參考清單 / 封存索引

> **這個檔的用途**:`suggest/` 以前累積很多 LIST / review / gemini 問答檔,變得混亂。現在規則:
> - **本檔** = 唯一「活的」清單。提過、但**還沒確認(沒實測結論)的建議方案**,依規模分兩類:
>   - **§1 待測試清單** = **小型消改**(改動小、可快速 interleaved A/B 的)。
>   - **§2 參考清單** = **大型結構重構**(改動大/風險高/borderline-IR,留作參考,不主動排測)。
> - **§3** = 曾出現在建議裡、但**已測過視為 dead** 的(列出來避免重提)。
> - **§4 + `old/`** = 已完整結案的歷史檔(含 gemini 問答原始紀錄),細節去 `old/` 翻。
>
> 最後更新:2026-05-31。用 `hotpath-ceiling-and-antipatterns` 等記憶交叉比對 —— 很多「某檔沒記結論」的項目其實在別處測過了,已歸到 §3 或 §4。

---

## 重要前提(先看再看清單)

S1 熱路徑經多輪實測已在**天花板**:C# ~65.7K / Rust ~70.1K hc/s(full_palette 200k);realistic ceiling C# 72-75K / Rust 78-82K。**real-time(42.95M hc/s)在現行 event-driven BFS 架構下不可達**,要突破得換架構(IR/AOT — 已證更慢)或更快單核。
所以下面**絕大多數預期 ≤1% 或負**;列出只代表「還沒親手測過」,不代表有希望。

通用教訓(提新點子先核對):
1. **減成本才贏、加成本必輸** —— ~121M 次/200k-hc 的熱迴圈加任何 per-call 工作(load/branch/shift/prefetch)都淨負。唯一 win R4 是**移除**一個分支。
2. **C#/JIT 與 Rust/LLVM 常反號** —— 同一改動各自實測(R4:+0.6% C# / −3.2% Rust)。
3. **netlist 摺疊踩 floating tie-break 電容(dead-end #8)** —— drop/merge 改變 node 的 `C1c2s.Count+Gates.Count` 會 silently 破 checksum(R2、stub-removal、prune-merge 都中)。
4. **sub-2% 必用 interleaved-paired**(交替 base/exp + 配對勝場 + 中位數);batched 會偽造方向(R4 舊 batched −3.07% 是錯號)。

---

## §1 待測試清單 — 2026-05-31 全部實測完畢,清單清空(無一新採用)

4 條小型消改全部以 interleaved-paired A/B + checksum 實測。結果與預期一致(噪音/負面),且 **T4 發現其實早已實作**:

| # | 提案 | 結果(interleaved-paired,full_palette 200k) |
|---|---|---|
| T1 | gate-probability sub-list 排序(以 pull-up=likely-ON 為靜態 proxy,排到子串前) | ❌ **−0.43% median / 配對 14-30(擲硬幣)** → 噪音,reverted。子串多為長度 1-2,early-break 本就快,排序無感;C# fast-path 已是 OR-all(R4)無 early-break。 |
| T2 | Rust `group_buf` Vec<i32>→Vec<u16> | ❌ **−0.91% median / 配對 6-40** → 明確負面,reverted。group_buf 工作集本就 ~1.4 entries(縮 footprint 無益),u16 cast/codegen 反傷 LLVM。 |
| T3 | non-temporal framebuffer store(`Sse2.StoreNonTemporal`) | ❌ **−0.10% median / 配對 14-30** → 噪音,reverted。每像素單 store 是 callback 成本的零頭,且打散了同 scanline 的 write-combining。 |
| T4 | memory handler ROM/RAM body 特化(captured byte[]+mask、ROM/RAM 分 closure) | ✅ **早已實作** —— `Handlers.cs` `AttachRamLikeHandler`(標 "suggest #F2"):`byte[] data` 與 `mask` 已 hoist、`readOnly` vs read/write 已分成兩個 closure。**無事可做。** |

**淨結果**:fast-path / footprint 的「小型消改」空間已逐條驗證為空(三條噪音/負面 + 一條早完成)。引擎維持 baseline。再次印證:這條熱路徑加任何 per-call 成本都不賺,唯一 win 是 R4(移除分支)。詳見 `old/csharp_s1_optimization_round2_20260531.md`。

---

## §2 參考清單(大型結構重構 — 風險高/borderline-IR,留參考,不主動排測)

| # | 提案 | 一句話 | 來源 | 為何「大」+ 評估 |
|---|---|---|---|---|
| ~~R-1~~ | ~~**Dynamic-singleton fast-path**~~ | — | — | ✅ **已實作並採用(2026-05-31,commit `a80dab4`)→ +18.6% C#,專案最大 win!** 見下方。 |
| R-2 | **Clock-phase partitioning** | 依時鐘相位把 edges 分段,runtime 依相位整段 skip | netlist_non_ir P2、gemini Q3 | 改 BFS 掃描結構 + 需正確辨識 clock 語意。Gemini 原預測 +15-30%,但實測 clock Wave-0 工作佔比 **<1%** → ROI 上界 <1%;borderline-IR + 正確性風險。 |
| R-3 | **Warm-up constant propagation** | warm-up 跑出長期不變 node,當常數重新 lowering | netlist_non_ir P3 | netlist 重構。R2(2026-05-31)已證 static const-fold 破 tie-break(#8)+ 效能上界 0;warm-up 版更危險、同零上界。 |
| R-4 | **Long-list SIMD / bitset mirror(len>16)** | 對長 gate list 走 SIMD gather 或 ulong bitset | gemini Q2、netlist_non_ir、#I3 | 加平行資料結構。small-N SIMD = anti-pattern #3;walk 平均 1.4 node;bitset-BFS 實測 156× 慢。長串子集罕見。 |
| R-5 | **Per-handler dirty-set lookup** | 預算每個 handler 寫入後會 dirty 的 node 集合,查表取代 settle | gemini Q4 | 改 settle 流程。Gemini 預測 +5-10%,但 handler 冷(5×/200k)→ 真實 ROI ≈ 0。 |
| R-6 | **Rust fixed-width bus / video fixed arrays** | bus/video node 改 `Bus16`/`[u16;9]` 等定長結構 | RustS1 P2 | 改資料結構、侵入性高;footprint 已夠 → 預期噪音。 |

> **🎉 R-1 已畢業並採用(2026-05-31)→ +18.6% C#(~65.5K → ~77.6K hc/s),60/60 配對全勝,bit-exact。專案至今最大的單一 win,並突破先前認定的 72-75K 天花板。**
> 機制:把「動態 singleton」(有 c1c2s channel 但本 half-cycle 全 OFF,佔 ~51.5% 的 recalc call)在 `RecalcNode` 裡先掃 c1c2s,全 OFF 就走 O(1) `RecalcNodeFast`(對單節點群組 bit-identical)。3-valued `IsPureLogic`(0/1/2)維持單一陣列載入。
> **教訓**:`hotpath-ceiling` 記憶曾斷言「cheap singleton wins 已捕捉/天花板已到」—— **錯**:只捕捉了 static singleton(18.3%),動態的(半數 call)一直走完整 BFS。再次驗證:預測不可恃,實作量測才算數。
> **待辦**:Rust(`experiment/rust-s1`)同款結構未測 —— 這是大型演算法 win(非 micro-branch),很可能 Rust 也贏,值得各自 A/B。
> §2 其餘(R-2~R-6)維持參考:預期 ≤1% 或 moot。

---

## §3 曾被建議、但已測過視為 dead(別重提)

| 提案(別名) | 實測結果 |
|---|---|
| Macro-node / 串聯 stack compound edge(= prune-merge) | 破 PPU(full_palette frame 48 黑屏)+ 淨負 |
| Hypergraph / cache-line-aware renumber(= RCM) | −3-4% |
| Software prefetch(NodeInfo/NodeStates) | N2(2026-05-31)−1.5%;NodeStates 已 L1d-resident |
| in_group generation counter | #H2 u16 −3.93% / Rust byte −1.65% |
| Branchless AddNodeToGroup(BFS 主路徑) | Phase C/E −19% / −37% |
| Rust raw pointer iteration | −1.57% |
| netlist_non_ir P0 四條 | 全測過:loop-unswitch=#G2 採用、enqueue+shield=Rust Phase A +1.63%/C# −2.15%、**OR-all=R4 +0.6% C#/−3.2% Rust**、ReadBits=#G1 +0.29% |

---

## §4 封存索引(`old/`)

全部已完整結案 / 歷史問答原始檔。細節數據進去翻。

| 檔名(在 `old/`) | 類型 | 結論摘要 |
|---|---|---|
| `csharp_s1_optimization_analysis_20260531.md` | 結案 LIST | N1–N7 全 ≤0,無採用。 |
| `csharp_s1_optimization_round2_20260531.md` | 結案 LIST | R1 AOT −5.5%、R2/R3 摺疊破 bit-exact+零上界、**R4 OR-all +0.6% C# / −3.2% Rust**。 |
| `fastpath_lowering_strategy_review_20260530.md` | 結案 review | fast-path/lowering 分布數據;Gemini 6 提案全已做/dead/<1%。 |
| `hotpath_review_LIST_2026-05-28.md` | 結案 LIST | #01–#08 + P2/P3,採用數項(NodeConnections 延後讀 +12.3% 等)。 |
| `hotpath_review_LIST_2026-05-29.md` | 結案 LIST(主進度) | #G/#H/#I + Phase A–F;#G2 採用,多數 reverted;#I 區 P2 已併入本摘要。 |
| `Sim_hotpath_efficiency_suggestions_2026-05-28.md` | 結案建議 | C# 12 提案 P0–P3,各有結論。 |
| `Sim_hotpath_followup_suggestions_2026-05-29.md` | 建議(open 已併入 §1/§2) | followup 5 提案;SetNodeState split=#G2 採用。 |
| `RustS1_hotpath_suggestions_2026-05-29.md` | 建議(open 已併入 §1/§2) | Rust 10 提案;多數已測。 |
| `gemini_query_2026-05-29.md` | 歷史問答 | 送 Gemini 的 Q1–Q6 prompt。 |
| `gemini_query_round2_2026-05-29.md` | 歷史問答 | round2 prompt(附實測回饋)。 |
| `gemini_reply_2026-05-29.md` | 歷史問答 | Gemini Q1–Q6 原始回覆。 |
| `gemini_reply_round2_2026-05-29.md` | 歷史問答 | Gemini 修正版 + 5 anti-patterns + ceiling。 |
| `gemini_recommended_LIST_2026-05-29.md` | 結案 LIST(open 已併入 §1/§2) | Gemini 建議彙整 + 測試矩陣。 |
| `netlist_non_ir_optimization_review_20260529.md` | review(open 已併入 §1/§2) | P0–P3 非-IR 框架;P0 已全測,P2/P3 多歸 §2/§3。 |
