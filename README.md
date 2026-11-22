<p align="center">
  <img src=".github/assets/hero-banner.svg" alt="FreeNotch — Dynamic Island for Windows 11" width="880" />
</p>

<p align="center">
  <a href="https://github.com/Foxtrott7/FreeNotch/releases">
    <img src="https://img.shields.io/github/v/release/Foxtrott7/FreeNotch?style=for-the-badge&color=2563EB" alt="Latest Release" />
  </a>
  <a href="https://github.com/Foxtrott7/FreeNotch/actions/workflows/ci.yml">
    <img src="https://img.shields.io/github/actions/workflow/status/Foxtrott7/FreeNotch/ci.yml?branch=master&style=for-the-badge&label=CI" alt="CI Status" />
  </a>
  <a href="LICENSE">
    <img src="https://img.shields.io/badge/License-GPLv3-30D158?style=for-the-badge" alt="GPLv3 License" />
  </a>
</p>

<p align="center">
  <strong>A calm, native Dynamic Island-style notch for Windows 11 — written in C# and WPF (.NET 10).</strong><br/>
  Media controls, live widgets, system utilities, and productivity tools in one unobtrusive top-edge pill. Local-first, open source, and free of telemetry.
</p>

---

## Why FreeNotch?

FreeNotch turns the top edge of your Windows 11 screen into an adaptive, low-distraction command surface. Instead of juggling separate tray utilities, overlays, and settings windows, it brings everyday controls together in a single GPU-accelerated WPF shell that stays out of the way when you work or game.

- **Local-First & Private**: Runs entirely on your device with zero accounts, ads, or analytics.
- **Unprivileged by Design**: Uses an `asInvoker` manifest — no administrator privileges required to install or run.
- **Calm Motion**: Adaptive animations (60–240 Hz) and automatic hiding over fullscreen apps and games.

---

## Features

### Widgets & Bento Dashboard
- **2×2 Drag-and-Drop Bento Grid**: Reorderable tiles with a Jiggle-Mode editor and built-in Module Store.
- **Media & Synced Lyrics**: Windows GSMTC media playback controls with browser hover protection and time-synced karaoke lyrics (optional LRCLIB opt-in).
- **Weather**: Dedicated weather tab and bento tile with animated atmospheric effects, city search, and location management (optional Open-Meteo opt-in).
- **Finance**: Watchlist tab with live/historical chart ranges (`Live`, `1D`, `1W`, `1M`, `1Y`, `5Y`, `All-Time`) and a `1D` bento tile (optional opt-in).
- **Planning & Utilities**: Calendar, tasks, notes, stopwatch, Pomodoro/focus timer, configurable app shortcuts carousel, and CPU/RAM/GPU metrics.

### System & Control Center
- **Quick Actions & Control Center**: Customizable grid of system toggles and quick actions.
- **Live Notifications**: Windows notification listener with searchable history and per-app mute filters.
- **Bluetooth & Audio**: Quick audio output switcher and Bluetooth device/battery overview.
- **Privacy Indicator**: Subtle status LED when your camera or screen capture is active.

### Productivity & Capture
- **5-Slot File Tray**: Drag-and-drop temporary shelf for files and folders.
- **Clipboard History**: Searchable SQLite-backed clipboard manager with safe link handling.
- **Screen OCR & QR**: Region capture for text extraction, translation, and QR code scanning.
- **Screen Capture**: Built-in screen recording (Windows Graphics Capture + WASAPI audio) with preview.
- **Downloads Monitor**: Tracks recent browser downloads right from the notch.
- **Phone File Transfer**: Local Wi-Fi file transfer between your smartphone and PC via QR code (with optional temporary online sharing).

### Personalization & Profiles
- **Themes & Colors**: 4 themes (`Dark`, `DarkGlass`, `Light`, `LightGlass`), 14 accent presets, and a custom color/gradient editor.
- **Display & Positioning**: 8 screen positions, multi-monitor DPI awareness, and modular info slots (`NotchInfo`).
- **Onboarding Profiles**: Tailored presets for *Work*, *Personal*, and *Gaming* setups, plus an interactive tour.
- **Bilingual UI**: Full support for **English** (`en`) and **German** (`de`).

---

## Architecture, Security & Privacy Transparency

FreeNotch is built for verifiable, auditable execution on Windows 10/11:

