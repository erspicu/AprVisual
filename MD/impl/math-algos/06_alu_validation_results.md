# ALU 黑盒驗收結果 —— LLVM codegen 路徑 GO/NO-GO

> 設計與計畫:`05_codegen_design_notes.md` §4 4-step decision path + §5 ALU 黑盒實驗 + §5.6 決策表。
> 任務追蹤:#44(`--dump-block` ✅)→ #45(`AluBlock.cpp` ✅)→ #46(`--alu-bench` ✅)→ #47(本文)。

---

## 0. 結論 —— 🟢 **GO**

| 指標 | 數字 | Gemini 門檻 |
|---|---|---|
| **Native ALU bulk(`Eval_AluN`)** | **2.09 ns / call**(478 M ops/sec)| > 3× → ✅ **18.8×** |
| Native ALU per-call(`Eval_Alu`)| 4.38 ns / call(228 M ops/sec)| > 3× → ✅ **8.9×** |
| P/Invoke crossing overhead | ~2.3 ns / call | (差距 8.9× vs 18.8× 的來源)|
| 正確性 | 2000 / 2000 spot check(覆蓋 ADD/AND/OR/EOR + 隨機 vectors)| 100% ✅ |
| S1 baseline reference | ~39.2 ns / recalc(D ≈ 610,~41.8K hc/s 那批量測)| — |

**Codegen 路徑確定值得做。LLVM 確實能把 ALU 編到 Gemini 預測的「~register-only / branchless」形狀。**

---

## 1. 工程過程(從 `--dump-block` 到 `--alu-bench`)

### 1.1 `--dump-block`(#44)的發現

對 ALU outputs 反向 closure,加「stop at other pull-ups」啟發式後:
- 內部節點 = **133** + 涉及 **477 transistor**(符合 Gemini「100-200 gate」估計)。
- 暴露出 6502 ALU 的 carry-save 結構:`#A.B[7:0]`、`#(A+B)[7:0]`、`#(AxB)[7:0]`、`#(AxBxC)[7:0]` 四組 8-bit 中間 latch。

### 1.2 `AluBlock.cpp`(#45)模擬「macro-block IR codegen 期望輸出」

```cpp
struct AluCtx {
    uint8_t alua, alub, cin, op_sums, op_and, op_or, op_eor, _pad;
    uint8_t alu, cout;
};
extern "C" __declspec(dllexport)
void __cdecl Eval_Alu(AluCtx* c) {
    uint32_t sum = (uint32_t)c->alua + c->alub + c->cin;
    uint8_t r = 0;
    if (c->op_sums) r = (uint8_t)sum;
    if (c->op_and)  r = c->alua & c->alub;
    if (c->op_or)   r = c->alua | c->alub;
    if (c->op_eor)  r = c->alua ^ c->alub;
    c->alu = r;
    c->cout = c->op_sums ? (uint8_t)((sum >> 8) & 1) : 0;
}
```

`clang -O3 -shared` 編成 104 KB DLL。

### 1.3 LLVM 真的做出 Gemini 預測的形狀(asm 摘要)

```asm
Eval_Alu:
    movzbl (%rcx), %r8d         ; load alua
    movzbl 1(%rcx), %eax        ; load alub
    addl %r8d, %r9d              ; sum = alua + alub
    movzbl 2(%rcx), %edx
    addl %r9d, %edx              ; sum += cin
    andb %r8b, %r9b              ; and_result
    orb  %r8b, %r10b             ; or_result
    xorb %r8b, %al               ; eor_result
    movd 3(%rcx), %xmm0          ; load 4 op selectors as <4 x i8>
    punpcklbw / punpcklwd %xmm0  ; vectorize
    pcmpeqb %xmm0, %xmm1         ; compare to 0
    movmskps %xmm1, %r8d         ; extract 4-bit mask
    cmovnel %esi, %edx           ; branchless mux chain
    cmovnel %esi, %r11d
    cmovnel %edx, %r9d
    cmovel %r10d, %r9d
    cmovel %eax, %r9d
    movb %r9b, 8(%rcx)           ; store alu
    movb %r11b, 9(%rcx)          ; store cout
    retq
```

**重點**:
- **暫存器運算為主**,沒有任何中間 memory write。
- 4 個 op 選擇用 SIMD load(`movd` + `punpcklbw/wd`)一次解析成 mask。
- 分支邏輯全用 `cmov`(branchless mux)實現 —— **CPU 分支預測完全不受影響**。
- 總指令數 ~25 條(包含 prologue/epilogue),純 ALU 工作 ~12 條。

這正是 **Gemini §2.2 預測的「大 Basic Block + 純 SSA dataflow + 邊界 load/store + InstCombine」的最終形狀**。LLVM 真的可以做到。

### 1.4 `--alu-bench`(#46)實測

```
# AluBlock.dll native bench (n = 2,000,000 calls)
# bulk Eval_AluN  : 4.18 ms total, 2.09 ns/call (478,789,620 ops/sec)
# per-call Eval_Alu: 8.77 ms total, 4.38 ns/call (228,099,588 ops/sec)
# (P/Invoke crossing overhead per call = ~2.30 ns)
# correctness     : 2000/2000 match (alu + cout against hand-computed)
```

---

## 2. 速度推估 —— 整體 codegen 能給我們多少?

### 2.1 ALU 單獨

```
S1 baseline:                  ~39.2 ns / recalc(平均,所有 14.7k node)
Native ALU bulk:               2.09 ns / call  → 18.8× 加速
Native ALU per-call:           4.38 ns / call  →  8.9× 加速(含 ~2.3 ns P/Invoke)
```

實際 codegen dispatcher 用 Gemini §2.8 的 **bitmask polling + TZCNT + jump table switch**(編成 jump table 後 dispatch overhead ~3-5 cycles,大約 1 ns),會接近 bulk path 的數字。

