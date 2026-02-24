#!/usr/bin/env bash
# =============================================================================
#  Axon – iOS Satellite Build & Archive Script
#  Usage:  ./build/build-ios.sh [Release|Debug] [simulator|device]
#  Default: Release device
#
#  Prerequisites (macOS only):
#    • Xcode 16+ with iOS 17 SDK installed
#    • .NET iOS workload:  dotnet workload install ios
#    • Apple Developer account with provisioning profile for com.axon.telemetry
#    • Valid signing certificate in Keychain
#
#  Output (Release device):
#    src/Axon.UI/bin/Release/net10.0-ios/ios-arm64/publish/Axon.UI.ipa
# =============================================================================

set -euo pipefail

CONFIG="${1:-Release}"
TARGET="${2:-device}"   # "device" or "simulator"
PROJECT="src/Axon.UI/Axon.UI.csproj"

# Determine runtime identifier
if [[ "$TARGET" == "simulator" ]]; then
    RID="iossimulator-x64"
    AOT_FLAG=""            # Simulator does not support Native AOT
else
    RID="ios-arm64"
    AOT_FLAG="/p:PublishAot=true"
fi

echo ""
echo "============================================================"
echo " Axon iOS Build  |  Config: $CONFIG  |  Target: $TARGET ($RID)"
echo "============================================================"

# ── 0. Platform check ─────────────────────────────────────────────────────────
if [[ "$(uname)" != "Darwin" ]]; then
    echo "[ERROR] iOS builds require macOS with Xcode installed."
    exit 1
fi

if ! command -v dotnet &>/dev/null; then
    echo "[ERROR] dotnet CLI not found. Install .NET 10 SDK from https://dot.net"
    exit 1
fi

# ── 1. Ensure iOS workload is installed ───────────────────────────────────────
echo ""
echo "[STEP 1/5] Checking .NET iOS workload..."
if ! dotnet workload list | grep -q "^ios"; then
    echo "[INFO] iOS workload not found. Installing..."
    dotnet workload install ios
fi
echo "[INFO] iOS workload present."

# ── 2. Restore ────────────────────────────────────────────────────────────────
echo ""
echo "[STEP 2/5] Restoring NuGet packages..."
dotnet restore "$PROJECT" --runtime "$RID"
echo "[INFO] Restore complete."

# ── 3. Build ──────────────────────────────────────────────────────────────────
echo ""
echo "[STEP 3/5] Building $PROJECT ($CONFIG / $RID)..."
dotnet build "$PROJECT" \
    -f "net10.0-ios" \
    -c "$CONFIG" \
    -r "$RID" \
    /p:TreatWarningsAsErrors=true \
    --no-restore
echo "[INFO] Build succeeded."

# ── 4. Publish / Archive ──────────────────────────────────────────────────────
echo ""
echo "[STEP 4/5] Publishing..."
# shellcheck disable=SC2086
dotnet publish "$PROJECT" \
    -f "net10.0-ios" \
    -c "$CONFIG" \
    -r "$RID" \
    $AOT_FLAG \
    --no-restore
echo "[INFO] Publish complete."

# ── 5. TestFlight upload reminder ─────────────────────────────────────────────
if [[ "$CONFIG" == "Release" && "$TARGET" == "device" ]]; then
    IPA_PATH="src/Axon.UI/bin/$CONFIG/net10.0-ios/$RID/publish/Axon.UI.ipa"
    echo ""
    echo "[STEP 5/5] Checking for IPA..."
    if [[ -f "$IPA_PATH" ]]; then
        echo "[INFO] IPA found: $IPA_PATH"
        echo ""
        echo "  To upload to TestFlight / App Store Connect, run:"
        echo "    xcrun altool --upload-app -f \"$IPA_PATH\" \\"
        echo "                 --type ios \\"
        echo "                 --apiKey YOUR_API_KEY \\"
        echo "                 --apiIssuer YOUR_ISSUER_ID"
        echo ""
        echo "  Or drag the IPA into Transporter.app on macOS."
    else
        echo "[WARN] IPA not found at expected path: $IPA_PATH"
        echo "       Check Xcode archive settings or codesign configuration."
    fi
fi

echo ""
echo "============================================================"
echo " Done!  Axon iOS ($CONFIG / $TARGET) build complete."
echo "============================================================"
