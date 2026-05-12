using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Behavioral handlers + the callback mechanism — port of ref/metalnes-main:
        //      handler_clock.h / handler_ram.h / handler_rom.h / handler_video_out.h …
        //      Wires::add_callback / invoke_callbacks / step_cycle's handler chain (wire_module.cpp)
        //    See MD/note/03_系統整合與週期推進.md.
        //
        //    Design (kept from MetalNES):
        //      - "handlers" run once per half-cycle, chained into one delegate (RunHandlerChain).
        //        e.g. the clock handler toggles the master clock node.
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

        // ── per-half-cycle handler chain ──
        private static Action? _handlerChain;
        public static void AddHandler(Action h) => _handlerChain = _handlerChain is null ? h : _handlerChain + h;
        internal static void RunHandlerChain() => _handlerChain?.Invoke();

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

        // Nodes a behavioral handler drives via WriteBits (the RAM/ROM data-out pins). Analysis-only —
        // never read by the runtime; S2.1's BusResolver.RefineBuses uses it to find the true bidirectional
        // bus nodes (netlist-driven ∩ handler-driven). Populated by AttachRamLikeHandler; cleared on reset.
        internal static readonly HashSet<int> HandlerWrittenNodes = new();

        internal static void ResetHandlers() { _handlerChain = null; _callbacks.Clear(); HandlerWrittenNodes.Clear(); }

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

        // Called by ProcessQueue() once the dust settles (see WireCore.Recalc.cs). Re-entrant-safe:
        // a callback may itself drive nodes, which recurses into ProcessQueue → InvokeCallbacks; the
        // Enqueued flag stops a callback from being double-queued. Snapshot the list since a callback
        // could (in theory) add another.
        internal static void InvokeCallbacks()
        {
            // fast path: nothing pending
            bool any = false;
            foreach (var cb in _callbacks) if (cb.Enqueued) { any = true; break; }
            if (!any) return;

            for (int i = 0; i < _callbacks.Count; i++)
            {
                var cb = _callbacks[i];
                if (!cb.Enqueued) continue;
                cb.Enqueued = false;
                cb.Callback();
            }
        }

        internal static void EnqueueCallback(CallbackInfo cb) { cb.Enqueued = true; }

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
        public static int ReadBits(IReadOnlyList<int> nodes)
        {
            int v = 0;
            for (int i = 0; i < nodes.Count; i++) if (NodeStates[nodes[i]] != 0) v |= 1 << i;
            return v;
        }

        // Drive each bit high/low. Note: like MetalNES, this is a *persistent* drive (sets SetHigh/SetLow
        // flags); the data bus is released implicitly when the chip's select line deasserts (its internal
        // pass transistors disconnect the bus). Each SetHigh/SetLow re-settles the network.
        public static void WriteBits(IReadOnlyList<int> nodes, int value)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if ((value & (1 << i)) != 0) SetHigh(nodes[i]); else SetLow(nodes[i]);
            }
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

        /// <summary>The board's master clock: toggle the "clk" node every half-cycle. Port of handler_clock.</summary>
        public static void AttachClockHandler()
        {
            int clk = LookupNode("clk");
            if (clk == EmptyNode) { Console.Error.WriteLine("AttachClockHandler: no 'clk' node — skipping"); return; }
            AddHandler(() => { if (NodeStates[clk] != 0) SetLow(clk); else SetHigh(clk); });
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
            var addr = new List<int>(); ResolveNodes(Full("a[]"), addr);
            var dataOut = new List<int>(); ResolveNodes(Full("_d[7:0]"), dataOut);   // internal data bus the handler drives
            var dataBus = new List<int>(); ResolveNodes(Full("d[]"), dataBus);       // external data pins (for the trigger)
            if (cs == EmptyNode || addr.Count == 0 || dataOut.Count == 0)
            { Console.Error.WriteLine($"memory handler '{prefix}': missing cs/a[]/_d[7:0]"); return; }
            foreach (int id in dataOut) HandlerWrittenNodes.Add(id);                  // analysis-only; see HandlerWrittenNodes

            var trigger = new List<int> { cs };
            if (we != EmptyNode) trigger.Add(we);
            trigger.AddRange(addr);
            trigger.AddRange(dataBus);

            AddCallback(trigger, () =>
            {
                if (NodeStates[cs] != 0) return;                                  // chip not selected (cs active-low)
                int address = ReadBits(addr);
                bool writing = !isRom && we != EmptyNode && NodeStates[we] == 0;  // /we low ⇒ write
                if (writing) mem.Write(address, (byte)ReadBits(dataOut));
                else         WriteBits(dataOut, mem.Read(address));
            });
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

            int[] hN = hpos.ToArray(), vN = vpos.ToArray(), pN = palPtr.ToArray();
            bool prev = false;
            AddCallback(new[] { pclk1 }, () =>
            {
                bool now = NodeStates[pclk1] != 0;
                if (!prev && now)                               // rising edge of the pixel clock
                {
                    int x = ReadBits(hN), y = ReadBits(vN);
                    if ((uint)x < ScreenW && (uint)y < ScreenH && FrameBuffer != null)
                    {
                        int slot = ReadBits(pN) & 31;
                        int colour6 = palRam[slot].Length == 6 ? ReadBits(palRam[slot]) : 0;
                        FrameBuffer[y * ScreenW + x] = NesPalette[colour6 & 0x3F];
                    }
                }
                prev = now;
            });
        }

        // The 64-colour NES master palette (common "2C02 NTSC" RGB approximation). ARGB 0x00RRGGBB.
        // Indices 0x0D/0x0E/0x0F/0x1D-0x1F/0x2D-0x2F/0x3D-0x3F are "blacker than black" → black.
        private static readonly uint[] NesPalette =
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
