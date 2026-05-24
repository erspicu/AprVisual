// WireCore Rust port — minimal subset to drive bench-hc (Recalc + Group + SetNodeState +
// ProcessQueue + callback dispatch for memory handlers). No fast-path / prune-merge / IR /
// codegen / video — those are not needed for the baseline-vs-baseline Rust↔C# comparison.
//
// Mirrors: src/AprVisual/Sim/WireCore.Recalc.cs + WireCore.Group.cs + WireCore.Handlers.cs
// (callback dispatch path only).

use crate::snapshot::{NodeInfo, MemHandlerSpec, FLAG_GND, FLAG_PWR, FLAG_PULLUP, FLAG_SETHIGH,
                      FLAG_SETLOW, FLAG_STATE, FLAG_FORCE_COMPUTE, FLAG_HAS_CALLBACK};

pub const SCREEN_W: usize = 256;
pub const SCREEN_H: usize = 240;

// 64-colour NES master palette (common "2C02 NTSC" RGB approximation). ARGB 0x00RRGGBB.
// Mirrors src/AprVisual/Sim/WireCore.Handlers.cs:NesPalette.
pub const NES_PALETTE: [u32; 64] = [
    0x666666, 0x002A88, 0x1412A7, 0x3B00A4, 0x5C007E, 0x6E0040, 0x6C0600, 0x561D00,
    0x333500, 0x0B4800, 0x005200, 0x004F08, 0x00404D, 0x000000, 0x000000, 0x000000,
    0xADADAD, 0x155FD9, 0x4240FF, 0x7527FE, 0xA01ACC, 0xB71E7B, 0xB53120, 0x994E00,
    0x6B6D00, 0x388700, 0x0C9300, 0x008F32, 0x007C8D, 0x000000, 0x000000, 0x000000,
    0xFFFEFF, 0x64B0FF, 0x9290FF, 0xC676FF, 0xF36AFF, 0xFE6ECC, 0xFE8170, 0xEA9E22,
    0xBCBE00, 0x88D800, 0x5CE430, 0x45E082, 0x48CDDE, 0x4F4F4F, 0x000000, 0x000000,
    0xFFFEFF, 0xC0DFFF, 0xD3D2FF, 0xE8C8FF, 0xFBC2FF, 0xFEC4EA, 0xFECCC5, 0xF7D8A5,
    0xE4E594, 0xCFEF96, 0xBDF4AB, 0xB3F3CC, 0xB5EBF2, 0xB8B8B8, 0x000000, 0x000000,
];

pub struct WireCore {
    pub node_count: usize,
    pub npwr: i32,
    pub ngnd: i32,

    pub node_states: Vec<u8>,
    pub node_infos: Vec<NodeInfo>,
    pub transistor_list: Vec<i32>,
    pub flags_to_state: [u8; 256],

    // settle scratch
    pub recalc_list: Vec<i32>,
    pub recalc_list_next: Vec<i32>,
    pub recalc_hash: Vec<u8>,
    pub recalc_hash_next: Vec<u8>,
    pub list_count: usize,
    pub list_next_count: usize,

    pub group_buf: Vec<i32>,
    pub in_group: Vec<u8>,
    pub group_count: usize,
    pub group_flags: u8,
    pub max_state: u8,
    pub max_connections: i32,

    pub time: i64,

    // callback dispatch: target_node_id → handler index in `handlers`.
    pub handlers: Vec<MemHandlerSpec>,
    pub target_to_handler: Vec<i32>,   // -1 if not a handler target
    pub memories: Vec<Vec<u8>>,        // index matches handler.memory_index
    pub handler_enqueued: Vec<u8>,     // per-handler "needs to fire" flag

    // post-settle queue of handlers to invoke
    pub pending_handlers: Vec<i32>,

    // video output (PPU pclk1 rising-edge → pixel write)
    pub pclk1_node: i32,
    pub prev_pclk1: u8,
    pub hpos_nodes: Vec<i32>,
    pub vpos_nodes: Vec<i32>,
    pub pal_ptr_nodes: Vec<i32>,
    pub pal_ram_nodes: Vec<Vec<i32>>,
    pub framebuffer: Vec<u32>,    // ARGB, SCREEN_W * SCREEN_H

    // math-algos 策略二 — pure-logic-gnd fast-path classifier (--fast-path)
    pub enable_fast_path: bool,
    pub is_pure_logic: Vec<u8>,
    pub fast_path_count: usize,

