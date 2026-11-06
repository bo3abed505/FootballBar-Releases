<#
.SYNOPSIS
    Reproducible Release Publishing Script for FootballTray WinUI 3 Desktop Client.
.DESCRIPTION
    Builds, publishes, packages, and hashes the self-contained x64 Windows App SDK application.
.PARAMETER Version
    Optional version string. Defaults to the version found in the root VERSION file.
.PARAMETER SkipArchive
    If specified, skips creating the .zip archive.
.PARAMETER UpdateDownloadUrl
    Optional HTTPS download URL for generating release-manifest.json for automatic update discovery.
#>

[CmdletBinding()]
param (
    [string]$Version,
    [switch]$SkipArchive,
    [string]$UpdateDownloadUrl,
    [string]$ReleaseDir
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Split-Path -Parent $ScriptDir
$VersionFile = Join-Path $RootDir "VERSION"

if ([string]::IsNullOrWhiteSpace($Version)) {
    if (Test-Path $VersionFile) {
        $Version = (Get-Content $VersionFile -Raw).Trim()
    } else {
        $Version = "1.0.0"
    }
}

# Reject SemVer build metadata in official publisher
if ($Version -match '\+') {
    Write-Error "CRITICAL: Release build metadata ('+...') is not supported by the FootballTray publisher."
    exit 1
}

# Derive numeric 3-part core for Win32 AssemblyVersion and FileVersion (fail closed if invalid)
if ($Version -match '^(\d+\.\d+\.\d+)(?:-.*)?$') {
    $numericCore = $Matches[1]
} else {
    Write-Error "CRITICAL: Release version '$Version' cannot produce a valid numeric Major.Minor.Patch core."
    exit 1
}
$assemblyFileVersion = "$numericCore.0"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray Release Publishing — v$Version" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseBaseDir = Join-Path $RootDir "artifacts\releases\v$Version"
} else {
    $ReleaseBaseDir = $ReleaseDir
}
$PublishDir = Join-Path $ReleaseBaseDir "win-x64"
$ProjectFile = Join-Path $RootDir "src\client\FootballTray.Client\FootballTray.Client.csproj"
$ArchiveFile = Join-Path $ReleaseBaseDir "FootballTray-v$Version-win-x64.zip"
$SetupExeFile = Join-Path $ReleaseBaseDir "FootballTray-Setup-v$Version-win-x64.exe"
$ChecksumFile = Join-Path $ReleaseBaseDir "SHA256SUMS.txt"
$ManifestFile = Join-Path $ReleaseBaseDir "release-manifest.json"

# Validate or default -UpdateDownloadUrl early (fail-fast)
$hasExplicitUpdateUrl = $PSBoundParameters.ContainsKey('UpdateDownloadUrl')
if ($hasExplicitUpdateUrl) {
    if ([string]::IsNullOrWhiteSpace($UpdateDownloadUrl)) {
        Write-Error "CRITICAL: -UpdateDownloadUrl was supplied but is empty or whitespace."
        exit 1
    }

    if ($SkipArchive) {
        Write-Error "CRITICAL: Cannot generate release manifest when -SkipArchive is specified."
        exit 1
    }

    $uri = $null
    if (-not [System.Uri]::TryCreate($UpdateDownloadUrl, [System.UriKind]::Absolute, [ref]$uri)) {
        Write-Error "CRITICAL: -UpdateDownloadUrl '$UpdateDownloadUrl' is not a valid absolute URI."
        exit 1
    }

    if ($uri.Scheme -ne [System.Uri]::UriSchemeHttps) {
        Write-Error "CRITICAL: -UpdateDownloadUrl must use HTTPS scheme. Insecure or non-web scheme '$($uri.Scheme)' is rejected."
        exit 1
    }

    $expectedZipName = [System.IO.Path]::GetFileName($ArchiveFile)
    $urlFileName = [System.IO.Path]::GetFileName($uri.AbsolutePath)
    if ($urlFileName -cne $expectedZipName) {
        Write-Error "CRITICAL: -UpdateDownloadUrl filename '$urlFileName' does not match the generated release archive filename '$expectedZipName'."
        exit 1
    }
    $hasUpdateUrl = $true
} else {
    if (-not $SkipArchive) {
        $UpdateDownloadUrl = "https://github.com/bo3abed505/FootballBar-Releases/releases/download/v$Version/FootballTray-v$Version-win-x64.zip"
        $hasUpdateUrl = $true
    } else {
        $hasUpdateUrl = $false
    }
}

