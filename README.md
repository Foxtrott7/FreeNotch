# FreeNotch

**A calm, native Dynamic Island-style notch for Windows 11 — built with C# and WPF (.NET 10).**

[Releases](https://github.com/Foxtrott7/FreeNotch/releases) · [CI Status](https://github.com/Foxtrott7/FreeNotch/actions/workflows/ci.yml) · [Privacy Policy](PRIVACY.md) · [Security Policy](SECURITY.md) · [License (GPLv3)](LICENSE)

FreeNotch turns the top edge of your Windows 11 screen into an adaptive, low-distraction command surface. Media controls, live widgets, system utilities, and productivity tools live inside one GPU-accelerated WPF shell that stays out of the way when you work or game.

---

## Core Principles

- **Local-First & Zero Telemetry**: No accounts, no ads, no analytics, and no crash-reporting servers. Settings (`settings.json`), local databases (`notch.db`), and diagnostic logs (`error.log`) remain on your device under `%AppData%\WindowsNotch\`.
- **Strict Opt-In Networking**: Every online feature is **disabled by default** and connects only after explicit user consent.
- **Unprivileged Execution**: Uses an `asInvoker` manifest — no administrator privileges are required to install or run.
- **Calm Motion**: Adaptive 60–240 Hz animations (`prefers-reduced-motion` aware) with automatic hiding over fullscreen apps and games.

---

## Key Features

| Area | Highlights |
|---|---|
| **Dashboard & Widgets** | Reorderable 2×2 Bento grid with Jiggle-Mode & Module Store, Calendar/Tasks/Notes, Stopwatch & Focus Timer, App Carousel, and CPU/RAM/GPU metrics |
| **Media & Lyrics** | Windows GSMTC media controls with browser hover protection and time-synced karaoke lyrics (optional LRCLIB opt-in) |
| **Weather & Finance** | Dedicated tabs and synced Bento tiles — Open-Meteo weather with atmospheric effects; Finance watchlist with synced ranges (`Live`, `1D`, `1W`, `1M`, `1Y`, `5Y`, `ALL`) |
| **Control Center** | Customizable quick-action grid, built-in **Calculator** (keyboard support), per-app **WASAPI Sound Mixer**, **Identify Song** (opt-in Shazam audio fingerprinting with Spotify/Apple Music links), Bluetooth/Wi-Fi/Night Light toggles, and audio output switcher |
| **Productivity & Capture** | 5-slot drag-and-drop **File Tray**, SQLite **Clipboard History**, **Screen OCR & QR**, **Screen Capture** (WGC + WASAPI), **Downloads** monitor, **Notifications** history & mute filters, and LAN **Phone File Transfer** via QR |
| **Personalization** | Unified OLED Dark UI, 14 accent presets + custom color/gradient editor, 8 screen positions, modular info slots, *Work / Personal / Gaming* onboarding profiles, and bilingual UI (**English** & **German**) |

---

## Download & Install

No administrator rights are required.

1. Download **`FreeNotch-v*-win-x64-portable.zip`** (self-contained, includes .NET runtime) or **`FreeNotch-v*-win-x64-framework-dependent.zip`** (requires [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)) from [GitHub Releases](https://github.com/Foxtrott7/FreeNotch/releases).
2. Verify the archive checksum against `SHA256SUMS.txt`:
   ```powershell
   Get-FileHash .\FreeNotch-v*-win-x64-portable.zip -Algorithm SHA256
   ```
3. Extract the ZIP and either:
   - Run **`FreeNotch.exe`** directly (portable), or
   - Run **`Install.cmd`** to install per-user into `%LocalAppData%\Programs\WindowsNotch` with a Start Menu shortcut (uninstall anytime via `Uninstall-PerUser.ps1`).

---

## Security, Privacy & Build Transparency

FreeNotch is designed for transparent, verifiable execution on Windows 10/11:

- **Hardened Local Services**: `PhoneTransferService` sanitizes filenames against path traversal and reserved Windows device names, verifies directory containment, caps concurrent connections, and validates tokens using `CryptographicOperations.FixedTimeEquals`. External links opened from OCR or Clipboard History enforce `http`/`https` allowlisting; all SQLite queries are parameterized.
- **Automated CI Verification**: Every push and pull request is built and tested (including localization parity, settings normalization, and security tests) via [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- **Reproducible Releases & Code Signing**: Release binaries are built cleanly from this repository via [`.github/workflows/release.yml`](.github/workflows/release.yml) and published with `SHA256SUMS.txt`.
  > Free code signing provided by [SignPath.io](https://signpath.io/), using a certificate by [SignPath Foundation](https://signpath.org/).
- **Policies**: Full details on data handling and vulnerability reporting are documented in [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md).

---

## Build from Source

**Requirements**: Windows 10/11 (x64) and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# Build
dotnet build WindowsNotch.csproj -v q

# Run unit & security tests
dotnet test tests/WindowsNotch.Tests/WindowsNotch.Tests.csproj

# Package portable & framework-dependent release archives + SHA256SUMS.txt
.\scripts\publish-release.ps1
```

### Repository Structure

```text
├── Core/                # Design tokens, module catalog/host, settings, tray icon renderer
├── Modules/             # Feature modules (Widgets, Media, Control Center, FileTray, Finance, etc.)
├── Services/            # Localization, SQLite storage, GSMTC media, weather, lyrics, fullscreen
├── UI/                  # Notch shell, power menu, settings window, onboarding, localization
├── Resources/Lang/      # Embedded translations (en.json, de.json)
├── scripts/             # Release packaging and per-user install/uninstall scripts
├── tests/               # xUnit test suite (WindowsNotch.Tests)
└── .github/workflows/   # CI and SignPath-integrated Release workflows
```

---

## Contributing & License

- Bug reports and pull requests are welcome on [GitHub](https://github.com/Foxtrott7/FreeNotch). Please keep changes focused, bilingual (`en.json` & `de.json`), and covered by `dotnet test`.
- Security issues must be reported privately via [GitHub Security Advisories](https://github.com/Foxtrott7/FreeNotch/security/advisories/new) (see [SECURITY.md](SECURITY.md)).
- FreeNotch is free software licensed under the **[GNU General Public License v3.0 (GPLv3)](LICENSE)**.
