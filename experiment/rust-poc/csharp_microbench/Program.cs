// Synthetic microbenchmark of the switch-level "settle to quiescence" hot loop.
// Mirrors experiment/rust-poc/src/main.rs (same algorithm, same RNG, same graph),
// isolates language & compiler effect on this exact workload.
//
// Run: dotnet run -c Release -- [N_NODES] [N_TRANSISTORS] [N_ITERS] [SEED]
//      defaults: 15164  27305  100000  42

using System;
using System.Runtime.InteropServices;

unsafe class Program
{
    const int NPWR = 0;
    const int NGND = 1;

    const byte FLAG_NONE = 0;
    const byte FLAG_GND = 1 << 0;
    const byte FLAG_PWR = 1 << 1;
    const byte FLAG_PULLUP = 1 << 2;
    const byte FLAG_SETHIGH = 1 << 3;
    const byte FLAG_SETLOW = 1 << 4;
    const byte FLAG_STATE = 1 << 5;
    const byte FLAG_FORCE_COMPUTE = 1 << 6;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct NodeInfo {
        public byte Flags;
        public byte _pad0, _pad1, _pad2;
        public int Connections;
        public int TlistGates;
        public int TlistC1c2s;
        public int TlistC1gnd;
        public int TlistC1pwr;
    }

    // LCG matching the Rust version exactly.
    struct Lcg {
        public ulong State;
        public Lcg(ulong seed) { State = seed * 2862933555777941757UL + 3037000493UL; }
        public ulong Next() { State = State * 2862933555777941757UL + 3037000493UL; return State; }
        public uint NextRange(uint n) { return (uint)(Next() >> 33) % n; }
    }

    // === Bench state (matches Rust layout) ===
    static int NodeCount;
    static byte* NodeStates;
    static NodeInfo* NodeInfos;
    static int* TransistorList;
    static int TransistorListLen;
    static byte[] FlagsToState = new byte[256];
    static int* RecalcList;
    static int* RecalcListNext;
    static byte* RecalcHash;
    static byte* RecalcHashNext;
    static int* GroupBuf;
    static byte* InGroup;
    static int GroupCount;
    static byte GroupFlagsAcc;
    static byte MaxState;
    static int MaxConnections;
    static int ListCount, ListNextCount;

    static void* Alloc(int bytes) {
        return NativeMemory.AlignedAlloc((nuint)bytes, 64);
    }

    static void BuildFtsTable() {
        for (int i = 0; i < 256; i++) {
            byte f = (byte)i;
            byte g = f;
            if ((g & FLAG_FORCE_COMPUTE) != 0 && (g & FLAG_GND) != 0 && (g & FLAG_PWR) != 0) {
                g &= unchecked((byte)~(FLAG_GND | FLAG_PWR));
            }
            byte v = (byte)((g & FLAG_GND) != 0 ? 0 : (g & FLAG_PWR) != 0 ? 1
                          : (g & FLAG_SETHIGH) != 0 ? 1 : (g & FLAG_SETLOW) != 0 ? 0
                          : (g & FLAG_PULLUP) != 0 ? 1 : (g & FLAG_STATE) != 0 ? 1 : 0);
            FlagsToState[i] = v;
        }
    }

