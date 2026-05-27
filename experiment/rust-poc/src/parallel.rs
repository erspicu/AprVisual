// Per-chip parallel settle — Phase 2 of the parallel PoC.
//
// Splits the per-half-cycle dirty list into CPU/PPU/OTHER buckets, runs CPU and PPU
// walks in parallel threads via std::thread::scope, and serializes the OTHER bucket
// + any cross-chip-aborted walks at the end of each iteration.
//
// Safety: each thread writes only nodes of its own chip (CPU thread writes only
// CPU-tagged nodes via SetNodeState; PPU thread writes only PPU-tagged nodes).
// Cross-chip walks (which would write nodes of multiple chips) are detected by
// the chip-aware walk and aborted — they go to serial post-pass. Shared reads
// (node_infos, transistor_list, flags_to_state, chip_id) are immutable. Hence
// no data race on shared memory. The unsafe block below packages this into a
// Send-able raw-pointer struct for thread::scope.

use crate::snapshot::{NodeInfo, FLAG_GND, FLAG_PWR, FLAG_FORCE_COMPUTE, FLAG_HAS_CALLBACK,
                      CHIP_CPU, CHIP_PPU, CHIP_OTHER};

// Per-thread scratch — duplicated for the 2-thread parallel phase.
pub struct ChipScratch {
    pub group_buf: Vec<i32>,
    pub in_group: Vec<u8>,
    pub group_count: usize,
    pub group_flags: u8,
    pub max_state: u8,
    pub max_connections: i32,
    pub walk_chip: u8,
    pub walk_crossed: bool,
    // thread-local enqueue buckets (per next-chip), merged into shared after threads join
    pub next_cpu: Vec<i32>,
    pub next_ppu: Vec<i32>,
    pub next_other: Vec<i32>,
    // pending memory-handler dispatches (collected in thread, fired serial)
    pub pending_handlers: Vec<i32>,
    pub crossed_walks: Vec<i32>,   // walks aborted due to chip cross
    // diagnostics
    pub walks_pure: u64,
    pub walks_crossed: u64,
}

impl ChipScratch {
    pub fn new(node_count: usize) -> Self {
        ChipScratch {
            group_buf: vec![0; node_count],
            in_group: vec![0; node_count],
            group_count: 0,
            group_flags: 0,
            max_state: 0,
            max_connections: 0,
            walk_chip: 0,
            walk_crossed: false,
            next_cpu: Vec::with_capacity(node_count / 4),
            next_ppu: Vec::with_capacity(node_count / 2),
            next_other: Vec::with_capacity(64),
            pending_handlers: Vec::new(),
            crossed_walks: Vec::with_capacity(64),
            walks_pure: 0,
            walks_crossed: 0,
        }
    }

    pub fn reset_counters(&mut self) {
        self.next_cpu.clear();
        self.next_ppu.clear();
        self.next_other.clear();
        self.crossed_walks.clear();
        self.pending_handlers.clear();
    }
}

// Shared (read-only during parallel phase) — packaged for cross-thread access.
// Raw pointers are sound because writes are disjoint by chip discipline.
#[derive(Clone, Copy)]
pub struct SharedView {
    pub node_states: *mut u8,
    pub node_infos: *const NodeInfo,
    pub transistor_list: *const u16,
    pub flags_to_state: *const u8,
    pub chip_id: *const u8,
    pub target_to_handler: *const i32,
    pub recalc_hash_seen: *mut u8,
    pub handler_enqueued: *mut u8,
    pub handler_enqueued_len: usize,
    pub node_count: usize,
    pub npwr: i32,
    pub ngnd: i32,
}

unsafe impl Send for SharedView {}
unsafe impl Sync for SharedView {}

// add_node_to_group with chip-aware abort. Mirrors wire::add_node_to_group_chip_aware but
// uses SharedView (raw ptrs) + per-thread ChipScratch.
unsafe fn add_node_to_group_chip_aware(view: SharedView, scratch: &mut ChipScratch, nn: i32) {
    if scratch.walk_crossed { return; }
    let u = nn as usize;
    let nc = *view.chip_id.add(u);
    if nc != scratch.walk_chip && nc != CHIP_OTHER && scratch.walk_chip != CHIP_OTHER {
        scratch.walk_crossed = true;
        return;
    }
    if *scratch.in_group.get_unchecked(u) != 0 { return; }
    *scratch.in_group.get_unchecked_mut(u) = 1;
    let ni = *view.node_infos.add(u);
    let gc = scratch.group_count;
    scratch.group_buf[gc] = nn;
    scratch.group_count = gc + 1;
    if ni.connections > scratch.max_connections {
        scratch.max_state = *view.node_states.add(u);
        scratch.max_connections = ni.connections;
    }
    scratch.group_flags |= ni.flags;

    if ni.tlist_c1c2s != 0 {
        let mut p = ni.tlist_c1c2s as usize;
        loop {
            let gate = *view.transistor_list.add(p) as i32;
            if gate == 0 { break; }
            let other = *view.transistor_list.add(p + 1) as i32;
            p += 2;
            if *view.node_states.add(gate as usize) != 0 {
                add_node_to_group_chip_aware(view, scratch, other);
                if scratch.walk_crossed { return; }
            }
        }
    }
    if ni.tlist_c1gnd != 0 {
        let mut p = ni.tlist_c1gnd as usize;
        loop {
            let gate = *view.transistor_list.add(p) as i32;
            if gate == 0 { break; }
            p += 1;
            if *view.node_states.add(gate as usize) != 0 { scratch.group_flags |= FLAG_GND; break; }
        }
    }
    if ni.tlist_c1pwr != 0 {
        let mut p = ni.tlist_c1pwr as usize;
        loop {
            let gate = *view.transistor_list.add(p) as i32;
            if gate == 0 { break; }
            p += 1;
            if *view.node_states.add(gate as usize) != 0 { scratch.group_flags |= FLAG_PWR; break; }
        }
    }
}

