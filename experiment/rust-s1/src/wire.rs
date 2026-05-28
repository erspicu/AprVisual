// AprVisual.S1 Rust fork — WireCore minimal subset for bench-hc / shot.
//
// Distilled from experiment/rust-poc/src/wire.rs. Removes all post-S1 experimental
// branches; fast-path is hardcoded ON (the classification + recalc_node_fast path
// is mandatory). Recursive add_node_to_group is kept (per memory the C# iterative
// BFS was -1.3% in Rust; LLVM inlines the recursive form well).
//
// Kept: pure-logic-gnd fast-path, no-op skip on SetHigh/Low/Float, batch-settle
// in run_mem_handler, settle-stats diagnostic, video pclk1 pixel write.
//
// Removed: --prune-merge, --parallel, chip-aware walk, LUT-TTL dispatch.
// (Snapshots created with --lut-ttl will have lut_chips populated; we ignore them.)

use crate::snapshot::{NodeInfo, MemHandlerSpec, FLAG_GND, FLAG_PWR, FLAG_PULLUP, FLAG_SETHIGH,
                      FLAG_SETLOW, FLAG_FORCE_COMPUTE, FLAG_HAS_CALLBACK};

pub const SCREEN_W: usize = 256;
pub const SCREEN_H: usize = 240;

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
    pub transistor_list: Vec<u16>,
    pub flags_to_state: [u8; 256],

    // settle scratch (double-buffered FIFO)
    pub recalc_list: Vec<i32>,
    pub recalc_list_next: Vec<i32>,
    pub recalc_hash: Vec<u8>,
    pub recalc_hash_next: Vec<u8>,
    pub list_count: usize,
    pub list_next_count: usize,

    // group walk scratch
    pub group_buf: Vec<i32>,
    pub in_group: Vec<u8>,
    pub group_count: usize,
    pub group_flags: u8,
    pub max_state: u8,
    pub max_connections: i32,

    pub time: i64,

    // callback dispatch: target_node_id → handler index in `handlers`.
    pub handlers: Vec<MemHandlerSpec>,
    pub target_to_handler: Vec<i32>,
    pub memories: Vec<Vec<u8>>,
    pub handler_enqueued: Vec<u8>,
    pub pending_handlers: Vec<i32>,

    // video output (PPU pclk1 rising-edge → pixel write)
    pub pclk1_node: i32,
    pub prev_pclk1: u8,
    pub hpos_nodes: Vec<i32>,
    pub vpos_nodes: Vec<i32>,
    pub pal_ptr_nodes: Vec<i32>,
    pub pal_ram_nodes: Vec<Vec<i32>>,
    pub framebuffer: Vec<u32>,

    // fast-path: pre-classified pure-logic-gnd nodes that resolve in O(1)
    pub is_pure_logic: Vec<u8>,
    pub fast_path_count: usize,

    // settle-stats — per-ProcessQueue iter histogram
    pub enable_settle_stats: bool,
    pub settle_histogram: [u64; 12],
    pub settle_call_count: u64,
    pub settle_iter_total: u64,
    pub settle_max_iter: u32,
}

