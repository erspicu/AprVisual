@echo off
REM ============================================================================
REM  ONE-COMMAND crash/reboot recovery for the 2026-07-19 4-engine batch.
REM  Relaunches all four runs in parallel; each launcher auto-detects the NEWEST
REM  snapshot in its OWN isolated %SNAP% and --resume's from it (same config ->
REM  fingerprint matches; torn newest -> CRC error, delete it and re-run to fall
REM  back one). Cores unchanged: milestone=8, dot339=6, oamedge=10, ppualefb=12.
REM
REM  Usage:  resume_all_4engine.bat
REM  (Each opens its own console window. To resume just one, run its launcher
REM   directly, e.g.  run_ac_s1a_ctrl.bat dot339 6 )
REM ============================================================================
cd /d "%~dp0..\.."

start "milestone"      cmd /c "tools\testrom\run_ac_s1a_milestone.bat 8"
start "ctrl-dot339"    cmd /c "tools\testrom\run_ac_s1a_ctrl.bat dot339 6"
start "ctrl-oamedge"   cmd /c "tools\testrom\run_ac_s1a_ctrl.bat oamedge 10"
start "ctrl-ppualefb"  cmd /c "tools\testrom\run_ac_s1a_ctrl.bat ppualefb 12"

echo Relaunched all 4 engines (each auto-resumes from its newest snapshot).
echo Milestone=core8  dot339=core6  oamedge=core10  ppualefb=core12
