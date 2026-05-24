// WireCore Rust port — minimal subset to drive bench-hc (Recalc + Group + SetNodeState +
// ProcessQueue + callback dispatch for memory handlers). No fast-path / prune-merge / IR /
// codegen / video — those are not needed for the baseline-vs-baseline Rust↔C# comparison.
//
// Mirrors: src/AprVisual/Sim/WireCore.Recalc.cs + WireCore.Group.cs + WireCore.Handlers.cs
// (callback dispatch path only).

use crate::snapshot::{NodeInfo, MemHandlerSpec, FLAG_GND, FLAG_PWR, FLAG_PULLUP, FLAG_SETHIGH,
                      FLAG_SETLOW, FLAG_STATE, FLAG_FORCE_COMPUTE, FLAG_HAS_CALLBACK};

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
        }
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
            loop {
                let c1 = self.transistor_list[p];
                if c1 == 0 { break; }
                let c2 = self.transistor_list[p + 1];
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

    fn recalc_node(&mut self, nn: i32) {
        if nn == self.npwr || nn == self.ngnd { return; }
        let new_state = self.compute_node_group(nn);
        let gc = self.group_count;
        // First, propagate values
        for i in 0..gc {
            let id = self.group_buf[i];
            self.set_node_state(id, new_state);
        }
        // Then, dispatch callbacks for any nodes in the group whose flag has HAS_CALLBACK
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

    // ── per-cycle step: toggle clock + drain settle ──
    pub fn step_cycle(&mut self, clock_node: i32) {
        // Clock handler equivalent: toggle clk
        if self.node_states[clock_node as usize] != 0 {
            self.set_low(clock_node);
        } else {
            self.set_high(clock_node);
        }
        self.time += 1;
    }

    pub fn step(&mut self, n: i64, clock_node: i32) {
        for _ in 0..n { self.step_cycle(clock_node); }
    }

    pub fn node_states_checksum(&self) -> u64 {
        let mut h: u64 = 14695981039346656037;
        for &b in &self.node_states { h ^= b as u64; h = h.wrapping_mul(1099511628211); }
        h
    }
}
