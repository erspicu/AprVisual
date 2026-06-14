using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AprVisual.Sim
{
    // ── Bare-CPU raw visual6502 netlist loader + pin-level bench harness (AprVisual.etc only) ──
    //
    // Loads a single non-NES CPU directly from raw visual6502 files (segdefs.js / transdefs.js /
    // nodenames.js — NOT MetalNES `var module={}` wrappers) and drives it the visual6502 way:
    // power-on reset, then halfStep = toggle the clock + feed a flat memory[] NOP-sled through the
    // rw/ab/db (or z80 _rd/_mreq/_wr) pins. This mirrors ref/visual6502-master/macros.js (6502) and
    // chip-6800 / chip-z80 support.js, so AprVisual's engine runs the SAME workload as
    // tools/visual6502-node/bench.js — a direct "our algorithm vs the original JS" hc/s comparison.
    //
    // Reuses the existing parser primitives (LoadExternalArray + ReadSegDef/ReadTransDef) which
    // already understand the raw array format; the only new work is building one flat ModuleDef,
    // mapping the chip's supply names to vcc/vss, and the visual6502 pin-driving sequences.
    internal static unsafe partial class WireCore
    {
        internal sealed class RawCpuConfig
        {
            public string Name = "";
            public string Vss = "vss", Vcc = "vcc";
            public string Clock = "clk0";   // root for the locality-BFS renumber key (per-chip main clock)
            public int Nop;
            public bool SkipWeak;   // 6800 / z80 ignore the weak (7th-column) transistors — match visual6502
        }

        internal static readonly Dictionary<string, RawCpuConfig> RawCpus = new(StringComparer.OrdinalIgnoreCase)
        {
            ["6502"] = new RawCpuConfig { Name = "6502", Vss = "vss", Vcc = "vcc", Clock = "clk0", Nop = 0xEA, SkipWeak = false },
            ["6800"] = new RawCpuConfig { Name = "6800", Vss = "gnd", Vcc = "vcc", Clock = "phi2", Nop = 0x01, SkipWeak = true },
            ["z80"]  = new RawCpuConfig { Name = "z80",  Vss = "vss", Vcc = "vcc", Clock = "clk",  Nop = 0x00, SkipWeak = true },
        };

        // class-major renumber (range-prune + BFS-locality key); --no-renumber for an A/B
        public static bool RawRenumber = true;

        private static RawCpuConfig _rawCfg = null!;
        private static int[] _abNodes = Array.Empty<int>();
        private static int[] _dbNodes = Array.Empty<int>();
        private static byte[] _rawMem = Array.Empty<byte>();
        // cached pin node ids (resolved at load; EmptyNode if the chip lacks one)
        private static int _pRw, _pClk0, _pClk, _pPhi1, _pPhi2, _pDbe, _pRd, _pMreq, _pWr, _pM1, _pIorq;
        private static Action _rawHalfStep = null!;

        // ───────────────────────────── load ─────────────────────────────

        private static int _rawSkippedWeak;

        public static void LoadRawCpu(string dir, RawCpuConfig cfg)
        {
            _rawCfg = cfg;
            if (RawRenumber)
            {
                // PASS 0 — classify prune bits under identity ids (no renumber yet).
                BuildRawNetlist(dir, cfg);
                Reset();
                CapturePruneClasses();           // PendingClassBits = StashedClassBits

                // FINAL PASS — rebuild, apply class-major renumber + a BFS-from-clock locality key.
                // (The NES self-capture first-touch key needs ClockNode + callback-fed bus, which the
                //  bare-CPU harness doesn't use; the BFS key is the same one the Rust port ships.)
                BuildRawNetlist(dir, cfg);
                BuildRawLocalityOrder(cfg);       // PendingLocalityOrder (identity ids)
                PendingClassBits = StashedClassBits;
                ApplyRenumber();                  // consumes class bits + locality order; sets range-prune blocks
            }
            else
            {
                BuildRawNetlist(dir, cfg);
            }
            FinishRawLoad(cfg);
        }

        // parse the three raw .js files into a flat ModuleDef (no global state touched, no instancing).
        // Shared by BuildRawNetlist (our engine) and NaiveSim (the C# port of the original algorithm).
        internal static ModuleDef ParseRawModuleDef(string dir, string name)
        {
            var def = new ModuleDef { Name = name, Path = dir };
            LoadExternalArray(Path.Combine(dir, "nodenames.js"), r => r.ReadObject((k, nr) => def.NodeNames[k] = nr.ReadInt()), "nodenames");
            LoadExternalArray(Path.Combine(dir, "segdefs.js"),   r => r.ReadArray(ar => def.Segs.Add(ReadSegDef(ar))),  "segdefs");
            LoadExternalArray(Path.Combine(dir, "transdefs.js"), r => r.ReadArray(ar => def.Trans.Add(ReadTransDef(ar))), "transdefs");
            return def;
        }

        // compose only: parse the raw files + instantiate + lower (deterministic ids — safe to repeat)
        private static void BuildRawNetlist(string dir, RawCpuConfig cfg)
        {
            ResetBuild();   // registers Npwr=vcc / Ngnd=vss

            var def = ParseRawModuleDef(dir, cfg.Name);

            // AddInstance folds the locals named "vss"/"vcc" onto Ngnd/Npwr. If this chip names its
            // supplies differently (6800 = gnd), alias them so the fold still happens.
            if (cfg.Vss != "vss" && def.NodeNames.TryGetValue(cfg.Vss, out int gnd)) def.NodeNames["vss"] = gnd;
            if (cfg.Vcc != "vcc" && def.NodeNames.TryGetValue(cfg.Vcc, out int pwr)) def.NodeNames["vcc"] = pwr;

            int weak = 0;
            if (cfg.SkipWeak) weak = def.Trans.RemoveAll(t => t.IsWeak);   // visual6502 6800/z80 drop weak devices
            _rawSkippedWeak = weak;

            AddInstance(def, "");
            if (EnableLowering) LowerNetlist(); else LastLowerStats = "(lowering disabled — --no-lower)";
        }

        // post-build: power on + resolve the bus/clock pins + NOP-sled memory + half-step dispatch
        private static void FinishRawLoad(RawCpuConfig cfg)
        {
            Reset();              // alloc hot arrays + power-on state + build fast-path / P-2/3/4 prune masks
            RecomputeAllNodes();  // settle the raw power-on state (perm-ordered if renumbered)

            _abNodes = ResolveBusNodes("ab", 16);
            _dbNodes = ResolveBusNodes("db", 8);
            _rawMem = new byte[65536];
            for (int i = 0; i < _rawMem.Length; i++) _rawMem[i] = (byte)cfg.Nop;   // Infinite NOP Sled

            _pRw   = LookupNode("rw");
            _pClk0 = LookupNode("clk0");
            _pClk  = LookupNode("clk");
            _pPhi1 = LookupNode("phi1");
            _pPhi2 = LookupNode("phi2");
            _pDbe  = LookupNode("dbe");
            _pRd   = LookupNode("_rd");
            _pMreq = LookupNode("_mreq");
            _pWr   = LookupNode("_wr");
            _pM1   = LookupNode("_m1");
            _pIorq = LookupNode("_iorq");

            _rawHalfStep = cfg.Name switch
            {
                "6800" => HalfStep6800,
                "z80"  => HalfStepZ80,
                _      => HalfStep6502,
            };
        }

        // Locality key for the class-major renumber: BFS from the chip's main clock along the
        // gate→channel-endpoint edges (the same signal-flow edges SetNodeState enqueues through),
        // producing a per-identity-id first-reach order. ApplyRenumber consumes PendingLocalityOrder.
        private static void BuildRawLocalityOrder(RawCpuConfig cfg)
        {
            int n = NodeArrayCount;
            var order = new uint[n];
            for (int i = 0; i < n; i++) order[i] = uint.MaxValue;
            int clk = LookupNode(cfg.Clock);
            if (clk == EmptyNode) { PendingLocalityOrder = order; return; }

            var q = new Queue<int>();
            var seen = new bool[n];
            uint seq = 0;
            seen[clk] = true; q.Enqueue(clk);
            while (q.Count > 0)
            {
                int u = q.Dequeue();
                order[u] = seq++;
                var un = _nodes[u];
                if (un == null) continue;
                foreach (int t in un.Gates)
                {
                    var tr = _transistors[t];
                    int e1 = tr.C1, e2 = tr.C2;
                    if (e1 > Ngnd && e1 < n && !seen[e1]) { seen[e1] = true; q.Enqueue(e1); }
                    if (e2 > Ngnd && e2 < n && !seen[e2]) { seen[e2] = true; q.Enqueue(e2); }
                }
            }
            PendingLocalityOrder = order;
        }

        private static int[] ResolveBusNodes(string prefix, int n)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = LookupNode(prefix + i);
            return a;
        }

        // ───────────────────────── pin helpers ──────────────────────────

        private static void DriveName(string name, bool high)
        {
            int nn = LookupNode(name);
            if (nn == EmptyNode) return;
            if (high) SetHigh(nn); else SetLow(nn);
        }

        private static int ReadBusNodes(int[] nodes)
        {
            int v = 0;
            for (int i = 0; i < nodes.Length; i++)
            {
                int nn = nodes[i];
                if (nn != EmptyNode && NodeStates[nn] != 0) v |= 1 << i;
            }
            return v;
        }

        // External memory drives the data bus on a read (CPU's db drivers are tristated). Batched: one settle.
        private static void WriteDataBus(int value)
        {
            for (int i = 0; i < _dbNodes.Length; i++)
            {
                int nn = _dbNodes[i];
                if (nn == EmptyNode) continue;
                if (((value >> i) & 1) != 0) SetHighQueued(nn); else SetLowQueued(nn);
            }
            ProcessQueue();
        }

        // ──────────────────────── half-step models ──────────────────────

        // 6502: clk0 low phase = read (CPU drives ab/rw, we feed db); high phase = write.
        private static void HalfStep6502()
        {
            if (NodeStates[_pClk0] != 0) { SetLow(_pClk0); BusReadRwAbDb(); }
            else                         { SetHigh(_pClk0); BusWriteRwAbDb(); }
        }

        // 6800: two-phase phi1/phi2 + dbe data-bus-enable.
        private static void HalfStep6800()
        {
            if (NodeStates[_pPhi2] != 0) { SetLow(_pPhi2); SetLow(_pDbe); SetHigh(_pPhi1); BusReadRwAbDb(); }
            else { SetHigh(_pPhi1); SetLow(_pPhi1); SetHigh(_pPhi2); SetHigh(_pDbe); BusWriteRwAbDb(); }
        }

        // z80: single clk; read AND write evaluated each half-step (as visual6502 chip-z80/support.js does).
        private static void HalfStepZ80()
        {
            if (NodeStates[_pClk] != 0) SetLow(_pClk); else SetHigh(_pClk);
            // read: active when _rd & _mreq both low; else drive 0xe9 (int-ack) or 0xff (idle)
            if (NodeStates[_pRd] == 0 && NodeStates[_pMreq] == 0) WriteDataBus(_rawMem[ReadBusNodes(_abNodes)]);
            else if (_pM1 != EmptyNode && _pIorq != EmptyNode && NodeStates[_pM1] == 0 && NodeStates[_pIorq] == 0) WriteDataBus(0xE9);
            else WriteDataBus(0xFF);
            // write: when _wr low
            if (_pWr != EmptyNode && NodeStates[_pWr] == 0) _rawMem[ReadBusNodes(_abNodes)] = (byte)ReadBusNodes(_dbNodes);
        }

        // shared rw/ab/db bus for 6502 + 6800
        private static void BusReadRwAbDb()
        {
            if (_pRw == EmptyNode || NodeStates[_pRw] != 0) WriteDataBus(_rawMem[ReadBusNodes(_abNodes)]);
        }
        private static void BusWriteRwAbDb()
        {
            if (_pRw != EmptyNode && NodeStates[_pRw] == 0) _rawMem[ReadBusNodes(_abNodes)] = (byte)ReadBusNodes(_dbNodes);
        }

        // ────────────────────────── power-on ────────────────────────────

        // visual6502-faithful initChip per chip (input pins + reset sequence). Reset() already
        // allocated arrays + power-on state; here we drive the input pins and run the reset window.
        private static void InitRawCpu()
        {
            switch (_rawCfg.Name)
            {
                case "6800":
                    DriveName("reset", false);
                    DriveName("phi1", true); DriveName("phi2", false); DriveName("dbe", false);
                    DriveName("dbe", true); DriveName("tsc", false); DriveName("halt", true);
                    DriveName("irq", true); DriveName("nmi", true);
                    RecomputeAllNodes();
                    for (int i = 0; i < 8; i++)
                    {
                        SetLow(_pPhi1); SetHigh(_pPhi2); SetHigh(_pDbe); SetLow(_pPhi2); SetLow(_pDbe); SetHigh(_pPhi1);
                    }
                    DriveName("reset", true);
                    for (int i = 0; i < 6; i++) _rawHalfStep();
                    break;

                case "z80":
                    DriveName("_reset", false);
                    DriveName("clk", true);
                    DriveName("_busrq", true); DriveName("_int", true); DriveName("_nmi", true); DriveName("_wait", true);
                    RecomputeAllNodes();
                    for (int i = 0; i < 31; i++) _rawHalfStep();
                    DriveName("_reset", true);
                    break;

                default: // 6502
                    DriveName("res", false);
                    DriveName("clk0", false);
                    DriveName("rdy", true); DriveName("so", false);
                    DriveName("irq", true); DriveName("nmi", true);
                    RecomputeAllNodes();
                    for (int i = 0; i < 8; i++) { SetHigh(_pClk0); SetLow(_pClk0); }
                    DriveName("res", true);
                    for (int i = 0; i < 18; i++) _rawHalfStep();
                    break;
            }
            Time = 0;
        }

        // ─────────────────────────── bench ──────────────────────────────

        public static int RunRawCpuBench(string dir, string chipName, int benchHc, int warmup, int rounds)
        {
            if (rounds < 1) rounds = 1;
            if (!RawCpus.TryGetValue(chipName, out var cfg))
            {
                Console.Error.WriteLine($"unknown chip '{chipName}' (have: {string.Join(", ", RawCpus.Keys)})");
                return 2;
            }
            foreach (var f in new[] { "nodenames.js", "segdefs.js", "transdefs.js" })
                if (!File.Exists(Path.Combine(dir, f))) { Console.Error.WriteLine($"missing {f} in {dir}"); return 2; }

            if (benchHc <= 0) benchHc = 1_000_000;
            if (warmup < 0) warmup = 0;

            var swLoad = Stopwatch.StartNew();
            LoadRawCpu(dir, cfg);
            swLoad.Stop();

            int nodes = NonNullNodeCount;
            int trans = TransistorBuildCount;

            Console.WriteLine($"# AprVisual.etc raw-CPU bench (our event-driven engine) — chip {cfg.Name}");
            Console.WriteLine($"#   netlist dir: {dir}");
            Console.WriteLine($"#   nodes: {nodes}   transistors: {trans}" + (cfg.SkipWeak ? $"   (skipped {_rawSkippedWeak} weak)" : ""));
            Console.WriteLine($"#   workload: Infinite NOP Sled (opcode 0x{cfg.Nop:X2})   lowering: {(EnableLowering ? "on" : "off")}   renumber: {(RawRenumber ? "on" : "off")}");
            if (RawRenumber) Console.WriteLine($"#   {LastRenumberStats}  (range-prune verified: {RangePruneOk})");
            Console.WriteLine($"#   load (parse + compose + lower + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");

            InitRawCpu();

            // sanity: with a NOP sled the address bus should advance — sample it across the warmup
            int ab0 = ReadBusNodes(_abNodes);
            for (int i = 0; i < warmup; i++) _rawHalfStep();
            int ab1 = ReadBusNodes(_abNodes);
            bool advancing = ab0 != ab1;

            // Multiple timed rounds: the .NET tiered JIT + dynamic PGO leave the FIRST round(s) below
            // steady state (tier-0 -> tier-1 recompile with collected profile). Report every round so the
            // warm/steady rate is visible; median + best summarise it.
            var rates = new double[rounds];
            for (int r = 0; r < rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < benchHc; i++) _rawHalfStep();
                sw.Stop();
                rates[r] = benchHc / sw.Elapsed.TotalSeconds;
                advancing |= ReadBusNodes(_abNodes) != ab1;
            }

            Console.WriteLine($"#   AB sample: post-reset=0x{ab0:X4}  post-warmup=0x{ab1:X4}  " +
                              (advancing ? "(advancing — CPU running)" : "(NOT advancing — check harness)"));
            Console.WriteLine("#   " + new string('-', 50));
            for (int r = 0; r < rounds; r++)
                Console.WriteLine($"#   round {r + 1}: {rates[r]:N0} hc/s");
            var sorted = (double[])rates.Clone(); Array.Sort(sorted);
            double median = sorted[sorted.Length / 2];
            double best = sorted[sorted.Length - 1];
            double mean = 0; foreach (var v in rates) mean += v; mean /= rates.Length;
            Console.WriteLine("#   " + new string('-', 50));
            Console.WriteLine($"#   median: {median:N0} hc/s   best: {best:N0}   mean: {mean:N0}   ({benchHc:N0} hc/round, warmup {warmup:N0})");
            Console.WriteLine($"#   NodeStates checksum 0x{NodeStatesChecksum():X16}");
            return advancing ? 0 : 1;
        }
    }
}
