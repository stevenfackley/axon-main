@echo off
setlocal EnableDelayedExpansion

:: ============================================================
::  Axon – Android Build & Deploy Script
::  Usage:  build-android.cmd [Release|Debug]  [avd-name]
::  Defaults: Debug  /  Pixel_3a_API_33_x86_64
:: ============================================================

set CONFIG=%~1
if "%CONFIG%"=="" set CONFIG=Debug

set AVD=%~2
if "%AVD%"=="" set AVD=Pixel_3a_API_33_x86_64

:: ── Locate Android SDK ────────────────────────────────────────
if defined ANDROID_HOME goto :sdk_found
if exist "%LOCALAPPDATA%\Android\Sdk" (
    set ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk
    goto :sdk_found
)
echo [ERROR] ANDROID_HOME is not set and default path not found.
echo         Set ANDROID_HOME to your Android SDK root and retry.
exit /b 1
:sdk_found
echo [INFO] Using Android SDK: %ANDROID_HOME%

set ADB=%ANDROID_HOME%\platform-tools\adb.exe
set EMULATOR=%ANDROID_HOME%\emulator\emulator.exe
set APK=src\Axon.UI\bin\%CONFIG%\net9.0-android\com.axon.telemetry-Signed.apk
set PROJECT=src\Axon.UI\Axon.UI.csproj

:: ── 1. Start emulator if no device is attached ───────────────
echo.
echo [STEP 1/5] Checking for connected device...
for /f "tokens=*" %%d in ('"%ADB%" devices ^| findstr /v "List" ^| findstr "device"') do set DEVICE=%%d
if defined DEVICE (
    echo [INFO] Device already connected: %DEVICE%
) else (
    echo [INFO] No device found – launching emulator: %AVD%
    start "" "%EMULATOR%" -avd %AVD% -no-snapshot-load
    echo [INFO] Waiting for emulator to appear on adb...
    :wait_device
    "%ADB%" wait-for-device >nul 2>&1
    :: Wait for full boot
    :wait_boot
    for /f %%b in ('"%ADB%" shell getprop sys.boot_completed 2^>nul') do set BOOTED=%%b
    if not "!BOOTED!"=="1" (
        ping -n 3 127.0.0.1 >nul
        goto :wait_boot
    )
    echo [INFO] Emulator booted.
)

:: ── 2. Ensure Android SDK platforms are installed ─────────────
echo.
echo [STEP 2/5] Ensuring Android SDK dependencies (android.jar, build-tools)...
dotnet build "%PROJECT%" -f net9.0-android -t:InstallAndroidDependencies ^
    -p:AndroidSdkDirectory="%ANDROID_HOME%" ^
    -p:AcceptAndroidSDKLicenses=True ^
    --nologo -v:q
if errorlevel 1 (
    echo [WARN] InstallAndroidDependencies returned non-zero; continuing anyway...
)
echo [INFO] SDK dependencies checked.

:: ── 3. Build ─────────────────────────────────────────────────
echo.
echo [STEP 3/5] Building Axon.UI (%CONFIG%) for net9.0-android...
dotnet publish "%PROJECT%" -f net9.0-android -c %CONFIG%
if errorlevel 1 (
    echo [ERROR] Build failed. See output above.
    exit /b 1
)
echo [INFO] Build succeeded.

:: ── 4. Install APK ───────────────────────────────────────────
echo.
echo [STEP 4/5] Installing APK...
if not exist "%APK%" (
    echo [ERROR] APK not found: %APK%
    exit /b 1
)
"%ADB%" install -r "%APK%"
if errorlevel 1 (
    echo [ERROR] adb install failed.
    exit /b 1
)
echo [INFO] APK installed successfully.

:: ── 5. Launch ────────────────────────────────────────────────
echo.
echo [STEP 5/5] Launching Axon on device...
"%ADB%" shell monkey -p com.axon.telemetry -c android.intent.category.LAUNCHER 1 >nul 2>&1
if errorlevel 1 (
    echo [WARN] Could not auto-launch via monkey. Open 'Axon' from the app drawer manually.
) else (
    echo [INFO] Axon launched.
)

echo.
echo ============================================================
echo  Done!  Axon (%CONFIG%) is running on %AVD%
echo  Re-run this script any time to rebuild + redeploy.
echo ============================================================
endlocal