// Set node state from a worker thread. Enqueues fanout transistors' c1/c2 into thread-local
// next-buckets by chip_id. Mirrors wire::set_node_state.
unsafe fn set_node_state(view: SharedView, scratch: &mut ChipScratch, nn: i32, new_state: u8) {
    let u = nn as usize;
    let cur = *view.node_states.add(u);
    if cur == new_state { return; }
    *view.node_states.add(u) = new_state;
    let ni = *view.node_infos.add(u);
    if ni.tlist_gates == 0 { return; }
    let mut p = ni.tlist_gates as usize;
    loop {
        let c1 = *view.transistor_list.add(p) as i32;
        if c1 == 0 { break; }
        let c2 = *view.transistor_list.add(p + 1) as i32;
        p += 2;
        if c1 != view.npwr && c1 != view.ngnd {
            push_to_chip_bucket(view, scratch, c1);
        }
        if new_state == 0 && c2 != view.npwr && c2 != view.ngnd {
            push_to_chip_bucket(view, scratch, c2);
        }
    }
}

unsafe fn push_to_chip_bucket(view: SharedView, scratch: &mut ChipScratch, nn: i32) {
    let chip = *view.chip_id.add(nn as usize);
    match chip {
        CHIP_CPU => scratch.next_cpu.push(nn),
        CHIP_PPU => scratch.next_ppu.push(nn),
        _ => scratch.next_other.push(nn),
    }
}

// Drain one chip's dirty bucket for one wave. Walks are chip-aware; cross-chip walks are
// recorded in scratch.crossed_walks for serial post-pass.
pub unsafe fn drain_bucket(view: SharedView, scratch: &mut ChipScratch, bucket: &[i32],
                            chip_walk: u8) {
    let target_to_handler = std::slice::from_raw_parts(view.target_to_handler, view.node_count);
    let handler_enqueued = std::slice::from_raw_parts_mut(view.handler_enqueued, view.handler_enqueued_len);
    scratch.reset_counters();
    for &nn in bucket {
        if nn == view.npwr || nn == view.ngnd { continue; }
        // Reset scratch walk state.
        for i in 0..scratch.group_count { scratch.in_group[scratch.group_buf[i] as usize] = 0; }
        scratch.group_flags = 0;
        scratch.group_count = 0;
        scratch.max_state = 0;
        scratch.max_connections = 0;
        scratch.walk_chip = chip_walk;
        scratch.walk_crossed = false;
        add_node_to_group_chip_aware(view, scratch, nn);
        if scratch.walk_crossed {
            for i in 0..scratch.group_count { scratch.in_group[scratch.group_buf[i] as usize] = 0; }
            scratch.group_count = 0;
            scratch.crossed_walks.push(nn);
            scratch.walks_crossed += 1;
            continue;
        }
        // Resolve and propagate.
        let mut f = scratch.group_flags;
        if (f & FLAG_FORCE_COMPUTE) != 0 && (f & FLAG_GND) != 0 && (f & FLAG_PWR) != 0 {
            f &= !(FLAG_GND | FLAG_PWR);
        }
        let new_state = if f == 0 { scratch.max_state } else { *view.flags_to_state.add(f as usize) };
        let gc = scratch.group_count;
        for i in 0..gc {
            let id = scratch.group_buf[i];
            set_node_state(view, scratch, id, new_state);
        }
        // Pending handler dispatch — collect by target_to_handler lookup; main thread fires.
        if (scratch.group_flags & FLAG_HAS_CALLBACK) != 0 {
            for i in 0..gc {
                let id = scratch.group_buf[i] as usize;
                let ni = *view.node_infos.add(id);
                if (ni.flags & FLAG_HAS_CALLBACK) != 0 {
                    let hi = target_to_handler[id];
                    if hi >= 0 {
                        let h = hi as usize;
                        // synchronized-by-discipline: handler_enqueued is shared, but each
                        // handler is associated with exactly one chip's target node, so two
                        // threads can't be touching the same handler in the same wave.
                        if handler_enqueued[h] == 0 {
                            handler_enqueued[h] = 1;
                            scratch.pending_handlers.push(hi);
                        }
                    }
                }
            }
        }
        scratch.walks_pure += 1;
    }
}
