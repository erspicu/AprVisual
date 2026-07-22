using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Behavioral handlers + the callback mechanism — port of ref/metalnes-main:
        //      handler_clock.h / handler_ram.h / handler_rom.h / handler_video_out.h …
        //      Wires::add_callback / invoke_callbacks / step_cycle's handler chain (wire_module.cpp)
        //    See MD/note/03_系統整合與週期推進.md.
        //
        //    Design (kept from MetalNES, adapted):
        //      - the only per-half-cycle "handler" is the clock toggle; it is INLINED into StepCycle
        //        (Recalc.cs) via the static ClockNode — no delegate (the generic chain was removed, [H1]).
        //      - "callbacks" fire when any watched node changes, *after* propagation settles.
        //        Implemented by adding a fake transistor (gate = watchedNode, c1 = fakeTargetNode,
        //        c2 = Ngnd) per watched node, plus a callback record on the fake target — so the
        //        normal transistor-propagation machinery does the watching for free.
        //      - RAM/ROM are NOT simulated as transistors: a module declares `memory: { name: size }`,
        //        a handler watches its control/address nodes (plus data for writable RAM), does a plain
        //        array read/write, and drives the data-bus nodes via SetHigh/SetLow.
        //
        //    Ordering note: handlers must be attached (AddCallback adds fake nodes/transistors) BEFORE
        //    WireCore.Reset() — Reset() sizes the hot arrays to the node count *at that point*.

        // ── per-half-cycle work is now inlined into StepCycle (Recalc.cs) ──
        // [H1 2026-06-05] The old generic handler chain (`_handlerChain` Action + AddHandler/RunHandlerChain)
        // only ever held ONE handler — the clock toggle — so it was removed: StepCycle toggles the static
        // ClockNode inline (no delegate invoke / no closure capture). Add a real per-half-cycle hook back
        // here only if a second one is ever needed.

        // ── callbacks (node-change watchers) ──
        // [H3] CallbackInfo is now a TYPED handler descriptor: InvokeCallbacks switches on Kind and calls a
        // static handler (no Action delegate / no closure) for the hot kinds (memory / video). The per-instance
        // context lives directly on this object (it IS the dispatch unit — so a field read is one hop, NOT the
        // extra hop the rejected ctx-holder added). Generic Action is kept only for rare/test watchers.
        internal enum HandlerKind : byte { Generic = 0, MemRead, MemReadWrite, Video, MapperLatch, Joypad }

        internal sealed class CallbackInfo
        {
            public string Name = "";
            public int TargetNode;
            public int[] WatchedNodes = Array.Empty<int>();
            public bool Enqueued;
            public HandlerKind Kind;
            public Action? Callback;                       // Generic only

            // memory handler context (MemRead / MemReadWrite) — UNMANAGED (Stage 2). These pointers are
            // handler-lifetime (AllocHandlerArray, freed at rebuild); MemData = the Memory's byte* buffer.
            // Read as cb.Addr/cb.MemData = ONE hop from cb (which InvokeCallbacks already loaded) — no ctx
            // penalty — and drops the managed-array bounds check.
            public int Cs, Oe, We, Mask, ALen, DLen;
            public string DebugName = "";
            // CHR-ROM-only analog feedback guard (test mode). During the impossible-in-binary
            // ALE+read overlap, hold the previous ROM output until either control signal changes.
            public int PpuAle = EmptyNode, PpuRead = EmptyNode;
            public int* Addr;
            public int* DataOut;
            public byte* MemData;

            // mapper context (MemRead with banking / MapperLatch) — CNROM (mapper 3). BankPtr points at a
            // handler-lifetime int holding the current CHR bank offset, pre-shifted (bank << 13). NULL for
            // every non-banked memory: mapper-0 paths never touch it (bit-exactness preserved).
            public int* BankPtr;

            // video handler context (Video)
            public int Pclk1;
            public bool VidPrev;
        }

        private static readonly List<CallbackInfo> _callbacks = new();
        // Pending queue (suggest #05): replaces the old InvokeCallbacks two-pass scan over
        // _callbacks. EnqueueCallback now pushes to _pendingCallbacks; InvokeCallbacks
        // returns in O(1) when pending=0 (the common case after most settles).
        private static List<CallbackInfo> _pendingCallbacks = new();
        private static List<CallbackInfo> _processingCallbacks = new();
        // Test-mode model for the PPU's documented ALE+Read analog feedback window. Set before
        // LoadSystem so AttachRamLikeHandler can include both control nodes in the CHR callback.
        internal static bool PpuAleReadFeedbackShim;
        // M4·P4 MECHANISM: the same load-time trigger add + runtime analog-feedback break, promoted
        // to an opt-in mechanism (env PPU_ALE_FB) that supersedes the shim. Bit-identical code path
        // (the runtime hold at HandleMemRead keys off cb.PpuAle != EmptyNode, set by either flag).
        internal static bool PpuAleReadFeedbackMechEnabled;
        internal static long PpuAleReadFeedbackHoldCount;
        private static long _ppuAleReadFeedbackLastLogTime;
        // Node-id direct lookup (suggest #F4 / A6): _callbackByNode[nn] = the CallbackInfo registered on
        // node nn (null if none). Built in Reset; lets RecalcNode's HasCallback group-walk look the callback
        // up directly instead of jumping into the managed Nodes[] Node object graph.
        // [A6 2026-06-04] NodeCount-sized reference array, NOT a sparse Dictionary. The old code chose the
        // Dictionary on the *assumption* that the array's ~115 KB + gen2 scan wasn't worth it "for ~0 hot-path
        // benefit" — that was never measured and was WRONG: the HasCallback branch fires per-group-member for
        // every group containing a watched bus node (far more often than callbacks actually *fire*), so
        // Dictionary.TryGetValue's hash+probe was real cost. Direct array index measured **+~1.4%**
        // (3 interleaved-paired batches, 49/60 wins, median +1.36/+1.36/+1.46%, bit-exact).
        internal static CallbackInfo?[]? _callbackByNode;

        internal static void ResetHandlers()
        {
            ClockNode = EmptyNode;   // [H1] re-resolved by AttachClockHandler each rebuild
            ResetMapperState();      // CNROM bank register (handler-lifetime, freed below)
            _callbacks.Clear();
            _pendingCallbacks.Clear();
            _processingCallbacks.Clear();
            _callbackByNode = null;
            PpuAleReadFeedbackHoldCount = 0;
            _ppuAleReadFeedbackLastLogTime = long.MinValue;
            // free this composed system's handler-lifetime unmanaged arrays (video node-lists, palette,
            // memory-handler node-lists, behavioral RAM/ROM Data) before the next rebuild re-creates them.
            FreeHandlerArrays();
            _vidHpos = _vidVpos = _vidPalPtr = null; _vidHposLen = _vidVposLen = _vidPalPtrLen = 0;
            _vidPalRam = null; _vidPalRamOk = null; _nesPalettePtr = null;
        }

        /// <summary>
        /// Fire <paramref name="cb"/> (once, after the next settle) whenever any of <paramref name="watchedNodes"/>
        /// changes value. Port of Wires::add_callback — allocates a fake target node, adds a fake
        /// (gate=watched, c1=target, c2=Ngnd) transistor per watched node so the normal recalc machinery
        /// brings the target into a group whenever a watched node flips. MUST be called before Reset().
        /// </summary>
        public static void AddCallback(IReadOnlyList<int> watchedNodes, Action cb)
            => RegisterCallback(watchedNodes, new CallbackInfo { Kind = HandlerKind.Generic, Callback = cb });

        // Shared bookkeeping: allocate the fake target node, add a (gate=watched, c1=target, c2=Ngnd) fake
        // transistor per watched node, and record the (already-Kind-typed) CallbackInfo on the target. The
        // caller fills info.Kind + its context fields. MUST be called before Reset().
        private static void RegisterCallback(IReadOnlyList<int> watchedNodes, CallbackInfo info)
        {
            string name = "callback:" + string.Join(",", watchedNodes.Select(GetNodeName));
            int target = AddNamedNode(name);
            info.Name = name; info.TargetNode = target; info.WatchedNodes = watchedNodes.ToArray();
            _callbacks.Add(info);
            foreach (int nn in watchedNodes) AddTransistor("callback", gate: nn, c1: target, c2: Ngnd);
            var node = GetOrCreateNode(target);
            if (node != null) node.Callback = info;
        }

        // LAE shim's read recorder: while a $BB ember is active (see WireCore.System.cs), every
        // behavioral memory READ is logged here so the shim can recover the operand bytes and the
        // data byte from ground truth instead of guessing bus timing. Off outside the ember; the
        // hot-path cost when idle is one predictable branch per memory read callback.
        internal static bool LaeRecording;
        internal static readonly int[] LaeReadAddr = new int[16];
        internal static readonly int[] LaeReadVal = new int[16];
        internal static int LaeReadCount;
        internal static void LaeRecordRead(int addr, int val)
        {
            if (LaeReadCount < 16) { LaeReadAddr[LaeReadCount] = addr; LaeReadVal[LaeReadCount] = val; LaeReadCount++; }
        }

        private static bool _invoking;   // re-entrancy guard for InvokeCallbacks (see the note below)
