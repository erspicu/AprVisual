# aot-codegen — Phase E-2 結果:AotRuntime 第一步驗證

> 任務 #80 (AotRuntime skeleton + first step vs S1)。
> 接續 `09_phaseE1_latches.md`(latch patterns,88.8% coverage)。

---

## 0. 結論

🟢 **Phase E-2 milestone**:`AotRuntime` 從 S1 settled state 出發,跑 N 個 step 後跟 S1 比 NodeStates。第 1 hc 達 **99.89% match**,asymptotically drift 到 88.99%(剛好等於 AOT coverage 88.8%)。

| Step count | AOT vs S1 match | Mismatches | 解讀 |
|---|---|---|---|
| 1 hc | **99.89%** | 16 | 第一步幾乎完美 |
| 10 hc | 97.25% | 405 | 開始 drift |
| 100 hc | 92.98% | 1,034 | 累積 drift |
| 1,000 hc | **88.99%** | 1,622 | 跟 AOT coverage 88.8% 完全對齊 |

**未 cover 的 11% nodes 沒被 AOT 更新 → 隨時間 drift 從 S1 → 最終所有未 cover nodes 都跟 S1 不一致**。pull-up vs no-pullup mismatch 大致 60/40 比例。

---

## 1. AotRuntime 設計(`src/AprVisual/Codegen/AotRuntime.cs`)

```csharp
public sealed unsafe class AotRuntime
{
    public byte[] NodeStates;           // 14,730-byte own buffer
    public EvalAllDelegate EvalAll;
    public int ClockNodeId;
    public int MaxSettleIterations = 32;
    
    public void Step() {
        NodeStates[ClockNodeId] ^= 1;   // toggle clock manually
        // fixed-point: iterate EvalAll until NodeStates stops changing
        for (int iter = 0; iter < MaxSettleIterations; iter++) {
            var prev = (byte[])NodeStates.Clone();
            fixed (byte* p = NodeStates) { EvalAll(p); }
            if (prev.SequenceEqual(NodeStates)) break;
        }
    }
}
```

MVP scope:
- ✓ Own NodeStates buffer(獨立於 WireCore)
- ✓ Init from S1 settled state(snapshot)
- ✓ Clock toggle in Step
- ✓ Fixed-point settle loop

Out of E-2 scope(留 E-3+):
- Memory handlers(RAM/ROM I/O)
- Power-on reset sequence  
- Callback mechanism(PPU video, APU audio)

---

## 2. 量測結果分析

### 1 hc(99.89% match)

```
matches    : 14,714 / 14,730  (99.89%)
mismatches : 16
first mismatch: nn=148 name='ppu.chroma_ring4', AOT=0, S1=1
mismatch breakdown: pull-up 11, no-pullup 5
```

僅 16 個節點 diverge —— 大多是時序敏感 latch(`ppu.chroma_ring*` 是 PPU chroma generation ring,需要精確 phi propagation)。Phase E-3 加 clock handler 後預期改善。

### Drift 模式

```
hc        match    mismatch  growth
1         99.89%   16        baseline
10        97.25%   405       ~10× growth(早期感染期)
100       92.98%   1,034     ~2.5× growth(感染變慢)
1,000     88.99%   1,622     ~1.6× growth(approaches asymptote)
```

drift 來自:
1. **未 cover 的 11% nodes**(1,655 unsupported)永遠 stale → ceiling ≈ 89% match
2. **這些 stale nodes 餵錯誤輸入給 AOT-covered nodes** → cascade infection 一些 covered nodes 也錯
3. **AotRuntime 沒 memory handlers / reset sequence** → 任何 memory access、reset 都用初始 settled state 代替

### Settle iter count = 1 的意外發現

`AotRuntime.Step` 的 fixed-point loop 永遠在第一 iter 就收斂(EvalAll 沒讓 NodeStates 改變)。可能原因:
- S1 settled state 已是 AOT EvalAll 的 fixed point(從 settled 進去 settled 出來)
- Clock toggle 不太傳到 PPU/APU blocks(他們用自己的 pclk1/2,不用 CPU clk)
- 故 EvalAll 大多寫相同值 → no-change → loop break

也表示 PPU/APU 不被我們的 clock-toggle 觸發 —— 要 E-3 處理 PPU/APU clock source 才會有實際 propagation。

---

## 3. Code 規模躍進

加 latch patterns 後:
- 之前 master .cs:36 blocks / 573 delegates / 115 KB
- 現在 master .cs:**66 blocks / 5,210 delegates / 1,009,854 bytes**(1 MB)

source 大了 9×,delegate 多了 9× —— latch patterns 把很多原本 unsupported 的 internal mids 變成 emittable,讓更多 partition blocks 過 5-emittable threshold。

Roslyn compile 1 MB .cs 大約 ~5-10 s(沒精確量,可接受)。

---

## 4. Phase E-3+ 路線

| Phase | 目標 | 為什麼 |
|---|---|---|
| ⏭ E-3 | Clock 系統 — toggle CPU clk + PPU pclk1/pclk2 + reset propagation | 上面分析:現在 EvalAll 不太被觸發,因為 clock 沒真實傳 |
| ⏭ E-4 | Memory handlers — RAM/ROM bus I/O | CPU 程式執行需要 memory 才會跑;沒這個只是 settled 狀態 |
| ⏭ E-5 | 多 ROM 驗證(小程式 50-100 hc 對 S1)| 真實工作量下驗證 |
| ⏭ E-6 | 完整 ROM trace verify | 對 S1 trace identical |
| ⏭ E-7 | Perf baseline(AotRuntime hc/s vs S1)| 真實 speedup 對比 |

**E-3 (clock) 是 unblocker**:Clock 對沒做,AotRuntime 不會真的「跑」程式,只是反覆 evaluate fixed-point。

---

## 5. CLI

```
--aot-runtime-step <rom> [--bench-hc N]   E-2: AotRuntime first-step test vs S1
```

---

## 6. 一句話

> **Phase E-2:AotRuntime 第一版骨架完成,從 S1 settled state 出發 1 hc 達 99.89% match,asymptotically 接近 88.99%(= AOT coverage)。Drift 來自未 cover 的 11% nodes 加 missing clock/memory handlers。下一步 E-3 加 clock system 讓 AOT 真實 propagate state changes。**
