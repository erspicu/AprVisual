@echo off
REM ============================================================================
REM  S1A shim-RETIREMENT proof (MILESTONE, all mechanisms) - AccuracyCoin 141.
REM
REM  The in-suite (stage-acceptance) run the isolated three-arm protocol CANNOT
REM  do: it proves whether ALL of the built mechanisms can REPLACE their shims
REM  across the whole 141-test suite at once (not just each shim's isolated
REM  defender). Each mechanism was already verified hc-bit-identical to its shim
REM  on the isolated defender; this run is the broad-regression default-flip.
REM
REM  Mechanisms armed (each auto-supersedes its shim, TestRunner.Test.cs L162-206):
REM    M4_EDGE   -> DmcLatch + AluLatch            (PROVEN)
REM    M6X       -> even_odd + BGSerialIn + dot-339 (even_odd/BGSerialIn PROVEN; dot-339 mech-arm here)
REM    M4_P1     -> Dbl2007 + OamDmaPpuBus          (PROVEN)
REM    M1_LXA    -> LXA magic                       (PROVEN)
REM    M4_FI     -> FrameIrq                        (PROVEN)
REM    M4_OE     -> OamBlankEdge                    (mech-arm here)
REM    M3_ABORT  -> Dmc4015Abort                    (mech-arm here)
REM    PPU_ALE_FB-> PpuAleReadFeedback              (mech-arm here)
REM  All OTHER shims stay armed (DL, OpenBus/M5e, ...). 141/141 == every one of
REM  the above is retirable in-suite; a drop -> the per-sub-test table names the
REM  insufficient mechanism.
REM
REM  Recipe = the banked AC recipe (MD/memory/00) + all mechanism envs.
REM  ALEREAD_MUX is REQUIRED (ALERead test). ~8 HOURS. Snapshots every 10 frames.
REM
REM  Arg 1 (optional) = logical core to pin (default 8 = physical 4).
REM ============================================================================
setlocal
cd /d "%~dp0..\.."

set PIN=%1
if "%PIN%"=="" set PIN=8

set EXE=src\AprVisual.S1A\bin\Release\net11.0\AprVisual.S1A.exe
set SDD=AprVisualBenchMark\data\system-def
set OUT=tools\testrom\out\ac_s1a_milestone
set SNAP=temp\ac_s1a_milestone_snap

REM --- banked AC recipe (see MD/memory/00) ---
set ALEREAD_MUX=1
set MUX_HC=13,13,25,44,52
REM --- every built mechanism (each auto-disables its shim) ---
set M4_EDGE=1
set M6X=1
set M4_P1=1
set M1_LXA=1
set M4_FI=1
set M4_OE=1
set M3_ABORT=1
set PPU_ALE_FB=1

if not exist "%EXE%" ( echo Build S1A first: dotnet build src\AprVisual.S1A -c Release & exit /b 1 )
if not exist "%SNAP%" mkdir "%SNAP%"
if not exist "%OUT%"  mkdir "%OUT%"

echo ============================================================
echo   S1A MILESTONE retirement proof (all mechanisms) -^> core %PIN%  (~8h)
echo   M4_EDGE M6X M4_P1 M1_LXA M4_FI M4_OE M3_ABORT PPU_ALE_FB
echo   verdict -^> %OUT%\AccuracyCoin.json   (expect 141/141)
echo ============================================================

"%EXE%" --test AprAccuracyCoinUnattended\AccuracyCoin.nes --ac-verdict --joypad ^
  --callback-drain-limit 2000 --reset-hold-extra 1 --pin %PIN% ^
  --system-def-dir "%SDD%" --max-frames 12000 ^
  --snapshot-frames 10 --snapshot-dir "%SNAP%" ^
  --progress-frames 600 --progress-dir "%OUT%" ^
  --test-json "%OUT%\AccuracyCoin.json" --test-screenshot "%OUT%\AccuracyCoin.png"

echo. & echo === verdict: === & type "%OUT%\AccuracyCoin.json"