#if DEBUG
        internal static long GuardBlockedTotal;   // cumulative nested entries the guard absorbed
        internal static int  GuardBlockedMax;     // max nested entries within a single outermost drain (~old recursion depth)
        private  static int  _guardBlocksThisDrain;
#endif

        // Called by ProcessQueue() once the dust settles (see WireCore.Recalc.cs). Common case is
        // no pending — O(1) return (the Count==0 check below, before any guard bookkeeping).
        //
        // A callback may drive nodes → WriteBits → ProcessQueue → InvokeCallbacks, i.e. this method
        // re-enters itself. The OLD code let the nested call swap the shared _pendingCallbacks /
        // _processingCallbacks lists WHILE the outer for-loop was still iterating _processingCallbacks —
        // which tore the outer loop (re-ran callbacks, scrambled order) and, in intense ROMs like
        // AccuracyCoin, amplified the settle into ~24k-deep recursion that overflowed the stack.
        //
        // Fix: a re-entrancy guard. Only the OUTERMOST InvokeCallbacks drains; a nested entry returns at
        // once, leaving its work in _pendingCallbacks for the outermost while-loop to pick up (it
        // re-checks Count every iteration, so nothing is stranded — single-threaded). ProcessQueue's
        // node-settle still runs synchronously inside WriteBits, so bus values are exact when the next
        // callback reads them; only a *downstream callback* is deferred one drain-iteration instead of
        // running nested — same iterative order the classic event-queue simulators use. Bounded stack.
        internal static void InvokeCallbacks()
        {
            if (_pendingCallbacks.Count == 0) return;   // hot path: nothing pending — no guard cost
            if (_invoking)                              // nested re-entry: the outermost loop will drain
            {
#if DEBUG
                // Each nested entry is a bus-changing callback the OLD code would have recursed into.
                // Their count within ONE outermost drain upper-bounds the recursion depth the no-guard
                // build reached here (~24021 on AccuracyCoin frame 4480). DEBUG-only; not in Release.
                GuardBlockedTotal++;
                if (++_guardBlocksThisDrain > GuardBlockedMax) GuardBlockedMax = _guardBlocksThisDrain;
#endif
                return;
            }
            _invoking = true;
#if DEBUG
            _guardBlocksThisDrain = 0;
#endif
            try
            {
                while (_pendingCallbacks.Count > 0)
                {
                    // swap pending ↔ processing (zero-alloc snapshot). Safe now: no nested call can swap
                    // these out from under the loop below — nested InvokeCallbacks returns immediately.
                    (_pendingCallbacks, _processingCallbacks) = (_processingCallbacks, _pendingCallbacks);
                    for (int i = 0; i < _processingCallbacks.Count; i++)
                    {
                        var cb = _processingCallbacks[i];
                        cb.Enqueued = false;
                        // [H3] typed dispatch — no Action.Invoke for the hot kinds (memory/video).
                        switch (cb.Kind)
                        {
                            case HandlerKind.MemRead:      DoMemRead(cb); break;
                            case HandlerKind.MemReadWrite: DoMemReadWrite(cb); break;
                            case HandlerKind.Video:        DoVideo(cb); break;
                            case HandlerKind.MapperLatch:  DoMapperLatch(cb); break;   // CNROM CHR bank latch
                            case HandlerKind.Joypad:       DoJoypad(cb); break;        // behavioral controller (test mode)
                            default:                       cb.Callback!(); break;   // Generic (rare / test)
                        }
                    }
                    _processingCallbacks.Clear();
                }
            }
            finally { _invoking = false; }
        }

        // ── typed handler bodies (were per-instance closures; now static, dispatched by Kind) ──
        // ROM / read-only RAM: on chip-select active, drive the data-out bus with mem[addr].
        // BankPtr (CNROM CHR banking) adds the pre-shifted bank offset above the module's address lines;
        // it is null for every mapper-0 memory, so the plain path is untouched.
        private static void DoMemRead(CallbackInfo cb)
        {
            if (NodeStates[cb.Cs] != 0) return;
            int address = ReadBits(cb.Addr, cb.ALen);
            // AccuracyCoin deliberately creates a cycle where ALE and external /RD are active
            // together. The real board resolves that ROM -> AD bus -> transparent latch -> ROM
            // loop through analog delay/drive strength. Most overlaps still converge digitally;
            // intervene only when the current ROM page's low-byte mapping enters a nontrivial
            // cycle. Holding every overlap corrupts stable sprite/dummy fetches after dot 255.
            if (cb.PpuAle != EmptyNode && NodeStates[cb.PpuAle] != 0 && NodeStates[cb.PpuRead] == 0
                && HasNonTrivialRomFeedbackCycle(cb, address))
            {
                long hold = ++PpuAleReadFeedbackHoldCount;
                if (hold == 1)
                {
                    _ppuAleReadFeedbackLastLogTime = Time;
                    Console.Error.WriteLine($"# [shim] PPU ALE/read feedback hold at t={Time} X=${ReadReg(R_CpuX):X2} addr=${ReadBits(cb.Addr, cb.ALen):X4}");
                }
                return;
            }
            if (cb.BankPtr != null) address |= *cb.BankPtr;
            WriteBits(cb.DataOut, cb.DLen, cb.MemData[address & cb.Mask]);
            // CPU-bus reads only: with rendering on, CHR fetches hit this path every PPU cycle and
            // flood the 16-entry ring before the operands can land (measured: isolated ROM with
            // rendering off passed while the in-suite run mis-derived the operand pair).
            if (LaeRecording && cb.DebugName != "cart.chr." && cb.DebugName != "u4.")
                LaeRecordRead(address & cb.Mask, cb.MemData[address & cb.Mask]);
        }

        // During ALE, the external octal latch feeds ROM D[7:0] back into A[7:0]. For a fixed
        // upper address this is the finite function low -> ROM[page|low]. Floyd's algorithm
        // distinguishes a stable fixed point from a length>1 cycle without allocating or imposing
        // a callback-dispatch limit. Only the latter cannot settle in the binary simulator.
        private static bool HasNonTrivialRomFeedbackCycle(CallbackInfo cb, int address)
        {
            int effectiveAddress = address | (cb.BankPtr != null ? *cb.BankPtr : 0);
            int page = effectiveAddress & ~0xFF;
            int slow = cb.MemData[(page | (effectiveAddress & 0xFF)) & cb.Mask];
            int fast = cb.MemData[(page | slow) & cb.Mask];

            for (int i = 0; i < 256 && slow != fast; i++)
            {
                slow = cb.MemData[(page | slow) & cb.Mask];
                fast = cb.MemData[(page | fast) & cb.Mask];
                fast = cb.MemData[(page | fast) & cb.Mask];
            }

            return cb.MemData[(page | slow) & cb.Mask] != slow;
        }

        // CNROM (mapper 3) bank latch: a CPU write into PRG space ($8000-$FFFF: /romsel low, R/W low)
        // latches the data bus into the CHR bank register (stored pre-shifted, masked to the bank count).
        // Bus conflicts (AND with ROM byte) are not modeled — same simplification as AprNes Mapper003.
        private static void DoMapperLatch(CallbackInfo cb)
        {
            if (NodeStates[cb.Cs] != 0) return;   // /romsel inactive
            if (NodeStates[cb.We] != 0) return;   // R/W high = read
            *cb.BankPtr = (ReadBits(cb.DataOut, cb.DLen) & cb.Mask) << 13;
        }

        // read/write RAM: /we low ⇒ latch the data bus into mem[addr]; else drive data-out with mem[addr].
        private static void DoMemReadWrite(CallbackInfo cb)
        {
            if (NodeStates[cb.Cs] != 0) return;
            int address = ReadBits(cb.Addr, cb.ALen);
            if (NodeStates[cb.We] == 0) cb.MemData[address & cb.Mask] = (byte)ReadBits(cb.DataOut, cb.DLen);
            else
            {
                WriteBits(cb.DataOut, cb.DLen, cb.MemData[address & cb.Mask]);
                if (LaeRecording && cb.DebugName != "cart.chr." && cb.DebugName != "u4.")
                    LaeRecordRead(address & cb.Mask, cb.MemData[address & cb.Mask]);
            }
        }

        // video: on the pclk1 rising edge, write the visible pixel from palette RAM via the (static, unmanaged)
        // video node-lists + master palette. VidPrev (the rising-edge tracker) is per-instance state on cb.
        private static void DoVideo(CallbackInfo cb)
        {
            bool now = NodeStates[cb.Pclk1] != 0;
            if (!cb.VidPrev && now)
            {
                int x = ReadBits(_vidHpos, _vidHposLen), y = ReadBits(_vidVpos, _vidVposLen);
                if ((uint)x < ScreenW && (uint)y < ScreenH && FrameBuffer != null)
                {
                    int slot = ReadBits(_vidPalPtr, _vidPalPtrLen) & 31;
                    int colour6 = _vidPalRamOk[slot] != 0 ? ReadBits(_vidPalRam + slot * PalRamSlotW, PalRamSlotW) : 0;
                    FrameBuffer[y * ScreenW + x] = _nesPalettePtr[colour6 & 0x3F];
                }
            }
            cb.VidPrev = now;
        }

        internal static void EnqueueCallback(CallbackInfo cb)
        {
            if (!cb.Enqueued) { cb.Enqueued = true; _pendingCallbacks.Add(cb); }
        }

        // Find a previously-AddCallback'd callback by its name (the auto-generated "callback:<watched-node-names>"
        // string) and return its fake target node id. Used by SnapshotExporter to pair memory-handler bindings
        // with the target nodes their callbacks fire from. Returns EmptyNode if not found.
        internal static int FindCallbackTargetByName(string name)
        {
            foreach (var cb in _callbacks) if (cb.Name == name) return cb.TargetNode;
            return EmptyNode;
        }

        // ── behavioral memory ──
        // Data is UNMANAGED (byte*, AllocHandlerArray) — allocated at setupMemory, freed by FreeHandlerArrays
        // at rebuild. Length is the power-of-2 size (mask = Length - 1).
        internal sealed class Memory
        {
            public string Name = "";
            public byte* Data;
            public int Length;
            public byte Read(int addr) => Data[addr & (Length - 1)];               // assumes power-of-2 size
            public void Write(int addr, byte v) => Data[addr & (Length - 1)] = v;
            public void Clear() { if (Data != null) NativeMemory.Clear(Data, (nuint)Length); }
        }

        private static readonly Dictionary<string, Memory> _memories = new(StringComparer.Ordinal);
        public static Memory? ResolveMemory(string name) => _memories.TryGetValue(name, out var m) ? m : null;
        internal static IReadOnlyCollection<string> MemoryNames => _memories.Keys;

        // ── bit-vector helpers (a "register" = an ordered list of nodes; bit i = nodes[i]) ──
        // int[] fast-path overload (suggest #06): handlers cache their node lists as int[]
        // so this path bypasses IReadOnlyList interface dispatch (vtable Count + indexer).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadBits(int[] nodes)
        {
            // #G1 branchless gather: NodeStates ∈ {0,1} (FlagsToState guarantees), so
            // `v |= state << i` matches `if (state != 0) v |= 1 << i` exactly.
            int v = 0;
            for (int i = 0; i < nodes.Length; i++) v |= NodeStates[nodes[i]] << i;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBits(int[] nodes, int value)
        {
            bool changed = false;
            for (int i = 0; i < nodes.Length; i++)
            {
                if ((value & (1 << i)) != 0) changed |= SetHighQueued(nodes[i]);
                else                          changed |= SetLowQueued(nodes[i]);
            }
            if (changed) ProcessQueue();
        }

        // Unmanaged int* overloads — handler node-lists are now unmanaged (AllocHandlerArray), so the
        // hot path indexes raw pointers (no bounds checks, no managed array object).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadBits(int* nodes, int len)
        {
            int v = 0;
            for (int i = 0; i < len; i++) v |= NodeStates[nodes[i]] << i;
            return v;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteBits(int* nodes, int len, int value)
        {
            bool changed = false;
            for (int i = 0; i < len; i++)
            {
                if ((value & (1 << i)) != 0) changed |= SetHighQueued(nodes[i]);
                else                          changed |= SetLowQueued(nodes[i]);
            }
            if (changed) ProcessQueue();
        }

        // Generic IReadOnlyList overloads (List<int> / arrays-as-IReadOnlyList) — used by cold
        // paths (Trace, TestRunner debug dumps, callsites that own a List<int>).
        public static int ReadBits(IReadOnlyList<int> nodes)
        {
            // #G1 branchless gather (matches int[] overload).
            int v = 0;
            for (int i = 0; i < nodes.Count; i++) v |= NodeStates[nodes[i]] << i;
            return v;
        }

        public static void WriteBits(IReadOnlyList<int> nodes, int value)
        {
            bool changed = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                if ((value & (1 << i)) != 0) changed |= SetHighQueued(nodes[i]);
                else                          changed |= SetLowQueued(nodes[i]);
            }
            if (changed) ProcessQueue();
        }

        // "u1.func<ram>" -> "u1." (keep the trailing dot, so CombinePrefix("u1.", "ram") == "u1.ram")
        private static string PrefixOf(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            return dot < 0 ? "" : fullName.Substring(0, dot + 1);
        }

        // ───────────────────────────────────────────────────────────────────────
        //  Handler factories — call these between ComposeSystem() and Reset().
        // ───────────────────────────────────────────────────────────────────────

        /// <summary>The board's master clock: resolve the "clk" node into the static ClockNode; StepCycle
        /// toggles it inline every half-cycle (no handler delegate — see Recalc.cs StepCycle [H1]). Port of handler_clock.</summary>
        public static void AttachClockHandler()
        {
            int clk = LookupNode("clk");
            if (clk == EmptyNode) { Console.Error.WriteLine("AttachClockHandler: no 'clk' node — skipping"); return; }
            ClockNode = clk;   // [H1] StepCycle toggles ClockNode inline (was an AddHandler delegate)
        }

        /// <summary>Attach a behavioral RAM/ROM handler for every module that exposes a "func&lt;ram&gt;" / "func&lt;rom&gt;" hook node. Port of register_handlers&lt;handler_ram&gt;("*func&lt;ram&gt;") etc.</summary>
        public static void AttachMemoryHandlers()
        {
            var ramHooks = new List<int>(); ResolveNodes("*func<ram>", ramHooks);
            foreach (int hook in ramHooks) AttachRamLikeHandler(PrefixOf(GetNodeName(hook)), isRom: false);

            var romHooks = new List<int>(); ResolveNodes("*func<rom>", romHooks);
            foreach (int hook in romHooks) AttachRamLikeHandler(PrefixOf(GetNodeName(hook)), isRom: true);
        }

        private static void AttachRamLikeHandler(string prefix, bool isRom)
        {
            string Full(string n) => CombinePrefix(prefix, n);
            Memory? mem = ResolveMemory(Full("ram")) ?? ResolveMemory(Full("rom"));
            if (mem == null) { Console.Error.WriteLine($"memory handler '{prefix}': no ram/rom region"); return; }

            int cs = LookupNode(Full("cs"));
            int oe = LookupNode(Full("/oe"));
            int we = LookupNode(Full("/we"));                                    // ROM: usually absent
            var addrL = new List<int>(); ResolveNodes(Full("a[]"), addrL);
            var dataOutL = new List<int>(); ResolveNodes(Full("_d[7:0]"), dataOutL);
            var dataBusL = new List<int>(); ResolveNodes(Full("d[]"), dataBusL);
            if (cs == EmptyNode || addrL.Count == 0 || dataOutL.Count == 0)
            { Console.Error.WriteLine($"memory handler '{prefix}': missing cs/a[]/_d[7:0]"); return; }

            // [H3 Stage 2] typed CallbackInfo (no closure) — context lives on the CallbackInfo, dispatched by
            // Kind in InvokeCallbacks → DoMemRead / DoMemReadWrite (static). Node-lists are UNMANAGED int*
            // (handler-lifetime); MemData is the Memory's byte* buffer. cb.Addr/cb.MemData = one hop (no ctx
            // penalty), no managed-array bounds check.
            int alen = addrL.Count, dlen = dataOutL.Count;
            int* addr = AllocHandlerArray<int>(alen);
            for (int i = 0; i < alen; i++) addr[i] = addrL[i];
            int* dataOut = AllocHandlerArray<int>(dlen);
            for (int i = 0; i < dlen; i++) dataOut[i] = dataOutL[i];

            bool readOnly = isRom || we == EmptyNode;
            var trigger = new List<int> { cs };
            if (we != EmptyNode) trigger.Add(we);
            trigger.AddRange(addrL);
            // MetalNES handler_rom watches cs|rw|a[] only. A ROM's output cannot depend on
            // the data bus it drives; watching d[] makes its own output re-enqueue the ROM
            // callback and can form a non-converging loop on the PPU's multiplexed AD bus.
            // RAM keeps d[] because its write path must observe incoming data changes.
            if (!readOnly) trigger.AddRange(dataBusL);

            int ppuAle = EmptyNode, ppuRead = EmptyNode;
            if (readOnly && isRom && prefix == "cart.chr." && (PpuAleReadFeedbackShim || PpuAleReadFeedbackMechEnabled))
            {
                ppuAle = LookupNode("ppu.ale");
                ppuRead = LookupNode("ppu.rd");       // physical /RD: low means external read active
                if (ppuAle == EmptyNode || ppuRead == EmptyNode)
                {
                    Console.Error.WriteLine("# [shim] PPU ALE/read feedback: ppu.ale or ppu.rd unresolved -- disabled");
                    PpuAleReadFeedbackShim = false;
                    PpuAleReadFeedbackMechEnabled = false;
                    ppuAle = ppuRead = EmptyNode;
                }
                else
                {
                    trigger.Add(ppuAle);
                    trigger.Add(ppuRead);
                }
            }

            RegisterCallback(trigger, new CallbackInfo
            {
                Kind = readOnly ? HandlerKind.MemRead : HandlerKind.MemReadWrite,
                DebugName = prefix,
                Cs = cs, Oe = oe, We = we, Mask = mem.Length - 1,
                Addr = addr, ALen = alen, DataOut = dataOut, DLen = dlen, MemData = mem.Data,
                PpuAle = ppuAle, PpuRead = ppuRead,
                // CNROM: the CHR ROM handler gets the shared bank register (null for everything else)
                BankPtr = prefix == "cart.chr." ? _cnromChrBank : null,
            });
        }

        // ── CNROM (mapper 3) — behavioral bank latch, same abstraction level as the ROM handlers ──
        // The cart stays the stock NROM netlist (cart-mmu0 + chrrom wiring); only the behavioral layer
        // changes: the CHR Memory is enlarged to the full CHR size (SetupCnrom, before CopyRomBytes) and
        // the CHR read handler indexes it through *_cnromChrBank (pre-shifted bank << 13). A MapperLatch
        // callback watches the PRG module's bus and latches CPU writes to $8000-$FFFF into the register.
        private static int* _cnromChrBank;   // handler-lifetime; null when the loaded cart isn't CNROM

        /// <summary>Enlarge the CHR Memory to the full banked size and allocate the bank register.
        /// MUST run before CopyRomBytes (which fills the buffer) and AttachMemoryHandlers (which
        /// captures MemData/BankPtr). Per-pass: ResetHandlers frees everything at rebuild.</summary>
        public static void SetupCnrom(int chrRomLength)
        {
            _cnromChrBank = AllocHandlerArray<int>(1);   // zeroed → power-on bank 0
            var chr = ResolveMemory("cart.chr.rom");
            if (chr != null && chr.Length < chrRomLength)
            {
                chr.Data = AllocHandlerArray<byte>(chrRomLength);   // old buffer freed at rebuild with the rest
                chr.Length = chrRomLength;
            }
        }

        /// <summary>Register the CNROM bank-latch callback (call after AttachMemoryHandlers).</summary>
        public static void AttachCnromLatch(int chrBanks)
        {
            int cs = LookupNode("cart.prg.cs");    // = /romsel (active low)
            int rw = LookupNode("cart.prg.rw");    // CPU R/W (0 = write)
            var dataL = new List<int>(); ResolveNodes("cart.prg.d[7:0]", dataL);
            if (cs == EmptyNode || rw == EmptyNode || dataL.Count == 0)
            { Console.Error.WriteLine("AttachCnromLatch: missing cart.prg cs/rw/d[] — CNROM banking disabled"); return; }

            int dlen = dataL.Count;
            int* data = AllocHandlerArray<int>(dlen);
            for (int i = 0; i < dlen; i++) data[i] = dataL[i];

            var trigger = new List<int> { cs, rw };
            trigger.AddRange(dataL);
            RegisterCallback(trigger, new CallbackInfo
            {
                Kind = HandlerKind.MapperLatch,
                Cs = cs, We = rw, Mask = chrBanks - 1,   // We field reused for R/W (same active-low-write sense)
                DataOut = data, DLen = dlen, BankPtr = _cnromChrBank,
            });
        }

        /// <summary>Per-rebuild reset of mapper state (called from ResetHandlers).</summary>
        private static void ResetMapperState() => _cnromChrBank = null;

        // ── Behavioral joypad (test mode; see WireCore.System EnableJoypadHandler) ──────────
        // Implements the controller's CD4021: strobe (port.out) high => continuously reload the
        // shift register from JoyButtons and present bit 0; on each pad-clock falling edge while
        // the port is selected (joy select low) advance to the next bit; after 8 bits present 1
        // (the real 4021 shifts in DS = ground; the LS368 inversion turns that into reads of 1).
        // d0 is driven inverted: the board's LS368 inverts d0 onto the data bus, so a pressed
        // button (bit=1) must present d0=0 to read back as 1.
        public static readonly byte[] JoyButtons = new byte[2];   // bit0=A .. bit7=Right
        private static readonly byte[] _joyShift = new byte[2];
        private static readonly int[] _joyCount = new int[2];
        private static readonly int[] _joyDriven = new int[2];     // last driven d0 value (-1 = force first drive)
        private static readonly bool[] _joyStrobed = new bool[2];  // cold-port rule: reads return 0 until the first strobe
        internal static bool _joyArmed;

        private static void DoJoypad(CallbackInfo cb)
        {
            int pad = cb.Mask;
            bool strobe = NodeStates[cb.Cs] != 0;         // port.out (OUT0), active high
            bool selected = NodeStates[cb.We] == 0;       // cpu.joy1/joy2, active low
            // Advance on the DESELECT edge (read definitively over): the pad clk's falling edge
            // lands mid-read (phi1->phi2), i.e. BEFORE the CPU samples DB at the end of phi2 —
            // advancing there presents the next bit one read early (measured: the post-8 marker
            // appeared on read 8). The select line asserts exactly once per $4016/$4017 read.
            if (strobe) { _joyShift[pad] = JoyButtons[pad]; _joyCount[pad] = 0; _joyStrobed[pad] = true; }
            else if (cb.VidPrev && !selected && _joyCount[pad] < 8) _joyCount[pad]++;
            cb.VidPrev = selected;
            // Cold-port rule: until a pad is strobed for the first time, reads return 0 —
            // matching the reference machine (blargg's cpu_exec_space_apu executes opcode
            // fetches from $4016/$4017 and requires $40 = RTI, i.e. bit0 = 0; an unplugged
            // port's floating LS368 inputs read high, inverted to 0 on the bus).
            int bit = !_joyStrobed[pad] ? 0
                    : _joyCount[pad] < 8 ? (_joyShift[pad] >> _joyCount[pad]) & 1 : 1;
            int d0 = bit ^ 1;                             // LS368 inverts back onto the bus
            // The drive flag is persistent, so re-writing an unchanged value only costs a
            // redundant settle — and polling loops (test_buttons' wait-for-press) invoke this
            // callback thousands of times per frame. Skip when the presented value is unchanged.
            if (d0 == _joyDriven[pad]) return;
            _joyDriven[pad] = d0;
            WriteBits(cb.DataOut, 1, d0);
        }

        /// <summary>Attach the behavioral joypad callbacks (both ports). Test mode only —
        /// requires the nes-pad-behavioral module (EnableJoypadHandler before LoadSystem).</summary>
        public static void AttachJoypadHandler()
        {
            _joyArmed = false;
            for (int pad = 0; pad < 2; pad++)
            {
                int outN = LookupNode($"port{pad}.out");
                int clkN = LookupNode($"port{pad}.clk");
                int d0N  = LookupNode($"port{pad}.d0");
                int selN = LookupNode(pad == 0 ? "cpu.joy1" : "cpu.joy2");
                if (outN == EmptyNode || clkN == EmptyNode || d0N == EmptyNode || selN == EmptyNode)
                { Console.Error.WriteLine($"AttachJoypadHandler: port{pad} nodes unresolved — joypad disabled"); return; }
                int* d0 = AllocHandlerArray<int>(1);
                d0[0] = d0N;
                RegisterCallback(new List<int> { outN, clkN, selN }, new CallbackInfo
                {
                    Kind = HandlerKind.Joypad,
                    Cs = outN, Pclk1 = clkN, We = selN, Mask = pad,
                    DataOut = d0, DLen = 1,
                });
            }
            JoyButtons[0] = JoyButtons[1] = 0;
            _joyShift[0] = _joyShift[1] = 0;
            _joyCount[0] = _joyCount[1] = 8;
            _joyDriven[0] = _joyDriven[1] = -1;
            _joyStrobed[0] = _joyStrobed[1] = false;
            _joyArmed = true;
        }

        /// <summary>
        /// Video output. On each ppu.pclk1 rising edge, for the visible area (hpos &lt; 256, vpos &lt; 240)
        /// write FrameBuffer[y*256+x] = the current pixel's colour: read the 5-bit palette-RAM slot
        /// (ppu.pal_ptr[4:0]), read that slot's 6-bit stored colour (ppu.pal_ram_&lt;slot&gt;_b[5:0]) — exactly
        /// the value handler_palette_ram exposes in MetalNES — and look it up in the 64-colour NES master
        /// palette → ARGB.
        ///
        /// Caveats: not pixel-perfect — the hpos→column mapping may be off by ~1 (pipeline latency, not
        /// corrected), the master palette is the common "2C02 NTSC" RGB table (the real composite
        /// vid_[11:0] ladder + NTSC demodulation would be more accurate, but this is plenty for S1), and
        /// PPU colour emphasis (vid_emph) is ignored. Degrades to a black frame if the nodes don't
        /// resolve. Must be called before Reset() (AddCallback adds nodes); FrameBuffer is allocated by
        /// ResetNes(), and the callback reads the static field at call time.
        /// </summary>
        // Video-handler node-lists, UNMANAGED (there's exactly one video handler, so these are static
        // fields the closure reads directly — no captured-pointer-local problem). Allocated in
        // AttachVideoHandler via AllocHandlerArray, freed by FreeHandlerArrays at rebuild.
        private static int* _vidHpos; private static int _vidHposLen;
        private static int* _vidVpos; private static int _vidVposLen;
        private static int* _vidPalPtr; private static int _vidPalPtrLen;
        private static int* _vidPalRam;     // 32 slots × 6 nodes, flattened (slot s at +s*6)
        private static byte* _vidPalRamOk;  // [32] 1 = slot has the full 6 nodes
        private static uint* _nesPalettePtr; // 64-entry master palette, unmanaged copy of NesPaletteSrc
        private const int PalRamSlotW = 6;

        public static void AttachVideoHandler()
        {
            int pclk1 = LookupNode("ppu.pclk1");
            var hpos = new List<int>();   ResolveNodes("ppu.hpos[8:0]", hpos, quiet: true);
            var vpos = new List<int>();   ResolveNodes("ppu.vpos[8:0]", vpos, quiet: true);
            var palPtr = new List<int>(); ResolveNodes("ppu.pal_ptr[4:0]", palPtr, quiet: true);
            // the 32 palette-RAM entries' stored values (the "b" side of each 6T cell, like handler_palette_ram)
            var palRam = new int[32][];
            int palRamOk = 0;
            for (int i = 0; i < 32; i++)
            {
                var l = new List<int>();
                ResolveNodes($"ppu.pal_ram_{i:X2}_b[5:0]", l, quiet: true);
                palRam[i] = l.ToArray();
                if (l.Count == 6) palRamOk++;
            }
            if (pclk1 == EmptyNode || hpos.Count == 0 || vpos.Count == 0 || palPtr.Count == 0 || palRamOk == 0)
            {
                Console.Error.WriteLine("AttachVideoHandler: missing ppu.pclk1 / hpos / vpos / pal_ptr / pal_ram_* — video output disabled (black frame)");
                return;
            }

            // copy node-lists + master palette into unmanaged (handler-lifetime) storage.
            _vidHposLen = hpos.Count; _vidHpos = AllocHandlerArray<int>(_vidHposLen);
            for (int i = 0; i < _vidHposLen; i++) _vidHpos[i] = hpos[i];
            _vidVposLen = vpos.Count; _vidVpos = AllocHandlerArray<int>(_vidVposLen);
            for (int i = 0; i < _vidVposLen; i++) _vidVpos[i] = vpos[i];
            _vidPalPtrLen = palPtr.Count; _vidPalPtr = AllocHandlerArray<int>(_vidPalPtrLen);
            for (int i = 0; i < _vidPalPtrLen; i++) _vidPalPtr[i] = palPtr[i];
            _vidPalRam = AllocHandlerArray<int>(32 * PalRamSlotW);
            _vidPalRamOk = AllocHandlerArray<byte>(32);
            for (int s = 0; s < 32; s++)
            {
                if (palRam[s].Length == PalRamSlotW)
                {
                    _vidPalRamOk[s] = 1;
                    for (int k = 0; k < PalRamSlotW; k++) _vidPalRam[s * PalRamSlotW + k] = palRam[s][k];
                }
            }
            _nesPalettePtr = AllocHandlerArray<uint>(NesPaletteSrc.Length);
            for (int i = 0; i < NesPaletteSrc.Length; i++) _nesPalettePtr[i] = NesPaletteSrc[i];

            // [H3] typed CallbackInfo (no closure) — dispatched by Kind in InvokeCallbacks → DoVideo (static).
            // The node-lists/palette are the static unmanaged fields above; only the rising-edge tracker
            // (VidPrev) + Pclk1 are per-instance state on the CallbackInfo.
            RegisterCallback(new[] { pclk1 }, new CallbackInfo { Kind = HandlerKind.Video, Pclk1 = pclk1 });
        }

        // The 64-colour NES master palette (common "2C02 NTSC" RGB approximation). ARGB 0x00RRGGBB.
        // Indices 0x0D/0x0E/0x0F/0x1D-0x1F/0x2D-0x2F/0x3D-0x3F are "blacker than black" → black.
        // Managed SOURCE table; AttachVideoHandler copies it into the unmanaged _nesPalettePtr for the hot read.
        private static readonly uint[] NesPaletteSrc =
        [
            0x666666, 0x002A88, 0x1412A7, 0x3B00A4, 0x5C007E, 0x6E0040, 0x6C0600, 0x561D00,
            0x333500, 0x0B4800, 0x005200, 0x004F08, 0x00404D, 0x000000, 0x000000, 0x000000,
            0xADADAD, 0x155FD9, 0x4240FF, 0x7527FE, 0xA01ACC, 0xB71E7B, 0xB53120, 0x994E00,
            0x6B6D00, 0x388700, 0x0C9300, 0x008F32, 0x007C8D, 0x000000, 0x000000, 0x000000,
            0xFFFEFF, 0x64B0FF, 0x9290FF, 0xC676FF, 0xF36AFF, 0xFE6ECC, 0xFE8170, 0xEA9E22,
            0xBCBE00, 0x88D800, 0x5CE430, 0x45E082, 0x48CDDE, 0x4F4F4F, 0x000000, 0x000000,
            0xFFFEFF, 0xC0DFFF, 0xD3D2FF, 0xE8C8FF, 0xFBC2FF, 0xFEC4EA, 0xFECCC5, 0xF7D8A5,
            0xE4E594, 0xCFEF96, 0xBDF4AB, 0xB3F3CC, 0xB5EBF2, 0xB8B8B8, 0x000000, 0x000000,
        ];
    }
}
