# FootballBar — Live Football Scores, Fixtures & Results in Your Windows Taskbar

[![Release](https://img.shields.io/github/v/release/bo3abed505/FootballBar-Releases?include_prereleases&style=flat-square)](https://github.com/bo3abed505/FootballBar-Releases/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%20Windows%2010%20x64-blue?style=flat-square)](https://github.com/bo3abed505/FootballBar-Releases)
[![UI](https://img.shields.io/badge/framework-WinUI%203%20%2F%20Windows%20App%20SDK-0078D7?style=flat-square)](https://github.com/microsoft/WindowsAppSDK)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Privacy](https://img.shields.io/badge/privacy-Zero%20Data%20Collection-success?style=flat-square)](PRIVACY.md)

**FootballBar** is a native, ultra-lightweight Windows 11 desktop application designed to stream real-time football scores, match minutes, goal alerts, and broadcast-style tactical commentary directly inside your Windows taskbar.

*Note: For zero-breakage backwards compatibility and seamless user data preservation, the underlying binary executable and data directory remain `FootballTray.exe` and `%LOCALAPPDATA%\FootballTray`.*

---

## ⚡ Key Highlights (v1.3.1)

- **Native Windows 11 Taskbar Live Ticker**: Docked directly into your Windows taskbar alongside the system tray. Cycles live match scores, match minutes, goal scorers, and red card preemption alerts without requiring third-party widget frameworks or overlays.
- **Mouse-Wheel Ticker Cycling**: Hover over the taskbar score ticker and roll your mouse wheel (Scroll Up = Previous, Scroll Down = Next) to instantly flip between live matches.
- **On-Device Tactical Narrative Synthesizer**: Broadcast-quality, analytical tactical commentary paragraphs generated dynamically inside Match Center from live timeline events, expected goals (xG), shot accuracy, big chances, 5-match team form, and head-to-head records.
- **Upstream Match Insights & Player of the Match**: Deep tactical storylines, key fixture context, and Player of the Match honors extracted directly into Match Center.
- **Cross-Midnight Match Continuity**: Robust timezone synchronization ensuring late-night fixtures (kickoffs spanning midnight 12:00 AM – 2:00 AM local time / UTC+2) remain continuously active in both the Live tab and the Taskbar Ticker without sync overwrites.
- **Interactive Dark-Mode Setup Wizard**: One-click installer with automatic setup to `%LOCALAPPDATA%\Programs\FootballBar`, Desktop and Start Menu shortcuts, optional Windows Startup toggle, and immediate launch.
- **Team & Competition Hubs**: In-depth club exploration with upcoming fixtures, recent form badges, league standings, squad rosters, and competition knockout stage brackets.
- **Granular Notification Profiles**: Tailor toast alerts and goal audio effects per followed club or competition, with automatic suppression when in Do Not Disturb mode.
- **100% Privacy & Zero Tracking**: Operates completely on-device. Zero telemetry trackers, zero personal data harvesting, zero ads, and zero account registration required.

---

## 📦 Downloads & Distribution Formats

Visit the [Official Releases Page](https://github.com/bo3abed505/FootballBar-Releases/releases/latest) to download the latest **v1.3.1** package:

| Package | Filename | Best For |
| :--- | :--- | :--- |
| **One-Click Installer** | `FootballTray-Setup-v1.3.1-win-x64.exe` | Recommended for most users. Interactive modern setup with shortcut creation and auto-start. |
| **Portable Archive** | `FootballTray-v1.3.1-win-x64.zip` | Standalone portable edition. Extract anywhere and launch `FootballTray.exe` with zero installation. |
| **Microsoft Store Package** | `FootballTray-v1.3.1-win-x64.msix` | Packaged Desktop Bridge container for Microsoft Partner Center and Microsoft Store distribution. |

All builds are self-contained x64 executables. No separate .NET runtime or Windows App SDK package installation is required.

---

## 🚀 Getting Started

### Installation via Setup Wizard
1. Download `FootballTray-Setup-v1.3.1-win-x64.exe`.
2. Run the setup executable. The dark-themed installer will extract the application to `%LOCALAPPDATA%\Programs\FootballBar`.
3. Choose your shortcut preferences (Desktop, Start Menu, Windows Startup).
4. Click **Finish & Launch** to start FootballBar immediately.

### Portable Mode
1. Download `FootballTray-v1.3.1-win-x64.zip`.
2. Extract the archive into any folder of your choice.
3. Run `FootballTray.exe`.

---

## 🖥️ System Requirements

- **Operating System**: Windows 11 64-bit (recommended, Build 22000+) or Windows 10 64-bit (Build 17763+).
- **Processor**: x64 (AMD64 / Intel 64-bit).
- **Network**: Internet connection for live sports telemetry retrieval.
- **Runtimes**: None required (all necessary WinUI 3 and .NET runtime components are bundled self-contained).

---

## 🔒 Privacy & Security

FootballBar is built with an uncompromising commitment to user privacy:
- **Zero Personal Data Collection**: No accounts, usernames, IP addresses, emails, or hardware IDs are tracked or transmitted.
- **Local Storage**: Preferences, followed clubs, and temporary score caches remain strictly on your local disk at `%LOCALAPPDATA%\FootballTray\`.
- **Encrypted Communication**: All sports telemetry queries and manifest updates use encrypted HTTPS connections.
- Read our full [Privacy Policy](PRIVACY.md) for detailed legal and technical disclosures.

---

## 🛠️ Developer & Build Guide

### Building from Source

Prerequisites:
- [.NET 10.0 SDK](https://dotnet.microsoft.com/) with the Windows Desktop workload.
- PowerShell 7 (`pwsh`) or Windows PowerShell 5.1.

```pwsh
# Clone the repository
git clone https://github.com/bo3abed505/FootballBar-Releases.git
cd FootballBar-Releases

# Build the complete solution
dotnet build FootballTray.sln -c Release

# Run the test suite
dotnet test tests/FootballTray.Client.Tests/FootballTray.Client.Tests.csproj -c Release

# Package the official release bundle (produces .exe, .msix, .zip, and manifests)
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-release.ps1 -Version 1.3.1
```

---

## ⚖️ Legal & Fair-Use Disclaimer

FootballBar is an independent, open-source sports utility for personal informational and entertainment use. It is not affiliated with, authorized by, sponsored by, or endorsed by FIFA, UEFA, the Premier League, LaLiga, CAF, AFC, or any football club, league, or governing body. All team names, crests, competition titles, and trademarks referenced are the property of their respective owners and are utilized under nominative fair use for identification purposes only.

---

## 💬 Community & Support

- **Releases & Changelogs**: [GitHub Releases](https://github.com/bo3abed505/FootballBar-Releases/releases)
- **Issue Tracker & Feature Requests**: [GitHub Issues](https://github.com/bo3abed505/FootballBar-Releases/issues)
- **Privacy Disclosures**: [PRIVACY.md](PRIVACY.md)
