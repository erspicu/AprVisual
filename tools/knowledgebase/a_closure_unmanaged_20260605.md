Here is a detailed, performance-focused breakdown of your problem, answers to your questions, and the optimal patterns for .NET 10 / x64.

### 1. Is capturing `nint` and casting to `T*` idiomatic, lowest-overhead, and safe?

**Yes.** For a closure-based architecture, capturing `nint` is the canonical, safe workaround for the C# compiler's restriction against pointer-type capture. 

**Why it is safe:** 
Because you are using `NativeMemory.AlignedAlloc`, the memory is allocated on the unmanaged heap. The Garbage Collector does not know about it, does not track it, and **will never move it**. The memory address is permanently stable. As long as you manually control the lifetime (which you stated you do) and ensure the lambda doesn't execute after the memory is freed, casting back and forth between `nint` and `T*` is 100% safe.

### 2. RyuJIT Codegen: Will it generate fewer loads than the `Ctx` holder?

**Yes, strictly fewer dependent memory loads.**

To understand why your `Ctx` class caused a 0.7% regression and why `nint` fixes it, look at how the C# compiler generates closure display classes:

*   **The `Ctx` approach (Double Indirection):**
    The compiler generates a class containing a reference to your `Ctx` object.
    `this` (Closure instance) -> `ctx` (Heap Object) -> `Addr` (Pointer field).
    *x64 Codegen:*
    ```asm
    mov rcx, [rdi + 8]   ; Load 'ctx' object reference from closure field
    mov rdx, [rcx + 16]  ; Load 'Addr' pointer from ctx (DEPENDENT LOAD)
    mov eax, [rdx]       ; Finally read your unmanaged memory
    ```

*   **The `nint` approach (Single Indirection):**
    The compiler generates a class containing the `nint` fields directly.
    `this` (Closure instance) -> `addrP` (Pointer field).
    *x64 Codegen:*
    ```asm
    mov rdx, [rdi + 8]   ; Load 'addrP' directly from closure field
    mov eax, [rdx]       ; Read your unmanaged memory
    ```

By capturing `nint` locals, you physically inline the pointers into the compiler-generated closure object. You save a dependent memory fetch (`mov rdx, [rcx...]`), which saves CPU pipeline stalls. Furthermore, compared to your original managed `int[]` version, you completely drop the array bounds check and the array base-offset math. **This makes the `nint` capture strictly faster than both.**

### 3. Better/cleaner alternatives (Ranked by Overhead)

While the `nint` closure is the fastest *delegate-based* approach, delegates themselves impose a virtual call overhead (`Invoke()`). If you are simulating millions of nodes per tick, delegate dispatch is a measurable bottleneck.

Here is the ranking of callback architectures from fastest to slowest in .NET 10:

#### Rank 1: Function Pointers (`delegate*`) + Unmanaged Context (The Absolute Fastest)
If you are willing to refactor your `AddCallback` infrastructure, drop `Action`/`Func` entirely. Use C# 9+ function pointers. This eliminates the closure object allocation, eliminates GC pressure, and replaces virtual delegate dispatch with a direct native call.

```csharp
// 1. Define a blittable state struct for each handler instance
unsafe struct HandlerContext {
    public int* Addr;
    public int ALen;
    public byte* MemData;
    public int Mask;
}

// 2. Define your callback list as an array of these tuples:
unsafe struct CallbackEntry {
    public delegate* unmanaged[Cdecl]<HandlerContext*, void> Execute;
    public HandlerContext* Context;
}

// 3. The Handler (static method, no closure!)
[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
static unsafe void MyHandler(HandlerContext* ctx) {
    // Zero overhead access. No classes, no delegates.
    ReadBits(ctx->Addr, ctx->ALen);
    var v = ctx->MemData[address & ctx->Mask];
}
```
*Overhead:* Best possible. No objects. 1 cache-friendly read to get the struct, followed by a direct `call rax`. 

#### Rank 2: `nint` wrapped in a captured `readonly struct`
If you must keep your existing `Action` infrastructure, capturing individual `nint` locals can result in long, messy lambda signatures. You can clean this up by capturing a `readonly struct` by value.

```csharp
// Struct, NOT a class. 
readonly struct FastCtx {
    public readonly nint Addr;
    public readonly int ALen;
    public readonly nint MemData;
    // ...
}

var ctx = new FastCtx { Addr = (nint)addr, MemData = (nint)mem.Data /*...*/ };

// The lambda captures the struct BY VALUE.
AddCallback(trigger, () => {
    // Cast is free. Zero object hops.
    int* pAddr = (int*)ctx.Addr;
    byte* pMem = (byte*)ctx.MemData;
    // ...
});
```
*Overhead:* Identical codegen to capturing raw `nint` locals (Rank 3), but much cleaner code. The compiler embeds the struct directly into the display class. It is `[this + offsetOfStruct + offsetOfField]`, so it retains the single-indirection performance.

#### Rank 3: Capturing individual `nint` locals
(This is your proposed solution).
*Overhead:* Excellent, but gets syntactically noisy if you have more than 2-3 pointers/variables to capture.

#### Rank 4: Captured `Ctx` Class (Your measured regression)
*Overhead:* High due to the extra heap object and dependent load chain.

### 4. Correctness Pitfalls with `nint`

*   **Debugger Experience:** Pointers cast to `nint` show up as integers in the Visual Studio / Rider debugger. You lose the ability to easily view the memory as an array of `int` or `byte` during a breakpoint without setting up explicit watch window casts (e.g., `(int*)addrP`).
*   **Struct Tearing / Concurrency:** Because `nint` is 64-bit on x64, reads/writes are atomic. You don't have to worry about torn reads if callbacks execute concurrently, provided you aren't mutating the captured variables (which you shouldn't be).
*   **Accidental `ref struct` limitations:** Do not try to use C# `ref struct` (like `Span<T>`) or `ref` fields inside the closure. The C# compiler strictly forbids capturing `ref struct`s in lambdas because closures live on the heap. `nint` bypasses this precisely because it is a standard value type.

### Summary Recommendation

For an immediate, high-performance fix that keeps your current lambda architecture: **Use Rank 2 (capture a `readonly struct` of `nint` fields by value)**. It will give you the exact same RyuJIT optimization as capturing raw `nint`s, perfectly dropping the array bounds checks and dependent loads, while keeping your C# code highly readable. 

If you are fighting for the absolute last sub-millisecond in your simulator's main loop: **Refactor to Rank 1 (Unmanaged `delegate*`)**. In switch-level simulators, delegate dispatch overhead frequently dwarfs the actual bit-manipulation logic.