// 2026-05-25: 128 = ~2.8x safety margin over observed max (full_palette 45 iter, SMB 41 iter).
const MAX_SETTLE_PASSES: u32 = 128;

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

        // ── Fast-path classification (hardcoded ON in S1 fork). Eligible ⇔ has PullUp, no
        //    HasCallback/ForceCompute/Pwr/Gnd, and tlist_c1c2s == 0 (group provably {nn}).
        let mut is_pure_logic = vec![0u8; nc];
        let exclude = FLAG_HAS_CALLBACK | FLAG_FORCE_COMPUTE | FLAG_PWR | FLAG_GND;
        let mut fast_path_count = 0;
        for nn in 0..nc {
            if (nn as i32) == snap.npwr || (nn as i32) == snap.ngnd { continue; }
            let ni = &snap.node_infos[nn];
            if (ni.flags & FLAG_PULLUP) == 0 { continue; }
            if (ni.flags & exclude) != 0 { continue; }
            if ni.tlist_c1c2s != 0 { continue; }
            is_pure_logic[nn] = 1;
            fast_path_count += 1;
        }

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
            is_pure_logic,
            fast_path_count,
            enable_settle_stats: false,
            settle_histogram: [0; 12],
            settle_call_count: 0,
            settle_iter_total: 0,
            settle_max_iter: 0,
        }
    }

    pub fn enable_settle_stats(&mut self) { self.enable_settle_stats = true; }

    #[inline]
    fn record_settle(&mut self, iter: u32) {
        self.settle_call_count += 1;
        self.settle_iter_total += iter as u64;
        if iter > self.settle_max_iter { self.settle_max_iter = iter; }
        let b: usize = if iter <= 4 { (iter - 1) as usize }
                       else if iter <=   8 { 4 }
                       else if iter <=  16 { 5 }
                       else if iter <=  32 { 6 }
                       else if iter <=  64 { 7 }
                       else if iter <= 128 { 8 }
                       else if iter <= 256 { 9 }
                       else if iter <= 512 { 10 }
                       else                { 11 };
        self.settle_histogram[b] += 1;
    }

    pub fn settle_stats_report(&self) -> String {
        if self.settle_call_count == 0 { return "(settle-stats: 0 calls)".to_string(); }
        let avg = self.settle_iter_total as f64 / self.settle_call_count as f64;
        let target = (self.settle_call_count as f64 * 0.99).ceil() as u64;
        let labels = ["1", "2", "3", "4", "5-8", "9-16", "17-32", "33-64", "65-128", "129-256", "257-512", "513+"];
        let mut cum: u64 = 0;
        let mut p99 = labels.len() - 1;
        for i in 0..labels.len() {
            cum += self.settle_histogram[i];
            if cum >= target { p99 = i; break; }
        }
        let mut s = format!("settle-stats: {} ProcessQueue calls, avg {:.2} iter/call, max {} iter, p99 in [{}]",
            self.settle_call_count, avg, self.settle_max_iter, labels[p99]);
        s.push_str("\n# settle-stats histogram:");
        for i in 0..labels.len() {
            if self.settle_histogram[i] == 0 { continue; }
            let pct = 100.0 * self.settle_histogram[i] as f64 / self.settle_call_count as f64;
            s.push_str(&format!("\n#   {:>8}: {:>9} ({:>5.1}%)", labels[i], self.settle_histogram[i], pct));
        }
        s
    }

    #[inline(always)]
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

    #[inline(always)]
    fn set_high_queued(&mut self, nn: i32) -> bool {
        let u = nn as usize;
        let f = self.node_infos[u].flags;
        let new_f = (f & !FLAG_SETLOW) | FLAG_SETHIGH;
        if new_f == f { return false; }
        self.node_infos[u].flags = new_f;
        self.enqueue(nn);
        true
    }

    #[inline(always)]
    fn set_low_queued(&mut self, nn: i32) -> bool {
        let u = nn as usize;
        let f = self.node_infos[u].flags;
        let new_f = (f & !FLAG_SETHIGH) | FLAG_SETLOW;
        if new_f == f { return false; }
        self.node_infos[u].flags = new_f;
        self.enqueue(nn);
        true
    }

    pub fn set_high(&mut self, nn: i32) {
        if self.set_high_queued(nn) { self.process_queue(); }
    }
    pub fn set_low(&mut self, nn: i32) {
        if self.set_low_queued(nn) { self.process_queue(); }
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
                let gate = self.transistor_list[p] as i32;
                if gate == 0 { break; }
                let other = self.transistor_list[p + 1] as i32;
                p += 2;
                if self.node_states[gate as usize] != 0 {
                    self.add_node_to_group(other);
                }
            }
        }
        if ni.tlist_c1gnd != 0 {
            let mut p = ni.tlist_c1gnd as usize;
            loop {
                let gate = self.transistor_list[p] as i32;
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_GND; break; }
            }
        }
        if ni.tlist_c1pwr != 0 {
            let mut p = ni.tlist_c1pwr as usize;
            loop {
                let gate = self.transistor_list[p] as i32;
                if gate == 0 { break; }
                p += 1;
                if self.node_states[gate as usize] != 0 { self.group_flags |= FLAG_PWR; break; }
            }
        }
    }

    #[inline(always)]
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

    #[inline(always)]
    fn set_node_state(&mut self, nn: i32, new_state: u8) {
        let u = nn as usize;
        if self.node_states[u] == new_state { return; }
        self.node_states[u] = new_state;
        let ni = self.node_infos[u];
        if ni.tlist_gates != 0 {
            let mut p = ni.tlist_gates as usize;
            loop {
                let c1 = self.transistor_list[p] as i32;
                if c1 == 0 { break; }
                let c2 = self.transistor_list[p + 1] as i32;
                p += 2;
                if c1 != self.npwr && c1 != self.ngnd {
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

    #[inline(always)]
    fn recalc_node(&mut self, nn: i32) {
        if nn == self.npwr || nn == self.ngnd { return; }
        // Fast-path: pure-logic-gnd nodes resolve in O(1). Always on in S1 fork.
        if self.is_pure_logic[nn as usize] != 0 {
            self.recalc_node_fast(nn);
            return;
        }
        let new_state = self.compute_node_group(nn);
        let gc = self.group_count;
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
    }

    pub fn process_queue(&mut self) {
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
        if self.enable_settle_stats { self.record_settle(iters); }
        if !self.pending_handlers.is_empty() {
            self.invoke_callbacks();
        }
    }

    fn invoke_callbacks(&mut self) {
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
        let cs = self.handlers[h].cs;
        if self.node_states[cs as usize] != 0 { return; }

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
            let mut val: u32 = 0;
            for i in 0..dout_len {
                let nn = self.handlers[h].data_out[i];
                if self.node_states[nn as usize] != 0 { val |= 1 << i; }
            }
            self.memories[mem_idx][(address as usize) & mask] = val as u8;
        } else {
            // memory → drive data_out (batch enqueue then one settle)
            let v = self.memories[mem_idx][(address as usize) & mask];
            let mut changed = false;
            for i in 0..dout_len {
                let nn = self.handlers[h].data_out[i];
                if (v & (1 << i)) != 0 { changed |= self.set_high_queued(nn); }
                else                    { changed |= self.set_low_queued(nn); }
            }
            if changed { self.process_queue(); }
        }
    }

    pub fn step_cycle(&mut self, clock_node: i32) {
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
