using System;
using System.Diagnostics;
using System.Threading;

namespace AprVisual.Sim
{
    // ── thread-experiment (branch thread-experiment) — Stage B: the REAL 2-thread settle ──
    //
    // CPU‖PPU functional split. Main thread = CPU side, one worker thread = PPU side, each pinned to a
    // distinct top-EfficiencyClass P-core. Per settle wave the orchestrator partitions the wave's node
    // list by chip side; the two threads process their halves concurrently, then a lock-free barrier.
    //
    // LOCKING: none. With the proven disjoint partition (S1 contested=0), per-thread next-lists + group
    // scratch ([ThreadStatic], see WireCore.cs / .Group.cs), and the shared dedup hash written only in
    // disjoint byte ranges, the two threads never race. The ONLY shared mutable state touched concurrently
    // is NodeStates (disjoint writes -> hardware coherence handles it, correct) and RecalcHash/Next
    // (disjoint bytes). Synchronization is one sense/ping-pong barrier per parallel wave + the worker
    // start/stop handshake.
    //
    // BIT-EXACTNESS: a wave is run in PARALLEL only when it is provably free of cross-side coupling:
    //   (1) no cut node (db/ab/io_ce/nmi/rw) is in the wave  -> no cut CHANGE / cross-enqueue this wave;
    //   (2) io_ce is INACTIVE (active-low: NodeStates[io_ce] != 0) -> the shared db/ab bus transistors on
    //       the PPU side are OFF, so no conducting group spans the two sides;
    //   (3) no neutral node (clock tree) is in the wave -> clock fanout (which gates both sides) only
    //       happens at the hc boundary, handled serially;
    //   (4) both sides actually have work (else no point).
    // Otherwise the whole wave runs serially on main (exact). All four are cheap per-wave tests.
    internal static unsafe partial class WireCore
    {
        // ── partition (Release; self-contained flood-fill, independent of the DEBUG profiler) ──
        private static byte[]? TR_Side;      // 0 neutral/cut/unreached, 1 cpu, 2 ppu
        private static byte[]? TR_IsCut;     // 1 = one of the 14 CPU<->PPU cut wires
        private static int TR_IoCeNode = EmptyNode;
        internal static long TR_WavesSerial, TR_WavesParallel, TR_WavesEmpty;   // accounting
        internal static long TR_SerIoCe, TR_SerCut, TR_SerNeutral;   // why a wave went serial-coupled

        private static byte TR_SideOfName(string nm)
        {
            if (nm.StartsWith("cpu.", StringComparison.Ordinal)) return 1;
            if (nm.StartsWith("ppu.", StringComparison.Ordinal)) return 2;
            if (nm.StartsWith("u1.", StringComparison.Ordinal) || nm.StartsWith("u3.", StringComparison.Ordinal)
             || nm.StartsWith("u7.", StringComparison.Ordinal) || nm.StartsWith("u8.", StringComparison.Ordinal)
             || nm.StartsWith("port0.", StringComparison.Ordinal) || nm.StartsWith("port1.", StringComparison.Ordinal)
             || nm.StartsWith("u10.", StringComparison.Ordinal)) return 1;
            if (nm.StartsWith("u2.", StringComparison.Ordinal) || nm.StartsWith("u4.", StringComparison.Ordinal)
             || nm.StartsWith("u9.", StringComparison.Ordinal)
             || nm.StartsWith("BD", StringComparison.Ordinal) || nm.StartsWith("BA", StringComparison.Ordinal)) return 2;
            if (nm.StartsWith("cart", StringComparison.Ordinal))
            {
                if (nm.Contains("ppu", StringComparison.OrdinalIgnoreCase) || nm.Contains("chr", StringComparison.OrdinalIgnoreCase)) return 2;
                if (nm.Contains("cpu", StringComparison.OrdinalIgnoreCase) || nm.Contains("prg", StringComparison.OrdinalIgnoreCase)) return 1;
            }
            return 0;
        }

