<#
.SYNOPSIS
    Per-User Client Installer and Updater for FootballTray WinUI 3 Desktop Client.
.DESCRIPTION
    Installs or updates the FootballTray client into %LOCALAPPDATA%\Programs\FootballTray.
    Requires ZERO administrator privileges. Preserves user data under %LOCALAPPDATA%\FootballTray.
.PARAMETER SourceDir
    Directory containing the self-contained win-x64 client release payload.
    Defaults to the canonical validated build for the version declared in VERSION.
.PARAMETER TargetDir
    Installation directory. Defaults to %LOCALAPPDATA%\Programs\FootballTray.
.PARAMETER UserDataDir
    User data directory to preserve. Defaults to %LOCALAPPDATA%\FootballTray.
.PARAMETER StartMenuDir
    Start Menu directory for shortcuts. Defaults to per-user Programs folder.
.PARAMETER StartupDir
    Startup folder for optional autostart shortcut. Defaults to per-user Startup folder.
.PARAMETER UninstallRegKey
    HKCU registry path for uninstaller. Defaults to HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballTray.
.PARAMETER StartWithWindows
    If specified, creates a shortcut in the current user's Startup folder.
.PARAMETER Launch
    If specified, launches FootballTray.exe after installation completes.
.PARAMETER AllowDowngrade
    If specified, permits installing an older version over a newer version.
.PARAMETER WhatIf
    Simulates the installation sequence without modifying the filesystem or registry.
.PARAMETER SimulatePostActivationFailure
    Internal test hook to verify transaction rollback on post-activation failure.
.PARAMETER SimulateFailureStep
    Internal test hook to verify transaction rollback on a specific integration step
    ("Activation", "StartMenu", "Startup", "Registry", "FinalVerify").
#>

[CmdletBinding()]
param (
    [string]$SourceDir,
    [string]$TargetDir,
    [string]$UserDataDir,
    [string]$StartMenuDir,
    [string]$StartupDir,
    [string]$UninstallRegKey,
    [switch]$StartWithWindows,
    [switch]$Launch,
    [switch]$AllowDowngrade,
    [switch]$WhatIf,
    [int]$WaitForProcessId = 0,
    [switch]$SimulatePostActivationFailure,
    [string]$SimulateFailureStep
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# ----------------------------------------------------------------------
# 1. Target and Data Path Definitions & Invariant Protection
# ----------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $env:LOCALAPPDATA "Programs\FootballTray"
}
if ([string]::IsNullOrWhiteSpace($UserDataDir)) {
    $UserDataDir = Join-Path $env:LOCALAPPDATA "FootballTray"
}
if ([string]::IsNullOrWhiteSpace($StartMenuDir)) {
    $StartMenuDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
}
if ([string]::IsNullOrWhiteSpace($StartupDir)) {
    $StartupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)
}
if ([string]::IsNullOrWhiteSpace($UninstallRegKey)) {
    $UninstallRegKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballTray"
}

$StagingDir = "$TargetDir.__staging"
$BackupDir = "$TargetDir.__backup"

$StartMenuShortcut = Join-Path $StartMenuDir "FootballTray.lnk"
$StartupShortcut = Join-Path $StartupDir "FootballTray.lnk"

