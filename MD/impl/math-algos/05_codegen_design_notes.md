# Codegen 設計筆記(LLVM macro-block 路徑)+ ALU 黑盒實驗 prep

> 前置:Phase 2 已收尾在 `04_phase2_ir_results.md` —— event-driven IR interpreter 的 CPU 天花板 = 與 S1 持平。要再上去**只剩 codegen**。
> 本文 = (1) Gemini 3.1 Pro 對「macro-block codegen 設計」的深度回覆(原始 log:`tools/knowledgebase/message/20260523_130001.txt`)的整理 + 我們的綜合判斷;(2)第一個 P/Invoke 黑盒實驗(ALU)的具體 prep。

---

## 1. 工具鏈現況(確認過)

| 工具 | 狀態 | 路徑 / 備註 |
|---|---|---|
| **MSVC `cl.exe`** | ✅ 19.44(VS 2022 17.x)| `C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC\14.44.35207\bin\Hostx64\x64\cl.exe` |
| **VS Dev shell** | ✅ 一鍵激活 | `Common7\Tools\Launch-VsDevShell.ps1 -Arch amd64 -HostArch amd64 -SkipAutomaticLocation` —— 在 PowerShell session 內把 cl/link/INCLUDE/LIB 都載入 |
| C# **P/Invoke** | ✅ 內建 .NET 10 SDK 已具備 | |
| **clang-cl / clang** | ❌ 未裝(VS 沒帶 LLVM 元件)| 不需要 —— C# 端 LLVM 走 NuGet(見下) |
| **LLVM via NuGet** | ✅ **main 已驗證可用** | `LLVMSharp.Interop` v20.1.2 + `libLLVM.runtime.win-x64` v20.1.2 —— libLLVM.dll 由 NuGet bundle 提供,**不用裝系統 LLVM**。main 的 `Sim/Logic/LlvmSpike.cs` + `LlvmCodegen.cs` 是直接可參考的整合範本。要真寫 codegen backend 時加這兩個 PackageReference 即可。 |
| `llc`/`opt`/lld(獨立 binary)| ❌ 未裝 | **不需要** —— LLVMSharp 已涵蓋我們會用的 API(BuildModule/IRBuilder/MCJIT) |
| CMake / Ninja | ❌ | 不需要;C++ DLL 直接 `cl /LD foo.cpp` |
| **wla-dx 6502 assembler** | ✅ `tools/wla-dx/` | `wla-6502.exe v10.6` + `wlalink.exe v5.21`,**可造特製 NES 測試 ROM**(緊湊 ALU loop、PPU 特定 sub-block 壓力測試等)。比拿 `full_palette` demo 來測 ALU 精準很多 —— 真實工作量可控 + 可重現 |
| Python | ✅ | gemini_query.py 已驗證可用 |

**結論**:工具鏈完整到位 —— ALU 黑盒實驗 + 未來的 LLVM codegen 都不缺東西。**這比預期樂觀** —— LLVMSharp NuGet 走過(main 已驗),wla-dx 可造專屬測試 ROM。

### 1.5 既有可重用資產(來自 main + 工具目錄)

**main 的 LLVM 整合 scaffolding(`origin/main:src/AprVisual/Sim/Logic/`)**:
- `LlvmSpike.cs` —— 最小 spike,build 一個 `int add(int,int)`,MCJIT 編 + 呼叫。**驗證 LLVMSharp + libLLVM toolchain 接好的 boilerplate**,我們的 codegen 階段可以直接複用做 smoke test。
- `LlvmCodegen.cs` —— main S4.5 的真實 backend,emit `void step(i8* cur, i8* prev)`,**chunked**(每 chunk 512 nodes,避免 LLVM reg-alloc 在巨型 function 上崩潰)。Per-node Expr lowering:
  ```
  NodeRef  → load i8 from cur[id]      (within a chunk, just-stored value forwarded)
  Hold     → load i8 from prev[id]     (prev unchanged in step → loaded once)
  Const    → i8 0/1
  Not(x)   → xor i8 x, 1
  And/Or   → and/or i8
  Mux      → select i1 (icmp ne x, 0), then, else
  ```
