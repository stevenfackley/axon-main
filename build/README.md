# Axon — Build Scripts

All build and publish scripts live in this directory. Each script is self-contained and includes inline usage documentation.

---

## Scripts

| Script | Platform | Command |
|---|---|---|
| `build-windows.cmd` | Windows 11 (Power Station) | `build\build-windows.cmd [Release\|Debug]` |
| `build-android.cmd` | Android (Satellite) | `build\build-android.cmd [Release\|Debug] [avd-name]` |
| `build-ios.sh` | iOS (Satellite) — macOS only | `./build/build-ios.sh [Release\|Debug] [device\|simulator]` |

---

## Windows Desktop (Power Station)

Builds and publishes the Axon desktop app as a **Native AOT** binary for `win-x64`.

```cmd
build\build-windows.cmd
```

Runs: restore → build → test → `dotnet publish /p:PublishAot=true`

**Output:** `src\Axon.UI\bin\Release\net10.0\win-x64\publish\Axon.UI.exe`

**Prerequisites:**
- .NET 10 SDK
- Windows 11 with C++ Build Tools (required for Native AOT linking)

---

## Android Satellite

Builds, signs, and side-loads the Axon Android app onto an emulator or physical device.

```cmd
build\build-android.cmd
build\build-android.cmd Release Pixel_7_API_34
```

**Prerequisites:**
- Android SDK with `ANDROID_HOME` set (or default `%LOCALAPPDATA%\Android\Sdk`)
- .NET Android workload: `dotnet workload install android`
- Emulator AVD or physical device connected via ADB

---

## iOS Satellite *(macOS required)*

Builds the Axon iOS app and optionally prepares an IPA for TestFlight / App Store Connect.

```bash
./build/build-ios.sh                    # Release build for device (default)
./build/build-ios.sh Debug simulator    # Debug build for iOS Simulator
./build/build-ios.sh Release device     # Release build + IPA for TestFlight
```

**Prerequisites:**
- macOS with Xcode 16+
- .NET iOS workload: `dotnet workload install ios`
- Valid Apple Developer provisioning profile for `com.axon.telemetry`
- Signing certificate in Keychain

**Output:** `src/Axon.UI/bin/Release/net10.0-ios/ios-arm64/publish/Axon.UI.ipa`

### Uploading to TestFlight

After a successful `Release device` build:

```bash
xcrun altool --upload-app \
    -f "src/Axon.UI/bin/Release/net10.0-ios/ios-arm64/publish/Axon.UI.ipa" \
    --type ios \
    --apiKey YOUR_API_KEY \
    --apiIssuer YOUR_ISSUER_ID
```

Or use **Transporter.app** (free from the Mac App Store).

---

## CI Pipeline

The GitHub Actions workflow at `.github/workflows/ci.yml` runs automatically on every push and pull request to `main` and `dev`:

| Job | What It Does |
|---|---|
| `build-and-test` | `dotnet build` + `dotnet test` on Windows |
| `aot-publish` | Native AOT publish for `win-x64` (validates AOT compatibility) |
| `dependency-audit` | Scans for known CVEs and banned telemetry packages |
| `architecture-check` | Verifies `Axon.Core` has no forbidden references to Infrastructure or UI |
