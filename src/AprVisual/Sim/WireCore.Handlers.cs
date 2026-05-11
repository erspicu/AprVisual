using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Behavioral handlers + the callback mechanism — port of ref/metalnes-main:
        //      handler_clock.h / handler_ram.h / handler_rom.h / handler_video_out.h ...
        //      Wires::add_callback / invoke_callbacks / step_cycle's handler chain (wire_module.cpp)
        //    See MD/note/03_系統整合與週期推進.md.
        //
        //    Design (kept from MetalNES):
        //      - "handlers" run once per half-cycle, chained into one delegate (RunHandlerChain).
        //        e.g. the clock handler toggles the master clock node.
        //      - "callbacks" fire when any watched node changes, *after* propagation settles.
        //        Implemented by adding a fake transistor (gate = watchedNode, c1 = fakeTargetNode,
        //        c2 = Ngnd) per watched node, and a callback record on the fake target — so the
        //        normal transistor-propagation machinery does the watching for free.
        //      - RAM/ROM are NOT simulated as transistors: a module declares `memory: { name: size }`,
        //        a handler watches its cs / /we / addr / data nodes and does a plain array read/write,
        //        driving the data-bus nodes via SetHigh/SetLow on the bit nodes.

        // ── per-half-cycle handler chain ──
        private static Action? _handlerChain;

        public static void AddHandler(Action h)
            => _handlerChain = _handlerChain is null ? h : _handlerChain + h;

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

        /// <summary>
        /// Register <paramref name="cb"/> to fire (once, after the next settle) whenever any of
        /// <paramref name="watchedNodes"/> changes value. TODO: port Wires::add_callback —
        /// allocate a fake target node, add a fake (gate=watched, c1=target, c2=Ngnd) transistor
        /// per watched node, set NodeFlags.HasCallback on the target.
        /// </summary>
        public static void AddCallback(IReadOnlyList<int> watchedNodes, Action cb)
        {
            throw new NotImplementedException("WireCore.AddCallback — port Wires::add_callback");
        }

        // Called by ProcessQueue() once the dust settles (see WireCore.Recalc.cs).
        internal static void InvokeCallbacks()
        {
            // TODO: port invoke_callbacks — for each enqueued CallbackInfo: cb.Callback(); cb.Enqueued = false;
            foreach (var cb in _callbacks)
            {
                if (!cb.Enqueued) continue;
                cb.Enqueued = false;
                cb.Callback();
            }
        }

        internal static void EnqueueCallback(CallbackInfo cb)
        {
            if (cb.Enqueued) return;
            cb.Enqueued = true;
        }

        // ── memory (behavioral) ──
        internal sealed class Memory
        {
            public string Name = "";
            public byte[] Data = Array.Empty<byte>();
            public byte Read(int addr) => Data[addr];
            public void Write(int addr, byte v) => Data[addr] = v;
        }

        private static readonly Dictionary<string, Memory> _memories = new();
        public static Memory? ResolveMemory(string name) => _memories.TryGetValue(name, out var m) ? m : null;

        // ── handler factories (stubs) ──
        // TODO: ClockHandler — AddHandler(() => ToggleClock());
        // TODO: RamHandler / RomHandler — AddCallback({cs, /we, addr[], data[]}, () => DoMemoryAccess());
        // TODO: VideoOutHandler — watch ppu vid_[]/pclk, write decoded RGB into FrameBuffer;
        //       AudioOutHandler — watch cpu snd1/snd2 (S1: optional, can skip audio entirely first).
        public static void AttachClockHandler() => throw new NotImplementedException("WireCore.AttachClockHandler");
        public static void AttachMemoryHandlers() => throw new NotImplementedException("WireCore.AttachMemoryHandlers");
        public static void AttachVideoHandler() => throw new NotImplementedException("WireCore.AttachVideoHandler");
    }
}
