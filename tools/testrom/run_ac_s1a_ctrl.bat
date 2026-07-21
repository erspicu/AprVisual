@echo off
REM ============================================================================
REM  S1A shim CONTROL arm - AccuracyCoin 141, ONE shim turned OFF, baseline else.
REM
REM  This is the CONTROL side of the three-arm: base (certified 141/141, all shims
REM  default) / CONTROL (this: exactly one shim off) / mech (the milestone run).
REM  A sub-test that FAILs here but passes in base == that shim is load-bearing
REM  in-suite (decidable). Baseline = the banked run_accuracycoin.bat recipe with
REM  NO mechanism envs (M6X/M4_EDGE/... all OFF) -- only ALEREAD_MUX, as run8.
REM
REM  *** RECORDS ARE FULLY SEPARATED PER ARM ***  Each named arm writes to its own
REM  out\ac_ctrl_<name>\ and temp\ac_ctrl_<name>_snap\ -- no shared file with the
REM  milestone (out\ac_s1a_milestone) or with the other ctrl arms. No cross-run
REM  snapshot reuse (each starts fresh at frame 0 in its OWN config).
REM
REM  Usage:  run_ac_s1a_ctrl.bat <name> <core>
REM    name = dot339   -> --ppu-write-delay-global 0   (dot-339 global delay OFF)
REM    name = oamedge  -> NO_OAMEDGE_SHIM=1            (OamBlankEdge shim OFF)
REM    name = ppualefb -> --no-ppu-ale-read-feedback-shim (PPU ALE/read feedback OFF)
REM  ~8 HOURS (runs the full suite so an unknown-frame defender is still covered).
REM ============================================================================
setlocal
cd /d "%~dp0..\.."

set NAME=%1
set PIN=%2
if "%NAME%"=="" ( echo need a name: dot339 ^| oamedge ^| ppualefb & exit /b 1 )
if "%PIN%"==""  set PIN=6

set EXE=src\AprVisual.S1A\bin\Release\net11.0\AprVisual.S1A.exe
set SDD=AprVisualBenchMark\data\system-def
set OUT=tools\testrom\out\ac_ctrl_%NAME%
set SNAP=temp\ac_ctrl_%NAME%_snap

REM --- banked AC recipe (MD/memory/00) -- NO mechanism envs (baseline = shims) ---
set ALEREAD_MUX=1
set MUX_HC=13,13,25,44,52

REM --- per-arm: turn OFF exactly one shim ---
set EXTRA=
set OFFDESC=?
if /i "%NAME%"=="dot339"   ( set EXTRA=--ppu-write-delay-global 0 & set OFFDESC=dot-339 global write-delay OFF )
if /i "%NAME%"=="ppualefb" ( set EXTRA=--no-ppu-ale-read-feedback-shim & set OFFDESC=PPU ALE/read feedback OFF )
if /i "%NAME%"=="oamedge"  ( set NO_OAMEDGE_SHIM=1 & set OFFDESC=OamBlankEdge shim OFF ^(env NO_OAMEDGE_SHIM^) )

if not exist "%EXE%" ( echo Build S1A first & exit /b 1 )
if not exist "%SNAP%" mkdir "%SNAP%"
if not exist "%OUT%"  mkdir "%OUT%"

REM --- crash/reboot recovery: auto-resume from the NEWEST snapshot in THIS arm's own
REM     %SNAP% (isolated per arm) if one exists. Same config (this bat + name) ->
REM     fingerprint matches; a torn newest is caught by CRC. Fresh run: clear %SNAP%.
set RESUME=
for /f "delims=" %%F in ('dir /b /o-n "%SNAP%\state_*.sav" 2^>nul') do if not defined RESUME set RESUME=--resume "%SNAP%\%%F"

echo ============================================================
echo   S1A CONTROL arm [%NAME%]  -^>  core %PIN%  (~8h)
echo   %OFFDESC%   (everything else = certified baseline)
echo   records:  %OUT%\   +   %SNAP%\   (isolated)
if defined RESUME echo   ** RESUMING from newest snapshot: %RESUME% **
echo   verdict -^> %OUT%\AccuracyCoin.json
echo ============================================================

"%EXE%" --test AprAccuracyCoinUnattended\AccuracyCoin.nes --ac-verdict --joypad ^
  --reset-hold-extra 1 --pin %PIN% %EXTRA% %RESUME% ^
  --system-def-dir "%SDD%" --max-frames 12000 ^
  --snapshot-frames 10 --snapshot-dir "%SNAP%" ^
  --progress-frames 600 --progress-dir "%OUT%" ^
  --test-json "%OUT%\AccuracyCoin.json" --test-screenshot "%OUT%\AccuracyCoin.png"

echo. & echo === verdict [%NAME%]: === & type "%OUT%\AccuracyCoin.json"
