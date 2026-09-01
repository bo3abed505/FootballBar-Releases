<#
.SYNOPSIS
    Reproducible Release Publishing Script for FootballTray WinUI 3 Desktop Client.
.DESCRIPTION
    Builds, publishes, packages, and hashes the self-contained x64 Windows App SDK application.
.PARAMETER Version
    Optional version string. Defaults to the version found in the root VERSION file.
.PARAMETER SkipArchive
    If specified, skips creating the .zip archive.
#>

[CmdletBinding()]
param (
    [string]$Version,
    [switch]$SkipArchive
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

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray Release Publishing — v$Version" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$ReleaseBaseDir = Join-Path $RootDir "artifacts\releases\v$Version"
$PublishDir = Join-Path $ReleaseBaseDir "win-x64"
$ProjectFile = Join-Path $RootDir "src\client\FootballTray.Client\FootballTray.Client.csproj"
$ArchiveFile = Join-Path $ReleaseBaseDir "FootballTray-v$Version-win-x64.zip"
$ChecksumFile = Join-Path $ReleaseBaseDir "SHA256SUMS.txt"

# 1. Clean previous release artifacts for this version
Write-Host "`n[1/7] Cleaning previous publish artifacts..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Path $PublishDir -Recurse -Force
}
if (Test-Path $ArchiveFile) {
    Remove-Item -Path $ArchiveFile -Force
}
if (Test-Path $ChecksumFile) {
    Remove-Item -Path $ChecksumFile -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

# 2. Restore NuGet dependencies
Write-Host "`n[2/7] Restoring dependencies..." -ForegroundColor Yellow
$restoreOutput = dotnet restore $ProjectFile -r win-x64 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet restore failed with exit code $LASTEXITCODE`n$restoreOutput"
    exit 1
}

# 3. Publish self-contained x64 release
Write-Host "`n[3/7] Publishing self-contained x64 Release..." -ForegroundColor Yellow
$publishArgs = @(
    "publish", $ProjectFile,
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishReadyToRun=false",
    "-p:PublishTrimmed=false",
    "-p:PublishSingleFile=false",
    "-o", $PublishDir
)

$publishOutput = dotnet @publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE`n$publishOutput"
    exit 1
}

# 4. Generate VERSION.txt and RELEASE_NOTES.txt
Write-Host "`n[4/7] Generating release metadata..." -ForegroundColor Yellow
$versionTxtPath = Join-Path $PublishDir "VERSION.txt"
$releaseNotesPath = Join-Path $PublishDir "RELEASE_NOTES.txt"

Set-Content -Path $versionTxtPath -Value "FootballTray v$Version`nRuntime: net10.0-windows10.0.26100.0`nArchitecture: win-x64 (Self-Contained)`nPackaging: Unpackaged WinUI 3 (Windows App SDK 2.4.0)`nBuild Date (UTC): $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ssZ'))"

$releaseNotesContent = @"
FootballTray v$Version
============================================================
Milestone 8 Release — Native Windows Taskbar Football Tracker

Features:
- Native WinUI 3 Desktop Application with Mica styling
- Native Windows 11 Taskbar Integration and Live Ticker
- Intelligent Acquisition Scheduler with Multi-Tier Polling
- ETag-Aware Caching and Instant Offline Match Discovery
- Windows App SDK Native Match Notifications (Toast Alerts)
- Comprehensive User Preferences (Teams, Competitions, Alerts)
- Fully Decoupled Architecture with Zero Secret Bundling

Requirements:
- Windows 11 64-bit (or Windows 10 build 17763+)
- x64 processor
- FootballTray Backend Service (Local or Network-hosted)
"@

Set-Content -Path $releaseNotesPath -Value $releaseNotesContent

# Also copy VERSION.txt and RELEASE_NOTES.txt to release root
Copy-Item -Path $versionTxtPath -Destination (Join-Path $ReleaseBaseDir "VERSION.txt") -Force
Copy-Item -Path $releaseNotesPath -Destination (Join-Path $ReleaseBaseDir "RELEASE_NOTES.txt") -Force

# 5. Validate published output integrity
Write-Host "`n[5/7] Validating published output..." -ForegroundColor Yellow
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
    Write-Host "`n[6/7] Creating release archive: $ArchiveFile..." -ForegroundColor Yellow
    Compress-Archive -Path "$PublishDir\*" -DestinationPath $ArchiveFile -Force
    $archiveSizeMb = [math]::Round(((Get-Item $ArchiveFile).Length / 1MB), 2)
    Write-Host "Archive created successfully ($archiveSizeMb MB)." -ForegroundColor Green
} else {
    Write-Host "`n[6/7] Skipping archive creation (-SkipArchive was specified)." -ForegroundColor Gray
}

# 7. Compute SHA-256 Checksums
Write-Host "`n[7/7] Generating SHA-256 checksums..." -ForegroundColor Yellow
$checksumEntries = [System.Collections.Generic.List[string]]::new()

if (Test-Path $ArchiveFile) {
    $zipHash = (Get-FileHash -Path $ArchiveFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumEntries.Add("$zipHash  $([System.IO.Path]::GetFileName($ArchiveFile))")
    Write-Host "  $zipHash  $([System.IO.Path]::GetFileName($ArchiveFile))" -ForegroundColor Cyan
}

$exeHash = (Get-FileHash -Path $exePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumEntries.Add("$exeHash  win-x64/FootballTray.exe")
Write-Host "  $exeHash  win-x64/FootballTray.exe" -ForegroundColor Cyan

Set-Content -Path $ChecksumFile -Value ($checksumEntries -join "`n")

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " Release v$Version published successfully!" -ForegroundColor Green
Write-Host " Artifacts directory: $ReleaseBaseDir" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green
exit 0
