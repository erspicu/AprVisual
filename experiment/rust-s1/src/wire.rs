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

use crate::snapshot::{MemHandlerSpec, FLAG_GND, FLAG_PWR, FLAG_PULLUP, FLAG_SETHIGH,
                      FLAG_SETLOW, FLAG_FORCE_COMPUTE, FLAG_HAS_CALLBACK};

// R1: Hot NodeInfo (16 bytes) — only fields read every BFS visit / set_node_state walk.
// Cold fields (connections, tlist_gates) moved to separate arrays.
// 1 (flags) + 3 (pad) + 4*3 (tlists) = 16 bytes, exactly one quarter of an L1 cache line.
#[repr(C)]
#[derive(Default, Clone, Copy)]
pub struct NodeHot {
    pub flags: u8,
    _pad: [u8; 3],
    pub tlist_c1c2s: i32,
    pub tlist_c1gnd: i32,
    pub tlist_c1pwr: i32,
}

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

// enqueue-prune mask bits (see WireCore.prune_mask)
pub const PRUNE_TURN_ON_UNSAFE: u8 = 1;
pub const PRUNE_TURN_OFF_SKIP: u8 = 2;

pub struct WireCore {
    pub npwr: i32,
    pub ngnd: i32,

    pub node_states: Vec<u8>,
    pub node_hot: Vec<NodeHot>,
    pub node_connections: Vec<i32>,   // cold: only floating tie-break (compute_node_group)
    pub node_tlist_gates: Vec<i32>,   // cold: only fanout writeback (set_node_state)
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

    // enqueue-prune safety mask (parity with C# PruneMask, bit-packed): one byte per node.
    //   bit 0 (PRUNE_TURN_ON_UNSAFE) = UNSAFE for the same-state turn-ON prune (P-1): no-PullUp
    //       floating/hold-previous dynamic+storage cell, or in a ForceCompute channel-component;
    //       CLEARED again by P-3/P-4 for the cap<all-neighbours subset that can't win a tie-break.
    //   bit 1 (PRUNE_TURN_OFF_SKIP) = single-channel no-driver leaf that isolates→float-holds on
    //       turn-OFF (P-2): skip enqueuing it when its channel opens.
    pub prune_mask: Vec<u8>,
    pub prune_unsafe_count: usize,
    pub turn_off_skip_count: usize,
    pub p34_untaint_count: usize,
}