### 2.2 全域估計

假設 ALU 在 D 中佔 ~10-15%(從 `--dump-block` 的 133 內部節點 / ~14.7k live 的比例,加上 ALU 的高觸發頻率推算):

```
S1 全部 D 工作:         ~25 µs / hc(per recalc 平均 ~40 ns × 610 recalc)
ALU 區工作量:            ~10% × 25 = 2.5 µs / hc
ALU 用 codegen 後:        2.5 / 18.8 ≈ 0.13 µs / hc
ALU 部分省下:             2.37 µs / hc
ALU 單獨給的加速:         25 / (25 - 2.37) ≈ 1.10× ≈ +10%
```

**單做 ALU,粗估 +10% 加速**。要拿到 Gemini 預測的 **2.5×-4× 全域**,需要把 50-100 個熱 macro-block 都 codegen 化。例如:
- ALU(+10%)
- Address decoder + IR(~+15%)
- PPU sprite eval(~+15%)
- PC / SP 計算邏輯(~+10%)
- 各 PLA 解碼(~+10%)

累積起來達到 main parked β 的 ~2-3× 範圍是合理推估。

---

## 3. 為什麼是 GO(對照 Gemini § 5.6 決策表)

| Native vs S1 倍率 | 解讀 | 決策 |
|---|---|---|
| > 5× | LLVM 路徑強烈值得做 | 進 codegen |
| 3-5× | 仍值得做但要更細評估 | 多測幾個 block |
| 1-3× | LLVM 救不了多少 | 放棄 LLVM |
| ≤ 1× | 不可能,排除 | 重新審視 |

**Native ALU = 18.8× bulk / 8.9× per-call,直接落在「>5×」最強訊號區**。Codegen 路徑確定有路。

---

## 4. 沒做的部分 + 為什麼可接受

### 4.1 沒做嚴格的「S1 vs native on same vectors」

我們的 S1 對照用了**全網表平均 ns/recalc(39.2 ns)**,而非「驅動 ALU 輸入後 specifically alu output 的 recalc 時間」。
要做嚴格對照,需要:
- SetHigh/SetLow 驅動 alua/alub/cin/op-SUMS 各 bit。
- 強制 RecalcNodeList(alu output 集合)。
- 量 ns / vector。

但這在 S1 上不直接 —— alua/alub 在運轉中的 CPU 上被 SB(special bus)驅動,SetHigh 會被 S1 的群解析覆蓋。要 isolate 需要凍住 CPU 其他部分(force res HIGH 等),工程量不小。

**為什麼可以省略**:
- 18.8× 遠超 >3× 門檻,**即使 ALU 的 specific S1 recalc 比 average 快 5×(極端假設),native 還是 ~4× faster**。仍在 GO 區域。
- 嚴格對照只是「精確化倍率」,不會改變 go/no-go 決定。
- 真正要驗的是「整體 codegen 路徑有沒有效益」,不是「ALU 一個 block 的精確倍率」。

### 4.2 沒做 ALU stress ROM

`05 §5.5 選做` 有提到可用 wla-dx 寫 ALU 緊湊壓力 ROM 量「真實工作量下 ALU 佔多少 D」。**現階段沒必要**:既然單 block 18.8×,即使 ALU 只佔 5% D 也值得做。可以留到 dispatcher 框架實作後,真實多 block 整合測 hc/s 時再用。

---

## 5. 後續工程路線(可開工的清單)

依 Gemini §4 + 本驗收結論,接下來:

1. ✅ **§4 Step 1**:ALU C++ 黑盒驗收 → 通過(本文)。
2. **§4 Step 2 — Dispatcher framework**(下一步):
   - 實作 `uint64_t dirty_mask` + `__builtin_ctzll` + `switch` jump-table 的 dispatcher。
   - 把 ALU 變成第一個 codegen block,其餘走 hybrid interpreter fallback。
   - 量 dispatch overhead 確實在 3-5 cycles。
   - `--ir-codegen-alu` 之類的 flag。
3. **§4 Step 3 — Graph partitioner**:
   - 從 `--dump-block` 的「stop at pull-up」啟發式做進階版,自動切 50-100 個 macro-block。
   - Bottom-up from clean pass-island + topological clustering + size-constrained cut(Gemini §2.4)。
4. **§4 Step 4 — LLVM JIT/AOT emit**:
   - 加 `LLVMSharp.Interop` v20.1.2 + `libLLVM.runtime.win-x64` v20.1.2(main 已驗)。
   - 從 main 的 `LlvmSpike.cs` boilerplate 複製出來。
   - 為每個 macro-block emit `void Eval_X(byte* in, byte* out, byte* state)`。
   - MCJIT compile,綁定 dispatcher。

每一步都有可驗證 deliverable。

---

## 6. 量測 reproducibility

```
git: 32fc210 +(這份 doc)
工具鏈: clang 22.1.6 / MSVC cl 19.44 / .NET 10
ROM: 不需要(純 native benchmark)
命令:
  clang -O3 -shared src/AprVisual/Native/AluBlock.cpp -o src/AprVisual/Native/AluBlock.dll
  dotnet run --project src/AprVisual -c Release -- --alu-bench 2000000

機器:本機,測試時 baseline ~41.8K hc/s(S1 reference)。
```

---

## 7. 一句話收尾

> **6502 ALU 在 LLVM 編出來的 native code 跑 2.09 ns/call(478 M ops/sec)、18.8× faster 比 S1 平均 recalc 成本。Gemini 說的 macro-block codegen 路徑有真實的速度槓桿,go signal 確認。下一步進 dispatcher framework + graph partitioner + LLVM emit。**
