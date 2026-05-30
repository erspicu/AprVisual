# Fast-path PullUp gate：同一改動在 C# 加速、在 Rust 變慢(編譯器分歧)

2026-05-30。記錄一個反直覺、但經嚴謹驗證為真的現象:**完全相同的 source-level 微最佳化,在 C#/.NET-JIT 是 +0.4~1.3% 加速,在 Rust/LLVM 卻是 −1.9~2.5% 減速。** 並記錄為什麼,以及用來確認它的量測方法(交錯配對 A/B)。

## 改動內容

`ClassifyPureLogicNodes()`(C# `WireCore.FastPath.cs` / Rust `wire.rs`)原本要求節點具有 `PullUp` flag 才納入 O(1) fast-path。實際上 `TlistC1c2s == 0`(無一般通道、group 必為 `{nn}`)已足以保證單節點 group;`PullUp` 只是用來保證 OR 後的 flags 非空,讓 `FlagsToState` LUT 不必處理「純 floating → 最大電容成員 hold」那一支。

放寬作法:
- **分類**:移除 `if ((flags & PullUp) == 0) continue;`。覆蓋率 **3,408 → 3,930(23.1% → 26.7%)**。
- **`RecalcNodeFast` 尾端**:`SetNodeState(nn, LUT[f])` 改成
  `SetNodeState(nn, f != None ? LUT[f] : NodeStates[nn])`(floating 時 hold 前值)。

正確性:兩引擎、兩版本 checksum 完全一致(`0x9B103E5E206E4C37` @ full_palette 200k hc;`0x933ABE7915AC18BE` @ 50k)。fast-path 只是內部最佳化,不改變輸出,bit-equivalence 不受影響。

## 兩個疊加效果,正負相抵

| | 效果 | 觸發頻率 |
|---|---|---|
| **(A) 省工** | +522 節點改走 fast-path,跳過 `compute_node_group` 的 BFS/DFS 與 `_groupBuf/_inGroup` 記帳 | 只在那 +522 節點 recalc 時 |
| **(B) 加成本** | `recalc_node_fast` 尾端多一個 `f != 0 ?` 分支 | **所有** 3,930 fast-path 節點、每次 recalc |

淨效果 = (A) − (B)。其正負號**取決於編譯器 baseline codegen 的緊密度**:

- **C# / .NET JIT**:codegen 較鬆,被 (A) 省掉的 BFS 路徑相對昂貴 → (A) 蓋過 (B) → **淨正**。
- **Rust / LLVM**:`recalc_node_fast` 已壓到極緊(S1 fork 的核心目標),BFS 也被 LLVM 把遞迴 `add_node_to_group` inline 得很好(見 memory `jit-vs-llvm-recursive-inline`)。所以 (A) 每節點省下的絕對 cycle 較少;而 (B) 把一個 compare + conditional-load + branch 加進一個本來 branchless 的熱尾巴、每秒跑數百萬次,**相對**成本反而更高 → (B) 蓋過 (A) → **淨負**。

一句話:**當周邊程式碼已被編譯器壓到極緊(Rust/LLVM),即使是「省下演算法工作」的改動,只要它在最熱的 inline 函式裡多塞一個分支,也可能輸給它省下的工作。** 周邊較鬆時(C#/JIT),演算法的節省才壓得過分支成本。這是 memory `hotpath-ceiling-and-antipatterns` 裡「compiler micromanagement」反模式的一個乾淨實例。

## 量測方法:交錯配對 A/B(這是重點)

第一次我用「批次 A/B」:先把 baseline 跑完一批 8 次、rebuild、再把 expanded 跑完一批 8 次。問題:**兩批在不同時間點量,背景負載 / CPU boost / 熱節流的時間漂移會系統性偏向其中一批**;當真實效果 < 2% 時,這個偏差足以蓋過或假造方向。

正確作法 **interleaved paired**:預先建好兩個 binary,然後 `base, exp, base, exp …` 輪流跑,讓任何時間漂移**平均地**打在兩邊;跑 30 輪,看 median、trimmed mean(去頭尾各 20%),以及**配對勝場**(每輪 exp 是否快過同輪 base —— 這個指標天然抵銷時間相關 outlier)。

四個 binary 來源:
- Rust:`wire_s1_base.exe`(留 PullUp gate)/ `wire_s1_exp.exe`(去 gate),同一 `cargo build --release`。
- C#:`be4cd38^`(baseline)/ `be4cd38`(expanded committed),同一 `dotnet build -c Release`。

## 結果(30 輪交錯配對,full_palette 200k hc,AMD Ryzen 7 3700X)

| 引擎 | base median | exp median | median 變化 | trimmed-mean 變化 | exp 配對勝場 | median 配對差 |
|---|---|---|---|---|---|---|
| **Rust** | 68,615 | 67,309 | **−1.90%** | **−2.50%** | **3 / 30** | **−1,331 hc/s** |
| **C#** | 63,344 | 63,583 | **+0.38%** | **+1.28%** | **22 / 30** | **+486 hc/s** |

- Rust:exp 只在 3 輪贏 —— 而那 3 輪剛好都是 **baseline 自己出現冷 outlier**(65,252 / 65,801 / 66,373)時。穩態 base ~68,500–69,000 vs exp ~67,100–67,600,每輪差約 −1,200~1,500。方向極穩。
- C#:exp 在 22 輪贏。大幅正值(+3,488 / +5,325 / +5,276)都是 baseline 出冷 outlier 時;穩態 exp 領先約 +300~800。注意 median +0.38% 比 trimmed +1.28% 低,因 baseline 的冷 outlier 把 base 中位數往下拉、縮小了表觀差距;配對指標(22/30、+486)最可信。

C# 真實增益 ~+0.4~1.3%(典型 ~+0.8%),比先前 batched top-4 估的 +1.6% 小 —— top-4 選取偏向了 expanded 批次的時段。交錯配對是更誠實的數字。

## 決策

- **C#**:採用 expanded(`be4cd38`,已 commit+push)。真實小幅加速。
- **Rust**:保留 baseline(留 PullUp gate)。expanded 真的較慢。
- 兩引擎輸出 checksum 仍相同,bit-equivalence invariant 不變 —— fast-path 覆蓋率是內部細節。

## 可重用教訓

1. **同一改動在不同編譯器可能正負相反。** 不要把 C# 熱路徑改動盲目同步到 Rust(或反之);**逐引擎量測**。這是繼「iterative BFS +2.9% C# / −1.3% Rust」之後的第二個明確資料點。
2. **效果 < 2% 時,批次 A/B 不可信;用交錯配對 + 配對勝場。** 時間相關漂移會偽造方向。
3. **「省演算法工作」不保證更快。** 若省工的代價是在最熱 inline 函式多一個分支,而周邊已被編譯器壓到極緊,淨值可能為負。先看那個分支的觸發頻率(這裡是「所有 fast-path 節點每次」,不是「只有新增節點」)。
