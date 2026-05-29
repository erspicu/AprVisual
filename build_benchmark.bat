@echo off
REM ============================================================================
REM  build_benchmark.bat
REM  Build both the C# (AprVisual.S1) and Rust (wire_s1) S1 forks in Release,
REM  then stage the binaries + runtime data into AprVisualBenchMark\.
REM
REM  C# is published SELF-CONTAINED single-file (bundles the .NET 10 runtime),
REM  so AprVisualBenchMark runs on machines WITHOUT any .NET runtime installed.
REM  Rust is a native exe (no runtime needed).
REM
REM  Output layout (C:\ai_project\AprVisual\AprVisualBenchMark):
REM    csharp\          AprVisual.S1.exe (self-contained, ~73 MB) + pdb
REM    rust\            Rust wire_s1.exe (native)
REM    data\system-def\ .js module defs (C# --system-def-dir)
REM    snapshot\        .aprsnap snapshots (Rust bench input)
REM    roms\            test ROM(s) (C# --benchmark input)
REM    run_csharp.bat   convenience: run C# bench
REM    run_rust.bat     convenience: run Rust bench
REM ============================================================================
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "OUT=%ROOT%\AprVisualBenchMark"

echo.
echo === AprVisual S1 benchmark build ===
echo ROOT = %ROOT%
echo OUT  = %OUT%
echo.

REM ---------------------------------------------------------------------------
REM 1. Publish C# (AprVisual.S1) Release - SELF-CONTAINED single file.
REM    Bundles the .NET 10 runtime into the exe so it runs WITHOUT any
REM    installed .NET runtime on the target machine.
REM ---------------------------------------------------------------------------
echo [1/4] Publishing C# AprVisual.S1 (Release, self-contained win-x64) ...
dotnet publish "%ROOT%\src\AprVisual.S1\AprVisual.S1.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo
if errorlevel 1 (
    echo ERROR: C# publish failed.
    exit /b 1
)

set "CS_BIN=%ROOT%\src\AprVisual.S1\bin\Release\net10.0\win-x64\publish"
if not exist "%CS_BIN%\AprVisual.S1.exe" set "CS_BIN=%ROOT%\src\AprVisual.S1\bin\x64\Release\net10.0\win-x64\publish"
if not exist "%CS_BIN%\AprVisual.S1.exe" (
    echo ERROR: could not locate published AprVisual.S1.exe.
    exit /b 1
)
echo     C# publish: %CS_BIN%

REM ---------------------------------------------------------------------------
REM 2. Build Rust (wire_s1) Release
REM ---------------------------------------------------------------------------
echo [2/4] Building Rust wire_s1 (Release) ...
pushd "%ROOT%\experiment\rust-s1"
cargo build --release
if errorlevel 1 (
    echo ERROR: Rust build failed.
    popd
    exit /b 1
)
popd

set "RUST_BIN=%ROOT%\experiment\rust-s1\target\release"
if not exist "%RUST_BIN%\wire_s1.exe" (
    echo ERROR: could not locate wire_s1.exe after build.
    exit /b 1
)
echo     Rust bin: %RUST_BIN%

REM ---------------------------------------------------------------------------
REM 3. Stage binaries + runtime data into AprVisualBenchMark
REM ---------------------------------------------------------------------------
echo [3/4] Staging into %OUT% ...

if not exist "%OUT%"               mkdir "%OUT%"
REM wipe stale binaries (e.g. old framework-dependent dll/json) before staging
if exist "%OUT%\csharp"            rmdir /S /Q "%OUT%\csharp"
if exist "%OUT%\rust"              rmdir /S /Q "%OUT%\rust"
mkdir "%OUT%\csharp"
mkdir "%OUT%\rust"
if not exist "%OUT%\data"          mkdir "%OUT%\data"
if not exist "%OUT%\snapshot"      mkdir "%OUT%\snapshot"
if not exist "%OUT%\roms"          mkdir "%OUT%\roms"
if not exist "%OUT%\screenshots"        mkdir "%OUT%\screenshots"
if not exist "%OUT%\screenshots\csharp" mkdir "%OUT%\screenshots\csharp"
if not exist "%OUT%\screenshots\rust"   mkdir "%OUT%\screenshots\rust"

REM C# self-contained publish output (single-file exe bundles the runtime;
REM copy the whole publish dir in case any side files exist).
xcopy /Y /I /E /Q "%CS_BIN%" "%OUT%\csharp" >nul

REM Rust binary
copy /Y "%RUST_BIN%\wire_s1.exe"                  "%OUT%\rust\" >nul

REM Runtime data: system-def (.js) for C#
xcopy /Y /I /E /Q "%ROOT%\ref\metalnes-main\data\system-def" "%OUT%\data\system-def" >nul

REM Runtime data: snapshots for Rust
copy /Y "%ROOT%\experiment\rust-poc\snapshot\*.aprsnap" "%OUT%\snapshot\" >nul

REM Test ROM for C# benchmark
copy /Y "%ROOT%\nes-test-roms-master\choose\full_palette.nes" "%OUT%\roms\" >nul

REM ---------------------------------------------------------------------------
REM 4. Write convenience runner scripts
REM ---------------------------------------------------------------------------
echo [4/4] Writing run_*.bat / shot_*.bat ...

> "%OUT%\run_csharp.bat" (
    echo @echo off
    echo REM C# S1 benchmark. Arg 1 = hc count ^(default 200000^).
    echo setlocal
    echo set "HC=%%~1"
    echo if "%%HC%%"=="" set "HC=200000"
    echo "%%~dp0csharp\AprVisual.S1.exe" --benchmark "%%~dp0roms\full_palette.nes" --bench-hc %%HC%% --system-def-dir "%%~dp0data\system-def"
    echo pause
)

> "%OUT%\run_rust.bat" (
    echo @echo off
    echo REM Rust S1 benchmark. Arg 1 = hc count ^(default 200000^).
    echo setlocal
    echo set "HC=%%~1"
    echo if "%%HC%%"=="" set "HC=200000"
    echo "%%~dp0rust\wire_s1.exe" bench "%%~dp0snapshot\full_palette.aprsnap" %%HC%%
    echo pause
)

REM C# frame-dump: per-frame PNG with progress + timing. Arg 1 = frame count (default 50).
> "%OUT%\shot_csharp.bat" (
    echo @echo off
    echo REM C# S1 frame-dump ^(full_palette^). Arg 1 = frame count ^(default 50^).
    echo setlocal
    echo set "N=%%~1"
    echo if "%%N%%"=="" set "N=50"
    echo "%%~dp0csharp\AprVisual.S1.exe" --frame-dump "%%~dp0roms\full_palette.nes" --frame-count %%N%% --out-dir "%%~dp0screenshots\csharp" --system-def-dir "%%~dp0data\system-def"
    echo pause
)

REM Rust frame-dump: per-frame PNG with progress + timing. Arg 1 = frame count (default 50).
> "%OUT%\shot_rust.bat" (
    echo @echo off
    echo REM Rust S1 frame-dump ^(full_palette^). Arg 1 = frame count ^(default 50^).
    echo setlocal
    echo set "N=%%~1"
    echo if "%%N%%"=="" set "N=50"
    echo "%%~dp0rust\wire_s1.exe" framedump "%%~dp0snapshot\full_palette.aprsnap" %%N%% "%%~dp0screenshots\rust"
    echo pause
)

echo.
echo === DONE ===
echo Staged to: %OUT%
echo   C#   : %OUT%\csharp\AprVisual.S1.exe
echo   Rust : %OUT%\rust\wire_s1.exe
echo Benchmarks:   AprVisualBenchMark\run_csharp.bat 200000
echo               AprVisualBenchMark\run_rust.bat   200000
echo Frame dumps:  AprVisualBenchMark\shot_csharp.bat 50   ^(-^> screenshots\csharp^)
echo               AprVisualBenchMark\shot_rust.bat   50   ^(-^> screenshots\rust^)
echo.

endlocal
