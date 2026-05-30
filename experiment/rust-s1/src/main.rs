// wire_s1 — AprVisual.S1 Rust fork bench-hc / shot / frame-dump runner.
//
// Usage:
//   wire_s1 bench     <snapshot.aprsnap> <hc_count> [log_dir]
//   wire_s1 shot      <snapshot.aprsnap> <frames>   <out.png>
//   wire_s1 framedump <snapshot.aprsnap> <frames>   <out_dir>

mod snapshot;
mod wire;

use std::time::{Instant, SystemTime, UNIX_EPOCH};
use std::fs::File;
use std::io::BufWriter;
use std::path::Path;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 2 { usage(); std::process::exit(2); }
    match args[1].as_str() {
        "bench"     => bench(&args),
        "shot"      => shot(&args),
        "framedump" => framedump(&args),
        _           => { usage(); std::process::exit(2); }
    }
}

fn usage() {
    eprintln!("usage: wire_s1 bench     <snapshot.aprsnap> <hc_count>");
    eprintln!("       wire_s1 shot      <snapshot.aprsnap> <frames>   <out.png>");
    eprintln!("       wire_s1 framedump <snapshot.aprsnap> <frames>   <out_dir>");
    eprintln!();
    eprintln!("Fast-path is always on in the S1 fork (no flag).");
}

// Write the current framebuffer to <path> as an RGBA PNG.
fn write_framebuffer_png(framebuffer: &[u32], path: &Path) {
    let file = File::create(path).expect("create PNG");
    let w = BufWriter::new(file);
    let mut encoder = png::Encoder::new(w, wire::SCREEN_W as u32, wire::SCREEN_H as u32);
    encoder.set_color(png::ColorType::Rgba);
    encoder.set_depth(png::BitDepth::Eight);
    let mut writer = encoder.write_header().expect("PNG header");
    let mut rgba = Vec::with_capacity(wire::SCREEN_W * wire::SCREEN_H * 4);
    for &argb in framebuffer {
        let r = ((argb >> 16) & 0xFF) as u8;
        let g = ((argb >>  8) & 0xFF) as u8;
        let b = ( argb        & 0xFF) as u8;
        rgba.extend_from_slice(&[r, g, b, 0xFF]);
    }
    writer.write_image_data(&rgba).expect("PNG body");
}

