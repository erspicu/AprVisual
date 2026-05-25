// wire_realbench — Rust port of bench-hc / --screenshot on a snapshot exported by C#.
//
// Run:
//   wire_realbench bench <snapshot.aprsnap> <hc_count> [--fast-path] [--prune-merge]
//   wire_realbench shot  <snapshot.aprsnap> <frames>   <out.png> [--fast-path] [--prune-merge]

mod snapshot;
mod wire;
mod parallel;

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
    eprintln!("usage: wire_realbench bench <snapshot.aprsnap> <hc_count> [--fast-path] [--prune-merge]");
    eprintln!("       wire_realbench shot  <snapshot.aprsnap> <frames>   <out.png>  [--fast-path] [--prune-merge]");
}

fn flag_set(args: &[String], flag: &str) -> bool {
    args.iter().any(|a| a == flag)
}

fn configure(wc: &mut wire::WireCore, args: &[String]) {
    if flag_set(args, "--fast-path")   { wc.enable_fast_path();   eprintln!("# --fast-path: {} pure-logic-gnd nodes classified", wc.fast_path_count); }
    if flag_set(args, "--prune-merge") { wc.enable_prune_merge(); eprintln!("# --prune-merge: topology-group-ID skip armed"); }
    if flag_set(args, "--parallel")    { wc.enable_parallel();    eprintln!("# --parallel: per-chip bucketed settle (Phase 1: serial-bucketed for correctness verification)"); }
    if flag_set(args, "--settle-stats"){ wc.enable_settle_stats(); eprintln!("# --settle-stats: ProcessQueue iter histogram"); }
}

fn bench(args: &[String]) {
    if args.len() < 4 { usage(); std::process::exit(2); }
    let path = &args[2];
    let n: i64 = args[3].parse().expect("hc_count must be int");

    eprintln!("# wire_realbench (Rust): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let clock_node = snap.clock_node;
    let mut wc = wire::WireCore::from_snapshot(snap);
    configure(&mut wc, args);

    let t = Instant::now();
    wc.step(n, clock_node);
    let secs = t.elapsed().as_secs_f64();
    let hcps = (n as f64) / secs;
    let us_per_hc = secs * 1e6 / (n as f64);

    let checksum = wc.node_states_checksum();
    println!("# bench-hc: (rust port) — {n} master half-cycles");
    println!("# simulated: {n} master half-cycles in {secs:.3} s");
    println!("# rate: {hcps:.0} hc/s ({us_per_hc:.2} µs/hc)");
    println!("# NodeStates checksum @ t={}: 0x{checksum:016X}  (A/B equivalence: must match the C# baseline run)",
             wc.time);
    if wc.enable_settle_stats { println!("# {}", wc.settle_stats_report()); }
    if wc.enable_parallel {
        println!("# parallel walks: total {} | pure-CPU {} ({:.1}%) | pure-PPU {} ({:.1}%) | pure-other {} ({:.1}%) | crossed→serial {} ({:.1}%)",
                 wc.walks_total, wc.walks_pure_cpu, 100.0 * wc.walks_pure_cpu as f64 / wc.walks_total as f64,
                 wc.walks_pure_ppu, 100.0 * wc.walks_pure_ppu as f64 / wc.walks_total as f64,
                 wc.walks_pure_other, 100.0 * wc.walks_pure_other as f64 / wc.walks_total as f64,
                 wc.walks_crossed, 100.0 * wc.walks_crossed as f64 / wc.walks_total as f64);
    }
}

fn shot(args: &[String]) {
    if args.len() < 5 { usage(); std::process::exit(2); }
    let path = &args[2];
    let frames: i64 = args[3].parse().expect("frames must be int");
    let out = &args[4];

    eprintln!("# wire_realbench (Rust shot): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let clock_node = snap.clock_node;
    let vblank_node = snap.ppu_vblank_node;
    let mut wc = wire::WireCore::from_snapshot(snap);
    configure(&mut wc, args);

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
    if wc.enable_settle_stats {
        println!("# {}", wc.settle_stats_report());
    }
}
