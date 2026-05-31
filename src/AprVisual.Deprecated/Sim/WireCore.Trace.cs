using System;
using System.Collections.Generic;
using System.Text;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Trace logging + the blargg $6000 test-ROM result probe — port of ref/metalnes-main
        //    handler_log.h / logger.cpp + handler_nes_system::check_unit_test().
        //    See MD/note/04_驗證與測試策略.md and MD/struct/01 附錄 B (cpu_eval_trace.json).
        //    (ReadBits / WriteBits live in WireCore.Handlers.cs.)

        // ── trace columns (a "logic analyser": pick node/register expressions, dump one line per trace point) ──
        private static readonly List<(string Label, int[] Nodes)> _traceColumns = new();
        private static readonly List<string> _traceLog = new();
        public static IReadOnlyList<string> TraceLog => _traceLog;
        public static void ClearTrace() { _traceLog.Clear(); }

        /// <summary>Configure trace columns from a comma-separated expression list, e.g. "cpu.clk0,cpu.ab[15:0],cpu.db[7:0],cpu.rw".</summary>
        public static void SetTraceColumns(string exprCsv)
        {
            _traceColumns.Clear();
            foreach (var raw in exprCsv.Split(','))
            {
                string e = raw.Trim();
                if (e.Length == 0) { _traceColumns.Add(("", Array.Empty<int>())); continue; }   // blank column = separator
                var ids = new List<int>();
                ResolveNodes(e, ids, quiet: true);
                _traceColumns.Add((e, ids.ToArray()));
            }
            if (_traceColumns.Count > 0) TraceLevel = 1;
        }

        /// <summary>Append one trace line: the current value of each configured column.</summary>
        public static void CaptureTraceLine()
        {
            if (_traceColumns.Count == 0) return;
            var sb = new StringBuilder();
            sb.Append(Time.ToString().PadLeft(9)).Append("  ");
            foreach (var (label, nodes) in _traceColumns)
            {
                if (nodes.Length == 0) { sb.Append(" | "); continue; }
                int v = ReadBits(nodes);
                int hexDigits = (nodes.Length + 3) / 4;
                sb.Append(label).Append('=').Append(v.ToString("X").PadLeft(hexDigits, '0')).Append("  ");
            }
            _traceLog.Add(sb.ToString());
        }

        /// <summary>Read a value spread across an ordered list of nodes (bit i = nodes[i]). Returns -1 if the list is empty.</summary>
        public static int ReadReg(IReadOnlyList<int> nodes) => nodes.Count == 0 ? -1 : ReadBits(nodes);

        /// <summary>Snapshot the CPU's named-state nodes (a/x/y/p/s/pc, ab/db, opcode) — for golden-trace comparison.</summary>
        public static string DumpCpuState()
        {
            int a = ReadReg(R_CpuA), x = ReadReg(R_CpuX), y = ReadReg(R_CpuY);
            int p = ReadReg(R_CpuP), s = ReadReg(R_CpuS), ir = ReadReg(R_CpuIr);
            int pcl = ReadReg(R_CpuPcl), pch = ReadReg(R_CpuPch);
            int pc = (pcl >= 0 && pch >= 0) ? (pch << 8) | pcl : -1;
            int ab = ReadReg(R_CpuAb), db = ReadReg(R_CpuDb);
            bool sync = N_CpuSync != EmptyNode && NodeStates[N_CpuSync] != 0;
            string H2(int v) => v < 0 ? "--" : v.ToString("X2");
            string H4(int v) => v < 0 ? "----" : v.ToString("X4");
            return $"t={Time,8}  PC={H4(pc)}  A={H2(a)} X={H2(x)} Y={H2(y)} P={H2(p)} S={H2(s)}  IR={H2(ir)}  AB={H4(ab)} DB={H2(db)}{(sync ? "  (fetch)" : "")}";
        }

        // ── blargg test-ROM result detection ($6000 in cart.eram.ram): signature 0xDE 0xB0 0x61 at $6001-3,
        //    status at $6000 (<0x80 = done; 0x80 = running; 0x81 = needs reset), NUL-terminated text from $6004. ──
        public readonly struct UnitTestResult
        {
            public readonly bool Found, Complete;
            public readonly int Code;
            public readonly string Text;
            public UnitTestResult(bool found, bool complete, int code, string text)
            { Found = found; Complete = complete; Code = code; Text = text; }
        }

        public static UnitTestResult CheckUnitTest()
        {
            var eram = M_EramRam;
            if (eram == null || eram.Data.Length < 5) return default;
            byte[] d = eram.Data;
            if (d[1] != 0xDE || d[2] != 0xB0 || d[3] != 0x61) return default;   // signature not yet written

            int code = d[0];
            if (code is >= 0x80)
                return new UnitTestResult(found: true, complete: false, code, "");   // 0x80 running, 0x81 needs reset

            var sb = new StringBuilder();
            for (int i = 4; i < d.Length; i++)
            {
                char c = (char)d[i];
                if (c == '\0') break;
                if (c == '\r') continue;
                sb.Append(c);
            }
            return new UnitTestResult(found: true, complete: true, code, sb.ToString());
        }
    }
}