// framedump: render <frames> frames, save EACH to <out_dir>/frame_NNNN.png,
// printing per-frame progress + wall-clock time.
fn framedump(args: &[String]) {
    if args.len() < 5 { usage(); std::process::exit(2); }
    let path = &args[2];
    let frames: i64 = args[3].parse().expect("frames must be int");
    let out_dir = &args[4];

    std::fs::create_dir_all(out_dir).expect("create out_dir");
    eprintln!("# wire_s1 (Rust S1 framedump): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let clock_node = snap.clock_node;
    let vblank_node = snap.ppu_vblank_node;
    let mut wc = wire::WireCore::from_snapshot(snap);
    eprintln!("# fast-path: {} pure-logic-gnd nodes classified (hardcoded on)", wc.fast_path_count);
    println!("# rendering {frames} frame(s) -> {out_dir}");

    let mut total = 0.0f64;
    for f in 1..=frames {
        let t = Instant::now();
        wc.run_frame(clock_node, vblank_node, 1_200_000);
        let secs = t.elapsed().as_secs_f64();
        total += secs;

        let fname = format!("frame_{f:04}.png");
        write_framebuffer_png(&wc.framebuffer, &Path::new(out_dir).join(&fname));
        println!("# frame {f:4}/{frames}  done in {secs:6.2} s  ->  {fname}");
    }
    println!("# =============================================");
    println!("#  {frames} frames in {total:.1} s  (avg {:.2} s/frame, {:.3} fps)",
             total / frames as f64, frames as f64 / total);
    println!("#  output dir: {out_dir}");
    println!("# =============================================");
}

fn bench(args: &[String]) {
    if args.len() < 4 { usage(); std::process::exit(2); }
    let path = &args[2];
    let n: i64 = args[3].parse().expect("hc_count must be int");
    let log_dir = args.get(4).map(|s| s.as_str()).unwrap_or("log");

    eprintln!("# wire_s1 (Rust S1 fork): loading {path} ...");
    let snap = snapshot::load(path).expect("snapshot load failed");
    let node_count = snap.node_count;
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
    write_bench_log(log_dir, path, n, secs, hcps, checksum, wc.fast_path_count, node_count);
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

// ── Benchmark JSON log (cross-platform; mirrors the C# engine's format) ──
// Writes <log_dir>/<machineGuid>-<user>-<UTCstamp>-rust.log so a future
// "upload your result" mechanism can aggregate runs. Console output unchanged.
fn write_bench_log(log_dir: &str, snap_path: &str, n: i64, secs: f64, hcps: f64,
                   checksum: u64, fast_path: usize, node_count: usize) {
    let dir = Path::new(log_dir);
    if std::fs::create_dir_all(dir).is_err() { eprintln!("# (bench log dir create failed)"); return; }
    let guid = machine_guid(dir);
    let user = std::env::var("USERNAME").or_else(|_| std::env::var("USER")).unwrap_or_else(|_| "unknown".into());
    let (stamp, iso) = utc_stamp();
    let pct = hcps / NES_REALTIME_HC_PER_SEC * 100.0;
    let gap = NES_REALTIME_HC_PER_SEC / hcps;
    let sec_per_fr = NES_HC_PER_FRAME / hcps;
    let rom = Path::new(snap_path).file_name().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
    let cpu = cpu_model();
    let cpus = std::thread::available_parallelism().map(|c| c.get()).unwrap_or(0);
    let json = format!(
"{{
  \"schema\": \"aprvisual-bench/1\",
  \"engine\": \"rust\",
  \"timestampUtc\": \"{iso}\",
  \"machineGuid\": \"{guid}\",
  \"user\": \"{user}\",
  \"os\": \"{os}\",
  \"arch\": \"{arch}\",
  \"cpuModel\": \"{cpu}\",
  \"cpuCount\": {cpus},
  \"rom\": \"{rom}\",
  \"benchHc\": {n},
  \"halfCycles\": {n},
  \"elapsedSec\": {secs:.6},
  \"hcPerSec\": {hcps:.1},
  \"secondsPerFrame\": {sec_per_fr:.4},
  \"pctRealtime\": {pct:.4},
  \"slowdownFactor\": {gap:.1},
  \"checksum\": \"0x{checksum:016X}\",
  \"fastPathNodes\": {fast_path},
  \"liveNodes\": {node_count}
}}
",
        guid = esc(&guid), user = esc(&user), os = std::env::consts::OS, arch = std::env::consts::ARCH,
        cpu = esc(&cpu), cpus = cpus, rom = esc(&rom), n = n, secs = secs, hcps = hcps,
        sec_per_fr = sec_per_fr, pct = pct, gap = gap, checksum = checksum,
        fast_path = fast_path, node_count = node_count, iso = iso);
    let fname = format!("{}-{}-{}-rust.log", sanitize(&guid), sanitize(&user), stamp);
    let file = dir.join(fname);
    match std::fs::write(&file, json) {
        Ok(_)  => println!("# log written: {}", file.display()),
        Err(e) => eprintln!("# (bench log write skipped: {e})"),
    }
}

