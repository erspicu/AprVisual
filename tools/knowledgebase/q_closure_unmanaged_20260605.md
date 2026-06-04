Context: C# .NET 10, x64, `unsafe` performance-critical switch-level simulator. Hot per-node arrays are unmanaged (NativeMemory.AlignedAlloc, raw `byte*`/`int*`). I have behavioral RAM/ROM "handlers" implemented as captured lambdas (closures) that run as callbacks during simulation. Each handler needs to read a few small per-instance arrays of node ids (`addr`, `dataOut`) and a per-instance `byte*` memory buffer.

The problem: I want these per-instance arrays to be UNMANAGED (raw pointers), but:
- C# forbids capturing a pointer-typed LOCAL in a lambda/anonymous method ("Cannot use a pointer ... inside a lambda").
- There are MULTIPLE handler instances, so I can't use static pointer fields (which work fine for a singleton handler — a lambda can read a static `int*` field directly with zero indirection).

My first implementation parked the per-instance pointers as FIELDS of a captured managed holder object:
    sealed class Ctx { public int* Addr; public int ALen; public int* DataOut; public int DLen; public Memory Mem; public int Mask; }
    var ctx = new Ctx { ... };
    AddCallback(trigger, () => { ... ReadBits(ctx.Addr, ctx.ALen); v = ctx.Mem.Data[address & ctx.Mask]; ... });
This compiled and was bit-exact, but measured ~0.7% SLOWER than the original managed `int[]`/`byte[]` captured-local version (interleaved-paired A/B). I believe the regression is the extra object-hop indirection: the closure captures the `ctx` reference, then must field-load `ctx.Addr` (and `ctx.Mem.Data` is two hops: ctx -> Mem -> Data), whereas the original captured the `int[] addr` / `byte[] data` references directly as closure display-class fields.

MY CANDIDATE FIX: capture `nint`/`IntPtr` LOCALS (legal to capture — they are integer value types, not pointer types) and cast back to `T*` inside the (unsafe) lambda body:
    nint addrP = (nint)addr; int alen = ...;   // addr is int* from NativeMemory
    nint memDataP = (nint)mem.Data; int mask = ...;
    AddCallback(trigger, () => { ... ReadBits((int*)addrP, alen); v = ((byte*)memDataP)[address & mask]; ... });
My reasoning: each `nint` is captured DIRECTLY as a field of the closure display class (same as capturing any int/ref local), so there's NO extra object-hop like the holder had; the `(int*)`/`(byte*)` casts are free (reinterpret, no memory access); and indexing the raw pointer drops the bounds check the managed array had. So this should be perf-neutral-or-better vs the managed version, and strictly better than the holder.

QUESTIONS:
1. Is capturing `nint`/`IntPtr` locals and casting to `T*` inside an unsafe lambda the idiomatic, lowest-overhead way to use per-instance unmanaged buffers in a captured closure in modern .NET? Is it correct/safe (GC can't move NativeMemory, so the address is stable)?
2. Compared to the managed-holder (`ctx`) approach, will RyuJIT actually generate fewer loads (no second object dereference)? Or does the closure display class end up identical either way?
3. Are there better/cleaner alternatives I'm missing — e.g. a `readonly struct` captured by value (does that even work for closures?), `delegate*<...>` function pointers + an explicit context pointer, a single static array-of-contexts indexed by a captured int, or something else? Rank them by per-call overhead.
4. Any correctness pitfalls with `nint` capture (e.g., if the lambda outlives the allocation — in my case the lifetime is managed: the buffers are freed only when all handlers are torn down together)?

Be concrete about the .NET 10 / RyuJIT codegen implications. Keep it focused on the lowest per-call overhead for a hot callback. Assume I will measure interleaved-paired afterward; I want the best DESIGN to try.
