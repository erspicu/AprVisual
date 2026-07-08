@echo off
REM ============================================================================
REM  Pack the previous run's out\ results/screenshots/logs into a timestamped
REM  archive so the next regression starts from a clean directory (keeps the
REM  report's timing/perf numbers from mixing with an older batch).
REM
REM  Make sure NO runner is currently active before running this.
REM ============================================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0archive_old_results.ps1"
pause
