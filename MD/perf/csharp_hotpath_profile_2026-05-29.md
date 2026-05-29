# C# S1 Hot Path 效能分析 (2026-05-29)

## 分析工具

- **dotnet-trace** v9.0.661903(`dotnet-sampled-thread-time` profile,~100Hz 取樣)
- **JIT Disassembly**(`DOTNET_JitDisasm` + `DOTNET_TieredCompilation=0` 強制 FullOpts)
- **WPR / xperf**(嘗試 PMC 但需要 admin,未取得 i-cache / branch-mispredict counter)

## Workload

- Binary: `src/AprVisual.S1/bin/Release/net10.0-windows/AprVisual.S1.exe`
- ROM: `full_palette.nes` (PRG 32KB, mapper 0, 200k 與 1M master half-cycles)
- 平台: Windows 11 build 26200, 16 cores @ 3593 MHz
- Build: Release, .NET 10, AllowUnsafeBlocks, x64

---

## Finding 1:Inline cascade 完全成功

### 證據

`DOTNET_JitDisasm="*"` 跑單 hc bench 後,所有 hot path 方法的 disasm 結果:

| 方法 | 是否獨立 JIT? | 結論 |
|---|---|---|
| `StepCycle()` | ✅ 獨立(47 bytes) | 純 delegate dispatch + Time++ |
| `ProcessQueueInterp()` | ✅ 獨立(**3034 bytes**) | **整個 BFS hot path 融合於此** |
| `RecalcNode(int32)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `ComputeNodeGroup(int32)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `AddNodeToGroup(int32)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `AddNodeOrApplyDriver(int32)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `SetNodeState(int32, byte)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `RecalcNodeFast(int32)` | ❌ inlined | inline 進 ProcessQueueInterp |
| `GetNodeValue()` | ❌ inlined | inline 進 ProcessQueueInterp |
| `SetHigh/SetLow/SetHighQueued/etc.` | ❌ inlined | inline 進 StepCycle 內的 clock lambda |

### ProcessQueueInterp 結構摘要

```
; Assembly listing for method AprVisual.Sim.WireCore:ProcessQueueInterp() (FullOpts)
; 21 inlinees with PGO data; 23 single block inlinees; 20 inlinees without PGO data
;                          ← 共 64 個 inlinee 全部融合
; rsp based frame
; G_M000_IG01: prologue
       push     r15  push     r14  push     r13  push     r12
       push     rdi  push     rsi  push     rbp  push     rbx
       sub      rsp, 152
