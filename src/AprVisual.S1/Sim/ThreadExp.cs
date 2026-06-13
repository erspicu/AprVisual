using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AprVisual.Sim
{
    // ── thread-experiment (branch thread-experiment) — Stage A ──
    // Measures the REAL per-wave cross-core synchronization cost on this machine, then feeds it into a
    // performance model of the CPU‖PPU 2-thread split (using the S1 work-split: cpu-side 16.9% /
    // ppu-side 54.3% / shared 28.8%, ~12.06 settle waves/hc, 2-way work ceiling ~1.20x).
    //
    // The split parallelizes the per-wave node processing across two threads (one per chip side), with a
    // BARRIER every settle wave to exchange the cut. The handoff cost = "signal the worker, wait for its
    // ack" = one cross-core round-trip on a shared, cache-line-padded flag pair. That round-trip latency,
    // times the ~12 waves/hc, is the overhead added on top of the (work-imbalance-limited) parallel work.
    internal static unsafe class ThreadExp
    {
        // Two flags on separate 64-byte cache lines (avoid false sharing). main writes ToWorker & spins on
        // ToMain; worker spins on ToWorker & writes ToMain. One iteration = one full round-trip.
        [StructLayout(LayoutKind.Explicit, Size = 192)]
        private struct PingPong
        {
            [FieldOffset(64)] public int ToWorker;   // main -> worker (own line)
            [FieldOffset(128)] public int ToMain;    // worker -> main (own line)
        }
        private static PingPong _pp;
        private static volatile bool _stop;

        public static int RunBarrierBench(long iters)
        {
            if (iters < 100000) iters = 5_000_000;
            var (ca, cb) = PerfTuning.TwoBestCores();
            Console.WriteLine($"# [thread-exp] barrier microbenchmark — {iters:N0} cross-core round-trips");
            if (ca < 0 || cb < 0 || ca == cb)
            {
                Console.WriteLine("# [thread-exp] could not pick two distinct P-cores (topology query failed or <2 top-class cores) — aborting");
                return 2;
            }
            Console.WriteLine($"# [thread-exp] cores: main->{ca}, worker->{cb} (two highest top-EfficiencyClass physical cores, first-of-pair)");

            _pp.ToWorker = 0; _pp.ToMain = 0; _stop = false;
            var ready = new ManualResetEventSlim(false);

            var worker = new Thread(() =>
            {
                Console.WriteLine($"#   worker: {PerfTuning.PinCurrentThreadTo(cb)}");
                ready.Set();
                int last = 0;
                var sp = new SpinWait();
                while (true)
                {
                    int s = Volatile.Read(ref _pp.ToWorker);
                    if (s < 0) break;                       // stop sentinel
                    if (s != last) { last = s; Volatile.Write(ref _pp.ToMain, s); sp = new SpinWait(); }
                    else sp.SpinOnce(-1);
                }
            }) { IsBackground = true, Priority = ThreadPriority.Highest };
            worker.Start();
            ready.Wait();
            Console.WriteLine($"#   main:   {PerfTuning.PinCurrentThreadTo(ca)}");

            // warm up the ping-pong (JIT + bring the worker's spin into steady state)
            for (int w = 0; w < 200000; w++) { int seq = w + 1; Volatile.Write(ref _pp.ToWorker, seq); var s = new SpinWait(); while (Volatile.Read(ref _pp.ToMain) != seq) s.SpinOnce(-1); }

            var sw = Stopwatch.StartNew();
            int baseSeq = 200000;
            for (long i = 1; i <= iters; i++)
            {
                int seq = baseSeq + (int)(i & 0x3FFFFFFF) + 1;
                Volatile.Write(ref _pp.ToWorker, seq);
                var spin = new SpinWait();
                while (Volatile.Read(ref _pp.ToMain) != seq) spin.SpinOnce(-1);
            }
            sw.Stop();
            Volatile.Write(ref _pp.ToWorker, -1);   // stop the worker
            worker.Join(1000);

            double nsPerRt = sw.Elapsed.TotalSeconds * 1e9 / iters;
            Console.WriteLine($"# [thread-exp] measured cross-core round-trip (per-wave handoff): {nsPerRt:F1} ns  ({iters / sw.Elapsed.TotalSeconds / 1e6:F2} M/s)");
            PrintModel(nsPerRt);
            return 0;
        }

        // The CPU‖PPU split performance model (Stage A). All inputs from S1 / the engine's measured profile.
        private static void PrintModel(double barrierNs)
        {
            const double wavesPerHc = 12.06;     // measured (settle-pass-dist)
            const double workCeiling = 1.20;     // S1 2-way work ceiling (shared duplicated on both)
            // representative single-thread Release rate on this machine (cool best ~135.9K; use a round 135K)
            const double serialHcPerS = 135000.0;
            double serialUsPerHc = 1e6 / serialHcPerS;            // ~7.41 us/hc
            double parallelWorkUs = serialUsPerHc / workCeiling;  // work after the (imbalance-limited) split
            double barrierUsPerHc_all = wavesPerHc * barrierNs / 1000.0;       // naive: barrier EVERY wave
            double barrierUsPerHc_smart = 0.125 * wavesPerHc * barrierNs / 1000.0; // smart: skip barrier when CPU-side empty (only the 12.5% both-hc)

            void Row(string label, double barrierUs)
            {
                double tPar = parallelWorkUs + barrierUs;
                double speedup = serialUsPerHc / tPar;
                Console.WriteLine($"#   {label}: T_par = {parallelWorkUs:F2}(work) + {barrierUs:F2}(barriers) = {tPar:F2} us/hc -> {1e6 / tPar / 1000:F1}K hc/s = {speedup:F2}x  ({(speedup >= 1 ? "SPEEDUP" : "SLOWDOWN")})");
            }
            Console.WriteLine($"# [thread-exp] CPU‖PPU split model (serial {serialHcPerS / 1000:F0}K hc/s = {serialUsPerHc:F2} us/hc; work-ceiling {workCeiling:F2}x; {wavesPerHc} waves/hc):");
            Console.WriteLine($"#   parallel work alone (zero sync): {1e6 / parallelWorkUs / 1000:F1}K hc/s = {workCeiling:F2}x  <-- the unreachable ideal");
            Row("naive  (barrier every wave)", barrierUsPerHc_all);
            Row("smart  (barrier only in both-hc)", barrierUsPerHc_smart);
            Console.WriteLine($"#   verdict: a real >1x needs barrier < {(serialUsPerHc - parallelWorkUs) * 1000 / wavesPerHc:F0} ns (naive) / {(serialUsPerHc - parallelWorkUs) * 1000 / (0.125 * wavesPerHc):F0} ns (smart) per wave");
        }
    }
}
