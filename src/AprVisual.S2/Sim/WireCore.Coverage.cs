using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim
{
    // ───────────────────────────────────────────────────────────────────────────
    //  Boolean-coverability probe (Escape-1 de-risking; analysis only, behaviour unchanged).
    //  Design: MD/S2/design/05. The question: how much of the chip is a CLEAN boolean function
    //  of its local (radius-1) inputs — i.e. compilable to oblivious straight-line logic — vs how
    //  much carries hidden state / analog behaviour (must stay switch-level)?
    //
    //  Method (Gemini's empirical idea, but consistency not formula): for each node, its candidate
    //  inputs = the gate nodes + normal far-end nodes of its channel transistors (radius-1). Over a
    //  golden run, record (inputVector -> resolved value). If the SAME inputVector ever yields a
    //  DIFFERENT value, the node is NOT a pure boolean function of its radius-1 neighbourhood ->
    //  it depends on hidden state / deeper paths / analog -> flag it. Clean (never-conflicting)
    //  nodes are the lower bound on what an oblivious logic compiler could cover.
    //
    //  Radius-1 is a LOWER BOUND (a pass-island OUTPUT is boolean in the island's inputs but may
    //  look inconsistent on radius-1); a high number here is therefore a strong positive signal.
    // ───────────────────────────────────────────────────────────────────────────
    internal static unsafe partial class WireCore
    {
        public static bool Coverage = false;
        internal const int CovMaxInputs = 60;     // pack into a ulong; wider => "complex", untracked

        private static int* _covBase;             // per-node index into _covInputs (0 = none/skip)
        private static ushort* _covInputs;        // flat: (k, id_0..id_{k-1}) per node
        private static byte* _covWide;            // 1 = > CovMaxInputs inputs (untracked, "complex")
        private static byte* _covSeen;            // 1 = observed at least once
        private static byte* _covStateful;        // 1 = saw a contradiction (NOT pure-boolean)
        private static Dictionary<ulong, byte>[]? _covMap;   // per-node inputVector -> value (lazy)

        public static void AllocCoverage()
        {
            _covBase     = AllocArray<int>(NodeCount);
            _covWide     = AllocArray<byte>(NodeCount);
            _covSeen     = AllocArray<byte>(NodeCount);
            _covStateful = AllocArray<byte>(NodeCount);
            _covMap      = new Dictionary<ulong, byte>[NodeCount];

            // Read channel structure from the UNMANAGED data (survives ClearPostLoadBuildState):
            // inline payload [c1c2 (gate,other) pairs][gnd gates][pwr gates], or the TransistorList
            // fallback for overflow nodes. inputs = gates + normal far-ends.
            var flat = new List<ushort> { 0 };
            var inputs = new List<int>();
            void AddIn(int id) { if (id != Npwr && id != Ngnd && !inputs.Contains(id)) inputs.Add(id); }
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (nn == Npwr || nn == Ngnd) continue;
                NodeInfo* ns = NodeInfos + nn;
                inputs.Clear();
                if (ns->Inline != 0)
                {
                    ushort* pay = ns->InlinePayload;
                    int n2 = ns->C1c2Count << 1;
                    for (int k = 0; k < n2; k += 2) { AddIn(pay[k]); AddIn(pay[k + 1]); }   // gate + far-end
                    int gndEnd = n2 + ns->GndCount, pwrEnd = gndEnd + ns->PwrCount;
                    for (int k = n2; k < pwrEnd; k++) AddIn(pay[k]);                          // gnd/pwr gates
                }
                else
                {
                    if (ns->TlistC1c2s != 0) { ushort* p = TransistorList + ns->TlistC1c2s; while (*p != 0) { AddIn(p[0]); AddIn(p[1]); p += 2; } }
                    if (ns->TlistC1gnd != 0) { ushort* p = TransistorList + ns->TlistC1gnd; while (*p != 0) AddIn(*p++); }
                    if (ns->TlistC1pwr != 0) { ushort* p = TransistorList + ns->TlistC1pwr; while (*p != 0) AddIn(*p++); }
                }
                if (inputs.Count == 0) continue;                  // no channels -> not interesting
                if (inputs.Count > CovMaxInputs) { _covWide[nn] = 1; continue; }
                _covBase[nn] = flat.Count;
                flat.Add((ushort)inputs.Count);
                foreach (int id in inputs) flat.Add((ushort)id);
            }
            _covInputs = AllocArray<ushort>(flat.Count);
            for (int i = 0; i < flat.Count; i++) _covInputs[i] = flat[i];
            Coverage = true;
        }

        // Called from SetNodeState (top, before the unchanged early-return) with the freshly resolved value.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static void CoverageObserve(int nn, byte value)
        {
            int b = _covBase[nn];
            if (b == 0 || _covStateful[nn] != 0) return;          // untracked (wide/no-channel) or already flagged
            ushort* p = _covInputs + b;
            int k = *p++;
            ulong key = 0;
            for (int i = 0; i < k; i++) key |= (ulong)NodeStates[p[i]] << i;
            _covSeen[nn] = 1;
            var map = _covMap![nn];
            if (map == null) { map = new Dictionary<ulong, byte>(); _covMap[nn] = map; }
            if (map.TryGetValue(key, out byte prev))
            {
                if (prev != value) { _covStateful[nn] = 1; _covMap[nn] = null; }   // contradiction -> not pure boolean
            }
            else map[key] = value;
        }

        public static void ReportCoverage()
        {
            Coverage = false;
            int live = 0, seen = 0, clean = 0, stateful = 0, wide = 0;
            long cpuClean = 0, cpuTot = 0, ppuClean = 0, ppuTot = 0, othClean = 0, othTot = 0;
            for (int nn = 0; nn < NodeCount; nn++)
            {
                if (nn == Npwr || nn == Ngnd || Nodes[nn] == null) continue;
                live++;
                if (_covWide[nn] != 0) wide++;
                bool tracked = _covSeen[nn] != 0;
                bool isClean = tracked && _covStateful[nn] == 0;
                if (tracked) { seen++; if (isClean) clean++; else stateful++; }
                // subsystem
                string name = GetNodeName(nn);
                int dot = name.IndexOf('.');
                string sub = dot > 0 ? name.Substring(0, dot) : "other";
                bool relevant = _covWide[nn] != 0 || tracked;
                if (relevant)
                {
                    bool ok = isClean;
                    if (sub.StartsWith("cpu")) { cpuTot++; if (ok) cpuClean++; }
                    else if (sub.StartsWith("ppu")) { ppuTot++; if (ok) ppuClean++; }
                    else { othTot++; if (ok) othClean++; }
                }
            }
            Console.WriteLine("# ========== BOOLEAN-COVERABILITY PROBE (radius-1, lower bound) ==========");
            Console.WriteLine($"#  live nodes: {live:N0}");
            Console.WriteLine($"#  observed (had channels, <= {CovMaxInputs} inputs, recalc'd): {seen:N0}");
            Console.WriteLine($"#    CLEAN boolean (same inputs -> same value, always): {clean:N0}  ({Pct(clean, seen):F1}% of observed, {Pct(clean, live):F1}% of live)");
            Console.WriteLine($"#    STATEFUL/analog (a contradiction was seen):       {stateful:N0}  ({Pct(stateful, seen):F1}% of observed)");
            Console.WriteLine($"#  wide (> {CovMaxInputs} inputs, untracked/complex): {wide:N0}");
            Console.WriteLine($"#  by subsystem (clean / total relevant):");
            Console.WriteLine($"#    cpu.*   {cpuClean:N0} / {cpuTot:N0}  ({Pct(cpuClean, cpuTot):F1}% clean)");
            Console.WriteLine($"#    ppu.*   {ppuClean:N0} / {ppuTot:N0}  ({Pct(ppuClean, ppuTot):F1}% clean)");
            Console.WriteLine($"#    other   {othClean:N0} / {othTot:N0}  ({Pct(othClean, othTot):F1}% clean)");
            Console.WriteLine("# =======================================================================");
        }
    }
}
