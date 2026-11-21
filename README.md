# Windows Notch

A native, GPU-accelerated Dynamic Island-style notch for Windows 11 built with WPF and .NET 10 — local-first, zero telemetry, no account required, and bilingual (**English** & **Deutsch**).

---

## Key Features

- **Privacy by Design**: No accounts, no ads, no analytics or telemetry. Settings (`settings.json`), SQLite history, and logs (`error.log`) stay locally in `%AppData%\WindowsNotch\`.
- **Explicit Online Opt-Ins**: All network features are disabled by default and only connect when you enable them (Weather via Open-Meteo, Synced Lyrics via LRCLIB, website favicons, optional finance live feeds, or temporary online phone-transfer uploads).
- **Native & Smooth (60–240 Hz)**: Pure WPF/DirectX UI with adaptive frame rates and Windows *Animation Effects* respect.
- **Fullscreen Protection**: Automatically hides over fullscreen games, videos, and presentations (with customizable app exclusions).
- **Flexible Interaction & Placement**: Hover and/or click activation, multi-monitor support (`PerMonitorV2` DPI), 8 screen edges (docked or floating), 4 themes (`Dark`, `DarkGlass`, `Light`, `LightGlass`), 14 accent presets + custom color editor, and customizable idle/hover status slots.

## Included Modules

- **Widgets Dashboard (`2×2` Bento Grid)**: Rearrangeable drag-and-drop tiles with a built-in module drawer — Live Lyrics (with karaoke word wave & pinning), Media Player, Weather (with animated atmospheric effects), Calendar / Tasks / Notes, Finance tile, customizable Carousel, Focus Timer, Stopwatch, and Hardware Monitor (CPU / RAM / GPU).
- **Media & Notifications**: Windows media session controls (`GSMTC`) and live notification banners with history and per-app mute filters.
- **Productivity & Utilities**:
  - **File Tray**: 5-slot drag-and-drop shelf for quick file staging across apps.
  - **Clipboard History**: Searchable SQLite history for text, links, and images with pinning.
  - **Screen Snip & OCR**: Region capture with Windows OCR, QR detection, image/text copy, and save.
  - **Screen Capture**: Monitor recording via Windows Graphics Capture + WASAPI audio with built-in preview.
  - **Phone Transfer**: Local Wi-Fi QR transfer (send & receive) plus optional temporary online sharing.
  - **Downloads Monitor**: Live browser download progress, speed, and ETA directly in the notch.
  - **Control Center & Bluetooth**: Quick toggles, master volume slider, audio output switcher, and Bluetooth battery display.
  - **Finance & Weather Tabs**: Focused views for stocks/crypto watchlists and multi-city forecasts.

## Download & Installation (No Admin Rights Required)

1. Download **`WindowsNotch-v*-win-x64-portable.zip`** from the [GitHub Releases](https://github.com/Foxtrott7/WindowsNotch/releases) page (includes the .NET 10 Desktop Runtime).
   *(Alternatively, choose `framework-dependent.zip` if .NET 10 Desktop Runtime is already installed.)*
2. Extract the archive and either:
   - Run `WindowsNotch.exe` directly for portable use, **or**
   - Double-click `Install.cmd` to install per-user to `%LocalAppData%\Programs\WindowsNotch` with a Start Menu shortcut.

To verify downloads, compare against the published `SHA256SUMS.txt`.

## Building from Source

**Requirements**: Windows 10/11 (x64) and [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
# Build & run
dotnet build WindowsNotch.csproj -v q
dotnet run --project WindowsNotch.csproj

# Run unit tests
dotnet test tests/WindowsNotch.Tests/WindowsNotch.Tests.csproj

# Create release archives (publish/)
.\scripts\publish-release.ps1
```

## License

Licensed under the **GNU General Public License v3.0 (GPLv3)** — see [LICENSE](LICENSE) for details.
