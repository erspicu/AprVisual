@echo off
REM ============================================================================
REM  S1 (C#) cache A/B  —  32B NodeInfo  vs  16B NodeInfo   —  RUN AS ADMINISTRATOR
REM  (right-click -> Run as administrator, or double-click: it self-elevates)
REM
REM  Captures BOTH builds back-to-back under the IDENTICAL PerfView config so the
REM  D-cache miss rate is comparable across versions. We add InstructionRetired
REM  so the metric is MPKI (misses / 1000 retired instructions) -- a ratio, so
REM  clock/thermal drift cancels and the two versions are directly comparable.
REM  (3 PMC sources: proven to work here; 4+ can hit Windows' simultaneous cap.)
REM
REM  NOTE: during the merge/zip step PerfView prints benign lines like
REM     "Could not find CLR directory for NGEN image ... Giving up"
REM  -- that is just framework R2R symbol generation; it does NOT affect the
REM  capture or our managed-method resolution. Ignore them.
REM
REM  Output: C:\ai_project\AprVisual\temp\perf\ab_32b.etl.zip  and  ab_16b.etl.zip
REM ============================================================================

setlocal EnableExtensions
set "ROOT=C:\ai_project\AprVisual"
set "PV=%ROOT%\tools\perfview\PerfView.exe"
set "EXE32=%ROOT%\temp\ab\32b\AprVisual.S1.exe"
set "EXE16=%ROOT%\temp\ab\16b\AprVisual.S1.exe"
set "BENCHDIR=%ROOT%\AprVisualBenchMark"
set "ROM=%BENCHDIR%\roms\full_palette.nes"
set "OUT=%ROOT%\temp\perf"
set "HC=1000000"
set "COUNTERS=DcacheMisses:65536,IcacheMisses:65536,InstructionRetired:65536"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Not elevated. Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
if not exist "%PV%"    ( echo [ERROR] PerfView missing: "%PV%" & pause & exit /b 1 )
if not exist "%EXE32%" ( echo [ERROR] 32B build missing: "%EXE32%"  ^(Claude builds both into temp\ab\^) & pause & exit /b 1 )
if not exist "%EXE16%" ( echo [ERROR] 16B build missing: "%EXE16%" & pause & exit /b 1 )
if not exist "%ROM%"   ( echo [ERROR] ROM missing: "%ROM%" & pause & exit /b 1 )
if not exist "%OUT%" mkdir "%OUT%"

cd /d "%BENCHDIR%"
echo ===========================================================
echo  A/B cache capture  (%HC% hc each, --extra-ram)
echo  Counters: %COUNTERS%
echo  Output  : %OUT%
echo ===========================================================
echo.
echo [1/2] Capturing 32B NodeInfo build... (will look idle during merge/zip)
"%PV%" -AcceptEula -NoGui -ThreadTime -CpuCounters:"%COUNTERS%" -DataFile:"%OUT%\ab_32b.etl" run "%EXE32%" --benchmark "%ROM%" --bench-hc %HC% --extra-ram
echo.
echo [2/2] Capturing 16B NodeInfo build...
"%PV%" -AcceptEula -NoGui -ThreadTime -CpuCounters:"%COUNTERS%" -DataFile:"%OUT%\ab_16b.etl" run "%EXE16%" --benchmark "%ROM%" --bench-hc %HC% --extra-ram

echo.
echo ===========================================================
echo  DONE.
echo   - 32B : %OUT%\ab_32b.etl.zip
echo   - 16B : %OUT%\ab_16b.etl.zip
echo  Tell Claude it's done; he'll diff the D-cache MPKI of the two.
echo  (The "Could not find CLR directory ... Giving up" lines above are benign.)
echo ===========================================================
pause
