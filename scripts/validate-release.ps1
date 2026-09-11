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
.PARAMETER RequireAutoUpdateInstaller
    When set, validates that install-client.ps1 is present in the publish directory.
    Omit this switch when validating historical release artifacts that pre-date the
    auto-update installer (e.g. v1.0.0).
#>

[CmdletBinding()]
param (
    [string]$Version,
    [string]$ReleaseDir,
    [switch]$RequireAutoUpdateInstaller
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
Write-Host "`n[1/8] Checking executable existence..." -ForegroundColor Yellow
if (-not (Test-Path $ExePath)) {
    $validationErrors.Add("Missing main executable: $ExePath")
} else {
    $exeSizeMb = [math]::Round(((Get-Item $ExePath).Length / 1MB), 2)
    Write-Host "  Found FootballTray.exe ($exeSizeMb MB)" -ForegroundColor Green
}

# 2. Verify Architecture is 64-bit
Write-Host "`n[2/8] Validating executable architecture (x64)..." -ForegroundColor Yellow
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

# 3. Verify Required Windows App SDK, Application DLLs, and Installer
Write-Host "`n[3/8] Validating required runtime DLLs and installer..." -ForegroundColor Yellow
$requiredFiles = @(
    "FootballTray.dll",
    "FootballTray.Core.dll",
    "Microsoft.WinUI.dll",
    "Microsoft.WindowsAppRuntime.Bootstrap.dll",
    "Microsoft.Windows.AppNotifications.Projection.dll"
)

# install-client.ps1 is required for auto-update-capable releases (v1.1.0+).
# Historical artifacts (e.g. v1.0.0) pre-date this component; only enforce
# when the caller explicitly requests it via -RequireAutoUpdateInstaller.
if ($RequireAutoUpdateInstaller) {
    $requiredFiles += "install-client.ps1"
}

foreach ($req in $requiredFiles) {
    $filePath = Join-Path $PublishDir $req
    if (Test-Path $filePath) {
        Write-Host "  Found $req" -ForegroundColor Green
    } else {
        $validationErrors.Add("Missing required release component: $req")
    }
}

# 4. Verify VERSION.txt
Write-Host "`n[4/8] Checking VERSION.txt metadata..." -ForegroundColor Yellow
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
Write-Host "`n[5/8] Verifying SHA-256 checksums..." -ForegroundColor Yellow
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
Write-Host "`n[6/8] Performing security boundary scan on release files..." -ForegroundColor Yellow
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
Write-Host "`n[7/8] Verifying absence of test and development files..." -ForegroundColor Yellow
$leakedDevFiles = @(Get-ChildItem -Path $PublishDir -Recurse -Include "*.Tests.dll", "*.cs", "*.ts", ".env*", "*.mock-state.json")
if ($leakedDevFiles.Count -gt 0) {
    foreach ($lf in $leakedDevFiles) {
        $validationErrors.Add("Leaked development file in publish folder: $($lf.FullName)")
    }
} else {
    Write-Host "  Zero development/test artifacts detected in release output." -ForegroundColor Green
}

# 8. Verify Release Manifest (if present)
$ManifestFilePath = Join-Path $ReleaseDir "release-manifest.json"
Write-Host "`n[8/8] Checking release manifest (optional)..." -ForegroundColor Yellow
if (-not (Test-Path $ManifestFilePath)) {
    Write-Host "  No release-manifest.json found (optional artifact; skipped)." -ForegroundColor Gray
} else {
    try {
        $manifestJson = Get-Content -Path $ManifestFilePath -Raw
        $doc = [System.Text.Json.JsonDocument]::Parse($manifestJson)
        try {
            $root = $doc.RootElement
            $props = [System.Collections.Generic.Dictionary[string, System.Text.Json.JsonElement]]::new([System.StringComparer]::OrdinalIgnoreCase)
            foreach ($p in $root.EnumerateObject()) {
                $props[$p.Name] = $p.Value
            }

            # 1. Schema Version
            if (-not $props.ContainsKey("schemaVersion") -or $props["schemaVersion"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: schemaVersion is missing or not a string.")
            } else {
                $schemaVer = $props["schemaVersion"].GetString()
                if ($schemaVer -cne "1.0") {
                    $validationErrors.Add("release-manifest.json: Unsupported schemaVersion '$schemaVer'. Expected '1.0'.")
                }
            }

            # 2. Version
            if (-not $props.ContainsKey("version") -or $props["version"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: version is missing or not a string.")
            } else {
                $ver = $props["version"].GetString()
                if ($ver -cne $Version) {
                    $validationErrors.Add("release-manifest.json: Version '$ver' does not match expected release version '$Version'.")
                }
            }

            # 3. Architecture
            if (-not $props.ContainsKey("architecture") -or $props["architecture"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: architecture is missing or not a string.")
            } else {
                $arch = $props["architecture"].GetString()
                if ($arch -cne "win-x64") {
                    $validationErrors.Add("release-manifest.json: Incompatible architecture '$arch'. Expected 'win-x64'.")
                }
            }

            # 4. Download URL (absolute HTTPS, matching archive filename)
            if (-not $props.ContainsKey("downloadUrl") -or $props["downloadUrl"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: downloadUrl is missing or not a string.")
            } else {
                $dlUrl = $props["downloadUrl"].GetString()
                $uri = $null
                if ([string]::IsNullOrWhiteSpace($dlUrl) -or -not [System.Uri]::TryCreate($dlUrl, [System.UriKind]::Absolute, [ref]$uri)) {
                    $validationErrors.Add("release-manifest.json: downloadUrl '$dlUrl' is not a valid absolute URI.")
                } elseif ($uri.Scheme -ne [System.Uri]::UriSchemeHttps) {
                    $validationErrors.Add("release-manifest.json: downloadUrl must use HTTPS scheme. Insecure scheme '$($uri.Scheme)' is rejected.")
                } else {
                    $expectedZipName = [System.IO.Path]::GetFileName($ArchiveFilePath)
                    $urlFileName = [System.IO.Path]::GetFileName($uri.AbsolutePath)
                    if ($urlFileName -cne $expectedZipName) {
                        $validationErrors.Add("release-manifest.json: downloadUrl filename '$urlFileName' does not match archive filename '$expectedZipName'.")
                    } else {
                        Write-Host "  Manifest downloadUrl matches archive filename: $urlFileName" -ForegroundColor Green
                    }
                }
            }

            # 5. SHA-256 Checksum (valid hex and matches actual ZIP)
            if (-not $props.ContainsKey("sha256") -or $props["sha256"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: sha256 is missing or not a string.")
            } else {
                $sha = $props["sha256"].GetString()
                if ([string]::IsNullOrWhiteSpace($sha) -or $sha -notmatch '^[a-fA-F0-9]{64}$') {
                    $validationErrors.Add("release-manifest.json: sha256 must be exactly 64 hexadecimal characters. Received '$sha'.")
                } elseif (Test-Path $ArchiveFilePath) {
                    $actualZipHash = (Get-FileHash -Path $ArchiveFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
                    if ($sha.ToLowerInvariant() -ne $actualZipHash) {
                        $validationErrors.Add("release-manifest.json: sha256 mismatch! Manifest: $sha, Actual ZIP: $actualZipHash")
                    } else {
                        Write-Host "  Manifest SHA-256 matches release archive ($actualZipHash)" -ForegroundColor Green
                    }
                }
            }

            # 6. Size Bytes (matches actual ZIP byte length)
            if (-not $props.ContainsKey("sizeBytes") -or $props["sizeBytes"].ValueKind -ne [System.Text.Json.JsonValueKind]::Number) {
                $validationErrors.Add("release-manifest.json: sizeBytes is missing or not a number.")
            } else {
                $sizeBytes = $props["sizeBytes"].GetInt64()
                if (Test-Path $ArchiveFilePath) {
                    $actualSizeBytes = (Get-Item $ArchiveFilePath).Length
                    if ($sizeBytes -ne $actualSizeBytes) {
                        $validationErrors.Add("release-manifest.json: sizeBytes mismatch! Manifest: $sizeBytes, Actual ZIP: $actualSizeBytes")
                    } else {
                        Write-Host "  Manifest sizeBytes matches release archive ($actualSizeBytes bytes)" -ForegroundColor Green
                    }
                }
            }

            # 7. PublishedAt (explicit timezone indication and valid ISO 8601)
            if (-not $props.ContainsKey("publishedAt") -or $props["publishedAt"].ValueKind -ne [System.Text.Json.JsonValueKind]::String) {
                $validationErrors.Add("release-manifest.json: publishedAt is missing or not a string.")
            } else {
                $pubAt = $props["publishedAt"].GetString()
                if ([string]::IsNullOrWhiteSpace($pubAt)) {
                    $validationErrors.Add("release-manifest.json: publishedAt is missing or empty.")
                } else {
                    $trimmedPubAt = $pubAt.Trim()
                    if ($trimmedPubAt -notmatch '(?:[zZ]|[+-]\d{2}(?::?\d{2})?)$') {
                        $validationErrors.Add("release-manifest.json: publishedAt '$pubAt' must include an explicit UTC 'Z' or timezone offset.")
                    } else {
                        $dto = [System.DateTimeOffset]::MinValue
                        $styles = [System.Globalization.DateTimeStyles]::RoundtripKind -bor [System.Globalization.DateTimeStyles]::AllowWhiteSpaces
                        if (-not [System.DateTimeOffset]::TryParse($trimmedPubAt, [System.Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$dto)) {
                            $validationErrors.Add("release-manifest.json: publishedAt '$pubAt' is not a valid ISO 8601 timestamp.")
                        } else {
                            Write-Host "  Manifest publishedAt is valid explicit-offset timestamp: $pubAt" -ForegroundColor Green
                        }
                    }
                }
            }

            # 8. Release Notes (optional, bounded length, string or null)
            if ($props.ContainsKey("releaseNotes")) {
                $rnElem = $props["releaseNotes"]
                if ($rnElem.ValueKind -eq [System.Text.Json.JsonValueKind]::String) {
                    $rn = $rnElem.GetString()
                    if ($rn -and $rn.Length -gt 10000) {
                        $validationErrors.Add("release-manifest.json: releaseNotes exceeds maximum allowed length of 10000 characters ($($rn.Length)).")
                    }
                } elseif ($rnElem.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
                    $validationErrors.Add("release-manifest.json: releaseNotes must be a string or null if present. Found '$($rnElem.ValueKind)'.")
                }
            }
        } finally {
            $doc.Dispose()
        }
    } catch {
        $validationErrors.Add("release-manifest.json: Failed to parse or inspect manifest: $_")
    }
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
