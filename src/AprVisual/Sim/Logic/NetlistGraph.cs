using System;
using System.Collections.Generic;
using System.Linq;

namespace AprVisual.Sim.Logic
{
    // ── S2.0: a static-analysis view of the (S1.5-lowered) switch-level netlist —
    //    per-node "role" + per-transistor 1-transistor "kind" classification. This is the foundation
    //    later S2 steps build on (S2.1 drive-structure analysis, S2.2 Expr extraction, S2.3 SCC, …).
    //
    //    Separate from WireCore (S1's runtime engine) — different lifecycle, different concern — and
    //    *instantiable* (not static) so later steps can build a graph from a small hand-built netlist
    //    for unit tests. Building it does NOT touch / depend on the simulation; call BuildFrom() after
    //    WireCore.ComposeSystem() (which has already run LowerNetlist()).
    //
    //    See MD/impl/S2/01_S2.0_靜態圖_設計.md (incl. the Gemini review that shaped it).

    /// <summary>How a node is driven from the rest of the system.</summary>
    internal enum NodeRole
    {
        Internal,  // driven by the netlist itself (default)
        Supply,    // vcc / vss
        Input,     // never a transistor channel end + no pull-up — only ever driven by external code (clk, …)
        Bus,       // bidirectional: driven both by the netlist and by a behavioral handler (cpu.db[], ppu.io_db[], …)
    }

    /// <summary>1-transistor physical classification (no topology — series/parallel is S2.1).</summary>
    internal enum TransistorKind
    {
        Pass,           // both channel ends are signal nodes — a pass/transmission transistor, or the middle of a pull-down chain (S2.1 disambiguates)
        PullDown,       // a channel end is GND  (the leaf of a pull-down network)
        PullUpStrong,   // a channel end is VCC and gate is VCC — an unconditional VCC tie / strong pull-up
        PullUpLoad,     // a channel end is VCC and gate is the other (signal) channel end — the classic diode-connected NMOS depletion load
        PullUpActive,   // a channel end is VCC and gate is some other signal — a conditional pull-up (superbuffer / push-pull upper half / precharge clock)
        Dead,           // gate is GND — can never conduct (S1.5 lowering removes these; kept for safety)
    }

    /// <summary>Direction of a Pass transistor (filled by S2.1's directionality heuristics; Unknown until then).</summary>
    internal enum PassDir { Unknown, AtoB, BtoA, Bidirectional }

    internal sealed class NetlistGraph
    {
        public NodeRole[] Role = [];            // [nodeId]; null node slots stay Internal but are unused
        public TransistorKind[] Kind = [];      // [transistorIndex]
        public bool[] TransIsWeak = [];         // [transistorIndex] — copy of Transistor.IsWeak (handy for S2.1)
        public PassDir[] PassDirection = [];    // [transistorIndex] — meaningful only when Kind==Pass; Unknown until S2.1

        public int NodeArrayLength => Role.Length;
        public int TransistorCount => Kind.Length;

        private const int Npwr = WireCore.Npwr;   // 1
        private const int Ngnd = WireCore.Ngnd;   // 2

        /// <summary>Build a NetlistGraph from the netlist currently composed in WireCore (after ComposeSystem / LowerNetlist).</summary>
        public static NetlistGraph BuildFrom()
        {
            var nodes = WireCore.Nodes;
            var trans = WireCore.Transistors;
            var g = new NetlistGraph
            {
                Role = new NodeRole[nodes.Count],
                Kind = new TransistorKind[trans.Count],
                TransIsWeak = new bool[trans.Count],
                PassDirection = new PassDir[trans.Count],
            };

            // ── transistor kinds (1-transistor classification) ──
            for (int i = 0; i < trans.Count; i++)
            {
                var t = trans[i];
                g.TransIsWeak[i] = t.IsWeak;
                // PassDirection[i] defaults to Unknown
                if (t.Gate == Ngnd) { g.Kind[i] = TransistorKind.Dead; continue; }   // gate tied to GND — never conducts
                bool c1Gnd = t.C1 == Ngnd, c2Gnd = t.C2 == Ngnd;
                bool c1Pwr = t.C1 == Npwr, c2Pwr = t.C2 == Npwr;
                if (c1Gnd || c2Gnd) { g.Kind[i] = TransistorKind.PullDown; continue; }
                if (c1Pwr || c2Pwr)
                {
                    int sig = c1Pwr ? t.C2 : t.C1;   // the non-VCC channel end
                    g.Kind[i] = t.Gate == Npwr ? TransistorKind.PullUpStrong
                              : t.Gate == sig  ? TransistorKind.PullUpLoad
                                               : TransistorKind.PullUpActive;
                    continue;
                }
                g.Kind[i] = TransistorKind.Pass;   // both channel ends are signal nodes
            }

            // ── node roles ──
            // Role defaults to Internal (enum value 0).
            if (Npwr < g.Role.Length) g.Role[Npwr] = NodeRole.Supply;
            if (Ngnd < g.Role.Length) g.Role[Ngnd] = NodeRole.Supply;
            for (int n = 0; n < nodes.Count; n++)
            {
                if (g.Role[n] == NodeRole.Supply) continue;
                var node = nodes[n];
                if (node == null) continue;
                // Input: never a transistor channel end + no pull-up → only ever driven by external code (clk, …)
                if (node.C1c2s.Count == 0 && node.Pullups == 0) g.Role[n] = NodeRole.Input;
            }
            // Bus (pre-fill — refined in S2.1's RefineBuses()): the two main external-facing data buses,
            // which are genuinely bidirectional (CPU drives on writes; RAM/ROM/PPU handlers drive on reads).
            foreach (var expr in new[] { "cpu.db[7:0]", "ppu.io_db[7:0]" })
            {
                var ids = new List<int>();
                WireCore.ResolveNodes(expr, ids, quiet: true);
                foreach (int id in ids)
                    if (id >= 0 && id < g.Role.Length && g.Role[id] != NodeRole.Supply)
                        g.Role[id] = NodeRole.Bus;
            }

            return g;
        }