    static void BuildSynthetic(int nTrans, ref Lcg rng) {
        var gates = new System.Collections.Generic.List<int>[NodeCount];
        var c1c2s = new System.Collections.Generic.List<(int g, int o)>[NodeCount];
        var c1gnd = new System.Collections.Generic.List<int>[NodeCount];
        var c1pwr = new System.Collections.Generic.List<int>[NodeCount];
        for (int i = 0; i < NodeCount; i++) {
            gates[i] = new(); c1c2s[i] = new(); c1gnd[i] = new(); c1pwr[i] = new();
        }
        for (int t = 0; t < nTrans; t++) {
            int gate = 2 + (int)rng.NextRange((uint)NodeCount - 2);
            uint kind = rng.NextRange(8);
            int c1, c2;
            if (kind < 6) {
                c1 = 2 + (int)rng.NextRange((uint)NodeCount - 2);
                c2 = 2 + (int)rng.NextRange((uint)NodeCount - 2);
                if (c2 == c1) c2 = c2 + 1 < NodeCount ? c2 + 1 : 2;
            } else if (kind == 6) {
                c1 = 2 + (int)rng.NextRange((uint)NodeCount - 2);
                c2 = NGND;
            } else {
                c1 = 2 + (int)rng.NextRange((uint)NodeCount - 2);
                c2 = NPWR;
            }
            gates[gate].Add(c1);
            gates[gate].Add(c2);
            if (c2 == NGND) c1gnd[c1].Add(gate);
            else if (c2 == NPWR) c1pwr[c1].Add(gate);
            else { c1c2s[c1].Add((gate, c2)); c1c2s[c2].Add((gate, c1)); }
        }

        // build flat TransistorList (size estimate: 1 sentinel + sum)
        int totalLen = 1;
        for (int nn = 0; nn < NodeCount; nn++) {
            if (gates[nn].Count > 0) totalLen += gates[nn].Count + 1;
            if (c1c2s[nn].Count > 0) totalLen += c1c2s[nn].Count * 2 + 1;
            if (c1gnd[nn].Count > 0) totalLen += c1gnd[nn].Count + 1;
            if (c1pwr[nn].Count > 0) totalLen += c1pwr[nn].Count + 1;
        }
        TransistorListLen = totalLen;
        TransistorList = (int*)Alloc(totalLen * sizeof(int));
        for (int i = 0; i < totalLen; i++) TransistorList[i] = 0;
        int idx = 1;
        for (int nn = 0; nn < NodeCount; nn++) {
            ref NodeInfo ni = ref NodeInfos[nn];
            ni.Connections = c1c2s[nn].Count + gates[nn].Count / 2;
            ni.Flags = (byte)((nn != NPWR && nn != NGND && rng.NextRange(4) == 0) ? FLAG_PULLUP : FLAG_NONE);
            if (nn == NPWR) ni.Flags = FLAG_PWR;
            if (nn == NGND) ni.Flags = FLAG_GND;
            if ((ni.Flags & FLAG_PULLUP) != 0) NodeStates[nn] = 1;
            if (nn == NPWR) NodeStates[nn] = 1;

            if (gates[nn].Count > 0) {
                ni.TlistGates = idx;
                foreach (int v in gates[nn]) TransistorList[idx++] = v;
                TransistorList[idx++] = 0;
            }
            if (c1c2s[nn].Count > 0) {
                ni.TlistC1c2s = idx;
                foreach (var (g, o) in c1c2s[nn]) { TransistorList[idx++] = g; TransistorList[idx++] = o; }
                TransistorList[idx++] = 0;
            }
            if (c1gnd[nn].Count > 0) {
                ni.TlistC1gnd = idx;
                foreach (int v in c1gnd[nn]) TransistorList[idx++] = v;
                TransistorList[idx++] = 0;
            }
            if (c1pwr[nn].Count > 0) {
                ni.TlistC1pwr = idx;
                foreach (int v in c1pwr[nn]) TransistorList[idx++] = v;
                TransistorList[idx++] = 0;
            }
        }
    }

