// Snapshot loader — reads the binary blob produced by the C# SnapshotExporter
// (src/AprVisual/Test/SnapshotExporter.cs) and returns the in-memory state ready
// for Rust to drive bench-hc.

use std::fs::File;
use std::io::{BufReader, Read, Result as IoResult};

const MAGIC: &[u8; 8] = b"APRSNAP\0";
const VERSION: u32 = 4;   // v4: chip_id + LUT chip specs (for --lut-ttl exports)

pub const CHIP_CPU: u8 = 0;
pub const CHIP_PPU: u8 = 1;
pub const CHIP_OTHER: u8 = 2;

// LUT chip types — matches C# WireCore.LutChips.LutChipType
pub const LUT_INVERTER: u8 = 0;          // 74HC04   — 1 input, 1 output, Y = ~A
pub const LUT_DECODER_2_TO_4: u8 = 1;    // 74LS139  — 3 inputs (E, A0, A1), 4 outputs
pub const LUT_TRISTATE_BUFFER: u8 = 2;   // 74LS368  — N inputs (A), N outputs (Y), 1 OE (extra)

pub struct LutChipSpec {
    pub chip_type: u8,
    pub target_node: i32,
    pub oe_node: i32,           // -1 if not applicable
    pub inputs: Vec<i32>,
    pub outputs: Vec<i32>,
}

// NodeFlags bit values — MUST match C# WireCore.NodeFlags exactly (FlagsToState LUT is exported
// pre-indexed against these bit positions).
pub const FLAG_NONE: u8 = 0;
pub const FLAG_STATE: u8 = 1 << 0;
pub const FLAG_PULLUP: u8 = 1 << 1;
pub const FLAG_SETHIGH: u8 = 1 << 2;
pub const FLAG_SETLOW: u8 = 1 << 3;
pub const FLAG_PWR: u8 = 1 << 4;
pub const FLAG_GND: u8 = 1 << 5;
pub const FLAG_FORCE_COMPUTE: u8 = 1 << 6;
pub const FLAG_HAS_CALLBACK: u8 = 1 << 7;

#[repr(C)]
#[derive(Default, Clone, Copy, Debug)]
pub struct NodeInfo {
    pub flags: u8,
    pub _pad: [u8; 3],
    pub connections: i32,
    pub tlist_gates: i32,
    pub tlist_c1c2s: i32,
    pub tlist_c1gnd: i32,
    pub tlist_c1pwr: i32,
}

pub struct MemHandlerSpec {
    pub is_rom: bool,
    pub memory_index: usize,
    pub cs: i32,
    pub we: i32,         // -1 if none
    pub target: i32,     // fake callback target node
    pub addr: Vec<i32>,
    pub data_out: Vec<i32>,
}

pub struct Memory {
    pub name: String,
    pub data: Vec<u8>,
}

pub struct Snapshot {
    pub node_count: usize,
    pub tlist_len: usize,
    pub npwr: i32,
    pub ngnd: i32,
    pub clock_node: i32,
    pub reset_node: i32,
    pub ppu_vblank_node: i32,
    pub node_states: Vec<u8>,
    pub node_infos: Vec<NodeInfo>,
    pub transistor_list: Vec<i32>,
    pub flags_to_state: [u8; 256],
    pub memories: Vec<Memory>,
    pub handlers: Vec<MemHandlerSpec>,
    // v2: video output
    pub pclk1_node: i32,
    pub hpos_nodes: Vec<i32>,
    pub vpos_nodes: Vec<i32>,
    pub pal_ptr_nodes: Vec<i32>,
    pub pal_ram_nodes: Vec<Vec<i32>>,  // 32 entries, each up to 6 bit-nodes
    // v3: per-node chip_id
    pub chip_id: Vec<u8>,
    // v4: LUT chip specs (empty when --lut-ttl was not active at export time)
    pub lut_chips: Vec<LutChipSpec>,
}

struct R<'a> { rd: BufReader<&'a mut File> }
impl<'a> R<'a> {
    fn u8(&mut self) -> IoResult<u8> { let mut b = [0u8; 1]; self.rd.read_exact(&mut b)?; Ok(b[0]) }
    fn u32(&mut self) -> IoResult<u32> { let mut b = [0u8; 4]; self.rd.read_exact(&mut b)?; Ok(u32::from_le_bytes(b)) }
    fn i32(&mut self) -> IoResult<i32> { Ok(self.u32()? as i32) }
    fn bytes(&mut self, n: usize) -> IoResult<Vec<u8>> { let mut v = vec![0u8; n]; self.rd.read_exact(&mut v)?; Ok(v) }
    fn skip(&mut self, n: usize) -> IoResult<()> { let mut v = vec![0u8; n]; self.rd.read_exact(&mut v)?; Ok(()) }
}

