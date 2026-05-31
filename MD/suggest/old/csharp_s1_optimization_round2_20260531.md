# C# S1 優化 第二輪 — fast-path / lowering 再壓縮 + .NET 10 建構 (2026-05-31)

承上一輪 `csharp_s1_optimization_analysis_20260531.md`(N1–N7 全數無效,熱路徑已在實測天花板)。
本輪換**角度**:不再碰「讓每次運算更便宜」(那條已證實到頂),改攻使用者點名的兩條「**減少運算量**」槓桿 + 一個 .NET 建構問題:

1. **lowering**(在 build/Reset 一次性把網表壓更小 → 每個 half-cycle 都少掃)
2. **fast-path**(讓更多 node 走 O(1),不走 BFS group walk)
3. **.NET 10 建構選項**(我們已經在 net10.0,問題其實是「還有沒有沒開的建構模式」)

> 關鍵差異:N1–N7 是 per-call 微優化(加熱路徑成本 → 全敗)。本輪 R1/R2/R3 的成本都付在**一次性 setup**,runtime 熱路徑**不增反減** —— 這正是 fast-path / always-on-merge 當初能贏的原因,所以理論上有機會,值得實測。

---

## 現況量測 (2026-05-31, base binary, full_palette 200k hc)

| 指標 | 數值 |
|---|---|
| baseline | **66,012 hc/s**(短單跑;正式 interleaved baseline 待測) |
| lowering | nodes 15,094 → 14,681(合併 **413**, 2.7%);transistors 27,228 → 26,726(丟 **502**, 1.8%) |
| fast-path 覆蓋 | 3,895 / 14,686 = **26.5%** node 走 O(1) RecalcNodeFast |
| checksum @200192 | `0x02D4EDBE5A8224EA`(正確性基準) |

`Lower.cs` 目前做:always-on 短路合併(`gate==Npwr`,已涵蓋 gate==vcc)、`gate==vss` 死電晶體丟棄、self-loop/dedupe、dense renumber。
**程式碼自承未做**:「一個 normal node 經 always-on device 連到 supply(vcc/vss)沒被當常數摺疊」← R2 的標的。

---

## 不重做的已知死路(本輪明確排除)

| 死路 | 結果 | 為何不再碰 |
|---|---|---|
| prune-merge(串聯 pull-down stack 壓 compound edge) | 破 PPU + 淨負 | 改語意;R2 只做**可形式證明的常數摺疊**,不建 compound edge |
| dead-end-skip(跳過未觀測 node) | 破 CPU bus | 真正封閉死端 < 1%;葉節點狀態會經 group walk 流到 ppu.db |
| levelize / SCC 分層排程 | −15% | SCC 根本問題 |
| RCM / cache-line renumber | ~1.0× | cache 已滿足 |
| counter-fastpath(在 set_node_state 維護計數) | −6% | 在最熱路徑加維護成本 |
| generation-counter(免清 in_group) | −3.9% | 同上 |
| N1–N7(消陣列/prefetch/縮 struct/PGO 關閉) | 全 ≤0 | 熱路徑加成本 → 全敗(上一輪) |

---

## 候選清單

### R1 — NativeAOT(`PublishAot`)建構 A/B  〔build〕  ❌ −5.5%,不採用 (2026-05-31)
**實測(interleaved 20 輪 / 200k hc / full_palette)**:

| | median hc/s | trimmed-mean(20%) | 配對勝場 | checksum |
|---|---|---|---|---|
| base(JIT + 預設 Dynamic PGO) | 65,664 | 65,487 | — | `0x02D4EDBE5A8224EA` |
| **AOT** | 62,128 | 61,832 | **0/20** | 同(bit-exact ✓) |
| 差 | **−5.39%** | **−5.58%** | base 每輪都贏 | — |

