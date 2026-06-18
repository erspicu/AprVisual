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

// enqueue-prune classification bits — used by compute_prune_mask (load-time). The hot path no
// longer reads a mask: the class-major renumber turns these classes into id RANGES (range_a/s/b).
pub const PRUNE_TURN_ON_UNSAFE: u8 = 1;
pub const PRUNE_TURN_OFF_SKIP: u8 = 2;

pub struct WireCore {
    pub npwr: i32,
    pub ngnd: i32,

    pub node_states: Vec<u8>,
    pub node_hot: Vec<NodeHot>,
    pub node_connections: Vec<i32>,   // cold: only floating tie-break (compute_node_group)
    pub node_tlist_gates: Vec<i32>,   // cold: set_node_state turn-ON writeback
    pub node_tlist_gates_off: Vec<i32>, // cold: set_node_state turn-OFF writeback (endpoints < range_s removed)
    pub transistor_list: Vec<u16>,
    pub transistor_list_off: Vec<u16>,  // single endpoints (not c1/c2 pairs); sub-range_s ids pre-stripped
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

    // enqueue-prune classification stats. The mask itself is NOT kept: since the class-major
    // renumber ([range-prune], parity with C# 51e046d) the hot path reads the id RANGES below;
    // the mask is recomputed on the renumbered snapshot only as verification ground truth.
    pub prune_unsafe_count: usize,
    pub turn_off_skip_count: usize,
    pub p34_untaint_count: usize,

    // [range-prune] class-major renumber boundaries. Ids sorted into contiguous prune-class blocks:
    //   [3,A) skip&unsafe   [A,S) skip&safe   [S,B) noskip&safe   [B,..) noskip&unsafe
    // ⇒ turn-off skip ⇔ c < S (supply ids 1,2 < 3 ≤ S ride the same test — the old hash-shield
    //   still covers them too); turn-on unsafe ⇔ c < A || c >= B (c1 is never supply).
    // Verified against the recomputed mask at construction (panic on mismatch — never mis-prune).
    pub range_a: i32,
    pub range_s: i32,
    pub range_b: i32,
    // old→new id permutation; node_states_checksum iterates ORIGINAL id order through it, so the
    // checksum stays directly comparable with the C# goldens across the renumbering.
    pub renumber_perm: Vec<u16>,
    // renumbered ids the runner needs (the snapshot's own fields hold pre-renumber ids).
    pub clock_node: i32,
    pub vblank_node: i32,
}

// ── prune classification (P-1 taint / P-2 skip / P-3-4 untaint / driven exclusion) ──────────────
// Extracted from the constructor so the renumber can run it twice: once on IDENTITY ids to build
// the class-major permutation, once on the REMAPPED snapshot as verification ground truth + stats.
// The classes are intrinsic per-node properties (flags / channel component / pair count / relative
// capacitance / driven set), so they are invariant under renumbering.
fn compute_prune_mask(snap: &crate::snapshot::Snapshot) -> (Vec<u8>, usize, usize, usize) {
    let nc = snap.node_count;
    let mut prune_mask = vec![0u8; nc];
    let mut unsafe_count = 0usize;
    let mut skip_count = 0usize;
    let mut p34_count = 0usize;

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
        if dynamic || fc { prune_mask[nn] |= PRUNE_TURN_ON_UNSAFE; unsafe_count += 1; }
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
        if npairs == 1 { prune_mask[nn] |= PRUNE_TURN_OFF_SKIP; skip_count += 1; }
        if cap_lt_all { prune_mask[nn] &= !PRUNE_TURN_ON_UNSAFE; p34_count += 1; }
    }
    (prune_mask, unsafe_count, skip_count, p34_count)
}

// block index per prune class — order matches C#: no-skip&unsafe is LAST.
fn block_of(bits: u8) -> u8 { match bits & 3 { 3 => 0, 2 => 1, 0 => 2, _ => 3 } }

