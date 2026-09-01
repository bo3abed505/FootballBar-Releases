<#
.SYNOPSIS
    Release Artifact Validation Script for FootballTray.
.DESCRIPTION
    Inspects published output to ensure architecture correctness, runtime dependencies,
    version integrity, checksum verification, and absence of development/secret artifacts.
.PARAMETER Version
    Optional version string. Defaults to the version found in the root VERSION file.
.PARAMETER ReleaseDir
    Optional custom path to the release directory to validate.
#>

[CmdletBinding()]
param (
    [string]$Version,
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

if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $RootDir "artifacts\releases\v$Version"
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Validating FootballTray Release Artifacts — v$Version" -ForegroundColor Cyan
Write-Host " Target Directory: $ReleaseDir" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$PublishDir = Join-Path $ReleaseDir "win-x64"
$ExePath = Join-Path $PublishDir "FootballTray.exe"
$VersionTxtPath = Join-Path $PublishDir "VERSION.txt"
$ChecksumFilePath = Join-Path $ReleaseDir "SHA256SUMS.txt"
$ArchiveFilePath = Join-Path $ReleaseDir "FootballTray-v$Version-win-x64.zip"

$validationErrors = [System.Collections.Generic.List[string]]::new()

# 1. Verify Executable Exists
Write-Host "`n[1/7] Checking executable existence..." -ForegroundColor Yellow
if (-not (Test-Path $ExePath)) {
    $validationErrors.Add("Missing main executable: $ExePath")
} else {
    $exeSizeMb = [math]::Round(((Get-Item $ExePath).Length / 1MB), 2)
    Write-Host "  Found FootballTray.exe ($exeSizeMb MB)" -ForegroundColor Green
}

# 2. Verify Architecture is 64-bit
Write-Host "`n[2/7] Validating executable architecture (x64)..." -ForegroundColor Yellow
if (Test-Path $ExePath) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($ExePath)
        # Check PE signature offset at 0x3C
        $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
        $machine = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
        # 0x8664 = IMAGE_FILE_MACHINE_AMD64 (x64)
        if ($machine -eq 0x8664) {
            Write-Host "  Architecture verified: x64 (AMD64)" -ForegroundColor Green
        } else {
            $validationErrors.Add("Executable machine type 0x$($machine.ToString('X4')) is not x64 (expected 0x8664)")
        }
    } catch {
        $validationErrors.Add("Failed to inspect PE header of '$ExePath': $_")
    }
}

# 3. Verify Required Windows App SDK and Application DLLs
Write-Host "`n[3/7] Validating required runtime DLLs..." -ForegroundColor Yellow
$requiredDlls = @(
    "FootballTray.dll",
    "FootballTray.Core.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.Windows.AppNotifications.Projection.dll"
)

foreach ($dll in $requiredDlls) {
    $dllPath = Join-Path $PublishDir $dll
    if (Test-Path $dllPath) {
        Write-Host "  Found $dll" -ForegroundColor Green
    } else {
        $validationErrors.Add("Missing required runtime library: $dll")
    }
}

# 4. Verify VERSION.txt
Write-Host "`n[4/7] Checking VERSION.txt metadata..." -ForegroundColor Yellow
if (-not (Test-Path $VersionTxtPath)) {
    $validationErrors.Add("Missing VERSION.txt in publish folder")
} else {
    $versionContent = Get-Content $VersionTxtPath -Raw
    if ($versionContent -notmatch "v$Version") {
        $validationErrors.Add("VERSION.txt does not contain configured version '$Version'. Content:`n$versionContent")
    } else {
        Write-Host "  VERSION.txt matches version v$Version" -ForegroundColor Green
    }
}

# 5. Verify Checksums
Write-Host "`n[5/7] Verifying SHA-256 checksums..." -ForegroundColor Yellow
if (-not (Test-Path $ChecksumFilePath)) {
    $validationErrors.Add("Missing SHA256SUMS.txt in $ReleaseDir")
} else {
    $checksumLines = Get-Content $ChecksumFilePath
    foreach ($line in $checksumLines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Trim() -split "\s+", 2
        if ($parts.Length -eq 2) {
            $expectedHash = $parts[0].Trim().ToLowerInvariant()
            $relPath = $parts[1].Trim()
            $targetPath = Join-Path $ReleaseDir $relPath

            if (Test-Path $targetPath) {
                $actualHash = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualHash -eq $expectedHash) {
                    Write-Host "  [MATCH] $relPath ($actualHash)" -ForegroundColor Green
                } else {
                    $validationErrors.Add("Checksum mismatch for '$relPath'! Expected: $expectedHash, Actual: $actualHash")
                }
            } else {
                $validationErrors.Add("Referenced file in checksums not found: $targetPath")
            }
        }
    }
}

# 6. Security & Secret Boundary Audit
Write-Host "`n[6/7] Performing security boundary scan on release files..." -ForegroundColor Yellow
$forbiddenKeywords = @(
    "API_FOOTBALL_KEY",
    "x-apisports-key",
    "DATABASE_URL",
    "postgresql://",
    "postgres:your_password"
)

# Scan text, json, xml, config files in publish directory
$textFiles = @(Get-ChildItem -Path $PublishDir -Recurse -Include "*.json", "*.xml", "*.txt", "*.config", "*.manifest")
foreach ($file in $textFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($kw in $forbiddenKeywords) {
        if ($content.Contains($kw)) {
            $validationErrors.Add("SECURITY VIOLATION: Secret or credential keyword '$kw' found in published file '$($file.FullName)'")
        }
    }
}

# 7. Verify Absence of Test / Development Files
Write-Host "`n[7/7] Verifying absence of test and development files..." -ForegroundColor Yellow
$leakedDevFiles = @(Get-ChildItem -Path $PublishDir -Recurse -Include "*.Tests.dll", "*.cs", "*.ts", ".env*", "*.mock-state.json")
if ($leakedDevFiles.Count -gt 0) {
    foreach ($lf in $leakedDevFiles) {
        $validationErrors.Add("Leaked development file in publish folder: $($lf.FullName)")
    }
} else {
    Write-Host "  Zero development/test artifacts detected in release output." -ForegroundColor Green
}

Write-Host "`n============================================================" -ForegroundColor Cyan
if ($validationErrors.Count -eq 0) {
    Write-Host " VALIDATION PASSED: Release artifact is complete, secure, and valid!" -ForegroundColor Green
    Write-Host "============================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host " VALIDATION FAILED with $($validationErrors.Count) error(s):" -ForegroundColor Red
    foreach ($err in $validationErrors) {
        Write-Host "  - $err" -ForegroundColor Red
    }
    Write-Host "============================================================" -ForegroundColor Red
    exit 1
}
