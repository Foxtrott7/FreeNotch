param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$SourceDir = $PSScriptRoot
$SourceExe = Join-Path $SourceDir 'WindowsNotch.exe'
if (-not (Test-Path -LiteralPath $SourceExe)) {
    throw "WindowsNotch.exe wurde in '$SourceDir' nicht gefunden."
}

$TargetDir = Join-Path $env:LOCALAPPDATA 'Programs\WindowsNotch'
$TargetExe = Join-Path $TargetDir 'WindowsNotch.exe'

Write-Host 'Installiere Windows Notch (Benutzerkontext, ohne Adminrechte)...'

# Laufende Instanz sauber beenden, damit Dateien ersetzt werden koennen
$running = Get-Process -Name 'WindowsNotch' -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 600
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

# Programmdateien kopieren (ohne Installations-Wrapper)
Get-ChildItem -LiteralPath $SourceDir -Force | Where-Object {
    $_.Name -notin @('Install.cmd', 'Install-PerUser.ps1')
} | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $TargetDir -Recurse -Force
}

# Startmenue-Verknuepfung im Benutzerprofil anlegen
$StartMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$ShortcutPath = Join-Path $StartMenuDir 'Windows Notch.lnk'
$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $TargetExe
$Shortcut.WorkingDirectory = $TargetDir
$Shortcut.IconLocation = "$TargetExe,0"
$Shortcut.Description = 'Windows Notch'
$Shortcut.Save()

# Version aus PE-Metadaten lesen
$Version = (Get-Item -LiteralPath $TargetExe).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '1.0.0' }
$Version = ($Version -split '\+')[0]

# Deinstallation unter "Einstellungen -> Apps" (HKCU, ohne Adminrechte) registrieren
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsNotch'
New-Item -Path $UninstallKey -Force | Out-Null
Set-ItemProperty -Path $UninstallKey -Name 'DisplayName' -Value 'Windows Notch'
Set-ItemProperty -Path $UninstallKey -Name 'DisplayVersion' -Value $Version
Set-ItemProperty -Path $UninstallKey -Name 'Publisher' -Value 'WindowsNotch'
Set-ItemProperty -Path $UninstallKey -Name 'InstallLocation' -Value $TargetDir
Set-ItemProperty -Path $UninstallKey -Name 'DisplayIcon' -Value "$TargetExe,0"
$UninstallScript = Join-Path $TargetDir 'Uninstall-PerUser.ps1'
Set-ItemProperty -Path $UninstallKey -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$UninstallScript`""
Set-ItemProperty -Path $UninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty -Path $UninstallKey -Name 'NoRepair' -Value 1 -Type DWord

# Falls Autostart bereits aktiviert war, Pfad auf den Installationsort aktualisieren
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path -LiteralPath $RunKey) {
    $existingRun = (Get-ItemProperty -Path $RunKey -Name 'WindowsNotch' -ErrorAction SilentlyContinue).WindowsNotch
    if ($existingRun) {
        Set-ItemProperty -Path $RunKey -Name 'WindowsNotch' -Value "`"$TargetExe`""
    }
}

Write-Host "Installation abgeschlossen: $TargetDir"

if (-not $NoLaunch) {
    Start-Process -FilePath $TargetExe -WorkingDirectory $TargetDir
}
