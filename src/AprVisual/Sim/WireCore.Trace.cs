using System;
using System.Collections.Generic;

namespace AprVisual.Sim
{
    internal static unsafe partial class WireCore
    {
        // ── Trace logging — port of ref/metalnes-main handler_log.h / logger.cpp + wire_gui's column picker.
        //    See MD/note/04_驗證與測試策略.md §4 and MD/struct/01 附錄 B (cpu_eval_trace.json).
        //
        //    Two uses in S1:
        //      1. like a logic analyser: pick a set of node/register expressions as "columns",
        //         dump one line per trace point.
        //      2. golden-reference comparison: each frame (or each cycle), dump cpu.a/x/y/p/s/pc,
        //         cpu.ab/db — compare against MetalNES / chipsim.js / Perfect6502 (S1 exit gate).

        // Resolved trace columns: (label, node ids). A multi-node column is read as one integer.
        private static readonly List<(string Label, int[] Nodes)> _traceColumns = new();
        private static readonly List<string> _traceLog = new();   // S1: plain in-memory list; ring buffer later

        /// <summary>Configure trace columns from a comma-separated expr list, e.g. "cpu.clk0,cpu.ab[],cpu.db[],cpu.rw".</summary>
        public static void SetTraceColumns(string exprCsv)
        {
            throw new NotImplementedException("WireCore.SetTraceColumns — resolve each expr via ResolveNodes");
        }

        /// <summary>Read a multi-node "register" expression as an integer (bit i = nodes[i]).</summary>
        public static int ReadBits(ReadOnlySpan<int> nodes)
        {
            int v = 0;
            for (int i = 0; i < nodes.Length; i++)
                if (NodeStates[nodes[i]] != 0) v |= 1 << i;
            return v;
        }

        /// <summary>Append one trace line (the current values of all configured columns).</summary>
        public static void CaptureTraceLine()
        {
            throw new NotImplementedException("WireCore.CaptureTraceLine");
        }

        /// <summary>Snapshot the CPU's named state nodes (cpu.a/x/y/p/s/pc, ab/db) — for golden comparison.</summary>
        public static string DumpCpuState()
        {
            throw new NotImplementedException("WireCore.DumpCpuState — read cpu.a[7:0] etc. via ResolveNodes/ReadBits");
        }

        public static IReadOnlyList<string> TraceLog => _traceLog;

        public static void ClearTrace()
        {
            _traceLog.Clear();
        }

        // ── $6000 blargg test-ROM signature detection (port of handler_nes_system::check_unit_test) ──
        //    Lives here because it reads a memory ("cart.eram.ram") like a trace probe.
        public readonly struct UnitTestResult
        {
            public readonly bool Found, Complete;
            public readonly int Code;       // <0x80 = done; 0x80 = running; 0x81 = needs reset
            public readonly string Text;
            public UnitTestResult(bool found, bool complete, int code, string text)
            { Found = found; Complete = complete; Code = code; Text = text; }
        }

        public static UnitTestResult CheckUnitTest()
        {
            // TODO: port check_unit_test:
            //   var eram = ResolveMemory("cart.eram.ram"); if (eram is null) return default;
            //   if (eram.Data[1]==0xDE && eram.Data[2]==0xB0 && eram.Data[3]==0x61) {
            //       int code = eram.Data[0];
            //       if (code < 0x80) { read NUL-terminated string from eram.Data[4..]; return done; }
            //       else return running/needs-reset;
            //   }
            throw new NotImplementedException("WireCore.CheckUnitTest — port handler_nes_system::check_unit_test");
        }
    }
}
