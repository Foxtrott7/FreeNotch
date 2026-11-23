param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallerSha256
)

$ErrorActionPreference = 'Stop'

$ver = $Version.TrimStart('v')
$sha = $InstallerSha256.Trim().ToUpperInvariant()
if ($sha -notmatch '^[0-9A-F]{64}$') {
    throw "InstallerSha256 muss genau 64 Hex-Zeichen enthalten (erhalten: '$InstallerSha256')."
}

$url = "https://github.com/Foxtrott7/FreeNotch/releases/download/v$ver/FreeNotch-v$ver-win-x64-setup.exe"
$releaseNotesUrl = "https://github.com/Foxtrott7/FreeNotch/releases/tag/v$ver"

$files = @(
    'Foxtrott7.FreeNotch.yaml',
    'Foxtrott7.FreeNotch.installer.yaml',
    'Foxtrott7.FreeNotch.locale.en-US.yaml',
    'Foxtrott7.FreeNotch.locale.de-DE.yaml'
)

$manifestDir = Join-Path $PSScriptRoot 'manifests'
foreach ($name in $files) {
    $path = Join-Path $manifestDir $name
    $content = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $content = [regex]::Replace($content, '(?m)^PackageVersion:\s*.*$', "PackageVersion: $ver")
    $content = [regex]::Replace($content, '(?m)^(\s*InstallerUrl:\s*).*$', "`${1}$url")
    $content = [regex]::Replace($content, '(?m)^(\s*InstallerSha256:\s*).*$', "`${1}$sha")
    $content = [regex]::Replace($content, '(?m)^ReleaseNotesUrl:\s*.*$', "ReleaseNotesUrl: $releaseNotesUrl")
    Set-Content -LiteralPath $path -Value $content -NoNewline -Encoding UTF8
}

Write-Host "Winget-Manifeste aktualisiert auf Version $ver ($sha)."
