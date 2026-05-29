@echo off
REM ============================================================================
REM  build_benchmark.bat
REM  Build the AprVisual S1 forks (C# + Rust) and assemble a PORTABLE, cross-
REM  platform benchmark package in AprVisualBenchMark\ that you can zip and ship.
REM
REM  C# is published SELF-CONTAINED + TRIMMED single-file for BOTH win-x64 and
REM  osx-arm64 (bundles the .NET 10 runtime; no install needed on either OS).
REM  Rust win-x64 is built natively here; Rust macOS can't be cross-compiled
REM  from Windows, so its SOURCE is bundled and built on first ./run_rust.sh.
REM
REM  Output layout (C:\ai_project\AprVisual\AprVisualBenchMark):
REM    win\   AprVisual.S1.exe (C#)   wire_s1.exe (Rust)
REM    mac\   AprVisual.S1     (C#)   wire_s1 (Rust, built on first run)
REM    rust-src\               portable Rust source (Cargo.toml + src)
REM    data\system-def\        netlist .js modules (C# input)
REM    snapshot\               .aprsnap snapshots (Rust input)
REM    roms\                   test ROM(s) (C# input)
REM    screenshots\            frame-dump PNG output
REM    run_*.bat shot_*.bat    Windows runners (pause at end)
REM    run_*.sh  shot_*.sh     macOS runners (read pause at end)
REM    README.txt readme.html  docs (readme.html has EN/ZH toggle)
REM ============================================================================
setlocal enabledelayedexpansion

set "ROOT=%~dp0"
if "%ROOT:~-1%"=="\" set "ROOT=%ROOT:~0,-1%"
set "OUT=%ROOT%\AprVisualBenchMark"
set "TPL=%ROOT%\tools\mac-benchmark"

echo.
echo === AprVisual S1 portable benchmark package ===
echo ROOT = %ROOT%
echo OUT  = %OUT%
echo.

REM ---------------------------------------------------------------------------
REM 1. Publish C# win-x64 (self-contained + trimmed single file)
REM ---------------------------------------------------------------------------
echo [1/6] Publishing C# win-x64 (self-contained + trimmed) ...
dotnet publish "%ROOT%\src\AprVisual.S1\AprVisual.S1.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo
if errorlevel 1 ( echo ERROR: C# win-x64 publish failed. & exit /b 1 )
set "CS_WIN=%ROOT%\src\AprVisual.S1\bin\Release\net10.0\win-x64\publish"
if not exist "%CS_WIN%\AprVisual.S1.exe" ( echo ERROR: win-x64 exe not found. & exit /b 1 )

REM ---------------------------------------------------------------------------
REM 2. Publish C# osx-arm64 (self-contained + trimmed single file)
REM    PlatformTarget override needed (csproj pins x64).
REM ---------------------------------------------------------------------------
echo [2/6] Publishing C# osx-arm64 (self-contained + trimmed) ...
dotnet publish "%ROOT%\src\AprVisual.S1\AprVisual.S1.csproj" -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PlatformTarget=arm64 --nologo
if errorlevel 1 ( echo ERROR: C# osx-arm64 publish failed. & exit /b 1 )
set "CS_MAC=%ROOT%\src\AprVisual.S1\bin\Release\net10.0\osx-arm64\publish"
if not exist "%CS_MAC%\AprVisual.S1" ( echo ERROR: osx-arm64 binary not found. & exit /b 1 )

REM ---------------------------------------------------------------------------
REM 3. Build Rust win-x64 (native)
REM ---------------------------------------------------------------------------
echo [3/6] Building Rust win-x64 (Release) ...
pushd "%ROOT%\experiment\rust-s1"
cargo build --release
if errorlevel 1 ( echo ERROR: Rust build failed. & popd & exit /b 1 )
popd
set "RUST_WIN=%ROOT%\experiment\rust-s1\target\release"
if not exist "%RUST_WIN%\wire_s1.exe" ( echo ERROR: wire_s1.exe not found. & exit /b 1 )

REM ---------------------------------------------------------------------------
REM 4. (Re)create the package tree
REM ---------------------------------------------------------------------------
echo [4/6] Staging package tree ...
if exist "%OUT%\win"       rmdir /S /Q "%OUT%\win"
if exist "%OUT%\mac"       rmdir /S /Q "%OUT%\mac"
if exist "%OUT%\rust-src"  rmdir /S /Q "%OUT%\rust-src"
if not exist "%OUT%"                    mkdir "%OUT%"
mkdir "%OUT%\win"
mkdir "%OUT%\mac"
mkdir "%OUT%\rust-src"
if not exist "%OUT%\data"               mkdir "%OUT%\data"
if not exist "%OUT%\snapshot"           mkdir "%OUT%\snapshot"
if not exist "%OUT%\roms"               mkdir "%OUT%\roms"
if not exist "%OUT%\screenshots"        mkdir "%OUT%\screenshots"
if not exist "%OUT%\screenshots\csharp" mkdir "%OUT%\screenshots\csharp"
if not exist "%OUT%\screenshots\rust"   mkdir "%OUT%\screenshots\rust"

REM Binaries
copy /Y "%CS_WIN%\AprVisual.S1.exe" "%OUT%\win\" >nul
copy /Y "%RUST_WIN%\wire_s1.exe"    "%OUT%\win\" >nul
copy /Y "%CS_MAC%\AprVisual.S1"     "%OUT%\mac\" >nul

REM Portable Rust source (Cargo.toml + src/*.rs only; no target/)
copy /Y "%ROOT%\experiment\rust-s1\Cargo.toml" "%OUT%\rust-src\" >nul
if not exist "%OUT%\rust-src\src" mkdir "%OUT%\rust-src\src"
copy /Y "%ROOT%\experiment\rust-s1\src\*.rs"    "%OUT%\rust-src\src\" >nul

REM Runtime data
xcopy /Y /I /E /Q "%ROOT%\ref\metalnes-main\data\system-def" "%OUT%\data\system-def" >nul
copy /Y "%ROOT%\experiment\rust-poc\snapshot\*.aprsnap"        "%OUT%\snapshot\" >nul
copy /Y "%ROOT%\nes-test-roms-master\choose\full_palette.nes"  "%OUT%\roms\" >nul

REM macOS scripts + docs (keep their LF endings ? copy preserves bytes)
copy /Y "%TPL%\run_csharp.sh"  "%OUT%\" >nul
copy /Y "%TPL%\run_rust.sh"    "%OUT%\" >nul
copy /Y "%TPL%\shot_csharp.sh" "%OUT%\" >nul
copy /Y "%TPL%\shot_rust.sh"   "%OUT%\" >nul
copy /Y "%TPL%\README.txt"     "%OUT%\" >nul
copy /Y "%TPL%\readme.html"    "%OUT%\" >nul

REM ---------------------------------------------------------------------------
REM 5. Generate Windows runner .bat files (point at win\)
REM ---------------------------------------------------------------------------
echo [5/6] Writing Windows run_*.bat / shot_*.bat ...

> "%OUT%\run_csharp.bat" (
    echo @echo off
    echo setlocal
    echo set "HC=%%~1"
    echo if "%%HC%%"=="" set "HC=300000"
    echo "%%~dp0win\AprVisual.S1.exe" --benchmark "%%~dp0roms\full_palette.nes" --bench-hc %%HC%% --system-def-dir "%%~dp0data\system-def"
    echo pause
)
> "%OUT%\run_rust.bat" (
    echo @echo off
    echo setlocal
    echo set "HC=%%~1"
    echo if "%%HC%%"=="" set "HC=300000"
    echo "%%~dp0win\wire_s1.exe" bench "%%~dp0snapshot\full_palette.aprsnap" %%HC%%
    echo pause
)
> "%OUT%\shot_csharp.bat" (
    echo @echo off
    echo setlocal
    echo set "N=%%~1"
    echo if "%%N%%"=="" set "N=50"
    echo "%%~dp0win\AprVisual.S1.exe" --frame-dump "%%~dp0roms\full_palette.nes" --frame-count %%N%% --out-dir "%%~dp0screenshots\csharp" --system-def-dir "%%~dp0data\system-def"
    echo pause
)
> "%OUT%\shot_rust.bat" (
    echo @echo off
    echo setlocal
    echo set "N=%%~1"
    echo if "%%N%%"=="" set "N=50"
    echo "%%~dp0win\wire_s1.exe" framedump "%%~dp0snapshot\full_palette.aprsnap" %%N%% "%%~dp0screenshots\rust"
    echo pause
)

REM ---------------------------------------------------------------------------
REM 6. Done
REM ---------------------------------------------------------------------------
echo [6/6] Done.
echo.
echo === PACKAGE READY (portable; zip and ship) ===
echo   %OUT%
echo   win\AprVisual.S1.exe  win\wire_s1.exe
echo   mac\AprVisual.S1      (Rust mac built on first ./run_rust.sh)
echo   rust-src\             (portable Rust source)
echo Windows:  run_csharp.bat / run_rust.bat / shot_csharp.bat / shot_rust.bat
echo macOS:    run_csharp.sh  / run_rust.sh  / shot_csharp.sh  / shot_rust.sh
echo Docs:     README.txt, readme.html (EN/ZH)
echo.

endlocal
