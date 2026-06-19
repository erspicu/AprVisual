<#
================================================================================
 release_benchmark.ps1 — build, package, and publish the AprVisualBenchMark zip
================================================================================
 ONE command to cut a benchmark release. Reproduces the whole workflow so we
 never hand-assemble it again.

 What it does:
   1. Publishes the C# S1 engine as a SELF-CONTAINED, MULTI-FILE, TRIMMED folder
      into AprVisualBenchMark\win\csharp\  (no single .exe; no .NET install needed;
      no .pdb).  -> ~19 MB / ~36 files.
   2. Removes any stale single-file win\AprVisual.S1.exe.
   3. (-BuildRust) optionally rebuilds the Rust win binary from experiment\rust-s1.
   4. Smoke-tests the published exe: must hit the golden checksum 0x794A43ABDF169ADA
      (full_palette 300k --extra-ram) or it aborts.
   5. Stages a copy EXCLUDING runtime output: log\ and screenshots\ (keeps empty
      dirs via .keep) and any stray old *.exe.
   6. Zips -> temp\pkg\AprVisualBenchMark.zip  (top-level folder = AprVisualBenchMark).
   7. (-Publish) creates the GitHub release  benchmark-<Version>  with the zip,
      marked --latest. Without -Publish it only builds the zip (dry run).
   8. Prints the download URL + the reminder to bump WebSite/index.html links.

 Usage:
   pwsh tools\release_benchmark.ps1 -Version 2026.06.03            # build zip only
   pwsh tools\release_benchmark.ps1 -Version 2026.06.03 -Publish   # + GitHub release
   pwsh tools\release_benchmark.ps1 -Version 2026.06.03 -Publish -BuildRust

 Notes:
   * gh CLI must be authenticated (gh auth status).
   * The benchmark folder is gitignored (distribution staging); nothing here is committed.
   * Mac binary (mac\AprVisual.S1) is NOT rebuilt here (needs a Mac host); it ships as-is.
================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,   # e.g. 2026.06.03  -> tag benchmark-2026.06.03
    [switch]$Publish,
    [switch]$BuildRust,
    [string]$Title,
    [string]$Notes,
    [switch]$UpdatePerf,        # after publishing, auto-run the perf workflow (measure x64 + update /version dashboard)
    [string]$PerfTitle          # change description for the perf metadata row (else a stub is added)
)
$ErrorActionPreference = 'Stop'

$Root   = Split-Path -Parent $PSScriptRoot           # tools\ -> repo root
$Bench  = Join-Path $Root 'AprVisualBenchMark'
$Csproj = Join-Path $Root 'src\AprVisual.S1\AprVisual.S1.csproj'
$CsOut  = Join-Path $Bench 'win\csharp'
$Rom    = Join-Path $Bench 'roms\full_palette.nes'
$SysDef = Join-Path $Bench 'data\system-def'
$Tag    = "benchmark-$Version"
$Stage  = Join-Path $Root 'temp\pkg\AprVisualBenchMark'
$Zip    = Join-Path $Root 'temp\pkg\AprVisualBenchMark.zip'
$Golden = '0x794A43ABDF169ADA'
$AssetUrl = "https://github.com/erspicu/AprVisual/releases/download/$Tag/AprVisualBenchMark.zip"

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# 1. publish C# self-contained / multi-file / trimmed / no-pdb -------------------
Step "1/7  publish C# (self-contained, multi-file, trimmed, no pdb) -> win\csharp"
if (Test-Path $CsOut) { Remove-Item -Recurse -Force -LiteralPath $CsOut }
dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=true -p:DebugType=none -p:DebugSymbols=false `
    -o $CsOut | Out-Null
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
$nFiles = (Get-ChildItem $CsOut -File).Count
$mb = [math]::Round((Get-ChildItem $CsOut -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  win\csharp: $nFiles files, $mb MB"
$oldExe = Join-Path $Bench 'win\AprVisual.S1.exe'
if (Test-Path $oldExe) { Remove-Item -Force -LiteralPath $oldExe; Write-Host "  removed stale single-file win\AprVisual.S1.exe" }

# 2. optional Rust rebuild -------------------------------------------------------
if ($BuildRust) {
    Step "2/7  rebuild Rust (experiment\rust-s1) -> win\wire_s1.exe"
    Push-Location (Join-Path $Root 'experiment\rust-s1')
    cargo build --release | Out-Null
    Pop-Location
    Copy-Item (Join-Path $Root 'experiment\rust-s1\target\release\wire_s1.exe') (Join-Path $Bench 'win\wire_s1.exe') -Force
} else { Step "2/7  Rust: keeping existing win\wire_s1.exe (use -BuildRust to refresh)" }

# 3. smoke test: golden checksum -------------------------------------------------
Step "3/7  smoke test (full_palette 300k --extra-ram must hit $Golden)"
Push-Location $Bench
$out = & "$CsOut\AprVisual.S1.exe" --benchmark $Rom --bench-hc 300000 --extra-ram --system-def-dir $SysDef 2>&1
Pop-Location
$joined = $out -join "`n"
if ($joined -notmatch [regex]::Escape($Golden)) { Write-Host $joined; throw "SMOKE TEST FAILED — checksum != $Golden" }
if ($joined -match 'rate:\s*([\d,]+) hc/s') { Write-Host "  ok: $($Matches[1]) hc/s, checksum $Golden" }