function New-ReleaseManifest {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory=$true)][string]$Version,
        [Parameter(Mandatory=$true)][string]$DownloadUrl,
        [Parameter(Mandatory=$true)][string]$ArchiveFile,
        [Parameter(Mandatory=$false)][string]$ReleaseNotesFile,
        [Parameter(Mandatory=$true)][string]$ManifestFile,
        [string]$Architecture = "win-x64"
    )

    $MaxReleaseNotesLength = 10000

    if (-not (Test-Path -LiteralPath $ArchiveFile)) {
        throw "CRITICAL: Release archive not found at '$ArchiveFile'."
    }

    $zipHash = (Get-FileHash -LiteralPath $ArchiveFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $archiveSizeBytes = (Get-Item -LiteralPath $ArchiveFile).Length

    $releaseNotesContent = $null
    if ($ReleaseNotesFile -and (Test-Path -LiteralPath $ReleaseNotesFile)) {
        $releaseNotesContent = Get-Content -LiteralPath $ReleaseNotesFile -Raw
        if ($releaseNotesContent.Length -gt $MaxReleaseNotesLength) {
            throw "CRITICAL: Release notes length ($($releaseNotesContent.Length) characters) exceeds maximum allowed length of $MaxReleaseNotesLength characters."
        }
    }

    $publishedAt = (Get-Date).ToUniversalTime().ToString(
        "yyyy-MM-ddTHH:mm:ssZ",
        [System.Globalization.CultureInfo]::InvariantCulture
    )

    $manifestObj = [ordered]@{
        schemaVersion = "1.0"
        version       = $Version
        publishedAt   = $publishedAt
        architecture  = $Architecture
        downloadUrl   = $DownloadUrl
        sha256        = $zipHash
        sizeBytes     = $archiveSizeBytes
        releaseNotes  = $releaseNotesContent
    }

    $manifestJson = $manifestObj | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($ManifestFile, $manifestJson + "`n", [System.Text.UTF8Encoding]::new($false))
}

# If script is dot-sourced (e.g. for testing New-ReleaseManifest), do not execute the build workflow
if ($MyInvocation.InvocationName -eq '.') {
    return
}

