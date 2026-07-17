using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AprVisual.Rom;
using AprVisual.Sim;

namespace AprVisual.Test
{
    internal static partial class TestRunner
    {
        // ── Benchmark modes (--benchmark / --bench-hc) + bench-log/machine-fingerprint helpers. ──
        // ── --benchmark: simulated FPS, MIPS, raw step rate over N frames ──
        public static int Benchmark(string romPath, int frames)
        {
            if (frames < 4) frames = 12;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# benchmark: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {frames} frame(s) headless, Release build recommended");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                swLoad.Stop();

                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int f = 0; f < frames; f++) WireCore.RunFrame();
                sw.Stop();

                long halfCycles = WireCore.Time - t0;
                double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
                double cpuCycles = halfCycles / 24.0;
                double fps      = frames / secs;
                double cpuHz    = cpuCycles / secs;
                double stepsHz  = halfCycles / secs;
                const double realFps = 60.0988, realCpuHz = 1_789_773.0;
                const double cycPerInstr = 2.8;

                Console.WriteLine($"# {WireCore.LastLowerStats}");
                Console.WriteLine($"# {WireCore.LastFastPathStats}");
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {frames} frames = {halfCycles:N0} master half-cycles = {cpuCycles:N0} 6502 cycles");
                Console.WriteLine($"# real time: {secs:F3} s");
                Console.WriteLine();
                Console.WriteLine($"  FPS  = {fps,8:F2}  simulated NES frames / real second        ( {fps / realFps * 100:F1}% of realtime, i.e. 1 real second of NES takes {realFps / fps:F1} s to simulate )");
                Console.WriteLine($"  MIPS = {cpuHz / 1e6,8:F3}  M simulated 6502 cycles / real second    ( {cpuHz / realCpuHz * 100:F1}% of the real 1.79 MHz 2A03 )");
                Console.WriteLine($"         {cpuHz / cycPerInstr / 1e6,8:F3}  M 6502 instructions / s  (≈, assuming ~{cycPerInstr:F1} cycles/instr)");
                Console.WriteLine($"  ---");
                Console.WriteLine($"  raw   {stepsHz / 1e6,8:F2}  M switch-level steps (master half-cycles) / s   — the actual inner-loop rate");
                return 0;
            }
            finally { WireCore.Shutdown(); }
        }

        // ── --bench-hc: time exactly N raw master-half-cycles (finer than --frames; for slow variants) ──
        public static int BenchmarkHalfCycles(string romPath, int hcCount, string logDir = "log")
        {
            if (hcCount < 1) hcCount = 1000;
            var rom = NesRom.LoadFromFile(romPath);
            if (rom is null) { Console.Error.WriteLine($"failed to load ROM: {romPath}"); return 2; }
            Console.WriteLine($"# bench-hc: {Path.GetFileName(romPath)}  (PRG {rom.PrgRom.Length / 1024} KB, mapper {rom.Mapper}) — {hcCount:N0} master half-cycles");
            try
            {
                var swLoad = System.Diagnostics.Stopwatch.StartNew();
                WireCore.LoadSystem(rom);
                swLoad.Stop();
                // Memory hygiene before the hot path: LoadSystem's ClearPostLoadBuildState already freed
                // the build graph (~25-50 MB); release the residual name maps + Node shells the hot loop
                // never touches, then force a final compacting Gen2 GC so no collection can fire mid-
                // measurement. Hot data is unmanaged (NodeStates/NodeInfos/TransistorList/...), so this is
                // timing-stability hygiene + minimum resident heap, not a throughput change.
#if DEBUG
                WireCore.InitBoundaryDiag();   // cpu/ppu boundary profiler — BEFORE ReleaseBenchResidualState frees the name maps
#endif
                WireCore.ReleaseBenchResidualState();
                System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
                System.GC.WaitForPendingFinalizers();
                System.GC.Collect(2, System.GCCollectionMode.Aggressive, blocking: true, compacting: true);
                // [array-footprint] hot-array base + byte size — for cache-miss data-address bucketing
                // (ARM SPE / AMD IBS) + footprint analysis (which arrays exceed L1d/L2). Printed once at
                // setup, zero hot-path cost. Opt-in via --array-footprint (works in Release, for IBS/SPE
                // data-address bucketing); sizes are config-independent. Pointer access needs unsafe.
                if (_dumpArrayFootprint)
                unsafe
                {
                    int nc = WireCore.NodeCount;
                    void Fp(string name, ulong b, long bytes) =>
                        Console.WriteLine($"#   {name,-18} 0x{b:X12}..0x{b + (ulong)bytes:X12} {bytes / 1024.0,9:F1} KB");
                    Console.WriteLine($"# [array-footprint] NodeCount={nc:N0}  (A76: L1d=64KB, L2=512KB/core)");
                    Fp("NodeStates",        (ulong)WireCore.NodeStates,        nc);
                    Fp("NodeInfos",         (ulong)WireCore.NodeInfos,         (long)nc * 16);
                    Fp("RecalcList",        (ulong)WireCore.RecalcList,        (long)nc * 4);
                    Fp("RecalcListNext",    (ulong)WireCore.RecalcListNext,    (long)nc * 4);
                    Fp("RecalcHash",        (ulong)WireCore.RecalcHash,        nc);
                    Fp("RecalcHashNext",    (ulong)WireCore.RecalcHashNext,    nc);
                    Fp("NodeTlistGates",    (ulong)WireCore.NodeTlistGates,    (long)nc * 4);
                    Fp("NodeTlistGatesOff", (ulong)WireCore.NodeTlistGatesOff, (long)nc * 4);
                    Fp("IsPureLogic",       (ulong)WireCore.IsPureLogic,       nc);
                    Fp("TransistorList",    (ulong)WireCore.TransistorList,    (long)WireCore.TransistorListLength * 2);
                    Fp("TransistorListOff", (ulong)WireCore.TransistorListOff, (long)WireCore.TransistorListOffLength * 2);
                    Fp("FlagsToState",      (ulong)WireCore.FlagsToState,      256);
                }
                long t0 = WireCore.Time;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                WireCore.Step(hcCount);
                sw.Stop();
                long halfCycles = WireCore.Time - t0;
                double secs = sw.Elapsed.TotalSeconds; if (secs <= 0) secs = 1e-9;
                double stepsHz = halfCycles / secs;
                ulong stateHash = WireCore.NodeStatesChecksum();
                Console.WriteLine($"# {WireCore.LastLowerStats}");
                Console.WriteLine($"# {WireCore.LastFastPathStats}");
                Console.WriteLine($"# {WireCore.LastPruneTaintStats}");
                Console.WriteLine($"# {WireCore.LastTurnOffSkipStats}");
                Console.WriteLine($"# {WireCore.LastRenumberStats}");
                Console.WriteLine($"# load (compose netlist + power-on settle): {swLoad.Elapsed.TotalSeconds:F2} s");
                Console.WriteLine($"# simulated: {halfCycles:N0} master half-cycles in {secs:F3} s");
                Console.WriteLine($"# rate: {stepsHz:N0} hc/s ({secs * 1e6 / halfCycles:F2} µs/hc)");
                var (bv, bd) = BuildVersion();
                Console.WriteLine($"# engine: csharp  version: {bv} ({bd})");
                Console.WriteLine($"# NodeStates checksum @ t={WireCore.Time}: 0x{stateHash:X16}  (A/B equivalence: must match the baseline run)");
#if DEBUG
                WireCore.DumpM2Census();   // M2_CENSUS firing census (no-op unless enabled)
#endif
#if DEBUG
                {
                    // wasted-pop profiler (DEBUG only; counts identical to Release). See WireCore.Recalc.cs.
                    double W(long x) => WireCore.DiagNoChange == 0 ? 0 : 100.0 * x / WireCore.DiagNoChange;
                    Console.WriteLine($"# [waste-profile] pops={WireCore.DiagPops:N0} no-change={WireCore.DiagNoChange:N0} ({(WireCore.DiagPops == 0 ? 0 : 100.0 * WireCore.DiagNoChange / WireCore.DiagPops):F1}%)  (% of waste:)");
                    Console.WriteLine($"#   FloatSingle={WireCore.DiagNCFloatSingle:N0}({W(WireCore.DiagNCFloatSingle):F1}%) FloatMulti={WireCore.DiagNCFloatMulti:N0}({W(WireCore.DiagNCFloatMulti):F1}%,capLTall={WireCore.DiagNCFloatMultiCapLT:N0}) PullUp={WireCore.DiagNCPullUp:N0}({W(WireCore.DiagNCPullUp):F1}%) Supply={WireCore.DiagNCSupply:N0}({W(WireCore.DiagNCSupply):F1}%) Other={WireCore.DiagNCOther:N0}({W(WireCore.DiagNCOther):F1}%)");
                    double Pp(long x) => WireCore.DiagPops == 0 ? 0 : 100.0 * x / WireCore.DiagPops;
                    Console.WriteLine($"# [P-2b candidate] single-channel pure-PullUp class: pops={WireCore.DiagP2bPops:N0} ({Pp(WireCore.DiagP2bPops):F1}% of all) no-change={WireCore.DiagNCP2b:N0} skippable(state==1)={WireCore.DiagNCP2bState1:N0} ({Pp(WireCore.DiagNCP2bState1):F1}% of all pops)");
                    Console.WriteLine($"# [B1 pair-path] pops resolved inline as 2-node groups: {WireCore.DiagPairPath:N0} ({Pp(WireCore.DiagPairPath):F1}% of all pops)");
                    {
                        // [fast-gate dist] DEBUG-only: gnd/pwr/c1c2 gate-count distribution of fast-path
                        // (RecalcNodeFast) pops — sizes Gemini's "Design 1 fixed 2gnd+2pwr+1c1c2" MLP idea.
                        long fp = WireCore.DiagFastPops;
                        double Fp(long x) => fp == 0 ? 0 : 100.0 * x / fp;
                        string Hist(long[] h) { var s = new System.Text.StringBuilder(); for (int i = 0; i < h.Length; i++) if (h[i] != 0) s.Append($" {(i==7?"7+":i.ToString())}:{Fp(h[i]):F1}%"); return s.ToString(); }
                        Console.WriteLine($"# [fast-gate dist] fast-path pops={fp:N0} ({Pp(fp):F1}% of all pops)  inline={Fp(WireCore.DiagFastInline):F1}%  FITS fixed(c1c2<=1,gnd<=2,pwr<=2)={WireCore.DiagFastFitsFixed:N0} ({Fp(WireCore.DiagFastFitsFixed):F1}%)");
                        Console.WriteLine($"#   GndCount:{Hist(WireCore.DiagFastGnd)}");
                        Console.WriteLine($"#   PwrCount:{Hist(WireCore.DiagFastPwr)}");
                        Console.WriteLine($"#   C1c2Count:{Hist(WireCore.DiagFastC1c2)}");
                    }
                    {
                        // [branch-dist] step-0 of the mem-latency branch: direction split of the hot
                        // DATA-DEPENDENT branches → locate the ~6 MPKI branch-miss source. ~50/50 = high
                        // entropy = likely mispredicted; lopsided = the predictor handles it cheaply.
                        long c0 = WireCore.DiagBrCls[0], c1c = WireCore.DiagBrCls[1], c2c = WireCore.DiagBrCls[2];
                        long clsT = c0 + c1c + c2c;
                        double Cp(long x) => clsT == 0 ? 0 : 100.0 * x / clsT;
                        string Split(long a, long b) { long t = a + b; return t == 0 ? "n/a" : $"{100.0 * a / t:F1}% / {100.0 * b / t:F1}%  (n={t:N0})"; }
                        Console.WriteLine($"# [branch-dist] (DEBUG; ~50/50 = high-entropy = likely mispredicted)");
                        Console.WriteLine($"#   dispatch cls: 0(BFS)={Cp(c0):F1}% 1(static-fast)={Cp(c1c):F1}% 2(dyn-singleton)={Cp(c2c):F1}%  (n={clsT:N0})");
                        Console.WriteLine($"#   cls2 channels [allOFF->fast / someON->pair.BFS]: {Split(WireCore.DiagBrCls2Off, WireCore.DiagBrCls2On)}");
                        Console.WriteLine($"#   SetNodeState [turn-on / turn-off]: {Split(WireCore.DiagBrTurnOn, WireCore.DiagBrTurnOff)}");
                        Console.WriteLine($"#   turn-on enqueue prune [keep / skip] (per-transistor, hottest): {Split(WireCore.DiagBrPruneKeep, WireCore.DiagBrPruneSkip)}");
                        Console.WriteLine($"#   fast-path [drive(write) / float(no-op)]: {Split(WireCore.DiagBrFastDrive, WireCore.DiagBrFastFloat)}");
                    }
                    {
                        // [co-read B feasibility] can a NodeStates bitset mirror merge two scattered byte
                        // loads into one 64-bit word load? Only if the co-read node ids share a word (id>>6).
                        // Low % => Scheme B (+ co-read renumber) is dead.
                        double Rp(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
                        Console.WriteLine($"# [co-read B] rising (c1,c2) same 64-bit word: {Rp(WireCore.DiagCoRiseSame, WireCore.DiagCoRiseTot):F1}% (n={WireCore.DiagCoRiseTot:N0})  |  fast-path >=2-gate pops all-in-1-word: {Rp(WireCore.DiagCoFast1Word, WireCore.DiagCoFastMulti):F1}% (n={WireCore.DiagCoFastMulti:N0})");
                    }
                    {
                        // [hotpath-calls] call count at each hot-path-chain method entry: count, % of all
                        // chain calls, and calls per RecalcNode pop — shows where the per-pop work goes.
                        long pq = WireCore.HpProcessQueue, rn = WireCore.HpRecalcNode, rf = WireCore.HpRecalcNodeFast,
                             cg = WireCore.HpComputeNodeGroup, ag = WireCore.HpAddNodeToGroup, gv = WireCore.HpGetNodeValue, ss = WireCore.HpSetNodeState;
                        long tot = pq + rn + rf + cg + ag + gv + ss;
                        double pcent(long a) => tot == 0 ? 0 : 100.0 * a / tot;
                        double perpop(long a) => rn == 0 ? 0 : (double)a / rn;
                        Console.WriteLine($"# [hotpath-calls] (DEBUG; count / % of all chain calls / per-RecalcNode-pop)  chain total={tot:N0}");
                        Console.WriteLine($"#   ProcessQueue     {pq,14:N0}  {pcent(pq),5:F1}%  {perpop(pq),7:F4}/pop  (coarse: per-half-cycle settle driver)");
                        Console.WriteLine($"#   RecalcNode       {rn,14:N0}  {pcent(rn),5:F1}%  {perpop(rn),7:F4}/pop  (= the pop count, the denominator)");
                        Console.WriteLine($"#   RecalcNodeFast   {rf,14:N0}  {pcent(rf),5:F1}%  {perpop(rf),7:F4}/pop  (O(1) singleton resolve)");
                        Console.WriteLine($"#   ComputeNodeGroup {cg,14:N0}  {pcent(cg),5:F1}%  {perpop(cg),7:F4}/pop  (slow BFS group build)");
                        Console.WriteLine($"#   AddNodeToGroup   {ag,14:N0}  {pcent(ag),5:F1}%  {perpop(ag),7:F4}/pop  (BFS walk)");
                        Console.WriteLine($"#   GetNodeValue     {gv,14:N0}  {pcent(gv),5:F1}%  {perpop(gv),7:F4}/pop  (group resolve LUT)");
                        Console.WriteLine($"#   SetNodeState     {ss,14:N0}  {pcent(ss),5:F1}%  {perpop(ss),7:F4}/pop  (write + enqueue)");
                    }
                }
                {
                    // settle-pass distribution (DEBUG only): how many settle waves each ProcessQueue() call
                    // took. Used to revisit the MaxSettlePasses safety cap (Release omits the cap). See
                    // WireCore.Recalc.cs SettlePassTally. Counts all ProcessQueue calls (clk + handler settles).
                    var h = WireCore.SettlePassHist;
                    long calls = WireCore.SettleCalls;
                    long total = 0, sum = 0; int maxIter = 0;
                    for (int i = 0; i < h.Length; i++) { total += h[i]; sum += h[i] * i; if (h[i] != 0) maxIter = i; }
                    double mean = total == 0 ? 0 : (double)sum / total;
                    // percentile helper: smallest pass-count whose cumulative share >= q
                    int Pct(double q) { long need = (long)System.Math.Ceiling(q * total); long cum = 0; for (int i = 0; i < h.Length; i++) { cum += h[i]; if (cum >= need) return i; } return maxIter; }
                    Console.WriteLine($"# [settle-pass-dist] ProcessQueue calls={calls:N0}  passes: mean={mean:F2} max={maxIter} | p50={Pct(0.50)} p90={Pct(0.90)} p99={Pct(0.99)} p99.9={Pct(0.999)} p99.99={Pct(0.9999)}");
                    Console.WriteLine("#   histogram (passes: count, %, cum%):");
                    double cumPct = 0;
                    for (int i = 0; i <= maxIter; i++)
                    {
                        if (h[i] == 0) continue;
                        double pct = total == 0 ? 0 : 100.0 * h[i] / total;
                        cumPct += pct;
                        Console.WriteLine($"#     {i,3}: {h[i],12:N0}  {pct,6:F2}%  {cumPct,6:F2}%");
                    }
                }
                {
                    // BFS group-walk DEPTH distribution (DEBUG only): max BFS level each AddNodeToGroup walk
                    // reached = hops through ON transistors from seed to the farthest conducting member.
                    // Depth 0 = singleton (no conducting neighbour). See WireCore.Group.cs / BfsDepthTally.
                    var h = WireCore.BfsDepthHist;
                    long walks = WireCore.BfsWalks;
                    long total = 0, sum = 0; int maxD = 0;
                    for (int i = 0; i < h.Length; i++) { total += h[i]; sum += h[i] * i; if (h[i] != 0) maxD = i; }
                    double mean = total == 0 ? 0 : (double)sum / total;
                    int Pct(double q) { long need = (long)System.Math.Ceiling(q * total); long cum = 0; for (int i = 0; i < h.Length; i++) { cum += h[i]; if (cum >= need) return i; } return maxD; }
                    Console.WriteLine($"# [bfs-depth-dist] AddNodeToGroup walks={walks:N0}  depth: mean={mean:F2} MAX={maxD} | p50={Pct(0.50)} p90={Pct(0.90)} p99={Pct(0.99)} p99.9={Pct(0.999)} p99.99={Pct(0.9999)}");
                    Console.WriteLine("#   histogram (depth: count, %, cum%):");
                    double cumPct = 0;
                    for (int i = 0; i <= maxD; i++)
                    {
                        if (h[i] == 0) continue;
                        double pct = total == 0 ? 0 : 100.0 * h[i] / total;
                        cumPct += pct;
                        Console.WriteLine($"#     {i,3}: {h[i],12:N0}  {pct,6:F2}%  {cumPct,6:F2}%");
                    }
                }
                {
                    // co-activity / cache-line headroom (DEBUG only) — the renumbering decision gate.
                    // headroom = mean distinct NodeInfo lines touched per half-cycle (current numbering)
                    // vs the perfect-packing floor ceil(distinctNodes/4). Near 1.0 ⇒ renumbering can't gain.
                    long w = WireCore.CoWindows;
                    if (w > 0)
                    {
                        double mPops = (double)WireCore.CoSumPops / w, mNodes = (double)WireCore.CoSumDistinctNodes / w, mLines = (double)WireCore.CoSumDistinctLines / w;
                        double ideal = mNodes / 4.0;
                        Console.WriteLine($"# [co-activity] windows={w:N0}  per-hc: pops={mPops:F1} distinct-nodes={mNodes:F1} NodeInfo-lines={mLines:F1} (ideal {ideal:F1}) headroom={(ideal > 0 ? mLines / ideal : 0):F2}x");
                        Console.WriteLine($"#   global hot set: {WireCore.CoGlobalNodes:N0} nodes ever popped, on {WireCore.CoGlobalLines:N0} lines ({WireCore.CoGlobalLines * 64 / 1024} KB of NodeInfos; ideal {(WireCore.CoGlobalNodes + 3) / 4:N0} lines = {((WireCore.CoGlobalNodes + 3) / 4) * 64 / 1024} KB)");
                        // (the --co-profile per-node dump was removed 2026-06-11 with the --renumber file
                        //  mode — the locality key is now SELF-CAPTURED at load; see WireCore.Renumber.cs.)
                    }
                }
                {
                    // CPU/PPU boundary profiler (DEBUG only) — PDES "split the two chips" feasibility.
                    // See WireCore.Recalc.cs InitBoundaryDiag / BoundaryPopTally.
                    long hc = halfCycles > 0 ? halfCycles : 1;
                    var cut = WireCore.DiagCut; var cr = WireCore.CutResolved;
                    double Ph(long x) => (double)x / hc;
                    Console.WriteLine($"# [cpu-ppu-boundary] over {halfCycles:N0} hc — cut-wire state-changes (per hc):");
                    Console.WriteLine($"#   data-bus db[7:0] (ids={cr[1]}): {cut[1]:N0} ({Ph(cut[1]):F4}/hc)  [also CPU<->RAM traffic — over-counts]");
                    Console.WriteLine($"#   reg-addr ab[2:0] (ids={cr[2]}): {cut[2]:N0} ({Ph(cut[2]):F4}/hc)  [also CPU bus — over-counts]");
                    Console.WriteLine($"#   rw line (ids={cr[5]}): {cut[5]:N0} ({Ph(cut[5]):F4}/hc)  [also CPU bus]");
                    Console.WriteLine($"#   *** io_ce PPU-select (ids={cr[3]}): {cut[3]:N0} ({Ph(cut[3]):F4}/hc)  <-- TRUE CPU<->PPU exchange trigger");
                    Console.WriteLine($"#   *** nmi PPU->CPU (ids={cr[4]}): {cut[4]:N0} ({Ph(cut[4]):F4}/hc)  <-- PPU->CPU coupling");
                    long both = WireCore.DiagHcBothChips, cpuO = WireCore.DiagHcCpuOnly, ppuO = WireCore.DiagHcPpuOnly, seen = WireCore.DiagHcSeen;
                    long quiet = hc - both - cpuO - ppuO;
                    double Pc(long x) => seen > 0 ? 100.0 * x / hc : 0;
                    Console.WriteLine($"#   per-hc chip activity: both={both:N0} ({Pc(both):F1}%) cpu-only={cpuO:N0} ({Pc(cpuO):F1}%) ppu-only={ppuO:N0} ({Pc(ppuO):F1}%) neither~={quiet:N0}");
                    Console.WriteLine($"#   total pops by domain: cpu={WireCore.DiagPopCpu:N0} ppu={WireCore.DiagPopPpu:N0} board={WireCore.DiagPopBoard:N0}");
                    // per-module pop histogram (which modules carry the work) — sorted desc.
                    var mn = WireCore.ModuleNames; var mp = WireCore.DiagModulePops;
                    if (mn != null && mp != null)
                    {
                        long tot = 0; foreach (var x in mp) tot += x;
                        var ord = new int[mn.Length];
                        for (int z = 0; z < ord.Length; z++) ord[z] = z;
                        System.Array.Sort(ord, (a, b) => mp[b].CompareTo(mp[a]));
                        Console.WriteLine($"#   pops by module (of {tot:N0} total):");
                        foreach (int k in ord) { if (mp[k] == 0) continue; Console.WriteLine($"#     {mn[k],-12} {mp[k],14:N0}  {(tot>0?100.0*mp[k]/tot:0),5:F1}%"); }
                        // PDES 2-way work balance + Amdahl ceiling under the SideOf() assignment.
                        long sN = WireCore.DiagSidePops[0], sC = WireCore.DiagSidePops[1], sP = WireCore.DiagSidePops[2];
                        double pc(long x) => tot > 0 ? 100.0 * x / tot : 0;
                        long heavy = System.Math.Max(sC, sP);
                        double ceilExcl = tot > 0 && heavy > 0 ? (double)tot / heavy : 0;            // neutral free
                        double ceilDup = tot > 0 && (heavy + sN) > 0 ? (double)tot / (heavy + sN) : 0; // neutral duplicated on both
                        Console.WriteLine($"#   PDES side split: cpu-side={sC:N0} ({pc(sC):F1}%) ppu-side={sP:N0} ({pc(sP):F1}%) neutral={sN:N0} ({pc(sN):F1}%)");
                        Console.WriteLine($"#   2-way speedup CEILING (sync-free, perfect): {ceilExcl:F2}x (neutral free) .. {ceilDup:F2}x (neutral on both)  [heavy side = {(sP>=sC?"PPU":"CPU")}]");
                    }
                }
#endif
                PrintRealtimeGap(stepsHz);
                WriteBenchLog(logDir, romPath, hcCount, halfCycles, secs, stepsHz, stateHash);
                if (_dumpStatesPath != null) DumpStates(_dumpStatesPath);
            }
            finally { WireCore.Shutdown(); }
            return 0;
        }

        // ── DIAGNOSTIC (--dump-states): write per-node state for A/B node-level diffing ──
        // (Nodes[] shells are freed by ReleaseBenchResidualState before the run; NodeStates is unmanaged
        //  and valid until Shutdown, so dump all NodeCount slots — empty slots read 0 on both sides.)
        private static unsafe void DumpStates(string path)
        {
            using var w = new StreamWriter(path);
            for (int nn = 0; nn < WireCore.NodeCount; nn++)
                w.WriteLine($"{nn} {WireCore.NodeStates[nn]}");
            Console.WriteLine($"# dumped {WireCore.NodeCount} node slots to {path}");
        }

        // NES NTSC runs at 1.789773 MHz CPU * 24 master-half-cycles/CPU-cycle = 42,954,552 hc/s,
        // i.e. 60.0988 frames/s * 714,732 hc/frame. Print how far our sim rate is from that.
        private const double NesRealtimeHcPerSec = 42_954_552.0;
        private const double NesRealtimeFps      = 60.0988;
        private const double NesHcPerFrame       = 714_732.0;   // 357,366 master clocks * 2 half-cycles
        private static void PrintRealtimeGap(double stepsHz)
        {
            double pct      = stepsHz / NesRealtimeHcPerSec * 100.0;
            double gap      = NesRealtimeHcPerSec / stepsHz;
            double fps      = stepsHz / NesHcPerFrame;            // simulated NES frames / real second
            double secPerFr = NesHcPerFrame / stepsHz;            // real seconds to render 1 NES frame
            Console.WriteLine($"# =============================================");
            Console.WriteLine($"#  PERFORMANCE: {stepsHz / 1000.0:F1}K hc/s  ({stepsHz:N0} hc/s)");
            Console.WriteLine($"#  vs NES NTSC real-time ({NesRealtimeHcPerSec / 1000.0:N0}K hc/s):");
            Console.WriteLine($"#    {pct:F3}% of real-time   ->   {gap:F1}x too slow");
            Console.WriteLine($"#    {fps:F3} simulated NES frames / real second  (real NES = {NesRealtimeFps:F1} fps)");
            Console.WriteLine($"#    {secPerFr:F2} s to render 1 frame  (real NES = {1.0 / NesRealtimeFps:F4} s/frame)");
            Console.WriteLine($"# =============================================");
        }

        // Build version embedded by the SetGitVersion MSBuild target (AssemblyMetadata): short git
        // commit + commit date, "-dirty" if built from uncommitted sources. Falls back to "unknown"
        // if absent (git unavailable at build time). Shown in bench output + written to the bench log.
        private static (string ver, string date) BuildVersion()
        {
            string ver = "unknown", date = "unknown";
            foreach (System.Reflection.AssemblyMetadataAttribute a in
                     typeof(TestRunner).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
            {
                if (a.Key == "GitVersion" && !string.IsNullOrEmpty(a.Value)) ver = a.Value!;
                else if (a.Key == "CommitDate" && !string.IsNullOrEmpty(a.Value)) date = a.Value!;
            }
            return (ver, date);
        }

        // ── Benchmark JSON log — one machine-parseable record per run, for a future
        //    "upload your result" aggregation mechanism. Console output is unchanged;
        //    this is an *additional* file: <logDir>/<machineGuid>-<user>-<UTCstamp>-csharp.log
        private static void WriteBenchLog(string logDir, string romPath, long benchHc, long halfCycles,
                                          double secs, double stepsHz, ulong checksum)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                string guid  = MachineGuid(logDir);
                string user  = Environment.UserName;
                var now      = DateTime.UtcNow;
                string stamp = now.ToString("yyyyMMddHHmmss");
                var (bv, bd) = BuildVersion();
                // <engine>-<stamp>-… so logs sort by engine, then chronologically.
                string file  = Path.Combine(logDir, $"csharp-{stamp}-{Safe(guid)}-{Safe(user)}.log");

                double pct      = stepsHz / NesRealtimeHcPerSec * 100.0;
                double gap      = NesRealtimeHcPerSec / stepsHz;
                double secPerFr = NesHcPerFrame / stepsHz;

                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"schema\": \"aprvisual-bench/2\",\n");
                sb.Append("  \"engine\": \"csharp\",\n");
                sb.Append($"  \"engineVersion\": \"{Esc(bv)}\",\n");
                sb.Append($"  \"commitDate\": \"{Esc(bd)}\",\n");
                sb.Append($"  \"timestampUtc\": \"{now:yyyy-MM-ddTHH:mm:ss}Z\",\n");
                sb.Append($"  \"machineGuid\": \"{Esc(guid)}\",\n");
                sb.Append($"  \"user\": \"{Esc(user)}\",\n");
                sb.Append($"  \"os\": \"{Esc(System.Runtime.InteropServices.RuntimeInformation.OSDescription)}\",\n");
                sb.Append($"  \"arch\": \"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}\",\n");
                sb.Append($"  \"cpuModel\": \"{Esc(CpuModel())}\",\n");
                sb.Append($"  \"cpuCount\": {Environment.ProcessorCount},\n");
                sb.Append($"  \"rom\": \"{Esc(Path.GetFileName(romPath))}\",\n");
                sb.Append($"  \"benchHc\": {benchHc},\n");
                sb.Append($"  \"halfCycles\": {halfCycles},\n");
                sb.Append($"  \"elapsedSec\": {secs:F6},\n");
                sb.Append($"  \"hcPerSec\": {stepsHz:F1},\n");
                sb.Append($"  \"secondsPerFrame\": {secPerFr:F4},\n");
                sb.Append($"  \"pctRealtime\": {pct:F4},\n");
                sb.Append($"  \"slowdownFactor\": {gap:F1},\n");
                sb.Append($"  \"checksum\": \"0x{checksum:X16}\",\n");
                sb.Append($"  \"fastPathNodes\": {WireCore.PureLogicNodeCount},\n");
                sb.Append($"  \"liveNodes\": {WireCore.NonNullNodeCount},\n");
                sb.Append($"  \"pinned\": {(_pinned ? "true" : "false")}\n");
                sb.Append("}\n");
                File.WriteAllText(file, sb.ToString());
                Console.WriteLine($"# log written: {file}");
            }
            catch (Exception ex) { Console.Error.WriteLine($"# (bench log write skipped: {ex.Message})"); }
        }

        // Stable per-machine id, consistent across the C# and Rust engines on the same machine:
        //   Windows → registry MachineGuid;  macOS → IOPlatformUUID;  else → a GUID cached in logDir.
        private static string MachineGuid(string logDir)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string o = RunShell("reg", @"query HKLM\SOFTWARE\Microsoft\Cryptography /v MachineGuid");
                    foreach (var line in o.Split('\n'))
                    {
                        int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
                        if (idx >= 0) { string g = line.Substring(idx + 6).Trim(); if (g.Length > 0) return g; }
                    }
                }
                else if (OperatingSystem.IsMacOS())
                {
                    string o = RunShell("ioreg", "-rd1 -c IOPlatformExpertDevice");
                    foreach (var line in o.Split('\n'))
                        if (line.Contains("IOPlatformUUID"))
                        {
                            int eq = line.IndexOf('='); int q1 = line.IndexOf('"', eq + 1); int q2 = line.IndexOf('"', q1 + 1);
                            if (q1 >= 0 && q2 > q1) return line.Substring(q1 + 1, q2 - q1 - 1);
                        }
                }
            }
            catch { }
            try
            {
                string f = Path.Combine(logDir, "machine.guid");
                if (File.Exists(f)) { string s = File.ReadAllText(f).Trim(); if (s.Length > 0) return s; }
                string g = Guid.NewGuid().ToString();
                File.WriteAllText(f, g);
                return g;
            }
            catch { return "unknown"; }
        }

        private static string CpuModel()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    string o = RunShell("reg", @"query ""HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0"" /v ProcessorNameString");
                    foreach (var line in o.Split('\n'))
                    {
                        int idx = line.IndexOf("REG_SZ", StringComparison.Ordinal);
                        if (idx >= 0) { string s = line.Substring(idx + 6).Trim(); if (s.Length > 0) return s; }
                    }
                }
                else if (OperatingSystem.IsMacOS())
                    return RunShell("sysctl", "-n machdep.cpu.brand_string").Trim();
            }
            catch { }
            return "unknown";
        }

        private static string RunShell(string exe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return o;
        }

        private static string Safe(string s)
        {
            var c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
                if (!char.IsLetterOrDigit(c[i]) && c[i] != '.' && c[i] != '-' && c[i] != '_') c[i] = '_';
            return new string(c);
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '"' || ch == '\\') { sb.Append('\\'); sb.Append(ch); }
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