// Stable per-machine id, consistent with the C# engine on the same machine:
// Windows -> registry MachineGuid; macOS -> IOPlatformUUID; else -> cached file in log_dir.
fn machine_guid(log_dir: &Path) -> String {
    #[cfg(windows)]
    {
        if let Ok(out) = std::process::Command::new("reg")
            .args(["query", r"HKLM\SOFTWARE\Microsoft\Cryptography", "/v", "MachineGuid"]).output()
        {
            let s = String::from_utf8_lossy(&out.stdout);
            for line in s.lines() {
                if let Some(idx) = line.find("REG_SZ") {
                    let g = line[idx + 6..].trim();
                    if !g.is_empty() { return g.to_string(); }
                }
            }
        }
    }
    #[cfg(target_os = "macos")]
    {
        if let Ok(out) = std::process::Command::new("ioreg")
            .args(["-rd1", "-c", "IOPlatformExpertDevice"]).output()
        {
            let s = String::from_utf8_lossy(&out.stdout);
            for line in s.lines() {
                if line.contains("IOPlatformUUID") {
                    if let Some(v) = line.split('=').nth(1) {
                        let v = v.trim().trim_matches('"');
                        if !v.is_empty() { return v.to_string(); }
                    }
                }
            }
        }
    }
    let f = log_dir.join("machine.guid");
    if let Ok(s) = std::fs::read_to_string(&f) {
        let s = s.trim();
        if !s.is_empty() { return s.to_string(); }
    }
    let g = pseudo_guid();
    let _ = std::fs::write(&f, &g);
    g
}

fn cpu_model() -> String {
    #[cfg(windows)]
    {
        if let Ok(out) = std::process::Command::new("reg")
            .args(["query", r"HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "/v", "ProcessorNameString"]).output()
        {
            let s = String::from_utf8_lossy(&out.stdout);
            for line in s.lines() {
                if let Some(idx) = line.find("REG_SZ") {
                    let v = line[idx + 6..].trim();
                    if !v.is_empty() { return v.to_string(); }
                }
            }
        }
    }
    #[cfg(target_os = "macos")]
    {
        if let Ok(out) = std::process::Command::new("sysctl").args(["-n", "machdep.cpu.brand_string"]).output() {
            let v = String::from_utf8_lossy(&out.stdout).trim().to_string();
            if !v.is_empty() { return v; }
        }
    }
    "unknown".to_string()
}

fn pseudo_guid() -> String {
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_nanos()).unwrap_or(0);
    let pid = std::process::id() as u128;
    let host = std::env::var("COMPUTERNAME").or_else(|_| std::env::var("HOSTNAME")).unwrap_or_default();
    let mut h: u128 = 0xcbf29ce484222325;
    for b in host.bytes() { h = h.wrapping_mul(0x100000001b3).wrapping_add(b as u128); }
    let x = nanos ^ pid.wrapping_shl(64) ^ h;
    let s = format!("{x:032x}");
    format!("{}-{}-{}-{}-{}", &s[0..8], &s[8..12], &s[12..16], &s[16..20], &s[20..32])
}

// epoch seconds -> (YYYYMMDDhhmmss, ISO-8601 UTC) via Howard Hinnant's civil_from_days.
fn utc_stamp() -> (String, String) {
    let secs = SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0) as i64;
    let days = secs.div_euclid(86400);
    let rem = secs.rem_euclid(86400);
    let (hh, mm, ss) = (rem / 3600, (rem % 3600) / 60, rem % 60);
    let z = days + 719468;
    let era = if z >= 0 { z } else { z - 146096 } / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let y0 = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y0 + 1 } else { y0 };
    (format!("{y:04}{m:02}{d:02}{hh:02}{mm:02}{ss:02}"),
     format!("{y:04}-{m:02}-{d:02}T{hh:02}:{mm:02}:{ss:02}Z"))
}

fn esc(s: &str) -> String {
    let mut o = String::with_capacity(s.len());
    for c in s.chars() {
        match c {
            '"' | '\\' => { o.push('\\'); o.push(c); }
            '\n' => o.push_str("\\n"),
            '\r' => o.push_str("\\r"),
            '\t' => o.push_str("\\t"),
            c if (c as u32) < 0x20 => o.push(' '),
            c => o.push(c),
        }
    }
    o
}

fn sanitize(s: &str) -> String {
    s.chars().map(|c| if c.is_ascii_alphanumeric() || c == '.' || c == '-' || c == '_' { c } else { '_' }).collect()
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

    write_framebuffer_png(&wc.framebuffer, Path::new(out));
    println!("# wrote {out}  ({}x{}, {} half-cycles total)", wire::SCREEN_W, wire::SCREEN_H, total_hc);

    let checksum = wc.node_states_checksum();
    println!("# NodeStates checksum @ t={}: 0x{checksum:016X}", wc.time);
}
