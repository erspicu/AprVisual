# 2026-05-25 試錯筆記:per-chip 分類 + 平行化嘗試

> Branch:`aot-codegen` HEAD `ac75cf0`
> 主題:今天嘗試在 S1 上做 per-chip 分析跟平行化,中途撞了幾次牆,把過程記下來避免重撞。

## TL;DR

| 嘗試 | 結果 | 為何失敗 |
|---|---|---|
| chip-diag v1 用 name-prefix 分類 node | 數據錯(53% "other") | 大多數 transistor 內部 node 沒有命名,被誤分類 |
| 從 v1 數據估 Strategy D 平行上限 | 估 1.3-1.4×(誤判 RoI 不值) | 上述數據錯 |
| chip-diag v2 用 InstanceRanges + Lower remap | 數據對(CPU 28% / PPU 71% / other 0.6%) | — |
| 從 v2 數據估 Strategy D 平行上限 | 估 1.99×(改認為值得做) | — |
| Rust per-chip parallel:std::thread::scope | 86× **慢** | spawn/join 每 iter 太貴 |
| Rust per-chip parallel:rayon::join | 15× 慢 | sync overhead 仍 >> per-iter work |
| Strategy E LUT idea | 估 1.006×(從 1.02× 下修)| 真實 "other" 工作量只 0.6% |

**核心 lesson**:per-wave parallelism 在 switch-level sim 因為 wave 太小被 sync overhead 壓爆,Amdahl 估計沒考慮 sync 常數。

---

## 1. chip-diag v1 ── name-prefix 分類錯誤

### 一開始的想法

「每個 node 都有 name。 看 name 的 prefix 就能分類:`cpu.*` → CPU,`ppu.*` → PPU,其他 → other」。

```csharp
if (nm.StartsWith("cpu.")) NodeChip[i] = CHIP_CPU;
else if (nm.StartsWith("ppu.")) NodeChip[i] = CHIP_PPU;
else NodeChip[i] = CHIP_OTHER;
```

### 跑出來的數據

```
CPU 1,777 (12%) | PPU 6,642 (45%) | other 6,311 (43%)
工作量分布:CPU 10.1% / PPU 37.1% / other 52.8%
"other" breakdown:(global)=5,852  port0=125  port1=125  cart=74
```

「other 比 PPU 還多!? 不合理」── 但因為 prefix 對得起來,我一度信了。 推到 user 那邊。

### user 點破 → 真實原因

只有「重要」node 在每個 module 的 `nodenames.txt` 裡有名字。 大多數 transistor 通道的中間 node **沒有名字**(只有編號 ID)── `GetNodeName(id)` 對沒命名的 node 回 id 字串(`"12345"`),沒有點 → 我的 classifier 歸到 `(global)`/other。

實際:cpu 的 2A03 整顆有 ~10,958 transistor 卻只命名 990 個 node。 ~6,000 個 cpu 內部 node 全被誤判 other。

### 正確做法

每個 module instance 在 `AddInstance` 時透過 `AllocNodes(maxNode+1)` reserve 一塊連續 ID 範圍。 該範圍內所有 ID 都屬於這個 instance,不管有沒有 name。

加 `WireCore.Module.InstanceRanges: List<(int Start, int End, string Prefix)>` 紀錄每個 instance 的範圍。 ClassifyChips 用這個 lookup。

### 還有第二個雷:Lowering 重編 ID

第一版 v2 仍錯,因為:
- `InstanceRanges` 紀錄的是 **pre-lowering** ID(`cpu` 範圍 `[13000..33001)`)
- Lowering 把 nodes merge + 重編 **post-lowering** dense ID(`[0..14730)`)
- 我的 classifier 用 pre-lowering 範圍對 post-lowering NodeCount → 大部分 ID 對不到

修法:`WireCore.Lower.cs` 暴露 `LastLowerRemap: int[]`(pre → post 映射),classifier 走「pre ID → owner via InstanceRanges → post ID via remap → tag NodeChip[postID]」。

### 修完數據

```
CPU 5,508 (37%)  | PPU 8,743 (60%)  | other 479 (3%)
工作量:CPU 28.3% | PPU 71.2%  | other 0.6%
跨晶片 walk:0.3%
```

