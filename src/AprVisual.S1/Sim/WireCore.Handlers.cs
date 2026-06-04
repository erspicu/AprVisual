using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

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
        //        a handler watches its cs / /we / addr / data nodes and does a plain array read/write,
        //        driving the data-bus nodes via SetHigh/SetLow.
        //
        //    Ordering note: handlers must be attached (AddCallback adds fake nodes/transistors) BEFORE
        //    WireCore.Reset() — Reset() sizes the hot arrays to the node count *at that point*.

        // ── per-half-cycle work is now inlined into StepCycle (Recalc.cs) ──
        // [H1 2026-06-05] The old generic handler chain (`_handlerChain` Action + AddHandler/RunHandlerChain)
        // only ever held ONE handler — the clock toggle — so it was removed: StepCycle toggles the static
        // ClockNode inline (no delegate invoke / no closure capture). Add a real per-half-cycle hook back
        // here only if a second one is ever needed.

        // ── callbacks (node-change watchers) ──
        internal sealed class CallbackInfo
        {
            public string Name = "";
            public int TargetNode;
            public int[] WatchedNodes = Array.Empty<int>();
            public Action Callback = static () => { };
            public bool Enqueued;
        }

        private static readonly List<CallbackInfo> _callbacks = new();
        // Pending queue (suggest #05): replaces the old InvokeCallbacks two-pass scan over
        // _callbacks. EnqueueCallback now pushes to _pendingCallbacks; InvokeCallbacks
        // returns in O(1) when pending=0 (the common case after most settles).
        private static List<CallbackInfo> _pendingCallbacks = new();
        private static List<CallbackInfo> _processingCallbacks = new();
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
            _callbacks.Clear();
            _pendingCallbacks.Clear();
            _processingCallbacks.Clear();
            _callbackByNode = null;
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
        {
            string name = "callback:" + string.Join(",", watchedNodes.Select(GetNodeName));
            int target = AddNamedNode(name);
            var info = new CallbackInfo { Name = name, TargetNode = target, WatchedNodes = watchedNodes.ToArray(), Callback = cb };
            _callbacks.Add(info);
            foreach (int nn in watchedNodes) AddTransistor("callback", gate: nn, c1: target, c2: Ngnd);
            var node = GetOrCreateNode(target);
            if (node != null) node.Callback = info;
        }

        // Called by ProcessQueue() once the dust settles (see WireCore.Recalc.cs). Common case is
        // no pending — O(1) return. Re-entrant-safe via swap-and-drain: a callback may drive nodes
        // → ProcessQueue → InvokeCallbacks recursively; the new pending list gets drained on the
        // next outer-loop iteration. Enqueued flag still prevents double-queueing a single callback.
        internal static void InvokeCallbacks()
        {
            while (_pendingCallbacks.Count > 0)
            {
                // swap pending ↔ processing (zero-alloc snapshot)
                (_pendingCallbacks, _processingCallbacks) = (_processingCallbacks, _pendingCallbacks);
                for (int i = 0; i < _processingCallbacks.Count; i++)
                {
                    var cb = _processingCallbacks[i];
                    cb.Enqueued = false;
                    cb.Callback();
                }
                _processingCallbacks.Clear();
            }
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
        internal sealed class Memory
        {
            public string Name = "";
            public byte[] Data = Array.Empty<byte>();
            public byte Read(int addr) => Data[addr & (Data.Length - 1)];          // assumes power-of-2 size
            public void Write(int addr, byte v) => Data[addr & (Data.Length - 1)] = v;
            public void Clear() => Array.Clear(Data);
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
            int we = LookupNode(Full("/we"));                                    // ROM: usually absent
            var addrL = new List<int>(); ResolveNodes(Full("a[]"), addrL);
            var dataOutL = new List<int>(); ResolveNodes(Full("_d[7:0]"), dataOutL);
            var dataBusL = new List<int>(); ResolveNodes(Full("d[]"), dataBusL);
            if (cs == EmptyNode || addrL.Count == 0 || dataOutL.Count == 0)
            { Console.Error.WriteLine($"memory handler '{prefix}': missing cs/a[]/_d[7:0]"); return; }

            // Cache as int[] (suggest #06) — the lambda closure captures arrays not List<int>,
            // so ReadBits/WriteBits resolves to the int[] overload (no interface dispatch).
            // (memory handler kept MANAGED — the unmanaged int*/byte* version needed a captured-holder
            //  indirection that measured net-negative; see MD/suggest. Video handler is unmanaged, this isn't.)
            int[] addr = addrL.ToArray();
            int[] dataOut = dataOutL.ToArray();

            var trigger = new List<int> { cs };
            if (we != EmptyNode) trigger.Add(we);
            trigger.AddRange(addrL);
            trigger.AddRange(dataBusL);

            // Specialize callback body by ROM/RAM at attach time (suggest #F2): hoist mem.Data
            // and mask out of the hot path, and split read-only vs read/write into separate
            // closures so the closure body has no attach-time-known branches.
            byte[] data = mem.Data;
            int mask = data.Length - 1;
            bool readOnly = isRom || we == EmptyNode;

            if (readOnly)
            {
                AddCallback(trigger, () =>
                {
                    if (NodeStates[cs] != 0) return;
                    int address = ReadBits(addr);
                    WriteBits(dataOut, data[address & mask]);
                });
            }
            else
            {
                AddCallback(trigger, () =>
                {
                    if (NodeStates[cs] != 0) return;
                    int address = ReadBits(addr);
                    if (NodeStates[we] == 0) data[address & mask] = (byte)ReadBits(dataOut);
                    else                      WriteBits(dataOut, data[address & mask]);
                });
            }
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

            bool prev = false;
            AddCallback(new[] { pclk1 }, () =>
            {
                bool now = NodeStates[pclk1] != 0;
                if (!prev && now)                               // rising edge of the pixel clock
                {
                    int x = ReadBits(_vidHpos, _vidHposLen), y = ReadBits(_vidVpos, _vidVposLen);
                    if ((uint)x < ScreenW && (uint)y < ScreenH && FrameBuffer != null)
                    {
                        int slot = ReadBits(_vidPalPtr, _vidPalPtrLen) & 31;
                        int colour6 = _vidPalRamOk[slot] != 0 ? ReadBits(_vidPalRam + slot * PalRamSlotW, PalRamSlotW) : 0;
                        FrameBuffer[y * ScreenW + x] = _nesPalettePtr[colour6 & 0x3F];
                    }
                }
                prev = now;
            });
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
