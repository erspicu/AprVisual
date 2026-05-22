# Phase 1（CPU/event-driven，IR 前）：結構排程 —— 量出天花板

> branch 重新定位見 `MEMORY` 的 `math-algos-branch-charter`：main = GPU/oblivious IR；本 branch = CPU/event-driven，分兩階段：**Phase 1 先把 IR 前的結構優化做到極限、量出天花板**，再進 Phase 2（event-driven IR）。
> 本文 = Phase 1 結構**排程**槓桿（levelized event-driven scheduling）的調查 + 結論。前一批的 pruning 槓桿（#1 merge-pruning + 策略二 fast-path，1.37×）見 `01_results.md`。

---

## TL;DR

**結構排程（levelization）對這顆 NES 是死路。** 量出來的天花板:結構**排程**加不了速;Phase 1 的真正天花板就是前面 pruning 的 **1.37×**(#1 + 策略二)。原因被 `--dump-levels` 一刀講清楚:**靜態相依圖有 94.5% 的節點在「一個」巨型 SCC 裡**(pass-transistor 雙向耦合造成),所以**不存在有效的靜態拓樸序**可排。而那個巨型 SCC 正是 **IR(Phase 2)用有向 next-state 溶掉的東西** —— 所以這個負結果本身就驗證了「要突破 1.37× 必須進 IR」。

| 槓桿 | flag | 結果 |
|---|---|---|
| 結構量測 | `--dump-levels` | full graph 94.5% 在一個 13,680-node SCC;gate-only 才大致無環(最大 SCC 44)|
| 嚴格 level-drain | (棄) | **不收斂** —— pass 耦合跨越 gate-only level,無有效拓樸序 |
| 軟 level-sort(wave 內排序)| `--levelize` | D 610.4→**602.4(−1.3%)**、glitch 1.138→**1.124**、hc/s **−15%**(counting-sort overhead) → **淨負** |

---

## Increment 1 — 結構量測（`--dump-levels`）

建一張有向相依圖,跑 Tarjan SCC + 凝聚圖拓樸 level。相依邊(靜態、與導通無關):
- **gate 控制**:`t.Gate → t.C1`、`t.Gate → t.C2`(gate 決定兩端是否連通 → 兩端的解析值都依賴它)。
- **channel 耦合**:`t.C1 ↔ t.C2`(雙向)**僅當兩端都是普通節點**(導通的 pass transistor 把兩端耦合)。對 GND/VCC 的 channel 是固定源,不加耦合邊。

為了分辨「真邏輯回授」vs「pass 耦合的保守過近似」,同時建*含*與*不含* channel 邊兩版:

| | full(gate + 雙向 pass 耦合)| gate-only(去掉 pass 耦合)|
|---|---|---|
| edges | 56,016 | 36,522 |
| SCCs | 890(817 單 + 73 多)| **11,672**(9,270 單 + 2,402 多)|
| 最大 SCC | **13,680** 節點(94.5%)| **44** 節點 |
| 可分級(單點)| 817(5.5%)| **9,270(63%)** |
| 策略二 pure-logic 落在單點 SCC | 565 / 3,408 | **2,976 / 3,408(87%)** |

**診斷**:那個 13,680 的巨型 SCC 幾乎全是 pass-transistor 耦合的假象。去掉 channel 邊後晶片**邏輯本身大致無環**(真正的回授 SCC 最大只有 44 節點 —— 就是那些 cross-coupled latch / 動態儲存)。靜態看每條 bus 上任何節點都能透過雙向 pass transistor 驅動任何其他節點 → 一個大環;但 runtime 每個 phase 只有一個 driver 真導通,真實 per-phase 導通圖稀疏且無環。

→ **一個*正確*的靜態 schedule 必須尊重 full graph**(pass 耦合導通時是真的),而它 94.5% 在一個 SCC → **無級可分**。gate-only 雖然無環,但拿它排程會忽略 pass 的值傳遞 → 不正確。

---

## Increment 2 — 軟層級化（`--levelize`）

### 嘗試 A:嚴格 level-drain（最低 level 先抽乾再進下一級）→ **不收斂**

實測每次 settle 都撞上 14.7M recalc 的安全上限。原因正是 Increment 1 的預警:**一個 pass-transistor group 橫跨多個 gate-only level**,所以「把某一級抽乾」會用*尚未處理*的高 level gate 值去解析 group,得到暫態值,稍後高 level 處理時又翻轉它,再回頭觸發低 level → ping-pong。**巨型 SCC = 不存在有效靜態拓樸序**,嚴格 drain 必然不收斂。已棄。

### 嘗試 B:軟 level-sort（保留收斂的雙緩衝 FIFO,只把每個 wave 內的 dirty 節點依 gate-only level 排序）→ **淨負**

收斂性跟原 FIFO 完全一樣(新 dirty 節點照樣進下一個 wave;只改 wave *內*順序),理論上讓節點在同一 wave 內就看到更新後的低 level 輸入 → 減少 wave 數 → 減少重複 re-eval。實測(full_palette, 50K hc):

| | baseline | `--levelize` |
|---|---|---|
| D(RecalcNode/hc)| 610.4 | **602.4(−1.3%)** |
| glitch factor | 1.138 | **1.124** |
| hc/s | ~41,500 | **~35,300(−15%)** |

**幾乎沒削到 D**(−1.3%),因為 bus 區(94.5% 那個 SCC)主宰 recalc,而它一個 RecalcNode 的 group walk 就把整群解完、跟順序無關;真正被排序改善的組合邏輯佔比太小。而 counting-sort 的 per-wave overhead 直接讓 hc/s **倒退 15%**。→ **淨負,棄用**。

### 正確性

`--levelize` 的 checksum 與 baseline *不同*(wave 內重排 → dynamic 區落到不同暫態,同 #1)。但 CPU trace 對照(full_palette,120 cycle):**`PC/A/X/Y/S/IR` 全程逐位元相同**,唯一差異是 `$2002`(PPUSTATUS)讀取時的 `DB`(open-bus 值,base 00 / lvl 30)以及由 `BIT` 指令*衍生*的 `P` flag。也就是說 **CPU 架構狀態與控制流沒有發散**,只有 PPU open-bus 暫態不同 —— 跟 #1 的 float-artifact 豁免**同一類**(只是更顯眼:#1 在 power-on I-flag、這裡在 PPU open-bus)。所以它「正確性等級跟已接受的 #1 相同」,只是**更慢、沒好處**。

---

## 結論 —— Phase 1 結構天花板

1. **結構*排程*(levelization)在這顆 NES 是死路**,有 `--dump-levels` 的結構證明(94.5% 一個 pass-耦合 SCC、無有效靜態拓樸序)+ `--levelize` 的實測(D −1.3%、hc/s −15%)雙重背書。FIFO event-driven settle 在無導通感知的前提下已接近最優。
2. **Phase 1 的天花板 = pruning 的 1.37×**(#1 merge-pruning + 策略二 fast-path);排程加不了東西。
3. **這個負結果直接驗證 Phase 2**:那個巨型 pass-耦合 SCC 正是「有向 next-state IR」要溶掉的(替每顆 pass transistor 選定驅動方向 → 組合邏輯無環 + 只剩 ≤44 節點的顯式 latch SCC,就是 gate-only 圖的樣子)。**要突破 1.37× 必須進 IR(Phase 2)** —— 而 gate-only 結構(最大 SCC 44)證明那條路有根基。
4. 保留的工具:`--dump-levels`(結構診斷,= S2.1–2.3,Phase 2 直接會用)、`--levelize`(gated,文件化的負結果)。

**狀態**:Phase 1 結束。結構做法的天花板量出來了 = **1.37×**(pruning),排程死路。交棒 Phase 2(event-driven IR)。
