param(
    [string]$Version = '',
    [switch]$NoRestore,
    [switch]$SkipArchive,
    [switch]$PackageOnly,
    [switch]$SkipInstaller,
    [switch]$SkipMsix,
    [switch]$ChecksumsOnly,
    [string]$MsixPackageName = '',
    [string]$MsixPublisher = '',
    [string]$MsixPublisherDisplayName = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot 'WindowsNotch.csproj'
$IssPath = Join-Path $RepoRoot 'installer\FreeNotch.iss'
$MsixManifestTemplate = Join-Path $RepoRoot 'installer\msix\AppxManifest.xml'
$MsixAssetsDir = Join-Path $RepoRoot 'installer\msix\Assets'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$proj = Get-Content -LiteralPath $CsprojPath -Encoding UTF8
    $Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = '1.0.0'
    }
}
$Version = $Version.TrimStart('v')

$PublishRoot = Join-Path $RepoRoot 'publish'
$PortableDir = Join-Path $PublishRoot 'win-x64-portable'
$ZipPath = Join-Path $PublishRoot "FreeNotch-v$Version-win-x64-portable.zip"
$SetupPath = Join-Path $PublishRoot "FreeNotch-v$Version-win-x64-setup.exe"
$MsixPath = Join-Path $PublishRoot "FreeNotch-v$Version-win-x64.msix"

function Resolve-IsccPath {
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe',
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($path in $candidates) {
        if ($path -and (Test-Path -LiteralPath $path)) {
            return $path
        }
    }
    return $null
}

function Resolve-MakeAppxPath {
    $cmd = Get-Command makeappx -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kits = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($kits) { return $kits.FullName }
    return $null
}

function ConvertTo-MsixVersion([string]$rawVersion) {
    $parts = @($rawVersion.Split('.', [System.StringSplitOptions]::RemoveEmptyEntries))
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts[0..3] -join '.')
}

function Write-Checksums {
    $artifacts = @($SetupPath, $ZipPath, $MsixPath) | Where-Object { Test-Path -LiteralPath $_ }
    if (-not $artifacts) {
        throw "Keine Release-Artefakte in '$PublishRoot' gefunden."
    }

    $sumsPath = Join-Path $PublishRoot 'SHA256SUMS.txt'
    $sumLines = foreach ($file in $artifacts) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $(Split-Path -Leaf $file)"
    }
    $sumLines | Set-Content -LiteralPath $sumsPath -Encoding UTF8

    Write-Host ''
    Write-Host '=== Release-Artefakte ==='
    Get-Content -LiteralPath $sumsPath | ForEach-Object { Write-Host $_ }
}

if ($ChecksumsOnly) {
    Write-Checksums
    return
}

if (-not $PackageOnly) {
    if (Test-Path -LiteralPath $PublishRoot) {
        Remove-Item -LiteralPath $PublishRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $PublishRoot -Force | Out-Null

    Write-Host "=== Publishe win-x64-portable (SelfContained=true, Version=$Version) ==="
    $publishArgs = @(
        'publish', $CsprojPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        "-p:Version=$Version",
        '-p:PublishSingleFile=false',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $PortableDir
    )
    if ($NoRestore) {
        $publishArgs += '--no-restore'
    }
    & dotnet @publishArgs | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish fehlgeschlagen (ExitCode $LASTEXITCODE)."
    }
}

if ($SkipArchive) {
    return
}

if (-not (Test-Path -LiteralPath $PortableDir)) {
    throw "Publish-Verzeichnis nicht gefunden: $PortableDir"
}

if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory($PortableDir, $ZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "Erstellt: $ZipPath"

if (-not $SkipInstaller) {
    if (-not (Test-Path -LiteralPath $IssPath)) {
        throw "Inno-Setup-Skript nicht gefunden: $IssPath"
    }
    $iscc = Resolve-IsccPath
    if (-not $iscc) {
        throw "Inno Setup (ISCC.exe) nicht gefunden. Installiere via 'winget install JRSoftware.InnoSetup' oder nutze -SkipInstaller."
    }

    Write-Host "=== Baue Inno-Setup-Installer (Version=$Version) ==="
    & $iscc "/DMyAppVersion=$Version" "/DMySourceDir=$PortableDir" "/DMyOutputDir=$PublishRoot" $IssPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC fehlgeschlagen (ExitCode $LASTEXITCODE)."
    }
    Write-Host "Erstellt: $SetupPath"
}

if (-not $SkipMsix) {
    if (-not (Test-Path -LiteralPath $MsixManifestTemplate)) {
        throw "MSIX-Manifest-Vorlage nicht gefunden: $MsixManifestTemplate"
    }
    $makeappx = Resolve-MakeAppxPath
    if (-not $makeappx) {
        throw "Windows SDK Tool (makeappx.exe) nicht gefunden. Installiere Windows 10/11 SDK oder nutze -SkipMsix."
    }

    $msixStage = Join-Path $PublishRoot 'msix-stage'
    if (Test-Path -LiteralPath $msixStage) {
        Remove-Item -LiteralPath $msixStage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $msixStage -Force | Out-Null

    try {
        Copy-Item -Path (Join-Path $PortableDir '*') -Destination $msixStage -Recurse -Force
        if (Test-Path -LiteralPath $MsixAssetsDir) {
            Copy-Item -LiteralPath $MsixAssetsDir -Destination (Join-Path $msixStage 'Assets') -Recurse -Force
        }

        [xml]$manifest = Get-Content -LiteralPath $MsixManifestTemplate -Encoding UTF8
        $manifest.Package.Identity.Version = ConvertTo-MsixVersion $Version
        if (-not [string]::IsNullOrWhiteSpace($MsixPackageName)) {
            $manifest.Package.Identity.Name = $MsixPackageName
        }
        if (-not [string]::IsNullOrWhiteSpace($MsixPublisher)) {
            $manifest.Package.Identity.Publisher = $MsixPublisher
        }
        if (-not [string]::IsNullOrWhiteSpace($MsixPublisherDisplayName)) {
            $manifest.Package.Properties.PublisherDisplayName = $MsixPublisherDisplayName
        }

        $manifestOut = Join-Path $msixStage 'AppxManifest.xml'
        $xmlSettings = New-Object System.Xml.XmlWriterSettings
        $xmlSettings.Indent = $true
        $xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($false)
        $writer = [System.Xml.XmlWriter]::Create($manifestOut, $xmlSettings)
        try {
            $manifest.Save($writer)
        }
        finally {
            $writer.Dispose()
        }

        if (Test-Path -LiteralPath $MsixPath) {
            Remove-Item -LiteralPath $MsixPath -Force
        }
        Write-Host "=== Baue MSIX-Paket (Version=$($manifest.Package.Identity.Version)) ==="
        & $makeappx pack /d $msixStage /p $MsixPath /o | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "makeappx pack fehlgeschlagen (ExitCode $LASTEXITCODE)."
        }
        Write-Host "Erstellt: $MsixPath"
    }
    finally {
        if (Test-Path -LiteralPath $msixStage) {
            Remove-Item -LiteralPath $msixStage -Recurse -Force
        }
    }
}

Write-Checksums
