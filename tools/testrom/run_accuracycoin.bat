@echo off
REM ============================================================================
REM  AprVisual.S1 - one-click AccuracyCoin 141-test unattended run.
REM
REM  AccuracyCoin has its OWN verdict path (NOT the 147-ROM run_tests.py flow):
REM  the ROM writes a completion block to CPU RAM $07F0 (magic DE B0 61 +
REM  passed/total/skipped). This script bakes in the VERIFIED banked recipe so
REM  nobody hand-writes it and forgets a flag. The single most-missed one is the
REM  ALEREAD_MUX env below: without it the ALERead test FAILs -> 140/141, not
REM  141/141. Full recipe reference: MD/memory/00_baseline-and-run-recipes.md.
REM
REM  This takes ~8 HOURS (verdict ~frame 4870, ~6 s/frame switch-level). Ctrl+C
REM  to abort. Snapshots every 10 frames allow --resume after a crash.
REM
REM  Arg 1 (optional) = logical core to pin (default 8 = physical core 4, which
REM  the 147 sweep leaves free so both can run at once).
REM ============================================================================
setlocal
cd /d "%~dp0..\.."

set PIN=%1
if "%PIN%"=="" set PIN=8

set EXE=src\AprVisual.S1\bin\Release\net11.0\AprVisual.S1.exe
set SDD=AprVisualBenchMark\data\system-def
set OUT=tools\testrom\out\ac
set SNAP=temp\ac141_snap

REM --- ALERead mux (REQUIRED for 141/141): env-gated opt-in, read BEFORE ---
REM --- LoadSystem because it cuts ppu.io_ab<->cpu.ab. MUX_HC defaults match ---
REM --- the code default; set for parity with banked run8. -------------------
set ALEREAD_MUX=1
set MUX_HC=13,13,25,44,52

if not exist "%EXE%" (
  echo Engine exe not found: %EXE%
  echo Build first:  dotnet build src\AprVisual.S1 -c Release
  exit /b 1
)
if not exist "%SNAP%" mkdir "%SNAP%"
if not exist "%OUT%"  mkdir "%OUT%"

echo ============================================================
echo   AccuracyCoin 141 unattended  -^>  pin core %PIN%  (~8 hours)
echo   ALEREAD_MUX=%ALEREAD_MUX%  MUX_HC=%MUX_HC%
echo   verdict -^> %OUT%\AccuracyCoin.json  ($07F0 completion block)
echo   mid-run: python tools\testrom\ac_snap_results.py --dir %SNAP%
echo ============================================================
echo.

"%EXE%" --test AprAccuracyCoinUnattended\AccuracyCoin.nes --ac-verdict --joypad ^
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin %PIN% ^
  --system-def-dir "%SDD%" --max-frames 12000 ^
  --snapshot-frames 10 --snapshot-dir "%SNAP%" ^
  --progress-frames 600 --progress-dir "%OUT%" ^
  --test-json "%OUT%\AccuracyCoin.json" --test-screenshot "%OUT%\AccuracyCoin.png"

echo.
echo === done. verdict block: ===
type "%OUT%\AccuracyCoin.json"
echo.
echo (Tip: mail progress every 600 frames with
echo   python tools\testrom\ac_watch.py --dir %OUT% --pid ^<enginePID^> --every-frames 600 )
pause
