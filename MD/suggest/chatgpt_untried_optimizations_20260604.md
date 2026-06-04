# ChatGPT 優化建議 — 「沒用過」項目整理清單(2026-06-04)

> **來源**:`ref/chatgpt/1.txt`~`4.txt`(另一個模型對 S1 C# 引擎 hot-path + 記憶體結構的審視)。
> **本檔用途**:把這批建議**逐條對照目前程式碼 + 既有 dead-end 記憶**,把**真正還沒親手測過**的方法整理成待測清單(§A)。已試過/已實作/對應 dead-end 的列於 §B;**因 baseline 已大改而值得「重測」的老死路**列於 §B2(2026-06-04 user 提醒新增),確定維持關閉的列於 §B3。
> **交叉比對來源**:`MD/suggest/00_open_proposals_summary.md`(活清單)、記憶 `hotpath-ceiling-and-antipatterns`、`csharp-rust-parity-policy`。

---

## 重要前提(先看)

- S1 熱路徑已多輪實測到**天花板**;real-time(42.95M hc/s)在現行 event-driven BFS 架構下不可達。目前最佳(full_palette 300k,top-3):**C# ~90.9K / Rust ~83.3K hc/s**。
- 因此下面**絕大多數預期 ≤1% 或負**;列出只代表「ChatGPT 提了、而且我們還沒親手量過」,不代表有希望。
- 通用鐵律(每次提新點子都先核對):
  1. **減成本才贏、加成本必輸** —— 熱迴圈每 call 加任何 load/branch/shift/prefetch 幾乎都淨負。專案至今的 micro-win 幾乎都是「**移除**或**收窄**工作」(R4 移除分支、T-A 拿掉 enum cast、T3 把 static 提進暫存器)。
  2. **C#/JIT 與 Rust/LLVM 常反號** —— 同一改動各自 interleaved-paired 實測,別盲目 sync(見 `csharp-rust-parity-policy`)。
  3. **netlist 摺疊/丟 transistor 會踩 floating tie-break 電容(dead-end #8)** —— 改變 node 的 `C1c2s.Count+Gates.Count`(= `NodeConnections`)會 silently 破 checksum。
  4. **sub-2% 必用 interleaved-paired**(交替 base/exp + 配對勝場 + 中位數,≥3 batch);batched 會偽造方向。

---

## 📊 測試結果記錄(2026-06-04,持續更新)

逐項 interleaved-paired A/B(`tools/ab_bench.ps1`,base = HEAD `c3993e9`,full_palette 300k,各 ≥20 配對輪,warm-up 丟棄,checksum 全程 `0x794A43ABDF169ADA`)。

| # | 提案 | 結果 | 判定 |
|---|---|---|---|
| A2 | c2 supply range check `(uint)(c2-npwr)>1u` | median **−0.32%** / tmean −0% / **8-20** | ❌ 噪音(RyuJIT 對 `x!=1&&x!=2` 本就生好 codegen),revert |
| A1 | IsPureLogic 併入 NodeInfo.Kind | median **−0.34% / −0.40%**(2 batch)/ **18-40** | ❌ 平到略負,revert。bit-exact + 分類數一致(3929+10784),但 IsPureLogic byte 陣列本就便宜/常駐,改讀 `Kind>>1` 反而把 dispatch 綁到較大的 16B NodeInfo line + 多一個 shift。**ChatGPT 與我都看好的頭號項,實測無 signal —— 再次印證熱路徑不缺這筆 load** |
| A3 | RecalcList/Next `int*`→`ushort*` | median **−1.51%** / tmean −1.34% / **4-20** | ❌ 明確負,revert。對應 Rust `group_buf` u16 −0.91%:`(ushort)` cast/zero-extend 成本 > queue bandwidth 省的(queue 工作集小、本就常駐) |
| A5 | NodeConnections `int*`→`ushort*` | median **−0.11%** / tmean −0.08% / **11-20** | ❌ 純噪音,revert。冷陣列(<1% floating tie-break),省空間不影響熱工作集 |

| RT2 | SetNodeState 冷路徑 split(enqueue walk 拆 NoInlining method) | median **−4.71%** / tmean −4.49% / **0-20** | ❌ 決定性負,revert。**比舊 Q5 −1.5% 更糟** —— 假設「body 變大→冷拆會翻」**被推翻**:長大的 body(含 ulong dual-pair)更需要 inline 進 BFS writeback,拆出去斷了 inline cascade + 每次 state-change 加 call overhead。anti-pattern #4 再次成立 |
| RT1 | branchless XOR enqueue(C#) | (未重測) | ⏸️ **2026-06-03 已 interleaved 重測 = −1%(14/52)**,非 batched-era 殘留 → 已是確認負,重測冗餘,跳過 |
| **A6** | **callback `Dictionary`→`CallbackInfo?[]` 直接陣列** | median **+1.36/+1.36/+1.46%**(3 batch)/ **49-60** | ✅ **採用!(commit `1447f8b`,+~1.4%)** —— 全清單唯一 win,且推翻舊註解「array 115KB/gen2 不值得」的*未實測*假設。HasCallback group-walk 對每個含 watched bus node 的 group **逐成員**做 `Dictionary.TryGetValue`(hash+probe),遠比 callback 實際 fire 頻繁;改直接 array index 省掉 hash。bit-exact。**預期是「冷路徑噪音」—— 實測打臉,再次驗證「量,別假設」** |
| A4 | 獨立 gate pool(pool 分離,NodeTlistGates 留 int) | 7 batch:median −0.62/+0.62/+0.62/+1.34/−0.26/+0.19/+0.10% / **76-140(54.3%)** / tmean 合計 ~+0.26% | ❌ 噪音,revert。各 batch 反覆變號(−0.62→+1.34→~0),配對勝率 54%≈1σ(p≈0.16,不顯著)。shrink TransistorList 對隨機 gather 無感、pool 分離反多一條 cache stream。**不為不可靠的 ~+0.2% 加永久結構複雜度** |
| A7 | RecalcNodeFast gnd/pwr fanout-adaptive(≤2 OR-all,>2 early-break) | median **−0.87%** / tmean −0.82% / **6-20** | ❌ 負,revert。**histogram 預測正確**:inline fast-path 節點 gnd+pwr count **96% ≤2**(0:2660/1:8948/2:1566,≥3 僅 4.6%),threshold-branch 課稅 96% > early-break 救 4.6%;R4 OR-all 本就贏 |
| A8 | high-fanout overflow c1c2 改 length-based | (未實作 — histogram gate 擋下) | ⏸️ **moot-by-data**:overflow fast-path 節點僅 **900 個(占 fast-path 6%)**且路徑罕見;length-based 需獨立 metadata pool = **每次存取多一筆 load 換掉一個便宜 sentinel**,正是已測 **−0.82% ulong-overflow dead-end** 的機制。為罕見路徑做大重構去確認near-certain負 = data-gate 正要擋的浪費。要實測可再投入,但預期負 |
| A6→Rust | sync A6(Dict→array)到 Rust | (無事可做) | ✅/N/A **Rust 早已 array-based**(`target_to_handler: Vec<i32>`,dense、node-id 索引,從無 HashMap)→ 「a win can be already-present」(同 T-A) |
| A6b | 反向移植:把 Rust 的 per-member `flags & HasCallback` 預檢搬進 C#(查 cbByNode 前先 gate) | median **−0.58/−0.13/+0.33%**(3 batch)/ **30-60(50%)** | ❌ 噪音,不採用。經典 sign-flip:flag-gate 是 **Rust 的 default win**,但 C# 上多一筆 Flags load+branch 抵掉省下的冷 cbByNode read。**C# 留純 A6(array,無 flag-gate);Rust 留 array+flag-gate,各取所長** |

### 📌 最終總結(2026-06-04,清單全跑完)
**9 項評估完畢:1 採用(A6 +1.4%)、6 實測負/噪音(A2/A1/A3/A5/RT2/A4/A7)、1 已確認負略過(RT1)、1 data-gate 擋下(A8)。**

- **唯一 win = A6**,而且它是清單裡**最不被看好的一項**(我預測「冷路徑噪音」)—— callback group-walk 的 `Dictionary.TryGetValue` 比想像中熱。**最被看好的 A1(移除隨機 load)反而無 signal。** → 本批最大教訓重申:**預測不可恃,逐項實測才算數(user 的「分開測」紀律 + 「老死路會變」直覺都再次被驗證 —— A6 等於推翻了一個*基於未實測假設*的舊決定)。**
- 其餘全部一致指向同一面牆:**BFS 隨機 gather 的記憶體延遲**。footprint 縮減(A1/A3/A5/A4 footprint 面)對 latency-bound 無感;結構重構(RT2/A4 pool 分離)斷 inline / 加 stream 反傷;per-call 取捨(A2/A7)加成本必輸。
- **A6 後新 baseline ≈ C# 92K hc/s**(待 top-3 複測)。real-time 仍 ~466× 外,需換架構或更快硬體。

**判定:這批 micro/footprint/restructure 候選 CLOSED。** 唯一未榨的長 shot = A8(罕見路徑大重構,預期負);其餘已逐條 interleaved-paired 驗空。

---

## §A 待測清單 — 真正沒用過的方法

依「值得測的順序」排(收益期望 × 風險低 × 還沒測過)。

| # | 提案 | 來源 | 預期 | 風險 | 一句話機制 |
|---|---|---|---|---|---|
| A1 | **`IsPureLogic` 併入 `NodeInfo`(repurpose `Inline` byte 的 bits 1-2 當 class)** | 4.txt §3 | 小正~噪音 | 中 | 移除 RecalcNode dispatch 開頭那筆**多餘的隨機 array load** |
| A2 | **`c2` supply 檢查改 unsigned range check** `(uint)(c2-npwr) > 1u` | 1/2.txt、3.txt §1 | 噪音~小正 | 低 | 兩個 `!=` 合成一次比較,少一個 `ngnd` local |
| A3 | **`RecalcList`/`RecalcListNext` `int*` → `ushort*`** | 4.txt §2 | 噪音~小正 | 低 | queue 是 sequential scan,半 bandwidth、更易進 cache |
| A4 | **獨立 gate pool + `NodeTlistGates` 16-bit index** | 4.txt §1 | 小正~噪音 | 中 | 把 writeback 用的 gate 子表從共用 `TransistorList`(~115K)拆出獨立 pool(可 <65536),縮 footprint + 不互相污染 cache |
| A5 | **`NodeConnections` `int*` → `ushort*`** | 4.txt §4 | 噪音(省空間) | 低 | cold(只 floating tie-break 用),純省 footprint,讓別的資料更易留 cache |
| A6 | **`_callbackByNode` `Dictionary<int,>` → `CallbackInfo?[]` array** | 3.txt §5 | 噪音(冷路徑) | 低 | callback group 走訪改 array index、避開 Dictionary hash |
| A7 | **`RecalcNodeFast` GND/PWR 掃描依 fanout 走 branchless vs early-break** | 3.txt §3 | 需資料決定 | 中 | 短 list OR-all(現況 R4)、長 list early-break;**先收 histogram 再決定** |
| A8 | **high-fanout overflow 子表改 length-based(取代 0-terminated)** | 4.txt §7 | 中(只高 fanout) | 中高 | 長串省 sentinel 分支,代價是多一筆 overflow metadata load |

### 逐項細節

**A1 — `IsPureLogic` 併入 `NodeInfo.Inline`(bits 1-2 當 class)** ★最值得測
- 現況:`RecalcNode`(`WireCore.Recalc.cs:120`)每個 dirty node 先讀 `byte cls = IsPureLogic[nn]`(獨立 `byte*` array,`WireCore.FastPath.cs:36`)再 dispatch。
- 關鍵觀察:cls==0/1/2 **三條路徑後續都會讀 `NodeInfos[nn]`**(BFS 走訪、`RecalcNodeFast`、dyn c1c2s 檢查都要)。所以 `IsPureLogic[nn]` 是一筆**多餘的隨機 load**(dependency stream + ~14KB array)。
- 可行性:`NodeInfo` offset 1 的 `Inline` byte 目前只用 bit 0(值 0/1),**bits 1-2 是空的**(`WireCore.cs:293`)。把 class(0/1/2)塞進去**不撐破 16B / 不破壞 4-node-per-line**。
- 改法:`Inline` 改名 `Kind`,`bool inline = (Kind & 1)`、`int cls = Kind >> 1`;`ClassifyPureLogicNodes()` 改寫 `Kind`、移除 `IsPureLogic` array。
- 注意:這是**移除一個 array load**,不是 state-caching(anti-pattern #1 是「加儲存」,這裡是「減」)→ 不違反鐵律 1。是 ChatGPT 認為最有性能意義的一項,我同意。

**A2 — `c2` supply unsigned range check**
- 現況:`WireCore.Recalc.cs:193` 與 `:198` 仍是 `if (c2a != npwr && c2a != ngnd && nextHash[c2a] == 0)`。
- 改法:`Npwr=1, Ngnd=2` 連續 → `(uint)(c2a - npwr) > 1u` 同時排除 1/2,少一個 compare、可拿掉 `ngnd` local。
- 風險:依賴 `Ngnd == Npwr + 1`(需加註解 / `Debug.Assert`)。RyuJIT 對 `x!=c1 && x!=c2` 可能已生不錯 codegen → 期望噪音~小正。低風險,先測。

**A3 — `RecalcList`/`RecalcListNext` → `ushort*`**
- 現況:`WireCore.cs:93-94` 是 `int*`。NodeCount <65536(`_groupBuf` 已是 `ushort*` 的同理)。
- 改法:queue 改 `ushort*`,讀出 `int nn = RecalcList[i]`。每 settle pass 都掃 → 半 bandwidth。
- 風險:JIT 對 `ushort*` load 後 zero-extend 可能多指令;低風險,要測。**Rust 端先前 `group_buf` u16 是 −0.91%(LLVM cast 反傷)** → 這項**只在 C# 測**,Rust 不跟。

**A4 — 獨立 gate pool + 16-bit index**
- 現況:`NodeTlistGates`(`WireCore.cs:80`)是 `int*`,`SetNodeState` 每次 state flip 都讀它。共用的 `TransistorList` ~115K → 不能直接 ushort-index。
- 改法:把 gate 子表從共用 pool 拆成獨立 `GateList`(長度有機會 <65536)→ `NodeTlistGates` 可 `ushort*`,writeback pool 更小更集中,且 BFS overflow channel pool 與 writeback gate pool **不互相污染 cache**。
- 風險:build 階段要再拆 pool;中等侵入性。期望小正~噪音。

**A5 — `NodeConnections` → `ushort*`**
- 現況:`WireCore.cs:79` 是 `int*`;只在 `GetNodeValue()` 的 `_groupFlags == None`(floating,<1% walks)用。
- 改法:改 `ushort*`(count 不會 >65535),比較時轉 int。**純省空間**,不直接動 hot path。
- ⚠️ **正確性**:只改**儲存寬度**、不改值 → bit-exact。**絕不可改變 capacitance 的值語意**(dead-end #8:tie-break 權重一動就破 checksum)。

**A6 — `_callbackByNode` → array**
- 現況:`WireCore.Handlers.cs:56` 是 `Dictionary<int, CallbackInfo>?`。
- 改法:`CallbackInfo?[NodeCount]`,group 走訪改 `arr[_groupBuf[i]]`。多一個 ~數百 KB managed array(`ClearPostLoadBuildState` 後保留)。
- ROI 低:memory handler 冷(~5×/200k hc),callback group 罕見 → 期望噪音。但低風險、語意乾淨,可順手測。

**A7 — `RecalcNodeFast` fanout-adaptive 掃描**
- 現況:GND/PWR 掃描是 R4 OR-all branchless(`byte any=0; while(*p) any|=...; flags|=any<<5`)。
- 提案:若實測「第一個 gate 常 ON」,長 list early-break 可能贏;`if (count<=2) branchless else early-break`。
- ⚠️ **資料先行**:這要**先收 `GndCount`/`PwrCount` histogram + first-ON 位置**(見 §D)再決定;加 branch 本身有 code-size/誤判成本。**不要直接改**。

**A8 — high-fanout overflow length-based 子表**
- 現況:`TransistorList` 子表 0-terminated,迴圈 `while (*p != 0)`。
- 提案:長串改 `end = p + len; while (p < end)` 省 sentinel 檢查;但 `NodeInfo` union 12 bytes 放不下三個 (start+len) → 需獨立 overflow metadata pool(多一筆 load)。
- ⚠️ trade-off:少 sentinel 分支、多 metadata load。**只在高 fanout overflow node 測**(短 walk 必輸,呼應 ulong dual-pair 的 walk-length 判別)。風險中高,排最後。

---

## §B 已測過 / 已實作 / 對應既有 dead-end

> ⚠️ **注意(2026-06-04 user 提醒)**:baseline 自這些 dead-end 量測以來**已大改**(R-1 dyn-singleton、S2-A inline payload、T2/T3 hoist、T-A int-promotion…),**部分舊死路值得在新 baseline 重測** —— 見下方 **§B2**。以下表格是「對應到哪條既有結論」的索引,不代表全部永久封死。

| ChatGPT 提案 | 來源 | 結論 |
|---|---|---|
| **locality-aware node renumbering**(graph BFS/adjacency clustering 重編號) | 4.txt §8(他列為「最大方向」) | ❌ **= dead-end #5「RCM reorder −3~4%」**。`00_open_proposals §3` 也記:Gemini 同樣提過、差點被當新點子重做。目前 lowering 只做保序壓縮(`WireCore.Lower.cs:73`),locality 重排已驗證為負。 |
| **narrow-unroll `C1c2Count == 0/1/2`** special case | 3.txt §4 | ❌ 已測:inline 0-3 loop unroll −0.77%、1-2 element group unroll = 噪音(`hotpath-ceiling` 2026-06-04 段)。dispatch loop 已 latency-hidden。 |
| **replicated gate-state cache**(把 state 複製到 adjacency 附近) | 4.txt §5 | ❌ = **counter-fastpath dead-end −6%**(state-caching fallacy / anti-pattern #1)。ChatGPT 自己也標「不建議立刻做」。 |
| **bitset `NodeStates` / `RecalcHash`** | 4.txt §5/§6 | ❌ 已測:shift+mask 抵消 cache 收益(`RecalcHash` 已是 `byte*`)。ChatGPT 也明說「不建議」。 |
| **`TransistorList` 拆 SoA(gate/other 兩串)** | 4.txt §6.1 | ❌ ChatGPT 自己不建議:現況 interleaved AoS + `ulong` dual-pair 已有實測收益(+1.2~2.35%),拆 SoA 變兩條 stream 更差。 |
| **`SetNodeState` 已是 loop-unswitch** | 3.txt(他建議的前提) | ✅ 已實作(`#G2`,`newState==0` vs else 分支已存在,`WireCore.Recalc.cs:184/210`)。 |
| **`ProcessQueueInterp` 把 `RecalcHash` local 化** | 3.txt §6 | ◐ 部分已做:`AddNodeToGroup` 已 hoist `byte* recalcHash = RecalcHash`(T3,+3.2%)。再傳 pointer 為參數 = 低優先,期望噪音。 |

> 另:ChatGPT §3.7/§3.8(`Step(1)` inline、`RunFrame` 避開 `Step(1)`)只影響 **frame-stepping / vblank 偵測路徑**(`WireCore.System.cs:203`),不在 `--benchmark` 的原始 hot loop 上 → ROI 極低,未列入待測;真要做屬可讀性層級。

---

## §B2 老死路再測候選(baseline 已大改,值得重量)

**判別原則**(來自 `hotpath-ceiling` 的 R4 教訓):
- ✅ **batched 時代的 sub-3% 負值** → **可疑,值得 interleaved-paired 重測**。R4 OR-all 當年 batched −3.07% 被當死路,interleaved-paired 重測是 **+0.6%** 並採用;同批其他 sub-3% 負值同樣可疑。
- ✅ **與「新結構」直接互動的舊死路** → baseline 變了,結論可能翻。例:`SetNodeState` body 因 ulong dual-pair + hoist 已**變大**,當年「冷路徑 split」的 i-cache 論點現在更強;S2-A inline payload 改變了「哪些 load 是隨機的」,locality 重排的前提也變了。
- ❌ **大幅度、method-independent 的負值/正確性破壞** → 不會因 baseline 微調而翻,維持關閉(見 §B3)。

| # | 老死路 | 舊結果 | 為何新 baseline 可能翻 | 信心 |
|---|---|---|---|---|
| RT1 | **Branchless XOR enqueue(C#)** | batched −2.15% | batched 時代 sub-3% 負(R4 同款前科);且這是 **Rust 的 default win(+1.63%)**;`SetNodeState` 現有 ulong+hoist,codegen 環境已不同 | 中 |
| RT2 | **`SetNodeState` 冷路徑 split(`newState==0` body 拆 method)** | Q5 cold-split −1.5% / −1.7% | `SetNodeState` body 自 Q5 後**長大不少**(ulong dual-pair、hoist locals)→ early-return path 的 i-cache/分支預測論點變強;= ChatGPT 3.txt §3.2 提案 | 中低 |
| RT3 | **RecalcNodeFast OR-all(Rust)** | batched-era −3.2% / 配對 3-40 | Rust 已吃 T2(每次 group walk 都走平坦清單),codegen 已變;C# 版同款是 +0.6% 採用中。**只 Rust 端重測** | 低(限 Rust) |
| RT4 | **locality-aware node renumbering / RCM** | −3~4%(method-independent) | S2-A inline payload 後,主要隨機 gather 已集中在 `NodeStates[gate]` / `NodeInfos[other]`;當年量測的記憶體佈局已不同。**但這是大幅度 method-independent 負值 + 實作昂貴** → ChatGPT 的頭號方向,卻是本表**信心最低、CP 值最差**的一項 | 低(高風險高成本) |

> RT1/RT2 風險低、值得排進測試批次;RT3 限 Rust;**RT4 只在 RT1~A8 全測完、仍想再挖時才碰**(實作成本最高、翻盤機率最低,但若真翻是最大的)。
> 重測一律 interleaved-paired ≥3 batch + checksum,C#/Rust 分開判斷。

## §B3 維持關閉(method-independent,別浪費時間重測)

這些是大幅度、與寫法無關的負值或正確性破壞,**baseline 微調不會翻**,除非換架構(IR/AOT,已禁)或改正確性 gate:

| 死路 | 結果 | 為何不會翻 |
|---|---|---|
| Per-chip parallel(rayon) | 15× 慢 | MESI 跨核 round-trip(40-100ns)≫ 事件迴圈(數十 ns),Amdahl |
| Bit-parallel BFS(Ligra dense) | 156× 慢 | walk 平均 1.4 node,bit-parallel overhead 壓垮 99% 小 walk |
| Counter FastPath(active_gnd/pwr_count) | −6% | write path(SetNodeState)比 read(fast-path)頻 ~10×;state-caching fallacy |
| Generation counter / lastGate cache | −1.6~5.4% | 同 state-caching fallacy:加 L1d footprint 換 ALU,淨負 |
| Software prefetch(D=8) | −5.45%(2026-06-03 最近才量) | 硬體 MLP 已飽和(隨機 gate gather),prefetch 只是 overhead + 污染 cache |
| AOT-batch / IR codegen / GPU | 3-84× 慢 | 全節點重算 ~14.7K/half-cycle(實際只幾百個變),架構性冗餘 |
| prune-merge / stub-removal / const-fold | 破 PPU 黑屏 / 破 checksum | dead-end #8:動到 `NodeConnections`(floating tie-break 電容權重)就 silently 破正確性 |
| iso-state culling(Direction C) | 破 checksum | BFS skip 需證明整個下游子樹不貢獻 group_flags,runtime 檢查成本 > 省下的 |

> 唯一可能解封的條件:netlist 摺疊類(prune-merge/const-fold)若哪天**過了 per-node PPU 視覺等價 gate**(不只 CPU checksum)才重議;其餘維持關閉。

## §C 僅可讀性 / 維護性(非效能,不排測)

- **`NodeValue` enum 收斂**(3.txt §10):hot path 實際用 `byte` + `FlagsToState`,`NodeValue` 像設計殘留 → 可移除或只留 debug 層,避免誤導。**僅在哪天要做精確 floating semantics 才相關**(會動 hot path,現在不做)。
- **註解與實作同步**(3.txt §11):部分頭註解(如 `NodeInfo` 早期「flags + 3× indices」描述)已與 union+inline-payload 實作脫節 → 越靠近 hot path 越要精準。建議 benchmark 結論集中成 `// PERF[date][rom][build]:` 格式。

---

## §D 建議測試順序 + 前置

**前置(資料先行)** — 收這些 runtime histogram 才能 gate A7/A8,並驗證 A1 的假設:
1. `RecalcNode` cls=0/1/2 命中次數;cls==2 dynamic-singleton 成功率 vs fallback BFS 率。
2. `GndCount`/`PwrCount` histogram + first-ON 位置分布(gate A7)。
3. overflow node 的 `c1c2` pair count 分布(gate A8)。
4. `SetNodeState` state-changed vs early-return 比例、`c2` 是 supply/already-queued/newly-queued 比例(佐證 A2)。

**測試批次(由低風險、可能有意義排起;新提案 A* 與老死路再測 RT* 混排)**:
1. **A2**(range check)+ **A3**(ushort queue)+ **A5**(ushort NodeConnections) — 三條低風險小消改,各自 interleaved-paired。
2. **A1**(IsPureLogic 併入 NodeInfo.Kind) — ChatGPT 與我都認為最有性能意義(移除一筆隨機 load);中風險,單獨測 + checksum。
3. **RT1**(branchless XOR enqueue,C#)+ **RT2**(SetNodeState 冷路徑 split) — 兩條老死路在新 baseline 重測;batched-era 負值 + 結構已變,翻盤機率不低。
4. **A6**(callback array) — 低風險、冷路徑,順手測。
5. **A4**(獨立 gate pool 16-bit) — 中等侵入,需改 build 階段。
6. 收完前置數據後再決定 **A7 / A8**;**RT3**(Rust OR-all)併入 Rust 端的 sync 審視時測。
7. **RT4**(RCM/locality renumber)— 只在以上全測完、仍想再挖時才碰(成本最高、信心最低)。

**全程鐵律**:每條獨立(分開測,別綑綁)、interleaved-paired ≥3 batch、checksum 必須維持 `0x794A43ABDF169ADA`、有進步才留、C#/Rust 分開判斷不盲 sync。測完把結論回寫進 `00_open_proposals_summary.md`(活清單)的 §1/§3。
