param(
    [string]$Version = '',
    [switch]$SkipFrameworkDependent,
    [switch]$NoRestore,
    [switch]$SkipArchive,
    [switch]$PackageOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

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
if (-not $PackageOnly) {
    if (Test-Path -LiteralPath $PublishRoot) {
        Remove-Item -LiteralPath $PublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null
}

$InstallerFiles = @(
    (Join-Path $PSScriptRoot 'Install-PerUser.ps1'),
    (Join-Path $PSScriptRoot 'Install.cmd'),
    (Join-Path $PSScriptRoot 'Uninstall-PerUser.ps1')
)

$Variants = @(
    @{ Name = 'win-x64-portable'; SelfContained = $true; Zip = "WindowsNotch-v$Version-win-x64-portable.zip" }
)
if (-not $SkipFrameworkDependent) {
    $Variants += @{ Name = 'win-x64-framework-dependent'; SelfContained = $false; Zip = "WindowsNotch-v$Version-win-x64-framework-dependent.zip" }
}

if (-not $PackageOnly) {
    foreach ($v in $Variants) {
        $OutDir = Join-Path $PublishRoot $v.Name
        Write-Host "=== Publishe $($v.Name) (SelfContained=$($v.SelfContained), Version=$Version) ==="

        $scFlag = if ($v.SelfContained) { 'true' } else { 'false' }
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
            throw "dotnet publish fuer '$($v.Name)' fehlgeschlagen (ExitCode $LASTEXITCODE)."
        }

        foreach ($file in $InstallerFiles) {
            Copy-Item -LiteralPath $file -Destination $OutDir -Force
        }
    }
}

if ($SkipArchive) {
    return
}

$zips = foreach ($v in $Variants) {
    $OutDir = Join-Path $PublishRoot $v.Name
    if (-not (Test-Path -LiteralPath $OutDir)) {
        throw "Publish-Verzeichnis nicht gefunden: $OutDir"
    }
    $ZipPath = Join-Path $PublishRoot $v.Zip
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Write-Host "Erstellt: $ZipPath"
    $ZipPath
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
