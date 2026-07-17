using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace AprVisual.Sim
{
    // ── Optional benchmark-variance reduction (opt-in via TestRunner's --pin flag) ──
    //
    // Pins the single hot thread to one quiet physical core, raises priority, and disables
    // Win11 power throttling. DEFAULT OFF: this is also a public, downloadable leaderboard tool —
    // auto-pinning a stranger's run could land on a slow E-core or an oversubscribed core, and
    // taking over OS scheduling is the submitter's call, not ours. Whether --pin was used is
    // recorded in the bench JSON ("pinned") so leaderboard entries stay apples-to-apples.
    //
    // Windows-only; a graceful no-op on macOS/Linux (returns a "not applied" status string).
    //
    // Strategy (2026-06-09 Gemini consult + project judgement):
    //  - Pin the THREAD, not the process. .NET's GC / JIT-tiering / finalizer / threadpool
    //    background threads then stay free to run on OTHER cores instead of preempting the hot
    //    loop and trashing its L1/L2. We are memory-latency bound, so cache eviction is enemy #1;
    //    process-wide affinity would co-locate every background thread onto the one hot core.
    //  - Auto core pick (--pin, no arg): GetLogicalProcessorInformationEx → highest EfficiencyClass
    //    (= P-core on Intel hybrid; uniform on AMD) → among those physical cores take the
    //    HIGHEST-numbered one's FIRST logical processor. That single rule satisfies all of:
    //    P-core (not a slow E-core), first-of-pair (skip the SMT sibling sharing L1/L2),
    //    avoid core 0 (it absorbs most Windows DPCs/interrupts) and the user's "quiet high core"
    //    preference — only falls back to core 0 if it is the sole top-class core.
    //  - Manual (--pin N): force logical processor N (e.g. to match cpu_lock_3.6ghz.bat).
    //  - Priority High, NOT Realtime (Realtime outranks input/watchdog DPCs and can hard-lock).
    //  - Disable EcoQoS EXECUTION_SPEED throttling so Win11 can't silently drop us to efficiency.
    //
    // Caveat: only processor group 0 is considered (>64 logical CPUs would need GROUP_AFFINITY.Group);
    // fine for this audience. SetThreadAffinityMask is per-OS-thread — safe here because Run() executes
    // synchronously on the main thread (no await), and we BeginThreadAffinity() to forbid CLR remapping.
    internal static unsafe class PerfTuning
    {
        // ── kernel32 ──
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
                                                         ref PROCESS_POWER_THROTTLING_STATE info, int infoSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

        private const int RelationProcessorCore = 0;
        private const int ProcessPowerThrottling = 4;                       // PROCESS_INFORMATION_CLASS
        private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        // libSystem (macOS): request a QoS class for the calling thread. Apple Silicon has no hard
        // per-core affinity API, so the macOS-correct way to "use the best cores" is to raise QoS to
        // USER_INTERACTIVE, which biases the scheduler onto the performance (P) cores.
        [DllImport("libSystem.dylib")]
        private static extern int pthread_set_qos_class_self_np(int qosClass, int relativePriority);
        private const int QOS_CLASS_USER_INTERACTIVE = 0x21;

        // Apply pinning + priority + throttling-opt-out. coreOverride: -1 = auto-pick best P-core,
        // >=0 = force that logical processor. Returns a human-readable status for the bench log/console.
        public static string Apply(int coreOverride)
        {
            if (OperatingSystem.IsMacOS())
                return ApplyMacOs();
            if (!OperatingSystem.IsWindows())
                return "pin: requested but not applied (this OS has no thread-pinning hook here; OS scheduling)";

            var notes = new System.Text.StringBuilder();

            // Priority first — cheap, and High also nudges Win11's power scheduler away from EcoQoS.
            try { using var p = Process.GetCurrentProcess(); p.PriorityClass = ProcessPriorityClass.High; notes.Append("priority=High "); }
            catch (Exception ex) { notes.Append($"priority=FAILED({ex.GetType().Name}) "); }
            try { Thread.CurrentThread.Priority = ThreadPriority.Highest; notes.Append("threadPrio=Highest "); }
            catch { /* best effort */ }

            // Disable EXECUTION_SPEED throttling (Win11 EcoQoS) so we are never silently slowed.
            try
            {
                var st = new PROCESS_POWER_THROTTLING_STATE
                {
                    Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,   // control this knob...
                    StateMask = 0,                                            // ...and turn throttling OFF
                };
                if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref st, sizeof(PROCESS_POWER_THROTTLING_STATE)))
                    notes.Append("ecoQoS=off ");
            }
            catch { /* older Windows lacks the class; ignore */ }

            // Resolve the affinity mask + build a clear core-info string (shown on every --pin run).
            int total = Environment.ProcessorCount;
            nuint mask;
            int coreIdx;
            string mode;
            if (coreOverride >= 0)
            {
                if (coreOverride >= total)
                    return $"pin: core {coreOverride} out of range (0..{total - 1}); affinity NOT set | {notes.ToString().TrimEnd()}";
                mask = (nuint)1 << coreOverride;
                coreIdx = coreOverride;
                mode = "forced";
            }
            else
            {
                mask = AutoBestCoreMask(out coreIdx, out int topClassCount);
                if (mask == 0)
                    return $"pin: topology query failed; affinity NOT set (OS scheduling) | {notes.ToString().TrimEnd()}";
                mode = topClassCount > 1
                    ? $"auto-detected best core: 1 of {topClassCount} top-class physical cores, first-of-pair"
                    : "auto-detected best core";
            }

            // Forbid the CLR from migrating this managed thread to another OS thread, then pin.
            Thread.BeginThreadAffinity();
            nuint prev = SetThreadAffinityMask(GetCurrentThread(), mask);
            if (prev == 0)
                return $"pin: SetThreadAffinityMask(0x{mask:X}) FAILED (err {Marshal.GetLastWin32Error()}) | {notes.ToString().TrimEnd()}";

            return $"pinned to logical core {coreIdx} of {total} ({mode}; affinity mask 0x{mask:X}) | {notes.ToString().TrimEnd()}";
        }

        // macOS best-effort: there is NO hard per-core affinity on Apple Silicon, so --pin can't pin a
        // specific core. Instead request USER_INTERACTIVE QoS for this thread, which biases the scheduler
        // onto the performance (P) cores — the macOS-idiomatic "give me the fast cores". coreOverride is
        // ignored (the OS chooses which P-core). Bit-exact: this only changes scheduling.
        private static string ApplyMacOs()
        {
            try
            {
                int rc = pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
                return rc == 0
                    ? "pin: macOS has no hard core-pinning (Apple Silicon) — requested USER_INTERACTIVE QoS instead (biases scheduler to P-cores)"
                    : $"pin: macOS QoS request failed (rc={rc}); OS scheduling";
            }
            catch (Exception ex) { return $"pin: macOS QoS not applied ({ex.GetType().Name}); OS scheduling"; }
        }

        // Highest-EfficiencyClass physical core, its highest-numbered instance, first logical proc.
        // Also reports topClassCount = how many physical cores share that top EfficiencyClass (so the
        // status line can say "1 of N" — N == all physical cores on a uniform CPU, == P-core count on hybrid).
        private static nuint AutoBestCoreMask(out int bitIndex, out int topClassCount)
        {
            bitIndex = -1;
            topClassCount = 0;
            uint len = 0;
            GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);  // probe size
            if (len == 0) return 0;

            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return 0;

                byte maxEff = 0;
                nuint bestMask = 0;
                int bestBit = -1;
                int count = 0;

                byte* ptr = (byte*)buf;
                byte* end = ptr + len;
                while (ptr < end)
                {
                    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4) then the union.
                    int relationship = *(int*)ptr;
                    uint size = *(uint*)(ptr + 4);
                    if (relationship == RelationProcessorCore)
                    {
                        // PROCESSOR_RELATIONSHIP (union @ +8): Flags(+8) EfficiencyClass(+9)
                        //   Reserved[20] GroupCount(+30) GROUP_AFFINITY GroupMask[](+32)
                        // GROUP_AFFINITY: Mask(nuint @ +32) Group(ushort @ +40). Group-0 only.
                        byte eff = *(ptr + 9);
                        nuint coreMask = *(nuint*)(ptr + 32);
                        if (coreMask != 0)
                        {
                            nuint first = coreMask & (nuint)(-(nint)coreMask);   // lowest set bit = first logical proc
                            int idx = System.Numerics.BitOperations.TrailingZeroCount((ulong)first);
                            // Prefer higher EfficiencyClass; within the top class prefer the highest-numbered
                            // core (quiet, away from core 0 / its interrupt-heavy neighbours).
                            if (eff > maxEff) { maxEff = eff; bestMask = first; bestBit = idx; count = 1; }
                            else if (eff == maxEff) { count++; if (idx > bestBit) { bestMask = first; bestBit = idx; } }
                        }
                    }
                    ptr += size;
                }
                bitIndex = bestBit;
                topClassCount = count;
                return bestMask;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }
}