// class-major + clk-BFS-locality permutation (parity with C# WireCore.Renumber.cs).
// Locality key = BFS from clk along signal-flow edges (node → endpoints of the transistors it
// gates — the edges set_node_state enqueues through) ≈ the settle cascade's first-touch order.
fn build_renumber(snap: &crate::snapshot::Snapshot, bits: &[u8]) -> (Vec<u16>, i32, i32, i32) {
    let nc = snap.node_count;
    assert!(snap.npwr == 1 && snap.ngnd == 2, "renumber assumes npwr=1/ngnd=2 (snapshot format)");
    let mut order = vec![u32::MAX; nc];
    if snap.clock_node > 2 && (snap.clock_node as usize) < nc {
        let mut q = std::collections::VecDeque::new();
        let mut seen = vec![false; nc];
        let clk = snap.clock_node as usize;
        seen[clk] = true;
        q.push_back(clk);
        let mut seq = 0u32;
        while let Some(u) = q.pop_front() {
            order[u] = seq; seq += 1;
            let tg = snap.node_infos[u].tlist_gates;
            if tg != 0 {
                let mut p = tg as usize;
                while snap.transistor_list[p] != 0 {
                    let c1 = snap.transistor_list[p] as usize;       // (c1,c2) pairs, 0-terminated
                    let c2 = snap.transistor_list[p + 1] as usize;
                    if c1 > 2 && c1 < nc && !seen[c1] { seen[c1] = true; q.push_back(c1); }
                    if c2 > 2 && c2 < nc && !seen[c2] { seen[c2] = true; q.push_back(c2); }
                    p += 2;
                }
            }
        }
    }
    let mut ids: Vec<usize> = (3..nc).collect();
    ids.sort_by(|&a, &b| {
        block_of(bits[a]).cmp(&block_of(bits[b]))
            .then(order[a].cmp(&order[b]))
            .then(a.cmp(&b))
    });
    let mut perm = vec![0u16; nc];
    perm[1] = 1; perm[2] = 2;   // perm[0] = 0 (reserved); supply fixed
    let (mut c0, mut c1, mut c2c) = (0i32, 0i32, 0i32);
    let mut next = 3u16;
    for &old in &ids {
        perm[old] = next; next += 1;
        match block_of(bits[old]) { 0 => c0 += 1, 1 => c1 += 1, 2 => c2c += 1, _ => {} }
    }
    (perm, 3 + c0, 3 + c0 + c1, 3 + c0 + c1 + c2c)
}

// remap every node id baked into the snapshot (states/infos slots, transistor-list entries — every
// nonzero u16 IS a node id regardless of which sublist it sits in — handler pins, video node lists).
fn apply_renumber(snap: &mut crate::snapshot::Snapshot, perm: &[u16]) {
    let nc = snap.node_count;
    let m = |x: i32| -> i32 { if x >= 0 && (x as usize) < nc { perm[x as usize] as i32 } else { x } };
    let mut ns = vec![0u8; nc];
    let mut ni = vec![crate::snapshot::NodeInfo::default(); nc];
    for i in 0..nc {
        ns[perm[i] as usize] = snap.node_states[i];
        ni[perm[i] as usize] = snap.node_infos[i];
    }
    snap.node_states = ns;
    snap.node_infos = ni;
    for v in snap.transistor_list.iter_mut() { if *v != 0 { *v = perm[*v as usize]; } }
    for h in snap.handlers.iter_mut() {
        h.cs = m(h.cs); h.we = m(h.we); h.target = m(h.target);
        for x in h.addr.iter_mut() { *x = m(*x); }
        for x in h.data_out.iter_mut() { *x = m(*x); }
    }
    snap.clock_node = m(snap.clock_node);
    snap.ppu_vblank_node = m(snap.ppu_vblank_node);
    snap.pclk1_node = m(snap.pclk1_node);
    for x in snap.hpos_nodes.iter_mut() { *x = m(*x); }
    for x in snap.vpos_nodes.iter_mut() { *x = m(*x); }
    for x in snap.pal_ptr_nodes.iter_mut() { *x = m(*x); }
    for bits in snap.pal_ram_nodes.iter_mut() {
        for x in bits.iter_mut() { *x = m(*x); }
    }
}

