# AprVisual perf workflow - COLLECT (x64 boost).
# Runs each released benchmark binary, emits CSVs consumed by build_json.py:
#   out/boost.csv  Version,BoostTop3Avg,BoostMax,Samples,Checksum
#   out/sizes.csv  Version,IL,Native
#   out/locked.csv Version,LockedCycPerHc          (only with -Locked)
# Release zips are cached under releases/<version>/ and reused (no re-download).
#
#   pwsh tools/perf/collect_x64.ps1 [-Versions 2026.06.18,2026.06.19] [-Locked]
param(
  [string[]]$Versions = @(),                                  # empty = all versions in metadata.csv
  [int]$Runs = 5,
  [int]$BenchHc = 400000,
  [switch]$Locked,                                            # also measure load-subtracted cyc/hc at base clock
  [string]$Repo = "erspicu/AprVisual",
  [string]$CacheDir = "$PSScriptRoot\releases",
  [string]$OutDir   = "$PSScriptRoot\out"
)
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $CacheDir,$OutDir | Out-Null

# version list from metadata.csv unless overridden
if (-not $Versions -or $Versions.Count -eq 0) {
  $Versions = (Import-Csv "$PSScriptRoot\metadata.csv").version
}

# QueryProcessCycleTime (exact CPU cycles, frequency/scheduling-independent) for the -Locked cyc/hc metric
Add-Type @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class CT {
  [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong c);
  public static ulong RunCycles(string exe, string args, out string so) {
    var psi = new ProcessStartInfo(exe, args){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
    var p = Process.Start(psi); so = p.StandardOutput.ReadToEnd()+p.StandardError.ReadToEnd(); p.WaitForExit();
    ulong c=0; QueryProcessCycleTime(p.Handle, out c); return c;
  }
}
"@

function Get-Exe($v) {
  $d = Join-Path $CacheDir $v
  $exe = (Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue | Select-Object -First 1).FullName
  if (-not $exe) {
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    Write-Host "  downloading benchmark-$v ..." -ForegroundColor DarkGray
    gh -R $Repo release download "benchmark-$v" -D $d 2>&1 | Out-Null
    $zip = Get-ChildItem $d -Filter *.zip -EA SilentlyContinue | Select-Object -First 1
    if ($zip) { Expand-Archive -Path $zip.FullName -DestinationPath $d -Force }
    $exe = (Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue | Select-Object -First 1).FullName
  }
  return $exe
}

$boost=@(); $sizes=@(); $lockedRows=@()
if ($Locked) { & "$PSScriptRoot\..\cpu_lock_3.6ghz.bat" | Out-Null; Write-Host "CPU locked to base clock" -ForegroundColor Yellow }
try {
  foreach ($v in $Versions) {
    $exe = Get-Exe $v
    if (-not $exe) { Write-Host "$v : EXE NOT FOUND - skip" -ForegroundColor Red; continue }
    $d   = Split-Path $exe
    $rom = (Get-ChildItem (Join-Path $CacheDir $v) -Recurse -Filter "full_palette.nes" | Select-Object -First 1).FullName
    $sd  = (Get-ChildItem (Join-Path $CacheDir $v) -Recurse -Directory -Filter "system-def" | Select-Object -First 1).FullName
    $args4 = "--benchmark `"$rom`" --bench-hc $BenchHc --extra-ram --system-def-dir `"$sd`""

    # boost: N runs, parse rate + checksum
    $rates=@(); $ck=""
    for ($i=0; $i -lt $Runs; $i++) {
      $so=""; [void][CT]::RunCycles($exe,$args4,[ref]$so)
      $r=([regex]::Match($so,'rate:\s*([\d,]+) hc/s')).Groups[1].Value -replace ',',''
      if ($r) { $rates += [int]$r }
      if (-not $ck) { $ck=([regex]::Match($so,'(0x[0-9A-F]{16})')).Groups[1].Value }
    }
    $top3 = ($rates | Sort-Object -Descending | Select-Object -First 3 | Measure-Object -Average).Average
    $boost += [pscustomobject]@{Version=$v;BoostTop3Avg=[int]$top3;BoostMax=($rates|Measure-Object -Maximum).Maximum;Samples=($rates -join '/');Checksum=$ck}

    # sizes: IL + native of ProcessQueue via JitDisasmSummary
    $env:DOTNET_JitDisasmSummary="1"; $env:DOTNET_TieredCompilation="0"
    $so2=""; [void][CT]::RunCycles($exe,"--benchmark `"$rom`" --bench-hc 3000 --extra-ram --system-def-dir `"$sd`"",[ref]$so2)
    $env:DOTNET_JitDisasmSummary=$null; $env:DOTNET_TieredCompilation=$null
    $m=[regex]::Match($so2,'WireCore:ProcessQueue(Interp)?\(\) \[FullOpts, IL size=(\d+), code size=(\d+)\]')
    $sizes += [pscustomobject]@{Version=$v;IL=$m.Groups[2].Value;Native=$m.Groups[3].Value}

    $line="{0,-12} boost top3={1,8} max={2,8} ck={3} IL/nat={4}/{5}" -f $v,[int]$top3,($rates|Measure-Object -Maximum).Maximum,$ck.Substring(0,[Math]::Min(6,$ck.Length)),$m.Groups[2].Value,$m.Groups[3].Value

    # locked cyc/hc: load-subtracted QPCT (min of 2), frequency-independent count at fixed clock
    if ($Locked) {
      $so=""; $c4=[Math]::Min([CT]::RunCycles($exe,$args4,[ref]$so),[CT]::RunCycles($exe,$args4,[ref]$so))
      $c0=[CT]::RunCycles($exe,"--benchmark `"$rom`" --bench-hc 40000 --extra-ram --system-def-dir `"$sd`"",[ref]$so)
      $cphc=[Math]::Round(($c4-$c0)/($BenchHc-40000))
      $lockedRows += [pscustomobject]@{Version=$v;LockedCycPerHc=[int]$cphc}
      $line += "  cyc/hc=$cphc"
    }
    Write-Host $line
  }
} finally {
  if ($Locked) { & "$PSScriptRoot\..\cpu_restore.bat" | Out-Null; Write-Host "CPU clock restored" -ForegroundColor Yellow }
}

$boost | Export-Csv "$OutDir\boost.csv" -NoTypeInformation -Encoding UTF8
$sizes | Export-Csv "$OutDir\sizes.csv" -NoTypeInformation -Encoding UTF8
if ($Locked) { $lockedRows | Export-Csv "$OutDir\locked.csv" -NoTypeInformation -Encoding UTF8 }
Write-Host "`nWROTE $OutDir\boost.csv, sizes.csv$(if($Locked){', locked.csv'})  ($($boost.Count) versions)" -ForegroundColor Green
Write-Host "next: python tools/perf/build_json.py --platform x64 --metadata tools/perf/metadata.csv --env tools/perf/env_x64.json --boost $OutDir\boost.csv --sizes $OutDir\sizes.csv$(if($Locked){" --locked $OutDir\locked.csv"}) --out tools/perf/web/x64/data.json"
