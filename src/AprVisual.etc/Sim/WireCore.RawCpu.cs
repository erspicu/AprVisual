using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

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
            // opcodes that HALT/LOCK the CPU (excluded from fuzz so the PC keeps advancing). Per Gemini
            // 2026-06-15 (refs: 6502 undocumented-opcode lit / Visual6502; Wheeler M6800 BYTE 1977; Young Z80):
            //   6502 KIL/JAM ×12, 6800 WAI+HCF ×5, z80 HALT ×1.
            public int[] Jam = Array.Empty<int>();
        }

        internal static readonly Dictionary<string, RawCpuConfig> RawCpus = new(StringComparer.OrdinalIgnoreCase)
        {
            ["6502"] = new RawCpuConfig { Name = "6502", Vss = "vss", Vcc = "vcc", Clock = "clk0", Nop = 0xEA, SkipWeak = false,
                       Jam = new[] { 0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72, 0x92, 0xB2, 0xD2, 0xF2 } },   // KIL/JAM
            ["6800"] = new RawCpuConfig { Name = "6800", Vss = "gnd", Vcc = "vcc", Clock = "phi2", Nop = 0x01, SkipWeak = true,
                       Jam = new[] { 0x3E, 0x9D, 0xDD, 0xED, 0xFD } },   // WAI + HCF (halt-and-catch-fire)
            ["z80"]  = new RawCpuConfig { Name = "z80",  Vss = "vss", Vcc = "vcc", Clock = "clk",  Nop = 0x00, SkipWeak = true,
                       Jam = new[] { 0x76 } },   // HALT
        };

        // class-major renumber (range-prune + locality key); --no-renumber for an A/B
        public static bool RawRenumber = true;
        // self-captured first-touch locality key (S1's peak method); off → BFS-from-clock key. --no-capture for an A/B
        public static bool RawSelfCapture = true;

        // pin-level synthetic workload (no test ROM): NOP sled (steady regular), random-bus fuzz
        // (max-entropy event-queue stress), or reset-hold (chip held in reset, clock tree only).
        public enum RawWorkload { NopSled, Fuzz, ResetHold }
        public static RawWorkload Workload = RawWorkload.NopSled;
        private static bool _fuzz;          // read feed = LCG byte instead of memory
        private static bool _resetHold;     // skip the bus + keep reset asserted (clock-only)
        private const uint FuzzSeed = 0x1357_BD2Fu;   // fixed → reproducible
        private static uint _fuzzState;
        private static readonly bool[] _jamMask = new bool[256];   // fuzz excludes these (halt/lock opcodes)
        private const int RawCaptureWarmupHc = 1024;     // warm past the reset transient before capturing
        private const int RawCaptureHc = 32768;          // first-touch capture span

        // first-touch capture state (active only during the pass-1 capture; zero cost in the bench)
        private static bool _rawCapturing;
        private static uint[]? _captureOrder;
        private static uint _captureSeq;

        private static RawCpuConfig _rawCfg = null!;
        // HOT PATH reads these — UNMANAGED (handler-lifetime pool; no bounds checks, no GC tracking).
        private static int* _abNodes;   // 16 address-bus node ids
        private static int* _dbNodes;   // 8 data-bus node ids
        private static byte* _rawMem;   // flat 64K NOP-sled memory
        // cached pin node ids (resolved at load; EmptyNode if the chip lacks one)
        private static int _pRw, _pClk0, _pClk, _pPhi1, _pPhi2, _pDbe, _pRd, _pMreq, _pWr, _pM1, _pIorq;
        private static Action _rawHalfStep = null!;

        // ───────────────────────────── load ─────────────────────────────

        private static int _rawSkippedWeak;

        public static void LoadRawCpu(string dir, RawCpuConfig cfg)
        {
            _rawCfg = cfg;
            _fuzz = Workload == RawWorkload.Fuzz;
            _resetHold = Workload == RawWorkload.ResetHold;
            _fuzzState = FuzzSeed;
            Array.Clear(_jamMask);
            foreach (int j in cfg.Jam) _jamMask[j & 0xFF] = true;   // fuzz re-rolls past these
            if (RawRenumber)
            {
                // Mirrors LoadSystem's 3-pass auto-renumber for a bare CPU.
                // PASS 0 — classify the prune bits under identity ids.
                BuildRawNetlist(dir, cfg);
                Reset();
                CapturePruneClasses();           // PendingClassBits = StashedClassBits

                if (RawSelfCapture)
                {
                    // PASS 1 — build class-major with a temporary BFS key (ranges VERIFY → prunes ON),
                    // power the CPU on, warm past the reset transient, then RUN the production (pruned)
                    // cascade and record each node's TRUE first-touch order through an instrumented
                    // settle copy (RawSettle → ProcessQueueCapturing). This is S1's self-capture, made
                    // to work for the bare CPU by driving the real halfStep + bus instead of ClockNode.
                    BuildRawNetlist(dir, cfg);
                    BuildRawLocalityOrder(cfg);
                    PendingClassBits = StashedClassBits;
                    ApplyRenumber();
                    FinishRawLoad(cfg);
                    InitRawCpu();
                    RawCaptureFirstTouch(RawCaptureWarmupHc, RawCaptureHc);   // → PendingLocalityOrder (identity ids)
                    PendingClassBits = StashedClassBits;                // re-arm for the final pass
                }

                // FINAL PASS — class-major + the captured (or BFS) locality key.
                BuildRawNetlist(dir, cfg);
                if (!RawSelfCapture) BuildRawLocalityOrder(cfg);   // BFS key when capture is disabled
                PendingClassBits = StashedClassBits;
                ApplyRenumber();
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
            _rawMem = AllocHandlerArray<byte>(65536);                              // unmanaged 64K (hot path)
            for (int i = 0; i < 65536; i++) _rawMem[i] = (byte)cfg.Nop;            // Infinite NOP Sled

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

        private static int* ResolveBusNodes(string prefix, int n)
        {
            int* a = AllocHandlerArray<int>(n);   // unmanaged; freed at the next ResetBuild
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadBusNodes(int* nodes, int len)
        {
            int v = 0;
            for (int i = 0; i < len; i++)
            {
                int nn = nodes[i];
                if (nn != EmptyNode && NodeStates[nn] != 0) v |= 1 << i;
            }
            return v;
        }

        // Byte the external "memory" drives on a read: NOP-sled reads the flat array; Fuzz returns a
        // fixed-seed LCG byte (reproducible max-entropy garbage). _fuzz is constant during a run.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadFeed(int addr)
        {
            if (!_fuzz) return _rawMem[addr];
            int b;   // re-roll past halt/lock opcodes so the CPU keeps executing (real stress, not a jam)
            do { _fuzzState = _fuzzState * 1664525u + 1013904223u; b = (int)(_fuzzState >> 16) & 0xFF; }
            while (_jamMask[b]);
            return b;
        }

        // settle indirection: production ProcessQueue in the bench; the instrumented capturing copy
        // during the pass-1 first-touch capture. _rawCapturing is false everywhere except that span.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawSettle() { if (_rawCapturing) ProcessQueueCapturing(); else ProcessQueue(); }

        // a COPY of ProcessQueue's double-buffered wave loop that records each node's first pop
        // (mirrors WarmupCaptureFirstTouch's inner settle; runs ONLY at load, never in the bench).
        private static unsafe void ProcessQueueCapturing()
        {
            var order = _captureOrder!;
            while (RecalcListNextCount != 0)
            {
                int* tmpList = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpList;
                byte* tmpHash = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpHash;
                RecalcListCount = RecalcListNextCount;
                RecalcListNextCount = 0;
                for (int i = 0; i < RecalcListCount; i++)
                {
                    int nn = RecalcList[i];
                    if (RecalcHash[nn] != 0)
                    {
                        if (order[nn] == uint.MaxValue) order[nn] = _captureSeq++;
                        RecalcNode(nn);
                        RecalcHash[nn] = 0;
                    }
                }
                RecalcListCount = 0;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void RawDrive(int nn, bool high)
        {
            if (nn == EmptyNode) return;
            bool changed = high ? SetHighQueued(nn) : SetLowQueued(nn);
            if (changed) RawSettle();
        }

        // External memory drives the data bus on a read (CPU's db drivers are tristated). Batched: one settle.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteDataBus(int value)
        {
            for (int i = 0; i < 8; i++)
            {
                int nn = _dbNodes[i];
                if (nn == EmptyNode) continue;
                if (((value >> i) & 1) != 0) SetHighQueued(nn); else SetLowQueued(nn);
            }
            RawSettle();
        }

        // PASS-1 self-capture: warm past the reset transient (not recorded), then run the production
        // cascade for CaptureHc half-cycles recording first-touch order, translated back to identity ids.
        private static unsafe void RawCaptureFirstTouch(int warmupHc, int captureHc)
        {
            for (int i = 0; i < warmupHc; i++) _rawHalfStep();

            int n = NodeCount;
            _captureOrder = new uint[n];
            for (int i = 0; i < n; i++) _captureOrder[i] = uint.MaxValue;
            _captureSeq = 0;

            _rawCapturing = true;
            for (int i = 0; i < captureHc; i++) _rawHalfStep();
            _rawCapturing = false;

            // pass-1 ids are renumbered → translate the captured order back to identity ids for the
            // final pass's ApplyRenumber (which runs pre-renumber): identity[orig] = order[perm[orig]].
            ushort* perm = RenumberPerm;
            int permLen = RenumberPermLen;
            var identityOrder = new uint[n];
            for (int orig = 0; orig < n; orig++)
                identityOrder[orig] = _captureOrder[orig < permLen ? perm[orig] : orig];
            PendingLocalityOrder = identityOrder;
            _captureOrder = null;
        }

        // ──────────────────────── half-step models ──────────────────────

        // 6502: clk0 low phase = read (CPU drives ab/rw, we feed db); high phase = write.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HalfStep6502()
        {
            if (_resetHold) { RawDrive(_pClk0, NodeStates[_pClk0] == 0); return; }   // clock tree only
            if (NodeStates[_pClk0] != 0) { RawDrive(_pClk0, false); BusReadRwAbDb(); }
            else                         { RawDrive(_pClk0, true);  BusWriteRwAbDb(); }
        }

        // 6800: two-phase phi1/phi2 + dbe data-bus-enable.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HalfStep6800()
        {
            if (NodeStates[_pPhi2] != 0) { RawDrive(_pPhi2, false); RawDrive(_pDbe, false); RawDrive(_pPhi1, true); if (!_resetHold) BusReadRwAbDb(); }
            else { RawDrive(_pPhi1, true); RawDrive(_pPhi1, false); RawDrive(_pPhi2, true); RawDrive(_pDbe, true); if (!_resetHold) BusWriteRwAbDb(); }
        }

        // z80: single clk; read AND write evaluated each half-step (as visual6502 chip-z80/support.js does).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void HalfStepZ80()
        {
            RawDrive(_pClk, NodeStates[_pClk] == 0);
            if (_resetHold) return;   // clock tree only
            // read: active when _rd & _mreq both low; else drive 0xe9 (int-ack) or 0xff (idle)
            if (NodeStates[_pRd] == 0 && NodeStates[_pMreq] == 0) WriteDataBus(ReadFeed(ReadBusNodes(_abNodes, 16)));
            else if (_pM1 != EmptyNode && _pIorq != EmptyNode && NodeStates[_pM1] == 0 && NodeStates[_pIorq] == 0) WriteDataBus(0xE9);
            else WriteDataBus(0xFF);
            // write: when _wr low
            if (_pWr != EmptyNode && NodeStates[_pWr] == 0) _rawMem[ReadBusNodes(_abNodes, 16)] = (byte)ReadBusNodes(_dbNodes, 8);
        }

        // shared rw/ab/db bus for 6502 + 6800
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BusReadRwAbDb()
        {
            if (_pRw == EmptyNode || NodeStates[_pRw] != 0) WriteDataBus(ReadFeed(ReadBusNodes(_abNodes, 16)));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BusWriteRwAbDb()
        {
            if (_pRw != EmptyNode && NodeStates[_pRw] == 0) _rawMem[ReadBusNodes(_abNodes, 16)] = (byte)ReadBusNodes(_dbNodes, 8);
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
                    if (!_resetHold) DriveName("reset", true);   // reset-hold: keep asserted
                    for (int i = 0; i < 6; i++) _rawHalfStep();
                    break;

                case "z80":
                    DriveName("_reset", false);
                    DriveName("clk", true);
                    DriveName("_busrq", true); DriveName("_int", true); DriveName("_nmi", true); DriveName("_wait", true);
                    RecomputeAllNodes();
                    for (int i = 0; i < 31; i++) _rawHalfStep();
                    if (!_resetHold) DriveName("_reset", true);   // reset-hold: keep asserted
                    break;

                default: // 6502
                    DriveName("res", false);
                    DriveName("clk0", false);
                    DriveName("rdy", true); DriveName("so", false);
                    DriveName("irq", true); DriveName("nmi", true);
                    RecomputeAllNodes();
                    for (int i = 0; i < 8; i++) { SetHigh(_pClk0); SetLow(_pClk0); }
                    if (!_resetHold) DriveName("res", true);   // reset-hold: keep asserted
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
            string rn = RawRenumber ? (RawSelfCapture ? "on (self-capture locality)" : "on (BFS-key locality)") : "off";
            string wl = Workload switch
            {
                RawWorkload.Fuzz      => $"Random Bus Fuzzing (fixed-seed LCG)",
                RawWorkload.ResetHold => $"Reset-Hold (reset asserted, clock tree only)",
                _                     => $"Infinite NOP Sled (opcode 0x{cfg.Nop:X2})",
            };
            Console.WriteLine($"#   workload: {wl}   lowering: {(EnableLowering ? "on" : "off")}   renumber: {rn}");
            if (RawRenumber) Console.WriteLine($"#   {LastRenumberStats}  (range-prune verified: {RangePruneOk})");
            Console.WriteLine($"#   load (parse + compose + lower + capture + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");

            InitRawCpu();

            // Entering the hot path: free ALL build-time managed state so the timed loop touches ONLY
            // unmanaged arrays (NodeStates / NodeInfos / TransistorList / _abNodes / _dbNodes / _rawMem) —
            // same hygiene S1's LoadSystem applies (ClearPostLoadBuildState + ReleaseBenchResidualState).
            ClearPostLoadBuildState();     // drop Node.Gates/C1c2s + the transistor list + parsed defs (+ GC)
            ReleaseBenchResidualState();   // drop the name maps + Node shells (no name lookups past here)

            // sanity: with a NOP sled the address bus should advance — sample it across the warmup
            int ab0 = ReadBusNodes(_abNodes, 16);
            for (int i = 0; i < warmup; i++) _rawHalfStep();
            int ab1 = ReadBusNodes(_abNodes, 16);
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
                advancing |= ReadBusNodes(_abNodes, 16) != ab1;
            }

            // reset-hold deliberately keeps the chip in reset, so a static address bus is EXPECTED there.
            string abMsg = _resetHold ? "(held in reset — clock tree only, as intended)"
                                      : (advancing ? "(advancing — CPU running)" : "(NOT advancing — check harness)");
            Console.WriteLine($"#   AB sample: post-reset=0x{ab0:X4}  post-warmup=0x{ab1:X4}  {abMsg}");
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
            return (_resetHold || advancing) ? 0 : 1;
        }
    }
}
