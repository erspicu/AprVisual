// Measure rayon::join per-call overhead with no-op closures.
use std::time::Instant;

fn main() {
    let n = 1_000_000;
    let t = Instant::now();
    for _ in 0..n {
        rayon::join(|| {}, || {});
    }
    let elapsed = t.elapsed();
    println!("rayon::join × {n}: {:.2} ms total, {:.0} ns/call",
             elapsed.as_secs_f64() * 1000.0,
             elapsed.as_nanos() as f64 / n as f64);

    // also measure 2-arg with non-empty work
    let t = Instant::now();
    let mut sum: u64 = 0;
    for _ in 0..n / 10 {
        let (a, b) = rayon::join(|| 1u64, || 2u64);
        sum = sum.wrapping_add(a + b);
    }
    let elapsed = t.elapsed();
    println!("rayon::join (light work) × {}: {:.2} ms, {:.0} ns/call  (sum={sum})",
             n / 10,
             elapsed.as_secs_f64() * 1000.0,
             elapsed.as_nanos() as f64 / (n as f64 / 10.0));
}
