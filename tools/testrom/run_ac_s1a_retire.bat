@echo off
REM ============================================================================
REM  S1A shim-RETIREMENT proof - AccuracyCoin 141 unattended, mechanisms ON.
REM
REM  This is the in-suite (stage-acceptance) run that the isolated three-arm
REM  protocol CANNOT do: it proves whether the M4_EDGE + M6X mechanisms can
REM  REPLACE their five shims across the whole 141-test suite (not just each
REM  shim's isolated defender). M4_EDGE=1 auto-supersedes DmcLatch + AluLatch;
REM  M6X=1 auto-supersedes even_odd + dot-339 + BGSerialIn (TestRunner.Test.cs
REM  L162-189). All OTHER shims stay armed. A 141/141 here == those 5 shims are
REM  retirable; a drop -> the per-sub-test table names the insufficient mechanism.
REM
REM  Recipe = the banked AC recipe (run_accuracycoin.bat) on the S1A engine, plus
REM  the two mechanism envs. ALEREAD_MUX is still REQUIRED (ALERead test).
REM  ~8 HOURS. Snapshots every 10 frames -> --resume after a crash.
REM
REM  Arg 1 (optional) = logical core to pin (default 8 = physical 4, free while
REM  the 6-lane isolated re-verification uses cores 2,6,10,14,4,12).
REM ============================================================================
setlocal
cd /d "%~dp0..\.."

set PIN=%1
if "%PIN%"=="" set PIN=8

set EXE=src\AprVisual.S1A\bin\Release\net11.0\AprVisual.S1A.exe
set SDD=AprVisualBenchMark\data\system-def
set OUT=tools\testrom\out\ac_s1a_retire
set SNAP=temp\ac_s1a_retire_snap

REM --- banked AC recipe (see MD/memory/00) ---
set ALEREAD_MUX=1
set MUX_HC=13,13,25,44,52
REM --- the two mechanisms under test (auto-disable their 5 shims) ---
set M4_EDGE=1
set M6X=1

if not exist "%EXE%" ( echo Build S1A first: dotnet build src\AprVisual.S1A -c Release & exit /b 1 )
if not exist "%SNAP%" mkdir "%SNAP%"
if not exist "%OUT%"  mkdir "%OUT%"

echo ============================================================
echo   S1A retirement proof (M4_EDGE + M6X)  -^>  pin core %PIN%  (~8h)
echo   auto-retires: DmcLatch AluLatch even_odd dot-339 BGSerialIn
echo   ALEREAD_MUX=%ALEREAD_MUX%  M4_EDGE=%M4_EDGE%  M6X=%M6X%
echo   verdict -^> %OUT%\AccuracyCoin.json   (expect 141/141)
echo ============================================================

"%EXE%" --test AprAccuracyCoinUnattended\AccuracyCoin.nes --ac-verdict --joypad ^
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin %PIN% ^
  --system-def-dir "%SDD%" --max-frames 12000 ^
  --snapshot-frames 10 --snapshot-dir "%SNAP%" ^
  --progress-frames 600 --progress-dir "%OUT%" ^
  --test-json "%OUT%\AccuracyCoin.json" --test-screenshot "%OUT%\AccuracyCoin.png"

echo. & echo === verdict: === & type "%OUT%\AccuracyCoin.json"
