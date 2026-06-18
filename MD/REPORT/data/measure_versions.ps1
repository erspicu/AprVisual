# Measure hotpath cycles/hc + throughput for every released benchmark version, HC=400000.
# Metric: QueryProcessCycleTime (exact CPU cycles, frequency/scheduling-independent). Load subtracted via a 40k run.
Add-Type @"
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class CT {
  [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong c);
  public static ulong RunCycles(string exe, string args, out string so) {
    var psi = new ProcessStartInfo(exe, args){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
    var p = Process.Start(psi);
    so = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    ulong c=0; QueryProcessCycleTime(p.Handle, out c);
    return c;
  }
}
"@
# chronological order (by release creation date)
$vers = @(
 @{v="2026.05.30";d="2026-05-30"}, @{v="2026.05.31";d="2026-05-31"}, @{v="2026.06.01";d="2026-05-31"},
 @{v="2026.06.03";d="2026-06-02"}, @{v="2026.06.04";d="2026-06-03"}, @{v="2026.06.05";d="2026-06-05"},
 @{v="2026.06.07";d="2026-06-06"}, @{v="2026.06.07b";d="2026-06-07"}, @{v="2026.06.08";d="2026-06-08"},
 @{v="2026.06.09";d="2026-06-08"}, @{v="2026.06.09b";d="2026-06-08"}, @{v="2026.06.09c";d="2026-06-09"},
 @{v="2026.06.09d";d="2026-06-09"}, @{v="2026.06.09e";d="2026-06-09"}, @{v="2026.06.11";d="2026-06-10"},
 @{v="2026.06.12";d="2026-06-11"}, @{v="2026.06.18";d="2026-06-18"}, @{v="2026.06.19";d="2026-06-18"}
)
$vd="C:\ai_project\AprVisual\temp\vers"; New-Item -ItemType Directory -Force -Path $vd | Out-Null
$rows=@()
foreach($e in $vers){
  $v=$e.v; $d=Join-Path $vd $v
  $exe=(Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue|Select-Object -First 1).FullName
  if(-not $exe){
    gh -R erspicu/AprVisual release download "benchmark-$v" -D $d 2>&1 | Out-Null
    $zip=Get-ChildItem $d -Filter *.zip -EA SilentlyContinue|Select-Object -First 1
    if($zip){ Expand-Archive -Path $zip.FullName -DestinationPath $d -Force }
    $exe=(Get-ChildItem $d -Recurse -Filter "AprVisual.S1.exe" -EA SilentlyContinue|Select-Object -First 1).FullName
  }
  if(-not $exe){ Write-Output "$v : EXE NOT FOUND - skip"; continue }
  $rom=(Get-ChildItem $d -Recurse -Filter "full_palette.nes"|Select-Object -First 1).FullName
  $sd=(Get-ChildItem $d -Recurse -Directory -Filter "system-def"|Select-Object -First 1).FullName
  $a4="--benchmark `"$rom`" --bench-hc 400000 --extra-ram --system-def-dir `"$sd`""
  $a0="--benchmark `"$rom`" --bench-hc 40000 --extra-ram --system-def-dir `"$sd`""
  $so=""
  $c4a=[CT]::RunCycles($exe,$a4,[ref]$so); $rate=([regex]::Match($so,'rate:\s*([\d,]+) hc/s')).Groups[1].Value -replace ',',''
  $sum=([regex]::Match($so,'checksum @ t=\d+: (0x[0-9A-F]+)')).Groups[1].Value
  $c4b=[CT]::RunCycles($exe,$a4,[ref]$so)
  $c4=[Math]::Min($c4a,$c4b)
  $c0=[CT]::RunCycles($exe,$a0,[ref]$so)
  $cphc=[Math]::Round(($c4-$c0)/360000,1)
  $rows += [pscustomobject]@{Version=$v;Date=$e.d;ThroughputHcS=[int]$rate;Cycles400k=$c4;Cycles40k=$c0;HotpathCyclesPerHc=$cphc;Checksum=$sum}
  Write-Output ("{0,-12} {1,9} hc/s  cyc/hc={2,8}  ck={3}" -f $v,$rate,$cphc,$sum)
}
$csv="C:\ai_project\AprVisual\temp\version_perf.csv"
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
Write-Output "`nWROTE $csv ($($rows.Count) rows)"
