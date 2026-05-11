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
        private static void RunHandlerChain() => _handlerChain?.Invoke();

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

        internal static void ResetHandlers() { _handlerChain = null; _callbacks.Clear(); }

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
        /// Video output (placeholder). TODO (Step 7): watch the PPU's vid_[11:0] composite-ladder value +
        /// the pixel clock, decode each pixel to RGB into FrameBuffer. For now just allocates a black frame.
        /// </summary>
        public static void AttachVideoHandler()
        {
            if (FrameBuffer == null) FrameBuffer = AllocArray<uint>(ScreenW * ScreenH);
            // TODO: AddCallback on ppu.vid_[...] / ppu.pclk → write FrameBuffer[y*256+x].
        }
    }
}
