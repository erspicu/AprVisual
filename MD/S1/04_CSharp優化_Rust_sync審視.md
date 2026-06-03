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

## 待驗證候選(逐一實測)
| # | 技巧 | C# 來源 / 效益 | Rust 現況 | 預判 | 結果 |
|---|---|---|---|---|---|
| 1 | **ulong 雙對讀取** @ `set_node_state` | 4c7e938 / +1.2% | 逐一讀 `[p]`,`[p+1]`,p+=2 | 有機會(同 #1 機制,需 transistor_list 加 padding) | _待測_ |
| 2 | **flatten cls==2 dispatch**(去 `bool grows`) | f34b8a6 / +0.5% | 仍用 `grows` 旗標 | 可疑(LLVM 多半已最佳化) | _待測_ |
| 3 | **settle-cap `#[cfg(debug_assertions)]`** | a01f31c | 每 pass 無條件檢查 `MAX_SETTLE_PASSES` | 有機會(in-loop break 抑制 LLVM 迴圈 codegen) | _待測_ |
| 4 | **ulong @ cls==2 conduction check** | (新應用) | 逐一讀 transistor_list | 有機會(Rust 此處對全部 cls==2 走 tlist) | _待測_ |

驗證順序:#1(最大 C# 勝)→ #3 → #4 → #2。結果回填上表。
