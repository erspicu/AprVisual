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
echo  ^^ If the list above is empty / has no "I-cache" / "Instruction" / "Refill"
echo     counter, this CPU+OS exposes no PMU via ETW -> i-cache misses are NOT
echo     obtainable here (AMD uProf would be the only route). You can stop now.
echo.
echo [STEP 2] (optional) CPU-sampling profile of the S1 bench (%HC% hc, ~30-60s
echo          incl. symbol merge -- it will look idle while merging, that's normal).
echo          Press a key to run it, or close this window to skip.
pause >nul

cd /d "%BENCHDIR%"
echo Collecting... (please wait, no per-line output during merge)
"%PV%" -AcceptEula -NoGui -ThreadTime -DataFile:"%OUT%\s1_cpu.etl" run "dotnet %DLL% --benchmark roms\full_palette.nes --bench-hc %HC%"

echo.
echo ===========================================================
echo  DONE.
echo   - Counters : %OUT%\listcounters.txt
echo   - Profile  : %OUT%\s1_cpu.etl.zip
echo       Double-click it (PerfView opens) -> "CPU Stacks" -> pick the dotnet process.
echo  NOTE: the whole hot loop is ONE inlined method (ProcessQueueInterp ~4.6KB),
echo  so CPU Stacks will show ~all time there; to split it (fast-path vs group-BFS)
echo  use PerfView's "Goto Source" line view, or see the engine's own --profile
echo  numbers in WebSite/ceiling.html (69.5%% fast-path / 30.5%% group-BFS).
echo ===========================================================
pause
