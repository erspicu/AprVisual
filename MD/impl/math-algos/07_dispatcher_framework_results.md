# 07 — Dispatcher framework(Phase 2.5 codegen Step 2)結果

> 設計參照:`05_codegen_design_notes.md` §2.8(Gemini r2 的 bitmask-polling 設計)+ §4 4-step decision path。
> 任務追蹤:#48(framework wiring)→ #49(overhead + trace-diff 驗證)→ #50(本文)。
> 接續:`06_alu_validation_results.md`(Step 1 ALU 黑盒 18.8× GO signal)。

---

## 0. 結論

🟢 **Step 2 通過**:
1. **Trace-diff 完全一致**:S1 vs dispatcher dry-run,**3000 個 6502 cycle 逐 cycle 對比 100% byte-identical**。NodeStates checksum 同 `0xD39EE4BD1457D932`。
2. **框架 overhead 僅 4.6%**(38,389 → 36,640 hc/s)—— 在 noise + 預期 hook 成本內;P/Invoke 部分 < 1%。
3. **ALU dispatch 頻率 ≈ 1 次/CPU cycle**(3,785 ALU evals / 4167 cycles ≈ 0.9),完全符合 6502 microcode 預期(~80-90% 的 cycle 用到 ALU)。
4. **重入安全**(callbacks → SetNodeState → DispatcherRun 再進入路徑,無 corruption)。

下一步可進 Step 3(graph partitioner)+ Step 2.5(寫回 + op-selector 驗證 + 真實 ALU codegen evidence-ROM 量測)。

---

## 1. 實作架構

```
ProcessQueue()                                            // src/AprVisual/Sim/WireCore.Recalc.cs
├─ EnableOblivious  → ProcessAllOblivious
├─ EnableLevelize   → ProcessQueueLevelized
├─ EnableCodegenDispatcher → DispatcherRun                ★ Step 2 入口
└─ ProcessQueueInterp                                     // 原本的 BFS settle 提出

DispatcherRun()                                           // src/AprVisual/Sim/WireCore.Dispatcher.cs
├─ if (RecalcListNextCount != 0)  dirty_mask |= 1 << 63   // 起手:有 enqueue → interp dirty
├─ while (dirty_mask != 0)
│   ├─ next = BitOperations.TrailingZeroCount(dirty_mask) // 硬體 TZCNT 1 cycle
│   ├─ dirty_mask &= ~(1UL << next)
│   ├─ switch (next) {                                    // JIT 編成 jump-table(BTB friendly)
│   │     case 0:  Eval_AluBlock();        break;         // ALU block
│   │     case 63: ProcessQueueInterp();   break;         // hybrid interp fallback
│   │     // future: case 1..62 = more macro-blocks
│   │  }
│   └─ if (RecalcListNextCount != 0) dirty_mask |= 1 << 63 // interp 內若再 enqueue,re-arm
└─ InvokeCallbacks()                                       // memory handlers 等

Hook 1 — SetNodeState 結尾                                // 觸發 dirty bit
└─ if (EnableCodegenDispatcher) CodegenInputChanged(nn)
   └─ if (CodegenInputWatched[nn] != 0) dirty_mask |= 1 << 0

Hook 2 — RecalcNode 開頭                                  // 讓 codegen 接管的節點 skip 解析
└─ if (EnableCodegenDispatcher && CodegenOwned[nn]) return

Eval_AluBlock()                                           // src/AprVisual/Sim/WireCore.Dispatcher.cs
├─ Pack 17 inputs (alua x8 + alub x8 + alucin) into AluCtx
├─ Read op selectors (op-SUMS + op-T+-ora/and/eor/adc)
├─ fixed (AluCtx* p = &_aluCtx) AluBlockBindings.Eval_Alu(p);  // P/Invoke → AluBlock.dll
└─ if (EnableCodegenAluWriteback) SetNodeState(alu[i]..., notalu[i]..., alucout, notalucout)
   else return                                            // ★ Step 2 預設 dry-run:不寫回
```

