// wire_realbench — Rust port of bench-hc on a snapshot exported by C#'s --export-snapshot.
//
// Run:
//   cargo run --release -- <snapshot.aprsnap> <hc_count>
//
// Output mirrors C#'s --bench-hc format: load time, hc/s rate, NodeStates checksum (must
// match the C# baseline run for verification).

mod snapshot;
mod wire;

use std::time::Instant;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 3 {
        eprintln!("usage: wire_realbench <snapshot.aprsnap> <hc_count>");
        std::process::exit(2);
    }
    let path = &args[1];
    let n: i64 = args[2].parse().expect("hc_count must be int");

    eprintln!("# wire_realbench (Rust): loading {path} ...");
    let t_load = Instant::now();
    let snap = snapshot::load(path).expect("snapshot load failed");
    eprintln!("# snapshot: {} nodes, {} tlist, {} memories, {} mem-handlers (load: {:.3} s)",
              snap.node_count, snap.tlist_len, snap.memories.len(), snap.handlers.len(),
              t_load.elapsed().as_secs_f64());

    let clock_node = snap.clock_node;
    let mut wc = wire::WireCore::from_snapshot(snap);

    // bench
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
