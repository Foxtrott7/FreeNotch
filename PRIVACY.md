# Privacy Policy — FreeNotch

**FreeNotch does not collect, store, sell, or transmit personal data to the developer.**

## Local-Only by Default

- **Zero Telemetry**: No user accounts, no ads, no analytics, and no crash-reporting servers.
- **On-Device Storage**: Settings (`settings.json`), local databases (`notch.db` for File Tray, Clipboard History, Tasks, and Notes), and diagnostic error logs (`error.log`) are stored exclusively on your PC in `%AppData%\WindowsNotch\`.
- **Local Phone Transfer**: Local Wi-Fi phone file transfer runs solely on your local network (`LAN`) unless you explicitly trigger a temporary online share.

## Optional Online Features (Strictly Opt-In)

All network features are **disabled by default** and only connect when you explicitly enable or trigger them in Settings or directly in the corresponding module:

- **Weather**: Fetches forecasts and location search results from [Open-Meteo](https://open-meteo.com/) for the cities you choose.
- **Synced Lyrics**: Fetches time-synced lyrics from [LRCLIB](https://lrclib.net/) for the currently playing track.
- **Website Favicons**: Downloads icons for saved URLs in Clipboard History.
- **Finance Quotes**: Fetches stock/crypto market data for tickers in your watchlist.
- **Music Recognition**: Sends a short, non-stored system-audio fingerprint snippet to Shazam discovery only when you explicitly trigger song identification.
- **Temporary Online Phone Transfer**: Uploads a selected file to a temporary third-party host (`catbox.moe` / `0x0.st`) only when you explicitly choose online sharing.

Each external service is governed by its own privacy policy. You can disable all online features at any time under **Settings → Privacy**.
