param(
    [string]$Version = '',
    [switch]$SkipFrameworkDependent,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot 'WindowsNotch.csproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$proj = Get-Content -LiteralPath $CsprojPath -Encoding UTF8
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = '1.0.0'
    }
}
$Version = $Version.TrimStart('v')

$PublishRoot = Join-Path $RepoRoot 'publish'
if (Test-Path -LiteralPath $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

$InstallerFiles = @(
    (Join-Path $PSScriptRoot 'Install-PerUser.ps1'),
    (Join-Path $PSScriptRoot 'Install.cmd'),
    (Join-Path $PSScriptRoot 'Uninstall-PerUser.ps1')
)

function Publish-Variant {
    param(
        [string]$VariantName,
        [bool]$SelfContained,
        [string]$ZipName
    )

    $OutDir = Join-Path $PublishRoot $VariantName
    Write-Host "=== Publishe $VariantName (SelfContained=$SelfContained, Version=$Version) ==="

    $scFlag = if ($SelfContained) { 'true' } else { 'false' }
    $publishArgs = @(
        'publish', $CsprojPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', $scFlag,
        "-p:Version=$Version",
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $OutDir
    )
    if ($NoRestore) {
        $publishArgs += '--no-restore'
    }
    & dotnet @publishArgs | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish fuer '$VariantName' fehlgeschlagen (ExitCode $LASTEXITCODE)."
    }

    foreach ($file in $InstallerFiles) {
        Copy-Item -LiteralPath $file -Destination $OutDir -Force
    }

    $ZipPath = Join-Path $PublishRoot $ZipName
    Compress-Archive -Path (Join-Path $OutDir '*') -DestinationPath $ZipPath -CompressionLevel Optimal -Force
    Write-Host "Erstellt: $ZipPath"
    return $ZipPath
}

$zips = @()
$zips += Publish-Variant -VariantName 'win-x64-portable' -SelfContained $true -ZipName "WindowsNotch-v$Version-win-x64-portable.zip"

if (-not $SkipFrameworkDependent) {
    $zips += Publish-Variant -VariantName 'win-x64-framework-dependent' -SelfContained $false -ZipName "WindowsNotch-v$Version-win-x64-framework-dependent.zip"
}

$SumsPath = Join-Path $PublishRoot 'SHA256SUMS.txt'
$sumLines = foreach ($zip in $zips) {
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $(Split-Path -Leaf $zip)"
}
$sumLines | Set-Content -LiteralPath $SumsPath -Encoding UTF8

Write-Host ''
Write-Host '=== Release-Artefakte ==='
Get-Content -LiteralPath $SumsPath | ForEach-Object { Write-Host $_ }