        // Build TR_Side / TR_IsCut. MUST run after LoadSystem (final ids) and BEFORE the name maps are
        // freed (ReleaseBenchResidualState). Mirrors the DEBUG BuildPartition flood-fill.
        internal static unsafe void BuildThreadPartition()
        {
            TR_Side = new byte[NodeCount];
            TR_IsCut = new byte[NodeCount];
            for (int i = 0; i < NodeCount; i++) TR_Side[i] = TR_SideOfName(GetNodeName(i));
            void Cut(string name) { int n = LookupNode(name); if (n != EmptyNode && n != Npwr && n != Ngnd) TR_IsCut[n] = 1; }
            for (int b = 0; b <= 7; b++) Cut($"cpu.db{b}");
            for (int b = 0; b <= 2; b++) Cut($"cpu.ab{b}");
            Cut("ppu.io_ce"); Cut("cpu.nmi"); Cut("cpu.rw");
            TR_IoCeNode = LookupNode("ppu.io_ce");
            // flood chip-side ownership to nameless internals through channel adjacency, not crossing cut/supply
            var q = new System.Collections.Generic.Queue<int>();
            for (int i = 0; i < NodeCount; i++) if (TR_Side[i] == 1 || TR_Side[i] == 2) q.Enqueue(i);
            while (q.Count > 0)
            {
                int nn = q.Dequeue(); byte s = TR_Side[nn];
                if (s != 1 && s != 2 || TR_IsCut[nn] != 0) continue;
                NodeInfo* d = NodeInfos + nn;
                if (d->Inline != 0)
                {
                    ushort* pay = d->InlinePayload; int n2 = d->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) TR_Flood(pay[k + 1], s, q);
                }
                else { ushort* p = TransistorList + d->TlistC1c2s; while (*p != 0) { TR_Flood(p[1], s, q); p += 2; } }
            }
        }
        private static void TR_Flood(int o, byte s, System.Collections.Generic.Queue<int> q)
        {
            if (o == Npwr || o == Ngnd || TR_IsCut![o] != 0) return;
            if (TR_Side![o] == 0) { TR_Side[o] = s; q.Enqueue(o); }
        }

        // ── worker handoff (shared, non-[ThreadStatic]) ──
        private static int* TR_CpuSub;          // shared buffer of cpu node ids for this wave
        private static int* TR_NeuSub;          // shared buffer of neutral (clock/shared) node ids
        private static int* TR_PpuSub;          // shared buffer of ppu node ids for this wave
        private static int TR_WCount;           // count in TR_PpuSub
        private static int* TR_WResultPtr;      // worker's next-list pointer (its TLS RecalcListNext)
        private static int TR_WResultCount;     // worker's next-list count
        private static volatile int _trToWorker, _trToMain;   // ping-pong barrier sequence
        private static Thread? _trWorker;
        private static volatile bool _trStop;

        // Allocate the calling (worker) thread's [ThreadStatic] scratch. RecalcHash/Next are shared
        // (main's), so only the lists + group scratch are per-thread.
        private static void EnsureWorkerScratch()
        {
            RecalcListNext = AllocArray<int>(NodeCount);
            RecalcList = AllocArray<int>(NodeCount);   // unused by worker but keep non-null
            _groupBuf = AllocArray<ushort>(NodeCount);
            _inGroup = AllocArray<byte>(NodeCount);
            RecalcListNextCount = 0; RecalcListCount = 0; _groupCount = 0;
        }

        private static void TR_WorkerLoop(int core)
        {
            Console.WriteLine($"#   worker: {PerfTuning.PinCurrentThreadTo(core)}");
            EnsureWorkerScratch();
            int last = 0;
            var sp = new SpinWait();
            while (true)
            {
                int s = Volatile.Read(ref _trToWorker);
                if (_trStop) return;
                if (s == last) { sp.SpinOnce(-1); continue; }
                last = s; sp = new SpinWait();
                int* sub = TR_PpuSub; int c = TR_WCount;
                for (int i = 0; i < c; i++) { int nn = sub[i]; if (RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; } }
                TR_WResultPtr = RecalcListNext; TR_WResultCount = RecalcListNextCount; RecalcListNextCount = 0;
                Volatile.Write(ref _trToMain, s);   // ack
            }
        }

