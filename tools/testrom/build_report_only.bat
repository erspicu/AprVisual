@echo off
REM ============================================================================
REM  Rebuild only the report page (WebSite\Report\) from the existing out\
REM  results. Does NOT re-run any tests -- handy after a run, or to preview the
REM  page from a partial set of results.
REM ============================================================================
setlocal
cd /d "%~dp0..\.."
python tools\testrom\run_tests.py --report-only
echo.
echo === report rebuilt. Open WebSite\Report\index.html ===
pause
