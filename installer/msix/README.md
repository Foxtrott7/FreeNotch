# FreeNotch – Microsoft Store (MSIX) Anleitung

Mit einem **MSIX-Paket** über den Microsoft Store übernimmt Microsoft die Code-Signierung kostenlos. Damit entfallen Browser-SmartScreen-Warnungen und Windows-11-**Smart App Control**-Blockaden für alle Endnutzer.

## 1. Kostenlosen Entwickler-Account anlegen (0 €)
1. Auf [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com) **Individual developer (free)** auswählen (keine 19-$-Gebühr mehr).
2. Identität einmalig per Ausweisdokument und Selfie verifizieren.

## 2. App-Namen reservieren & Store-Identität übernehmen
1. Im **Microsoft Partner Center** unter *Apps and games → New product → MSIX or PWA app* den Namen **FreeNotch** reservieren.
2. Unter **Product management → Product identity** stehen drei Werte:
   - `Package/Identity/Name`
   - `Package/Identity/Publisher` (z. B. `CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX`)
   - `Package/Properties/PublisherDisplayName`
3. Diese Werte entweder direkt in `installer/msix/AppxManifest.xml` eintragen **oder** in GitHub unter **Settings → Secrets and variables → Actions → Variables** anlegen:
   - `MSIX_PACKAGE_NAME`
   - `MSIX_PUBLISHER`
   - `MSIX_PUBLISHER_DISPLAY_NAME`

## 3. MSIX bauen & einreichen
Lokal bauen (erzeugt `publish\FreeNotch-v<Version>-win-x64.msix`):

```powershell
./scripts/publish-release.ps1 -NoRestore
```

Anschließend im Partner Center bei der Einreichung:
1. Die erzeugte `.msix`-Datei hochladen (muss **nicht** selbst signiert werden – Microsoft signiert sie bei der Zertifizierung automatisch).
2. **`runFullTrust`-Hinweis** im Einreichungsformular kurz begründen (unter *Übermittlungsoptionen → Hinweise zur Zertifizierung*, z. B.: *„Desktop notch utility for Windows 11 requiring Win32 window positioning, WASAPI audio session mixer, media/system telemetry, and screen capture.“*).