跟 transistor 占比(PPU 60% / CPU 40%)對齊。

### Lesson

**Parse-time 已經知道的結構別用 runtime 字串 hack 重建**。 `AllocNodes` 已經知道每個 instance 的範圍 ── 直接記下來就好,不用靠 prefix 字串猜。 lowering 重編 ID 那層也得記,不然 pre/post ID 混用 silent 出錯。

---

## 2. Strategy D 平行化上限的兩次誤判

### 第一次(基於 v1 錯數據)

```
serial fraction = cross-chip 12.1%
parallel work = 87.9%
2-thread Amdahl = 1 / (0.121 + 0.879/2) = 1.79× 理論
扣 sync overhead 預估 1.2-1.4× 實際
RoI 結論:不值得做
```

### 第二次(基於 v2 修正數據)

```
serial fraction = 0.3% + other 0.6% = 0.9%
parallel work = 99.1%
2-thread Amdahl = 1 / (0.003 + max(0.283, 0.712)) = 1.40× 理論
扣 sync overhead 預估 ~1.7× 實際(我當時這樣樂觀預估)
RoI 結論:值得做 ✓
```

**這次我估錯了什麼**:把 Amdahl 用在 wave 內部 work / serial fraction,**沒考慮 sync 本身有 constant overhead**。

### 第三次(實測後)

```
serial baseline:       46,927 hc/s
threaded (rayon):       3,000 hc/s   ← 15× 慢
threaded (std::thread): 537 hc/s     ← 86× 慢
```

每 master half-cycle:
- ~5-10 个 BFS wave
- 每 wave 平均 ~120 dirty node,每個 node walk ~30ns
- → 每 wave useful work ≈ **4 µs**
- 每 wave 一次 sync barrier(rayon::join ~10-30 µs,std::thread::spawn ~100 µs)
- 每 hc sync overhead = 5-10 × 30µs = **150-300 µs**
- 跟 useful per-hc work(~20-60µs)比,sync 是 work 的 5-10×

**真實 Amdahl 模型應該是:**

```
T_parallel(N threads) = T_serial / N + N_waves × sync_overhead
                      = 60µs / 2 + 7 × 25µs = 30µs + 175µs = 205µs
T_serial = 60µs
Speedup = 60 / 205 = 0.29×   ← 慢 3.4×
```

跟實測 15× 慢相比仍差幾倍,可能是 partition + bucket 寫入也有 overhead。 但**方向對了**:per-wave parallel 在 wave 太小的 workload 必然輸給 sync overhead。

### Lesson

**Amdahl law 不夠 ── 還要看 sync constant overhead vs work 比例**。 公式應該是:

```
Speedup ≤ T_serial / (T_serial × s + T_serial × (1-s) / N + sync_overhead × wave_count)
```

其中 `wave_count × sync_overhead` 在 fine-grained barrier 模型下是 dominant 項。

**啟示**:per-wave parallel 在 switch-level sim 不可行,因為 wave 設計就是「小頻繁同步」── 跟 thread sync overhead 完全相剋。 要 parallel speedup 得用 coarse-grained 切法(per-batch / per-frame / per-chip-independent-stepping),那是另一個算法 paradigm。

---

## 3. Strategy E LUT idea ── 從 1.02× 下修到 1.006×

### user 提的想法

「TTL 晶片(74LS139 解碼器、74LS368 反相器、74HC04 反相器)是純組合邏輯,可以整顆換成 LUT,$O(1)$ 取代 BFS walk」。

理論上正確 ── 而且我先前在 `MD/summary/s1-performance.md` 也是這個方向(Strategy E)。

### 真實工作量分布(v2 chip-diag)

```
CPU 28.3%  PPU 71.2%  其他 0.6%
"其他" breakdown(479 個 node 全部):
  cart.edge=69  u2=60  cart.eram=42  cart.prg=36  cart.chr=34
  u1=32  u4=32  cart.u3=28  u3=28  u7=22  u8=22 ...
```

**TTL + cart + bus + controller 加起來只佔 0.6% 工作量**。 全 LUT 化最多省 0.6% → **1.006× 加速**,完全可忽略。

### 那 LUT 還有何處可施

