// SnapshotExporter — dump the WireCore runtime state post-LoadSystem (parsed .js modules + Reset)
// to a binary blob so a separate process (the Rust PoC port) can load the exact same state and
// run bench-hc. Bypasses the need to re-port the .js parser / module composer into Rust.
//
// Format: see experiment/rust-poc/src/snapshot.rs for the matching loader.
//
// Usage:
//   dotnet run -c Release -- --benchmark <rom> --export-snapshot <out.bin>
//
// Only call after WireCore.LoadSystem(rom) has run (NodeCount/TlistLen are populated, all
// handlers attached, all memory regions present, ResetNes has run so NodeStates is settled).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AprVisual.Sim;

namespace AprVisual.Test
{
    internal static unsafe class SnapshotExporter
    {
        // Magic + version. Bump version if format changes.
        const string MAGIC = "APRSNAP\0";   // 8 bytes
        const uint VERSION = 4;   // v4: appended LUT chip specs after chip_id (empty when --lut-ttl not active)

        public static int Export(string outPath, string romPath)
        {
            var rom = AprVisual.Rom.NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }

            // Important: do NOT enable any optimization flags. Snapshot is the raw S1 state.
            WireCore.EnableFastPath = false;
            WireCore.EnablePruneMerge = false;
            WireCore.EnableIrInterp = false;
            WireCore.EnableCodegenDispatcher = false;
            WireCore.EnableChipDiag = true;     // force chip classification so v3 chip_id field is populated
            WireCore.ResetLutChips();
            WireCore.LoadSystem(rom);

