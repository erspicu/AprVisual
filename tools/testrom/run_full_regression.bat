@echo off
REM ============================================================================
REM  AprVisual.S1 - one-click full test-ROM regression.
REM  Builds the engine, runs every catalog test on 7 core-pinned workers
REM  (longest tests first), then generates the report page at WebSite\Report\.
REM
REM  This takes SEVERAL HOURS (switch-level sim is ~5 s per simulated frame).
REM  Press Ctrl+C to abort. To start from a clean slate, run
REM  archive_old_results.bat first. Extra args pass through to run_tests.py.
REM ============================================================================
setlocal
cd /d "%~dp0..\.."
echo ============================================================
echo   build engine  -^>  run all tests (7 lanes)  -^>  build report
echo   This takes SEVERAL HOURS.  Ctrl+C to abort.
echo ============================================================
echo.
python tools\testrom\run_tests.py %*
echo.
echo === done. Open WebSite\Report\index.html ===
pause
