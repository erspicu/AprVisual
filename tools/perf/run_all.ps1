# AprVisual perf workflow - ORCHESTRATE (x64): ensure metadata -> collect -> build_json -> (deploy).
# Default = incremental: collect only -EnsureVersion and MERGE into the existing data.json (old versions
# keep their numbers). -Full re-measures every version. Used standalone or by release_benchmark.ps1 -UpdatePerf.
#
#   pwsh tools/perf/run_all.ps1 -EnsureVersion 2026.06.20 -Title "..." -Deploy   # one new release
#   pwsh tools/perf/run_all.ps1 -Full -Deploy                                     # rebuild everything
param(
  [string]$Platform = "x64",
  [string]$EnsureVersion = "",
  [string]$Date = "",
  [string]$Title = "",
  [switch]$Full,
  [switch]$Locked,
  [switch]$Deploy
)
$ErrorActionPreference = "Stop"
$PerfDir = $PSScriptRoot
$Meta = Join-Path $PerfDir "metadata.csv"
$Data = Join-Path $PerfDir "web\$Platform\data.json"
$Out  = Join-Path $PerfDir "out"
function Step($m){ Write-Host "`n=== $m ===" -ForegroundColor Cyan }

# 1. ensure the version has a metadata row (stub-append if missing; user edits title/desc later) ---------
if ($EnsureVersion) {
  $have = (Import-Csv $Meta).version
  if ($have -notcontains $EnsureVersion) {
    if (-not $Date)  { $Date  = Get-Date -Format 'yyyy-MM-dd' }
    if (-not $Title) { $Title = "(pending — edit metadata.csv)" }
    $q = { param($s) '"' + ($s -replace '"','""') + '"' }
    $row = ($EnsureVersion,$Date,'net11.0','','0',$Title,'',$Title,'' | ForEach-Object { & $q $_ }) -join ','
    Add-Content -Path $Meta -Value $row -Encoding UTF8
    Write-Host "metadata.csv: appended stub for $EnsureVersion (fill title/desc + rerun build_json to refine)" -ForegroundColor Yellow
  }
}

# 2. collect ---------------------------------------------------------------------------------------------
$incremental = $EnsureVersion -and -not $Full
$collectArgs = @{}
if ($Locked) { $collectArgs.Locked = $true }
if ($incremental) { $collectArgs.Versions = @($EnsureVersion) }
Step "collect ($([string]::Join(',', ($collectArgs.Versions ?? @('all')))))"
& (Join-Path $PerfDir "collect_$Platform.ps1") @collectArgs
if ($LASTEXITCODE) { throw "collect failed ($LASTEXITCODE)" }

# 3. build_json (merge when incremental so untouched versions keep their numbers) ------------------------
Step "build_json -> web\$Platform\data.json"
$bj = @("$PerfDir\build_json.py","--platform",$Platform,"--metadata",$Meta,"--env","$PerfDir\env_$Platform.json",
        "--boost","$Out\boost.csv","--sizes","$Out\sizes.csv","--out",$Data)
if (Test-Path "$Out\locked.csv") { $bj += @("--locked","$Out\locked.csv") }
if ($incremental -and (Test-Path $Data)) { $bj += @("--merge",$Data) }
python @bj
if ($LASTEXITCODE) { throw "build_json failed ($LASTEXITCODE)" }

# 4. deploy ----------------------------------------------------------------------------------------------
if ($Deploy) {
  Step "deploy -> /version/$Platform/"
  & (Join-Path $PerfDir "deploy_version.ps1") -Platforms $Platform
  if ($LASTEXITCODE) { throw "deploy failed ($LASTEXITCODE)" }
}
Write-Host "`nrun_all done ($Platform$(if($incremental){", incremental "+$EnsureVersion}else{", full"}))." -ForegroundColor Green
