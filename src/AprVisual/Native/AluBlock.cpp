// AluBlock.cpp — hand-coded 6502 ALU, modelling what LLVM macro-block codegen WOULD produce from
// the IR after we built a partitioner. Used for the P/Invoke black-box validation experiment
// (MD/impl/math-algos/05 §5). If this isn't >3× faster than S1's ComputeNodeGroup on the same
// inputs, the whole LLVM codegen path is abandoned (per Gemini r1 §5.6 go/no-go criterion).
//
// 6502 ALU (NMOS 2A03, no decimal mode — the NES variant disables BCD):
//   inputs : alua[8]  alub[8]  cin (1 bit)  op selector (one-hot of SUMS / AND / OR / EOR)
//   outputs: alu[8]   cout (carry-out, meaningful only for SUMS)
// We model the BEHAVIOUR, not the bit-slice carry-save transistor structure. LLVM/MSVC's
// optimizer is what does the lowering to optimal ALU instructions (typically: lea / add for SUMS,
// single-instruction native bitwise for the others, blended via cmov for op-mux).
//
// Build:
//   clang -O3 -shared AluBlock.cpp -o AluBlock.dll
//     (clang 22.x; uses Microsoft x64 calling convention by default on Windows)
// or:
//   cl /LD /O2 /std:c++17 AluBlock.cpp /Fe:AluBlock.dll   (VS Dev shell)

#include <stdint.h>

// Layout: keep packed (size matters for cache locality on the C# side; this is what crosses
// the P/Invoke boundary). Inputs filled in by the C# harness before the call; outputs read
// after the call. The op selector is one-hot — exactly one of op_sums/op_and/op_or/op_eor
// should be 1 per call (other combinations: see Eval_Alu semantics below).
struct AluCtx {
    uint8_t alua;
    uint8_t alub;
    uint8_t cin;
    uint8_t op_sums;
    uint8_t op_and;
    uint8_t op_or;
    uint8_t op_eor;
    uint8_t _pad;   // align output bytes to a fresh word
    uint8_t alu;    // result
    uint8_t cout;   // carry-out
};

// Pure-function eval: takes a context pointer, computes the new outputs in place. Marked
// noinline so it shows up as a discrete call in profiles + so P/Invoke crossing is clean.
extern "C" __declspec(dllexport)
void __cdecl Eval_Alu(AluCtx* c) {
    uint32_t a = c->alua;
    uint32_t b = c->alub;

    // SUMS — full 8-bit add with carry-in; carry-out is bit 8 of the result.
    uint32_t sum = a + b + c->cin;
    uint8_t  sum_result = (uint8_t)sum;
    uint8_t  sum_cout   = (uint8_t)((sum >> 8) & 1);

    // Bitwise — single instruction each on x64 (and / or / xor).
    uint8_t and_result = (uint8_t)(a & b);
    uint8_t or_result  = (uint8_t)(a | b);
    uint8_t eor_result = (uint8_t)(a ^ b);

    // Op mux — one-hot select. If no op is asserted, output is 0 (caller's responsibility to
    // assert exactly one). If multiple are asserted simultaneously, the LAST one wins (this
    // is the order the 6502's actual ALU resolves under conflicting controls; in practice
    // the PLA enforces mutual exclusion so the conflict path is never exercised).
    uint8_t r = 0;
    if (c->op_sums) r = sum_result;
    if (c->op_and)  r = and_result;
    if (c->op_or)   r = or_result;
    if (c->op_eor)  r = eor_result;

    c->alu  = r;
    c->cout = c->op_sums ? sum_cout : 0;
}

// Bulk-eval over an array of contexts — amortises the P/Invoke crossing across many evals.
// The C# harness can call this with a contiguous Span<AluCtx> for tight benchmark inner loops.
extern "C" __declspec(dllexport)
void __cdecl Eval_AluN(AluCtx* arr, int32_t n) {
    for (int32_t i = 0; i < n; i++) Eval_Alu(&arr[i]);
}