# 1. Clean previous release artifacts for this version
Write-Host "`n[1/8] Cleaning previous publish artifacts..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
if (Test-Path $ArchiveFile) {
    Remove-Item -Path $ArchiveFile -Force
}
if (Test-Path $SetupExeFile) {
    Remove-Item -Path $SetupExeFile -Force
}
$msixFile = Join-Path $ReleaseBaseDir "FootballTray-v$Version-win-x64.msix"
if (Test-Path $msixFile) {
    Remove-Item -Path $msixFile -Force
}
if (Test-Path $ChecksumFile) {
    Remove-Item -Path $ChecksumFile -Force
}
if (Test-Path $ManifestFile) {
    Remove-Item -Path $ManifestFile -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# 2. Restore NuGet dependencies
Write-Host "`n[2/8] Restoring dependencies..." -ForegroundColor Yellow
$restoreOutput = dotnet restore $ProjectFile -r win-x64 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet restore failed with exit code $LASTEXITCODE`n$restoreOutput"
    exit 1
}

# 3. Publish self-contained x64 release
Write-Host "`n[3/8] Publishing self-contained x64 Release..." -ForegroundColor Yellow
$publishArgs = @(
    "publish", $ProjectFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishReadyToRun=false",
    "-p:PublishTrimmed=false",
    "-p:PublishSingleFile=false",
    "-p:Version=$Version",
    "-p:VersionPrefix=$numericCore",
    "-p:InformationalVersion=$Version",
    "-p:FileVersion=$assemblyFileVersion",
    "-p:AssemblyVersion=$assemblyFileVersion",
    "-o", $PublishDir
)

$publishOutput = dotnet @publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE`n$publishOutput"
    exit 1
}

# 4. Generate VERSION.txt and RELEASE_NOTES.txt
Write-Host "`n[4/8] Generating release metadata..." -ForegroundColor Yellow
$versionTxtPath = Join-Path $PublishDir "VERSION.txt"
$releaseNotesPath = Join-Path $PublishDir "RELEASE_NOTES.txt"

Set-Content -Path $versionTxtPath -Value "FootballTray v$Version`nRuntime: net10.0-windows10.0.26100.0`nArchitecture: win-x64 (Self-Contained)`nPackaging: Unpackaged WinUI 3 (Windows App SDK 2.4.0)`nBuild Date (UTC): $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ssZ'))"

$versionNotesFile = Join-Path $RootDir "release-notes" "v$Version.txt"
if (Test-Path -LiteralPath $versionNotesFile) {
    $releaseNotesContent = (Get-Content -LiteralPath $versionNotesFile -Raw).TrimEnd()
    Write-Host "  Loaded version-specific release notes from: release-notes/v$Version.txt" -ForegroundColor Green
} else {
    Write-Host "  No version-specific release notes found at: release-notes/v$Version.txt — using generic template." -ForegroundColor Gray
    $releaseNotesContent = @"
FootballTray v$Version
============================================================
Release — Native Windows Taskbar Football Tracker

Requirements:
- Windows 11 64-bit (or Windows 10 build 17763+)
- x64 processor
- FootballTray Backend Service (Local or Network-hosted)
"@
}

Set-Content -Path $releaseNotesPath -Value $releaseNotesContent

# Also copy VERSION.txt and RELEASE_NOTES.txt to release root
Copy-Item -Path $versionTxtPath -Destination (Join-Path $ReleaseBaseDir "VERSION.txt") -Force
Copy-Item -Path $releaseNotesPath -Destination (Join-Path $ReleaseBaseDir "RELEASE_NOTES.txt") -Force

# 5. Validate published output integrity
Write-Host "`n[5/8] Validating published output..." -ForegroundColor Yellow
$exePath = Join-Path $PublishDir "FootballTray.exe"
if (-not (Test-Path $exePath)) {
    Write-Error "CRITICAL: Published executable not found at '$exePath'!"
    exit 1
}

$requiredDlls = @(
    "FootballTray.dll",
    "FootballTray.Core.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.Windows.AppNotifications.Projection.dll"
)

foreach ($dll in $requiredDlls) {
    $dllPath = Join-Path $PublishDir $dll
    if (-not (Test-Path $dllPath)) {
        Write-Error "CRITICAL: Required runtime component '$dll' missing from publish directory!"
        exit 1
    }
}

# Ensure no test assemblies or source files leaked into publish folder
$forbiddenFiles = @(Get-ChildItem -Path $PublishDir -Recurse -Include "*.Tests.dll", "*.cs", "*.ts", ".env*")
if ($forbiddenFiles.Count -gt 0) {
    Write-Error "CRITICAL: Forbidden development/test files detected in publish folder:`n$($forbiddenFiles.FullName -join "`n")"
    exit 1
}

# 6. Create Zip Archive
if (-not $SkipArchive) {
    Write-Host "`n[6/8] Creating release archive: $ArchiveFile..." -ForegroundColor Yellow
    Compress-Archive -Path "$PublishDir\*" -DestinationPath $ArchiveFile -Force
    $archiveSizeMb = [math]::Round(((Get-Item $ArchiveFile).Length / 1MB), 2)
    Write-Host "Archive created successfully ($archiveSizeMb MB)." -ForegroundColor Green
} else {
    Write-Host "`n[6/8] Skipping archive creation (-SkipArchive was specified)." -ForegroundColor Gray
}
# 6b. Build One-Click Setup Executable Installer
if (-not $SkipArchive -and (Test-Path $ArchiveFile)) {
    Write-Host "`n[6b/9] Building one-click installer: $SetupExeFile..." -ForegroundColor Yellow
    $installerProject = Join-Path $RootDir "src\client\FootballTray.Installer\FootballTray.Installer.csproj"
    $installerStagingDir = Join-Path $ReleaseBaseDir "setup-staging"
    if (Test-Path $installerStagingDir) {
        Remove-Item -Path $installerStagingDir -Recurse -Force
    }
    
    & dotnet publish $installerProject -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:PayloadZip="$ArchiveFile" `
        -p:TreatWarningsAsErrors=true `
        -o $installerStagingDir

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to compile FootballTray-Setup installer executable (exit code $LASTEXITCODE)."
    }

    $builtSetupExe = Join-Path $installerStagingDir "FootballTray-Setup.exe"
    if (-not (Test-Path $builtSetupExe)) {
        throw "Expected setup binary not found at '$builtSetupExe'."
    }

    Copy-Item -Path $builtSetupExe -Destination $SetupExeFile -Force
    Remove-Item -Path $installerStagingDir -Recurse -Force -ErrorAction SilentlyContinue

    $setupSizeMb = [math]::Round(((Get-Item $SetupExeFile).Length / 1MB), 2)
    Write-Host "  One-click installer built successfully ($setupSizeMb MB)." -ForegroundColor Green
} else {
    Write-Host "`n[6b/9] Skipping setup installer build (archive missing or -SkipArchive specified)." -ForegroundColor Gray
}