pub fn load(path: &str) -> IoResult<Snapshot> {
    let mut f = File::open(path)?;
    let mut r = R { rd: BufReader::new(&mut f) };

    let mut magic = [0u8; 8];
    r.rd.read_exact(&mut magic)?;
    if &magic != MAGIC {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData,
            format!("bad magic: {:?} (expected APRSNAP\\0)", &magic)));
    }
    let ver = r.u32()?;
    if ver != VERSION {
        return Err(std::io::Error::new(std::io::ErrorKind::InvalidData,
            format!("unsupported snapshot version {ver} (expected {VERSION})")));
    }

    let node_count = r.i32()? as usize;
    let tlist_len = r.i32()? as usize;
    let npwr = r.i32()?;
    let ngnd = r.i32()?;
    let clock_node = r.i32()?;
    let reset_node = r.i32()?;
    let ppu_vblank_node = r.i32()?;

    let node_states = r.bytes(node_count)?;

    let mut node_infos = vec![NodeInfo::default(); node_count];
    for ni in node_infos.iter_mut() {
        let flags = r.i32()? as u32;
        ni.flags = (flags & 0xFF) as u8;
        ni.connections = r.i32()?;
        ni.tlist_gates = r.i32()?;
        ni.tlist_c1c2s = r.i32()?;
        ni.tlist_c1gnd = r.i32()?;
        ni.tlist_c1pwr = r.i32()?;
    }

    let mut transistor_list = vec![0i32; tlist_len];
    for v in transistor_list.iter_mut() { *v = r.i32()?; }

    let mut flags_to_state = [0u8; 256];
    for v in flags_to_state.iter_mut() { *v = r.u8()?; }

    // memories
    let num_mem = r.i32()? as usize;
    let mut memories = Vec::with_capacity(num_mem);
    for _ in 0..num_mem {
        let nlen = r.i32()? as usize;
        let nbytes = r.bytes(nlen)?;
        let name = String::from_utf8(nbytes)
            .map_err(|e| std::io::Error::new(std::io::ErrorKind::InvalidData, e))?;
        let dlen = r.i32()? as usize;
        let data = r.bytes(dlen)?;
        memories.push(Memory { name, data });
    }

    // handlers
    let num_h = r.i32()? as usize;
    let mut handlers = Vec::with_capacity(num_h);
    for _ in 0..num_h {
        let is_rom = r.u8()? != 0;
        let memory_index = r.i32()? as usize;
        let cs = r.i32()?;
        let we = r.i32()?;
        let target = r.i32()?;
        let alen = r.i32()? as usize;
        let mut addr = Vec::with_capacity(alen);
        for _ in 0..alen { addr.push(r.i32()?); }
        let dlen = r.i32()? as usize;
        let mut data_out = Vec::with_capacity(dlen);
        for _ in 0..dlen { data_out.push(r.i32()?); }
        handlers.push(MemHandlerSpec { is_rom, memory_index, cs, we, target, addr, data_out });
    }

    // v2: video output node ids
    let pclk1_node = r.i32()?;
    let hpos_len = r.i32()? as usize;
    let mut hpos_nodes = Vec::with_capacity(hpos_len);
    for _ in 0..hpos_len { hpos_nodes.push(r.i32()?); }
    let vpos_len = r.i32()? as usize;
    let mut vpos_nodes = Vec::with_capacity(vpos_len);
    for _ in 0..vpos_len { vpos_nodes.push(r.i32()?); }
    let palptr_len = r.i32()? as usize;
    let mut pal_ptr_nodes = Vec::with_capacity(palptr_len);
    for _ in 0..palptr_len { pal_ptr_nodes.push(r.i32()?); }
    let pal_count = r.i32()? as usize;
    let mut pal_ram_nodes = Vec::with_capacity(pal_count);
    for _ in 0..pal_count {
        let blen = r.i32()? as usize;
        let mut bits = Vec::with_capacity(blen);
        for _ in 0..blen { bits.push(r.i32()?); }
        pal_ram_nodes.push(bits);
    }

    // v3: per-node chip_id
    let chip_id = r.bytes(node_count)?;

    // v4: LUT chip specs
    let n_lut = r.i32()? as usize;
    let mut lut_chips = Vec::with_capacity(n_lut);
    for _ in 0..n_lut {
        let chip_type = r.u8()?;
        let target_node = r.i32()?;
        let oe_node = r.i32()?;
        let ni = r.i32()? as usize;
        let mut inputs = Vec::with_capacity(ni);
        for _ in 0..ni { inputs.push(r.i32()?); }
        let no = r.i32()? as usize;
        let mut outputs = Vec::with_capacity(no);
        for _ in 0..no { outputs.push(r.i32()?); }
        lut_chips.push(LutChipSpec { chip_type, target_node, oe_node, inputs, outputs });
    }

    Ok(Snapshot {
        node_count, tlist_len, npwr, ngnd, clock_node, reset_node, ppu_vblank_node,
        node_states, node_infos, transistor_list, flags_to_state, memories, handlers,
        pclk1_node, hpos_nodes, vpos_nodes, pal_ptr_nodes, pal_ram_nodes,
        chip_id,
        lut_chips,
    })
}