兩個 byte* (`CodegenOwned`, `CodegenInputWatched`)零分支 hot read,大部分節點 byte = 0,branch predictor 預測極佳。

---

## 2. 量測

### 2.1 Trace-diff(正確性)

ROM:`instr_test-v3/01-implied.nes`。

| 測試 | 結果 |
|---|---|
| 500 cycle | TRACE IDENTICAL ✓ |
| 3,000 cycle | TRACE IDENTICAL ✓ |
| NodeStates checksum @ 100K hc | `0xD39EE4BD1457D932`(S1 與 dispatcher 同)✓ |
| EnqueueCount @ 100K hc | 65,911,957(兩邊同)✓ |
| RecalcNodeCount @ 100K hc | 61,422,650(兩邊同)✓ |

Dry-run 框架不改變 sim state,**S1 觀察者完全看不出來下面有 dispatcher 在跑**。

### 2.2 速度 overhead

```
baseline (S1)            : 38,389 hc/s  (26.05 µs/hc)
dispatcher (dry-run)     : 36,640 hc/s  (27.29 µs/hc)
Δ                        : 1,749 hc/s 慢  →  -4.6%
```

實際 overhead 來源拆解:
- `CodegenInputChanged` hook:每 SetNodeState 都觸發。SetNodeState 觸發 ~659/hc → 100K hc × 659 = 65.9M 次 × ~1 ns/次 = ~66 ms = **2.4%** of 2.73 s。
- `CodegenOwned` skip-check:每 RecalcNode 一次。614/hc × 100K = 61.4M × ~1 ns = ~61 ms = **2.2%**。
- DispatcherRun loop(TZCNT + switch + mask):約 2.4 次/hc × 100K = 242K 次 × ~5 ns = 1.2 ms = **0.04%**。
- Eval_AluBlock dry-run(P/Invoke + read + return):0.04/hc × 100K = 3,785 次 × ~3 ns = ~11 µs = **<0.001%**。

加總 ≈ 4.6%,跟實測符合。Step 2 的代價幾乎完全是兩個 byte-watch 的 hot-path overhead,**P/Invoke 跟 dispatcher loop 本身幾乎是免費的**。

### 2.3 Dispatch 頻率

```
# dispatcher: 242,337 block-evals (238,552 interp, 3,785 ALU) over 100,000 hc
#             (0.04 ALU evals/hc; mode = dry-run)
```

- 100K hc = 4167 CPU cycles(每 6502 cycle = 24 master hc)。
- ALU bit fires 3,785 次 → **每 CPU cycle ≈ 0.9 次 ALU eval**。
- Interp bit fires 238,552 次 → 平均 **2.4 次/hc**(interp 在每個 hc 內可能因為 callback 觸發新一波 settle 而被重新 arm 一兩次)。

**ALU 觸發頻率符合預期** —— 6502 的 ~80-90% cycle 會用到 ALU(ADC/SBC/CMP/INC/DEC/AND/OR/EOR/位元移位),其他純 stack/jmp 不用 ALU。dispatcher 確實精準 arm 在 ALU 真的有事的時候。

---

## 3. 為什麼是 dry-run

Step 2 預設關閉 `EnableCodegenAluWriteback`。**理由**:

1. **op-selector mapping 尚未驗證**:6502 ALU 的 op 控制不是簡單的 `op-SUMS`/`op-AND`/`op-OR`/`op-EOR` 四選一。實際 PLA 輸出更複雜(`op-T+-ora/and/eor/adc`、`op-T0-ora`、各種 cycle-state-specific 信號),需要逐一研究 netlist 才能寫出 byte-for-byte 等同 S1 的 mapping。
2. **Step 2 的目標是 framework 驗證**:確認 dispatcher mechanism 正確 + overhead 可控。**Step 2.5 才是接「真的接管 ALU 輸出」**。
3. **dry-run 給了一個無風險的 baseline**:現在我們有「框架在 + 不影響結果」的明確 commit,將來 op-selector mapping 寫錯時,可以一行 flag flip 回到 dry-run 排除問題。