        // ── stats / introspection ──

        public (int supply, int input, int bus, int internal_) CountByRole()
        {
            int s = 0, i = 0, b = 0, n = 0;
            var nodes = WireCore.Nodes;
            for (int id = 0; id < Role.Length; id++)
            {
                if (id < nodes.Count && nodes[id] == null) continue;   // skip the reserved/unused slots
                switch (Role[id])
                {
                    case NodeRole.Supply: s++; break;
                    case NodeRole.Input: i++; break;
                    case NodeRole.Bus: b++; break;
                    default: n++; break;
                }
            }
            return (s, i, b, n);
        }

        public int[] CountByKind()
        {
            var c = new int[Enum.GetValues<TransistorKind>().Length];
            foreach (var k in Kind) c[(int)k]++;
            return c;
        }

        public string Describe(int nodeId)
        {
            if (nodeId < 0 || nodeId >= Role.Length) return $"#{nodeId} (out of range)";
            var node = nodeId < WireCore.Nodes.Count ? WireCore.Nodes[nodeId] : null;
            int pd = 0, pu = 0, ps = 0;
            if (node != null)
                foreach (int tid in node.C1c2s)
                    switch (Kind[tid])
                    {
                        case TransistorKind.PullDown: pd++; break;
                        case TransistorKind.PullUpStrong:
                        case TransistorKind.PullUpLoad:
                        case TransistorKind.PullUpActive: pu++; break;
                        case TransistorKind.Pass: ps++; break;
                    }
            return $"{WireCore.GetNodeName(nodeId)}#{nodeId}  role={Role[nodeId]}  pulldownTrans={pd}  pullupTrans={pu}  passTrans={ps}  pullups={(node?.Pullups ?? 0)}";
        }

        /// <summary>Run the exhaustive + sanity assertions (S2.0 §1.7). Returns "OK" or the first violation.</summary>
        public string SelfCheck()
        {
            var nodes = WireCore.Nodes;
            var trans = WireCore.Transistors;

            // exhaustive: every transistor has exactly one kind; every non-null node has a role
            if (CountByKind().Sum() != trans.Count) return $"FAIL: CountByKind sums to {CountByKind().Sum()}, expected {trans.Count}";
            int nonNullNodes = 0; for (int n = 0; n < nodes.Count; n++) if (nodes[n] != null) nonNullNodes++;
            var (s, i, b, nn) = CountByRole();
            if (s + i + b + nn != nonNullNodes) return $"FAIL: CountByRole sums to {s + i + b + nn}, expected {nonNullNodes} non-null nodes";
            if (s != 2) return $"FAIL: expected exactly 2 Supply nodes, got {s}";
            if (Role.Length > Npwr && Role[Npwr] != NodeRole.Supply) return "FAIL: node 1 (vcc) is not Supply";
            if (Role.Length > Ngnd && Role[Ngnd] != NodeRole.Supply) return "FAIL: node 2 (vss) is not Supply";
            if (CountByKind()[(int)TransistorKind.Dead] != 0) return $"FAIL: {CountByKind()[(int)TransistorKind.Dead]} Dead transistor(s) — S1.5 lowering should have removed every gate==vss transistor";

            // per-transistor sanity cross-checks
            for (int t = 0; t < trans.Count; t++)
            {
                var tr = trans[t];
                switch (Kind[t])
                {
                    case TransistorKind.PullDown:
                        if (tr.C1 != Ngnd && tr.C2 != Ngnd) return $"FAIL: #{t} '{tr.Name}' PullDown but neither channel end is vss";
                        break;
                    case TransistorKind.PullUpLoad:
                    {
                        bool c1Pwr = tr.C1 == Npwr, c2Pwr = tr.C2 == Npwr;
                        if (!(c1Pwr || c2Pwr)) return $"FAIL: #{t} '{tr.Name}' PullUpLoad but neither channel end is vcc";
                        int sig = c1Pwr ? tr.C2 : tr.C1;
                        if (tr.Gate != sig) return $"FAIL: #{t} '{tr.Name}' PullUpLoad but gate ({tr.Gate}) != non-vcc channel end ({sig})";
                        break;
                    }
                    case TransistorKind.PullUpStrong:
                        if (tr.Gate != Npwr) return $"FAIL: #{t} '{tr.Name}' PullUpStrong but gate != vcc";
                        if (tr.C1 != Npwr && tr.C2 != Npwr) return $"FAIL: #{t} '{tr.Name}' PullUpStrong but neither channel end is vcc";
                        break;
                    case TransistorKind.PullUpActive:
                        if (tr.C1 != Npwr && tr.C2 != Npwr) return $"FAIL: #{t} '{tr.Name}' PullUpActive but neither channel end is vcc";
                        if (tr.Gate == Npwr) return $"FAIL: #{t} '{tr.Name}' PullUpActive but gate == vcc (should be PullUpStrong)";
                        break;
                    case TransistorKind.Pass:
                        if (tr.C1 == Npwr || tr.C1 == Ngnd || tr.C2 == Npwr || tr.C2 == Ngnd) return $"FAIL: #{t} '{tr.Name}' Pass but a channel end is a supply";
                        break;
                    case TransistorKind.Dead:
                        if (tr.Gate != Ngnd) return $"FAIL: #{t} '{tr.Name}' Dead but gate != vss";
                        break;
                }
            }
            return "OK";
        }
    }
}
