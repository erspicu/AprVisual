using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AprVisual.Sim
{
    // ── S2 gate-level EXPORT (load-time) ───────────────────────────────────────────────────────────────
    //
    // Turns the recognized gate-level structure (WireCore.GateClass.cs) into two human/tool-readable
    // artifacts, so the 43% static-logic core stops being a hairball of transistors and becomes a netlist
    // you can trace, diff against a high-level emulator, and open in standard EDA viewers:
    //
    //   <base>.v    structural Verilog. EXACT boolean for NOR/inverter outputs (the depletion-load gate =
    //               ~(OR of its pull-down gate inputs)); SeriesGate (NAND/AOI) and the dynamic/bus classes
    //               are emitted as typed wires/regs with their fan-in listed and an honest annotation that
    //               the closed-form boolean was NOT synthesized (series-parallel reduction is future work,
    //               and bridge networks aren't SP-reducible) — so nothing emitted is a WRONG equation.
    //   <base>.dot  Graphviz signal-flow graph: one node per live node, colored by GateClass, edges from
    //               each gate's fan-in signals to its output. Clustered by name prefix (cpu / ppu / ...).
    //
    // Plus a register/latch boundary summary: named nodes grouped by base name (a0..a7 -> a[7:0]) so the
    // raw NMOS node ids read back as the multi-bit registers an emulator dev actually reasons about.
    //
    // Behavioral, not bit-exact; the engine is untouched. Invoked via TestRunner --gate-export <rom> --out <base>.
    internal static unsafe partial class WireCore
    {
        private static List<int> GndGateList(NodeInfo* ns)
        {
            var r = new List<int>();
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload; int s = ns->C1c2Count << 1; int e = s + ns->GndCount;
                for (int k = s; k < e; k++) r.Add(pay[k]);
            }
            else if (ns->TlistC1gnd != 0)
            {
                ushort* p = TransistorList + ns->TlistC1gnd; while (*p != 0) { r.Add(*p); p++; }
            }
            return r;
        }
        private static List<int> PwrGateList(NodeInfo* ns)
        {
            var r = new List<int>();
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload; int s = (ns->C1c2Count << 1) + ns->GndCount; int e = s + ns->PwrCount;
                for (int k = s; k < e; k++) r.Add(pay[k]);
            }
            else if (ns->TlistC1pwr != 0)
            {
                ushort* p = TransistorList + ns->TlistC1pwr; while (*p != 0) { r.Add(*p); p++; }
            }
            return r;
        }
        private static List<(int gate, int other)> PassList(NodeInfo* ns)
        {
            var r = new List<(int, int)>();
            if (ns->Inline != 0)
            {
                ushort* pay = ns->InlinePayload; int n2 = ns->C1c2Count << 1;
                for (int k = 0; k < n2; k += 2) r.Add((pay[k], pay[k + 1]));
            }
            else if (ns->TlistC1c2s != 0)
            {
                ushort* p = TransistorList + ns->TlistC1c2s; while (*p != 0) { r.Add((p[0], p[1])); p += 2; }
            }
            return r;
        }

        // Verilog escaped identifier for a node: \<name> with a trailing space. Always valid for any chars
        // (Visual6502 names carry / # ~ _). Unnamed nodes -> \n<id>.
        private static string V(int nn)
        {
            string name = GetNodeName(nn);
            if (nn == Npwr) return "1'b1"; if (nn == Ngnd) return "1'b0";
            return "\\" + name + " ";
        }

        // Top-level cluster name = prefix before the first '.' or '/'; flat names -> "core".
        private static string Cluster(int nn)
        {
            string s = GetNodeName(nn);
            int i = s.IndexOfAny(new[] { '.', '/' });
            return i > 0 ? s.Substring(0, i) : "core";
        }

        // filter: if non-null, only nodes whose NAME contains this substring (plus their 1-hop fan-in,
        // for edge context) are emitted — for cutting a small, renderable subgraph out of the 14.7K-node chip.
        public static void ExportGateLevel(string basePath, string? filter = null)
        {
            int n = NodeCount;
            var ctx = BuildGateCtx();
            string vPath = basePath + ".v";
            string dotPath = basePath + ".dot";

            var cls = new GateClass[n];
            for (int nn = 0; nn < n; nn++) if (Nodes[nn] != null) cls[nn] = ClassifyGate(nn, ctx);

            // build the keep-set when filtering: matched nodes + their 1-hop fan-in sources (so edges resolve).
            bool[]? keep = null;
            if (filter != null)
            {
                keep = new bool[n];
                var matched = new List<int>();
                for (int nn = 0; nn < n; nn++)
                    if (Nodes[nn] != null && nn != Npwr && nn != Ngnd && GetNodeName(nn).Contains(filter, StringComparison.Ordinal))
                    { keep[nn] = true; matched.Add(nn); }
                foreach (int nn in matched)
                {
                    NodeInfo* ns = NodeInfos + nn;
                    foreach (int s in GndGateList(ns)) if (s != Npwr && s != Ngnd) keep[s] = true;
                    foreach (int s in PwrGateList(ns)) if (s != Npwr && s != Ngnd) keep[s] = true;
                    foreach (var (g, o) in PassList(ns)) { if (g != Npwr && g != Ngnd) keep[g] = true; if (o != Npwr && o != Ngnd) keep[o] = true; }
                }
                Console.WriteLine($"# filter '{filter}': {matched.Count:N0} matched nodes (+ 1-hop fan-in)");
            }
            bool Keep(int nn) => keep == null || keep[nn];

            // ── Verilog ──────────────────────────────────────────────────────────────────────────────
            long exactGates = 0, annotated = 0, regs = 0, wires = 0;
            using (var w = new StreamWriter(vPath))
            {
                w.WriteLine("// AprVisual S2 gate-level export (structural, behavioral — NOT synthesizable IP).");
                w.WriteLine("// NOR/inverter outputs carry an EXACT boolean (~OR of pull-down gate inputs).");
                w.WriteLine("// SeriesGate / dynamic / bus nodes are typed wires/regs with fan-in listed; their");
                w.WriteLine("// closed-form boolean is NOT synthesized (series-parallel reduction is future work).");
                w.WriteLine("module nes_2a03_2c02_gatelevel;");
                w.WriteLine();

                // declarations
                for (int nn = 0; nn < n; nn++)
                {
                    if (Nodes[nn] == null || nn == Npwr || nn == Ngnd || !Keep(nn)) continue;
                    if (cls[nn] == GateClass.DynamicStorage) { w.WriteLine($"  reg  {V(nn)};  // {cls[nn]}"); regs++; }
                    else { w.WriteLine($"  wire {V(nn)};  // {cls[nn]}"); wires++; }
                }
                w.WriteLine();

                // logic
                for (int nn = 0; nn < n; nn++)
                {
                    if (Nodes[nn] == null || nn == Npwr || nn == Ngnd || !Keep(nn)) continue;
                    NodeInfo* ns = NodeInfos + nn;
                    switch (cls[nn])
                    {
                        case GateClass.Inverter:
                        case GateClass.NorGate:
                            {
                                var ins = GndGateList(ns);
                                bool clean = ns->PwrCount == 0 && ins.Count > 0;
                                if (clean)
                                {
                                    var sb = new StringBuilder();
                                    for (int i = 0; i < ins.Count; i++) { if (i > 0) sb.Append(" | "); sb.Append(V(ins[i])); }
                                    w.WriteLine($"  assign {V(nn)}= ~({sb}); // {cls[nn]}");
                                    exactGates++;
                                }
                                else
                                {
                                    w.WriteLine($"  // {cls[nn]} {V(nn)}: pull-down inputs={NameSet(ins)} pwr-gates={NameSet(PwrGateList(ns))} (not emitted as boolean)");
                                    annotated++;
                                }
                                break;
                            }
                        case GateClass.SeriesGate:
                            {
                                var gnd = GndGateList(ns); var pass = PassList(ns);
                                var passGates = new List<int>(); foreach (var (g, _) in pass) passGates.Add(g);
                                w.WriteLine($"  // SeriesGate {V(nn)}: direct-gnd inputs={NameSet(gnd)} series/pass gates={NameSet(passGates)} (AOI — closed-form not synthesized)");
                                annotated++;
                                break;
                            }
                        case GateClass.DynamicStorage:
                            {
                                var pass = PassList(ns);
                                var sb = new StringBuilder();
                                foreach (var (g, o) in pass) sb.Append($"on({V(g).Trim()}) load {V(o).Trim()}; ");
                                w.WriteLine($"  // latch {V(nn)}: {sb}// dynamic charge-held register/RAM bit");
                                annotated++;
                                break;
                            }
                        default:
                            // SupplyDrivenDyn / BusFabric / PassWaypoint / Driven / Special / Unknown
                            w.WriteLine($"  // {cls[nn]} {V(nn)}: (switch-level — kept as-is)");
                            break;
                    }
                }
                w.WriteLine("endmodule");
            }

            // ── Graphviz ─────────────────────────────────────────────────────────────────────────────
            long edges = 0;
            var colors = new Dictionary<GateClass, string>
            {
                [GateClass.Inverter] = "#bfe6bf", [GateClass.NorGate] = "#8fd18f", [GateClass.SeriesGate] = "#4fae4f",
                [GateClass.DynamicStorage] = "#ffd27f", [GateClass.SupplyDrivenDyn] = "#ffe9bf",
                [GateClass.BusFabric] = "#ff9f9f", [GateClass.PassWaypoint] = "#ffd0d0",
                [GateClass.Driven] = "#9fcfff", [GateClass.Special] = "#d9b3ff", [GateClass.Unknown] = "#dddddd",
            };
            using (var w = new StreamWriter(dotPath))
            {
                w.WriteLine("digraph nes_gatelevel {");
                w.WriteLine("  rankdir=LR; node [shape=box,style=filled,fontsize=8];");
                // cluster by name prefix
                var byCluster = new Dictionary<string, List<int>>();
                for (int nn = 0; nn < n; nn++)
                {
                    if (Nodes[nn] == null || nn == Npwr || nn == Ngnd || !Keep(nn)) continue;
                    string c = Cluster(nn);
                    if (!byCluster.TryGetValue(c, out var lst)) byCluster[c] = lst = new List<int>();
                    lst.Add(nn);
                }
                foreach (var (c, lst) in byCluster)
                {
                    w.WriteLine($"  subgraph \"cluster_{c}\" {{ label=\"{c}\"; color=gray;");
                    foreach (int nn in lst)
                    {
                        string col = colors.TryGetValue(cls[nn], out var cc) ? cc : "#ffffff";
                        w.WriteLine($"    n{nn} [label=\"{Escape(GetNodeName(nn))}\\n{cls[nn]}\",fillcolor=\"{col}\"];");
                    }
                    w.WriteLine("  }");
                }
                // edges: fan-in signal -> gate output (only for the logic classes, to keep the graph meaningful)
                for (int nn = 0; nn < n; nn++)
                {
                    if (Nodes[nn] == null || nn == Npwr || nn == Ngnd || !Keep(nn)) continue;
                    NodeInfo* ns = NodeInfos + nn;
                    GateClass g = cls[nn];
                    if (g == GateClass.Inverter || g == GateClass.NorGate || g == GateClass.SeriesGate || g == GateClass.SupplyDrivenDyn)
                    {
                        foreach (int src in GndGateList(ns)) { if (src != Npwr && src != Ngnd && Keep(src)) { w.WriteLine($"  n{src} -> n{nn};"); edges++; } }
                        foreach (int src in PwrGateList(ns)) { if (src != Npwr && src != Ngnd && Keep(src)) { w.WriteLine($"  n{src} -> n{nn} [style=dashed];"); edges++; } }
                    }
                    if (g == GateClass.SeriesGate || g == GateClass.DynamicStorage)
                        foreach (var (gate, other) in PassList(ns))
                        {
                            if (gate != Npwr && gate != Ngnd && Keep(gate)) { w.WriteLine($"  n{gate} -> n{nn} [color=orange,label=ctrl];"); edges++; }
                            if (other != Npwr && other != Ngnd && other != nn && Keep(other)) { w.WriteLine($"  n{other} -> n{nn} [color=gray,style=dotted];"); edges++; }
                        }
                }
                w.WriteLine("}");
            }

            // ── register/latch boundary summary (name-pattern grouping a0..a7 -> a[7:0]) ──────────────
            string regSummary = RegisterGrouping(cls);

            Console.WriteLine($"# gate-level export written:");
            Console.WriteLine($"#   {vPath}   ({exactGates:N0} exact NOR/INV assigns, {annotated:N0} annotated gates, {regs:N0} regs, {wires:N0} wires)");
            Console.WriteLine($"#   {dotPath}  ({edges:N0} signal edges; open with: dot -Tsvg {Path.GetFileName(dotPath)} -o nes.svg  — large, filter by cluster if slow)");
            Console.WriteLine(regSummary);
        }

        private static string NameSet(List<int> ids)
        {
            if (ids.Count == 0) return "{}";
            var sb = new StringBuilder("{");
            for (int i = 0; i < ids.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(GetNodeName(ids[i])); }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        // Group named nodes by base name (strip trailing digits): a0..a7 -> a[7:0]. Reports the multi-bit
        // registers/buses an emulator dev reasons about, with the dominant gate-class of each group.
        private static string RegisterGrouping(GateClass[] cls)
        {
            int n = NodeCount;
            var groups = new Dictionary<string, List<(int bit, int nn)>>();
            for (int nn = 0; nn < n; nn++)
            {
                if (Nodes[nn] == null || nn == Npwr || nn == Ngnd) continue;
                string name = GetNodeName(nn);
                if (name.Length == 0 || !char.IsDigit(name[^1])) continue;
                int e = name.Length; while (e > 0 && char.IsDigit(name[e - 1])) e--;
                if (e == 0) continue;                          // all-digit (unnamed id) — skip
                string baseName = name.Substring(0, e);
                if (!int.TryParse(name.AsSpan(e), out int bit)) continue;
                if (!groups.TryGetValue(baseName, out var lst)) groups[baseName] = lst = new List<(int, int)>();
                lst.Add((bit, nn));
            }
            // keep multi-bit groups, sort by width desc
            var multi = new List<(string b, List<(int bit, int nn)> bits)>();
            foreach (var (b, bits) in groups) if (bits.Count >= 2) multi.Add((b, bits));
            multi.Sort((x, y) => y.bits.Count.CompareTo(x.bits.Count));

            var sb = new StringBuilder();
            sb.Append("# ----- register / latch boundaries (name-pattern multi-bit groups) -----\n");
            sb.Append($"#  {multi.Count:N0} multi-bit groups detected. Top 40 by width:\n");
            int shown = 0;
            foreach (var (b, bits) in multi)
            {
                if (shown++ >= 40) break;
                bits.Sort((x, y) => x.bit.CompareTo(y.bit));
                int lo = bits[0].bit, hi = bits[^1].bit;
                // dominant class
                var cc = new Dictionary<GateClass, int>();
                foreach (var (_, nn) in bits) { cc.TryGetValue(cls[nn], out int c); cc[cls[nn]] = c + 1; }
                GateClass dom = GateClass.Unknown; int best = -1;
                foreach (var (k, v) in cc) if (v > best) { best = v; dom = k; }
                sb.Append($"#    {b}[{hi}:{lo}]  ({bits.Count} bits, {dom})\n");
            }
            sb.Append("# -----------------------------------------------------------------------");
            return sb.ToString();
        }
    }
}