        // Parallel settle for one half-cycle's worth of enqueued work (replaces ProcessQueue).
        private static void ProcessQueueParallel()
        {
            int seq = 0;
            while (RecalcListNextCount != 0)
            {
                int* tmp = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmp;
                byte* th = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = th;
                RecalcListCount = RecalcListNextCount; RecalcListNextCount = 0;
                int N = RecalcListCount; int* cur = RecalcList;

                // A wave is parallelizable ONLY if cpu and ppu nodes are mutually independent for the whole
                // wave. That requires: io_ce inactive (no cpu↔ppu channel span), NO cut node (no cross-
                // enqueue), and NO neutral/clock node (a neutral node changing mid-wave gates BOTH sides,
                // and the within-wave Gauss-Seidel order across neutral↔chip nodes is observable semantics
                // — reordering it diverges, MEASURED: the 3-phase neutral-first variant broke bit-exact).
                bool ioCeActive = TR_IoCeNode != EmptyNode && NodeStates[TR_IoCeNode] == 0;
                bool bad = ioCeActive; int why = ioCeActive ? 1 : 0;   // 1=io_ce 2=cut 3=neutral
                int pc = 0, cc = 0;
                if (!bad)
                    for (int i = 0; i < N; i++)
                    {
                        int nn = cur[i];
                        if (TR_IsCut![nn] != 0) { bad = true; why = 2; break; }
                        byte s = TR_Side![nn];
                        if (s == 0) { bad = true; why = 3; break; }     // neutral (clock/shared) — order is semantics
                        if (s == 2) TR_PpuSub[pc++] = nn; else cc++;
                    }

                if (bad || pc == 0 || cc == 0)
                {
                    for (int i = 0; i < N; i++) { int nn = cur[i]; if (RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; } }
                    if (bad) { TR_WavesSerial++; if (why == 1) TR_SerIoCe++; else if (why == 2) TR_SerCut++; else TR_SerNeutral++; } else TR_WavesEmpty++;
                }
                else
                {
                    TR_WavesParallel++;
                    TR_WCount = pc;
                    Volatile.Write(ref _trToWorker, ++seq);     // release worker on ppuSub
                    for (int i = 0; i < N; i++) { int nn = cur[i]; if (TR_Side![nn] == 1 && RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; } }
                    var spin = new SpinWait();
                    while (Volatile.Read(ref _trToMain) != seq) spin.SpinOnce(-1);
                    int* mn = RecalcListNext; int mc = RecalcListNextCount;
                    int* wr = TR_WResultPtr; int wc = TR_WResultCount;
                    for (int j = 0; j < wc; j++) mn[mc++] = wr[j];
                    RecalcListNextCount = mc;
                }
            }
        }

        // Run `hc` half-cycles with the 2-thread parallel settle, pinned to two P-cores. Bit-exact gate
        // is the caller's job (compare NodeStatesChecksum vs the single-thread golden).
        public static int RunThreaded(int hc)
        {
            var (ca, cb) = PerfTuning.TwoBestCores();
            if (ca < 0 || cb < 0 || ca == cb) { Console.WriteLine("# [thread-run] need two distinct P-cores — aborting"); return 2; }
            Console.WriteLine($"# [thread-run] cores: main->{ca}, worker->{cb}");
            TR_PpuSub = AllocArray<int>(NodeCount); TR_CpuSub = AllocArray<int>(NodeCount); TR_NeuSub = AllocArray<int>(NodeCount);
            _trStop = false; _trToWorker = 0; _trToMain = 0;
            _trWorker = new Thread(() => TR_WorkerLoop(cb)) { IsBackground = true, Priority = ThreadPriority.Highest };
            _trWorker.Start();
            Console.WriteLine($"#   main:   {PerfTuning.PinCurrentThreadTo(ca)}");
            Thread.Sleep(50);   // let the worker pin + alloc its scratch

            int clk = ClockNode;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < hc; i++)
            {
                if (clk != EmptyNode)
                {
                    ref NodeInfo ns = ref NodeInfos[clk];
                    int next = NodeStates[clk] ^ 1;
                    ns.Flags = (ns.Flags & ~(NodeFlags.SetHigh | NodeFlags.SetLow)) | (NodeFlags)(8 >> next);
                    EnqueueNode(clk);
                    ProcessQueueParallel();
                    InvokeCallbacks();
                }
                Time++;
            }
            sw.Stop();
            _trStop = true; Volatile.Write(ref _trToWorker, -1); _trWorker.Join(1000);

            double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
            Console.WriteLine($"# [thread-run] {hc:N0} hc in {secs:F3} s -> {hc / secs / 1000:F1}K hc/s ({secs * 1e6 / hc:F2} us/hc)");
            long w = TR_WavesParallel + TR_WavesSerial + TR_WavesEmpty;
            double Pw(long x) => w > 0 ? 100.0 * x / w : 0;
            Console.WriteLine($"# [thread-run] waves: parallel={TR_WavesParallel:N0} ({Pw(TR_WavesParallel):F1}%) serial-coupled={TR_WavesSerial:N0} ({Pw(TR_WavesSerial):F1}%) serial-empty={TR_WavesEmpty:N0} ({Pw(TR_WavesEmpty):F1}%)");
            Console.WriteLine($"#   serial-coupled cause: io_ce-active={TR_SerIoCe:N0} cut-node={TR_SerCut:N0} neutral-node={TR_SerNeutral:N0}  (io_ce node id={TR_IoCeNode})");
            return 0;
        }
    }
}