user 也提到「CPU 內部 PLA 指令解碼」、「PPU palette decoder」── 這些是 transistor 級的純組合邏輯巨型 block。 LUT 化潛在效益大。 但:

- **CPU PLA 不是獨立 module**,跟 register file / ALU 混在 2A03 整顆 netlist 裡。 要分離得做 transistor topology 分析(BFS 找 acyclic 子圖、追 input/output 邊界)
- **PPU palette decoder 已經 handler-based**(`AttachVideoHandler` 直接 `NesPalette[colour6]`),不再是 transistor sim

所以 LUT idea 對 **board-level TTL** 無效(< 1%),對 **chip 內部 PLA** 需要 IR-extraction 工作量(等於 S4 的子集)。

### Lesson

「TTL 整顆 LUT 化」直覺很對,但實際 NES 的 TTL 加起來太少。 真有 RoI 的 LUT 目標在 chip 內(PLA、decoder block),那要 topology 分析才能找到 ── 不是看 .js 模組目錄列哪些 TTL 就抓到的。

**先量數據再估收益**。 v1 數據我會以為 TTL 占了 53% other 工作 → LUT 看起來有 ~10-20% 提升空間;v2 數據說只 0.6% → LUT 提升空間 1%。 差兩個量級。

---

## 4. 共同 meta-lesson

1. **Parse-time 知道的事,別用 runtime 字串硬猜**(chip-diag v1 的錯)
2. **資料層的 ID 重編,所有 metadata 都要跟著 remap**(chip-diag v2 第一版的錯)
3. **Amdahl law 不含 sync overhead;細粒度同步系統要算 `wave_count × sync_overhead`**
4. **效益估計先看真實 work 分布,再選策略;不要因為「概念對」就推進**(LUT idea 的錯)

---

## 5. 反映到 code 跟 doc

### code(已 commit `ac75cf0`)

- `src/AprVisual/Sim/WireCore.ChipDiag.cs` ── v2 classifier(InstanceRanges + remap),含 "other" prefix breakdown
- `src/AprVisual/Sim/WireCore.Module.cs` ── `InstanceRanges: List<(start, end, prefix)>` 在 AddInstance 時 record
- `src/AprVisual/Sim/WireCore.Lower.cs` ── `LastLowerRemap: int[]` 暴露
- `experiment/rust-poc/src/parallel.rs`(新)── 平行 settle 嘗試(correctness ok,速度負)
- `experiment/rust-poc/src/wire.rs` ── `process_queue_threaded` + chip-aware walk
- snapshot v3 加 chip_id field

平行那段 code **保留**(不刪),作為 negative result 的 reference。 後來想做 multi-wave-per-sync 的同學可以基於它改。

### doc

- 這個檔(`MD/note/2026-05-25-parallel-and-chip-diag-wrong-turns.md`)── 試錯筆記
- `MD/summary/s1-performance.md` 的 §5 / §「per-chip strategies」估計數字需要更正:
  - Strategy D 上限不是 1.3-1.4×,**實測 0.06×(慢)**
  - Strategy E TTL LUT 不是 1.02×,**真實 1.006×**

### MEMORY.md 應該記一條

> 2026-05-25:per-chip parallel(Strategy D)用 rayon::join per-wave 嘗試了,結果 15× 慢 ── wave 太小被 sync overhead 壓爆,不是實作問題是 paradigm 不合。 future 嘗試「multi-wave-per-sync」或「chip-independent stepping」前先讀 `MD/note/2026-05-25-parallel-and-chip-diag-wrong-turns.md` §2,別重撞。

---

## 6. 仍可探的方向

雖然 D / E 上限低,還沒試過的 small wins:

| 策略 | 預期效益 | 工程成本 |
|---|---|---|
| F:snapshot cache(C# 加 `--load-snapshot`)| 短 bench -28%,batch -13% | 半天 |
| Strategy A:per-chip `--prune-merge`(CPU only)| ~1.04× | 1 天 |
| 把 IR / codegen path 從 Rust port 補完 + 再 bench | unknown(可能 1.05-1.10×)| 多天 |

不在 paradigm 突破範圍,但 ROI 合理的小確幸組合可以再榨 ~10-15%。
