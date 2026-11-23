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
| **Media & Lyrics** | Windows GSMTC media controls with browser hover protection and time-synced karaoke lyrics (optional LRCLIB opt-in, pin-to-expand view) |
| **Weather & Finance** | Dedicated tabs and synced Bento tiles — Open-Meteo weather with atmospheric effects; Finance watchlist with synced ranges (`Live`, `1D`, `1W`, `1M`, `1Y`, `5Y`, `ALL`) |
| **Control Center & Power** | Customizable quick-action grid, built-in **Calculator**, per-app **WASAPI Sound Mixer**, **Identify Song** (opt-in Shazam audio fingerprinting via system loopback), Bluetooth/Wi-Fi/Night Light toggles, audio output switcher, and header **Power Menu** with double-click confirmation & **Power Scheduler** (*Shut down*, *Restart*, *Sign out*, *Sleep*) |
| **Productivity & Capture** | 5-slot drag-and-drop **File Tray**, SQLite **Clipboard History**, **Screen OCR & QR**, **Screen Capture** (WGC + WASAPI), **Downloads** monitor, **Notifications** history & mute filters, and LAN **Phone File Transfer** via QR |
| **Personalization** | Unified OLED Dark UI, 14 accent presets + custom color/gradient editor, 8 screen positions, modular info slots, *Work / Personal / Gaming* onboarding profiles, and bilingual UI (**English** & **German**) |

---

## Download & Install

No administrator rights are required — all releases are self-contained (`.NET 10` included).

1. Download from [GitHub Releases](https://github.com/Foxtrott7/FreeNotch/releases):
   - **Installer (Recommended)**: **`FreeNotch-v*-win-x64-setup.exe`** — per-user Inno Setup installer (`%LocalAppData%\Programs\FreeNotch`), English & German, Start Menu shortcut, and standard Windows *Apps & Features* uninstall.
   - **Portable**: **`FreeNotch-v*-win-x64-portable.zip`** — extract anywhere and run **`FreeNotch.exe`** directly (no installation).
2. Verify the downloaded file against `SHA256SUMS.txt`:
   ```powershell
   Get-FileHash .\FreeNotch-v*-win-x64-setup.exe -Algorithm SHA256
   ```

> **Note on Unsigned Open-Source Builds**: Release builds are currently **unsigned** (no commercial EV certificate yet).
> - **Windows SmartScreen**: On first launch, Windows may show *"Windows protected your PC"*. After verifying the SHA-256 hash against `SHA256SUMS.txt`, click **More info → Run anyway**.
> - **Windows 11 Smart App Control**: If Smart App Control is set to *On* (enforcement mode), Windows blocks unsigned executables without a per-app override. In that case, build from source below or wait for future code-signed releases.

---

## Security, Privacy & Build Transparency

FreeNotch is designed for transparent, verifiable execution on Windows 10/11:

- **Hardened Local Services**: `PhoneTransferService` sanitizes filenames against path traversal and reserved Windows device names, verifies directory containment, caps concurrent connections, and validates tokens using `CryptographicOperations.FixedTimeEquals`. External links opened from OCR or Clipboard History enforce `http`/`https` allowlisting; all SQLite queries are parameterized.
- **Automated CI Verification**: Every push and pull request is built and tested (including localization parity, settings normalization, and security tests) via [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
- **Reproducible Releases & Build Transparency**: Release binaries and the Inno Setup installer are built cleanly from this repository via [`.github/workflows/release.yml`](.github/workflows/release.yml) on GitHub-hosted runners and published alongside `SHA256SUMS.txt`.
  - **Team & Review**: Maintainer & release approver is [@Foxtrott7](https://github.com/Foxtrott7). Releases are triggered exclusively from tagged commits on `master` through GitHub Actions.
  - **Code Signing Status**: Release assets are currently **unsigned** and verifiable via `SHA256SUMS.txt` and public GitHub Actions build logs. Automated code signing (e.g. via SignPath) is prepared in the release workflow for future releases.
- **Policies**: Full details on data handling and vulnerability reporting are documented in [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md).

---

## Build from Source

**Requirements**: Windows 10/11 (x64) and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# Build
dotnet build WindowsNotch.csproj -v q

# Run unit & security tests
dotnet test tests/WindowsNotch.Tests/WindowsNotch.Tests.csproj

# Package portable ZIP, Inno Setup installer, and SHA256SUMS.txt (add -SkipInstaller if Inno Setup is not installed)
.\scripts\publish-release.ps1
```

### Repository Structure

```text
├── Core/                # Design tokens, module catalog/host, settings, tray icon renderer
├── Modules/             # Feature modules (Widgets, Media, Control Center, FileTray, Finance, etc.)
├── Services/            # Localization, SQLite storage, GSMTC media, weather, lyrics, fullscreen
├── UI/                  # Notch shell, power menu, settings window, onboarding, localization
├── Resources/Lang/      # Embedded translations (en.json, de.json)
├── installer/           # Per-user Inno Setup script (FreeNotch.iss) & Winget manifest templates
├── scripts/             # Release publish & packaging script (publish-release.ps1)
├── tests/               # xUnit test suite (WindowsNotch.Tests)
└── .github/workflows/   # CI and Release workflows
```

---

## Known Issues & Limitations

- **Unsigned Executables**: Release builds are currently unsigned and may trigger a Windows SmartScreen prompt on first launch (verify checksums via `SHA256SUMS.txt`), and cannot run when Windows 11 Smart App Control is in enforcement mode.
- **System Limits**: Bluetooth battery reporting depends on the device's Windows driver; Windows notification listening uses a 500 ms polling fallback when running unpackaged.

---

## Contributing & License

- Bug reports and pull requests are welcome on [GitHub](https://github.com/Foxtrott7/FreeNotch). Please keep changes focused, bilingual (`en.json` & `de.json`), and covered by `dotnet test`.
- Security issues must be reported privately via [GitHub Security Advisories](https://github.com/Foxtrott7/FreeNotch/security/advisories/new) (see [SECURITY.md](SECURITY.md)).
- FreeNotch is free software licensed under the **[GNU General Public License v3.0 (GPLv3)](LICENSE)**.
