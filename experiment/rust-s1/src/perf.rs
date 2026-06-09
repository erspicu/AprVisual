// perf.rs — optional benchmark-variance reducer, mirroring the C# Sim/PerfTuning.cs.
//
// Pins the hot THREAD (not the process) to one quiet physical core, raises priority, and
// disables Win11 EcoQoS throttling. Opt-in via `bench ... --pin [N]` (default OFF, same as C#:
// a public leaderboard tool must not auto-pin a stranger's run to a slow E-core). Windows-only
// via raw kernel32 FFI (no extra crate); a graceful no-op + status string elsewhere.
//
// Auto core pick (--pin, no arg): GetLogicalProcessorInformationEx -> highest EfficiencyClass
// (= P-core on Intel hybrid; uniform on AMD) -> highest-numbered such physical core -> its first
// logical proc (skip the SMT sibling that shares L1/L2, avoid core 0, honour "quiet high core").
// Manual (--pin N): force logical core N. Priority High (NOT Realtime). Bit-exact: pure scheduling.

#[cfg(windows)]
mod imp {
    use std::ffi::c_void;

    type Handle = *mut c_void;

    #[link(name = "kernel32")]
    extern "system" {
        fn GetCurrentThread() -> Handle;
        fn GetCurrentProcess() -> Handle;
        fn SetThreadAffinityMask(h: Handle, mask: usize) -> usize;
        fn GetLogicalProcessorInformationEx(rel: i32, buf: *mut u8, len: *mut u32) -> i32;
        fn SetPriorityClass(h: Handle, class: u32) -> i32;
        fn SetThreadPriority(h: Handle, prio: i32) -> i32;
        fn SetProcessInformation(h: Handle, class: i32, info: *mut c_void, size: u32) -> i32;
        fn GetLastError() -> u32;
    }

    const RELATION_PROCESSOR_CORE: i32 = 0;
    const HIGH_PRIORITY_CLASS: u32 = 0x0000_0080;
    const THREAD_PRIORITY_HIGHEST: i32 = 2;
    const PROCESS_POWER_THROTTLING: i32 = 4; // PROCESS_INFORMATION_CLASS
    const PPT_CURRENT_VERSION: u32 = 1;
    const PPT_EXECUTION_SPEED: u32 = 0x1;

    #[repr(C)]
    struct ProcessPowerThrottlingState {
        version: u32,
        control_mask: u32,
        state_mask: u32,
    }

