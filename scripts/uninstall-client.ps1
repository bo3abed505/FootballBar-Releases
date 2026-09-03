<#
.SYNOPSIS
    Per-User Client Uninstaller for FootballTray WinUI 3 Desktop Client.
.DESCRIPTION
    Removes the FootballTray installation from %LOCALAPPDATA%\Programs\FootballTray,
    removes shortcuts and registry entries, but PRESERVES user data under %LOCALAPPDATA%\FootballTray.
.PARAMETER TargetDir
    Installation directory to remove. Defaults to %LOCALAPPDATA%\Programs\FootballTray.
.PARAMETER UserDataDir
    User data directory to preserve. Defaults to %LOCALAPPDATA%\FootballTray.
.PARAMETER StartMenuDir
    Start Menu directory containing shortcut. Defaults to per-user Programs folder.
.PARAMETER StartupDir
    Startup directory containing shortcut. Defaults to per-user Startup folder.
.PARAMETER UninstallRegKey
    HKCU registry path for uninstaller. Defaults to HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\FootballTray.
.PARAMETER Quiet
    Suppresses interactive console pauses.
.PARAMETER WhatIf
    Simulates the uninstallation sequence without modifying the filesystem or registry.
#>

[CmdletBinding()]
param (
    [string]$TargetDir,
    [string]$UserDataDir,
    [string]$StartMenuDir,
    [string]$StartupDir,
    [string]$UninstallRegKey,
    [switch]$Quiet,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

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

Assert-NotUserDataPath -Path $TargetDir -Operation "target"
Assert-NotUserDataPath -Path $StagingDir -Operation "staging"
Assert-NotUserDataPath -Path $BackupDir -Operation "backup"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray — Windows Client Uninstaller" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Target Directory:       $TargetDir"
Write-Host " User Data Directory:    $UserDataDir (Will be PRESERVED)"
if ($WhatIf) {
    Write-Host " Mode:                   WHAT-IF (Simulation only)" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------
# 1. Stop Running FootballTray Instances
# ----------------------------------------------------------------------
function Stop-RunningClient {
    param ([int]$TimeoutSeconds = 5)
    $procs = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { return }

    Write-Host "`nStopping running FootballTray process(es)..." -ForegroundColor Yellow
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
        Write-Host "Process did not exit cleanly within ${TimeoutSeconds}s. Terminating..." -ForegroundColor Yellow
        foreach ($p in $remaining) {
            try {
                $p.Kill()
                $p.WaitForExit(2000)
            } catch {}
        }
    }

    $stillRunning = @(Get-Process -Name "FootballTray" -ErrorAction SilentlyContinue)
    if ($stillRunning.Count -gt 0) {
        throw "CRITICAL: Unable to terminate running FootballTray process (PID(s): $($stillRunning.Id -join ', ')). Aborting uninstallation."
    }
}

if ($WhatIf) {
    Write-Host "`n[WhatIf] Would stop running FootballTray processes (if any)." -ForegroundColor Gray
    Write-Host "[WhatIf] Would remove installation directory '$TargetDir'." -ForegroundColor Gray
    Write-Host "[WhatIf] Would remove Start Menu shortcut '$StartMenuShortcut'." -ForegroundColor Gray
    Write-Host "[WhatIf] Would remove Startup shortcut '$StartupShortcut' (if present)." -ForegroundColor Gray
    Write-Host "[WhatIf] Would remove registry key '$UninstallRegKey'." -ForegroundColor Gray
    Write-Host "[WhatIf] Would preserve user data in '$UserDataDir'." -ForegroundColor Green
    Write-Host "`n[WhatIf] Simulation complete. No changes were made." -ForegroundColor Green
    return
}

Stop-RunningClient

# ----------------------------------------------------------------------
# 2. Remove Shortcuts
# ----------------------------------------------------------------------
Write-Host "`nRemoving shortcuts..." -ForegroundColor Yellow
if (Test-Path -LiteralPath $StartMenuShortcut) {
    Remove-Item -LiteralPath $StartMenuShortcut -Force
    Write-Host "  Removed Start Menu shortcut." -ForegroundColor Gray
}

if (Test-Path -LiteralPath $StartupShortcut) {
    Remove-Item -LiteralPath $StartupShortcut -Force
    Write-Host "  Removed Startup shortcut." -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# 3. Remove Registry Uninstall Key
# ----------------------------------------------------------------------
Write-Host "`nRemoving Windows Uninstall registration..." -ForegroundColor Yellow
if (Test-Path -LiteralPath $UninstallRegKey) {
    Remove-Item -Path $UninstallRegKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Removed registry key: $UninstallRegKey" -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# 4. Remove Binary Installation Directory
# ----------------------------------------------------------------------
Write-Host "`nRemoving application binaries..." -ForegroundColor Yellow
Assert-NotUserDataPath -Path $TargetDir -Operation "delete"
Assert-NotUserDataPath -Path $StagingDir -Operation "delete"
Assert-NotUserDataPath -Path $BackupDir -Operation "delete"

if (Test-Path -LiteralPath $TargetDir) {
    Remove-Item -LiteralPath $TargetDir -Recurse -Force
    Write-Host "  Removed application directory: $TargetDir" -ForegroundColor Gray
}

if (Test-Path -LiteralPath $StagingDir) {
    Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
}
if (Test-Path -LiteralPath $BackupDir) {
    Remove-Item -LiteralPath $BackupDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ----------------------------------------------------------------------
# 5. User Data Preservation Confirmation
# ----------------------------------------------------------------------
Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " FootballTray has been successfully uninstalled." -ForegroundColor Green
if (Test-Path -LiteralPath $UserDataDir) {
    Write-Host " User data preserved at: $UserDataDir" -ForegroundColor Cyan
    Write-Host " (Preferences, offline cache, and diagnostic logs were not deleted)." -ForegroundColor Cyan
}
Write-Host "============================================================" -ForegroundColor Green