- **但**:main 的 IR shape 是 *oblivious topological step over the whole netlist*(也就是被證偽的那條路徑)。**我們要重用的是 LLVMSharp API 用法 + chunking 策略 + JIT 設定,IR emit 的形狀要改**(macro-block + event-driven dispatcher,不是全網表 step)。
- **直接複用 `LlvmSpike` 做我們的 smoke test 沒問題**,連 NuGet 版本都對齊。
- **`LlvmCodegen` 當參考**(尤其 BuildModule/CreateBuilder/AppendBasicBlock/MCJIT 那一套 setup),但 codegen body 要重寫成「per macro-block function with explicit Inputs/Outputs/State context struct」。

**wla-dx(`tools/wla-dx/`)6502 + 多平台 macro assembler**:
- `wla-6502.exe v10.6` 編 → `wlalink.exe v5.21` 連結 → NES ROM(iNES 格式)。
- 可寫 ~50–200 行 6502 組合,**鎖死一個特定 sub-block 的工作量**(e.g. 一個只反覆做 ADC/AND/EOR 的緊湊 loop)。
- 對 ALU 黑盒實驗特別有用:除了 §5 直接驅動 ALU input nodes 之外,也可以用「ALU 壓力 ROM」量出 ALU 在真實 workload 裡的 D 佔比,避免被 `full_palette` 等通用 ROM 的工作分布稀釋。
- 也可用於後續 codegen 驗收的微觀對照(每個 block 用一支專屬 ROM 驅動,比較細)。

---

## 2. Gemini 3.1 Pro 的核心洞察(整理)

### 2.1 為何 per-node IR 對 compiler 不友好(機制層)

LLVM 的優化引擎(`mem2reg`、`InstCombine`、register allocator)依賴 **SSA + 足夠大的 Basic Block**。把現在的 per-node Expr/LUT 直接餵給 LLVM,它看到的是:

```llvm
%in1 = load i8, ptr %state_A
%in2 = load i8, ptr %state_B
%out = and i8 %in1, %in2
store i8 %out, ptr %state_C
```

**崩潰點**:scope 太小,LLVM 無法把 `state_A/B` 提升到硬體暫存器 → 滿天飛的 Load/Store。原生機械碼**不只沒比直譯器快**,還因為失去 L1 cache 的緊湊性 + I-cache 碎片化 → 更慢。**這就是 main 的 S4 撞過的牆。**

### 2.2 Compiler-friendly IR 形狀

關鍵性質:
- **大 Basic Block** —— 一個 block 內幾十~上百個 gate,沒有 control flow。
- **純 SSA dataflow** —— 完全摒棄中間 memory 存取,只在 block **頭**(Input load)和 **尾**(Output store)碰 memory。
- (選做)**Bit-packing**:把 8 個 NMOS 狀態打包進 `i8`/`i32`,讓 `InstCombine` 自動平行 bitwise。

**對照範例**(同一個 `D=AND(A,B); E=NOT(D); F=OR(E,C)`):

| 我們現在的 IR | LLVM 期望的 IR |
|---|---|
| 三個 NodeRef + 三個 Expr 節點,EvalLut 各 evict | 一個 `Eval_MacroBlock_X(in*, out*)` 函數,SSA 純暫存器,只在頭尾 load/store |

### 2.3 量級估計:**全域 2.5×–4×**

推導(Amdahl):
- 直譯器算 60 個 gate ≈ 60 × 15ns dispatch = **900ns**。
- LLVM 編 60 條 ALU 指令 < **20ns**(純暫存器,pipeline)。
- 單 block 內 **10×–20×**。
- 扣掉 block 之間的 event-driven dispatch overhead → **全域 2.5×–4×**。
- 業界 reference:**Verilator** 從 event-driven 轉 oblivious-compiled 通常 ~10×,我們因受限於 macro-block(非整網表 oblivious)打折拿 3×–4×。

### 2.4 Macro-block 切割演算法

**不要手動標註,也不要單純依賴 SCC**。建議:
1. **Bottom-up 從現有 clean pass-island 起步**(它們是不可分割的原子)。
2. **Topological clustering**:island A 只有一個 fanout 指向 B → merge(消除 A→B 的 dispatch overhead)。
3. **Size-constrained cut**:目標 **50–250 gates/block**。最小化 block 間 external edges。greedy:選種子,沿 fanout/fanin 吸收,直到 size limit 或撞到高 fanout 節點(clock 樹、大 bus)。

### 2.5 Block 邊界形式化

每個 block 一個 **Context Struct**:
```cpp
struct Block_ALU_Context {
    const uint8_t* in_alua[8];    // 邊界 inputs 是指向全域 NodeStates 的指標
    const uint8_t* in_alub[8];
    const uint8_t* in_alucin;
    const uint8_t* in_op_sums;
    uint8_t out_alu[8];           // 邊界 outputs
    uint8_t out_alucout;
    uint8_t state_carry_chain[7]; // internal state(carry 鏈中間 latch,若有)
};
```

