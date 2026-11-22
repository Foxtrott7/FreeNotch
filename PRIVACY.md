# Privacy Policy — FreeNotch

**FreeNotch does not collect, store, sell, or transmit personal data to the developer.**

## Local-Only by Default

- No user accounts, no ads, no analytics, and no telemetry.
- Settings (`settings.json`), local databases (`notch.db` for File Tray, Clipboard History, Tasks, and Notes), and error logs (`error.log`) are stored exclusively on your device in `%AppData%\WindowsNotch\`.
- Local Wi-Fi phone file transfer runs solely on your local network unless you explicitly trigger an online upload.

## Optional Online Features (Opt-In)

All network features are **disabled by default** and only connect when you enable or trigger them:

- **Weather**: Fetches forecasts from [Open-Meteo](https://open-meteo.com/) for the cities you choose.
- **Synced Lyrics**: Fetches lyrics from [LRCLIB](https://lrclib.net/) for the currently playing track.
- **Website Favicons**: Downloads icons for saved URLs in Clipboard History.
- **Finance Quotes**: Fetches stock/crypto data for tickers you add.
- **Temporary Online Phone Transfer**: Uploads a file to a temporary third-party host (`catbox.moe` / `0x0.st`) only when you explicitly choose online sharing.

Each external service is subject to its own privacy policy. You can disable all online features at any time in **Settings → Privacy**.