    // math-algos #1 — prune-merge with topology-group-ID fix (--prune-merge)
    pub enable_prune_merge: bool,
    pub node_group_ids: Vec<i64>,
    pub next_group_id: i64,

    // Per-chip parallel settle infrastructure (--parallel)
    pub chip_id: Vec<u8>,
    pub enable_parallel: bool,
    pub serial_queue: Vec<i32>,           // walks that crossed chip boundary in chip-aware walk
    pub walks_pure_cpu: u64,
    pub walks_pure_ppu: u64,
    pub walks_pure_other: u64,
    pub walks_crossed: u64,
    pub walks_total: u64,
    // chip-aware walk scratch
    pub walk_chip: u8,
    pub walk_crossed: bool,

    // Phase 2: per-thread scratch for actual std::thread::scope parallel
    pub cpu_scratch: crate::parallel::ChipScratch,
    pub ppu_scratch: crate::parallel::ChipScratch,
}

const MAX_SETTLE_PASSES: u32 = 1000;

impl WireCore {
    pub fn from_snapshot(snap: crate::snapshot::Snapshot) -> Self {
        let nc = snap.node_count;
        let mut target_to_handler = vec![-1i32; nc];
        for (i, h) in snap.handlers.iter().enumerate() {
            if h.target >= 0 && (h.target as usize) < nc {
                target_to_handler[h.target as usize] = i as i32;
            }
        }
        let memories: Vec<Vec<u8>> = snap.memories.into_iter().map(|m| m.data).collect();
        let h_count = snap.handlers.len();
        let prev_pclk1 = if snap.pclk1_node >= 0 { snap.node_states[snap.pclk1_node as usize] } else { 0 };

        WireCore {
            node_count: nc,
            npwr: snap.npwr,
            ngnd: snap.ngnd,
            node_states: snap.node_states,
            node_infos: snap.node_infos,
            transistor_list: snap.transistor_list,
            flags_to_state: snap.flags_to_state,
            recalc_list: vec![0i32; nc],
            recalc_list_next: vec![0i32; nc],
            recalc_hash: vec![0u8; nc],
            recalc_hash_next: vec![0u8; nc],
            list_count: 0,
            list_next_count: 0,
            group_buf: vec![0i32; nc],
            in_group: vec![0u8; nc],
            group_count: 0,
            group_flags: 0,
            max_state: 0,
            max_connections: 0,
            time: 0,
            handlers: snap.handlers,
            target_to_handler,
            memories,
            handler_enqueued: vec![0u8; h_count],
            pending_handlers: Vec::with_capacity(h_count),
            pclk1_node: snap.pclk1_node,
            prev_pclk1,
            hpos_nodes: snap.hpos_nodes,
            vpos_nodes: snap.vpos_nodes,
            pal_ptr_nodes: snap.pal_ptr_nodes,
            pal_ram_nodes: snap.pal_ram_nodes,
            framebuffer: vec![0u32; SCREEN_W * SCREEN_H],
            enable_fast_path: false,
            is_pure_logic: Vec::new(),
            fast_path_count: 0,
            enable_prune_merge: false,
            node_group_ids: Vec::new(),
            next_group_id: 0,
            chip_id: snap.chip_id,
            enable_parallel: false,
            serial_queue: Vec::with_capacity(nc),
            walks_pure_cpu: 0,
            walks_pure_ppu: 0,
            walks_pure_other: 0,
            walks_crossed: 0,
            walks_total: 0,
            walk_chip: 0,
            walk_crossed: false,
            cpu_scratch: crate::parallel::ChipScratch::new(nc),
            ppu_scratch: crate::parallel::ChipScratch::new(nc),
        }
    }

    pub fn enable_parallel(&mut self) {
        self.enable_parallel = true;
    }

