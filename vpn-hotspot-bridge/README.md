# Zero-Config — Android-to-PC VPN Hotspot Bridge

**Zero-Config** turns a non-rooted Android phone running an active VPN into an invisible upstream router via your Windows 10/11 PC. Smart TVs, consoles, secondary phones, and laptops connect to your PC's Mobile Hotspot normally and receive full VPN-tunneled internet (TCP + UDP + DNS) with **zero manual proxy configuration**.

## Quick Start
1. **Build Android APK:** `cd android-hub && gradle assembleDebug` (or download from GitHub Actions Artifacts) and install on your phone.
2. **Place Windows Native Binaries:** Put x64 `tun2socks.exe` and `wintun.dll` into `windows-bridge/Dependencies/` (see `windows-bridge/Dependencies/README_BINARIES.md`).
3. **Build Windows Bridge:** `cd windows-bridge && dotnet publish -c Release -r win-x64 --self-contained true -o ./publish`.
4. **Run:** Start VPN on Android -> Start **Zero-Config** on Android -> Connect phone via USB Tethering (recommended), Wi-Fi, or USB ADB -> Run `ZeroConfigBridge.exe` as Administrator and click **Connect & Share VPN**.