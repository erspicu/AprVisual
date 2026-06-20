# S1 branch-miss 熱點分析(軟體方向熵 + 硬體 perf)

> 日期:2026-06-20 · 引擎:`src/AprVisual.S1`(main `57eac98`) · 量測:`full_palette.nes` 400,000 half-cycles `--extra-ram` · 硬體:Raspberry Pi 5 / Cortex-A76 · checksum golden `0x9174E19D961CB6E5`

## 方法(兩個互補角度)
- **(A) 軟體 — 方向熵 × 頻率**:`[branch-dist]` DEBUG profiler 數每個資料相依分支的方向分布(taken / not-taken)與次數。~50/50 = 高熵 = 容易誤預測;偏斜 = 預測器吃得住。用 `min(p,1-p) × count` 估「若該分支不可預測時的誤預測量」做排名。
- **(B) 硬體 — perf**:Pi 上 `perf stat -e branches,branch-misses`(總量)+ `perf record -e branch-misses`(分布)。

## (B) 硬體總量(Pi A76,400k hc)
| 指標 | 值 |
|---|--:|
| instructions | 39,047,433,936 |
| branches | 9,162,042,441(佔指令 **23.5%**)|
| **branch-misses** | **243,088,717** |
| branch-miss 率 | **2.65%** of branches |
| MPKI | **≈ 6.23**(243M / 39.05G × 1000)|

**per-method 無法拆**:熱路徑的 RecalcNode / RecalcNodeFast / ComputeNodeGroup / AddNodeToGroup / GetNodeValue / SetNodeState 全是 `[AggressiveInlining]` → JIT 折成**單一 ~3.5KB 機器碼 blob**(perf record 的樣本全落在 `0x44a4dc–0x44b2bc` 這一段連續位址);加上 .NET 的 W^X 雙映射 code heap 讓 perf 無法用 perf-map 符號化(顯示成 `memfd:doublemapper`)。→ 硬體只能確認「branch-miss 全在那個 inlined 熱迴圈裡」,要分到「哪個邏輯分支」得靠 (A) 或差分量測(見末節)。

## (A) 軟體 per-branch（`[branch-dist]`，400k hc）
| 分支 | 方向分布 | 次數 n | min(p,1-p)×n 估計 | 判讀 |
|---|---|--:|--:|---|
| **turn-on enqueue prune**(keep/skip,每電晶體)| **58.2 / 41.8** | 108,515,740 | **≈ 45.4M** | 最熱 × 高熵 → **頭號嫌疑**(在 SetNodeState 內)|
| **cls2 channels**(全 off? / 有 on)| **70.9 / 29.1** | 128,424,737 | **≈ 37.4M** | 高頻中高熵 → **第二**(在 RecalcNode dispatch 內)|
| dispatch cls(cls==1?)| 20.3 taken | 182,457,809 | ≈ 37.0M(上界)| cls **靜態 per-node** → 預測器靠 node 關聯多半吃得住 → **實際遠低於上界** |
| fast-path drive/float | 84.2 / 15.8 | 128,033,212 | ≈ 20.2M(上界)| 偏斜 → 多半可預測 → 實際偏低 |
| **SetNodeState turn-on/off** | **50.0 / 50.0** | 38,941,710 | **≈ 19.5M** | **完美熵**,頻率較低;clock 驅動或許有 per-node pattern,但全域交錯難預測 |

> 估計值是「**若不可預測**」的上界;偏斜且靜態的分支(dispatch、fast-path)實際誤預測遠低於上界,高熵分支(prune 58/42、cls2 71/29、turn 50/50)才是真正貢獻者。這幾個「if-unpredictable」上界相加 ≈ 159M,低於硬體總量 243M —— 差額 ≈ 84M 在**未插樁的分支**(AddNodeToGroup 內逐電晶體的 gate-ON 測試與 break、cls2 內層 c1c2 掃描、各迴圈 back-edge 等,多數也資料相依但多半規律)。

## 熱點結論(排名)
1. **turn-on enqueue 同態剪枝判斷**(SetNodeState,58/42,108M)—— 頭號 branch-miss 熱點。
2. **cls2「通道全 off?」**(RecalcNode dispatch,71/29,128M)—— 第二。
3. **SetNodeState turn-on/off 方向**(50/50,39M)—— 熵最高但頻率較低。
4. dispatch cls / fast-path drive-float —— 偏斜+靜態,實際多半被預測器吃住,非主要。
5. 其餘 ~1/3 散在 BFS 內層的逐電晶體分支(無單一大宗)。

## ⚠️ 重要 caveat — 這是「特徵描述」不是「最佳化機會」
branch-miss 在本引擎是**次要瓶頸**:perf 顯示 backend(記憶體延遲)stall 19.5% vs frontend 5.1%,**branch-miss 的懲罰大多躲在記憶體 stall 後面**(OoO 在等 scattered load 時早就把分支重新導向了)。已實證:
- **I-1**(branchless gate-OR)實測把 **branch-miss 降了 −2.5%,但 cycle 反而 +1.77%** → 修分支不改善 wall-clock。
- 頭號熱點「enqueue 剪枝」是 P-1 同態剪枝(+15% 大功臣),動它(branchless / Rule B 類)風險高且歷史上發散/退步。

→ **不建議以「降 branch-miss」為最佳化目標**;此分析的價值是把 ~6 MPKI 的來源講清楚,佐證「記憶體延遲才是牆」的結論。

## 附:要精確「per-branch 硬體歸因」的方法(未做)
inlining 讓 perf 無法符號化到邏輯分支。要硬體級精確歸因,可用**差分量測**:對單一可疑分支做 branchless/always-taken 改寫(需 bit-exact 或暫時破壞只為量測),`perf stat -e branch-misses` 看總量掉多少 = 該分支的真實貢獻。成本高且每個分支一輪,目前不值得(見上面 caveat)。

## 工具
- `[branch-dist]` DEBUG profiler(`WireCore.DiagBr*`,`#if DEBUG`,Release byte-identical);跑法 `dotnet run -c Debug --project src/AprVisual.S1 -- --benchmark <rom> --bench-hc N --extra-ram --system-def-dir <dir>`,讀 `[branch-dist]` 段。
- 硬體:Pi `sudo perf stat -e instructions,branches,branch-misses -- env DOTNET_ROOT=… dotnet <Release.dll> --benchmark …`。
