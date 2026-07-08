# Move out\{results,screenshots,logs} into out\archive_<timestamp>\ so the next
# regression starts from a clean directory. Called by archive_old_results.bat.
# Safe to run whenever no runner is active (never move a live run's data).
$out = Join-Path $PSScriptRoot 'out'
if (-not (Test-Path $out)) { Write-Host "no out\ directory yet - nothing to archive"; exit 0 }

$ts   = Get-Date -Format 'yyyyMMdd_HHmm'
$arch = Join-Path $out "archive_$ts"
$moved = $false
foreach ($d in 'results', 'screenshots', 'logs') {
    $p = Join-Path $out $d
    if (Test-Path $p) {
        if (-not (Test-Path $arch)) { New-Item -ItemType Directory -Path $arch | Out-Null }
        Move-Item $p (Join-Path $arch $d)
        $moved = $true
    }
}
# also sweep loose run*.log / *.err left in out\ (previous campaign logs)
Get-ChildItem $out -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '\.(log|err)$' } |
    ForEach-Object {
        if (-not (Test-Path $arch)) { New-Item -ItemType Directory -Path $arch | Out-Null }
        Move-Item $_.FullName (Join-Path $arch $_.Name); $moved = $true
    }

if ($moved) {
    $n = (Get-ChildItem (Join-Path $arch 'results') -Filter *.json -ErrorAction SilentlyContinue).Count
    Write-Host "archived $n result(s) + screenshots/logs to out\archive_$ts"
} else {
    Write-Host "nothing to archive (out\ already clean)"
}
