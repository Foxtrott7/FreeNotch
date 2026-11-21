param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'SilentlyContinue'

$TargetDir = Join-Path $env:LOCALAPPDATA 'Programs\WindowsNotch'
$StartMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Windows Notch.lnk'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsNotch'

Write-Host 'Deinstalliere Windows Notch...'

Get-Process -Name 'WindowsNotch' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

if (Test-Path -LiteralPath $RunKey) {
    Remove-ItemProperty -Path $RunKey -Name 'WindowsNotch' -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $StartMenuShortcut) {
    Remove-Item -LiteralPath $StartMenuShortcut -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $UninstallKey) {
    Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $TargetDir) {
    Remove-Item -LiteralPath $TargetDir -Recurse -Force -ErrorAction SilentlyContinue
}

if ($PurgeData) {
    $DataDir = Join-Path $env:APPDATA 'WindowsNotch'
    if (Test-Path -LiteralPath $DataDir) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Windows Notch wurde entfernt. Lokale Einstellungen in %AppData%\WindowsNotch blieben erhalten (mit -PurgeData loeschbar).'