由 Dispatcher 在初始化時綁定 inputs 到全域 `NodeStates` 或其他 block 的 outputs。

### 2.6 動態節點在 macro-block 下**反而變簡單**(反直覺但對)

**這是 Gemini 給的最大驚喜**。我們 Phase 2 因為「dynamic node 抽象錯」風險(計畫文件失敗點 #1)放棄的 **1,084 個 no-pull islands**,在 macro-block 框架下變容易:

- 直譯器:Hold 依賴「不更新」維持狀態,容易出微妙 bug。
- macro-block:Hold 是 context struct 裡的明確 memory 欄位 → block 開頭 `load prev`、結尾 `store new`。LLVM 看到 pointer/struct 寫入 = **observable side-effect** → 絕對不會常數摺疊。

也就是說,**走 codegen 路徑反而能撈回我們先前不敢動的 1,084 個 dynamic island**。這對速度上限有實質意義。

### 2.7 Block 內執行 = **Oblivious + 靜態拓樸序**

block 內全部算一遍。算 100 個純暫存器指令的成本遠低於 block 內再做一次 dirty check。

Latch(feedback)處理:**break the loop**。把 latch 的輸出端宣告成 `state` 欄位:
```cpp
uint8_t prev_Q = state->Q;            // load prev
uint8_t next_Q = nor(R, prev_Qbar);
uint8_t next_Qbar = nor(S, prev_Q);
state->Q = next_Q;                    // store new
state->Qbar = next_Qbar;
```
完全符合硬體 R-S latch 時序語意。

### 2.8 **Block-level dispatch:Bitmask Polling**(關鍵設計突破)

r2 警告過 function-pointer queue 會死於 indirect-branch 預測失敗。Gemini 3.1 給的正解:

```c
uint64_t dirty_mask = 0;   // 最多 64 個 macro-block

while (dirty_mask != 0) {
    int next_block = __builtin_ctzll(dirty_mask);   // BSF/TZCNT,硬體指令 1 cycle
    dirty_mask &= ~(1ULL << next_block);

    // 對小且密集的整數範圍 0..63,C++/LLVM 編成 jump table。
    // 現代 CPU BTB 對 jump table 的預測率 >> 亂序 function pointer。
    switch (next_block) {
        case 0: Eval_Block_0(&ctx0); break;
        case 1: Eval_Block_1(&ctx1); break;
        // ...
    }
}
```

**Dispatch overhead 從 15–30 cycles 壓到 3–5 cycles,零動態記憶體配置。** 這是把 main 撞過的 indirect-branch 死路繞開的關鍵。

Block A 改變了 output,連到 block B → A 的程式碼負責 `dirty_mask |= (1ULL << B_ID)`(完全內聯,LLVM 編成 `or` + 立即數)。

### 2.9 混合設計可行(最後防線)

如果某些 block 太怪 / 太冷,保留直譯器路徑作 fallback:
- 在 `dirty_mask` 保留 bit 63 給 interpreter。
- LLVM → Interpreter 邊界:LLVM block 結尾呼叫 `AddNodeToGroup(boundary_nn)` + `dirty_mask |= (1ULL << 63)`。
- Interpreter → LLVM 邊界:直譯器算完邊界節點時 `dirty_mask |= (1ULL << X)`。
- **但切換粒度不能太細**(I-cache vs D-cache 互擾)。

### 2.10 第一個驗收 block = **ALU + P-flag 計算**

理由(Gemini 排序):
1. **純組合邏輯為主**(~100-200 gate),carry chain 內部依賴深 → 最能展現 LLVM `InstCombine` + register allocation 的威力。
2. **邊界清晰**:Inputs = A/B/Cin/Control;Outputs = Sum/Flags。
3. **高頻觸發**:CPU 計算時 ALU 大量動作 → 高 D 佔比 → Profiler 可見。
4. **隔離容易**:沒有 PPU sprite eval 那些怪異多相時脈/動態浮接。

**驗收門檻**:**手寫 C++ ALU 對比 S1 `ComputeNodeGroup` 跑同樣輸入,必須 > 3×。若不到,整條 LLVM 路徑放棄。**

---

## 3. Phase 2 vs Codegen — 設計轉向對照表(承 §6 of `04_phase2_ir_results.md`,Gemini 3.1 補充後再確認)

| 設計面向 | Phase 2(interpreter 友好) | Codegen 需要 |
|---|---|---|
| **抽取粒度** | per-node Expr(3,602 個小 Expr,K≈1.86)| **macro-block**(~50 個 50-250 gate 的塊)|
| **runtime eval** | byte LUT lookup(O(1) branch-free)| inline bitwise ops + 暫存器 SSA dataflow(LLVM 編出),**捨棄 LUT** |
| **dirty queue** | per-node + revDep CSR | **bitmask polling(uint64,~50 blocks)+ TZCNT + jump-table switch** |
| **block 邊界** | 沒概念 | **Context Struct**:Inputs ptrs / Outputs / Internal state(明確 memory 區隔)|
| **動態節點(no-pull)** | Phase 2 放棄(失敗點 #1 風險)| **變容易** —— context state 欄位,LLVM 認為是 observable side-effect → 不會被常數摺疊。**Phase 2 放棄的 1,084 個 islands 可救** |
| **block 內排程** | (沒概念,per-node)| **Oblivious + 靜態拓樸序**;latch 用 `prev_*` / `next_*` break the loop |
| **記憶體佈局** | 已 RCM 重排 | **保留** —— codegen 必須繼續尊重 contiguous |
| **絕對不能(雷區)** | (interpreter 沒這問題)| ❌ Oblivious 全網表(main 撞過,O(N) + I-cache);❌ Per-node function pointer queue(indirect-branch 死)|

---

## 4. 決策路徑(採納 Gemini 建議)

**完整路徑要 4 步**:

```
Step 1. [Stop. Validate first.]
  手寫 ALU + P-flag 的 C++,P/Invoke 給 C#,量比 S1 ComputeNodeGroup 快多少。
  ↓ if speedup > 3× → 繼續;否則放棄 LLVM 路徑。

Step 2. [Dispatcher framework.]
  實作 uint64_t dirty_mask + __builtin_ctzll + jump-table switch 的 dispatcher。
  把 ALU 變成第一個 block,其餘走 hybrid interpreter fallback。
  ↓ 驗證 dispatch overhead 真的在 3-5 cycles。

Step 3. [Graph partitioner.]
  寫一個從 pass-island 出發,bottom-up 聚合到 50-250 gate/block 的切割演算法。
  切出 ~50 個熱 block。

Step 4. [LLVM JIT/AOT codegen.]
  把每個 block 的 oblivious + topological-ordered next-state emit 成 LLVM IR
  → JIT 或 AOT 編成 native function。dispatcher 呼叫。
```

**Step 1 是 go/no-go**。下一節是它的 prep。

---

## 5. ALU 黑盒實驗 prep —— 具體計畫

### 5.1 NES 2A03 ALU 節點清單(從 `ref/metalnes-main/data/system-def/2a03/nodenames.js`)

| 角色 | nodename | node id | 數量 |
|---|---|---|---|
| Input A(operand 1)| `alua[0..7]` | 1167, 1248, 1332, 1680, 1142, 530, 1627, 1522 | 8 |
| Input B(operand 2)| `alub[0..7]` | 977, 1432, 704, 96, 1645, 1678, 235, 1535 | 8 |
| Carry in | `alucin` | 910 | 1 |
| Output(non-inverting)| `alu[0..7]` | 401, 872, 1637, 1414, 606, 314, 331, 765 | 8 |
| Output(inverting)| `notalu[0..7]` | 394, 697, 276, 495, 1490, 893, 68, 1123 | 8 |
| Carry out(latched)| `alucout` | 1146 | 1 |
| Carry out(inverted)| `notalucout` | 412 | 1 |
| Op select - ADD | `op-SUMS` | 1196 | 1 |
| Op select - AND | (待查,類似 PLA 輸出)| | |
| Op select - OR | | | |
| Op select - EOR | | | |
| Pipeline state | `op-T0-adc/sbc`, `op-T+-ora/and/eor/adc`, `op-T+-adc/sbc`, `x-op-T+-adc/sbc` | 575, 1243, 822, 1155 | 4+ |
| **Boundary 估計** | inputs ~25, outputs ~18, **總 boundary ~43 nodes** | | |

**內部節點**(carry chain 中間節點、bit-slice 內部多工器、latch 等):還沒展開,估計 **80–150 個 internal gates**。

### 5.2 邊界提取算法(自動,寫一個 `--dump-block` 工具)

從 `alu[*]` 開始反向走依賴圖(找出所有影響 `alu[*]` 的 transistor),用 closure 把所有相關內部節點抓出來,直到所有依賴只剩「上述 inputs 集合 + 控制信號」。

工作流:
1. 從 outputs `alu0..7` + `alucout` + `notalu0..7` + `notalucout` 出發。
2. 反向遍歷:對每個目前已知節點 v,把「gate 了 v 的 transistor 的 control 端 / 跟 v pass-coupled 的鄰居 / 在 v 的 pulldown 鏈裡的中間節點」都納入 closure。
3. 停止條件:邊界節點 = (a) 標記為 ALU inputs 集合裡的;(b) 標記為 PLA op 控制信號;(c) clock / reset 等全域信號。
4. 內部節點 = closure 內,但不是邊界的。

實作:新增 `--dump-block <output-names>` CLI 子命令,印出 block 的 inputs / outputs / internal / 估計 gate 數。

### 5.3 兩端的測試 harness

```
test_alu.exe (host)
   ↓
   for each test vector (random A, B, Cin, op):
     ↓
     [A] 跑 S1:
        - 把 NodeStates 寫入 alua/alub/alucin/op-* nodes
        - 喚醒 ALU 區域(enqueue inputs,ProcessQueue)
        - 讀回 alu0..7 + alucout
        - 量 elapsed ns
     ↓
     [B] 跑手寫 C++ ALU(via P/Invoke,DLL):
        - 把 inputs 餵進 native function
        - 讀回 outputs
        - 量 elapsed ns
     ↓
     比對 (A) vs (B) outputs 是否相同(正確性)
     累計 (A_ns, B_ns) 統計(效能)
```

**測試向量**:~1000-10000 組隨機 (A, B, Cin, op),涵蓋 add/sub(透過 op-SUMS + 反相)、and、or、eor 四種 op。 也加邊界 case(0/255、carry overflow 等)。

### 5.4 C++ skeleton(`AluBlock.cpp`)

```cpp
// AluBlock.cpp — hand-coded 6502 ALU, modeling what LLVM should produce from macro-block IR.
// Build: cl /LD /O2 /arch:AVX2 AluBlock.cpp /Fe:AluBlock.dll

#include <cstdint>

struct AluCtx {
    uint8_t alua;       // packed 8-bit input A
    uint8_t alub;       // packed 8-bit input B
    uint8_t cin;        // carry-in (bit 0)
    uint8_t op_sums;    // 1 = SUMS (add)
    uint8_t op_and;     // 1 = AND
    uint8_t op_or;      // 1 = OR
    uint8_t op_eor;     // 1 = EOR (xor)
    // outputs
    uint8_t alu;        // result
    uint8_t cout;       // carry-out (only meaningful for SUMS)
};

extern "C" __declspec(dllexport)
void __cdecl Eval_Alu(AluCtx* c) {
    uint32_t a = c->alua;
    uint32_t b = c->alub;

    // SUMS: a + b + cin → result in low 8 bits, carry from bit 8
    uint32_t sum = a + b + c->cin;
    uint8_t  sum_result = (uint8_t)sum;
    uint8_t  sum_cout   = (uint8_t)((sum >> 8) & 1);

    // bitwise
    uint8_t and_result = a & b;
    uint8_t or_result  = a | b;
    uint8_t eor_result = a ^ b;

    // op mux (mutually exclusive — assume exactly one is 1)
    uint8_t r = 0;
    if (c->op_sums) r = sum_result;
    if (c->op_and)  r = and_result;
    if (c->op_or)   r = or_result;
    if (c->op_eor)  r = eor_result;

    c->alu = r;
    c->cout = c->op_sums ? sum_cout : 0;
}
```

(實際 6502 ALU 還有 decimal mode、SBC vs ADC 之類細節,但 NES 的 2A03 **沒有** decimal mode → 我們省事。其他細節在實驗中迭代即可。)

### 5.5 Build + 量測 harness(計畫)

PowerShell 流程:
```powershell
# 1. 啟用 VS Dev shell
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\Launch-VsDevShell.ps1' `
    -Arch amd64 -HostArch amd64 -SkipAutomaticLocation

# 2. 編譯成 DLL
cl /LD /O2 /arch:AVX2 /std:c++17 src/AprVisual/Native/AluBlock.cpp `
   /Fe:src/AprVisual/Native/AluBlock.dll

# 3. C# 端用 [DllImport] 呼叫,跑 harness
dotnet run --project src/AprVisual -c Release -- --alu-bench <rom>
```

在 C# 端加 `--alu-bench` 子命令:
- LoadSystem(rom)、跑到 vblank、然後用測試向量驅動 ALU 區。
- 同時呼叫 S1 path(只跑 ALU 範圍)+ Native path。
- 量 ns/eval,輸出比較。

**選做(更嚴格的「真實工作量」對照)**:用 wla-dx 寫一支 **ALU 壓力 ROM**(緊湊 loop:LDA #$55 / ADC #$33 / AND #$0F / EOR #$AA / ORA #$05 / INX / BNE LOOP / JMP RESET)— 約 16-32 bytes 的 6502 程式。跑這支 ROM 量:
- ALU 區的 RecalcNode 次數 / hc(在 D 中的佔比)
- 整體 hc/s 對比:S1 vs (S1 + ALU 走 native DLL via P/Invoke)
這給「在 ALU-heavy 真實 workload 下,把 ALU IR 化能省下多少全域時間」的數字 —— 比合成測試向量更接近真實 codegen 收益的上限。先做合成向量的直接量測;若結果模糊再做這個。

### 5.6 預期結果 + 決策

| 結果 | 解讀 | 下一步 |
|---|---|---|
| Native > 5× S1 | LLVM 路徑強烈值得做 | 進入 §4 Step 2(實作 dispatcher)|
| Native ~3-5× S1 | 仍值得做但要更細評估 ROI | 多測幾個 block(decoder、PPU 某 sub-block),平均後再決定 |
| Native ~1-3× S1 | LLVM 救不了多少 | **放棄 LLVM 路徑**,把這當 branch 終點 |
| Native ≤ 1× S1 | 設計假設破滅(極不可能,但要排除)| 重新審視,可能 ALU 不是好的 starter block |

---

## 6. 開放問題(Gemini 沒完全回答,或自己要試的)

- **Q-A**:Bitmask polling 的 jump-table dispatch,**LLVM/MSVC 真的會編成 jump table 嗎?** Gemini 說「對密集小整數範圍 0..63,C++/LLVM 會編成 jump table」,但要在 disassembly 確認(不同 codegen 設定可能掉回 binary search / chained branches)。**第一輪實驗就 disassembly cl 編出來的東西,確認 jump table。**
- **Q-B**:Block 切割演算法的具體 implementation —— Gemini 給的是方向(hypergraph partitioning + size-constrained cut + bottom-up from clean pass-islands)。實際 cut 出來的 ~50 個 block 怎樣才算「好」?要等 partitioner 跑出來看分布。
- **Q-C**:Hybrid 邊界處的記憶體一致性 —— Phase 2 NodeStates 是 `byte*`,LLVM block 寫進去後直譯器讀,沒有 memory barrier 問題(同 thread);但 cache line 共享導致的 false sharing 在某些 hot bus 上要不要 align?
- **Q-D**:**SIMD 機會**到底有沒有?Gemini 跟 r2 都比較保守(NES 大多 sequential dependent)。我們的 ALU 8 個 bit-slice 是天然 SIMD(8 個獨立 1-bit add slices = `_mm_add_epi8`?),但 carry chain 是 sequential ⇒ 不純 SIMD。第一輪 ALU 實驗時量 SIMD(`/arch:AVX2`)vs scalar 對 SIMD 大小敏感度。

---

## 7. 下一步行動

1. ✅ 本文件 commit(`MD/impl/math-algos/05_codegen_design_notes.md`)。
2. 寫 `--dump-block` 子命令,從 ALU outputs 反向 closure,印出邊界 + internal node count + 估計 gate 數。**這是黑盒實驗第一個可驗證 deliverable。**
3. 寫 `AluBlock.cpp`(skeleton 已在 §5.4),用 VS Dev shell + cl 編成 DLL。
4. 加 `--alu-bench` C# 子命令(P/Invoke + 同向量 vs S1 比較)。
5. **跑、量、報告:Native 是否 > 3× S1?** —— 這個答案決定 LLVM 路徑要不要走下去。

如果 Step 5 給綠燈,後續才進真正的 dispatcher 框架實作 + graph partitioner + LLVM emit。

如果 Step 5 給紅燈,本 branch 收尾在「Phase 1 1.37× + Phase 2 break-even + LLVM black-box rejected」,跟 main 的 GPU/oblivious 各自獨立、各自結論完整。