- **Zero Telemetry**: No background tracking, crash-reporting servers, or user accounts. All settings (`settings.json`), local databases (`notch.db` via parameterized `Microsoft.Data.Sqlite`), and error logs (`error.log`) stay in `%AppData%\WindowsNotch\`.
- **Strict Opt-In Networking**: All external network features (Weather via Open-Meteo, Lyrics via LRCLIB, website favicons, finance quotes, and temporary online phone-transfer uploads) are **disabled by default** and only connect when explicitly enabled by the user.
- **Hardened Local Services**:
  - `PhoneTransferService` sanitizes uploaded filenames against path traversal and reserved Windows device names, verifies target directory containment, enforces concurrent connection caps and header timeouts, and validates tokens using constant-time comparison (`CryptographicOperations.FixedTimeEquals`).
  - External links opened from OCR or Clipboard History enforce strict `http`/`https` scheme allowlisting before launching.
- **Transparent CI/CD & Signing**:
  - Every push and pull request is built and verified with xUnit tests (including localization parity, settings normalization, and security checks) via [`.github/workflows/ci.yml`](.github/workflows/ci.yml).
  - Release artifacts are built cleanly from source via [`.github/workflows/release.yml`](.github/workflows/release.yml), packaged with `SHA256SUMS.txt`, and signed using SignPath.io Authenticode signing.

For full details, see [PRIVACY.md](PRIVACY.md) and [SECURITY.md](SECURITY.md).

---

## Download & Install

No administrator rights are required.

1. Download **`FreeNotch-v*-win-x64-portable.zip`** from [GitHub Releases](https://github.com/Foxtrott7/FreeNotch/releases) (self-contained, includes the .NET runtime) — or **`FreeNotch-v*-win-x64-framework-dependent.zip`** if you already have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed.
2. Verify the download against `SHA256SUMS.txt`:
   ```powershell
   Get-FileHash .\FreeNotch-v*-win-x64-portable.zip -Algorithm SHA256
   ```
3. Extract the archive and either:
   - Run **`FreeNotch.exe`** directly (portable), or
   - Run **`Install.cmd`** to install per-user into `%LocalAppData%\Programs\WindowsNotch` with a Start Menu shortcut (uninstall anytime via `Uninstall-PerUser.ps1`).

Free code signing provided by [SignPath.io](https://signpath.io/), using a certificate by [SignPath Foundation](https://signpath.org/).

---

## Build from Source

**Requirements**: Windows 10/11 (x64) and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# Build the application
dotnet build WindowsNotch.csproj -v q

# Run unit & security tests
dotnet test tests/WindowsNotch.Tests/WindowsNotch.Tests.csproj

# Create portable & framework-dependent release archives + SHA256SUMS.txt
.\scripts\publish-release.ps1
```

### Repository Layout

```text
├── Core/                # Design tokens, module catalog/host, settings, tray icon renderer
├── Modules/             # Feature modules (Widgets, Media, Control Center, FileTray, OCR, etc.)
├── Services/            # Localization, SQLite storage, GSMTC media, weather, lyrics, fullscreen
├── UI/                  # Notch shell, settings window, onboarding, localization XAML extension
├── Resources/Lang/      # Embedded localization files (en.json, de.json)
├── scripts/             # Release packaging and per-user install/uninstall scripts
├── tests/               # xUnit test suite (WindowsNotch.Tests)
└── .github/workflows/   # CI and SignPath-integrated Release pipelines
```

---

## Roadmap (Upcoming)

The following improvements are planned for the upcoming `v1.0.x` releases:

- **Integrated Calculator**: Built-in Control Center calculator (`+ − × ÷ % = C ⌫` with keyboard support) in the Notchify design instead of launching external `calc.exe`.
- **Integrated Sound Mixer**: Native per-app WASAPI volume sliders and mute toggles directly inside the Control Center.
- **Identify Song Overhaul**: Full Control Center sub-view with live audio level visualization, animated recognition wave, and inline opt-in activation.
- **Header Power Menu**: Quick power menu next to Settings (*Quit FreeNotch*, *Sleep*, *Restart*, *Shut down*) with inline confirmation for destructive actions.
- **Visual Polish**: Unified Notchify context menus, refreshed `FN` tray badge icon, and streamlined OLED Dark UI.

---

## License

FreeNotch is free software licensed under the [GNU General Public License v3.0 (GPLv3)](LICENSE).
