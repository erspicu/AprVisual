@echo off
REM ============================================================================
REM  S1A default-flip VERIFICATION — AccuracyCoin 141, the RETIRED-shim defaults.
REM  Runs the plain banked AC recipe with NO mechanism env vars, so the eight
REM  mechanisms arm from their new DEFAULT-ON state (the retire-shims-default-flip
REM  branch). A 141/141 here proves the default-flip preserves the AC suite.
REM  ALEREAD_MUX is still REQUIRED (ALERead mux is a separate CALIBRATED mechanism,
REM  not one of the twelve retired shims). ~8 HOURS. Auto-resumes from newest snap.
REM  Arg 1 (optional) = logical core to pin (default 8).
REM ============================================================================
setlocal
cd /d "%~dp0..\.."

set PIN=%1
if "%PIN%"=="" set PIN=8

set EXE=src\AprVisual.S1A\bin\Release\net11.0\AprVisual.S1A.exe
set SDD=AprVisualBenchMark\data\system-def
set OUT=tools\testrom\out\ac_s1a_verify
set SNAP=temp\ac_s1a_verify_snap

REM --- banked AC recipe (MD/memory/00) — ALEREAD_MUX only; NO M4_EDGE/M6X/... ---
set ALEREAD_MUX=1
set MUX_HC=13,13,25,44,52

if not exist "%EXE%" ( echo Build S1A first & exit /b 1 )
if not exist "%SNAP%" mkdir "%SNAP%"
if not exist "%OUT%"  mkdir "%OUT%"

set RESUME=
for /f "delims=" %%F in ('dir /b /o-n "%SNAP%\state_*.sav" 2^>nul') do if not defined RESUME set RESUME=--resume "%SNAP%\%%F"

echo ============================================================
echo   S1A default-flip verify (mechanisms DEFAULT-ON, no env) -^> core %PIN%  (~8h)
if defined RESUME echo   ** RESUMING from newest snapshot: %RESUME% **
echo   verdict -^> %OUT%\AccuracyCoin.json   (expect 141/141)
echo ============================================================

"%EXE%" --test AprAccuracyCoinUnattended\AccuracyCoin.nes --ac-verdict --joypad ^
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin %PIN% %RESUME% ^
  --system-def-dir "%SDD%" --max-frames 12000 ^
  --snapshot-frames 10 --snapshot-dir "%SNAP%" ^
  --progress-frames 600 --progress-dir "%OUT%" ^
  --test-json "%OUT%\AccuracyCoin.json" --test-screenshot "%OUT%\AccuracyCoin.png"

echo. & echo === verdict: === & type "%OUT%\AccuracyCoin.json"
