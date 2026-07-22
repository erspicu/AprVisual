<#
================================================================================
 release_benchmark_s1a.ps1 — build, package, and publish the AprVisualBenchMarkS1A zip
================================================================================
 The S1A counterpart of release_benchmark.ps1. S1A = S1 + the always-on M1-M6
 mechanisms; --benchmark runs the FULL engine (the mechanisms + the realistic
 power-up state are armed unconditionally). C# / Windows only — no Rust, no mac.

 What it does:
   1. Publishes the C# S1A engine as a SELF-CONTAINED, MULTI-FILE, TRIMMED folder
      into AprVisualBenchMarkS1A\win\csharp\  (no single .exe; no .NET install; no pdb).
   2. Smoke-tests the published exe at the golden calibration point:
        --benchmark 300k -> 0x41244C26C45EDD32   (full S1A engine — always full-armed)
      Aborts if it mismatches.
   3. Stages a copy EXCLUDING runtime output (log\, screenshots\) via .keep, then zips
      -> temp\pkg\AprVisualBenchMarkS1A.zip (top-level folder = AprVisualBenchMarkS1A).
   4. (-Publish) creates/refreshes the GitHub release benchmark-s1a-<Version> (--latest).

 Usage:
   pwsh tools\release_benchmark_s1a.ps1 -Version 2026.07.21            # build zip only
   pwsh tools\release_benchmark_s1a.ps1 -Version 2026.07.21 -Publish   # + GitHub release

 Notes:
   * gh CLI must be authenticated (gh auth status).
   * AprVisualBenchMarkS1A\ is gitignored (distribution staging).
   * The netlist under data\system-def is the CORRECTED 2A03/2C02 data (t13032b patch);
     it is copied in as an input, not rebuilt here.
================================================================================
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,   # e.g. 2026.07.21 -> tag benchmark-s1a-2026.07.21
    [switch]$Publish,
    [string]$Title,
    [string]$Notes
)
$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent $PSScriptRoot
$Bench   = Join-Path $Root 'AprVisualBenchMarkS1A'
$Csproj  = Join-Path $Root 'src\AprVisual.S1A\AprVisual.S1A.csproj'
$CsOut   = Join-Path $Bench 'win\csharp'
$Rom     = Join-Path $Bench 'roms\full_palette.nes'
$SysDef  = Join-Path $Bench 'data\system-def'
$Tag     = "benchmark-s1a-$Version"
$Stage   = Join-Path $Root 'temp\pkg\AprVisualBenchMarkS1A'
$Zip     = Join-Path $Root 'temp\pkg\AprVisualBenchMarkS1A.zip'
$GoldFull = '0x41244C26C45EDD32'   # full S1A engine, 300k (S1A is always full-armed — no raw mode)
$AssetUrl = "https://github.com/erspicu/AprVisual/releases/download/$Tag/AprVisualBenchMarkS1A.zip"

function Step($m) { Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# 1. publish C# self-contained / multi-file / trimmed / no-pdb -------------------
Step "1/5  publish C# S1A (self-contained, multi-file, trimmed, no pdb) -> win\csharp"
if (Test-Path $CsOut) { Remove-Item -Recurse -Force -LiteralPath $CsOut }
dotnet publish $Csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=true -p:DebugType=none -p:DebugSymbols=false `
    -o $CsOut | Out-Null
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
$nFiles = (Get-ChildItem $CsOut -File).Count
$mb = [math]::Round((Get-ChildItem $CsOut -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  win\csharp: $nFiles files, $mb MB"

# 2. smoke test: both golden checksums -------------------------------------------
Step "2/5  smoke test (full_palette 300k --extra-ram)"
$exe = Join-Path $CsOut 'AprVisual.S1A.exe'
$full = (& $exe --benchmark $Rom --bench-hc 300000 --extra-ram --system-def-dir $SysDef 2>&1) -join "`n"
if ($full -notmatch [regex]::Escape($GoldFull)) { Write-Host $full; throw "SMOKE FAILED — full-engine checksum != $GoldFull" }
Write-Host "  ok: full $GoldFull"

# 3. stage excluding log\ + screenshots\ ----------------------------------------
Step "3/5  stage copy (exclude log\, screenshots\)"
if (Test-Path (Join-Path $Root 'temp\pkg\AprVisualBenchMarkS1A')) { Remove-Item -Recurse -Force -LiteralPath $Stage }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
robocopy $Bench $Stage /E /XD "$Bench\log" "$Bench\screenshots" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
foreach ($d in 'log', 'screenshots\csharp') {
    New-Item -ItemType Directory -Force -Path "$Stage\$d" | Out-Null
    Set-Content -Path "$Stage\$d\.keep" -Value '' -NoNewline
}
$smb = [math]::Round((Get-ChildItem $Stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  staged: $smb MB"

# 4. zip -------------------------------------------------------------------------
Step "4/5  zip -> $Zip"
if (Test-Path $Zip) { Remove-Item -Force -LiteralPath $Zip }
Compress-Archive -Path $Stage -DestinationPath $Zip -CompressionLevel Optimal
Write-Host "  AprVisualBenchMarkS1A.zip = $([math]::Round((Get-Item $Zip).Length / 1MB, 1)) MB"

# 5. publish ---------------------------------------------------------------------
$commit = (git -C $Root rev-parse --short=7 HEAD).Trim()
if (-not $Title) { $Title = "AprVisual S1A Benchmark — $Version" }
if (-not $Notes) {
    $Notes = @"
AprVisual S1A switch-level NES benchmark (C# / Windows). Engine $commit.

S1A = S1 + the always-on M1-M6 physics mechanisms. ``--benchmark`` runs the FULL engine, always
(golden checksum $GoldFull @ full_palette 300k --extra-ram). There is no raw mode — for the raw
switch-level engine use the separate S1 benchmark. Windows C# only — self-contained, multi-file,
trimmed folder ``win/csharp/`` (~$mb MB), no .NET install needed. Corrected netlist (t13032b).
Run: ``run_csharp.bat`` (benchmark) / ``shot_csharp.bat`` (frame-dump).
"@
}
if ($Publish) {
    gh release view $Tag *> $null
    if ($LASTEXITCODE -eq 0) {
        Step "5/5  release $Tag exists -> upload asset (--clobber) + refresh notes"
        gh release upload $Tag $Zip --clobber
        if ($LASTEXITCODE) { throw "gh release upload failed ($LASTEXITCODE)" }
        gh release edit $Tag --title $Title --notes $Notes --latest | Out-Null
    } else {
        Step "5/5  gh release create $Tag (--latest)"
        gh release create $Tag $Zip --title $Title --notes $Notes --latest
        if ($LASTEXITCODE) { throw "gh release create failed ($LASTEXITCODE)" }
    }
    Write-Host "  download: $AssetUrl"
} else {
    Step "5/5  DRY RUN (no -Publish): zip built, release NOT created"
    Write-Host "  zip: $Zip"
}
