// wire_s1 — AprVisual.S1 Rust fork bench-hc / shot runner.
//
// Usage:
//   wire_s1 bench <snapshot.aprsnap> <hc_count>
//   wire_s1 shot  <snapshot.aprsnap> <frames>   <out.png>

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
    eprintln!("usage: wire_s1 bench <snapshot.aprsnap> <hc_count>");
    eprintln!("       wire_s1 shot  <snapshot.aprsnap> <frames>   <out.png>");
    eprintln!();
    eprintln!("Fast-path is always on in the S1 fork (no flag).");
}

fn bench(args: &[String]) {
    if args.len() < 4 { usage(); std::process::exit(2); }
    let path = &args[2];
    let n: i64 = args[3].parse().expect("hc_count must be int");

    eprintln!("# wire_s1 (Rust S1 fork): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let clock_node = snap.clock_node;
    let mut wc = wire::WireCore::from_snapshot(snap);
    eprintln!("# fast-path: {} pure-logic-gnd nodes classified (hardcoded on)", wc.fast_path_count);

    let t = Instant::now();
    wc.step(n, clock_node);
    let secs = t.elapsed().as_secs_f64();
    let hcps = (n as f64) / secs;
    let us_per_hc = secs * 1e6 / (n as f64);

    let checksum = wc.node_states_checksum();
    println!("# bench-hc: (rust S1) — {n} master half-cycles");
    println!("# simulated: {n} master half-cycles in {secs:.3} s");
    println!("# rate: {hcps:.0} hc/s ({us_per_hc:.2} µs/hc)");
    println!("# NodeStates checksum @ t={}: 0x{checksum:016X}  (A/B equivalence: must match the C# baseline run)",
             wc.time);
    print_realtime_gap(hcps);
}

// NES NTSC: 1.789773 MHz CPU * 24 master-half-cycles/CPU-cycle = 42,954,552 hc/s,
// i.e. 60.0988 frames/s * 714,732 hc/frame. Show how far the sim rate is from real-time.
const NES_REALTIME_HC_PER_SEC: f64 = 42_954_552.0;
const NES_REALTIME_FPS: f64 = 60.0988;
const NES_HC_PER_FRAME: f64 = 714_732.0;   // 357,366 master clocks * 2 half-cycles
fn print_realtime_gap(hcps: f64) {
    let pct = hcps / NES_REALTIME_HC_PER_SEC * 100.0;
    let gap = NES_REALTIME_HC_PER_SEC / hcps;
    let fps = hcps / NES_HC_PER_FRAME;
    let sec_per_fr = NES_HC_PER_FRAME / hcps;
    println!("# =============================================");
    println!("#  PERFORMANCE: {:.1}K hc/s  ({hcps:.0} hc/s)", hcps / 1000.0);
    println!("#  vs NES NTSC real-time ({:.0}K hc/s):", NES_REALTIME_HC_PER_SEC / 1000.0);
    println!("#    {pct:.3}% of real-time   ->   {gap:.1}x too slow");
    println!("#    {fps:.3} simulated NES frames / real second  (real NES = {NES_REALTIME_FPS:.1} fps)");
    println!("#    {sec_per_fr:.2} s to render 1 frame  (real NES = {:.4} s/frame)", 1.0 / NES_REALTIME_FPS);
    println!("# =============================================");
}

fn shot(args: &[String]) {
    if args.len() < 5 { usage(); std::process::exit(2); }
    let path = &args[2];
    let frames: i64 = args[3].parse().expect("frames must be int");
    let out = &args[4];

    eprintln!("# wire_s1 (Rust S1 shot): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let clock_node = snap.clock_node;
    let vblank_node = snap.ppu_vblank_node;
    let mut wc = wire::WireCore::from_snapshot(snap);
    eprintln!("# fast-path: {} pure-logic-gnd nodes classified (hardcoded on)", wc.fast_path_count);

    let t = Instant::now();
    let mut total_hc = 0i64;
    for f in 1..=frames {
        let hc = wc.run_frame(clock_node, vblank_node, 1_200_000);
        total_hc += hc;
        if f % 10 == 0 || f == frames {
            eprintln!("# frame {f}/{frames}: {hc} half-cycles  (t={}, total {} hc)", wc.time, total_hc);
        }
    }
    let secs = t.elapsed().as_secs_f64();
    eprintln!("# rendered {frames} frames in {secs:.3} s, {total_hc} master half-cycles total ({:.0} hc/s effective)",
              (total_hc as f64) / secs);

    let file = File::create(out).expect("create PNG");
    let w = BufWriter::new(file);
    let mut encoder = png::Encoder::new(w, wire::SCREEN_W as u32, wire::SCREEN_H as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().expect("PNG header");
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
