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

### R2 — supply-short 常數摺疊 + 死電晶體串級  〔lowering〕  ➖ measured-moot,不實作 (2026-05-31)
**Step 0 實測殘餘(`--lower-diag` 暫時碼,已 revert)**:**84** supply-short nodes、其中 **56** provably-const、cascade 僅 **84** gated-transistors。
→ 最樂觀只摺 56/14,681 = **0.38%** node、影響 84/26,726 = **0.31%** transistor;且 56 個常數 node **本來就永不 toggle → 永不 re-enqueue**(runtime 早已 inert),真正能省的 84 個電晶體**全由常數驅動 = 冷路徑**。與 dead-end-skip 同一面牆(殘餘 <1% 且冷),A/B 必被噪音吃掉或淨負 → **依協定不實作**。
<details><summary>原始設計(保留參考)</summary>
延伸 `Lower.cs`:一個 normal node N 若被**強(non-weak)always-on device 連到 supply**,且**沒有任何可能把它拉向另一極的 channel**,則 N 可證明為常數(vcc→恆 1 / vss→恆 0)。
- 摺疊後串級:由常數 node 當 gate 的電晶體 → 恆通(merge)或恆斷(drop);迭代到 fixpoint。
- **Step 0(先做)**:加 `--lower-diag`(cold path)量殘餘 yield —— 數出這類常數 node 數 + 可串級摺掉的電晶體數。**若 ≈ 0 就不實作**(誠實標 moot)。
- **正確性**:可形式證明 bit-exact(與既有 always-on-merge 同性質)。關鍵陷阱:連到 vcc 但**另有可導通 pull-down** 的 node **不是**常數(LUT 中 Gnd > Pwr)——必須排除「有對向 channel」者。這也是原作者當初停在 normal↔normal 的原因。
- **payoff 指標**:lowering 縮減量 + fast-path 覆蓋率(應 > 26.5%)+ hc/s。
- 風險:中(證明要嚴謹)。
</details>

### R3 — dead-gate channel 的 fast-path 擴大  〔fast-path × lowering〕  ➖ moot(依賴 R2),不實作 (2026-05-31)
依賴 R2 找出的常數-OFF 集合,而該集合只在那 84 個 cascade 電晶體裡(其中還只有 const-OFF 子集)。一個 node 要因此變 fast-path 須**全部** c1c2s channel 都經 const-OFF gate —— 在這麼小的集合下幾乎不可能(預估 0–數個)。yield ≈ 0 → 不實作。
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
**Rust 版**:review 文件也列了 `recalc_node_fast` OR-all,但依 `jit-vs-llvm-recursive-inline` 教訓「C# 熱路徑改動別盲目同步 Rust」,本輪 C# 為主,Rust 留待各自 A/B(未做)。
**已採用**:`WireCore.FastPath.cs` RecalcNodeFast 的 gnd/pwr 掃描改 OR-all。
(原「length-1 特化」需區分 length-1/N → 又是加分類分支,N-lessons 已證會輸;改成更廣、不需分類的 OR-all。)
把 gnd/pwr 掃描的 `while(*p){ if(state) {f|=...; break;} }` 早退分支,換成無分支 OR-all:`byte any=0; while(*p) any|=NodeStates[*p++]; f|=any<<5;`。
- **動機**:pure-logic gnd-list 多為長度 1–2(反相器/小 NOR),早退幾乎不觸發;OR-all 每次省一個 data-dependent branch,且不需任何額外 per-node 資料。
- **風險**:低/中。gate ON 時 OR-all 會多讀一個 sentinel(早退本可省),所以淨效應**取決於 list 長度分布與 gate-high 機率** → 正是 A/B 要回答的。前置不變式:`NodeStates` 嚴格 0/1(已成立)。
- 通過條件:interleaved median + trimmed-mean > 0 且 checksum 一致。

---

## 執行協定(同上一輪)
每條:① 量 baseline → ② 實作 → ③ interleaved-paired A/B(24 輪 / 200k hc / full_palette,median + trimmed-mean(20%) + 配對勝場)→ ④ checksum 正確性 → ⑤ **只有「效能有幫助 + 正確無誤」才採用、更新本檔狀態、commit+push** → ⑥ 下一條。失敗則 revert + 記錄 + 續下一條。

## 一句話(本輪假設,已被 §實測 部分確認)
原本最看好「可能真的減少計算量」的 R2/R3 —— 實測殘餘 < 1% 且冷,如預期被天花板壓死。
反而是被我預判「邊際/≤0」的 **R4(OR-all branchless)** 成了本輪唯一的 win(~+0.4%)。教訓再次成立:**減成本會贏、加成本會輸**,而 R4 是唯一減成本的那條。

## 結果總結(2026-05-31,全部試畢)

| 候選 | 類別 | 結果 | 數字 |
|---|---|---|---|
| R1 NativeAOT | build | ❌ 不採用 | −5.39% median / −5.58% trim / 0-of-20;bit-exact。**已在 net10,AOT 丟 PGO 更慢** |
| R2 supply-short 常數摺疊 | lowering | ➖ measured-moot | 殘餘 84/56/84 = <0.4% 且冷 → 不實作 |
| R3 dead-gate fast-path 擴大 | fast-path×lowering | ➖ moot | 依賴 R2 的微小集合 → yield≈0 → 不實作 |
| **R4 OR-all branchless** | fast-path | ✅ **採用** | **+0.79% mean / 39-of-60 配對(p≈0.01)**;bit-exact |

**淨結果**:fast-path 拿到一個小但確認的 win(R4,~+0.4%);lowering **沒有**剩餘的安全壓縮空間(R2/R3 殘餘 <1% 且冷);**.NET 建構已最佳(net10 + JIT + DynamicPGO),NativeAOT 反而慢 5.5%**。
回答使用者三問:fast-path 還有一點(R4 已收)、lowering 基本到頂、改 .NET10 建構模式(AOT)不會更快。後續若要再榨,得換架構(IR/AOT 已證更慢)或更快單核。
**待辦**:R4 是 C# 專屬實測;Rust `recalc_node_fast` 的同款 OR-all 尚未各自 A/B(本輪 C# 為主)。採用 R4 後 `AprVisualBenchMark/` 內打包的 binary 已過時,如需對外比較再重打包。