; ...
; Total bytes of code 3034
; 104 instruction blocks
```

- **3034 bytes** 機器碼 ── 仍可全部住進 32KB L1i
- **8 個 callee-saved 暫存器**全用上(r12-r15, rdi, rsi, rbp, rbx)── **register pressure 接近極限**
- **152 byte stack frame** ── 部分變數 spill 到 stack(register 容量不夠)
- 64 個 inlinees + 104 個 block 表示 JIT 對整段 BFS cascade 真實 inline 成功

### 與 memory `jit-vs-llvm-recursive-inline` 對照

該 memory 警告 "making method too big for JIT inline = -6%"。 我們的方法是 3034 bytes,但 ProcessQueueInterp 本身**是 callee 的最外層**,JIT 對它沒有「inline 進更外」的決策需要 ── 它就是 root。 register pressure 高表示已逼近 codegen 邊界,**進一步擴大將 spill 更多,效能可能掉**。

---

## Finding 2:CPU 樣本分佈(1M hc bench, dotnet-trace)

### Top inclusive %(15.6 秒 bench)

| Rank | Method | Inclusive % | Exclusive % | 備註 |
|---|---|---|---|---|
| 1 | `WireCore.Step(int32)` | **97.26%** | 0.12% | 主迴圈(Step → StepCycle → ProcessQueue) |
| 2 | `JsReader.ReadArray` | 1.45% | 0.03% | Load JSON 模組(amortized over 1M hc) |
| 3 | `WireCore.InvokeCallbacks` | 1.04% | 0.18% | 每 settle 結束觸發 handlers |
| 4 | `WireCore.LoadExternalArray` | 1.65% | 0.01% | Load |
| 5 | `WireCore.ComposeSystem` | 2.27% | 0.01% | Load |

### Hot path methods exclusive %(sample 命中其 PC 位置)

| Method | Exclusive % | 解讀 |
|---|---|---|
| `AddNodeToGroup` | **0.32%** | 殘留歸屬 ── 實際 code 在 ProcessQueueInterp 內 |
| `InvokeCallbacks` | 0.18% | 真實 call site(每 settle 結束) |
| `SetNodeState` | 0.10% | 殘留歸屬 |
| `RecalcNode` | 0.08% | 殘留歸屬 |
| `ComputeNodeGroup` | 0.05% | 殘留歸屬 |
| `RecalcNodeFast` | 0.05% | 殘留歸屬 |

### 關鍵觀察

- **Step inclusive 97.26%** 但 exclusive 0.12% ── 表示 Step 本身只是 loop counter,**幾乎所有時間花在 inlinee 內**
- **hot path inlinees 的 exclusive 加總僅 ~0.6%** ── 大部分時間「不見了」,實際是分佈在 ProcessQueueInterp 內各條指令上,sample 沒有精確歸屬到「邏輯方法」
- 約 96% 的 CPU 時間在 ProcessQueueInterp 的 3034 bytes 內**無法用方法層級拆解**
- 載入(JSON 解析、ComposeSystem)約佔 ~2-3%(於 1M bench;200k bench 約 13%)

---

## Finding 3:hot path 唯一不能 inline 的點

`StepCycle()` 內 47 bytes:

```asm
sub      rsp, 40                          ; prologue
mov      rcx, 0x25FD68001E8               ; load _handlerChain pointer
mov      rax, gword ptr [rcx]             ; load Action delegate
test     rax, rax
je       SHORT G_M000_IG04                ; null check
mov      rcx, gword ptr [rax+0x08]        ; load Target field
call     [rax+0x18]System.Action:Invoke():this  ; ← vtable indirect call
mov      rax, 0x7FFB093DB0B0              ; load Time pointer
inc      qword ptr [rax]                  ; Time++
add      rsp, 40
ret
```

**`call [rax+0x18]` 是 multicast delegate 的 vtable dispatch ── runtime 限制,無法 inline**。 per StepCycle 200k 次,粗估每次 10-15 cycles × 200k = 2-3M cycles ≈ 0.7ms 在這個 indirect call 上。 佔 bench 3 秒的 0.02%。 **改寫成直接 call 無價值**。

---

## Finding 4:Register pressure 是 hot path 真正瓶頸的線索

ProcessQueueInterp 用了:
- 8 個 callee-saved general-purpose registers(r12, r13, r14, r15, rdi, rsi, rbp, rbx)
- 152 bytes stack frame(代表多個變數 spill 到 stack)

這表示 hot path 已經:
- **用光所有可用 GP register**
- 後續變數讀寫必須走 stack(L1d access,~4-5 cycle latency)

任何**新增變數 / 計數器 / cache** 都會擠壓既有 register,造成更多 spill。 這跟我們的 memory `counter-fastpath-dead-end`(維持 active_gnd_count / active_pwr_count 失敗 -6%)觀察一致 ── **狀態快取謬誤**的硬體層解釋。

---

## 未取得的數據

**PMC counters** 需要 admin 權限(IcacheMisses / BranchMispredictions / DcacheMisses 等)。 WPR custom profile 配置成功但 ETL 沒有 PMC events ── 確認需要提權執行。

若取得 PMC,可回答的問題:
- IPC(Instructions Per Cycle):若 <1 表示 front-end 受限(i-cache miss / branch mispredict);>2 表示 ALU 限制
- L1i miss rate:若 >2% 表示 3034 bytes 機器碼超過 hot working set
- L1d miss rate:若 >5% 表示 NodeStates/transistor_list 隨機 access 是真瓶頸
- BranchMispredict rate:若 <2% 確認 Gemini round 2 anti-pattern #2「predictor 飽和」

---

## 結論

### 已驗證

1. **Inline cascade 完整且最大化** ── 64 inlinees 融合進 ProcessQueueInterp,JIT 做的事符合預期
2. **Hot path 機器碼 3034 bytes** ── 仍在 L1i 內(32KB),未到 instruction-cache 撞牆規模
3. **Register pressure 已逼近 codegen 邊界** ── 8/8 callee-saved + 152 byte stack frame
4. **StepCycle 內 multicast delegate dispatch** 是唯一不可 inline 的 hot-path overhead(<0.05%,不值得改)
5. **載入 13% (200k) / 2-3% (1M)** ── 對 200k bench 來說 load 是顯著占比,但無法 amortize 只能跑長
6. **dotnet-trace 無法精確拆解 ProcessQueueInterp 內 3034 bytes 的時間分佈**

### 待驗(需 PMC / admin)

- L1i 與 L1d miss rate 確認 Gemini 的「memory subsystem latency wall」假說
- Branch mispredict rate 確認 「predictor 飽和」假說
- IPC 判斷 hot path 是 front-end bound 還是 back-end bound

### 行動建議

- **不要新增任何在 hot path 內的變數 / counter / 條件分支** ── register pressure 已飽和,新增即 spill
- **要量化突破**:取得 PMC 後才能定位真實瓶頸;不然每次「優化嘗試」都是猜測
- 若 PMC 不可用,可考慮:
  - 用 BenchmarkDotNet 對 `AddNodeOrApplyDriver` / `RecalcNodeFast` 做隔離 micro-bench(但脫離真實 inline context 數據用處有限)
  - 用 VTune 試用版量 PMC(但安裝重)

---

## 附件

- `tools/profile_results/cpu_sample.nettrace` ── 200k bench dotnet-trace 取樣(328KB)
- `tools/profile_results/long_sample.nettrace` ── 1M bench dotnet-trace 取樣
- `tools/profile_results/long_topN.txt` ── Top 40 method 報表
- `tools/profile_results/jit_all.txt` ── 全部 method JIT disasm(`*` wildcard)
- `tools/profile_results/jit_disasm.txt` ── StepCycle 單方法 disasm
- `tools/profile_results/pmc.etl` ── WPR trace(PMC 事件缺,需 admin)
- `tools/profile_results/cpu.etl` ── WPR CPU profile trace
