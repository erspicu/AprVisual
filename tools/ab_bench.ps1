<#
================================================================================
 ab_bench.ps1 — interleaved-paired A/B benchmark for the S1 C# engine
================================================================================
 Runs BASE and EXP alternately each round (adjacent in time so thermal drift
 cancels), captures hc/s + checksum from each, and reports:
   - per-engine median + trimmed mean (drop top+bottom)
   - exp/base ratio at median and at trimmed mean
   - paired win count (rounds where EXP > BASE)
   - checksum match guard (ABORTS-style WARN if any run != golden)

 Usage:
   pwsh tools/ab_bench.ps1 -BaseDir temp/bench_base -ExpDir src/AprVisual.S1/bin/Release/net10.0 -Rounds 20
================================================================================
#>
[CmdletBinding()]
param(
    [string]$BaseDir = 'temp/bench_base',
    [string]$ExpDir  = 'src/AprVisual.S1/bin/Release/net10.0',
    [int]$Rounds = 20,
    [int]$Hc = 300000,
    [string]$Golden = '0x794A43ABDF169ADA'
)
$ErrorActionPreference = 'Stop'
$Root   = Split-Path -Parent $PSScriptRoot
$Rom    = Join-Path $Root 'AprVisualBenchMark\roms\full_palette.nes'
$SysDef = Join-Path $Root 'AprVisualBenchMark\data\system-def'
$baseDll = Join-Path $Root "$BaseDir\AprVisual.S1.dll"
$expDll  = Join-Path $Root "$ExpDir\AprVisual.S1.dll"

function Run-One($dll) {
    $out = & dotnet $dll --benchmark $Rom --bench-hc $Hc --extra-ram --system-def-dir $SysDef 2>&1
    $joined = $out -join "`n"
    $rate = $null; $cksum = $null
    if ($joined -match '\(([\d,]+) hc/s\)') { $rate = [int]($Matches[1] -replace ',', '') }
    if ($joined -match 'checksum @ t=\d+:\s*(0x[0-9A-Fa-f]+)') { $cksum = $Matches[1] }
    [pscustomobject]@{ Rate = $rate; Cksum = $cksum }
}

function Stats($arr) {
    $s = $arr | Sort-Object
    $n = $s.Count
    $median = if ($n % 2) { $s[[int]($n/2)] } else { [int](($s[$n/2-1] + $s[$n/2]) / 2) }
    # trimmed mean: drop 1 from each end if n>=5
    $trim = if ($n -ge 5) { $s[1..($n-2)] } else { $s }
    $tmean = [int](($trim | Measure-Object -Average).Average)
    [pscustomobject]@{ Median = $median; TMean = $tmean; Min = $s[0]; Max = $s[-1] }
}

Write-Host "AB bench: BASE=$BaseDir  EXP=$ExpDir  rounds=$Rounds  hc=$Hc" -ForegroundColor Cyan
Write-Host "  warm-up (discarded)..." -NoNewline
$null = Run-One $baseDll; $null = Run-One $expDll
Write-Host " done"
$baseRates = @(); $expRates = @(); $wins = 0; $cksOk = $true
for ($r = 1; $r -le $Rounds; $r++) {
    $b = Run-One $baseDll
    $e = Run-One $expDll
    $baseRates += $b.Rate; $expRates += $e.Rate
    if ($e.Rate -gt $b.Rate) { $wins++ }
    if ($b.Cksum -ne $Golden -or $e.Cksum -ne $Golden) { $cksOk = $false }
    $delta = if ($b.Rate) { [math]::Round(100.0 * ($e.Rate - $b.Rate) / $b.Rate, 2) } else { 0 }
    Write-Host ("  r{0,-3} base={1,7} exp={2,7}  {3,6}%  {4}" -f $r, $b.Rate, $e.Rate, $delta, $(if ($e.Cksum -eq $Golden -and $b.Cksum -eq $Golden){'ok'}else{"CKSUM! b=$($b.Cksum) e=$($e.Cksum)"}))
}
$bs = Stats $baseRates; $es = Stats $expRates
$dMed = [math]::Round(100.0 * ($es.Median - $bs.Median) / $bs.Median, 2)
$dTm  = [math]::Round(100.0 * ($es.TMean  - $bs.TMean ) / $bs.TMean , 2)
Write-Host ""
Write-Host "=== RESULT ($Rounds rounds) ===" -ForegroundColor Yellow
Write-Host ("  BASE  median={0,7}  tmean={1,7}  [{2}..{3}]" -f $bs.Median, $bs.TMean, $bs.Min, $bs.Max)
Write-Host ("  EXP   median={0,7}  tmean={1,7}  [{2}..{3}]" -f $es.Median, $es.TMean, $es.Min, $es.Max)
Write-Host ("  delta median={0}%   tmean={1}%   paired-wins={2}/{3}" -f $dMed, $dTm, $wins, $Rounds)
Write-Host ("  checksum: {0}" -f $(if ($cksOk) {'all match golden'} else {'MISMATCH — NOT bit-exact!'})) -ForegroundColor $(if ($cksOk){'Green'}else{'Red'})