    pub fn apply(core_override: i32) -> String {
        let mut notes = String::new();
        unsafe {
            if SetPriorityClass(GetCurrentProcess(), HIGH_PRIORITY_CLASS) != 0 {
                notes.push_str("priority=High ");
            }
            if SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST) != 0 {
                notes.push_str("threadPrio=Highest ");
            }
            // Disable EcoQoS EXECUTION_SPEED throttling (control this knob, set state OFF).
            let mut st = ProcessPowerThrottlingState {
                version: PPT_CURRENT_VERSION,
                control_mask: PPT_EXECUTION_SPEED,
                state_mask: 0,
            };
            if SetProcessInformation(
                GetCurrentProcess(),
                PROCESS_POWER_THROTTLING,
                &mut st as *mut _ as *mut c_void,
                std::mem::size_of::<ProcessPowerThrottlingState>() as u32,
            ) != 0
            {
                notes.push_str("ecoQoS=off ");
            }

            let cpus = std::thread::available_parallelism().map(|c| c.get()).unwrap_or(1);
            let (mask, core_idx, mode) = if core_override >= 0 {
                if core_override as usize >= cpus {
                    return format!(
                        "pin: core {} out of range (0..{}); affinity NOT set | {}",
                        core_override, cpus - 1, notes.trim_end()
                    );
                }
                (1usize << core_override, core_override as u32, String::from("forced"))
            } else {
                match auto_best_core_mask() {
                    Some((m, idx, top)) => {
                        let mode = if top > 1 {
                            format!("auto-detected best core: 1 of {top} top-class physical cores, first-of-pair")
                        } else {
                            String::from("auto-detected best core")
                        };
                        (m, idx, mode)
                    }
                    None => return format!("pin: topology query failed; affinity NOT set (OS scheduling) | {}", notes.trim_end()),
                }
            };

            let prev = SetThreadAffinityMask(GetCurrentThread(), mask);
            if prev == 0 {
                return format!(
                    "pin: SetThreadAffinityMask(0x{mask:X}) FAILED (err {}) | {}",
                    GetLastError(), notes.trim_end()
                );
            }
            format!("pinned to logical core {core_idx} of {cpus} ({mode}; affinity mask 0x{mask:X}) | {}", notes.trim_end())
        }
    }

    // Highest-EfficiencyClass physical core, its highest-numbered instance, first logical proc.
    // Returns (single-bit affinity mask, logical-proc index, count of physical cores sharing the top
    // EfficiencyClass). Processor group 0 only.
    unsafe fn auto_best_core_mask() -> Option<(usize, u32, u32)> {
        let mut len: u32 = 0;
        GetLogicalProcessorInformationEx(RELATION_PROCESSOR_CORE, std::ptr::null_mut(), &mut len);
        if len == 0 {
            return None;
        }
        let mut buf = vec![0u8; len as usize];
        if GetLogicalProcessorInformationEx(RELATION_PROCESSOR_CORE, buf.as_mut_ptr(), &mut len) == 0 {
            return None;
        }

        let mut max_eff: u8 = 0;
        let mut best_mask: usize = 0;
        let mut best_bit: i32 = -1;
        let mut count: u32 = 0;
        let base = buf.as_ptr();
        let end = len as usize;
        let mut p: usize = 0;
        while p < end {
            // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(i32 @0) Size(u32 @4) then union.
            let relationship = *(base.add(p) as *const i32);
            let size = *(base.add(p + 4) as *const u32) as usize;
            if size == 0 {
                break;
            }
            if relationship == RELATION_PROCESSOR_CORE {
                // PROCESSOR_RELATIONSHIP (union @ +8): Flags(+8) EfficiencyClass(+9) Reserved[20]
                //   GroupCount(+30) GROUP_AFFINITY GroupMask[](+32). GROUP_AFFINITY.Mask(usize @ +32).
                let eff = *base.add(p + 9);
                let core_mask = *(base.add(p + 32) as *const usize);
                if core_mask != 0 {
                    let first = core_mask & core_mask.wrapping_neg(); // lowest set bit = first logical proc
                    let idx = first.trailing_zeros() as i32;
                    // Prefer higher EfficiencyClass; within the top class prefer the highest-numbered
                    // core (quiet, away from core 0 / its interrupt-heavy neighbours).
                    if eff > max_eff {
                        max_eff = eff;
                        best_mask = first;
                        best_bit = idx;
                        count = 1;
                    } else if eff == max_eff {
                        count += 1;
                        if idx > best_bit {
                            best_mask = first;
                            best_bit = idx;
                        }
                    }
                }
            }
            p += size;
        }
        if best_mask == 0 {
            None
        } else {
            Some((best_mask, best_bit as u32, count))
        }
    }
}

// macOS / Apple Silicon has NO hard per-core affinity API, so --pin can't pin a specific core.
// The macOS-idiomatic "use the fast cores" is to raise the thread's QoS class to USER_INTERACTIVE,
// which biases the scheduler onto the performance (P) cores. pthread_set_qos_class_self_np lives in
// libSystem (always linked on macOS). core_override is ignored (the OS picks the P-core). Bit-exact.
#[cfg(target_os = "macos")]
mod imp {
    extern "C" {
        fn pthread_set_qos_class_self_np(qos_class: u32, relative_priority: i32) -> i32;
    }
    const QOS_CLASS_USER_INTERACTIVE: u32 = 0x21;

    pub fn apply(_core_override: i32) -> String {
        let rc = unsafe { pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0) };
        if rc == 0 {
            String::from("pin: macOS has no hard core-pinning (Apple Silicon) — requested USER_INTERACTIVE QoS instead (biases scheduler to P-cores)")
        } else {
            format!("pin: macOS QoS request failed (rc={rc}); OS scheduling")
        }
    }
}

#[cfg(not(any(windows, target_os = "macos")))]
mod imp {
    pub fn apply(_core_override: i32) -> String {
        String::from("pin: requested but not applied (this OS has no thread-pinning hook here; OS scheduling)")
    }
}

pub use imp::apply;
