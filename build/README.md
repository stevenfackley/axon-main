# Axon — Build Scripts

All build and publish scripts live in this directory. Each script is self-contained and includes inline usage documentation.

---

## Scripts

| Script | Platform | Command |
|---|---|---|
| `build-windows.cmd` | Windows 11 (Power Station) | `build\build-windows.cmd [Release\|Debug]` |
| `build-android.cmd` | Android — Windows | `build\build-android.cmd [Release\|Debug] [avd-name]` |
| `build-android.sh` | Android — Linux / WSL2 | `./build/build-android.sh [Release\|Debug] [avd-name]` |
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

### Windows

```cmd
build\build-android.cmd
build\build-android.cmd Release Pixel_9_API_36_x86_64
```

**Prerequisites:**
- Android SDK with `ANDROID_HOME` set (or default `%LOCALAPPDATA%\Android\Sdk`)
- .NET Android workload: `dotnet workload install android`
- Emulator AVD or physical device connected via ADB

### Linux / WSL2

```bash
./build/build-android.sh
./build/build-android.sh Release axon_dev
```

**One-time WSL2 setup** (all commands run as `sudo` / `root`):

```bash
# 1. Install .NET Android workload
sudo dotnet workload install android

# 2. Install Java 17
sudo apt-get install -y openjdk-17-jdk-headless

# 3. Download Android cmdline-tools for Linux
sudo mkdir -p /opt/android-sdk/cmdline-tools
sudo wget -O /tmp/cmdtools.zip \
  https://dl.google.com/android/repository/commandlinetools-linux-11076708_latest.zip
sudo unzip -q /tmp/cmdtools.zip -d /opt/android-sdk/cmdline-tools
sudo mv /opt/android-sdk/cmdline-tools/cmdline-tools \
        /opt/android-sdk/cmdline-tools/latest

# 4. Accept licenses and install SDK components
yes | sudo /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager --licenses
sudo /opt/android-sdk/cmdline-tools/latest/bin/sdkmanager \
  "platforms;android-36" \
  "build-tools;36.0.0" \
  "platform-tools" \
  "emulator" \
  "system-images;android-36;google_apis;x86_64" \
  --sdk_root=/opt/android-sdk

# 5. Persist SDK environment variables
sudo cp /etc/profile.d/android-sdk.sh /etc/profile.d/android-sdk.sh  # already done
source /etc/profile.d/android-sdk.sh

# 6. Create the AVD (one-time)
echo no | avdmanager create avd \
  --name axon_dev \
  --abi google_apis/x86_64 \
  --package "system-images;android-36;google_apis;x86_64" \
  --device "pixel_9"
```

**KVM acceleration (required for WSL2):**

```bash
ls /dev/kvm          # must exist
groups               # current user must be in the 'kvm' group
sudo usermod -aG kvm $USER
```

**Run (headless emulator in WSL2):**

```bash
# Start emulator headless, then build + deploy:
./build/build-android.sh Debug axon_dev

# Or manually:
emulator -avd axon_dev -no-window -no-audio -gpu swiftshader_indirect -accel on &
adb wait-for-device
dotnet build src/Axon.UI/Axon.UI.csproj -f net10.0-android -c Debug
adb install -r src/Axon.UI/bin/Debug/net10.0-android/com.axon.telemetry-Signed.apk
adb shell monkey -p com.axon.telemetry -c android.intent.category.LAUNCHER 1
```

> **⚠️ WSL2 emulator limitation:** The Android emulator's QEMU backend requires KVM
> ioctls (`KVM_GET_SREGS2`, VM-stop) that WSL2's Hyper-V KVM compatibility layer does
> not fully implement. You will see `ERROR | stop: Not implemented` and the emulator
> will not start.
>
> **Workaround — connect WSL ADB to the Windows emulator:**
> 1. Start an AVD from Android Studio (or `%LOCALAPPDATA%\Android\Sdk\emulator\emulator.exe -avd <name>`) on the Windows side.
> 2. In WSL, connect ADB over TCP to the Windows-hosted emulator:
>    ```bash
>    adb connect 127.0.0.1:5554     # adjust port if needed
>    adb devices                    # should show 127.0.0.1:5554 device
>    ```
> 3. Then use `build-android.sh` normally — it will detect the connected device.
>
> Alternatively, connect a **physical Android device** via USB and use
> [usbipd-win](https://github.com/dorssel/usbipd-win) to forward the USB port into WSL2.

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