# 7. Generate and Stamp OTA Telemetry Manifest
$otaSource = Join-Path $ScriptDir "ota-telemetry-manifest.json"
$otaDest = Join-Path $ReleaseBaseDir "ota-telemetry-manifest.json"
if (Test-Path $otaSource) {
    Write-Host "`n[7/9] Stamping OTA telemetry manifest: $otaDest..." -ForegroundColor Yellow
    $otaRaw = Get-Content -LiteralPath $otaSource -Raw
    $otaDoc = [System.Text.Json.JsonDocument]::Parse($otaRaw)
    $otaDict = [ordered]@{}
    foreach ($prop in $otaDoc.RootElement.EnumerateObject()) {
        if ($prop.Name -eq "manifestVersion") {
            $otaDict[$prop.Name] = $Version
        } elseif ($prop.Name -eq "updatedAt") {
            $otaDict[$prop.Name] = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", [System.Globalization.CultureInfo]::InvariantCulture)
        } else {
            $otaDict[$prop.Name] = ($prop.Value.GetRawText() | ConvertFrom-Json)
        }
    }
    $otaOutJson = $otaDict | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($otaDest, $otaOutJson + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Host "  OTA manifest stamped with version $Version." -ForegroundColor Green
}

# 7b. Build Microsoft Store MSIX Package
$buildMsixScript = Join-Path $ScriptDir "build-msix.ps1"
if (Test-Path $buildMsixScript) {
    Write-Host "`n[7b/9] Building Store MSIX package..." -ForegroundColor Yellow
    & $buildMsixScript -Version $Version -ReleaseDir $ReleaseBaseDir -PublishDir $PublishDir -SkipSign
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to build MSIX package (exit code $LASTEXITCODE)."
    }
}

# 8. Compute SHA-256 Checksums
Write-Host "`n[8/9] Generating SHA-256 checksums..." -ForegroundColor Yellow
$checksumEntries = [System.Collections.Generic.List[string]]::new()

