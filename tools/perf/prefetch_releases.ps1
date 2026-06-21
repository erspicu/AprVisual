# AprVisual perf workflow - PREFETCH release benchmark binaries into the local cache.
#
# The x64 collector (collect_x64.ps1 -> Get-Exe) already reuses anything under
# releases/<version>/, but it only downloads a version the first time it's needed, one at a
# time, mid-measurement. This script fills that cache up-front and idempotently so a later
# `collect_x64.ps1` / `run_all.ps1 -Full` is a pure cache hit (no downloads during timing).
#
# Cache layout (gitignored, machine-local, persistent across sessions):
#   tools/perf/releases/<version>/AprVisualBenchMark/win/csharp/AprVisual.S1.exe   (+ rom + system-def)
#
# Idempotent: a version whose AprVisual.S1.exe already exists is SKIPPED (no re-download).
# By default the downloaded .zip is deleted after extraction (keeps the exe, halves disk:
# ~85 MB -> ~45 MB per version). Pass -KeepZip to retain the zip too.
#
#   pwsh tools/perf/prefetch_releases.ps1                       # fetch every missing version
#   pwsh tools/perf/prefetch_releases.ps1 -Versions 2026.06.20  # just one
#   pwsh tools/perf/prefetch_releases.ps1 -KeepZip              # keep the .zip as well
#   pwsh tools/perf/prefetch_releases.ps1 -Force                # re-download even if cached
param(
  [string[]]$Versions = @(),                                  # empty = all versions in metadata.csv
  [switch]$KeepZip,
  [switch]$Force,
  [string]$Repo = "erspicu/AprVisual",
  [string]$CacheDir = "$PSScriptRoot\releases"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $CacheDir | Out-Null

if (-not $Versions -or $Versions.Count -eq 0) {
  $Versions = (Import-Csv "$PSScriptRoot\metadata.csv").version
}

$hit = 0; $got = 0; $fail = 0
foreach ($v in $Versions) {
  $d   = Join-Path $CacheDir $v
  $exe = (Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue | Select-Object -First 1).FullName
  if ($exe -and -not $Force) { Write-Host ("{0,-12} cached  (hit)" -f $v) -ForegroundColor DarkGray; $hit++; continue }

  New-Item -ItemType Directory -Force -Path $d | Out-Null
  Write-Host ("{0,-12} downloading benchmark-{0} ..." -f $v) -ForegroundColor Cyan
  gh -R $Repo release download "benchmark-$v" -D $d --clobber 2>&1 | Out-Null
  $zip = Get-ChildItem $d -Filter *.zip -EA SilentlyContinue | Select-Object -First 1
  if (-not $zip) { Write-Host ("{0,-12} NO ZIP ASSET - skip" -f $v) -ForegroundColor Red; $fail++; continue }
  Expand-Archive -Path $zip.FullName -DestinationPath $d -Force
  $exe = (Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue | Select-Object -First 1).FullName
  if (-not $exe) { Write-Host ("{0,-12} EXE NOT IN ZIP - skip" -f $v) -ForegroundColor Red; $fail++; continue }
  if (-not $KeepZip) { Remove-Item $zip.FullName -Force -EA SilentlyContinue }
  Write-Host ("{0,-12} ready   ({1})" -f $v, $(if($KeepZip){"zip kept"}else{"zip removed"})) -ForegroundColor Green
  $got++
}

$total = (Get-ChildItem $CacheDir -Directory -EA SilentlyContinue).Count
$sizeMB = [math]::Round((Get-ChildItem $CacheDir -Recurse -File -EA SilentlyContinue | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "`nprefetch done: $hit cached, $got downloaded, $fail failed | cache now holds $total versions, ${sizeMB} MB" -ForegroundColor Green
Write-Host "next: pwsh tools/perf/collect_x64.ps1   (pure cache hit now)" -ForegroundColor DarkGray
