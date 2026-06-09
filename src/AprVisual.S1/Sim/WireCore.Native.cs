using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Unmanaged memory: 64-byte aligned (cache-line / SIMD friendly).
        //    .NET 10 only — no net48 fallback (decision: target net10 only).
        //    Every alloc here is tracked so FreeUnmanagedMemory() can release the lot. ──
        private static readonly List<IntPtr> _allocations = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AllocAligned(nuint byteCount)
        {
            void* p = NativeMemory.AlignedAlloc(byteCount, 64);
            _allocations.Add((IntPtr)p);
            return p;
        }

        /// <summary>Alloc and zero `count` elements of T (T must be unmanaged). 64-byte aligned, tracked.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* AllocArray<T>(int count) where T : unmanaged
        {
            nuint bytes = (nuint)count * (nuint)sizeof(T);
            void* p = AllocAligned(bytes);
            NativeMemory.Clear(p, bytes);
            return (T*)p;
        }

        /// <summary>Free a single tracked allocation early (rare; normally use FreeUnmanagedMemory()).</summary>
        public static void FreeAligned(void* p)
        {
            if (p == null) return;
            _allocations.Remove((IntPtr)p);
            NativeMemory.AlignedFree(p);
        }

        // ── Handler-lifetime allocations (NOT in _allocations) ──
        // Handler arrays (video node-lists, NES palette, memory-handler node-lists, behavioral RAM/ROM
        // Data) are allocated at attach / setup-memory time — BEFORE Reset() — and must survive the
        // FreeUnmanagedMemory() that every Reset()/SoftReset() runs. So they live in a SEPARATE pool,
        // freed only by FreeHandlerArrays() (called from ResetHandlers() at the start of each rebuild,
        // before the handlers/memories are re-created). The closures hold the pointers via static fields
        // or captured managed holders, so this pool's lifetime exactly matches one composed system.
        private static readonly List<IntPtr> _handlerAllocations = new();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* AllocHandlerArray<T>(int count) where T : unmanaged
        {
            nuint bytes = (nuint)count * (nuint)sizeof(T);
            void* p = NativeMemory.AlignedAlloc(bytes, 64);
            NativeMemory.Clear(p, bytes);
            _handlerAllocations.Add((IntPtr)p);
            return (T*)p;
        }

        /// <summary>Free every handler-lifetime allocation. Called from ResetHandlers() at rebuild.</summary>
        public static void FreeHandlerArrays()
        {
            foreach (IntPtr p in _handlerAllocations)
                if (p != IntPtr.Zero) NativeMemory.AlignedFree((void*)p);
            _handlerAllocations.Clear();
        }

        /// <summary>Free every unmanaged allocation owned by WireCore and null the field pointers.</summary>
        public static void FreeUnmanagedMemory()
        {
            foreach (IntPtr p in _allocations)
                if (p != IntPtr.Zero) NativeMemory.AlignedFree((void*)p);
            _allocations.Clear();

            NodeStates = null;
            NodeInfos = null;
            NodeConnections = null;
            NodeTlistGates = null;
            FlagsToState = null;
            TransistorList = null;
            RecalcList = RecalcListNext = null;
            RecalcHash = RecalcHashNext = null;
            _groupBuf = null;
            _inGroup = null;
            IsPureLogic = null;   // fast-path classifier (always re-built by Reset)
            PruneMask = null;   // enqueue-prune safety mask (bit0 turn-on / bit1 turn-off; always re-built by Reset)
            IsBypassCandidate = null;   // P-5 Escape B skip-candidate mask (always re-built by Reset)
            FrameBuffer = null;
        }
    }
}