NativeAOT bit-exact 正確,但**慢 ~5.5%** —— 正是預測的「AOT 放棄 dynamic PGO」。**回答使用者的 .NET 問題:我們已在 net10.0,改成 NativeAOT 不會更快、反而慢 5.5%;現行 JIT+DynamicPGO 就是最佳建構。**
建置障礙記錄:AOT link 需 `vswhere.exe` 在 PATH(`C:\Program Files (x86)\Microsoft Visual Studio\Installer`),否則 ILCompiler 的 link.rsp 呼叫會壞掉(return code 123)。
**ReadyToRun(R2R)**:邏輯上 steady-state ≤ baseline —— R2R 只把 startup 換成預編譯碼,runtime 仍 JIT tier-up 成同一份 tier-1 碼,200k-hc(~3s)由 steady-state 主導 → 不可能比 JIT 快。不另建,判 neutral。
**直接回答「改用 .NET 10 建構效能會再優化嗎」**:我們**已經**是 `net10.0`,跑的是 JIT + 預設 Dynamic PGO。唯一沒測過的建構模式是 **NativeAOT**(`PublishAot=true`,需 VS C++ build tools)。
- **預測**:steady-state ≤0。AOT 放棄 dynamic PGO(只有 static/instrumented PGO),而上一輪 N6 已證「關掉 tiering=丟 PGO → −1.1%」。長時間熱迴圈通常 JIT+DynamicPGO 勝。
- **但必須實測**才能給使用者數據化的答案。順帶記錄 ReadyToRun(R2R)作為次要對照(預測 steady-state ≈ JIT,只省 startup)。
- **通過條件**:interleaved median + trimmed-mean 都 > 0 **且** checksum 一致。
- 風險:低(純建構,不改一行邏輯)。

### R2 — supply-short 常數摺疊 + 死電晶體串級  〔lowering〕  ❌ 實作並實測:drop 破壞 bit-exact + 零效能上界,不採用 (2026-05-31 二次)
> 應使用者要求「真的實作測一次,微小正面就採用」。從「預測 moot」**升級為實作+實測**。

**實作方式**:在 `LowerNetlist` 前置 pass,把「只被強 always-on(`gate==Npwr` non-weak)短路到單一 supply、無其他 channel」的 const node 所 **gate 的電晶體** 改寫 gate→該 supply(const-high→Npwr、const-low→Ngnd),再交給現有 merge/drop。理論上 runtime 等價。實測殘餘 = **56 const nodes / 84 gated-transistors**(同 Step 0 診斷)。

**bisect 結果**:
- **merge-only(const-high→Npwr,2 個):bit-exact ✓ 但 no-op** —— 那 2 個改寫後淨值與 base 完全相同(merged 413 / dropped 502 不變),零縮減。
- **drop(const-low→Ngnd,82 個):破壞 bit-exact ❌**(checksum `0x68FB…` ≠ base `0x02D4…`)。
  - **根因**:dropping 死電晶體會降低 node 的 `cap = C1c2s.Count + Gates.Count`(= NodeConnections),而那是 **floating tie-break「最大電容者勝」** 的依據。原引擎裡死電晶體雖永不導通,仍被算進電容。**這正是 dead-end #8(stub-removal)同一個坑** —— 這次是親手實作再次撞到。

**效能上界(用不正確的 full-drop 版當上限,30 對 interleaved)**:median **−0.21%** / 配對 **14/30(擲硬幣)** / mean −0.95%。**連最大化縮減版都沒有任何正向 signal** —— 掉的電晶體是冷的、自動新增的 29 個 fast-path node 也 recalc 不夠頻繁。

