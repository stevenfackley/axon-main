@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  Axon - Windows Desktop Build & Publish Script
::  Usage:  build\build-windows.cmd [Release|Debug]
::  Keep this file ASCII: the checkout has LF line endings, and cmd.exe
::  mis-reads multi-byte characters in an LF batch file.
::  Default: Release, self-contained with ReadyToRun. Not Native AOT: the
::  on-device models (ML.NET) and EF Core need reflection, which AOT removes.
::
::  Output: src\Axon.UI\bin\Release\net10.0\win-x64\publish\Axon.UI.exe
:: ============================================================

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Release

set PROJECT=src\Axon.UI\Axon.UI.csproj
set RID=win-x64
set PUBLISH_DIR=src\Axon.UI\bin\%CONFIG%\net10.0\%RID%\publish

echo.
echo ============================================================
echo  Axon Desktop Build  ^|  Config: %CONFIG%  ^|  RID: %RID%
echo ============================================================

:: -- 1. Restore ----------------------------------------------------------------
echo.
echo [STEP 1/4] Restoring NuGet packages...
:: A win-x64 restore of the project would also restore its Android target for
:: win-x64, which fails (NU1102). Step 4 restores the desktop target for the RID.
dotnet restore Axon.sln
if errorlevel 1 (
    echo [ERROR] Restore failed.
    exit /b 1
)
echo [INFO] Restore complete.

:: -- 2. Build ------------------------------------------------------------------
echo.
echo [STEP 2/4] Building solution (TreatWarningsAsErrors)...
dotnet build Axon.sln -c %CONFIG% /p:TreatWarningsAsErrors=true --no-restore
if errorlevel 1 (
    echo [ERROR] Build failed. Fix warnings/errors above.
    exit /b 1
)
echo [INFO] Build succeeded.

:: -- 3. Test -------------------------------------------------------------------
echo.
echo [STEP 3/4] Running test suite...
dotnet test Axon.sln -c %CONFIG% --no-build --logger "console;verbosity=minimal"
if errorlevel 1 (
    echo [ERROR] Tests failed. Do not publish a broken build.
    exit /b 1
)
echo [INFO] All tests passed.

:: -- 4. Self-contained publish -----------------------------------------------
:: Desktop target only, its own .NET runtime, ReadyToRun pre-compilation, no
:: trimming (ML.NET and EF Core need the reflection metadata trimming removes).
echo.
echo [STEP 4/4] Publishing self-contained build to: %PUBLISH_DIR%
dotnet publish "%PROJECT%" ^
    -f net10.0 ^
    -p:TargetFrameworks=net10.0 ^
    -r %RID% ^
    -c %CONFIG% ^
    --self-contained true ^
    -p:PublishReadyToRun=true ^
    -p:PublishTrimmed=false
if errorlevel 1 (
    echo [ERROR] Publish failed.
    exit /b 1
)

echo.
echo ============================================================
echo  Done!  Axon Desktop (%CONFIG%) published.
echo  Binary: %PUBLISH_DIR%\Axon.UI.exe
echo.
echo  To package as MSIX for the Windows Store:
echo    msix-package.cmd  (not yet implemented)
echo ============================================================
endlocal