function Assert-NotUserDataPath {
    param (
        [string]$Path,
        [string]$Operation
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $normPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $normUser = [System.IO.Path]::GetFullPath($UserDataDir).TrimEnd('\')

    if ([string]::Equals($normPath, $normUser, [System.StringComparison]::OrdinalIgnoreCase) -or
        $normPath.StartsWith("$normUser\", [System.StringComparison]::OrdinalIgnoreCase) -or
        $normUser.StartsWith("$normPath\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "CRITICAL INVARIANT VIOLATION: Cannot $Operation user data directory or ancestor/descendant: '$Path' (UserDataDir: '$UserDataDir')."
    }
}

# Enforce invariants on target, staging, and backup paths
Assert-NotUserDataPath -Path $TargetDir -Operation "target"
Assert-NotUserDataPath -Path $StagingDir -Operation "use as staging"
Assert-NotUserDataPath -Path $BackupDir -Operation "use as backup"

# ----------------------------------------------------------------------
# 2. Pure Helper Functions (Testable)
# ----------------------------------------------------------------------
function Get-RelativeChildPath {
    param (
        [string]$BasePath,
        [string]$FullPath
    )
    if ([string]::IsNullOrWhiteSpace($BasePath)) {
        throw "Base path cannot be empty."
    }
    if ([string]::IsNullOrWhiteSpace($FullPath)) {
        throw "Full path cannot be empty."
    }
    if (-not [System.IO.Path]::IsPathRooted($BasePath)) {
        throw "CRITICAL: Base path '$BasePath' must be an absolute rooted path."
    }
    if (-not [System.IO.Path]::IsPathRooted($FullPath)) {
        throw "CRITICAL: Full path '$FullPath' must be an absolute rooted path."
    }

    # Canonicalize and enforce strict trailing separator boundary on base directory
    $normBase = [System.IO.Path]::GetFullPath($BasePath).TrimEnd('\') + '\'
    $normFull = [System.IO.Path]::GetFullPath($FullPath)

    if (-not $normFull.StartsWith($normBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "CRITICAL: Path '$FullPath' is not beneath base directory '$BasePath'."
    }

    $rel = $normFull.Substring($normBase.Length)
    if ([string]::IsNullOrWhiteSpace($rel) -or [System.IO.Path]::IsPathRooted($rel) -or $rel -match '(?:^|[\\/])\.\.(?:[\\/]|$)') {
        throw "CRITICAL: Invalid relative child path resolved: '$rel'."
    }
    return $rel
}

function Resolve-CompanionUninstaller {
    param (
        [string]$SourceDir
    )
    if ([string]::IsNullOrWhiteSpace($SourceDir)) {
        throw "CRITICAL: Source directory cannot be empty."
    }
    if (-not [System.IO.Path]::IsPathRooted($SourceDir)) {
        throw "CRITICAL: Source directory '$SourceDir' must be an absolute rooted path."
    }

    $canonicalSource = [System.IO.Path]::GetFullPath($SourceDir).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $canonicalSource -PathType Container)) {
        throw "CRITICAL: Source directory does not exist or is not a directory: '$canonicalSource'."
    }

    $candidatePath = Join-Path $canonicalSource "uninstall-client.ps1"
    $canonicalCandidate = [System.IO.Path]::GetFullPath($candidatePath)

    # Ensure canonicalCandidate is strictly beneath canonicalSource with directory separator boundary
    $baseWithSep = $canonicalSource + [System.IO.Path]::DirectorySeparatorChar
    if (-not $canonicalCandidate.StartsWith($baseWithSep, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "CRITICAL: Companion uninstaller path '$canonicalCandidate' is not beneath source directory '$canonicalSource'."
    }

    if (-not (Test-Path -LiteralPath $canonicalCandidate)) {
        throw "CRITICAL: Required companion uninstaller script was not found at '$canonicalCandidate'."
    }

    $item = Get-Item -LiteralPath $canonicalCandidate
    if ($item.PSIsContainer) {
        throw "CRITICAL: Companion uninstaller path '$canonicalCandidate' is a directory, not a file."
    }

    if ($item.Length -eq 0) {
        throw "CRITICAL: Companion uninstaller script at '$canonicalCandidate' is empty."
    }

    return $canonicalCandidate
}

function Test-IsX64Executable {
    param ([string]$FilePath)
    if (-not (Test-Path -LiteralPath $FilePath)) { return $false }
    try {
        $stream = [System.IO.File]::OpenRead($FilePath)
        $reader = New-Object System.IO.BinaryReader($stream)
        $mz = $reader.ReadUInt16()
        if ($mz -ne 0x5A4D) { $stream.Dispose(); return $false }
        $stream.Seek(0x3C, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peOffset = $reader.ReadInt32()
        $stream.Seek($peOffset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $peSig = $reader.ReadUInt32() # "PE\0\0"
        if ($peSig -ne 0x00004550) { $stream.Dispose(); return $false }
        $machine = $reader.ReadUInt16() # 0x8664 = IMAGE_FILE_MACHINE_AMD64
        $stream.Dispose()
        return ($machine -eq 0x8664)
    } catch {
        return $false
    }
}

function Parse-SemVer {
    param ([string]$VersionString)
    if ([string]::IsNullOrWhiteSpace($VersionString)) {
        throw "Version string is null or empty."
    }
    $trimmed = $VersionString.Trim()
    $pattern = '^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+([0-9A-Za-z.-]+))?$'
    if ($trimmed -notmatch $pattern) {
        throw "Invalid semantic version format: '$VersionString'. Expected format like '1.0.0', 'v1.0.0', '1.0.0-beta', or '1.0.0-rc.1'."
    }
    return [PSCustomObject]@{
        Raw        = $trimmed
        Clean      = ($trimmed -replace '^v', '')
        Major      = [int]$matches[1]
        Minor      = [int]$matches[2]
        Patch      = [int]$matches[3]
        Prerelease = if ($matches[4]) { $matches[4] } else { $null }
        Build      = if ($matches[5]) { $matches[5] } else { $null }
    }
}

function Compare-SemVer {
    param (
        [string]$Ver1,
        [string]$Ver2
    )
    $p1 = Parse-SemVer -VersionString $Ver1
    $p2 = Parse-SemVer -VersionString $Ver2

    # 1. Compare Major
    if ($p1.Major -ne $p2.Major) {
        return ($p1.Major - $p2.Major)
    }

    # 2. Compare Minor
    if ($p1.Minor -ne $p2.Minor) {
        return ($p1.Minor - $p2.Minor)
    }

    # 3. Compare Patch
    if ($p1.Patch -ne $p2.Patch) {
        return ($p1.Patch - $p2.Patch)
    }

    # 4. Compare Prerelease
    # Stable release (no prerelease) is newer than any prerelease of the same base version
    if ($null -eq $p1.Prerelease -and $null -ne $p2.Prerelease) {
        return 1
    }
    if ($null -ne $p1.Prerelease -and $null -eq $p2.Prerelease) {
        return -1
    }
    if ($null -eq $p1.Prerelease -and $null -eq $p2.Prerelease) {
        return 0
    }

    # Both have prerelease: split by '.' and compare identifiers
    $parts1 = $p1.Prerelease.Split('.')
    $parts2 = $p2.Prerelease.Split('.')
    $minLen = [Math]::Min($parts1.Length, $parts2.Length)

    for ($i = 0; $i -lt $minLen; $i++) {
        $id1 = $parts1[$i]
        $id2 = $parts2[$i]

        $isNum1 = [int]::TryParse($id1, [ref]$null)
        $isNum2 = [int]::TryParse($id2, [ref]$null)

        if ($isNum1 -and $isNum2) {
            $n1 = [int]$id1
            $n2 = [int]$id2
            if ($n1 -ne $n2) {
                return ($n1 - $n2)
            }
        } elseif ($isNum1 -and -not $isNum2) {
            # Numeric identifiers have lower precedence than non-numeric
            return -1
        } elseif (-not $isNum1 -and $isNum2) {
            return 1
        } else {
            # Both non-numeric: lexical ASCII compare
            $cmp = [string]::Compare($id1, $id2, [System.StringComparison]::Ordinal)
            if ($cmp -ne 0) {
                return $cmp
            }
        }
    }

    return ($parts1.Length - $parts2.Length)
}

function Get-PayloadVersion {
    param ([string]$Dir)
    if (-not (Test-Path -LiteralPath $Dir)) {
        throw "Cannot determine payload version: directory does not exist: '$Dir'."
    }

    $versionTxtCandidate = $null
    # 1. Try VERSION.txt
    $versionTxt = Join-Path $Dir "VERSION.txt"
    if (Test-Path -LiteralPath $versionTxt) {
        $content = Get-Content -LiteralPath $versionTxt -Raw
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            foreach ($line in ($content -split "`r?`n")) {
                if ($line -match "(?:FootballTray\s+)?v?([0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)") {
                    $candidate = $matches[1]
                    try {
                        $parsed = Parse-SemVer -VersionString $candidate
                        $versionTxtCandidate = $parsed.Clean
                        break
                    } catch {}
                }
            }
        }
    }

    $peCandidate = $null
    # 2. Try PE ProductVersion or FileVersion
    $exePath = Join-Path $Dir "FootballTray.exe"
    if (Test-Path -LiteralPath $exePath) {
        try {
            $vi = (Get-Item -LiteralPath $exePath).VersionInfo
            if (-not [string]::IsNullOrWhiteSpace($vi.ProductVersion)) {
                try {
                    $parsed = Parse-SemVer -VersionString $vi.ProductVersion
                    $peCandidate = $parsed.Clean
                } catch {}
            }
            if ($null -eq $peCandidate -and -not [string]::IsNullOrWhiteSpace($vi.FileVersion)) {
                try {
                    $parsed = Parse-SemVer -VersionString $vi.FileVersion
                    $peCandidate = $parsed.Clean
                } catch {}
            }
        } catch {}
    }

    # If both VERSION.txt and PE version exist, enforce consistency (ignoring build metadata)
    if ($null -ne $versionTxtCandidate -and $null -ne $peCandidate) {
        $normTxt = $versionTxtCandidate -replace '\+.*$', ''
        $normPe = $peCandidate -replace '\+.*$', ''
        if (-not [string]::Equals($normTxt, $normPe, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "CRITICAL: Payload version mismatch in '$Dir'. VERSION.txt specifies '$versionTxtCandidate' but FootballTray.exe specifies '$peCandidate'."
        }
        return $normTxt
    }

    if ($null -ne $versionTxtCandidate) {
        return ($versionTxtCandidate -replace '\+.*$', '')
    }

    if ($null -ne $peCandidate) {
        return ($peCandidate -replace '\+.*$', '')
    }

    # Fail closed: never silently invent a version
    throw "CRITICAL: Unable to determine valid semantic version for payload in '$Dir'. Ensure VERSION.txt or PE version info exists with valid SemVer (e.g. 1.0.0, 1.0.0-beta)."
}

function Stop-RunningClient {
    param ([int]$TimeoutSeconds = 5)
    $procs = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    Write-Host "Detected $($procs.Count) running FootballTray process(es). Requesting graceful exit..." -ForegroundColor Yellow
    foreach ($p in $procs) {
        try {
            $p.CloseMainWindow() | Out-Null
        } catch {}
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $remaining = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }

    $remaining = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
    if ($remaining.Count -gt 0) {
        Write-Host "Process did not exit within ${TimeoutSeconds}s. Terminating..." -ForegroundColor Yellow
        foreach ($p in $remaining) {
            try {
                $p.Kill()
                $p.WaitForExit(2000)
            } catch {}
        }
    }

    $stillRunning = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
    if ($stillRunning.Count -gt 0) {
        throw "CRITICAL: Unable to terminate running FootballTray process (PID(s): $($stillRunning.Id -join ', ')). Aborting installation."
    }
    Write-Host "All FootballTray processes stopped." -ForegroundColor Green
}

# ----------------------------------------------------------------------
# 3. Deterministic Payload Selection (Fail-Closed)
# ----------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $rootDir = Split-Path -Parent $scriptDir
    $versionFile = Join-Path $rootDir "VERSION"

    if (-not (Test-Path -LiteralPath $versionFile)) {
        throw "CRITICAL: Intended version file not found at '$versionFile'. Specify -SourceDir explicitly."
    }

    $intendedVerRaw = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($intendedVerRaw)) {
        throw "CRITICAL: Intended version file at '$versionFile' is empty. Specify -SourceDir explicitly."
    }

    # Validate intended version format
    $parsedIntended = Parse-SemVer -VersionString $intendedVerRaw
    $intendedVer = $parsedIntended.Clean

    # Strict canonical payload directory matching intended version
    $canonicalPayload = Join-Path $rootDir "artifacts\releases\v$intendedVer\win-x64"

    if (-not (Test-Path -LiteralPath $canonicalPayload)) {
        throw "CRITICAL: Intended release payload for v$intendedVer was not found at '$canonicalPayload'. Run publish-release.ps1 or specify -SourceDir."
    }

    $SourceDir = $canonicalPayload
}

$SourceDir = [System.IO.Path]::GetFullPath($SourceDir)

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray - Windows Client Per-User Installer" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Source Directory:       $SourceDir"
Write-Host " Target Directory:       $TargetDir"
Write-Host " User Data Directory:    $UserDataDir (Preserved)"
if ($WhatIf) {
    Write-Host " Mode:                   WHAT-IF (Simulation only)" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------
# 4. Pre-Flight Verification
# ----------------------------------------------------------------------
Write-Host "`n[1/5] Validating source payload..." -ForegroundColor Yellow
if (-not (Test-Path -LiteralPath $SourceDir)) {
    throw "Source directory does not exist: '$SourceDir'."
}

$sourceExe = Join-Path $SourceDir "FootballTray.exe"
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Source directory missing required executable 'FootballTray.exe': '$SourceDir'."
}

# Architecture check
if (-not (Test-IsX64Executable -FilePath $sourceExe)) {
    throw "Executable '$sourceExe' is not a valid 64-bit (x64) PE image."
}

# Refuse Debug builds
if ($SourceDir -like "*\Debug\*" -or $SourceDir -like "*\bin\Debug*") {
    throw "Refusing to install from a Debug build path: '$SourceDir'. Use a published Release payload."
}

# Check essential runtime dependencies
$requiredFiles = @("FootballTray.dll", "FootballTray.Core.dll", "Microsoft.WinUI.dll")
foreach ($req in $requiredFiles) {
    if (-not (Test-Path (Join-Path $SourceDir $req))) {
        throw "Source payload is incomplete; missing required component '$req'."
    }
}

# Version extraction & downgrade protection (fails closed)
$sourceVersion = Get-PayloadVersion -Dir $SourceDir
Write-Host "  Source Payload Version: v$sourceVersion (x64 Self-Contained)" -ForegroundColor Green

$isUpgrade = Test-Path -LiteralPath (Join-Path $TargetDir "FootballTray.exe")
if ($isUpgrade) {
    $installedVersion = Get-PayloadVersion -Dir $TargetDir
    Write-Host "  Detected existing installation: v$installedVersion" -ForegroundColor Gray
    $cmp = Compare-SemVer -Ver1 $installedVersion -Ver2 $sourceVersion
    if ($cmp -gt 0 -and -not $AllowDowngrade) {
        throw "CRITICAL: Downgrade rejected. Installed version '$installedVersion' is newer than source version '$sourceVersion'. Specify -AllowDowngrade to override."
    }
}

# ----------------------------------------------------------------------
# 5. Staging & Atomic Swap Simulation / Execution
# ----------------------------------------------------------------------
Write-Host "`n[2/5] Preparing installation staging..." -ForegroundColor Yellow

# If WaitForProcessId is specified, wait for the caller process to exit gracefully
if ($WaitForProcessId -gt 0) {
    Write-Host "Waiting for calling process (PID: $WaitForProcessId) to exit..." -ForegroundColor Yellow
    $swWait = [System.Diagnostics.Stopwatch]::StartNew()
    $waitedProcessExited = $false
    while ($swWait.Elapsed.TotalSeconds -lt 15) {
        $p = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
        if ($null -eq $p -or $p.HasExited) {
            $waitedProcessExited = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not $waitedProcessExited) {
        throw "CRITICAL: Calling process (PID: $WaitForProcessId) did not exit within 15 seconds. Aborting installation to prevent corrupting running application."
    }
    Write-Host "Calling process exited gracefully." -ForegroundColor Green
}

if ($WhatIf) {
    Write-Host "  [WhatIf] Would stop running FootballTray processes (if any)." -ForegroundColor Gray
    Write-Host "  [WhatIf] Would stage files from '$SourceDir' to '$StagingDir'." -ForegroundColor Gray
    Write-Host "  [WhatIf] Would swap '$StagingDir' into '$TargetDir' (backing up previous version)." -ForegroundColor Gray
    Write-Host "  [WhatIf] Would create Start Menu shortcut at '$StartMenuShortcut'." -ForegroundColor Gray
    if ($StartWithWindows) {
        Write-Host "  [WhatIf] Would create Startup shortcut at '$StartupShortcut'." -ForegroundColor Gray
    }
    Write-Host "  [WhatIf] Would register uninstall entry in '$UninstallRegKey'." -ForegroundColor Gray
    Write-Host "`n[WhatIf] Simulation complete. No changes were made." -ForegroundColor Green
    return
}

# Stop running instances before touching files
Stop-RunningClient

# Clean stale staging or backup directories
if (Test-Path -LiteralPath $StagingDir) {
    Assert-NotUserDataPath -Path $StagingDir -Operation "remove staging"
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
}
if (Test-Path -LiteralPath $BackupDir) {
    Assert-NotUserDataPath -Path $BackupDir -Operation "remove backup"
    Remove-Item -LiteralPath $BackupDir -Recurse -Force
}

# Stage new files
Write-Host "  Staging release payload..."
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

try {
    Get-ChildItem -Path $SourceDir -Recurse | Where-Object {
        -not $_.PSIsContainer -and $_.Name -notlike ".env*" -and $_.Name -notlike "*.tmp"
    } | ForEach-Object {
        $rel = Get-RelativeChildPath -BasePath $SourceDir -FullPath $_.FullName
        $dest = Join-Path $StagingDir $rel
        $parent = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }

    # Copy self-contained uninstaller into staging as uninstall.ps1
    $uninstallerSource = Resolve-CompanionUninstaller -SourceDir $SourceDir
    Copy-Item -LiteralPath $uninstallerSource -Destination (Join-Path $StagingDir "uninstall.ps1") -Force

    # Validate staged payload
    $stagedExe = Join-Path $StagingDir "FootballTray.exe"
    if (-not (Test-Path -LiteralPath $stagedExe) -or (Get-Item -LiteralPath $stagedExe).Length -eq 0) {
        throw "CRITICAL: Staging validation failed; staged executable is missing or empty."
    }

    $stagedUninstaller = Join-Path $StagingDir "uninstall.ps1"
    if (-not (Test-Path -LiteralPath $stagedUninstaller) -or (Get-Item -LiteralPath $stagedUninstaller).Length -eq 0) {
        throw "CRITICAL: Staging validation failed; staged uninstaller is missing or empty."
    }
} catch {
    $stagingException = $_
    if (Test-Path -LiteralPath $StagingDir) {
        try {
            Assert-NotUserDataPath -Path $StagingDir -Operation "remove staging"
            Remove-Item -LiteralPath $StagingDir -Recurse -Force
        } catch {
            Write-Warning "Failed to cleanup staging directory '$StagingDir': $_"
        }
    }
    throw $stagingException
}

# ----------------------------------------------------------------------
# 6. Capture Pre-Existing Integration State BEFORE Activation
# ----------------------------------------------------------------------
$hadPrevious = Test-Path -LiteralPath $TargetDir
$hadStartMenuShortcut = Test-Path -LiteralPath $StartMenuShortcut
$hadStartupShortcut = Test-Path -LiteralPath $StartupShortcut
$hadUninstallReg = Test-Path -LiteralPath $UninstallRegKey

# Backup existing Start Menu shortcut if present
if ($hadStartMenuShortcut) {
    Copy-Item -LiteralPath $StartMenuShortcut -Destination "$StartMenuShortcut.bak" -Force
}

# Backup existing Startup shortcut if present
if ($hadStartupShortcut) {
    Copy-Item -LiteralPath $StartupShortcut -Destination "$StartupShortcut.bak" -Force
}

# Backup existing registry properties if present
$previousRegValues = @{}
if ($hadUninstallReg) {
    try {
        $regItem = Get-Item -LiteralPath $UninstallRegKey
        foreach ($prop in $regItem.Property) {
            $previousRegValues[$prop] = $regItem.GetValue($prop)
        }
    } catch {}
}

# ----------------------------------------------------------------------
# 7. Complete Installation Transaction with Rollback Protection
# ----------------------------------------------------------------------
Write-Host "`n[3/5] Activating installation and configuring integration..." -ForegroundColor Yellow
$targetParent = Split-Path -Parent $TargetDir
if (-not (Test-Path -LiteralPath $targetParent)) {
    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
}

try {
    # Backup existing installation directory
    if ($hadPrevious) {
        Assert-NotUserDataPath -Path $TargetDir -Operation "backup"
        Rename-Item -LiteralPath $TargetDir -NewName (Split-Path $BackupDir -Leaf)
    }

    # Activate staged payload
    Assert-NotUserDataPath -Path $StagingDir -Operation "activate"
    Rename-Item -LiteralPath $StagingDir -NewName (Split-Path $TargetDir -Leaf)

    # 7.1 Validate active executable
    $installedExe = Join-Path $TargetDir "FootballTray.exe"
    if (-not (Test-Path -LiteralPath $installedExe) -or (Get-Item -LiteralPath $installedExe).Length -eq 0) {
        throw "Active installation validation failed: executable missing or empty."
    }

    if ($SimulateFailureStep -eq "Activation") {
        throw "SIMULATED FAILURE: Activation step failure."
    }

    # 7.2 Configure and verify Start Menu shortcut target
    Write-Host "`n[4/5] Configuring Start Menu shortcut..." -ForegroundColor Yellow
    if (-not (Test-Path -LiteralPath $StartMenuDir)) {
        New-Item -ItemType Directory -Path $StartMenuDir -Force | Out-Null
    }
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($StartMenuShortcut)
    $sc.TargetPath = $installedExe
    $sc.WorkingDirectory = $TargetDir
    $sc.Description = "FootballTray - Live Football Taskbar Ticker"
    $sc.IconLocation = "$installedExe,0"
    $sc.Save()

    if ($SimulateFailureStep -eq "StartMenu") {
        throw "SIMULATED FAILURE: StartMenu step failure."
    }

    if (-not (Test-Path -LiteralPath $StartMenuShortcut)) {
        throw "Start Menu shortcut file verification failed at '$StartMenuShortcut'."
    }

    # Verify that the shortcut target resolves to the active FootballTray.exe
    $verifySc = $wsh.CreateShortcut($StartMenuShortcut)
    $resolvedTarget = [System.IO.Path]::GetFullPath($verifySc.TargetPath).TrimEnd('\')
    $expectedTarget = [System.IO.Path]::GetFullPath($installedExe).TrimEnd('\')
    if (-not [string]::Equals($resolvedTarget, $expectedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Start Menu shortcut target verification failed: target resolves to '$resolvedTarget', expected '$expectedTarget'."
    }
    Write-Host "  Start Menu shortcut verified (resolves to: $resolvedTarget)" -ForegroundColor Green

    # 7.3 Configure and verify Startup shortcut (if requested)
    if ($StartWithWindows) {
        Write-Host "  Configuring Startup shortcut..."
        if (-not (Test-Path -LiteralPath $StartupDir)) {
            New-Item -ItemType Directory -Path $StartupDir -Force | Out-Null
        }
        $suSc = $wsh.CreateShortcut($StartupShortcut)
        $suSc.TargetPath = $installedExe
        $suSc.WorkingDirectory = $TargetDir
        $suSc.Description = "FootballTray Autostart"
        $suSc.IconLocation = "$installedExe,0"
        $suSc.Save()

        if ($SimulateFailureStep -eq "Startup") {
            throw "SIMULATED FAILURE: Startup step failure."
        }

        if (-not (Test-Path -LiteralPath $StartupShortcut)) {
            throw "Startup shortcut file verification failed at '$StartupShortcut'."
        }

        # Verify Startup shortcut target resolves to active FootballTray.exe
        $verifySuSc = $wsh.CreateShortcut($StartupShortcut)
        $resolvedSuTarget = [System.IO.Path]::GetFullPath($verifySuSc.TargetPath).TrimEnd('\')
        if (-not [string]::Equals($resolvedSuTarget, $expectedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Startup shortcut target verification failed: target resolves to '$resolvedSuTarget', expected '$expectedTarget'."
        }
        Write-Host "  Startup shortcut verified (resolves to: $resolvedSuTarget)" -ForegroundColor Green
    }

    # 7.4 Configure and verify HKCU Uninstall registration
    Write-Host "`n[5/5] Registering in Windows Add/Remove Programs (HKCU)..." -ForegroundColor Yellow
    if (-not (Test-Path -LiteralPath $UninstallRegKey)) {
        New-Item -Path $UninstallRegKey -Force | Out-Null
    }

    $installedUninstallScript = Join-Path $TargetDir "uninstall.ps1"
    $uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installedUninstallScript`""
    $quietUninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installedUninstallScript`" -Quiet"

    $installedBytes = (Get-ChildItem -Path $TargetDir -Recurse | Measure-Object -Property Length -Sum).Sum
    $installedKb = [math]::Round($installedBytes / 1024)

    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayName" -Value "FootballTray" -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayVersion" -Value $sourceVersion -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "Publisher" -Value "FootballTray" -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "InstallLocation" -Value $TargetDir -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "DisplayIcon" -Value "$installedExe,0" -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "UninstallString" -Value $uninstallCmd -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "QuietUninstallString" -Value $quietUninstallCmd -Type String
    Set-ItemProperty -Path $UninstallRegKey -Name "NoModify" -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallRegKey -Name "NoRepair" -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallRegKey -Name "EstimatedSize" -Value $installedKb -Type DWord

    if ($SimulateFailureStep -eq "Registry") {
        throw "SIMULATED FAILURE: Registry step failure."
    }

    if (-not (Test-Path -LiteralPath $UninstallRegKey)) {
        throw "Uninstall registry verification failed at '$UninstallRegKey'."
    }
    Write-Host "  Registered uninstaller verified in $UninstallRegKey" -ForegroundColor Green

    # 7.5 Confirm target executable still exists
    if (-not (Test-Path -LiteralPath $installedExe)) {
        throw "Final verification failed: target executable missing at '$installedExe'."
    }

    # 7.6 Confirm user data path invariant
    Assert-NotUserDataPath -Path $TargetDir -Operation "target"

    # 7.7 Internal test hook: simulate failure if requested
    if ($SimulatePostActivationFailure -or $SimulateFailureStep -eq "FinalVerify") {
        throw "SIMULATED POST-ACTIVATION FAILURE FOR TRANSACTION TESTING"
    }

    # ------------------------------------------------------------------
    # 8. TRANSACTION COMPLETE - Only now clean up backups!
    # ------------------------------------------------------------------
    if (Test-Path -LiteralPath $BackupDir) {
        Assert-NotUserDataPath -Path $BackupDir -Operation "remove backup"
        Remove-Item -LiteralPath $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath "$StartMenuShortcut.bak") {
        Remove-Item -LiteralPath "$StartMenuShortcut.bak" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath "$StartupShortcut.bak") {
        Remove-Item -LiteralPath "$StartupShortcut.bak" -Force -ErrorAction SilentlyContinue
    }

    Write-Host "  Installation transaction completed successfully." -ForegroundColor Green
} catch {
    Write-Host "CRITICAL: Installation transaction failed: $_. Rolling back..." -ForegroundColor Red

    # Rollback binaries
    if (Test-Path -LiteralPath $TargetDir) {
        Assert-NotUserDataPath -Path $TargetDir -Operation "remove active"
        Remove-Item -LiteralPath $TargetDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($hadPrevious -and (Test-Path -LiteralPath $BackupDir)) {
        Assert-NotUserDataPath -Path $BackupDir -Operation "restore backup"
        Rename-Item -LiteralPath $BackupDir -NewName (Split-Path $TargetDir -Leaf)
        Write-Host "  Restored previous installation from backup directory." -ForegroundColor Yellow
    }

    # Rollback Start Menu shortcut to exact previous state
    if (Test-Path -LiteralPath "$StartMenuShortcut.bak") {
        Move-Item -LiteralPath "$StartMenuShortcut.bak" -Destination $StartMenuShortcut -Force
        Write-Host "  Restored previous Start Menu shortcut." -ForegroundColor Yellow
    } elseif (-not $hadStartMenuShortcut -and (Test-Path -LiteralPath $StartMenuShortcut)) {
        Remove-Item -LiteralPath $StartMenuShortcut -Force -ErrorAction SilentlyContinue
    }

    # Rollback Startup shortcut to exact previous state
    if (Test-Path -LiteralPath "$StartupShortcut.bak") {
        Move-Item -LiteralPath "$StartupShortcut.bak" -Destination $StartupShortcut -Force
        Write-Host "  Restored previous Startup shortcut." -ForegroundColor Yellow
    } elseif (-not $hadStartupShortcut -and (Test-Path -LiteralPath $StartupShortcut)) {
        Remove-Item -LiteralPath $StartupShortcut -Force -ErrorAction SilentlyContinue
    }

    # Rollback registry entry to exact previous state
    if ($hadUninstallReg) {
        if (-not (Test-Path -LiteralPath $UninstallRegKey)) {
            New-Item -Path $UninstallRegKey -Force | Out-Null
        }
        # Clean current properties not in previous
        $currProps = (Get-Item -LiteralPath $UninstallRegKey).Property
        foreach ($cp in $currProps) {
            if (-not $previousRegValues.ContainsKey($cp)) {
                Remove-ItemProperty -Path $UninstallRegKey -Name $cp -Force -ErrorAction SilentlyContinue
            }
        }
        # Restore previous property values
        foreach ($kv in $previousRegValues.GetEnumerator()) {
            Set-ItemProperty -Path $UninstallRegKey -Name $kv.Key -Value $kv.Value -Force
        }
        Write-Host "  Restored previous uninstall registry properties." -ForegroundColor Yellow
    } else {
        if (Test-Path -LiteralPath $UninstallRegKey) {
            Remove-Item -Path $UninstallRegKey -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    throw "Installation transaction failed and was rolled back: $_"
}

# ----------------------------------------------------------------------
# 9. Completion & Optional Launch
# ----------------------------------------------------------------------
Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " FootballTray v$sourceVersion Installed Successfully!" -ForegroundColor Green
Write-Host " Installation Path: $TargetDir" -ForegroundColor Green
Write-Host " User Data Path:     $UserDataDir (Preserved)" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

if ($Launch) {
    Write-Host "Launching FootballTray..." -ForegroundColor Cyan
    Start-Process -FilePath $installedExe -WorkingDirectory $TargetDir
}
