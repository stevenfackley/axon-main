#!/usr/bin/env bash
# =============================================================
#  Axon – Android Build & Deploy Script (Linux / WSL2)
#  Usage:  ./build/build-android.sh [Release|Debug] [avd-name]
#  Defaults: Debug  /  axon_dev
#
#  Prerequisites (one-time setup — see build/README.md):
#    • .NET 10 SDK + Android workload:
#        sudo dotnet workload install android
#    • Java 17:
#        sudo apt-get install -y openjdk-17-jdk-headless
#    • Android SDK at /opt/android-sdk (cmdline-tools, platform-tools,
#      build-tools, platforms;android-36, emulator,
#      system-images;android-36;google_apis;x86_64):
#        sudo /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager \
#          "platforms;android-36" "build-tools;36.0.0" \
#          "platform-tools" "emulator" \
#          "system-images;android-36;google_apis;x86_64" \
#          --sdk_root=/opt/android-sdk
#    • AVD:
#        echo no | avdmanager create avd --name axon_dev \
#          --abi google_apis/x86_64 \
#          --package "system-images;android-36;google_apis;x86_64" \
#          --device "pixel_6"
#    • KVM (WSL2 — verify with: ls /dev/kvm)
# =============================================================
set -euo pipefail

CONFIG="${1:-Debug}"
AVD="${2:-axon_dev}"
PROJECT="src/Axon.UI/Axon.UI.csproj"
APK="src/Axon.UI/bin/${CONFIG}/net10.0-android/com.axon.telemetry-Signed.apk"

# ── Source SDK env if not already set ──────────────────────────
if [[ -z "${ANDROID_HOME:-}" ]]; then
    if [[ -f /etc/profile.d/android-sdk.sh ]]; then
        # shellcheck source=/dev/null
        source /etc/profile.d/android-sdk.sh
    else
        echo "[ERROR] ANDROID_HOME is not set. Source /etc/profile.d/android-sdk.sh first."
        exit 1
    fi
fi

ADB="${ANDROID_HOME}/platform-tools/adb"
EMULATOR_BIN="${ANDROID_HOME}/emulator/emulator"

echo "[INFO] Using Android SDK: ${ANDROID_HOME}"
echo "[INFO] Using Java: ${JAVA_HOME:-$(dirname $(dirname $(readlink -f $(which java))))}"

# ── STEP 1: Start emulator if none attached ───────────────────
echo ""
echo "[STEP 1/5] Checking for connected device..."
DEVICE=$(${ADB} devices | grep -v "List" | grep "device$" | awk '{print $1}' || true)

if [[ -n "${DEVICE}" ]]; then
    echo "[INFO] Device already connected: ${DEVICE}"
else
    echo "[INFO] No device found – launching emulator: ${AVD}"
    # Run headless with KVM acceleration and SwiftShader GPU
    "${EMULATOR_BIN}" \
        -avd "${AVD}" \
        -no-window \
        -no-audio \
        -gpu swiftshader_indirect \
        -accel on \
        -no-snapshot \
        -no-boot-anim > /tmp/axon-emulator.log 2>&1 &
    EMU_PID=$!
    echo "[INFO] Emulator PID: ${EMU_PID}  (log: /tmp/axon-emulator.log)"

    echo "[INFO] Waiting for ADB device..."
    "${ADB}" wait-for-device

    echo "[INFO] Waiting for full Android boot (up to 3 min)..."
    BOOT_TIMEOUT=180
    ELAPSED=0
    until [[ "$(${ADB} shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == "1" ]]; do
        sleep 3
        ELAPSED=$((ELAPSED + 3))
        if [[ ${ELAPSED} -ge ${BOOT_TIMEOUT} ]]; then
            echo "[ERROR] Emulator did not boot within ${BOOT_TIMEOUT}s. Check /tmp/axon-emulator.log"
            exit 1
        fi
    done
    echo "[INFO] Emulator booted (${ELAPSED}s)."
fi

# ── STEP 2: Ensure SDK dependencies ──────────────────────────
echo ""
echo "[STEP 2/5] Ensuring Android SDK dependencies..."
dotnet build "${PROJECT}" \
    -f net10.0-android \
    -t:InstallAndroidDependencies \
    -p:AndroidSdkDirectory="${ANDROID_HOME}" \
    -p:AcceptAndroidSDKLicenses=True \
    --nologo -v:q 2>/dev/null || true
echo "[INFO] SDK dependencies checked."

# ── STEP 3: Build ─────────────────────────────────────────────
echo ""
echo "[STEP 3/5] Building Axon.UI (${CONFIG}) for net10.0-android..."
dotnet build "${PROJECT}" -f net10.0-android -c "${CONFIG}" \
    -p:AndroidSdkDirectory="${ANDROID_HOME}"
echo "[INFO] Build succeeded."

# ── STEP 4: Install APK ───────────────────────────────────────
echo ""
echo "[STEP 4/5] Installing APK..."
if [[ ! -f "${APK}" ]]; then
    echo "[ERROR] APK not found: ${APK}"
    exit 1
fi
"${ADB}" install -r "${APK}"
echo "[INFO] APK installed."

# ── STEP 5: Launch ────────────────────────────────────────────
echo ""
echo "[STEP 5/5] Launching Axon on device..."
if "${ADB}" shell monkey -p com.axon.telemetry \
        -c android.intent.category.LAUNCHER 1 &>/dev/null; then
    echo "[INFO] Axon launched."
else
    echo "[WARN] Could not auto-launch. Open 'Axon' from the app drawer manually."
fi

echo ""
echo "============================================================"
echo " Done!  Axon (${CONFIG}) is running on ${AVD}"
echo " Re-run this script any time to rebuild + redeploy."
echo "============================================================"
