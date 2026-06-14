using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AprVisual.Sim
{
    // ── Faithful C# port of the ORIGINAL visual6502 algorithm (chipsim.js recursive group-walk
    //    + wires.js setupNodes/setupTransistors + macros.js / chip-*/support.js driving) ──
    //
    // Purpose: isolate ALGORITHM from LANGUAGE. tools/visual6502-node measures the original
    // algorithm in JavaScript; AprVisual.etc --cpu-bench measures OUR algorithm in C#. The two
    // differ in BOTH axes. This class runs the ORIGINAL algorithm in C# on the same raw netlist +
    // NOP-sled, so:  JS-naive vs C#-naive  = pure language;  C#-naive vs C#-ours = pure algorithm.
    //
    // It is a deliberately NAIVE, allocation-light transliteration of chipsim.js: object-per-node /
    // object-per-transistor, recursive addNodeToGroup, linear group membership (the JS indexOf), full
    // group re-resolution every recalc, no event prune / fast-path / inline payload / SoA. Pin lookups
    // are cached (idiomatic C#; the dict lookup is not the algorithm) — everything else mirrors the JS.
    internal sealed class NaiveSim
    {
        private sealed class NNode { public bool State, Pullup, Pulldown; public readonly List<int> Gates = new(); public readonly List<int> C1c2s = new(); }
        private struct NTrans { public bool On; public int Gate, C1, C2; }

        private readonly WireCore.RawCpuConfig _cfg;
        private NNode?[] _nodes = Array.Empty<NNode>();
        private NTrans[] _trans = Array.Empty<NTrans>();
        private Dictionary<string, int> _names = new();
        private int _ngnd, _npwr, _n;

        // settle scratch (mirrors chipsim.js globals group / recalclist / recalcHash)
        private readonly List<int> _group = new();
        private List<int> _recalcList = new();
        private bool[] _recalcHash = Array.Empty<bool>();

        // cached pins / buses / memory
        private int[] _ab = Array.Empty<int>(), _db = Array.Empty<int>();
        private byte[] _mem = Array.Empty<byte>();
        private int _pRw, _pClk0, _pClk, _pPhi1, _pPhi2, _pDbe, _pRd, _pMreq, _pWr, _pM1, _pIorq;
        private int _skippedWeak;

        public NaiveSim(WireCore.RawCpuConfig cfg) { _cfg = cfg; }

        // ───────────────────────── build (wires.js setup) ─────────────────────────

        private void Build(string dir)
        {
            var def = WireCore.ParseRawModuleDef(dir, _cfg.Name);
            if (!def.NodeNames.TryGetValue(_cfg.Vss, out _ngnd)) throw new InvalidOperationException($"no vss node '{_cfg.Vss}'");
            if (!def.NodeNames.TryGetValue(_cfg.Vcc, out _npwr)) throw new InvalidOperationException($"no vcc node '{_cfg.Vcc}'");

            int maxId = Math.Max(_ngnd, _npwr);
            foreach (int id in def.NodeNames.Values) if (id > maxId) maxId = id;
            foreach (var s in def.Segs) if (!s.Node.IsName && s.Node.Id > maxId) maxId = s.Node.Id;
            foreach (var t in def.Trans)
            {
                if (!t.Gate.IsName && t.Gate.Id > maxId) maxId = t.Gate.Id;
                if (!t.C1.IsName && t.C1.Id > maxId) maxId = t.C1.Id;
                if (!t.C2.IsName && t.C2.Id > maxId) maxId = t.C2.Id;
            }
            _n = maxId + 1;
            _nodes = new NNode?[_n];

            NNode N(int id) => _nodes[id] ??= new NNode();

            // setupNodes: first seg of a node decides pullup (chipsim.js: pullup: seg[1]=='+')
            foreach (var s in def.Segs)
            {
                int w = s.Node.Id;
                if (w < 0 || w >= _n) continue;
                if (_nodes[w] == null) _nodes[w] = new NNode { Pullup = s.Pull == '+' };
            }
            N(_ngnd); N(_npwr);

            // setupTransistors: no dedup, no c1==c2 drop (faithful); 6800/z80 skip weak
            var trans = new List<NTrans>(def.Trans.Count);
            foreach (var td in def.Trans)
            {
                if (_cfg.SkipWeak && td.IsWeak) { _skippedWeak++; continue; }
                int gate = td.Gate.Id, c1 = td.C1.Id, c2 = td.C2.Id;
                if (c1 == _ngnd) { c1 = c2; c2 = _ngnd; }
                if (c1 == _npwr) { c1 = c2; c2 = _npwr; }
                int idx = trans.Count;
                trans.Add(new NTrans { On = false, Gate = gate, C1 = c1, C2 = c2 });
                N(gate).Gates.Add(idx);
                N(c1).C1c2s.Add(idx);
                N(c2).C1c2s.Add(idx);
            }
            _trans = trans.ToArray();
            _recalcHash = new bool[_n];

            _names = def.NodeNames;
            _ab = ResolveBus("ab", 16);
            _db = ResolveBus("db", 8);
            _mem = new byte[65536];
            for (int i = 0; i < _mem.Length; i++) _mem[i] = (byte)_cfg.Nop;

            _pRw = Id("rw"); _pClk0 = Id("clk0"); _pClk = Id("clk");
            _pPhi1 = Id("phi1"); _pPhi2 = Id("phi2"); _pDbe = Id("dbe");
            _pRd = Id("_rd"); _pMreq = Id("_mreq"); _pWr = Id("_wr"); _pM1 = Id("_m1"); _pIorq = Id("_iorq");
        }

        private int Id(string name) => _names.TryGetValue(name, out int v) ? v : -1;
        private int[] ResolveBus(string p, int n) { var a = new int[n]; for (int i = 0; i < n; i++) a[i] = Id(p + i); return a; }

        // ───────────────────────── chipsim.js core (verbatim algorithm) ─────────────────────────

        private bool IsHigh(int nn) => nn >= 0 && _nodes[nn] != null && _nodes[nn]!.State;

        private void RecalcNodeList(List<int> list)
        {
            _recalcList = new List<int>();
            Array.Clear(_recalcHash);
            for (int j = 0; j < 100; j++)   // loop limiter (chipsim.js)
            {
                if (list.Count == 0) return;
                foreach (int node in list) RecalcNode(node);
                list = _recalcList;
                _recalcList = new List<int>();
                Array.Clear(_recalcHash);
            }
        }

        private void RecalcNode(int node)
        {
            if (node == _ngnd || node == _npwr) return;
            GetNodeGroup(node);
            bool newState = GetNodeValue();
            for (int gi = 0; gi < _group.Count; gi++)
            {
                var n = _nodes[_group[gi]];
                if (n == null || n.State == newState) continue;
                n.State = newState;
                var gates = n.Gates;
                for (int k = 0; k < gates.Count; k++)
                {
                    int ti = gates[k];
                    if (n.State) TurnOn(ti); else TurnOff(ti);
                }
            }
        }

        private void TurnOn(int ti) { if (_trans[ti].On) return; _trans[ti].On = true; AddRecalc(_trans[ti].C1); }
        private void TurnOff(int ti) { if (!_trans[ti].On) return; _trans[ti].On = false; AddRecalc(_trans[ti].C1); AddRecalc(_trans[ti].C2); }

        private void AddRecalc(int nn)
        {
            if (nn == _ngnd || nn == _npwr) return;
            if (_recalcHash[nn]) return;
            _recalcList.Add(nn);
            _recalcHash[nn] = true;
        }

        private void GetNodeGroup(int i) { _group.Clear(); AddNodeToGroup(i); }

        private void AddNodeToGroup(int i)
        {
            if (_group.Contains(i)) return;   // linear membership (chipsim.js group.indexOf)
            _group.Add(i);
            if (i == _ngnd || i == _npwr) return;
            var c = _nodes[i]!.C1c2s;
            for (int k = 0; k < c.Count; k++)
            {
                int ti = c[k];
                if (!_trans[ti].On) continue;
                int other = _trans[ti].C1 == i ? _trans[ti].C2 : _trans[ti].C1;
                AddNodeToGroup(other);
            }
        }

        private bool GetNodeValue()
        {
            if (_group.Contains(_ngnd)) return false;
            if (_group.Contains(_npwr)) return true;
            for (int gi = 0; gi < _group.Count; gi++)
            {
                var n = _nodes[_group[gi]];
                if (n == null) continue;
                if (n.Pullup) return true;
                if (n.Pulldown) return false;
                if (n.State) return true;
            }
            return false;
        }

        private readonly List<int> _one = new(1);
        private void SetHigh(int nn) { var x = _nodes[nn]!; x.Pullup = true; x.Pulldown = false; _one.Clear(); _one.Add(nn); RecalcNodeList(_one); }
        private void SetLow(int nn)  { var x = _nodes[nn]!; x.Pullup = false; x.Pulldown = true; _one.Clear(); _one.Add(nn); RecalcNodeList(_one); }
        private void SetHigh(string name) { int nn = Id(name); if (nn >= 0) SetHigh(nn); }
        private void SetLow(string name)  { int nn = Id(name); if (nn >= 0) SetLow(nn); }

        private List<int> AllNodes() { var l = new List<int>(_n); for (int i = 0; i < _n; i++) if (i != _ngnd && i != _npwr && _nodes[i] != null) l.Add(i); return l; }

        // ───────────────────────── bus / driving (macros.js + support.js) ─────────────────────────

        private int ReadBus(int[] nodes) { int v = 0; for (int i = 0; i < nodes.Length; i++) if (IsHigh(nodes[i])) v |= 1 << i; return v; }

        private readonly List<int> _dbRecalc = new(8);
        private void WriteDataBus(int value)
        {
            _dbRecalc.Clear();
            for (int i = 0; i < _db.Length; i++)
            {
                int nn = _db[i]; if (nn < 0) continue;
                var x = _nodes[nn]!;
                if (((value >> i) & 1) == 0) { x.Pulldown = true; x.Pullup = false; }
                else { x.Pulldown = false; x.Pullup = true; }
                _dbRecalc.Add(nn);
            }
            RecalcNodeList(_dbRecalc);
        }

        private void BusReadRwAbDb() { if (_pRw < 0 || IsHigh(_pRw)) WriteDataBus(_mem[ReadBus(_ab)]); }
        private void BusWriteRwAbDb() { if (_pRw >= 0 && !IsHigh(_pRw)) _mem[ReadBus(_ab)] = (byte)ReadBus(_db); }

        private void HalfStep()
        {
            switch (_cfg.Name)
            {
                case "6800":
                    if (IsHigh(_pPhi2)) { SetLow(_pPhi2); SetLow(_pDbe); SetHigh(_pPhi1); BusReadRwAbDb(); }
                    else { SetHigh(_pPhi1); SetLow(_pPhi1); SetHigh(_pPhi2); SetHigh(_pDbe); BusWriteRwAbDb(); }
                    break;
                case "z80":
                    if (IsHigh(_pClk)) SetLow(_pClk); else SetHigh(_pClk);
                    if (!IsHigh(_pRd) && !IsHigh(_pMreq)) WriteDataBus(_mem[ReadBus(_ab)]);
                    else if (_pM1 >= 0 && _pIorq >= 0 && !IsHigh(_pM1) && !IsHigh(_pIorq)) WriteDataBus(0xE9);
                    else WriteDataBus(0xFF);
                    if (_pWr >= 0 && !IsHigh(_pWr)) _mem[ReadBus(_ab)] = (byte)ReadBus(_db);
                    break;
                default: // 6502
                    if (IsHigh(_pClk0)) { SetLow(_pClk0); BusReadRwAbDb(); }
                    else { SetHigh(_pClk0); BusWriteRwAbDb(); }
                    break;
            }
        }

        private void InitChip()
        {
            for (int i = 0; i < _n; i++) { var x = _nodes[i]; if (x != null) { x.State = false; } }
            _nodes[_ngnd]!.State = false;
            _nodes[_npwr]!.State = true;
            for (int i = 0; i < _trans.Length; i++) _trans[i].On = false;

            switch (_cfg.Name)
            {
                case "6800":
                    SetLow("reset");
                    SetHigh("phi1"); SetLow("phi2"); SetLow("dbe");
                    SetHigh("dbe"); SetLow("tsc"); SetHigh("halt"); SetHigh("irq"); SetHigh("nmi");
                    RecalcNodeList(AllNodes());
                    for (int i = 0; i < 8; i++) { SetLow("phi1"); SetHigh("phi2"); SetHigh("dbe"); SetLow("phi2"); SetLow("dbe"); SetHigh("phi1"); }
                    SetHigh("reset");
                    for (int i = 0; i < 6; i++) HalfStep();
                    break;
                case "z80":
                    SetLow("_reset"); SetHigh("clk");
                    SetHigh("_busrq"); SetHigh("_int"); SetHigh("_nmi"); SetHigh("_wait");
                    RecalcNodeList(AllNodes());
                    for (int i = 0; i < 31; i++) HalfStep();
                    SetHigh("_reset");
                    break;
                default: // 6502
                    SetLow("res"); SetLow("clk0"); SetHigh("rdy"); SetLow("so"); SetHigh("irq"); SetHigh("nmi");
                    RecalcNodeList(AllNodes());
                    for (int i = 0; i < 8; i++) { SetHigh("clk0"); SetLow("clk0"); }
                    SetHigh("res");
                    for (int i = 0; i < 18; i++) HalfStep();
                    break;
            }
        }

        // ───────────────────────── bench ─────────────────────────

        public static int RunBench(string dir, string chipName, int benchHc, int warmup, int rounds)
        {
            if (!WireCore.RawCpus.TryGetValue(chipName, out var cfg))
            {
                Console.Error.WriteLine($"unknown chip '{chipName}' (have: {string.Join(", ", WireCore.RawCpus.Keys)})");
                return 2;
            }
            foreach (var f in new[] { "nodenames.js", "segdefs.js", "transdefs.js" })
                if (!File.Exists(Path.Combine(dir, f))) { Console.Error.WriteLine($"missing {f} in {dir}"); return 2; }
            if (benchHc <= 0) benchHc = 100_000;
            if (rounds < 1) rounds = 1;
            if (warmup < 0) warmup = 0;

            var sim = new NaiveSim(cfg);
            var swLoad = Stopwatch.StartNew();
            sim.Build(dir);
            swLoad.Stop();

            int nodeCount = 0; for (int i = 0; i < sim._n; i++) if (sim._nodes[i] != null) nodeCount++;

            Console.WriteLine($"# AprVisual.etc NAIVE C# port (original visual6502 algorithm, recursive group-walk) — chip {cfg.Name}");
            Console.WriteLine($"#   netlist dir: {dir}");
            Console.WriteLine($"#   nodes: {nodeCount}   transistors: {sim._trans.Length}" + (cfg.SkipWeak ? $"   (skipped {sim._skippedWeak} weak)" : ""));
            Console.WriteLine($"#   workload: Infinite NOP Sled (opcode 0x{cfg.Nop:X2})   [no prune / fast-path / SoA — faithful port]");
            Console.WriteLine($"#   load (parse + setup): {swLoad.Elapsed.TotalSeconds:F2} s");

            sim.InitChip();
            int ab0 = sim.ReadBus(sim._ab);
            for (int i = 0; i < warmup; i++) sim.HalfStep();
            int ab1 = sim.ReadBus(sim._ab);
            bool advancing = ab0 != ab1;

            var rates = new double[rounds];
            for (int r = 0; r < rounds; r++)
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < benchHc; i++) sim.HalfStep();
                sw.Stop();
                rates[r] = benchHc / sw.Elapsed.TotalSeconds;
                advancing |= sim.ReadBus(sim._ab) != ab1;
            }

            Console.WriteLine($"#   AB sample: post-reset=0x{ab0:X4}  post-warmup=0x{ab1:X4}  " + (advancing ? "(advancing — CPU running)" : "(NOT advancing — check harness)"));
            Console.WriteLine("#   " + new string('-', 50));
            for (int r = 0; r < rounds; r++) Console.WriteLine($"#   round {r + 1}: {rates[r]:N0} hc/s");
            var sorted = (double[])rates.Clone(); Array.Sort(sorted);
            double median = sorted[sorted.Length / 2], best = sorted[sorted.Length - 1];
            double mean = 0; foreach (var v in rates) mean += v; mean /= rates.Length;
            Console.WriteLine("#   " + new string('-', 50));
            Console.WriteLine($"#   median: {median:N0} hc/s   best: {best:N0}   mean: {mean:N0}   ({benchHc:N0} hc/round, warmup {warmup:N0})");
            return advancing ? 0 : 1;
        }
    }
}