    // ── math-algos 策略二: classify pure-logic-gnd nodes for the O(1) RecalcNodeFast path. ──
    //    Mirrors C# WireCore.FastPath.cs:ClassifyPureLogicNodes. Eligible ⇔ has PullUp,
    //    no HasCallback / ForceCompute / Pwr / Gnd, and TlistC1c2s == 0 (no normal-node channels,
    //    so the group is provably {nn}).
    pub fn enable_fast_path(&mut self) {
        self.enable_fast_path = true;
        self.is_pure_logic = vec![0u8; self.node_count];
        let exclude = FLAG_HAS_CALLBACK | FLAG_FORCE_COMPUTE | FLAG_PWR | FLAG_GND;
        let mut count = 0;
        for nn in 0..self.node_count {
            if (nn as i32) == self.npwr || (nn as i32) == self.ngnd { continue; }
            let ni = &self.node_infos[nn];
            if (ni.flags & FLAG_PULLUP) == 0 { continue; }
            if (ni.flags & exclude) != 0 { continue; }
            if ni.tlist_c1c2s != 0 { continue; }
            self.is_pure_logic[nn] = 1;
            count += 1;
        }
        self.fast_path_count = count;
    }

    // ── math-algos #1 (Gemini r3 fix): topology-group-ID bookkeeping for prune-merge. ──
    //    Mirrors C# WireCore.PruneMerge.cs:InitGroupIDs.
    pub fn enable_prune_merge(&mut self) {
        self.enable_prune_merge = true;
        self.node_group_ids = (0..self.node_count as i64).collect();
        self.next_group_id = self.node_count as i64;
    }