if (Test-Path $ArchiveFile) {
    $zipHash = (Get-FileHash -Path $ArchiveFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries.Add("$zipHash  $([System.IO.Path]::GetFileName($ArchiveFile))")
    Write-Host "  $zipHash  $([System.IO.Path]::GetFileName($ArchiveFile))" -ForegroundColor Cyan
}

if (Test-Path $SetupExeFile) {
    $setupHash = (Get-FileHash -Path $SetupExeFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries.Add("$setupHash  $([System.IO.Path]::GetFileName($SetupExeFile))")
    Write-Host "  $setupHash  $([System.IO.Path]::GetFileName($SetupExeFile))" -ForegroundColor Cyan
}

$msixFile = Join-Path $ReleaseBaseDir "FootballTray-v$Version-win-x64.msix"
if (Test-Path $msixFile) {
    $msixHash = (Get-FileHash -Path $msixFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries.Add("$msixHash  $([System.IO.Path]::GetFileName($msixFile))")
    Write-Host "  $msixHash  $([System.IO.Path]::GetFileName($msixFile))" -ForegroundColor Cyan
}

$exeHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumEntries.Add("$exeHash  win-x64/FootballTray.exe")
Write-Host "  $exeHash  win-x64/FootballTray.exe" -ForegroundColor Cyan

if (Test-Path $otaDest) {
    $otaHash = (Get-FileHash -Path $otaDest -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries.Add("$otaHash  $([System.IO.Path]::GetFileName($otaDest))")
    Write-Host "  $otaHash  $([System.IO.Path]::GetFileName($otaDest))" -ForegroundColor Cyan
}

Set-Content -Path $ChecksumFile -Value ($checksumEntries -join "`n")

# 9. Generate Release Manifest (Optional)
if ($hasUpdateUrl) {
    Write-Host "`n[9/9] Generating release manifest: $ManifestFile..." -ForegroundColor Yellow
    try {
        $notesFile = $releaseNotesPath
        New-ReleaseManifest `
            -Version $Version `
            -DownloadUrl $UpdateDownloadUrl `
            -ArchiveFile $ArchiveFile `
            -ReleaseNotesFile $notesFile `
            -ManifestFile $ManifestFile `
            -Architecture "win-x64"
        Write-Host "  Manifest generated successfully: $ManifestFile" -ForegroundColor Green
    } catch {
        Write-Error "Failed to generate release manifest: $_"
        exit 1
    }
} else {
    Write-Host "`n[9/9] Skipping release manifest (-UpdateDownloadUrl was not specified)." -ForegroundColor Gray
}

# Final gate: run validate-release.ps1 with -RequireAutoUpdateInstaller so every new
# release is confirmed to carry install-client.ps1. Historical artifacts (pre-v1.1.0)
# that lack this file can still be validated without the switch.
Write-Host "`n[Post-publish] Running artifact validation gate..." -ForegroundColor Yellow
$ValidateScript = Join-Path $ScriptDir "validate-release.ps1"
if (Test-Path $ValidateScript) {
    & $ValidateScript -Version $Version -ReleaseDir $ReleaseBaseDir -RequireAutoUpdateInstaller
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Post-publish validation failed. Release artifacts may be incomplete."
        exit 1
    }
    Write-Host "  Artifact validation passed." -ForegroundColor Green
} else {
    Write-Warning "validate-release.ps1 not found at '$ValidateScript'. Skipping post-publish validation."
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " Release v$Version published successfully!" -ForegroundColor Green
Write-Host " Artifacts directory: $ReleaseBaseDir" -ForegroundColor Green
if ($hasUpdateUrl) {
    Write-Host " Manifest:            $ManifestFile" -ForegroundColor Green
}
Write-Host "============================================================" -ForegroundColor Green

exit 0
