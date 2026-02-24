@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  Axon – Windows Desktop Build & Publish Script
::  Usage:  build\build-windows.cmd [Release|Debug]
::  Default: Release (Native AOT)
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

:: ── 1. Restore ────────────────────────────────────────────────────────────────
echo.
echo [STEP 1/4] Restoring NuGet packages...
dotnet restore "%PROJECT%" --runtime %RID%
if errorlevel 1 (
    echo [ERROR] Restore failed.
    exit /b 1
)
echo [INFO] Restore complete.

:: ── 2. Build ──────────────────────────────────────────────────────────────────
echo.
echo [STEP 2/4] Building solution (TreatWarningsAsErrors)...
dotnet build Axon.sln -c %CONFIG% /p:TreatWarningsAsErrors=true --no-restore
if errorlevel 1 (
    echo [ERROR] Build failed. Fix warnings/errors above.
    exit /b 1
)
echo [INFO] Build succeeded.

:: ── 3. Test ───────────────────────────────────────────────────────────────────
echo.
echo [STEP 3/4] Running test suite...
dotnet test Axon.sln -c %CONFIG% --no-build --logger "console;verbosity=minimal"
if errorlevel 1 (
    echo [ERROR] Tests failed. Do not publish a broken build.
    exit /b 1
)
echo [INFO] All tests passed.

:: ── 4. Native AOT Publish ─────────────────────────────────────────────────────
echo.
echo [STEP 4/4] Publishing Native AOT binary to: %PUBLISH_DIR%
dotnet publish "%PROJECT%" ^
    -r %RID% ^
    -c %CONFIG% ^
    /p:PublishAot=true ^
    /p:StripSymbols=true ^
    --no-restore
if errorlevel 1 (
    echo [ERROR] AOT publish failed.
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