// 2026-05-25: 128 = ~2.8x safety margin over observed max (full_palette 45 iter, SMB 41 iter).
// debug-only: release omits the per-pass cap check + counter (NES workloads always converge) — +1.8% Rust
// (LLVM's loop optimizer dislikes the in-loop counter+break far more than the C# JIT, where it was noise).
#[cfg(debug_assertions)]
const MAX_SETTLE_PASSES: u32 = 128;

impl WireCore {
    pub fn from_snapshot(mut snap: crate::snapshot::Snapshot) -> Self {
        // ── [range-prune] class-major renumber (parity with C# commit 51e046d) ──
        // 1. classify prune bits on the IDENTITY ids; 2. build the class-major + clk-BFS permutation;
        // 3. remap the whole snapshot; 4. re-classify on the remapped ids as ground truth and verify
        // every node's bits equal the range-implied bits (classes are id-invariant ⇒ must hold).
        let (bits0, _, _, _) = compute_prune_mask(&snap);
        let (perm, range_a, range_s, range_b) = build_renumber(&snap, &bits0);
        apply_renumber(&mut snap, &perm);
        let (prune_mask, prune_unsafe_count, turn_off_skip_count, p34_untaint_count) = compute_prune_mask(&snap);
        for nn in 3..snap.node_count {
            let implied: u8 = if (nn as i32) < range_a { 3 } else if (nn as i32) < range_s { 2 }
                              else if (nn as i32) < range_b { 0 } else { 1 };
            assert_eq!(prune_mask[nn] & 3, implied, "range-prune verification failed at node {nn} — refusing to mis-prune");
        }
        drop(prune_mask);   // hot path reads only the ranges from here on
        let clock_node = snap.clock_node;     // post-renumber ids for the runner
        let vblank_node = snap.ppu_vblank_node;

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

        // (prune classification + the class-major renumber happened above — see compute_prune_mask /
        //  build_renumber / apply_renumber. From here everything operates on the REMAPPED snapshot.)

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

        // ── Turn-OFF endpoint list (falling-writeback split, ported from C#) ──────────────────────────
        // Single endpoints (not c1/c2 pairs), with ids < range_s removed (the static P-2/supply skip
        // class). The falling writeback walks this with NO range compare and a shorter list. Built from
        // the (remapped) snapshot transistor_list via node_tlist_gates. Bit-exact: the dropped endpoints
        // are exactly the ones the old `c >= range_s` mask skipped at runtime.
        let mut transistor_list_off: Vec<u16> = vec![0u16];   // index 0 reserved (0 == "empty")
        let mut node_tlist_gates_off = vec![0i32; nc];
        for nn in 0..nc {
            let tg = node_tlist_gates[nn];
            if tg == 0 { continue; }
            let start = transistor_list_off.len();
            let mut p = tg as usize;
            loop {
                let c1 = snap.transistor_list[p];
                if c1 == 0 { break; }
                let c2 = snap.transistor_list[p + 1];
                if (c1 as i32) >= range_s { transistor_list_off.push(c1); }
                if (c2 as i32) >= range_s { transistor_list_off.push(c2); }
                p += 2;
            }
            if transistor_list_off.len() > start {
                node_tlist_gates_off[nn] = start as i32;
                transistor_list_off.push(0);   // 0-terminator
            }
        }
        transistor_list_off.extend_from_slice(&[0u16; 4]);   // +4 pad: dual-quad overread safety

        WireCore {
            npwr: snap.npwr,
            ngnd: snap.ngnd,
            node_states: snap.node_states,
            node_hot,
            node_connections,
            node_tlist_gates,
            node_tlist_gates_off,
            transistor_list: { let mut t = snap.transistor_list; t.extend_from_slice(&[0u16; 4]); t },  // +4 pad: ulong dual-pair overread safety
            transistor_list_off,
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
            prune_unsafe_count,
            turn_off_skip_count,
            p34_untaint_count,
            range_a,
            range_s,
            range_b,
            renumber_perm: perm,
            clock_node,
            vblank_node,
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
            if new_state == 0 {
                // Falling writeback: walk a PRE-FILTERED single-endpoint list (ids < range_s removed at
                // build = the static P-2/supply skip class), so NO range compare and a shorter walk. 4
                // endpoints per 64-bit load; transistor_list_off has +4 pad. Branchless XOR-shielded
                // enqueue (supply is already excluded by the build filter, so no mask needed). Bit-exact.
                let tlist_off = *self.node_tlist_gates_off.get_unchecked(u);
                if tlist_off != 0 {
                    let tlo = self.transistor_list_off.as_ptr();
                    let mut p = tlist_off as usize;
                    loop {
                        let quad = (tlo.add(p) as *const u64).read_unaligned();
                        let c0 = (quad as u16) as i32;
                        if c0 == 0 { break; }
                        let cu = c0 as usize;
                        let n = *self.recalc_hash_next.get_unchecked(cu) ^ 1;
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c0;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c1 = ((quad >> 16) as u16) as i32;
                        if c1 == 0 { break; }
                        let cu = c1 as usize;
                        let n = *self.recalc_hash_next.get_unchecked(cu) ^ 1;
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c2 = ((quad >> 32) as u16) as i32;
                        if c2 == 0 { break; }
                        let cu = c2 as usize;
                        let n = *self.recalc_hash_next.get_unchecked(cu) ^ 1;
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c2;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c3 = ((quad >> 48) as u16) as i32;
                        if c3 == 0 { break; }
                        let cu = c3 as usize;
                        let n = *self.recalc_hash_next.get_unchecked(cu) ^ 1;
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c3;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        p += 4;
                    }
                }
            } else {
                let tlist_gates = *self.node_tlist_gates.get_unchecked(u);
                if tlist_gates != 0 {
                    // ulong dual-pair load: two (c1,c2) pairs (4 u16) per 64-bit read. transistor_list has
                    // +4 pad zeros so the 8-byte read can't go OOB. x64 LE: low u16 = [p].
                    let tl = self.transistor_list.as_ptr();
                    let mut p = tlist_gates as usize;
                    // gate going high: the channel CONDUCTS, so c1 and c2 merge; single-sided enqueue of c1.
                    // [same-state turn-on prune, range form] unsafe ⇔ c < range_a || c >= range_b (the
                    // two outer class blocks; c1 is never supply). Two register compares replace the
                    // prune_mask byte load. `want` folds into the branchless XOR-shielded enqueue;
                    // `|= n` sets the hash only when we actually add (else a pruned node would look queued).
                    let ra = self.range_a;
                    let rb = self.range_b;
                    loop {
                        let quad = (tl.add(p) as *const u64).read_unaligned();
                        let c1a = (quad as u16) as i32;
                        if c1a == 0 { break; }
                        let c2a = ((quad >> 16) as u16) as i32;
                        let cu = c1a as usize;
                        let want = (c1a < ra) | (c1a >= rb)
                                 | (*self.node_states.get_unchecked(cu) != *self.node_states.get_unchecked(c2a as usize));
                        let n = (*self.recalc_hash_next.get_unchecked(cu) ^ 1) & (want as u8);
                        *self.recalc_list_next.get_unchecked_mut(self.list_next_count) = c1a;
                        *self.recalc_hash_next.get_unchecked_mut(cu) |= n; self.list_next_count += n as usize;
                        let c1b = ((quad >> 32) as u16) as i32;
                        if c1b == 0 { break; }
                        let c2b = ((quad >> 48) as u16) as i32;
                        let cu = c1b as usize;
                        let want = (c1b < ra) | (c1b >= rb)
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
                let ni = *self.node_hot.get_unchecked(nn as usize);
                let mut p = ni.tlist_c1c2s as usize;
                loop {
                    let gate = *self.transistor_list.get_unchecked(p);
                    if gate == 0 { self.recalc_node_singleton(nn); return; }
                    if *self.node_states.get_unchecked(gate as usize) != 0 { break; }
                    p += 2;  // (gate, other) pairs
                }
                // [B1 pair path, 2026-06-12 — port of C# WireCore.Recalc.cs B1] first ON gate at p:
                // if the group is provably exactly {nn, o}, resolve the pair inline without the group
                // walk. Same bit-exactness obligations as C#: every bail happens BEFORE any mutation
                // (self-channel, second ON seed gate, o has HasCallback/ForceCompute, any ON channel of
                // o to a third node); on commit clear recalc_hash[o] (the walk's member clear), OR both
                // nodes' flags + gnd/pwr early-break scans, floating tie-break = strict larger-cap wins
                // with the seed winning ties (== compute_node_group over push order [nn, o]), and write
                // back nn THEN o (next-wave enqueue order = within-wave Gauss-Seidel semantics).
                'pair: {
                    let o = *self.transistor_list.get_unchecked(p + 1) as i32;
                    if o == nn { break 'pair; }
                    let mut p2 = p + 2;
                    loop {
                        let gate = *self.transistor_list.get_unchecked(p2);
                        if gate == 0 { break; }
                        if *self.node_states.get_unchecked(gate as usize) != 0 { break 'pair; }
                        p2 += 2;
                    }
                    let oi = *self.node_hot.get_unchecked(o as usize);
                    if (oi.flags & (FLAG_HAS_CALLBACK | FLAG_FORCE_COMPUTE)) != 0 { break 'pair; }
                    if oi.tlist_c1c2s != 0 {
                        let mut q = oi.tlist_c1c2s as usize;
                        loop {
                            let gate = *self.transistor_list.get_unchecked(q);
                            if gate == 0 { break; }
                            if *self.node_states.get_unchecked(gate as usize) != 0
                                && *self.transistor_list.get_unchecked(q + 1) as i32 != nn { break 'pair; }
                            q += 2;
                        }
                    }
                    // committed — group is exactly {nn, o}
                    *self.recalc_hash.get_unchecked_mut(o as usize) = 0;
                    let mut f = ni.flags | oi.flags;
                    if ni.tlist_c1gnd != 0 {
                        let mut g = ni.tlist_c1gnd as usize;
                        loop {
                            let gate = *self.transistor_list.get_unchecked(g);
                            if gate == 0 { break; }
                            g += 1;
                            if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_GND; break; }
                        }
                    }
                    if ni.tlist_c1pwr != 0 {
                        let mut g = ni.tlist_c1pwr as usize;
                        loop {
                            let gate = *self.transistor_list.get_unchecked(g);
                            if gate == 0 { break; }
                            g += 1;
                            if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_PWR; break; }
                        }
                    }
                    if oi.tlist_c1gnd != 0 {
                        let mut g = oi.tlist_c1gnd as usize;
                        loop {
                            let gate = *self.transistor_list.get_unchecked(g);
                            if gate == 0 { break; }
                            g += 1;
                            if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_GND; break; }
                        }
                    }
                    if oi.tlist_c1pwr != 0 {
                        let mut g = oi.tlist_c1pwr as usize;
                        loop {
                            let gate = *self.transistor_list.get_unchecked(g);
                            if gate == 0 { break; }
                            g += 1;
                            if *self.node_states.get_unchecked(gate as usize) != 0 { f |= FLAG_PWR; break; }
                        }
                    }
                    let v = if f != 0 {
                        *self.flags_to_state.get_unchecked(f as usize)
                    } else if *self.node_connections.get_unchecked(o as usize)
                            > *self.node_connections.get_unchecked(nn as usize) {
                        *self.node_states.get_unchecked(o as usize)
                    } else {
                        *self.node_states.get_unchecked(nn as usize)
                    };
                    self.set_node_state(nn, v);
                    self.set_node_state(o, v);
                    return;
                }
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
        // ORIGINAL id order via the renumber permutation — stays directly comparable with the
        // C# goldens (which hash in original order too) across the class-major renumbering.
        let mut h: u64 = 14695981039346656037;
        for old in 0..self.node_states.len() {
            let i = self.renumber_perm[old] as usize;
            h ^= self.node_states[i] as u64;
            h = h.wrapping_mul(1099511628211);
        }
        h
    }
}
