// Embed a benchmark version string at compile time: short git commit + commit date, with a
// "-dirty" marker if THIS crate's tracked sources have uncommitted changes. Baked into the
// binary so the distributed AprVisualBenchMark runner reports its version with no git at runtime.
// Plain short-commit + date scheme (no semver/tags), per the project's chosen versioning.
use std::process::Command;

fn main() {
    // Rebuild (re-embed) when the repo's HEAD / index changes — i.e. after a commit.
    for p in ["../../.git/HEAD", "../../.git/index"] {
        println!("cargo:rerun-if-changed={p}");
    }

    let commit = git(&["rev-parse", "--short=7", "HEAD"]).unwrap_or_else(|| "unknown".into());
    let date = git(&["log", "-1", "--format=%cd", "--date=short"]).unwrap_or_else(|| "unknown".into());
    // dirty ⇔ any tracked change under the crate dir (build.rs CWD = crate root); target/ is gitignored.
    let dirty = git(&["status", "--porcelain", "."]).map_or(false, |s| !s.trim().is_empty());

    let version = if dirty { format!("{commit}-dirty") } else { commit };
    println!("cargo:rustc-env=APR_VERSION={version}");
    println!("cargo:rustc-env=APR_COMMIT_DATE={date}");
}

fn git(args: &[&str]) -> Option<String> {
    let out = Command::new("git").args(args).output().ok()?;
    if !out.status.success() { return None; }
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    if s.is_empty() { None } else { Some(s) }
}
