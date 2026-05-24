// wire_realbench — Rust port of bench-hc / --screenshot on a snapshot exported by C#.
//
// Run:
//   cargo run --release -- bench <snapshot.aprsnap> <hc_count>
//   cargo run --release -- shot  <snapshot.aprsnap> <frames> <out.png>

mod snapshot;
mod wire;

use std::time::Instant;
use std::fs::File;
use std::io::BufWriter;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 { usage(); std::process::exit(2); }
    match args[1].as_str() {
        "bench" => bench(&args),
        "shot"  => shot(&args),
        _       => { usage(); std::process::exit(2); }
    }
}

fn usage() {
    eprintln!("usage: wire_realbench bench <snapshot.aprsnap> <hc_count>");
    eprintln!("       wire_realbench shot  <snapshot.aprsnap> <frames> <out.png>");
}

fn bench(args: &[String]) {
    if args.len() < 4 { usage(); std::process::exit(2); }
    let path = &args[2];
    let n: i64 = args[3].parse().expect("hc_count must be int");

    eprintln!("# wire_realbench (Rust): loading {path} ...");
    let t_load = Instant::now();
    let snap = snapshot::load(path).expect("snapshot load failed");
    eprintln!("# snapshot: {} nodes, {} tlist, {} memories, {} mem-handlers (load: {:.3} s)",
              snap.node_count, snap.tlist_len, snap.memories.len(), snap.handlers.len(),
              t_load.elapsed().as_secs_f64());

    let clock_node = snap.clock_node;
    let mut wc = wire::WireCore::from_snapshot(snap);

    let t = Instant::now();
    wc.step(n, clock_node);
    let dt = t.elapsed();
    let secs = dt.as_secs_f64();
    let hcps = (n as f64) / secs;
    let us_per_hc = secs * 1e6 / (n as f64);

    let checksum = wc.node_states_checksum();
    println!("# bench-hc: (rust port) — {n} master half-cycles");
    println!("# simulated: {n} master half-cycles in {secs:.3} s");
    println!("# rate: {hcps:.0} hc/s ({us_per_hc:.2} µs/hc)");
    println!("# NodeStates checksum @ t={}: 0x{checksum:016X}  (A/B equivalence: must match the C# baseline run)",
             wc.time);
}

fn shot(args: &[String]) {
    if args.len() < 5 { usage(); std::process::exit(2); }
    let path = &args[2];
    let frames: i64 = args[3].parse().expect("frames must be int");
    let out = &args[4];

    eprintln!("# wire_realbench (Rust shot): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    eprintln!("# snapshot: {} nodes, {} tlist, {} memories, {} mem-handlers (pclk1 node {}, hpos {} bits, vpos {} bits, pal {} entries)",
              snap.node_count, snap.tlist_len, snap.memories.len(), snap.handlers.len(),
              snap.pclk1_node, snap.hpos_nodes.len(), snap.vpos_nodes.len(), snap.pal_ram_nodes.len());

    let clock_node = snap.clock_node;
    let vblank_node = snap.ppu_vblank_node;
    let mut wc = wire::WireCore::from_snapshot(snap);

    let t = Instant::now();
    let mut total_hc = 0i64;
    for f in 1..=frames {
        let hc = wc.run_frame(clock_node, vblank_node, 1_200_000);
        total_hc += hc;
        if f % 10 == 0 || f == frames {
            eprintln!("# frame {f}/{frames}: {hc} half-cycles  (t={}, total {} hc)", wc.time, total_hc);
        }
    }
    let dt = t.elapsed();
    eprintln!("# rendered {frames} frames in {:.3} s, {} master half-cycles total", dt.as_secs_f64(), total_hc);

    // write PNG
    let file = File::create(out).expect("create PNG");
    let w = BufWriter::new(file);
    let mut encoder = png::Encoder::new(w, wire::SCREEN_W as u32, wire::SCREEN_H as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().expect("PNG header");
    // ARGB → RGBA byte order
    let mut rgba = Vec::with_capacity(wire::SCREEN_W * wire::SCREEN_H * 4);
    for &argb in &wc.framebuffer {
        let r = ((argb >> 16) & 0xFF) as u8;
        let g = ((argb >>  8) & 0xFF) as u8;
        let b = ( argb        & 0xFF) as u8;
        rgba.extend_from_slice(&[r, g, b, 0xFF]);
    }
    writer.write_image_data(&rgba).expect("PNG body");
    println!("# wrote {out}  ({}x{}, {} half-cycles total)", wire::SCREEN_W, wire::SCREEN_H, total_hc);

    let checksum = wc.node_states_checksum();
    println!("# NodeStates checksum @ t={}: 0x{checksum:016X}", wc.time);
}