    fn recalc_node_fast(&mut self, nn: i32) {
        let u = nn as usize;
        let ni = self.node_infos[u];
        let mut f = ni.flags;
        if ni.tlist_c1gnd != 0 {
            let mut p = ni.tlist_c1gnd as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { f |= FLAG_GND; break; }
            }
        }
        if ni.tlist_c1pwr != 0 {
            let mut p = ni.tlist_c1pwr as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { f |= FLAG_PWR; break; }
            }
        }
        // topology-group-ID: fast-path nodes are singleton groups, so they get their own fresh gid
        if self.enable_prune_merge {
            let gid = self.next_group_id;
            self.next_group_id = gid + 1;
            self.node_group_ids[u] = gid;
        }
        self.set_node_state(nn, self.flags_to_state[f as usize]);
    }

    #[inline(always)]
    fn read_bits(&self, nodes: &[i32]) -> u32 {
        let mut v: u32 = 0;
        for (i, &nn) in nodes.iter().enumerate() {
            if self.node_states[nn as usize] != 0 { v |= 1 << i; }
        }
        v
    }

    fn video_pixel_write_if_rising_edge(&mut self) {
        if self.pclk1_node < 0 { return; }
        let now = self.node_states[self.pclk1_node as usize];
        let rose = self.prev_pclk1 == 0 && now != 0;
        self.prev_pclk1 = now;
        if !rose { return; }
        let x = self.read_bits(&self.hpos_nodes);
        let y = self.read_bits(&self.vpos_nodes);
        if (x as usize) >= SCREEN_W || (y as usize) >= SCREEN_H { return; }
        let slot = (self.read_bits(&self.pal_ptr_nodes) & 31) as usize;
        let pal_bits = &self.pal_ram_nodes[slot];
        if pal_bits.len() != 6 { return; }
        let colour6 = self.read_bits(pal_bits);
        let argb = NES_PALETTE[(colour6 & 0x3F) as usize];
        self.framebuffer[(y as usize) * SCREEN_W + (x as usize)] = argb;
    }

    #[inline(always)]
    pub fn enqueue(&mut self, nn: i32) {
        if nn == self.npwr || nn == self.ngnd { return; }
        let u = nn as usize;
        unsafe {
            if *self.recalc_hash_next.get_unchecked(u) == 0 {
                let i = self.list_next_count;
                *self.recalc_list_next.get_unchecked_mut(i) = nn;
                self.list_next_count = i + 1;
                *self.recalc_hash_next.get_unchecked_mut(u) = 1;
            }
        }
    }

    pub fn recalc_node_list(&mut self, nn: i32) {
        self.enqueue(nn);
        self.process_queue();
    }

    // SetHigh / SetLow / SetFloat — external pin drive
    pub fn set_high(&mut self, nn: i32) {
        let u = nn as usize;
        let f = self.node_infos[u].flags;
        let new_f = (f & !FLAG_SETLOW) | FLAG_SETHIGH;
        self.node_infos[u].flags = new_f;
        self.recalc_node_list(nn);
    }
    pub fn set_low(&mut self, nn: i32) {
        let u = nn as usize;
        let f = self.node_infos[u].flags;
        let new_f = (f & !FLAG_SETHIGH) | FLAG_SETLOW;
        self.node_infos[u].flags = new_f;
        self.recalc_node_list(nn);
    }

    fn add_node_to_group(&mut self, nn: i32) {
        let u = nn as usize;
        unsafe {
            if *self.in_group.get_unchecked(u) != 0 { return; }
            *self.in_group.get_unchecked_mut(u) = 1;
        }
        let ni = self.node_infos[u];
        let gc = self.group_count;
        self.group_buf[gc] = nn;
        self.group_count = gc + 1;
        if ni.connections > self.max_connections {
            self.max_state = self.node_states[u];
            self.max_connections = ni.connections;
        }
        self.recalc_hash[u] = 0;
        self.group_flags |= ni.flags;

        if ni.tlist_c1c2s != 0 {
            let mut p = ni.tlist_c1c2s as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                let other = self.transistor_list[p + 1];
                p += 2;
                if self.node_states[gate as usize] != 0 {
                    self.add_node_to_group(other);
                }
            }
        }
        if ni.tlist_c1gnd != 0 {
            let mut p = ni.tlist_c1gnd as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_GND; break; }
            }
        }
        if ni.tlist_c1pwr != 0 {
            let mut p = ni.tlist_c1pwr as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_PWR; break; }
            }
        }
    }

    fn compute_node_group(&mut self, nn: i32) -> u8 {
        for i in 0..self.group_count {
            self.in_group[self.group_buf[i] as usize] = 0;
        }
        self.group_flags = 0;
        self.group_count = 0;
        self.max_state = 0;
        self.max_connections = 0;
        self.add_node_to_group(nn);
        let mut f = self.group_flags;
        if (f & FLAG_FORCE_COMPUTE) != 0 && (f & FLAG_GND) != 0 && (f & FLAG_PWR) != 0 {
            f &= !(FLAG_GND | FLAG_PWR);
        }
        if f == 0 { self.max_state } else { self.flags_to_state[f as usize] }
    }

    fn set_node_state(&mut self, nn: i32, new_state: u8) {
        let u = nn as usize;
        if self.node_states[u] == new_state { return; }
        self.node_states[u] = new_state;
        let ni = self.node_infos[u];
        if ni.tlist_gates != 0 {
            let mut p = ni.tlist_gates as usize;
            // math-algos #1 (Gemini r3): when gate goes HIGH, skip enqueue if c1 and c2 already
            // share a GroupID (topologically merged — adding a parallel transistor is a true no-op).
            let prune = self.enable_prune_merge && new_state != 0;
            loop {
                let c1 = self.transistor_list[p];
                if c1 == 0 { break; }
                let c2 = self.transistor_list[p + 1];
                p += 2;
                if c1 != self.npwr && c1 != self.ngnd {
                    let should_enqueue = if prune {
                        self.node_group_ids[c1 as usize] != self.node_group_ids[c2 as usize]
                    } else { true };
                    if should_enqueue {
                        let cu = c1 as usize;
                        unsafe {
                            if *self.recalc_hash_next.get_unchecked(cu) == 0 {
                                let i = self.list_next_count;
                                *self.recalc_list_next.get_unchecked_mut(i) = c1;
                                self.list_next_count = i + 1;
                                *self.recalc_hash_next.get_unchecked_mut(cu) = 1;
                            }
                        }
                    }
                }
                if new_state == 0 && c2 != self.npwr && c2 != self.ngnd {
                    let cu = c2 as usize;
                    unsafe {
                        if *self.recalc_hash_next.get_unchecked(cu) == 0 {
                            let i = self.list_next_count;
                            *self.recalc_list_next.get_unchecked_mut(i) = c2;
                            self.list_next_count = i + 1;
                            *self.recalc_hash_next.get_unchecked_mut(cu) = 1;
                        }
                    }
                }
            }
        }
    }

    fn recalc_node(&mut self, nn: i32) {
        if nn == self.npwr || nn == self.ngnd { return; }
        // math-algos 策略二: pure-logic-gnd nodes resolve in O(1), bypassing the group DFS entirely.
        if self.enable_fast_path && self.is_pure_logic[nn as usize] != 0 {
            self.recalc_node_fast(nn);
            return;
        }
        let new_state = self.compute_node_group(nn);
        let gc = self.group_count;
        // math-algos #1: ratify the walked group with a fresh GroupID — every member shares it
        // for the topology-equivalence skip check in SetNodeState's prune-merge path.
        if self.enable_prune_merge {
            let gid = self.next_group_id;
            self.next_group_id = gid + 1;
            for i in 0..gc {
                let id = self.group_buf[i] as usize;
                self.node_group_ids[id] = gid;
            }
        }
        // Propagate values
        for i in 0..gc {
            let id = self.group_buf[i];
            self.set_node_state(id, new_state);
        }
        // Dispatch callbacks for any nodes in the group whose flag has HAS_CALLBACK
        if (self.group_flags & FLAG_HAS_CALLBACK) != 0 {
            for i in 0..gc {
                let id = self.group_buf[i];
                let u = id as usize;
                if (self.node_infos[u].flags & FLAG_HAS_CALLBACK) != 0 {
                    let hi = self.target_to_handler[u];
                    if hi >= 0 {
                        let h = hi as usize;
                        if self.handler_enqueued[h] == 0 {
                            self.handler_enqueued[h] = 1;
                            self.pending_handlers.push(hi);
                        }
                    }
                }
            }
        }
    }

    // ── chip-aware walk: aborts if a node of different chip_id is reached ──
    fn add_node_to_group_chip_aware(&mut self, nn: i32) {
        if self.walk_crossed { return; }
        let u = nn as usize;
        // chip boundary check — OTHER nodes are "wildcards" that join any walk
        let nc = self.chip_id[u];
        if nc != self.walk_chip && nc != crate::snapshot::CHIP_OTHER && self.walk_chip != crate::snapshot::CHIP_OTHER {
            self.walk_crossed = true;
            return;
        }
        unsafe {
            if *self.in_group.get_unchecked(u) != 0 { return; }
            *self.in_group.get_unchecked_mut(u) = 1;
        }
        let ni = self.node_infos[u];
        let gc = self.group_count;
        self.group_buf[gc] = nn;
        self.group_count = gc + 1;
        if ni.connections > self.max_connections {
            self.max_state = self.node_states[u];
            self.max_connections = ni.connections;
        }
        self.recalc_hash[u] = 0;
        self.group_flags |= ni.flags;
        if ni.tlist_c1c2s != 0 {
            let mut p = ni.tlist_c1c2s as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                let other = self.transistor_list[p + 1];
                p += 2;
                if self.node_states[gate as usize] != 0 {
                    self.add_node_to_group_chip_aware(other);
                    if self.walk_crossed { return; }
                }
            }
        }
        if ni.tlist_c1gnd != 0 {
            let mut p = ni.tlist_c1gnd as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_GND; break; }
            }
        }
        if ni.tlist_c1pwr != 0 {
            let mut p = ni.tlist_c1pwr as usize;
            loop {
                let gate = self.transistor_list[p];
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_PWR; break; }
            }
        }
    }

    // Recalc node with chip-awareness: returns false if walk crossed (caller should retry serial).
    fn recalc_node_chip_aware(&mut self, nn: i32) -> bool {
        if nn == self.npwr || nn == self.ngnd { return true; }
        // reset scratch + walk
        for i in 0..self.group_count { self.in_group[self.group_buf[i] as usize] = 0; }
        self.group_flags = 0;
        self.group_count = 0;
        self.max_state = 0;
        self.max_connections = 0;
        self.walk_chip = self.chip_id[nn as usize];
        self.walk_crossed = false;
        self.add_node_to_group_chip_aware(nn);
        if self.walk_crossed {
            // clean up partial walk's in_group
            for i in 0..self.group_count { self.in_group[self.group_buf[i] as usize] = 0; }
            self.group_count = 0;
            return false;   // caller should serialize this walk
        }
        // resolve normally
        let mut f = self.group_flags;
        if (f & FLAG_FORCE_COMPUTE) != 0 && (f & FLAG_GND) != 0 && (f & FLAG_PWR) != 0 {
            f &= !(FLAG_GND | FLAG_PWR);
        }
        let new_state = if f == 0 { self.max_state } else { self.flags_to_state[f as usize] };
        let gc = self.group_count;
        if self.enable_prune_merge {
            let gid = self.next_group_id;
            self.next_group_id = gid + 1;
            for i in 0..gc { self.node_group_ids[self.group_buf[i] as usize] = gid; }
        }
        for i in 0..gc {
            let id = self.group_buf[i];
            self.set_node_state(id, new_state);
        }
        if (self.group_flags & FLAG_HAS_CALLBACK) != 0 {
            for i in 0..gc {
                let id = self.group_buf[i];
                let u = id as usize;
                if (self.node_infos[u].flags & FLAG_HAS_CALLBACK) != 0 {
                    let hi = self.target_to_handler[u];
                    if hi >= 0 {
                        let h = hi as usize;
                        if self.handler_enqueued[h] == 0 {
                            self.handler_enqueued[h] = 1;
                            self.pending_handlers.push(hi);
                        }
                    }
                }
            }
        }
        true
    }

    // Phase 2: threaded settle via std::thread::scope. Replaces process_queue_parallel.
    pub fn process_queue_threaded(&mut self) {
        const MAX_PASSES: u32 = 1000;
        let mut iters = 0u32;
        while self.list_next_count != 0 {
            iters += 1;
            if iters > MAX_PASSES {
                for i in 0..self.list_next_count {
                    let nn = self.recalc_list_next[i] as usize;
                    self.recalc_hash_next[nn] = 0;
                }
                self.list_next_count = 0;
                break;
            }
            std::mem::swap(&mut self.recalc_list, &mut self.recalc_list_next);
            std::mem::swap(&mut self.recalc_hash, &mut self.recalc_hash_next);
            self.list_count = self.list_next_count;
            self.list_next_count = 0;

            // Partition current dirty list by chip; clear hash for live items.
            let mut cpu_bucket: Vec<i32> = Vec::with_capacity(self.list_count);
            let mut ppu_bucket: Vec<i32> = Vec::with_capacity(self.list_count);
            let mut other_bucket: Vec<i32> = Vec::with_capacity(64);
            for i in 0..self.list_count {
                let nn = self.recalc_list[i];
                let u = nn as usize;
                if self.recalc_hash[u] == 0 { continue; }
                self.recalc_hash[u] = 0;
                match self.chip_id[u] {
                    crate::snapshot::CHIP_CPU => cpu_bucket.push(nn),
                    crate::snapshot::CHIP_PPU => ppu_bucket.push(nn),
                    _ => other_bucket.push(nn),
                }
            }

            // Build shared view (raw ptrs — sound by chip-disjoint-write discipline).
            let view = crate::parallel::SharedView {
                node_states: self.node_states.as_mut_ptr(),
                node_infos: self.node_infos.as_ptr(),
                transistor_list: self.transistor_list.as_ptr(),
                flags_to_state: self.flags_to_state.as_ptr(),
                chip_id: self.chip_id.as_ptr(),
                target_to_handler: self.target_to_handler.as_ptr(),
                recalc_hash_seen: self.recalc_hash.as_mut_ptr(),
                handler_enqueued: self.handler_enqueued.as_mut_ptr(),
                handler_enqueued_len: self.handler_enqueued.len(),
                node_count: self.node_count,
                npwr: self.npwr,
                ngnd: self.ngnd,
            };

            // Two mutable disjoint borrows (split-borrow pattern); each thread owns one.
            let cpu_scratch = &mut self.cpu_scratch;
            let ppu_scratch = &mut self.ppu_scratch;

            // rayon::join — uses pre-allocated thread pool, much cheaper per-call than thread::spawn.
            let cpu_bucket_ref = &cpu_bucket;
            let ppu_bucket_ref = &ppu_bucket;
            rayon::join(
                || unsafe { crate::parallel::drain_bucket(view, cpu_scratch, cpu_bucket_ref, crate::snapshot::CHIP_CPU); },
                || unsafe { crate::parallel::drain_bucket(view, ppu_scratch, ppu_bucket_ref, crate::snapshot::CHIP_PPU); },
            );

            // Diagnostics (accumulated from per-thread).
            self.walks_pure_cpu += self.cpu_scratch.walks_pure;
            self.walks_pure_ppu += self.ppu_scratch.walks_pure;
            self.walks_crossed += self.cpu_scratch.walks_crossed + self.ppu_scratch.walks_crossed;
            self.cpu_scratch.walks_pure = 0;
            self.cpu_scratch.walks_crossed = 0;
            self.ppu_scratch.walks_pure = 0;
            self.ppu_scratch.walks_crossed = 0;
            self.walks_total += (cpu_bucket.len() + ppu_bucket.len() + other_bucket.len()) as u64;

            // Serial: OTHER bucket (small, no threading benefit).
            for &nn in &other_bucket {
                self.recalc_node(nn);
                self.walks_pure_other += 1;
            }
            // Serial: crossed walks from CPU thread + PPU thread (~0.3% rate).
            for &nn in &self.cpu_scratch.crossed_walks.clone() { self.recalc_node(nn); }
            for &nn in &self.ppu_scratch.crossed_walks.clone() { self.recalc_node(nn); }

            // Merge per-thread next-buckets into shared next-queue.
            for &nn in &self.cpu_scratch.next_cpu.clone() { self.enqueue(nn); }
            for &nn in &self.cpu_scratch.next_ppu.clone() { self.enqueue(nn); }
            for &nn in &self.cpu_scratch.next_other.clone() { self.enqueue(nn); }
            for &nn in &self.ppu_scratch.next_cpu.clone() { self.enqueue(nn); }
            for &nn in &self.ppu_scratch.next_ppu.clone() { self.enqueue(nn); }
            for &nn in &self.ppu_scratch.next_other.clone() { self.enqueue(nn); }

            // Merge pending handlers.
            for &hi in &self.cpu_scratch.pending_handlers.clone() {
                let h = hi as usize;
                if self.handler_enqueued[h] != 0 {
                    self.pending_handlers.push(hi);
                }
            }
            for &hi in &self.ppu_scratch.pending_handlers.clone() {
                let h = hi as usize;
                if self.handler_enqueued[h] != 0 {
                    self.pending_handlers.push(hi);
                }
            }
            self.cpu_scratch.pending_handlers.clear();
            self.ppu_scratch.pending_handlers.clear();

            self.list_count = 0;
        }
        if !self.pending_handlers.is_empty() {
            self.invoke_callbacks();
        }
    }

    pub fn process_queue_parallel(&mut self) {
        const MAX_PASSES: u32 = 1000;
        let mut iters = 0u32;
        while self.list_next_count != 0 {
            iters += 1;
            if iters > MAX_PASSES {
                for i in 0..self.list_next_count {
                    let nn = self.recalc_list_next[i] as usize;
                    self.recalc_hash_next[nn] = 0;
                }
                self.list_next_count = 0;
                break;
            }
            std::mem::swap(&mut self.recalc_list, &mut self.recalc_list_next);
            std::mem::swap(&mut self.recalc_hash, &mut self.recalc_hash_next);
            self.list_count = self.list_next_count;
            self.list_next_count = 0;

            // Phase A: chip-aware passes on CPU and PPU subsets (currently still serial — Phase B
            // adds threads). Cross-chip walks get queued to serial_queue. OTHER goes serial too.
            self.serial_queue.clear();
            for i in 0..self.list_count {
                let nn = self.recalc_list[i];
                let u = nn as usize;
                if self.recalc_hash[u] == 0 { continue; }
                let chip = self.chip_id[u];
                if chip == crate::snapshot::CHIP_OTHER {
                    // OTHER nodes (TTL/cart): too few to specialize, just serial via normal recalc
                    self.recalc_node(nn);
                    self.recalc_hash[u] = 0;
                    self.walks_total += 1;
                    self.walks_pure_other += 1;
                    continue;
                }
                if self.recalc_node_chip_aware(nn) {
                    self.recalc_hash[u] = 0;
                    self.walks_total += 1;
                    if chip == crate::snapshot::CHIP_CPU { self.walks_pure_cpu += 1; }
                    else { self.walks_pure_ppu += 1; }
                } else {
                    // crossed — defer to serial
                    self.serial_queue.push(nn);
                }
            }

            // Phase B: serial pass for crossed walks
            for i in 0..self.serial_queue.len() {
                let nn = self.serial_queue[i];
                let u = nn as usize;
                if self.recalc_hash[u] != 0 {
                    self.recalc_node(nn);
                    self.recalc_hash[u] = 0;
                    self.walks_total += 1;
                    self.walks_crossed += 1;
                }
            }
            self.list_count = 0;
        }
        if !self.pending_handlers.is_empty() {
            self.invoke_callbacks();
        }
    }

    pub fn process_queue(&mut self) {
        if self.enable_parallel { self.process_queue_threaded(); return; }
        self.process_queue_serial();
    }

    fn process_queue_serial(&mut self) {
        let mut iters = 0u32;
        while self.list_next_count != 0 {
            iters += 1;
            if iters > MAX_SETTLE_PASSES {
                for i in 0..self.list_next_count {
                    let nn = self.recalc_list_next[i] as usize;
                    self.recalc_hash_next[nn] = 0;
                }
                self.list_next_count = 0;
                break;
            }
            // swap next ↔ current
            std::mem::swap(&mut self.recalc_list, &mut self.recalc_list_next);
            std::mem::swap(&mut self.recalc_hash, &mut self.recalc_hash_next);
            self.list_count = self.list_next_count;
            self.list_next_count = 0;
            for i in 0..self.list_count {
                let nn = self.recalc_list[i];
                let u = nn as usize;
                if self.recalc_hash[u] != 0 {
                    self.recalc_node(nn);
                    self.recalc_hash[u] = 0;
                }
            }
            self.list_count = 0;
        }
        // invoke pending handlers (memory dispatch)
        if !self.pending_handlers.is_empty() {
            self.invoke_callbacks();
        }
    }

    fn invoke_callbacks(&mut self) {
        // Snapshot the pending list — a handler invocation may add more.
        // For re-entrant safety we use a swap-and-drain pattern.
        while !self.pending_handlers.is_empty() {
            let pending: Vec<i32> = std::mem::take(&mut self.pending_handlers);
            for hi in pending {
                let h = hi as usize;
                self.handler_enqueued[h] = 0;
                self.run_mem_handler(h);
            }
        }
    }

    fn run_mem_handler(&mut self, h: usize) {
        // Mirrors AttachRamLikeHandler's lambda body.
        let cs = self.handlers[h].cs;
        if self.node_states[cs as usize] != 0 { return; }   // active-low

        // read address (up to 16 bits)
        let addr_len = self.handlers[h].addr.len();
        let mut address: u32 = 0;
        for i in 0..addr_len {
            let nn = self.handlers[h].addr[i];
            if self.node_states[nn as usize] != 0 { address |= 1 << i; }
        }
        let we = self.handlers[h].we;
        let is_rom = self.handlers[h].is_rom;
        let writing = !is_rom && we >= 0 && self.node_states[we as usize] == 0;

        let mem_idx = self.handlers[h].memory_index;
        let mask = self.memories[mem_idx].len() - 1;
        let dout_len = self.handlers[h].data_out.len();

        if writing {
            // read data_out bits → memory
            let mut val: u32 = 0;
            for i in 0..dout_len {
                let nn = self.handlers[h].data_out[i];
                if self.node_states[nn as usize] != 0 { val |= 1 << i; }
            }
            self.memories[mem_idx][(address as usize) & mask] = val as u8;
        } else {
            // memory → drive data_out
            let v = self.memories[mem_idx][(address as usize) & mask];
            for i in 0..dout_len {
                let nn = self.handlers[h].data_out[i];
                if (v & (1 << i)) != 0 { self.set_high(nn); } else { self.set_low(nn); }
            }
        }
    }

    // ── per-cycle step: toggle clock + drain settle + write pixel on pclk1 rising edge ──
    pub fn step_cycle(&mut self, clock_node: i32) {
        // Clock handler equivalent: toggle clk
        if self.node_states[clock_node as usize] != 0 {
            self.set_low(clock_node);
        } else {
            self.set_high(clock_node);
        }
        self.video_pixel_write_if_rising_edge();
        self.time += 1;
    }

    pub fn step(&mut self, n: i64, clock_node: i32) {
        for _ in 0..n { self.step_cycle(clock_node); }
    }

    // Run until the vblank node rises (one frame boundary), or until max_hc hc have elapsed.
    // Returns the actual hc count stepped.
    pub fn run_frame(&mut self, clock_node: i32, vblank_node: i32, max_hc: i64) -> i64 {
        if vblank_node < 0 { self.step(max_hc.min(714_736), clock_node); return max_hc.min(714_736); }
        let mut prev = self.node_states[vblank_node as usize];
        let start = self.time;
        let mut count = 0i64;
        while count < max_hc {
            self.step_cycle(clock_node);
            count += 1;
            let now = self.node_states[vblank_node as usize];
            if prev == 0 && now != 0 { break; }
            prev = now;
        }
        self.time - start
    }

    pub fn node_states_checksum(&self) -> u64 {
        let mut h: u64 = 14695981039346656037;
        for &b in &self.node_states { h ^= b as u64; h = h.wrapping_mul(1099511628211); }
        h
    }
}
