# Phase 2 scope — event-driven IR for CPU（與 main 的 batch IR 對著做）

> 前提:Phase 1 已量出結構**排程**是死路(`02_phase1_structural.md`),pre-IR 天花板 = pruning 的 1.37×。要再上去,必須抽 IR —— 但**這次是 event-driven CPU IR,不是 main 的 oblivious GPU IR**。branch 定位見 charter memory。
>
> 本文 = Phase 2 的範圍合約:**做什麼、明確不做什麼、逐節點等價怎麼驗、分幾步、預期天花板**。動手前先對齊。

---

## 0. 一句話

把開關層級網表抽成「每個節點一條*有向*的 next-state 表示」,**用 event-driven(dirty-driven)方式在 CPU 上求值**(只重算輸入變了的節點、靠 fanout 傳播),對 S1 逐節點驗證,證明比 S1 開關層級快,最後 codegen 成 C#/LLVM 原生 event-driven backend。

## 1. 與 main 的關鍵差異(這是整條 branch 存在的理由)

| | main S2/S3/S4 | 本 branch Phase 2 |
|---|---|---|
| IR 求值 | **oblivious / batch**:每 phase 拓樸序重算*所有* ~14.7k 節點(為了能 bit-sliced 上 GPU)| **event-driven**:只重算 dirty 節點的 fanout(保留 S1 的 ~4% 稀疏性)|
| 為何 | GPU 要一致 SIMD work | CPU 要少做事 |
| 結果 | CPU backend 比 S1 慢 3–6×(batch 浪費稀疏性)| 目標:比 S1 快(稀疏 + 每節點 Expr 比 group walk 便宜)|
| 天花板 | (real-time 不可達,已收攤)| ~2–3×(main 的 parked event-driven β 數據點;real-time 非目標)|

**IR 同時解決 Phase 1 的死結**:有向 next-state = 替每顆 pass transistor 選定驅動方向 → 溶掉那個 94.5% 的雙向耦合巨型 SCC → 組合邏輯無環(就是 `--dump-levels` gate-only 圖:最大 SCC 44)。

## 2. 範圍

> **範圍決定(user, 2026-05-23):「所有 netlist-level 的東西都一起處理」** —— 不切良性子集、不延後 PPU。整張網表(2A03 + 2C02 + cart + board)當一個單位,在同一個分析 pass 裡把*每個*節點分類路由。hybrid 是 per-node 的自動 fallback(某節點抽不出乾淨 Expr 就退開關層級),**不是**延後的區塊;per-node vs S1 等價 gate 是安全網。

### IN
- **S2.1–2.3 結構分析(全網表)**:已有 `--dump-levels`(靜態圖 + Tarjan SCC + 凝聚 level)。擴充成**對每個節點的 routing 分類**(見 §4 P2.1)。
- **drive-direction 分析(全網表)**:對*所有* pass transistor 決定實際驅動方向,把 full-graph 那個 94.5% 雙向耦合 SCC 解成有向 —— 這是讓「節點 = 其 driver 的函數」成立、IR 能成形的核心。
- **布林 Expr 抽取(全網表,一次)**:每個節點 → 一條 `Expr`(`Const/NodeRef/Not/And/Or/Mux/Hold`)。串=AND、並=OR、有路徑到 GND→0、無下拉有 pull-up→1;dynamic→`HoldExpr`;序向→`NodeRefExpr`(上一輪)。逐節點按其結構類別路由,**不挑子集**。
- **event-driven IR 直譯器**:節點變動 → 把它 fanout 的 Expr 重新 evaluate(**不是**拓樸 batch 全算)。保留 dirty-set + fanout 傳播(S1 的骨架)。
- **hybrid fallback(per-node)**:單一節點抽不出乾淨 Expr(強耦合 dynamic / 解不出方向的 bus)就*該節點*退開關層級 group 求值,記錄覆蓋率。整體 trace 仍需一致。
- **逐節點等價 gate** vs S1(golden reference)。
- **CPU codegen**(後段):把 event-driven IR emit 成 C# / LLVM 原生(per-node update fn + dirty queue)。

