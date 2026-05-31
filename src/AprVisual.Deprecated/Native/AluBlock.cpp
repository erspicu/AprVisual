// AluBlock.cpp — hand-coded 6502 ALU, modelling what LLVM macro-block codegen WOULD produce from
// the IR after we built a partitioner. Used for the P/Invoke black-box validation experiment
// (MD/impl/math-algos/05 §5). If this isn't >3× faster than S1's ComputeNodeGroup on the same
// inputs, the whole LLVM codegen path is abandoned (per Gemini r1 §5.6 go/no-go criterion).
//
// 6502 ALU (NMOS 2A03, no decimal mode — the NES variant disables BCD):
//   inputs : alua[8]  alub[8]  alucin (1 bit)  op selectors (5 PLA outputs: SUMS/ANDS/ORS/EORS/SRS)
//   outputs: alu[8]   alucout (carry-out — meaningful for SUMS and SRS)
//
// The 5 op selectors are the REAL 6502 PLA outputs (per ref/metalnes-main/data/system-def/2a03/
// nodenames.js L606-L610). Multiple may be asserted simultaneously — bus contention resolves as
// wired-OR on the actual chip (each active result OR's into the alu output bus). PLA normally
// enforces exactly one active, but we model the wired-OR conservatively so the codegen byte-equals
// S1 even under the conflict path.
//
// Op coverage by 6502 instruction:
//   ADC/SBC/CMP/INC/DEC      → SUMS path (sum = alua + alub + cin)
//   ASL / ROL                → SUMS (alub=alua, cin selects ROL vs ASL: 0 vs prev-carry)
//   LSR / ROR                → SRS (right shift; cin selects ROR vs LSR: prev-carry vs 0)
//   AND                      → ANDS
//   ORA                      → ORS
//   EOR                      → EORS
//
// Build:
//   clang -O3 -shared AluBlock.cpp -o AluBlock.dll
//     (clang 22.x; uses Microsoft x64 calling convention by default on Windows)

#include <stdint.h>

// Layout: keep packed (size matters for cache locality on the C# side; this is what crosses
// the P/Invoke boundary). Inputs filled in by the C# harness before the call; outputs read
// after. Padded out to 16 bytes for a clean cache-line position.
struct AluCtx {
    uint8_t alua;       // 0
    uint8_t alub;       // 1
    uint8_t cin;        // 2
    uint8_t op_sums;    // 3
    uint8_t op_ands;    // 4
    uint8_t op_ors;     // 5
    uint8_t op_eors;    // 6
    uint8_t op_srs;     // 7  ← new in Step 2.5: shift-right path (LSR/ROR)
    uint8_t alu;        // 8  result
    uint8_t cout;       // 9  carry-out (SUMS or SRS)
    uint8_t _pad[6];    // 10..15 — pad to 16 bytes
};

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

    // SRS — right shift by 1; cin shifts INTO bit 7 (ROR's carry-in); old bit 0 becomes c-out.
    // LSR has cin=0 (PLA forces it); ROR has cin = the latched prev-carry.
    uint8_t srs_result = (uint8_t)((a >> 1) | ((uint32_t)c->cin << 7));
    uint8_t srs_cout   = (uint8_t)(a & 1);

    // Wired-OR mux — each active op contributes its result to the alu bus. In practice the PLA
    // asserts exactly one of the 5 ops per cycle; if more than one were simultaneously asserted
    // the chip would wire-OR them, which is what this code models.
    uint8_t r = 0;
    if (c->op_sums) r |= sum_result;
    if (c->op_ands) r |= and_result;
    if (c->op_ors)  r |= or_result;
    if (c->op_eors) r |= eor_result;
    if (c->op_srs)  r |= srs_result;

    // Carry-out — wired-OR of SUMS and SRS contributions (ANDS/ORS/EORS don't drive C).
    uint8_t cout = (uint8_t)((c->op_sums ? sum_cout : 0) | (c->op_srs ? srs_cout : 0));

    c->alu  = r;
    c->cout = cout;
}

// Bulk-eval over an array of contexts — amortises the P/Invoke crossing across many evals.
// The C# harness can call this with a contiguous Span<AluCtx> for tight benchmark inner loops.
extern "C" __declspec(dllexport)
void __cdecl Eval_AluN(AluCtx* arr, int32_t n) {
    for (int32_t i = 0; i < n; i++) Eval_Alu(&arr[i]);
}
