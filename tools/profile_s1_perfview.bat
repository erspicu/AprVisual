@echo off
REM ============================================================================
REM  S1 (C#) PerfView profiling  —  RUN AS ADMINISTRATOR
REM  (right-click -> Run as administrator, or double-click: it self-elevates)
REM
REM  Paced into two steps so you always see progress (no silent black window):
REM    STEP 1 (fast ~5-30s, slow only on PerfView's first unpack):
REM           list the HW CPU counters this machine exposes -> decides if real
REM           i-cache-miss PMU is even obtainable here. Printed on screen + saved.
REM    STEP 2 (optional, ~30-60s incl. symbol merge; press a key to start, or
REM           just close the window to skip): a CPU-sampling profile (.etl) you
REM           open in the PerfView GUI -> "CPU Stacks".
REM  Output dir: C:\ai_project\AprVisual\temp\perf\
REM ============================================================================

setlocal EnableExtensions
set "ROOT=C:\ai_project\AprVisual"
set "PV=%ROOT%\tools\perfview\PerfView.exe"
set "DLL=%ROOT%\src\AprVisual.S1\bin\Release\net10.0\AprVisual.S1.dll"
set "BENCHDIR=%ROOT%\AprVisualBenchMark"
set "OUT=%ROOT%\temp\perf"
set "HC=1000000"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Not elevated. Requesting administrator privileges...
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
if not exist "%PV%"  ( echo [ERROR] PerfView missing: "%PV%" & pause & exit /b 1 )
if not exist "%DLL%" ( echo [ERROR] S1 Release build missing: "%DLL%" & pause & exit /b 1 )
if not exist "%OUT%" mkdir "%OUT%"

echo ===========================================================
echo  Running ELEVATED.  Output: %OUT%
echo ===========================================================
echo.
echo [STEP 1] Listing CPU performance counters...
echo          (first PerfView launch unpacks itself -- can take ~20s; please wait)
"%PV%" -AcceptEula -LogFile:"%OUT%\listcounters.txt" ListCpuCounters >nul 2>&1
echo.
echo ----- listcounters.txt (this is the decisive bit) -----
type "%OUT%\listcounters.txt"
echo -------------------------------------------------------
echo  ^^ This machine DOES expose PMC (incl. IcacheMisses / DcacheMisses /
echo     BranchMispredictions), so STEP 2 below can capture real i-cache misses.
echo.
echo [STEP 2] CPU-sampling + HARDWARE COUNTERS profile of the S1 bench
echo          (%HC% hc, ~30-60s incl. symbol merge -- it will look idle while
echo          merging, that's normal). Captures IcacheMisses / DcacheMisses /
echo          BranchMispredictions attributed per method.
echo          Press a key to run it, or close this window to skip.
pause >nul

cd /d "%BENCHDIR%"
echo Collecting (i-cache + d-cache + branch-mispredict PMC + CPU stacks)... please wait.
"%PV%" -AcceptEula -NoGui -ThreadTime -CpuCounters:"IcacheMisses:65536,DcacheMisses:65536,BranchMispredictions:65536" -DataFile:"%OUT%\s1_perf.etl" run "dotnet %DLL% --benchmark roms\full_palette.nes --bench-hc %HC%"

echo.
echo ===========================================================
echo  DONE.
echo   - Counters : %OUT%\listcounters.txt
echo   - Profile  : %OUT%\s1_perf.etl.zip   (double-click -> PerfView opens)
echo.
echo  In PerfView, the left tree under this trace will have several stack views:
echo    * "CPU Stacks"             - time (where CPU time goes)
echo    * "IcacheMisses Stacks"    - L1 instruction-cache misses  <-- your question
echo    * "DcacheMisses Stacks"    - L1 data-cache misses (expect this to dominate)
echo    * "BranchMispredictions Stacks"
echo  Open each, pick the dotnet process. Expectation: IcacheMisses is tiny vs
echo  DcacheMisses (hot loop is one 4.6KB method that fits the 32KB L1i; the cost
echo  is data/memory latency, not instruction fetch).
echo  When done, paste me what the "IcacheMisses Stacks" / "DcacheMisses Stacks"
echo  totals show and I'll fold the real numbers into MD/S1/02.
echo ===========================================================
pause