**結論**:R2 無法「又正確又有益」—— 有益的部分(drop)不正確(且修正需保留 NodeConnections,繁複又踩 #8),正確的部分(merge)是 no-op,而且效能上界本來就是 0/負。**不採用。**
<details><summary>原始設計(保留參考)</summary>
延伸 `Lower.cs`:一個 normal node N 若被**強(non-weak)always-on device 連到 supply**,且**沒有任何可能把它拉向另一極的 channel**,則 N 可證明為常數(vcc→恆 1 / vss→恆 0)。
- 摺疊後串級:由常數 node 當 gate 的電晶體 → 恆通(merge)或恆斷(drop);迭代到 fixpoint。
- **Step 0(先做)**:加 `--lower-diag`(cold path)量殘餘 yield —— 數出這類常數 node 數 + 可串級摺掉的電晶體數。**若 ≈ 0 就不實作**(誠實標 moot)。
- **正確性**:可形式證明 bit-exact(與既有 always-on-merge 同性質)。關鍵陷阱:連到 vcc 但**另有可導通 pull-down** 的 node **不是**常數(LUT 中 Gnd > Pwr)——必須排除「有對向 channel」者。這也是原作者當初停在 normal↔normal 的原因。
- **payoff 指標**:lowering 縮減量 + fast-path 覆蓋率(應 > 26.5%)+ hc/s。
- 風險:中(證明要嚴謹)。
</details>

### R3 — dead-gate channel 的 fast-path 擴大  〔fast-path × lowering〕  ❌ 實測:+29 node 但零效能,不採用 (2026-05-31 二次)
R3 被 R2 的 drop **自動涵蓋**:drop 掉 const-low-gated 死電晶體後,c1c2s 全歸零的 node 直接被現有 `ClassifyPureLogicNodes`(查 `TlistC1c2s==0`)收編。實測 fast-path **3,895 → 3,924(+29,26.5%→26.7%)**。
但 R2 的效能上界 A/B **已經含這 +29 個 fast-path node** → 結果 median −0.21% / 擲硬幣 → **這 29 個 node recalc 不夠頻繁,貢獻 ≈ 0**。
若要 bit-exact 取得 R3(不 drop、改在 classify 端認「c1c2s 全為 const-low-gated」),yield 仍是同一批 29 個 node、效能仍 ≈ 0,還多一份 classify 複雜度 → **不值得,不採用。**
<details><summary>原始設計(保留參考)</summary>
有些 node 有 c1c2s channel,但這些 channel 全部經過「gate 為常數-OFF」的電晶體 → 永不導通 → group 永遠長不出去 → 其實可走 fast-path(即使 `TlistC1c2s != 0`)。
- 依賴 R2 找出的常數-OFF 集合。量「因此新增可 fast-path」的 node 數;若顯著就放寬 `ClassifyPureLogicNodes`。
- **payoff**:fast-path 覆蓋率再往上。
- 風險:中(classify 邏輯變複雜;checksum 驗證)。
</details>

### R4 — `RecalcNodeFast` gnd/pwr 掃描 branchless(OR-all)  〔fast-path 微優化〕  ✅ 採用,小幅 win (2026-05-31)
**實測(interleaved-paired,3 批 × 20 輪 = 60,200k hc / full_palette,bit-exact `0x02D4EDBE5A8224EA`)**:

| 批次 | median 差 | 配對勝場 |
|---|---|---|
| 1 | +0.13% | 11/20 |
| 2 | +0.37% | 14/20 |
| 3 | +1.47%(該批 base 偏低) | 14/20 |
| 決定性(單段緊密交錯 30 對) | +0.88% | 21/30 |
| **合併 90 輪** | 逐輪配對差中位數 **+0.6~0.9%** | **60/90 = 66.7%** |

逐輪配對差中位數(最抗時間漂移的估計)= **+403 hc/s(60 輪)/ +578 hc/s(決定性 30 對)≈ +0.62~0.88%**;sign-test 合併 z=3.06 → **單尾 p≈0.001**;四批同號、全程 bit-exact。
最佳估計 **~+0.6%** —— 小但真實。這是 N1–N7 + R1–R3 全敗後**第一個確認的 win**。

> **⚠️ 重要:與 2026-05-29 舊測的衝突與釐清。** 2026-05-29 的 `gemini_query` 文件曾記「OR-all on early-break GND/PWR scans: C# **−3.07%**」並當成 anti-pattern #3 的證據。本輪 90 輪 interleaved-paired 量到 **+0.6%**,**直接推翻**。
> 釐清:那個 −3.07% 是 **2026-05-30 採用 interleaved-paired 之前**用 **batched 舊方法**量的 —— 而 interleaved-paired 正是因為「batched 對 sub-3% 效應因 time-drift 不可信」才被採用的(見 `interleaved-paired-bench` 記憶:2026-05-30 在 fast-path PullUp-gate change 上 batched 給了模稜兩可/錯誤答案)。**這次的 −3% → +0.6% 翻盤,正是同一個 batched 假象**。一個被當成「dead-end 證據」的舊結論,其實是量測雜訊。
> **連帶教訓**:記憶中其他「2026-05-29 batched 量的 sub-3% 負面結論」現在都應視為**待重驗**(大幅度的如 per-chip 15×、bitset 156×、RCM −3-4%、counter −6% 不受影響 —— 那些方法無關且幅度大)。已更新 `hotpath-ceiling-and-antipatterns` 記憶。
**為何這條會贏(而 N1–N7 全敗)**:它是兩輪以來唯一**移除**熱路徑工作的改動(拿掉 per-gate data-dependent branch),不是加成本。pure-logic gnd/pwr list 短(多為 1–2),早退幾乎不觸發,所以 OR-all 幾乎不多讀,卻省下分支誤判 → 在寬 OoO 核心上小贏。方向與 N-lessons 完全一致(加成本=輸,減成本=贏)。
**已採用(C#)**:`WireCore.FastPath.cs` RecalcNodeFast 的 gnd/pwr 掃描改 OR-all。

**Rust 版(2026-05-31 應使用者要求測):❌ −3.2%,拒絕。** 同款改動移植到 `experiment/rust-s1/src/wire.rs` 的 `recalc_node_fast`,interleaved-paired 40 輪:
- exp 只贏 **3/40**(base 贏 37/40)、median paired diff **−2,250 hc/s = −3.21%**、mean −2.90%、bit-exact(`0x9B103E5E206E4C37`)。
- **教科書級 JIT/LLVM 反號**(見 `jit-vs-llvm-recursive-inline`):同一改動 **C# +0.6% / Rust −3.2%**。LLVM 對 early-break 迴圈的 codegen 顯然比手寫 OR-all 好(或 early-break 真的較常提前終止),OR-all 強制每次掃完整串+sentinel 反而虧。
- **附帶確認**:這次也**證實了 2026-05-29 batched Rust −1.86% 的方向是對的**(只是低估幅度;真值 −3.2%)。對照 C# 同一筆 batched −3.07% 卻是**錯號** —— 所以 batched 在 Rust 上剛好方向對、在 C# 上方向錯,更說明 batched 不可恃,必須逐案 interleaved 重驗。
- Rust 已 revert,維持 baseline(70.1K hc/s)。
(原「length-1 特化」需區分 length-1/N → 又是加分類分支,N-lessons 已證會輸;改成更廣、不需分類的 OR-all。)
把 gnd/pwr 掃描的 `while(*p){ if(state) {f|=...; break;} }` 早退分支,換成無分支 OR-all:`byte any=0; while(*p) any|=NodeStates[*p++]; f|=any<<5;`。
- **動機**:pure-logic gnd-list 多為長度 1–2(反相器/小 NOR),早退幾乎不觸發;OR-all 每次省一個 data-dependent branch,且不需任何額外 per-node 資料。
- **風險**:低/中。gate ON 時 OR-all 會多讀一個 sentinel(早退本可省),所以淨效應**取決於 list 長度分布與 gate-high 機率** → 正是 A/B 要回答的。前置不變式:`NodeStates` 嚴格 0/1(已成立)。
- 通過條件:interleaved median + trimmed-mean > 0 且 checksum 一致。

---

## 執行協定(同上一輪)
每條:① 量 baseline → ② 實作 → ③ interleaved-paired A/B(24 輪 / 200k hc / full_palette,median + trimmed-mean(20%) + 配對勝場)→ ④ checksum 正確性 → ⑤ **只有「效能有幫助 + 正確無誤」才採用、更新本檔狀態、commit+push** → ⑥ 下一條。失敗則 revert + 記錄 + 續下一條。

## 一句話(本輪假設,已被 §實測 部分確認)
原本最看好「可能真的減少計算量」的 R2/R3 —— 二次**實作並實測**:R2 的 drop 破壞 bit-exact(撞 dead-end #8 的 tie-break 電容),且效能上界本來就是 0/負;R3 自動 +29 fast-path node 但貢獻 ≈ 0。如預期被天花板壓死,但這次是親手驗證,不是預測。
反而是被我預判「邊際/≤0」的 **R4(OR-all branchless)** 成了本輪唯一的 win(~+0.4%)。教訓再次成立:**減成本會贏、加成本會輸**,而 R4 是唯一減成本的那條。

## 結果總結(2026-05-31,全部試畢)

| 候選 | 類別 | 結果 | 數字 |
|---|---|---|---|
| R1 NativeAOT | build | ❌ 不採用 | −5.39% median / −5.58% trim / 0-of-20;bit-exact。**已在 net10,AOT 丟 PGO 更慢** |
| R2 supply-short 常數摺疊 | lowering | ❌ 實作並實測 | drop 破 bit-exact(tie-break 電容=dead-end #8);merge 部分 no-op;效能上界 −0.21%/擲硬幣 → 不採用 |
| R3 dead-gate fast-path 擴大 | fast-path×lowering | ❌ 實測 | 被 R2 drop 自動 +29 node,但效能上界已含、貢獻≈0 → 不採用 |
| **R4 OR-all branchless** | fast-path | ✅ **採用** | **+0.79% mean / 39-of-60 配對(p≈0.01)**;bit-exact |

**淨結果**:fast-path 拿到一個小但確認的 win(R4,~+0.6% C# / Rust −3.2% 拒絕);lowering **沒有**剩餘的安全壓縮空間(R2/R3 二次親手實作實測:drop 破 bit-exact=dead-end #8、效能上界 0/負);**.NET 建構已最佳(net10 + JIT + DynamicPGO),NativeAOT 反而慢 5.5%**。
回答使用者三問:fast-path 還有一點(R4 已收)、lowering 基本到頂、改 .NET10 建構模式(AOT)不會更快。後續若要再榨,得換架構(IR/AOT 已證更慢)或更快單核。
**R4 跨平台定論**:**C# +0.6%(採用)/ Rust −3.2%(拒絕)** —— 又一個 JIT/LLVM 反號案例。Rust 維持 baseline。
**待辦**:採用 R4(C#)後 `AprVisualBenchMark/` 內打包的 C# binary 已過時,如需對外比較再重打包。