// 2026-05-25: 128 = ~2.8x safety margin over observed max (full_palette 45 iter, SMB 41 iter).
// debug-only: release omits the per-pass cap check + counter (NES workloads always converge) — +1.8% Rust
// (LLVM's loop optimizer dislikes the in-loop counter+break far more than the C# JIT, where it was noise).
#[cfg(debug_assertions)]
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
            if (ni.flags & exclude) != 0 { continue; }            // class 0 — callback / forceCompute / supply
            if ni.tlist_c1c2s != 0 {
                // R-1 class 2: dynamic-singleton candidate. PullUp NOT required — recalc_node_singleton
                // handles the floating (f==0 ⇒ hold previous) case, so no-pullup dynamic singletons are
                // covered too. That's where the bulk of them are (~10.8K vs ~3K if PullUp-gated).
                is_pure_logic[nn] = 2;
                continue;
            }
            // class 1 — static pure-logic. Rust keeps the PullUp gate: recalc_node_fast has no floating
            // branch, and adding one to that hot static path was -1.9% in Rust (be4cd38 / [[jit-vs-llvm]]).
            if (ni.flags & FLAG_PULLUP) == 0 { continue; }
            is_pure_logic[nn] = 1;
            fast_path_count += 1;
        }

        // ── same-state turn-on prune safety mask (parity with C# ClassifyPruneTaint) ──
        // union-find over c1c2 channels; taint a node iff it has no PullUp (can float → charge/cap
        // hold-previous tie-break: dynamic logic + storage cells) OR its channel-component holds a
        // ForceCompute node (Gnd+Pwr cancel → non-monotone). Pruning the untainted (PullUp-static) rest
        // is bit-exact. One-off at construction (not hot path); uses snap.* before they're moved below.
        let mut prune_mask = vec![0u8; nc];
        let mut prune_unsafe_count = 0usize;
        let mut turn_off_skip_count = 0usize;
        let mut p34_untaint_count = 0usize;
        {
            fn find(p: &mut [usize], mut x: usize) -> usize { while p[x] != x { p[x] = p[p[x]]; x = p[x]; } x }
            fn union(p: &mut [usize], a: usize, b: usize) { let ra = find(p, a); let rb = find(p, b); if ra != rb { p[ra] = rb; } }
            let mut parent: Vec<usize> = (0..nc).collect();
            for nn in 0..nc {
                if (nn as i32) == snap.npwr || (nn as i32) == snap.ngnd { continue; }
                let t = snap.node_infos[nn].tlist_c1c2s;
                if t != 0 {
                    let mut p = t as usize;
                    while snap.transistor_list[p] != 0 {
                        union(&mut parent, nn, snap.transistor_list[p + 1] as usize);
                        p += 2;
                    }
                }
            }
            let mut tainted_root = vec![false; nc];
            for nn in 0..nc {
                if (snap.node_infos[nn].flags & FLAG_FORCE_COMPUTE) != 0 {
                    let r = find(&mut parent, nn);
                    tainted_root[r] = true;
                }
            }
            // bit 0: P-1 turn-on-unsafe (no-PullUp dynamic/storage, or ForceCompute component)
            for nn in 0..nc {
                if (nn as i32) == snap.npwr || (nn as i32) == snap.ngnd { continue; }
                let dynamic = (snap.node_infos[nn].flags & FLAG_PULLUP) == 0;
                let fc = tainted_root[find(&mut parent, nn)];
                if dynamic || fc { prune_mask[nn] |= PRUNE_TURN_ON_UNSAFE; prune_unsafe_count += 1; }
            }

            // handler-driven data-bus pins (e.g. u1._d*): driven via SetHigh/SetLow → NOT float-hold; exclude
            // from both P-2 and P-3/4 (parity with C# ClassifyTurnOffSkip's `driven` set from _callbacks.DataOut).
            let mut driven = vec![false; nc];
            for h in &snap.handlers {
                for &d in &h.data_out { if d >= 0 && (d as usize) < nc { driven[d as usize] = true; } }
            }
            let excl = FLAG_PULLUP | FLAG_FORCE_COMPUTE | FLAG_HAS_CALLBACK | FLAG_PWR | FLAG_GND;
            for nn in 0..nc {
                if (nn as i32) == snap.npwr || (nn as i32) == snap.ngnd || driven[nn] { continue; }
                let ni = &snap.node_infos[nn];
                if (ni.flags & excl) != 0 { continue; }
                if ni.tlist_c1gnd != 0 || ni.tlist_c1pwr != 0 { continue; }   // has a supply driver ⇒ not a pure float node
                if ni.tlist_c1c2s == 0 { continue; }
                // walk c1c2 channels: count pairs + test cap < ALL neighbours
                let mut p = ni.tlist_c1c2s as usize;
                let my_cap = ni.connections;
                let mut npairs = 0usize;
                let mut cap_lt_all = true;
                while snap.transistor_list[p] != 0 {
                    let other = snap.transistor_list[p + 1] as usize;
                    if my_cap >= snap.node_infos[other].connections { cap_lt_all = false; }
                    npairs += 1;
                    p += 2;
                }
                // P-2: single-channel no-driver leaf → isolates→float-holds on turn-OFF → skip its turn-off enqueue
                if npairs == 1 { prune_mask[nn] |= PRUNE_TURN_OFF_SKIP; turn_off_skip_count += 1; }
                // P-3/P-4: cap < ALL neighbours ⇒ can never win a floating tie-break ⇒ same-state turn-ON prune
                // is bit-exact ⇒ un-taint bit 0 (P-1's endpoint same-state check blocks cross-state bridging).
                if cap_lt_all { prune_mask[nn] &= !PRUNE_TURN_ON_UNSAFE; p34_untaint_count += 1; }
            }
        }

        // R1: split snapshot NodeInfo into hot NodeHot[] + cold parallel arrays. Hot path
        // (add_node_to_group / recalc_node_fast / *_queued) only touches NodeHot;
        // node_connections is only read in compute_node_group's floating tie-break;
        // node_tlist_gates is only read in set_node_state's fanout writeback.
        let mut node_hot = vec![NodeHot::default(); nc];
        let mut node_connections = vec![0i32; nc];
        let mut node_tlist_gates = vec![0i32; nc];
        for (i, ni) in snap.node_infos.iter().enumerate() {
            node_hot[i].flags = ni.flags;
            node_hot[i].tlist_c1c2s = ni.tlist_c1c2s;
            node_hot[i].tlist_c1gnd = ni.tlist_c1gnd;
            node_hot[i].tlist_c1pwr = ni.tlist_c1pwr;
            node_connections[i] = ni.connections;
            node_tlist_gates[i] = ni.tlist_gates;
        }

        WireCore {
            npwr: snap.npwr,
            ngnd: snap.ngnd,
            node_states: snap.node_states,
            node_hot,
            node_connections,
            node_tlist_gates,
            transistor_list: { let mut t = snap.transistor_list; t.extend_from_slice(&[0u16; 4]); t },  // +4 pad: ulong dual-pair overread safety
            flags_to_state: snap.flags_to_state,
            recalc_list: vec![0i32; nc],
            recalc_list_next: vec![0i32; nc],
            recalc_hash: {
                let mut h = vec![0u8; nc];
                // Step 1 shield: VCC/GND hash permanently 1 — they always look "already enqueued",
                // so branchless enqueue's `is_new = hash ^ 1` evaluates to 0 for them (no advance,
                // no supply cmp needed in set_node_state's c2 path).
                h[snap.npwr as usize] = 1;
                h[snap.ngnd as usize] = 1;
                h
            },
            recalc_hash_next: {
                let mut h = vec![0u8; nc];
                h[snap.npwr as usize] = 1;
                h[snap.ngnd as usize] = 1;
                h
            },
            list_count: 0,
            list_next_count: 0,
            group_buf: vec![0i32; nc],
            in_group: vec![0u8; nc],
            group_count: 0,
            group_flags: 0,
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
            prune_mask,
            prune_unsafe_count,
            turn_off_skip_count,
            p34_untaint_count,
        }
    }

    #[inline(always)]
    fn recalc_node_fast(&mut self, nn: i32) {
        let u = nn as usize;
        // R3: hot path — snapshot guarantees node ids and tlist indices are in range.
        unsafe {
            let ni = *self.node_hot.get_unchecked(u);
            let mut f = ni.flags;
            if ni.tlist_c1gnd != 0 {
                let mut p = ni.tlist_c1gnd as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_GND; break; }
                }
            }
            if ni.tlist_c1pwr != 0 {
                let mut p = ni.tlist_c1pwr as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_PWR; break; }
                }
            }
            self.set_node_state(nn, *self.flags_to_state.get_unchecked(f as usize));
        }
    }

    // R-1: singleton resolver for class-2 (dynamic-singleton) nodes — identical to recalc_node_fast
    // but reproduces GetNodeValue's floating tie-break: f==0 (no pull-up, nothing conducting) ⇒ hold
    // previous state (for a 1-node group the max-capacitance member IS nn). This branch lives ONLY on
    // the dyn path, so the hot static recalc_node_fast keeps its no-branch form (the -1.9% gate).
    #[inline(always)]
    fn recalc_node_singleton(&mut self, nn: i32) {
        let u = nn as usize;
        unsafe {
            let ni = *self.node_hot.get_unchecked(u);
            let mut f = ni.flags;
            if ni.tlist_c1gnd != 0 {
                let mut p = ni.tlist_c1gnd as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_GND; break; }
                }
            }
            if ni.tlist_c1pwr != 0 {
                let mut p = ni.tlist_c1pwr as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_PWR; break; }
                }
            }
            let v = if f != 0 { *self.flags_to_state.get_unchecked(f as usize) } else { *self.node_states.get_unchecked(u) };
            self.set_node_state(nn, v);
        }
    }

    #[inline(always)]
    fn read_bits(&self, nodes: &[i32]) -> u32 {
        let mut v: u32 = 0;
        unsafe {
            for (i, &nn) in nodes.iter().enumerate() {
                if *self.node_states.get_unchecked(nn as usize) != 0 { v |= 1 << i; }
            }
        }
        v
    }

    fn video_pixel_write_if_rising_edge(&mut self) {
        if self.pclk1_node < 0 { return; }
        // pclk1_node validated at load; framebuffer fixed size; pal_ram_nodes sized 32.
        unsafe {
            let now = *self.node_states.get_unchecked(self.pclk1_node as usize);
            let rose = self.prev_pclk1 == 0 && now != 0;
            self.prev_pclk1 = now;
            if !rose { return; }
            let x = self.read_bits(&self.hpos_nodes);
            let y = self.read_bits(&self.vpos_nodes);
            if (x as usize) >= SCREEN_W || (y as usize) >= SCREEN_H { return; }
            let slot = (self.read_bits(&self.pal_ptr_nodes) & 31) as usize;
            let pal_bits = self.pal_ram_nodes.get_unchecked(slot);
            if pal_bits.len() != 6 { return; }
            let colour6 = self.read_bits(pal_bits);
            let argb = *NES_PALETTE.get_unchecked((colour6 & 0x3F) as usize);
            *self.framebuffer.get_unchecked_mut((y as usize) * SCREEN_W + (x as usize)) = argb;
        }
    }

    #[inline(always)]
    pub fn enqueue(&mut self, nn: i32) {
        // Step 1+2 branchless: VCC/GND have hash=1 perma-shield, so is_new=0 for them
        // (no advance, no supply cmp). For non-supply: is_new = hash^1 = 1 first time, 0 if dup.
        let u = nn as usize;
        unsafe {
            let is_new = *self.recalc_hash_next.get_unchecked(u) ^ 1;
            *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = nn;
            *self.recalc_hash_next.get_unchecked_mut(u) = 1;
            self.list_next_count += is_new as usize;
        }
    }

    #[inline(always)]
    fn set_high_queued(&mut self, nn: i32) -> bool {
        let u = nn as usize;
        unsafe {
            let f = self.node_hot.get_unchecked(u).flags;
            let new_f = (f & !FLAG_SETLOW) | FLAG_SETHIGH;
            if new_f == f { return false; }
            self.node_hot.get_unchecked_mut(u).flags = new_f;
        }
        self.enqueue(nn);
        true
    }

    #[inline(always)]
    fn set_low_queued(&mut self, nn: i32) -> bool {
        let u = nn as usize;
        unsafe {
            let f = self.node_hot.get_unchecked(u).flags;
            let new_f = (f & !FLAG_SETHIGH) | FLAG_SETLOW;
            if new_f == f { return false; }
            self.node_hot.get_unchecked_mut(u).flags = new_f;
        }
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
        // R3: snapshot guarantees node ids and tlist indices are in range.
        unsafe {
            if *self.in_group.get_unchecked(u) != 0 { return; }
            *self.in_group.get_unchecked_mut(u) = 1;
            let ni = *self.node_hot.get_unchecked(u);
            let gc = self.group_count;
            *self.group_buf.get_unchecked_mut(gc) = nn;
            self.group_count = gc + 1;
            *self.recalc_hash.get_unchecked_mut(u) = 0;
            self.group_flags |= ni.flags;

            if ni.tlist_c1c2s != 0 {
                // ulong dual-pair: two (gate,other) pairs per 64-bit load (transistor_list has +4 pad) — +2.35%
                // (ported from C# T2; bigger here since Rust has no inline-payload split → every walk uses this).
                // raw ptr (no borrow) so the recursive &mut self call below is fine; transistor_list is read-only here.
                let tl = self.transistor_list.as_ptr();
                let mut p = ni.tlist_c1c2s as usize;
                loop {
                    let quad = (tl.add(p) as *const u64).read_unaligned();
                    let g1 = quad as u16 as i32;
                    if g1 == 0 { break; }
                    if *self.node_states.get_unchecked(g1 as usize) != 0 {
                        self.add_node_to_group((quad >> 16) as u16 as i32);
                    }
                    let g2 = (quad >> 32) as u16 as i32;
                    if g2 == 0 { break; }
                    if *self.node_states.get_unchecked(g2 as usize) != 0 {
                        self.add_node_to_group((quad >> 48) as u16 as i32);
                    }
                    p += 4;
                }
            }
            if ni.tlist_c1gnd != 0 {
                let mut p = ni.tlist_c1gnd as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p) as i32;
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { self.group_flags |= FLAG_GND; break; }
                }
            }
            if ni.tlist_c1pwr != 0 {
                let mut p = ni.tlist_c1pwr as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p) as i32;
                    if gate == 0 { break; }
                    p += 1;
                    if *self.node_states.get_unchecked(gate as usize) != 0 { self.group_flags |= FLAG_PWR; break; }
                }
            }
        }
    }

    #[inline(always)]
    fn compute_node_group(&mut self, nn: i32) -> u8 {
        unsafe {
            for i in 0..self.group_count {
                let id = *self.group_buf.get_unchecked(i) as usize;
                *self.in_group.get_unchecked_mut(id) = 0;
            }
            self.group_flags = 0;
            self.group_count = 0;
            self.add_node_to_group(nn);
            // Sync #01: ForceCompute|Gnd|Pwr mask is pre-computed into the flags_to_state LUT.
            let f = self.group_flags;
            if f != 0 { return *self.flags_to_state.get_unchecked(f as usize); }

            // Sync #02: purely floating group — find max-connection node by linear scan.
            let mut max_conn = -1i32;
            let mut max_state = 0u8;
            for i in 0..self.group_count {
                let nn = *self.group_buf.get_unchecked(i) as usize;
                let conn = *self.node_connections.get_unchecked(nn);
                if conn > max_conn {
                    max_state = *self.node_states.get_unchecked(nn);
                    max_conn = conn;
                }
            }
            max_state
        }
    }

    #[inline(always)]
    fn set_node_state(&mut self, nn: i32, new_state: u8) {
        let u = nn as usize;
        // R3: hot path — snapshot guarantees node ids and tlist indices are in range.
        // Step 1+2 branchless: shield removes the c2 != npwr/ngnd check entirely; XOR-shielded
        // enqueue replaces the if-branch on hash. #G2 loop unswitch retained.
        unsafe {
            if *self.node_states.get_unchecked(u) == new_state { return; }
            *self.node_states.get_unchecked_mut(u) = new_state;
            let tlist_gates = *self.node_tlist_gates.get_unchecked(u);
            if tlist_gates != 0 {
                // ulong dual-pair load: two (c1,c2) pairs (4 u16) per 64-bit read — ported from C# (+0.6% Rust).
                // transistor_list has +4 pad zeros so the 8-byte read can't go OOB. x64 LE: low u16 = [p].
                let tl = self.transistor_list.as_ptr();
                let mut p = tlist_gates as usize;
                if new_state == 0 {
                    // [P-2 turn-off enqueue prune] gate each enqueue with `want = (mask & TURN_OFF_SKIP)==0`:
                    // a single-channel no-driver leaf isolates→float-holds when its channel opens, so re-eval
                    // is a no-op — skip it. `hash |= n` (was `=1`) so a skipped/already-queued node's hash is
                    // untouched; supply shield preserved (supply hash is permanently 1 ⇒ n=0).
                    loop {
                        let quad = (tl.add(p) as *const u64).read_unaligned();
                        let c1a = (quad as u16) as i32;
                        if c1a == 0 { break; }
                        let c2a = ((quad >> 16) as u16) as i32;
                        let cu = c1a as usize;
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_OFF_SKIP) == 0) as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1a;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let cu = c2a as usize;
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_OFF_SKIP) == 0) as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c2a;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c1b = ((quad >> 32) as u16) as i32;
                        if c1b == 0 { break; }
                        let c2b = ((quad >> 48) as u16) as i32;
                        let cu = c1b as usize;
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_OFF_SKIP) == 0) as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1b;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let cu = c2b as usize;
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_OFF_SKIP) == 0) as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c2b;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        p += 4;
                    }
                } else {
                    // gate going high: the channel CONDUCTS, so c1 and c2 merge; single-sided enqueue of c1.
                    // [same-state turn-on prune] skip the enqueue when c1==c2 state AND c1 is prune-safe
                    // (PullUp-static, non-FC). `want` folds that into the branchless XOR-shielded enqueue;
                    // `|= n` sets the hash only when we actually add (else a pruned node would look queued).
                    loop {
                        let quad = (tl.add(p) as *const u64).read_unaligned();
                        let c1a = (quad as u16) as i32;
                        if c1a == 0 { break; }
                        let c2a = ((quad >> 16) as u16) as i32;
                        let cu = c1a as usize;
                        let want = ((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_ON_UNSAFE) != 0)
                                 | (*self.node_states.get_unchecked(cu) != *self.node_states.get_unchecked(c2a as usize));
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (want as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1a;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c1b = ((quad >> 32) as u16) as i32;
                        if c1b == 0 { break; }
                        let c2b = ((quad >> 48) as u16) as i32;
                        let cu = c1b as usize;
                        let want = ((*self.prune_mask.get_unchecked(cu) & PRUNE_TURN_ON_UNSAFE) != 0)
                                 | (*self.node_states.get_unchecked(cu) != *self.node_states.get_unchecked(c2b as usize));
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (want as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1b;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        p += 4;
                    }
                }
            }
        }
    }

    #[inline(always)]
    fn recalc_node(&mut self, nn: i32) {
        if nn == self.npwr || nn == self.ngnd { return; }
        // Fast-path: pure-logic-gnd nodes resolve in O(1). Always on in S1 fork.
        unsafe {
            let cls = *self.is_pure_logic.get_unchecked(nn as usize);
            if cls == 1 { self.recalc_node_fast(nn); return; }
            if cls == 2 {
                // R-1: all c1c2s gates OFF ⇒ conducting group is {nn} ⇒ O(1) recalc_node_fast (bit-identical).
                let mut p = (*self.node_hot.get_unchecked(nn as usize)).tlist_c1c2s as usize;
                let mut grows = false;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { break; }
                    if *self.node_states.get_unchecked(gate as usize) != 0 { grows = true; break; }
                    p += 2;  // (gate, other) pairs
                }
                if !grows { self.recalc_node_singleton(nn); return; }
            }
        }
        let new_state = self.compute_node_group(nn);
        let gc = self.group_count;
        unsafe {
            for i in 0..gc {
                let id = *self.group_buf.get_unchecked(i);
                self.set_node_state(id, new_state);
            }
            if (self.group_flags & FLAG_HAS_CALLBACK) != 0 {
                for i in 0..gc {
                    let u = *self.group_buf.get_unchecked(i) as usize;
                    if (self.node_hot.get_unchecked(u).flags & FLAG_HAS_CALLBACK) != 0 {
                        let hi = *self.target_to_handler.get_unchecked(u);
                        if hi >= 0 {
                            let h = hi as usize;
                            if *self.handler_enqueued.get_unchecked(h) == 0 {
                                *self.handler_enqueued.get_unchecked_mut(h) = 1;
                                self.pending_handlers.push(hi);
                            }
                        }
                    }
                }
            }
        }
    }

    pub fn process_queue(&mut self) {
        #[cfg(debug_assertions)]
        let mut iters = 0u32;
        while self.list_next_count != 0 {
            #[cfg(debug_assertions)]
            {
                iters += 1;
                if iters > MAX_SETTLE_PASSES {
                    unsafe {
                        for i in 0..self.list_next_count {
                            let nn = *self.recalc_list_next.get_unchecked(i) as usize;
                            *self.recalc_hash_next.get_unchecked_mut(nn) = 0;
                        }
                    }
                    self.list_next_count = 0;
                    break;
                }
            }
            std::mem::swap(&mut self.recalc_list, &mut self.recalc_list_next);
            std::mem::swap(&mut self.recalc_hash, &mut self.recalc_hash_next);
            self.list_count = self.list_next_count;
            self.list_next_count = 0;
            unsafe {
                for i in 0..self.list_count {
                    let nn = *self.recalc_list.get_unchecked(i);
                    let u = nn as usize;
                    if *self.recalc_hash.get_unchecked(u) != 0 {
                        self.recalc_node(nn);
                        *self.recalc_hash.get_unchecked_mut(u) = 0;
                    }
                }
            }
            self.list_count = 0;
        }
        if !self.pending_handlers.is_empty() {
            self.invoke_callbacks();
        }
    }

    fn invoke_callbacks(&mut self) {
        while !self.pending_handlers.is_empty() {
            let pending: Vec<i32> = std::mem::take(&mut self.pending_handlers);
            for hi in pending {
                let h = hi as usize;
                unsafe { *self.handler_enqueued.get_unchecked_mut(h) = 0; }
                self.run_mem_handler(h);
            }
        }
    }

    fn run_mem_handler(&mut self, h: usize) {
        // Handler index validated by caller (target_to_handler array).
        unsafe {
            let handler = self.handlers.get_unchecked(h);
            let cs = handler.cs;
            if *self.node_states.get_unchecked(cs as usize) != 0 { return; }

            let addr_slice: &[i32] = &handler.addr;
            let mut address: u32 = 0;
            for i in 0..addr_slice.len() {
                let nn = *addr_slice.get_unchecked(i);
                if *self.node_states.get_unchecked(nn as usize) != 0 { address |= 1 << i; }
            }
            let we = handler.we;
            let is_rom = handler.is_rom;
            let writing = !is_rom && we >= 0 && *self.node_states.get_unchecked(we as usize) == 0;

            let mem_idx = handler.memory_index;
            let mem = self.memories.get_unchecked(mem_idx);
            let mask = mem.len() - 1;
            let dout_len = handler.data_out.len();
            let addr_masked = (address as usize) & mask;

            if writing {
                let dout_slice: &[i32] = &handler.data_out;
                let mut val: u32 = 0;
                for i in 0..dout_len {
                    let nn = *dout_slice.get_unchecked(i);
                    if *self.node_states.get_unchecked(nn as usize) != 0 { val |= 1 << i; }
                }
                *self.memories.get_unchecked_mut(mem_idx).get_unchecked_mut(addr_masked) = val as u8;
            } else {
                let v = *mem.get_unchecked(addr_masked);
                // need to release the &handler borrow before set_*_queued mutates self
                let dout_ptr = handler.data_out.as_ptr();
                let mut changed = false;
                for i in 0..dout_len {
                    let nn = *dout_ptr.add(i);
                    if (v & (1 << i)) != 0 { changed |= self.set_high_queued(nn); }
                    else                    { changed |= self.set_low_queued(nn); }
                }
                if changed { self.process_queue(); }
            }
        }
    }

    pub fn step_cycle(&mut self, clock_node: i32) {
        let state = unsafe { *self.node_states.get_unchecked(clock_node as usize) };
        if state != 0 {
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
        let vb = vblank_node as usize;
        let mut prev = unsafe { *self.node_states.get_unchecked(vb) };
        let start = self.time;
        let mut count = 0i64;
        while count < max_hc {
            self.step_cycle(clock_node);
            count += 1;
            let now = unsafe { *self.node_states.get_unchecked(vb) };
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