    static void AddNodeToGroup(int nn) {
        if (InGroup[nn] != 0) return;
        InGroup[nn] = 1;
        ref NodeInfo ni = ref NodeInfos[nn];
        GroupBuf[GroupCount++] = nn;
        if (ni.Connections > MaxConnections) { MaxState = NodeStates[nn]; MaxConnections = ni.Connections; }
        RecalcHash[nn] = 0;
        GroupFlagsAcc |= ni.Flags;
        if (ni.TlistC1c2s != 0) {
            int* p = TransistorList + ni.TlistC1c2s;
            while (*p != 0) {
                int gate = *p++;
                int other = *p++;
                if (NodeStates[gate] != 0) AddNodeToGroup(other);
            }
        }
        if (ni.TlistC1gnd != 0) {
            int* p = TransistorList + ni.TlistC1gnd;
            while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { GroupFlagsAcc |= FLAG_GND; break; } }
        }
        if (ni.TlistC1pwr != 0) {
            int* p = TransistorList + ni.TlistC1pwr;
            while (*p != 0) { int gate = *p++; if (NodeStates[gate] != 0) { GroupFlagsAcc |= FLAG_PWR; break; } }
        }
    }

    static byte ComputeNodeGroup(int nn) {
        for (int i = 0; i < GroupCount; i++) InGroup[GroupBuf[i]] = 0;
        GroupFlagsAcc = FLAG_NONE;
        GroupCount = 0;
        MaxState = 0;
        MaxConnections = 0;
        AddNodeToGroup(nn);
        byte f = GroupFlagsAcc;
        if ((f & FLAG_FORCE_COMPUTE) != 0 && (f & FLAG_GND) != 0 && (f & FLAG_PWR) != 0) {
            f &= unchecked((byte)~(FLAG_GND | FLAG_PWR));
        }
        return f == FLAG_NONE ? MaxState : FlagsToState[f];
    }

    static void SetNodeState(int nn, byte newState) {
        if (NodeStates[nn] == newState) return;
        NodeStates[nn] = newState;
        ref NodeInfo ni = ref NodeInfos[nn];
        if (ni.TlistGates != 0) {
            int* p = TransistorList + ni.TlistGates;
            while (*p != 0) {
                int c1 = *p++;
                int c2 = *p++;
                if (c1 != NPWR && c1 != NGND) {
                    if (RecalcHashNext[c1] == 0) {
                        RecalcListNext[ListNextCount++] = c1;
                        RecalcHashNext[c1] = 1;
                    }
                }
                if (newState == 0 && c2 != NPWR && c2 != NGND) {
                    if (RecalcHashNext[c2] == 0) {
                        RecalcListNext[ListNextCount++] = c2;
                        RecalcHashNext[c2] = 1;
                    }
                }
            }
        }
    }

    static void RecalcNode(int nn) {
        if (nn == NPWR || nn == NGND) return;
        byte newState = ComputeNodeGroup(nn);
        int gc = GroupCount;
        for (int i = 0; i < gc; i++) SetNodeState(GroupBuf[i], newState);
    }

    static void Enqueue(int nn) {
        if (nn == NPWR || nn == NGND) return;
        if (RecalcHashNext[nn] == 0) {
            RecalcListNext[ListNextCount++] = nn;
            RecalcHashNext[nn] = 1;
        }
    }

    static void ProcessQueue() {
        int iters = 0;
        const int MAX_PASSES = 1000;
        while (ListNextCount != 0) {
            iters++;
            if (iters > MAX_PASSES) {
                for (int i = 0; i < ListNextCount; i++) RecalcHashNext[RecalcListNext[i]] = 0;
                ListNextCount = 0; break;
            }
            // swap next ↔ current
            int* tmpL = RecalcList; RecalcList = RecalcListNext; RecalcListNext = tmpL;
            byte* tmpH = RecalcHash; RecalcHash = RecalcHashNext; RecalcHashNext = tmpH;
            ListCount = ListNextCount;
            ListNextCount = 0;
            for (int i = 0; i < ListCount; i++) {
                int nn = RecalcList[i];
                if (RecalcHash[nn] != 0) { RecalcNode(nn); RecalcHash[nn] = 0; }
            }
            ListCount = 0;
        }
    }

    static int Main(string[] args) {
        int nNodes = args.Length > 0 ? int.Parse(args[0]) : 15164;
        int nTrans = args.Length > 1 ? int.Parse(args[1]) : 27305;
        int nIters = args.Length > 2 ? int.Parse(args[2]) : 100_000;
        ulong seed = args.Length > 3 ? ulong.Parse(args[3]) : 42UL;

        Console.WriteLine("# wire_microbench (C#)");
        Console.WriteLine($"# nodes={nNodes}  transistors={nTrans}  iters={nIters}  seed={seed}");

        NodeCount = nNodes;
        NodeStates = (byte*)Alloc(NodeCount);
        for (int i = 0; i < NodeCount; i++) NodeStates[i] = 0;
        NodeInfos = (NodeInfo*)Alloc(NodeCount * sizeof(NodeInfo));
        for (int i = 0; i < NodeCount; i++) NodeInfos[i] = default;
        BuildFtsTable();
        var rngBuild = new Lcg(seed);
        BuildSynthetic(nTrans, ref rngBuild);

        RecalcList = (int*)Alloc(NodeCount * sizeof(int));
        RecalcListNext = (int*)Alloc(NodeCount * sizeof(int));
        RecalcHash = (byte*)Alloc(NodeCount);
        RecalcHashNext = (byte*)Alloc(NodeCount);
        GroupBuf = (int*)Alloc(NodeCount * sizeof(int));
        InGroup = (byte*)Alloc(NodeCount);
        for (int i = 0; i < NodeCount; i++) { RecalcList[i] = 0; RecalcListNext[i] = 0; RecalcHash[i] = 0; RecalcHashNext[i] = 0; GroupBuf[i] = 0; InGroup[i] = 0; }
        ListCount = ListNextCount = 0;

        // initial settle
        for (int nn = 2; nn < NodeCount; nn++) Enqueue(nn);
        ProcessQueue();

        // warmup
        var rng = new Lcg(seed ^ 0x5A5A5A5A5A5A5A5AUL);
        for (int i = 0; i < 1000; i++) {
            int nn = 2 + (int)rng.NextRange((uint)(NodeCount - 2));
            NodeStates[nn] = (byte)(1 - NodeStates[nn]);
            Enqueue(nn);
            ProcessQueue();
        }

        // bench
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < nIters; i++) {
            int nn = 2 + (int)rng.NextRange((uint)(NodeCount - 2));
            NodeStates[nn] = (byte)(1 - NodeStates[nn]);
            Enqueue(nn);
            ProcessQueue();
        }
        sw.Stop();
        ulong checksum = 14695981039346656037UL;
        for (int i = 0; i < NodeCount; i++) {
            checksum ^= NodeStates[i];
            checksum *= 1099511628211UL;
        }
        double secs = sw.Elapsed.TotalSeconds;
        double rate = nIters / secs;
        double perIterUs = secs * 1e6 / nIters;
        Console.WriteLine($"# iters: {nIters}  time: {secs:F3} s  rate: {rate:F0} iter/s  per-iter: {perIterUs:F2} µs");
        Console.WriteLine($"# NodeStates checksum: 0x{checksum:X16}");
        return 0;
    }
}
