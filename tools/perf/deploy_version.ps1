# AprVisual perf workflow - DEPLOY.
# FTPS-uploads the web bundle to <upload-base>/version/ on baxermux.org.
# Reads etc/ftp.txt (帳號/密碼/主機/上傳位置). Never echoes the password. Only writes under /version/
# (never touches the leaderboard's config.php / data).
#
#   pwsh tools/perf/deploy_version.ps1 [-Platforms x64,arm64]
param(
  [string]$FtpFile   = "$PSScriptRoot\..\..\etc\ftp.txt",
  [string]$WebDir    = "$PSScriptRoot\web",
  [string]$SubPath   = "version",
  [string[]]$Platforms = @("x64")
)
$ErrorActionPreference = "Stop"
# `pwsh -File ... -Platforms x64,arm64` arrives as one string "x64,arm64"; split + trim so both forms work
$Platforms = @($Platforms | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# parse "<key> <value>" lines
$kv = @{}
foreach ($line in Get-Content $FtpFile) {
  $p = $line -split '\s+', 2
  if ($p.Count -eq 2) { $kv[$p[0].Trim()] = $p[1].Trim() }
}
$acct = $kv['帳號']; $pass = $kv['密碼']; $ftpHost = $kv['主機']; $base = $kv['上傳位置']
if (-not ($acct -and $pass -and $ftpHost -and $base)) { throw "etc/ftp.txt missing 帳號/密碼/主機/上傳位置" }
$user = "$acct@$ftpHost"                                  # FTP login form: account@domain
$dest = "ftp://$ftpHost$base/$SubPath"                    # ftp://.../public_html/myemu/AprVisual/version

# temp curl config (password stays here, never printed; deleted in finally)
$cfgFile = [System.IO.Path]::GetTempFileName()
$cfgText = 'user "' + $user + ':' + $pass + '"' + "`nssl-reqd`ninsecure`nftp-create-dirs`n"
Set-Content -Path $cfgFile -Value $cfgText -Encoding ascii -NoNewline

try {
  $files = @(
    @{l = "$WebDir\.htaccess";  r = "$dest/.htaccess" },
    @{l = "$WebDir\index.html"; r = "$dest/index.html" },
    @{l = "$WebDir\app.css";    r = "$dest/app.css" },
    @{l = "$WebDir\app.js";     r = "$dest/app.js" },
    @{l = "$WebDir\api.php";    r = "$dest/api.php" }
  )
  foreach ($p in $Platforms) { $files += @{l = "$WebDir\$p\data.json"; r = "$dest/$p/data.json" } }

  $ok = 0
  foreach ($f in $files) {
    if (-not (Test-Path $f.l)) { Write-Host "skip (missing): $($f.l)" -ForegroundColor DarkGray; continue }
    curl.exe -sS -K $cfgFile -T $f.l $f.r
    if ($LASTEXITCODE -eq 0) { Write-Host "  uploaded $(Split-Path $f.l -Leaf) -> $($f.r)"; $ok++ }
    else { Write-Host "  FAILED $($f.l) (curl exit $LASTEXITCODE)" -ForegroundColor Red }
  }
  Write-Host "`n$ok file(s) uploaded." -ForegroundColor Green
} finally {
  Remove-Item $cfgFile -Force -ErrorAction SilentlyContinue
}
$webHost = $base -replace '^/public_html', ''
Write-Host "live: https://$ftpHost$webHost/$SubPath/   (api: .../$SubPath/api.php?platform=x64)"
