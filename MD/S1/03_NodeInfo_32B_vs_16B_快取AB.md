# S1 快取 A/B:NodeInfo 32B vs 16B 的 D-cache miss 比較

> 問題:把 `NodeInfo` 從 32 bytes 縮成 16 bytes(commit `a689a3e`,`NodeInfos` 460 KB→230 KB),
> **L-cache miss 到底有沒有進步?降了幾 %?**
> 結論先講:**有 —— 熱迴圈的 D-cache miss 每千指令降 ~7.3%**(全行程 −6.1%)。但吞吐量只 +0.75%,
> 因為引擎是**延遲 bound**,不是 miss-數量 bound(見 §4)。

## 1. 方法(為什麼這次的數字可信)

- **單一變數**:32B build 取自 16B commit 的 parent `fbf789f`(S2-A 鄰接內聯已在內,**只差 NodeInfo 大小**:
  32B `InlineCap=7` / 無顯式 `Size` vs 16B `InlineCap=6` / explicit-layout union `Size=16`)。兩版同 hc checksum
  皆 `0x9B103E5E206E4C37`(bit-exact)。
- **同條件背靠背捕獲**:`tools\profile_s1_ab.bat`(系統管理員、自我提權),兩版用**完全相同**設定:
  full_palette 1,000,000 hc、`--extra-ram`、PerfView `-CpuCounters:"DcacheMisses:65536,IcacheMisses:65536,InstructionRetired:65536"`。
- **指標 = MPKI(misses / 1000 retired instructions)**:這是**比值**,時脈/溫度/背景負載漂移會自動抵消,
  因此 32B 與 16B 可直接相比(吞吐量 hc/s 反而會被熱機/順序污染,不適合當此處的判準)。
- 分析:`tools\etl-analyze`(TraceEvent 3.2.2,從 CLR rundown 解析 managed 符號)→ 把 PMC 樣本依
  counter + 解析後的 top-of-stack 方法歸屬。三個 counter 同為 1/65536 取樣,樣本數可直接相除。

## 2. 原始樣本數(本行程 AprVisual,1/65536)

| | InstructionRetired | DcacheMisses | IcacheMisses |
|---|---|---|---|
| **32B**(全行程) | 3,868,248 | 95,502 | 4,994 |
| **16B**(全行程) | 3,892,581 | 90,267 | 5,489 |
| **32B**(`ProcessQueueInterp`) | 3,539,050 | 68,440 | 977 |
| **16B**(`ProcessQueueInterp`) | 3,564,608 | 63,895 | 1,177 |

> 兩版 InstructionRetired(本行程)幾乎相等(3.87M vs 3.89M,+0.6%)→ 確認是同一個 workload,MPKI 比較乾淨。
> 樣本數量級大(D-miss 6.4–9.6 萬),Poisson 相對誤差 ~0.4%,所以下面 6–7% 的差遠在雜訊之外,是真的。

## 3. 結果:D-cache MPKI 降了 ~7%

**熱方法 `ProcessQueueInterp`(引擎本體,最乾淨)**:

| 指標 | 32B | 16B | 變化 |
|---|---|---|---|
| **D-cache MPKI** | **19.34** | **17.93** | **−7.3%** ✅ |
| I-cache MPKI | 0.276 | 0.330 | +19.6% |
| D : I 比 | 70.1 : 1 | 54.3 : 1 | — |

**全行程(含啟動/parse/JIT)**:

| 指標 | 32B | 16B | 變化 |
|---|---|---|---|
| **D-cache MPKI** | **24.69** | **23.19** | **−6.1%** ✅ |
| I-cache MPKI | 1.291 | 1.410 | +9.2% |

- **D-cache miss 確實降了**:把 `NodeInfos` 砍半(460→230 KB)讓 working set 更能留在 L2/L1d,
  熱迴圈每千指令少吃 ~7.3% 的 D-cache miss。**這就是「L-cache miss 有進步」的硬數字。**
- **I-cache 微升、但無關緊要**:16B 的 union pack/unpack(`GndPwr` nibble、Inline 分支)讓指令略多,
  I-cache MPKI 升了一點;但絕對量極小(熱方法 977→1177 樣本),且 D:I 仍 54–70:1 ——
  i-cache 根本不是瓶頸,用一點 i-cache 換掉更多 d-cache 是淨賺。

## 4. 重要:為什麼 D-miss 降 7%,吞吐量卻只 +0.75%

16B 當初實測吞吐量 **+0.75%**,但這裡 D-cache miss 降了 **~7%**。兩者不成比例,原因是:

> **這條引擎是記憶體「延遲」bound,不是 miss「數量」bound。**

- BFS 群解析是序列化的 **pointer-chase**(下一筆 load 要等前一筆結果),時間由**關鍵相依鏈上**那幾筆
  miss 的延遲決定,不是由 miss 總數決定。
- 16B 主要把 `NodeInfos` 從會溢出 L2 → 留在 L2,**省下的多半是「本來就能被預取/重疊掉、或只到 L2 的較便宜 miss」**;
  真正貴的 `NodeStates[gate]` 隨機 gather(在關鍵鏈上)沒被 16B 動到。
- 所以縮資料結構**對 miss 數量有效、對延遲 bound 的時間只小幅有效**。這也解釋了為何 prefetch、ushort、
  §C 非託管那些「減少存取/搬移資料」的招都打不動(它們減的不是關鍵鏈延遲)。

## 5. 重現

```
tools\profile_s1_ab.bat          (系統管理員;輸出 temp\perf\ab_32b.etl.zip / ab_16b.etl.zip)
etlanalyze temp\perf\ab_32b.etl.zip AprVisual
etlanalyze temp\perf\ab_16b.etl.zip AprVisual
# D-cache MPKI = DcacheMisses_samples / InstructionRetired_samples * 1000
```

- 32B build:`temp\ab\32b\`(由 worktree `temp\ab\src-32b` @ `fbf789f` 編出);16B build:`temp\ab\16b\`。
- 兩份捕獲的「`Could not find CLR directory … Giving up` / `Could not get PDB signature for …BingWallpaper…`」
  都是 PerfView 替**不相干模組**找符號失敗的良性訊息,不影響本結果。
- 量法限制:此 A/B 只抓 3 個 counter(無 `TotalCycles`),故只有 MPKI、無 per-cycle/IPC;要 IPC 需另抓
  (本機同時 PMC 槽有限,4+ 可能超出,故維持 3)。