            try
            {
                using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
                using var bw = new BinaryWriter(fs);

                // ── header ──
                bw.Write(Encoding.ASCII.GetBytes(MAGIC));
                bw.Write(VERSION);
                bw.Write(WireCore.NodeCount);
                bw.Write(WireCore.TransistorListLength);
                bw.Write(WireCore.Npwr);
                bw.Write(WireCore.Ngnd);
                bw.Write(WireCore.LookupNode("clk"));
                bw.Write(WireCore.LookupNode("res"));
                bw.Write(WireCore.LookupNode("ppu.in_vblank"));

                // ── NodeStates ──
                for (int i = 0; i < WireCore.NodeCount; i++) bw.Write(WireCore.NodeStates[i]);

                // ── NodeInfos: pack (Flags, Connections, TlistGates, TlistC1c2s, TlistC1gnd, TlistC1pwr) ──
                // Connections + TlistGates now live in split arrays NodeConnections / NodeTlistGates;
                // pull them in so the v4 wire format stays identical to the Rust loader's expectation.
                for (int i = 0; i < WireCore.NodeCount; i++)
                {
                    ref var ns = ref WireCore.NodeInfos[i];
                    bw.Write((int)ns.Flags);
                    bw.Write(WireCore.NodeConnections[i]);
                    bw.Write(WireCore.NodeTlistGates[i]);
                    bw.Write(ns.TlistC1c2s);
                    bw.Write(ns.TlistC1gnd);
                    bw.Write(ns.TlistC1pwr);
                }

                // ── TransistorList ──
                // Widen ushort → int to preserve v4 wire format (Rust port reads int per entry).
                for (int i = 0; i < WireCore.TransistorListLength; i++) bw.Write((int)WireCore.TransistorList[i]);

                // ── FlagsToState ──
                for (int i = 0; i < 256; i++) bw.Write(WireCore.FlagsToState[i]);

                // ── Memories + their associated handlers ──
                // Re-discover the memory-handler configurations the way AttachMemoryHandlers does it
                // (so we know cs/we/addr/data-out node ids per memory). We collect both the memory
                // data and the handler binding here to keep the format self-contained.

                var memList = new List<(string Name, byte[] Data)>();
                var handlers = new List<MemHandlerSpec>();

                CollectMemHandlers(memList, handlers, isRom: false, hookPattern: "*func<ram>");
                CollectMemHandlers(memList, handlers, isRom: true,  hookPattern: "*func<rom>");

                // Memories section
                bw.Write(memList.Count);
                foreach (var (name, data) in memList)
                {
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    bw.Write(nameBytes.Length);
                    bw.Write(nameBytes);
                    bw.Write(data.Length);
                    bw.Write(data);
                }

                // Handlers section
                bw.Write(handlers.Count);
                foreach (var h in handlers)
                {
                    bw.Write((byte)(h.IsRom ? 1 : 0));
                    bw.Write(h.MemoryIndex);
                    bw.Write(h.Cs);
                    bw.Write(h.We);
                    bw.Write(h.Target);
                    bw.Write(h.Addr.Length);
                    foreach (int n in h.Addr) bw.Write(n);
                    bw.Write(h.DataOut.Length);
                    foreach (int n in h.DataOut) bw.Write(n);
                }

                // ── v2: video output node ids ──
                int pclk1 = WireCore.LookupNode("ppu.pclk1");
                var hpos = new List<int>();   WireCore.ResolveNodes("ppu.hpos[8:0]",   hpos,   quiet: true);
                var vpos = new List<int>();   WireCore.ResolveNodes("ppu.vpos[8:0]",   vpos,   quiet: true);
                var palPtr = new List<int>(); WireCore.ResolveNodes("ppu.pal_ptr[4:0]", palPtr, quiet: true);

                bw.Write(pclk1);
                bw.Write(hpos.Count);   foreach (int n in hpos)   bw.Write(n);
                bw.Write(vpos.Count);   foreach (int n in vpos)   bw.Write(n);
                bw.Write(palPtr.Count); foreach (int n in palPtr) bw.Write(n);

                // 32 palette entries, each with up to 6 bit nodes (b5..b0 stored as b0..b5 here)
                int palCount = 0;
                var palNodes = new List<List<int>>();
                for (int i = 0; i < 32; i++)
                {
                    var bits = new List<int>();
                    WireCore.ResolveNodes($"ppu.pal_ram_{i:X2}_b[5:0]", bits, quiet: true);
                    palNodes.Add(bits);
                    if (bits.Count == 6) palCount++;
                }
                bw.Write(palNodes.Count);
                foreach (var bits in palNodes)
                {
                    bw.Write(bits.Count);
                    foreach (int n in bits) bw.Write(n);
                }

                // ── v3: per-node chip_id (0=CPU, 1=PPU, 2=OTHER) — for parallel settle ──
                if (WireCore.NodeChip == null)
                {
                    Console.Error.WriteLine("NodeChip not populated — chip classification failed");
                    return 2;
                }
                int cpu = 0, ppu = 0;
                for (int i = 0; i < WireCore.NodeCount; i++)
                {
                    byte c = WireCore.NodeChip[i];
                    bw.Write(c);
                    if (c == WireCore.CHIP_CPU) cpu++; else if (c == WireCore.CHIP_PPU) ppu++;
                }
                Console.WriteLine($"# snapshot v3: chip_id — CPU {cpu:N0} PPU {ppu:N0} OTHER {WireCore.NodeCount - cpu - ppu:N0}");

                // ── v4: LUT chip specs (empty list when --lut-ttl not active) ──
                var lutSpecs = WireCore.RegisteredLutChips;
                bw.Write(lutSpecs.Count);
                foreach (var s in lutSpecs)
                {
                    bw.Write((byte)s.Type);
                    bw.Write(s.TargetNode);
                    bw.Write(s.OeNode);
                    bw.Write(s.Inputs.Length);
                    foreach (int n in s.Inputs) bw.Write(n);
                    bw.Write(s.Outputs.Length);
                    foreach (int n in s.Outputs) bw.Write(n);
                }
                Console.WriteLine($"# snapshot v4: LUT chip specs = {lutSpecs.Count} sub-callbacks");

                fs.Flush();
                Console.WriteLine($"# snapshot: {WireCore.NodeCount:N0} nodes, {WireCore.TransistorListLength:N0} tlist entries, {memList.Count} memories, {handlers.Count} mem-handlers → {outPath} ({fs.Length:N0} bytes)");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        struct MemHandlerSpec
        {
            public bool IsRom;
            public int MemoryIndex;
            public int Cs, We, Target;
            public int[] Addr;
            public int[] DataOut;
        }

        // The memory handler attached by AttachRamLikeHandler creates a callback whose target node
        // is the AddCallback's freshly-allocated fake node. We need the SAME target id used by C#'s
        // handler so the Rust side can dispatch on it. We can't recover it from outside the
        // handler closure, so we reproduce the lookup logic and pair it with the callback target
        // by querying the CallbackInfo list via reflection... actually simpler: we re-run the same
        // node lookups, and we find the target by matching the callback Name shape.
        //
        // The callback's Name is "callback:<watched-node-names-joined>". For a mem handler the
        // watched set is {cs, [we], addr[0..N], dataBus[0..N]} in that exact order. We construct
        // the expected Name and look it up in CallbackInfoNames.
        private static void CollectMemHandlers(List<(string Name, byte[] Data)> memList, List<MemHandlerSpec> handlers, bool isRom, string hookPattern)
        {
            var hooks = new List<int>();
            WireCore.ResolveNodes(hookPattern, hooks);
            foreach (int hook in hooks)
            {
                string full = WireCore.GetNodeName(hook);
                string prefix = PrefixOf(full);
                var mem = WireCore.ResolveMemory(prefix + "ram") ?? WireCore.ResolveMemory(prefix + "rom");
                if (mem == null) continue;

                int cs = WireCore.LookupNode(prefix + "cs");
                int we = WireCore.LookupNode(prefix + "/we");
                var addr = new List<int>();    WireCore.ResolveNodes(prefix + "a[]", addr);
                var dataOut = new List<int>(); WireCore.ResolveNodes(prefix + "_d[7:0]", dataOut);
                var dataBus = new List<int>(); WireCore.ResolveNodes(prefix + "d[]", dataBus);
                if (cs == WireCore.EmptyNode || addr.Count == 0 || dataOut.Count == 0) continue;

                int memIdx = memList.Count;
                memList.Add((mem.Name, (byte[])mem.Data.Clone()));

                // Find the target node for this handler by matching the callback Name.
                var watchedForName = new List<int> { cs };
                if (we != WireCore.EmptyNode) watchedForName.Add(we);
                watchedForName.AddRange(addr);
                watchedForName.AddRange(dataBus);
                string expectedName = "callback:" + string.Join(",", watchedForName.ConvertAll(WireCore.GetNodeName));
                int target = WireCore.FindCallbackTargetByName(expectedName);

                handlers.Add(new MemHandlerSpec
                {
                    IsRom = isRom,
                    MemoryIndex = memIdx,
                    Cs = cs,
                    We = we == WireCore.EmptyNode ? -1 : we,
                    Target = target,
                    Addr = addr.ToArray(),
                    DataOut = dataOut.ToArray(),
                });
            }
        }

        private static string PrefixOf(string fullName)
        {
            int dot = fullName.LastIndexOf('.');
            return dot < 0 ? "" : fullName.Substring(0, dot + 1);
        }
    }
}