# 4. stage excluding log\ + screenshots\ ----------------------------------------
Step "4/7  stage copy (exclude log\, screenshots\)"
if (Test-Path (Join-Path $Root 'temp\pkg')) { Remove-Item -Recurse -Force -LiteralPath (Join-Path $Root 'temp\pkg') }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
robocopy $Bench $Stage /E /XD "$Bench\log" "$Bench\screenshots" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
foreach ($d in 'log', 'screenshots\csharp', 'screenshots\rust') {
    New-Item -ItemType Directory -Force -Path "$Stage\$d" | Out-Null
    Set-Content -Path "$Stage\$d\.keep" -Value '' -NoNewline
}
# belt-and-suspenders: no stray *.exe outside win\csharp except wire_s1.exe
Get-ChildItem $Stage -Recurse -Filter *.exe |
    Where-Object { $_.FullName -notlike '*\win\csharp\*' -and $_.Name -ne 'wire_s1.exe' } |
    ForEach-Object { Write-Host "  dropping stray exe: $($_.Name)"; Remove-Item -Force $_.FullName }
$smb = [math]::Round((Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  staged: $smb MB"

# 5. zip -------------------------------------------------------------------------
Step "5/7  zip -> $Zip"
if (Test-Path $Zip) { Remove-Item -Force -LiteralPath $Zip }
Compress-Archive -Path $Stage -DestinationPath $Zip -CompressionLevel Optimal
$zmb = [math]::Round((Get-Item $Zip).Length / 1MB, 1)
Write-Host "  AprVisualBenchMark.zip = $zmb MB"

# 6. publish ---------------------------------------------------------------------
$commit = (git -C $Root rev-parse --short=7 HEAD).Trim()
if (-not $Title) { $Title = "AprVisual S1 Benchmark — $Version" }
if (-not $Notes) {
    $Notes = @"
AprVisual S1 cross-platform switch-level NES benchmark (C# + Rust). Engine $commit.

- Windows C#: self-contained, **multi-file, trimmed** folder ``win/csharp/`` (~$mb MB) — NOT a single packed .exe, no .NET install needed.
- Windows Rust: native ``win/wire_s1.exe``.
- macOS: self-contained C# binary ``mac/AprVisual.S1`` + Rust source (builds on first run).

Run: ``run_csharp.bat`` / ``run_rust.bat`` (Windows) · ``./run_csharp.sh`` / ``./run_rust.sh`` (Mac).
Bit-exact across engines (checksum $Golden @ full_palette 300k --extra-ram).
Package excludes runtime ``log/`` and ``screenshots/`` output.
"@
}
if ($Publish) {
    gh release view $Tag *> $null
    if ($LASTEXITCODE -eq 0) {
        # tag already exists (e.g. same-day refresh) — replace the asset + refresh title/notes
        Step "6/7  release $Tag exists -> upload asset (--clobber) + refresh notes"
        gh release upload $Tag $Zip --clobber
        if ($LASTEXITCODE) { throw "gh release upload failed ($LASTEXITCODE)" }
        gh release edit $Tag --title $Title --notes $Notes --latest | Out-Null
        Write-Host "  refreshed $Tag asset"
    } else {
        Step "6/7  gh release create $Tag (--latest)"
        gh release create $Tag $Zip --title $Title --notes $Notes --latest
        if ($LASTEXITCODE) { throw "gh release create failed ($LASTEXITCODE)" }
        Write-Host "  published $Tag"
    }
} else {
    Step "6/7  DRY RUN (no -Publish): zip built, release NOT created"
}

# 7. done ------------------------------------------------------------------------
Step "7/7  done"
Write-Host "  zip:      $Zip"
Write-Host "  download: $AssetUrl"
Write-Host "  -> update WebSite/index.html: bump benchmark-* download links to $Tag, and the 'Latest release' line." -ForegroundColor Yellow

# 8. (optional) auto-update the perf workflow / /version dashboard with this release -----------------
if ($UpdatePerf) {
    if (-not $Publish) {
        Write-Host "`n-UpdatePerf ignored on a dry run (no -Publish; release asset not available to measure)." -ForegroundColor Yellow
    } else {
        Step "8/8  perf workflow: measure x64 $Version + update /version dashboard"
        $runAll = Join-Path $PSScriptRoot 'perf\run_all.ps1'
        $pt = if ($PerfTitle) { $PerfTitle } else { '' }
        try {                                            # best-effort: the release already published
            & $runAll -Platform x64 -EnsureVersion $Version -Title $pt -Deploy
            Write-Host "  perf dashboard updated: https://baxermux.org/myemu/AprVisual/version/" -ForegroundColor Green
        } catch {
            Write-Host "  perf update FAILED: $_  — run tools/perf/run_all.ps1 -EnsureVersion $Version -Deploy manually" -ForegroundColor Red
        }
    }
}
