# Rust S1 最佳化搜尋 —— 結論:已達天花板(2026-06-01)

> 對應使用者「rust 端也照這樣找找看」。判定:**Rust S1 ~76K 已是其資料佈局局部最優**,不採用任何改動。

## 已測(本 session)

| Rust 候選 | 方向 | 結果 | 出處 |
|---|---|---|---|
| **S2-A 內聯**(NodeHot 16B→32B,channel 內聯) | grow struct,消 chase | **−4.98%(0/7)** | `proto/01` |
| (隱含)**OR-all 全走 gnd/pwr** | 去 early-break | **−3.2%(3/40)** | [[jit-vs-llvm-recursive-inline]] R4 |

## 未實作但已分析否決:8B NodeHot(三索引併一)

把 `tlist_c1c2s/gnd/pwr`(3×i32)併成單一 `chan` 索引,指向 0-terminated 連續段
`[c1c2…,0,gnd…,0,pwr…,0]`,可把 NodeHot 16B→8B(密度加倍,240KB→120KB)。**但結構上強制了一個已測為負的 pattern**:

- gnd 段為了定位 pwr 段起點,**必須整段走完**(不能 early-break)→ 等同對 gnd 套 **OR-all 全走**,而 OR-all 在 Rust 已實測 **−3.2%**。
- add_node_to_group 的 gnd/pwr 掃描同樣被迫從 early-break 變全走。

→ 密度增益(8B,但兩者都在 L2、L1 影響邊際)很可能補不回 OR-all 損失。這是建立在**兩個實測負面**上的否決,非臆測。

## 為何 Rust 比 C# 難

- C# 的大勝(S2-A +4.18% / S2-A2 +0.8%)源自 **C#/JIT 的鬆散 codegen** —— 內聯消 chase、縮陣列,JIT 都有 slack 可賺。
- **Rust/LLVM 已把熱路徑壓到極緊**:16B NodeHot(4/line)已夠密、那次 chase 多半 L1/L2 命中、early-break 已最優。任何「加成本」(內聯/全走/counter)都輸,「移成本」又無處可移(三索引已是最小可定址形式)。
- 規律(記憶 [[hotpath-ceiling-and-antipatterns]] / [[jit-vs-llvm-recursive-inline]]):**Rust 加任何 per-call 成本必輸;唯一 win 是移除分支,但熱路徑已無分支可移。**

## 結論

Rust S1 維持 baseline(16B NodeHot、PullUp-gated class 1、early-break、recursive BFS、`get_unchecked`),
**~76K = 局部最優**。C# 現以 ~80K 領先,純因 JIT-slack-specific 的 S2-A/S2-A2(在 Rust 皆負,故 C#-only,見 [[csharp-rust-parity-policy]])。
兩引擎仍 bit-exact(`0x794A43ABDF169ADA`)。S1 階段(雙引擎)優化收束,進入 S2。
