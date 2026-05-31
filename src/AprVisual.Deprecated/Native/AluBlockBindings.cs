using System.Runtime.InteropServices;

namespace AprVisual.Native
{
    // Shared P/Invoke bindings for AluBlock.dll. Used by both --alu-bench (TestRunner) and the
    // runtime dispatcher (WireCore.Dispatcher). Keep this thin — it's just the contract.
    //
    // Build the DLL with: clang -O3 -shared src/AprVisual/Native/AluBlock.cpp -o src/AprVisual/Native/AluBlock.dll
    // The csproj copies it next to the .exe so DllImport resolves it.

    internal static class AluBlockBindings
    {
        // Mirrors AluBlock.cpp's AluCtx exactly: 3 input bytes (alua/alub/cin) + 5 op-selector
        // bytes (SUMS/ANDS/ORS/EORS/SRS) + 2 output bytes (alu/cout) + 6 padding = 16 bytes.
        // Step 2.5: added op_srs (shift-right path), renamed op_and→op_ands etc. to match
        // 6502 PLA naming. wired-OR semantics on conflict (matches NMOS bus behaviour).
        [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 16)]
        internal struct AluCtx
        {
            public byte alua;     // 0
            public byte alub;     // 1
            public byte cin;      // 2
            public byte op_sums;  // 3
            public byte op_ands;  // 4
            public byte op_ors;   // 5
            public byte op_eors;  // 6
            public byte op_srs;   // 7
            public byte alu;      // 8
            public byte cout;     // 9
            // 6 bytes of padding (Size = 16)
        }

        [DllImport("AluBlock.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void Eval_Alu(AluCtx* c);

        [DllImport("AluBlock.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void Eval_AluN(AluCtx* arr, int n);
    }
}