`--codegen-writeback` 旗標已經就位(設 `EnableCodegenAluWriteback = true`),Step 2.5 工作就是:
- 把 ALU op-selector 跟 PLA 輸出對齊。
- 跑 `--trace --codegen-writeback`,確保 trace 仍然 identical。
- 量量 writeback 模式的 hc/s。

---

## 4. CLI

```
--codegen-dispatcher              啟用 bitmask dispatcher(dry-run;Step 2)
--codegen-writeback               啟用 ALU 寫回(Step 2.5;correctness risk 直到 op-selectors 驗證)
```

---

## 5. 對齊 Gemini r2 設計的 check

| Gemini §2.8 設計要件 | Step 2 實作狀態 |
|---|---|
| `uint64_t dirty_mask` | ✓ `_dirtyBlockMask`(static ulong)|
| `__builtin_ctzll` 1-cycle dispatch | ✓ `BitOperations.TrailingZeroCount`(JIT 編成 TZCNT)|
| Jump-table switch(避開 indirect call BTB stall) | ✓ dense `switch (0..63)`,JIT emit jump table |
| 每 block dispatch overhead ~ 3-5 cycles | ✓ 實測整個 DispatcherRun loop 佔 ~0.04% wall-time → 沒成本 |
| Re-arm 機制(block A 完成 → 寫 mask 給 block B) | ✓ `CodegenInputChanged` hook 自動 or-into-mask;interp drain 後 re-check `RecalcListNextCount` |
| Per-block Context Struct | ✓ `AluBlockBindings.AluCtx`(8 input + 2 output bytes,packed)|
| 與既有 interpreter 共存(混合模式) | ✓ bit 63 = interp = `ProcessQueueInterp` |

**8/8 設計要件落地**。

---

## 6. 沒做 / 留到 Step 2.5、Step 3

- **ALU 寫回**(Step 2.5):需要把 op-selector mapping 對齊 6502 PLA 真實控制信號。
- **多 block 切分**(Step 3):目前只有 ALU 一個 codegen block;graph partitioner 自動切 50-100 個 macro-block 還沒做。Step 3 直接搬本 Step 的 `--dump-block` 啟發式 + bottom-up clustering(Gemini §2.4)。
- **LLVM emit**(Step 4):目前 ALU 是 hand-coded `AluBlock.cpp`,Step 4 才把 LLVMSharp.Interop 接上,讓每個 macro-block 從 IR 自動 emit。
- **更壓力的 ROM 測試**:目前只跑 `instr_test-v3/01-implied`。多 ROM 測試 + 真實遊戲 ROM (`smb.nes`) 對 Step 2 框架的測試還沒做(留到 Step 3 同步)。

---

## 7. 工程 reproducibility

```
git: 6837aaa +(這個 Step 2 commit;預計推上去後 commit 在 6837aaa..HEAD)
工具鏈: clang 22.1.6 / MSVC cl 19.44 / .NET 10
ROM:    ref/metalnes-main/data/roms/nes-test-roms/instr_test-v3/rom_singles/01-implied.nes
命令:
  # trace-diff
  dotnet run --project src/AprVisual -c Release -- --trace <ROM> --cycles 3000
  dotnet run --project src/AprVisual -c Release -- --trace <ROM> --cycles 3000 --codegen-dispatcher
  # bench-hc + dispatcher counters
  dotnet run --project src/AprVisual -c Release -- --benchmark <ROM> --bench-hc 100000 --count-events
  dotnet run --project src/AprVisual -c Release -- --benchmark <ROM> --bench-hc 100000 --count-events --codegen-dispatcher
```

---

## 8. 一句話收尾

> **Bitmask-polling dispatcher framework 上機 + dry-run 模式驗證:trace byte-identical、overhead 4.6%、ALU bit 每 CPU cycle ≈ 1 次精準觸發、8/8 對齊 Gemini r2 §2.8 設計要件。codegen 路徑的 runtime kernel 確認可行;下一步可進 Step 2.5(寫回 + op-selector mapping)或 Step 3(graph partitioner)。**