### OUT(明確不做 —— 守住「為 CPU / event-driven」的定位)
- ❌ GPU / bit-slicing / batch oblivious 求值(那是 main 的路、已證在 CPU 上輸)。
- ❌ 多 instance。
- ❌ real-time 目標(~840×;早已放棄)。
- (PPU **不**延後 —— 依 user 指示整張網表一起處理;PPU 區若有節點抽不乾淨,走 per-node hybrid,不是整塊延後。)

## 3. 等價驗證方法(失敗點 #1 的防線)

- **逐節點 trace vs S1**:同一批輸入,IR 直譯器與 S1 開關層級的 `cpu.a/x/y/p/s/pcl/pch/ab/db` 等逐 cycle 比對(沿用 `--trace` + diff,如 #1 / levelize 的驗法)。
- **float-artifact 豁免**:dynamic / open-bus / power-on indeterminate 區域允許暫態不同(同 #1、levelize 已確立的等級)——**但 CPU 架構狀態(PC/A/X/Y/S/IR)必須逐位元一致**。這是硬線。
- **blargg $6000**:跑得通 S1 跑得過的那批(放寬:headless 太慢,改用固定 cycle 數的 trace 比對為主)。
- **覆蓋率門檻**:IR 覆蓋 ≥ X% + 其餘 hybrid,整體 trace 一致(同 main S2 過關條件的放寬條款)。

## 4. 增量(每步一個可驗證 deliverable)

| # | 內容 | gate |
|---|---|---|
| **P2.1** | **全網表 routing 分類**:對*每個*節點定類別(supply/pin、combinational-logic、combinational-pass、sequential-SCC、dynamic-float),報各類佔比 —— IR 覆蓋地圖。(本步純分析,安全;`--dump-levels` 擴充)| 每個 live 節點都被分到一類,覆蓋地圖印出 |
| **P2.2** | `Expr` 資料結構 + **全網表 drive-direction 分析 + Expr 抽取**(逐節點按 P2.1 類別路由,抽不乾淨的標 hybrid)| 能 dump `ir_debug`(全網表);抽出的 Expr 對每個 IR 節點 evaluate == S1 group 解 |
| **P2.3** | **event-driven IR 直譯器**:IR 節點走 Expr fanout 重算、序向 SCC 顯式更新、其餘 per-node hybrid 開關層級 | 直譯器對固定 ROM 的 CPU trace 與 S1 逐 cycle 一致(架構狀態硬線)+ 覆蓋率報告 |
| **P2.4** | **效率 go/no-go**:event-driven IR 直譯器 vs S1 開關層級,同 ROM 同 cycle,量 hc/s | 報告:IR-CPU 是否 > S1?(預期 ~2–3×)+ 為何快的技術解釋 |
| **P2.5** | (P2.4 過了才做)CPU codegen:event-driven IR → C# emit / LLVM-MCJIT,逐節點等價 + 再量速 | codegen 可重現、與直譯器逐節點一致 |

## 5. 風險

| 風險 | 防線 |
|---|---|
| dynamic node / 回授 / 共享 bus 抽象錯(失敗點 #1)| 先良性區域、難搞先 hybrid;P2.2/P2.3 逐節點 gate;float-artifact 豁免界線寫死(架構狀態硬線)|
| event-driven IR 沒比 S1 快 | P2.4 當 go/no-go —— 沒過就停在這(pre-IR 1.37× + IR 直譯器),不硬上 codegen。main 的 β ~2–3× 是正面前例 |
| 驅動方向選錯(pass transistor 雙向)| 用 `--dump-levels` 的 SCC 分類:單點 SCC = 有向可抽;多點 SCC(bus/latch)= hybrid 或顯式序向 |

## 6. 預期天花板

~2–3×(對 S1),依 main parked event-driven β 的數據點 + charter。**這不是 real-time**(那條路已證關閉),是「single-instance、event-driven、CPU 上盡可能快」的乾淨上限。P2.4 會給出真實數字。

**狀態**:scope 待對齊 → 對齊後從 P2.1 動手。
