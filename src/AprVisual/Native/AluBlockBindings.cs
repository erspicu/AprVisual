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
        // Mirrors AluBlock.cpp's AluCtx exactly (8 input + 2 output bytes, packed, no padding).
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct AluCtx
        {
            public byte alua, alub, cin, op_sums, op_and, op_or, op_eor, _pad;
            public byte alu, cout;
        }

        [DllImport("AluBlock.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void Eval_Alu(AluCtx* c);

        [DllImport("AluBlock.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe void Eval_AluN(AluCtx* arr, int n);
    }
}
