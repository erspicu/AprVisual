# C# → Rust 優化技巧 sync 審視(2026-06-03)

## 背景
這幾天 C# 連續多個**實作層**(非演算法)優化勝利,使 C# 從與 Rust 並駕(~77K)拉開到領先 ~7%(C# ~85K / Rust ~79K)。
依 [[csharp-rust-parity-policy]]:演算法層 parity、實作層各自最佳、不盲目互 sync(很多 C# 有效手法在 Rust 上持平/倒退)。
本文針對近期 C# 勝利逐一審視:**哪些還沒在 Rust、且值得移植**。紀律:**移植 → interleaved-paired 實測(≥2 批、跨批確認)→ bit-exact(checksum `0x794A43ABDF169ADA` @ full_palette 300k)→ 只有真贏才留**。

Rust 量測:`wire_s1 bench snapshot/full_palette.aprsnap 300000`;A/B 用 prebuild 兩個 exe 交錯跑。

## 已在 Rust(無需動作)
| C# commit | 技巧 | Rust 狀態 |
|---|---|---|
| a689a3e | NodeInfo 16B | ✓ `NodeHot` 已是 16B(R1) |
| af619f4 / Step1+2 | branchless enqueue(XOR shield) | ✓ `set_node_state` 已是 |
| (poison) | supply shield `recalc_hash[npwr/ngnd]=1` | ✓ Rust 早就有(C# 兩天前才評估、且因雜訊未採) |
| a80dab4 | R-1 dynamic-singleton fast-path | ✓ `recalc_node_singleton` |
| #02 | NodeConnections 移出 BFS | ✓ `node_connections` 冷陣列 |

## 已知 Rust-negative(跳過,別重蹈)
| 技巧 | 來源 | Rust 結果 |
|---|---|---|
| S2-A inline 節點鄰接(payload 內聯) | 360146b | 實測負 → Rust 無 inline payload(故 9cc0dc7 的「不發 inline sublist」也 N/A) |
| R4 OR-all branchless 掃描 | 5cad4b4 | −3.2% Rust([[jit-vs-llvm-recursive-inline]]) |
| 迭代式 BFS | — | −1.3% Rust(LLVM 對遞迴 inline 好) |
| sparse Dictionary callback | 2b54e6c | N/A(C# 是為了省 GC;Rust 無 GC,Vec<i32> 本就 OK) |

## 驗證結果(2026-06-03,interleaved-paired ≥2 批,bit-exact)
| # | 技巧 | C# / Rust | 結果 | 判定 |
|---|---|---|---|---|
| 1 | **ulong 雙對讀取** @ `set_node_state` | +1.2% / **+0.6%** | 29/32 勝,兩批正 | ✅ **採用** |
| 3 | **settle-cap `#[cfg(debug_assertions)]`** | C# 噪音(僥倖) / **+1.8%** | **32/32 勝**,+1.94/+1.67% | ✅ **採用** |
| 2 | **flatten cls==2 dispatch** | +0.5% / +0.1% | 32/52(雜訊,擲硬幣) | ✗ 否決 |
| 4 | **ulong @ cls==2 conduction check** | (新) / **−1.4%** | 0/32 勝 | ✗ 否決 |
| 5 | **group_buf i32→u16**(資料結構壓縮) | C# ushort 採用 / **−2.4%** | 0/32 勝 | ✗ 否決(C#/Rust sign-flip) |

**合計採用 #1+#3:Rust +2.0%**(32/32 vs HEAD,+2.10/+1.93%),top-3 **79.4K → ~80.6K hc/s**。

## 觀察(這次 sync 學到的)
- **最大的勝利 #3 在 C# 是「噪音」(當初 +2.77% 是熱機僥倖、靠非效能理由採用),在 Rust 卻是 +1.8% 真勝** ——
  **LLVM 的迴圈最佳化遠比 .NET JIT 討厭 settle 迴圈裡的 in-loop counter+break**。這是「同一改動兩引擎反向/不同幅度」
  的又一例,印證 [[csharp-rust-parity-policy]]:不能盲目互 sync,但**值得逐一驗證**——Rust 反而吃到了 C# 沒吃到的紅利。
- **#4 失敗、#1 成功**(同為 ulong 雙抓):差別在 **walk 長度 × 頻率**。`set_node_state` 的 fanout walk 較長(值得)、
  cls==2 conduction check 的 walk 很短且 73% recalc 都跑(ulong 開銷蓋過)。同 C# 的 A/B 教訓([[hotpath-ceiling-and-antipatterns]])。
- **#2 在 C# +0.5%、在 Rust 噪音**:goto/flatten 對 JIT codegen 有差,對 LLVM 無差(LLVM 早就把 `grows` 旗標最佳化掉)。
- Rust 早就有 supply-shield(C# 兩天前才評估、且因雜訊未採)——可見兩邊**各有領先項**,不是單向落後。
- **資料結構壓縮類已全查**(使用者 2026-06-03 提醒補測):NodeInfo 16B / TransistorList u16 / inGroup u8 / RecalcHash u8 都**早在 Rust**;唯一漏的 **`group_buf` C# ushort vs Rust i32**,實測 **−2.4%(Rust)** → 又一個 sign-flip(ushort 省的 29KB 在 group 只 1-2 entry 時無感,`as u16`/`as i32` 轉換在熱 BFS 寫 + SetNodeState 讀上反而蓋過)。C# RecalcList 的 ushort 實驗當初本就是 reverted 噪音,不移植。**結論:資料壓縮類沒有可移植的剩餘紅利。**
