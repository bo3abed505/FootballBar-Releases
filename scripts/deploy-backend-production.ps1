<#
.SYNOPSIS
    Automated Production Backend Deployment & Decoupling Script for FootballTray.
.DESCRIPTION
    Decouples the FootballTray production backend service from the Git source repository.
    Deploys versioned releases under C:\ProgramData\FootballTray\backend, manages the
    "current" active release junction, secures production credentials with strict ACLs,
    and configures the unattended Windows Task Scheduler task with automated two-way rollback
    and checked execution.
.PARAMETER SourceDir
    Root directory of the backend source repository. Defaults to repository backend directory.
.PARAMETER TargetDir
    Target dedicated production deployment directory. Defaults to C:\ProgramData\FootballTray\backend.
.PARAMETER DryRun
    Simulates the deployment and displays planned actions and task XML without modifying the system.
.PARAMETER Rollback
    Reverts the current active deployment to the previous known-good verified release and restarts the service.
.PARAMETER SkipTaskUpdate
    Performs directory deployment and dependency installation without updating the scheduled task.
.PARAMETER NoStart
    Deploys, packages, activates the release junction, and updates the scheduled task definition without starting the backend service or performing HTTP readiness probes.
.PARAMETER BaselineLegacySchema
    Instructs the production migration runner to perform the one-time legacy baseline verification and transactional baseline metadata insertion before applying pending migrations.
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param (
    [string]$SourceDir,
    [string]$TargetDir = "C:\ProgramData\FootballTray\backend",
    [switch]$DryRun,
    [switch]$Rollback,
    [switch]$SkipTaskUpdate,
    [switch]$NoStart,
    [switch]$BaselineLegacySchema
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Determine source root
if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
    $rootDir = Split-Path -Parent $scriptPath
    $SourceDir = Join-Path $rootDir "backend"
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray -- Production Backend Deployment Tool" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Source Directory: $SourceDir"
Write-Host " Target Directory: $TargetDir"
if ($DryRun) {
    Write-Host " Mode: DRY-RUN (Simulation only -- no system modifications)" -ForegroundColor Magenta
}
if ($NoStart) {
    Write-Host " Mode: NO-START (Will package and configure release without starting service)" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------
# 1. Administrator Privilege Check
# ----------------------------------------------------------------------
function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]$identity
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin -and -not $DryRun) {
        throw "CRITICAL: Administrator privileges are required to configure production backend files, ACLs, and Windows Scheduled Tasks. Please run this script from an elevated PowerShell terminal."
    }
}

# ----------------------------------------------------------------------
# 2. Checked External Process Invocation Helper
# ----------------------------------------------------------------------
function Invoke-SchtasksChecked {
    param (
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Operation
    )

    $output = & schtasks.exe @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $msg = "CRITICAL: schtasks operation failed: $Operation (exit code ${LASTEXITCODE}):`n$($output -join "`n")"
        throw $msg
    }
    return $output
}

# ----------------------------------------------------------------------
# 3. Source Validation
# ----------------------------------------------------------------------
function Assert-SourceValid {
    param ([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Source directory not found: $Path"
    }

    $requiredSourceFiles = @("package.json", "package-lock.json", "dist\server.js", "dist\db\migrate-prod.js", "drizzle\meta\_journal.json")
    foreach ($rel in $requiredSourceFiles) {
        $full = Join-Path $Path $rel
        if (-not (Test-Path $full)) {
            throw "Missing required source build artifact '$rel' at '$full'. Please run 'npm run build' before deploying."
        }
    }

    # Verify node executable
    $nodeCmd = Get-Command "node" -ErrorAction SilentlyContinue
    if (-not $nodeCmd) {
        throw "Node.js executable not found in PATH."
    }
}

# ----------------------------------------------------------------------
# 4. Junction Resolution Helpers
# ----------------------------------------------------------------------
function Get-JunctionTarget {
    param ([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        if ($item.LinkTarget) {
            return [System.IO.Path]::GetFullPath($item.LinkTarget)
        }
        if ($item.Target) {
            $t = $item.Target | Select-Object -First 1
            if ($t) { return [System.IO.Path]::GetFullPath($t) }
        }
        $resolved = [System.IO.Directory]::ResolveLinkTarget($Path, $true)
        if ($resolved) {
            return [System.IO.Path]::GetFullPath($resolved.FullName)
        }
    } catch {
        try {
            $parent = Split-Path -Parent $Path
            $leaf = Split-Path -Leaf $Path
            $dirOutput = cmd /c "dir /AL `"$parent`"" 2>$null
            $match = $dirOutput | Select-String "<JUNCTION>\s+$leaf\s+\[(.*)\]"
            if ($match -and $match.Matches.Groups.Count -gt 1) {
                return [System.IO.Path]::GetFullPath($match.Matches.Groups[1].Value)
            }
        } catch { }
    }
    return $null
}

function Get-PreviousKnownGoodRelease {
    param (
        [string]$ReleasesDir,
        [string]$CurrentActivePath
    )

    if (-not (Test-Path $ReleasesDir)) {
        return $null
    }

    $releaseDirs = @(Get-ChildItem -Path $ReleasesDir -Directory)
    if ($releaseDirs.Count -eq 0) {
        return $null
    }

    $normalizedActive = $null
    if ($CurrentActivePath) {
        $normalizedActive = [System.IO.Path]::GetFullPath($CurrentActivePath).TrimEnd('\')
    }

    $parsedList = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($dir in $releaseDirs) {
        $manifestPath = Join-Path $dir.FullName "deployment-manifest.json"
        if (-not (Test-Path $manifestPath)) {
            # Exclude releases without deployment manifest
            continue
        }

        try {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            # Strictly require verified status - exclude pending, failed, or untracked
            if ($manifest.status -ne "verified") {
                continue
            }

            $deployedAt = $null
            if ($manifest.deployedAt) {
                $deployedAt = [DateTime]::Parse($manifest.deployedAt).ToUniversalTime()
            } else {
                $deployedAt = $dir.CreationTimeUtc
            }

            $dirNorm = [System.IO.Path]::GetFullPath($dir.FullName).TrimEnd('\')
            $isActive = ($normalizedActive -and [string]::Equals($dirNorm, $normalizedActive, [System.StringComparison]::OrdinalIgnoreCase))

            $parsedList.Add([PSCustomObject]@{
                Path       = $dir.FullName
                DeployedAt = $deployedAt
                IsActive   = $isActive
                Version    = $manifest.version
                Status     = $manifest.status
            })
        } catch {
            # Exclude unparseable / corrupt manifests
            continue
        }
    }

    # Deterministic sort by DeployedAt descending
    $sorted = $parsedList | Sort-Object -Property DeployedAt -Descending

    # Pick the most recent VERIFIED release that is NOT the currently active release
    $candidate = $sorted | Where-Object { -not $_.IsActive } | Select-Object -First 1

    if ($candidate) {
        return $candidate.Path
    }

    return $null
}

# ----------------------------------------------------------------------
# 5. Manifest Status Updater
# ----------------------------------------------------------------------
function Set-DeploymentManifestStatus {
    param (
        [Parameter(Mandatory = $true)]
        [string]$ReleaseDir,

        [Parameter(Mandatory = $true)]
        [ValidateSet("pending", "verified", "failed")]
        [string]$Status
    )

    $manifestPath = Join-Path $ReleaseDir "deployment-manifest.json"

    # 1. Manifest must exist
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "CRITICAL: Deployment manifest file not found at '$manifestPath'."
    }

    # 2. Manifest must be valid JSON
    $manifest = $null
    try {
        $rawContent = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
        $manifest = $rawContent | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "CRITICAL: Deployment manifest at '$manifestPath' is malformed or unparseable: $($_.Exception.Message)"
    }

    if (-not $manifest) {
        throw "CRITICAL: Deployment manifest at '$manifestPath' could not be deserialized."
    }

    # 3. Update status and timestamps safely under StrictMode
    $manifest | Add-Member -NotePropertyName "status" -NotePropertyValue $Status -Force
    $nowStr = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    if ($Status -eq "verified") {
        $manifest | Add-Member -NotePropertyName "verifiedAt" -NotePropertyValue $nowStr -Force
    } elseif ($Status -eq "failed") {
        $manifest | Add-Member -NotePropertyName "failedAt" -NotePropertyValue $nowStr -Force
    }

    # 4. Write manifest
    try {
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Force -ErrorAction Stop
    } catch {
        throw "CRITICAL: Failed to write updated deployment manifest to '$manifestPath': $($_.Exception.Message)"
    }

    # 5. Re-read manifest from disk
    $persistedRaw = $null
    try {
        $persistedRaw = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
    } catch {
        throw "CRITICAL: Failed to re-read deployment manifest from '$manifestPath' for verification: $($_.Exception.Message)"
    }

    # 6. Parse again
    $persistedManifest = $null
    try {
        $persistedManifest = $persistedRaw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "CRITICAL: Re-read deployment manifest from '$manifestPath' is malformed: $($_.Exception.Message)"
    }

    # 7. Verify persisted status
    if ($persistedManifest.status -ne $Status) {
        throw "CRITICAL: Deployment manifest persistence verification failed: expected status '$Status', but found '$($persistedManifest.status)' at '$manifestPath'."
    }
}

# ----------------------------------------------------------------------
# 6. Generate Task XML Definition
# ----------------------------------------------------------------------
function New-BackendTaskXml {
    param (
        [string]$WorkingDir,
        [string]$EnvFilePath,
        [string]$NodeExePath = "C:\Program Files\nodejs\node.exe"
    )

    $taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>FootballTray Production Backend Service (Standalone)</Description>
    <URI>\FootballTray-Backend</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <RestartOnFailure>
      <Count>3</Count>
      <Interval>PT1M</Interval>
    </RestartOnFailure>
    <StartWhenAvailable>true</StartWhenAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
  </Settings>
  <Triggers>
    <BootTrigger />
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>$NodeExePath</Command>
      <Arguments>--env-file=$EnvFilePath dist/server.js</Arguments>
      <WorkingDirectory>$WorkingDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
    return $taskXml.Trim()
}

# ----------------------------------------------------------------------
# 7. Parse and Validate Task XML with XmlNamespaceManager
# ----------------------------------------------------------------------
function Test-TaskXmlDefinition {
    param (
        [Parameter(Mandatory = $true)]
        [string]$XmlContent
    )

    [xml]$doc = $XmlContent
    $taskNamespaceUri = "http://schemas.microsoft.com/windows/2004/02/mit/task"
    $nsm = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nsm.AddNamespace("t", $taskNamespaceUri)

    $taskNode = $doc.SelectSingleNode("/t:Task", $nsm)
    $useNs = ($null -ne $taskNode)
    if (-not $taskNode) {
        $taskNode = $doc.SelectSingleNode("/*[local-name()='Task']")
    }
    if (-not $taskNode) {
        throw "Task XML verification failed: missing root <Task> element."
    }

    $selectNode = {
        param([string]$xpathNs, [string]$xpathLocal)
        if ($useNs) {
            return $doc.SelectSingleNode($xpathNs, $nsm)
        } else {
            return $doc.SelectSingleNode($xpathLocal)
        }
    }

    $userNode = & $selectNode "/t:Task/t:Principals/t:Principal/t:UserId" "//*[local-name()='UserId']"
    $userId = if ($userNode) { $userNode.InnerText.Trim() } else { $null }

    $runLevelNode = & $selectNode "/t:Task/t:Principals/t:Principal/t:RunLevel" "//*[local-name()='RunLevel']"
    $runLevel = if ($runLevelNode) { $runLevelNode.InnerText.Trim() } else { $null }

    $bootTriggerNode = & $selectNode "/t:Task/t:Triggers/t:BootTrigger" "//*[local-name()='BootTrigger']"

    $cmdNode = & $selectNode "/t:Task/t:Actions/t:Exec/t:Command" "//*[local-name()='Exec']/*[local-name()='Command']"
    $command = if ($cmdNode) { $cmdNode.InnerText.Trim() } else { $null }

    $argsNode = & $selectNode "/t:Task/t:Actions/t:Exec/t:Arguments" "//*[local-name()='Exec']/*[local-name()='Arguments']"
    $arguments = if ($argsNode) { $argsNode.InnerText.Trim() } else { $null }

    $workDirNode = & $selectNode "/t:Task/t:Actions/t:Exec/t:WorkingDirectory" "//*[local-name()='Exec']/*[local-name()='WorkingDirectory']"
    $workingDir = if ($workDirNode) { $workDirNode.InnerText.Trim() } else { $null }

    return [PSCustomObject]@{
        IsValid          = ($null -ne $taskNode)
        UserId           = $userId
        RunLevel         = $runLevel
        HasBootTrigger   = ($null -ne $bootTriggerNode)
        Command          = $command
        Arguments        = $arguments
        WorkingDirectory = $workingDir
    }
}

# ----------------------------------------------------------------------
# 8. Set Restrictive ACLs on Configuration Directory and Files
# ----------------------------------------------------------------------
function Set-SecureAcl {
    param ([string]$Path)

    if ($DryRun) {
        Write-Host "  [DryRun] Would apply restrictive ACLs to $Path (SYSTEM + Administrators only)" -ForegroundColor Gray
        return
    }

    if (-not (Test-Path -LiteralPath $Path)) { return }

    $isDir = (Get-Item -LiteralPath $Path) -is [System.IO.DirectoryInfo]
    $acl = Get-Acl -LiteralPath $Path

    # Disable inheritance and purge inherited rules
    $acl.SetAccessRuleProtection($true, $false)

    # Purge any existing explicit access rules
    $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    foreach ($r in $rules) {
        $acl.RemoveAccessRule($r) | Out-Null
    }

    # Grant FullControl exclusively to SYSTEM (S-1-5-18) and BUILTIN\Administrators (S-1-5-32-544)
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
    $adminsSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")

    $inheritance = if ($isDir) {
        [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    } else {
        [System.Security.AccessControl.InheritanceFlags]::None
    }

    $systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $systemSid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow
    )

    $adminsRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $adminsSid,
        [System.Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow
    )

    $acl.AddAccessRule($systemRule)
    $acl.AddAccessRule($adminsRule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

# ----------------------------------------------------------------------
# 8. Manual Rollback Handler (-Rollback switch)
# ----------------------------------------------------------------------
if ($Rollback) {
    Assert-Administrator
    Write-Host "`n[ROLLBACK] Initiating manual rollback to previous known-good release..." -ForegroundColor Yellow

    $releasesDir = Join-Path $TargetDir "releases"
    $currentJunction = Join-Path $TargetDir "current"
    $configDir = Join-Path $TargetDir "config"

    $activeTarget = Get-JunctionTarget -Path $currentJunction
    Write-Host "Currently active release target: $(if ($activeTarget) { $activeTarget } else { 'None / Broken' })"

    $targetRelease = Get-PreviousKnownGoodRelease -ReleasesDir $releasesDir -CurrentActivePath $activeTarget
    if (-not $targetRelease) {
        throw "No suitable verified previous release found in '$releasesDir' to rollback to."
    }

    Write-Host "Selected rollback release: $targetRelease" -ForegroundColor Green

    if (-not $DryRun) {
        Write-Host "Stopping running service..."
        try {
            Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend") -Operation "Stop Service for Rollback"
        } catch {
            Write-Warning "Could not stop service (may already be stopped): $_"
        }
        Start-Sleep -Seconds 2

        # Restore junction
        if (Test-Path $currentJunction) {
            cmd /c "rmdir `"$currentJunction`"" | Out-Null
        }
        New-Item -ItemType Junction -Path $currentJunction -Target $targetRelease | Out-Null
        Write-Host "Junction 'current' redirected to: $targetRelease" -ForegroundColor Green

        # Look for latest task backup XML
        $backupXmls = @(Get-ChildItem -Path $configDir -Filter "task-backup-*.xml" | Sort-Object Name -Descending)
        if ($backupXmls.Count -gt 0) {
            $restoreXmlPath = $backupXmls[0].FullName
            Write-Host "Restoring Task Scheduler configuration from: $restoreXmlPath"
            Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $restoreXmlPath, "/F") -Operation "Restore Task XML"
        }

        if (-not $NoStart) {
            # Restart and verify
            Write-Host "Starting rolled-back service..."
            Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend") -Operation "Start Rolled-Back Service"
            Start-Sleep -Seconds 4

            # Readiness verification
            Write-Host "Verifying readiness after rollback via /api/tray..."
            $rollbackReady = $false
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($sw.Elapsed.TotalSeconds -lt 25) {
                try {
                    $tray = Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3
                    if ($null -ne $tray -and
                        $null -ne $tray.PSObject.Properties["schemaVersion"] -and
                        -not [string]::IsNullOrWhiteSpace($tray.schemaVersion) -and
                        $null -ne $tray.PSObject.Properties["data"] -and
                        $null -ne $tray.data -and
                        $null -ne $tray.data.PSObject.Properties["matches"] -and
                        ($tray.data.matches -is [System.Array])) {
                        $rollbackReady = $true
                        break
                    }
                } catch {
                    Start-Sleep -Seconds 2
                }
            }

            if ($rollbackReady) {
                Write-Host "[ROLLBACK SUCCESS] Service successfully rolled back and verified ready." -ForegroundColor Green
            } else {
                Write-Host "CRITICAL: Rollback readiness verification failed! Service did not report ready status." -ForegroundColor Red
                throw "Rollback verification failed."
            }
        } else {
            Write-Host "[-NoStart] Restored previous release configuration without starting the service." -ForegroundColor Yellow
            Write-Host "[ROLLBACK SUCCESS] Service successfully rolled back (service intentionally left stopped)." -ForegroundColor Green
        }
    } else {
        Write-Host "  [DryRun] Would redirect 'current' to $targetRelease, restore task XML, restart task, and verify readiness via /api/tray." -ForegroundColor Gray
    }

    exit 0
}

# ----------------------------------------------------------------------
# 9. Standard Deployment Execution
# ----------------------------------------------------------------------
Assert-Administrator
Assert-SourceValid -Path $SourceDir

# Parse version metadata
$packageJsonPath = Join-Path $SourceDir "package.json"
$pkgJson = Get-Content $packageJsonPath -Raw | ConvertFrom-Json
$version = $pkgJson.version

$gitCommit = "unknown"
try {
    $gitCommit = (git -C $SourceDir rev-parse --short HEAD).Trim()
} catch {
    $gitCommit = "untracked"
}

$timestampStr = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$releaseId = "v${version}-${timestampStr}"

Write-Host "`n[1/6] Preparing release structure..." -ForegroundColor Yellow
Write-Host "  Release ID: $releaseId"
Write-Host "  Package Version: $version"
Write-Host "  Commit SHA: $gitCommit"

$releasesBase = Join-Path $TargetDir "releases"
$releaseDir = Join-Path $releasesBase $releaseId
$currentJunction = Join-Path $TargetDir "current"
$configDir = Join-Path $TargetDir "config"
$targetEnvPath = Join-Path $configDir ".env.production.local"

if (-not $DryRun) {
    New-Item -ItemType Directory -Path $releasesBase -Force | Out-Null
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Set-SecureAcl -Path $configDir
}

# ----------------------------------------------------------------------
# 10. Copy Production Payload
# ----------------------------------------------------------------------
Write-Host "`n[2/6] Copying production backend payload..." -ForegroundColor Yellow
$filesToCopy = @(
    @{ Source = (Join-Path $SourceDir "dist"); Target = (Join-Path $releaseDir "dist"); IsDir = $true },
    @{ Source = (Join-Path $SourceDir "drizzle"); Target = (Join-Path $releaseDir "drizzle"); IsDir = $true },
    @{ Source = (Join-Path $SourceDir "package.json"); Target = (Join-Path $releaseDir "package.json"); IsDir = $false },
    @{ Source = (Join-Path $SourceDir "package-lock.json"); Target = (Join-Path $releaseDir "package-lock.json"); IsDir = $false }
)

foreach ($item in $filesToCopy) {
    if ($item.IsDir) {
        Write-Host "  Copying directory $($item.Source) -> $($item.Target)..."
        if (-not $DryRun) {
            Copy-Item -Path $item.Source -Destination $item.Target -Recurse -Force
        }
    } else {
        Write-Host "  Copying file $($item.Source) -> $($item.Target)..."
        if (-not $DryRun) {
            Copy-Item -Path $item.Source -Destination $item.Target -Force
        }
    }
}

# ----------------------------------------------------------------------
# 11. Manage Secure Production Environment File & Re-Apply ACLs
# ----------------------------------------------------------------------
Write-Host "`n[3/6] Configuring production environment credentials..." -ForegroundColor Yellow
$sourceEnvPath = Join-Path $SourceDir ".env.production.local"

$targetEnvExists = $false
try {
    $targetEnvExists = Test-Path -LiteralPath $targetEnvPath -ErrorAction Stop
} catch [System.UnauthorizedAccessException] {
    # Existing file is protected under strict SYSTEM/Admin ACLs
    $targetEnvExists = $true
} catch {
    $targetEnvExists = $false
}

if ($targetEnvExists) {
    Write-Host "  Existing production configuration detected at '$targetEnvPath'. Re-applying strict ACLs..." -ForegroundColor Green
    if (-not $DryRun) {
        Set-SecureAcl -Path $targetEnvPath
    }
} elseif (Test-Path $sourceEnvPath) {
    Write-Host "  Initializing production credentials from '$sourceEnvPath' into secure config directory..." -ForegroundColor Yellow
    if (-not $DryRun) {
        Copy-Item -Path $sourceEnvPath -Destination $targetEnvPath -Force
        Set-SecureAcl -Path $targetEnvPath
    }
} else {
    Write-Warning "No production environment file found at '$targetEnvPath' or '$sourceEnvPath'. Ensure credentials are configured before starting the service."
}

# ----------------------------------------------------------------------
# 12. Install Production Dependencies
# ----------------------------------------------------------------------
Write-Host "`n[4/6] Installing production dependencies with 'npm ci --omit=dev'..." -ForegroundColor Yellow
if (-not $DryRun) {
    Push-Location $releaseDir
    try {
        $npmOutput = npm ci --omit=dev --no-audit --no-fund 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed in $releaseDir with exit code ${LASTEXITCODE}:`n$npmOutput"
        }
        Write-Host "  Production dependencies installed successfully." -ForegroundColor Green
    } finally {
        Pop-Location
    }
} else {
    Write-Host "  [DryRun] Would execute: npm ci --omit=dev in $releaseDir" -ForegroundColor Gray
}

# ----------------------------------------------------------------------
# 13. Write Deployment Manifest (Initial Status: pending)
# ----------------------------------------------------------------------
Write-Host "`n[5/6] Generating deployment manifest (status: pending)..." -ForegroundColor Yellow
$manifest = [ordered]@{
    version       = $version
    releaseId     = $releaseId
    gitCommit     = $gitCommit
    deployedAt    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    nodeVersion   = (node --version).Trim()
    deployedPath  = $releaseDir
    environment   = "production"
    status        = "pending"
}

$manifestPath = Join-Path $releaseDir "deployment-manifest.json"
if (-not $DryRun) {
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $manifestPath -Force
    Write-Host "  Manifest written to $manifestPath (status: pending)" -ForegroundColor Green
}

# ----------------------------------------------------------------------
# 14. Pre-Flight Syntax & Runtime Validation
# ----------------------------------------------------------------------
Write-Host "`n[6/6] Validating deployed backend syntax..." -ForegroundColor Yellow
$serverJsPath = Join-Path $releaseDir "dist\server.js"
if (-not $DryRun) {
    $checkOutput = node --check $serverJsPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Node syntax validation failed on ${serverJsPath}:`n$checkOutput"
    }
    Write-Host "  Compiled server bundle syntax verified (node --check passed)." -ForegroundColor Green
}

# ----------------------------------------------------------------------
# 15. Task Preparation & Dry-Run Exit
# ----------------------------------------------------------------------
$taskXml = New-BackendTaskXml -WorkingDir $currentJunction -EnvFilePath $targetEnvPath

if ($DryRun) {
    Write-Host "`n=== [DryRun] Planned Database Migration ===" -ForegroundColor Cyan
    Write-Host "  Executable: node"
    Write-Host "  Script: dist\db\migrate-prod.js"
    Write-Host "  Target Database: footballtray_prod (enforced by production entrypoint)"
    if ($BaselineLegacySchema) {
        Write-Host "  Legacy Baseline Mode: ENABLED (--baseline-legacy-schema will be passed)" -ForegroundColor Yellow
    } else {
        Write-Host "  Legacy Baseline Mode: DISABLED (Standard mode)" -ForegroundColor Gray
    }
    Write-Host "  State Protocol: Machine-readable marker MIGRATION_STATE=(NO_DB_CHANGE|BASELINE_COMMITTED|MIGRATION_COMMITTED)"
    Write-Host "  Database Connection: SKIPPED in -DryRun mode (zero database queries executed)." -ForegroundColor Green

    Write-Host "`n=== [DryRun] Planned Task XML ===" -ForegroundColor Cyan
    Write-Host $taskXml
    if ($NoStart) {
        Write-Host "`n[DryRun] -NoStart flag is set: service startup and HTTP readiness probe would be skipped." -ForegroundColor Yellow
    }
    Write-Host "`n[DryRun] Simulation completed successfully. No system state was modified." -ForegroundColor Green
    exit 0
}

# ----------------------------------------------------------------------
# 16. Switch Junction and Update Scheduled Task with Hardened Rollback
# ----------------------------------------------------------------------
if (-not $SkipTaskUpdate) {
    Write-Host "`nSwitching 'current' junction and updating Task Scheduler..." -ForegroundColor Yellow

    # Capture previous junction target before touching anything
    $previousJunctionTarget = Get-JunctionTarget -Path $currentJunction
    if ($previousJunctionTarget) {
        Write-Host "  Preserved previous junction target: $previousJunctionTarget" -ForegroundColor Gray
    }

    # ------------------------------------------------------------------
    # HARD SAFETY GATE: Checked Task Backup & XML Validation
    # ------------------------------------------------------------------
    Write-Host "  Executing checked Task Scheduler backup before modifying state..." -ForegroundColor Yellow
    $taskBackupPath = Join-Path $configDir "task-backup-${timestampStr}.xml"
    $taskBackupLines = Invoke-SchtasksChecked -Arguments @("/Query", "/TN", "FootballTray-Backend", "/XML") -Operation "Backup Task Definition"
    $taskBackupXml = ($taskBackupLines -join "`n").Trim()

    # Confirm non-empty and write to file
    if ([string]::IsNullOrWhiteSpace($taskBackupXml)) {
        throw "CRITICAL SAFETY GATE: Task backup query returned empty output. Aborting before modifying production state."
    }
    Set-Content -Path $taskBackupPath -Value $taskBackupXml -Encoding Unicode

    # Confirm backup file exists and length > 0
    if (-not (Test-Path $taskBackupPath) -or (Get-Item $taskBackupPath).Length -eq 0) {
        throw "CRITICAL SAFETY GATE: Task backup file '$taskBackupPath' could not be written or is empty. Aborting."
    }

    # Parse and validate as XML
    try {
        $parsedBackup = Test-TaskXmlDefinition -XmlContent $taskBackupXml
        if (-not $parsedBackup.IsValid -or -not $parsedBackup.Command) {
            throw "Backup XML does not contain a valid <Task> structure."
        }
        Write-Host "  Task backup verified and valid XML saved to '$taskBackupPath'." -ForegroundColor Green
    } catch {
        throw "CRITICAL SAFETY GATE: Task backup XML validation failed: $_. Aborting before modifying production state."
    }

    # ------------------------------------------------------------------
    # Execute Switch with Enclosing Try/Catch for Dual Rollback
    # ------------------------------------------------------------------
    $migrationState = "NO_DB_CHANGE"
    try {
        # 1. Stop current running backend before database migration
        Write-Host "  Stopping running FootballTray-Backend task..."
        Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend") -Operation "Stop Current Backend"
        Start-Sleep -Seconds 2

        # 2. Execute production database migration before junction activation
        Write-Host "  Executing production database migration runner..." -ForegroundColor Yellow
        $migrateScriptRel = "dist\db\migrate-prod.js"
        $migrateScriptFull = Join-Path $releaseDir $migrateScriptRel
        if (-not (Test-Path $migrateScriptFull)) {
            throw "Production migration runner not found at '$migrateScriptFull'."
        }

        $nodeExe = "C:\Program Files\nodejs\node.exe"
        if (-not (Test-Path $nodeExe)) {
            $nodeExe = (Get-Command "node").Source
        }

        $migrateArgs = @("--env-file=$targetEnvPath", $migrateScriptRel)
        if ($BaselineLegacySchema) {
            $migrateArgs += "--baseline-legacy-schema"
            Write-Host "  Passing --baseline-legacy-schema flag to migration runner." -ForegroundColor Yellow
        }

        Push-Location $releaseDir
        try {
            $migOutput = & $nodeExe @migrateArgs 2>&1
            $migExit = $LASTEXITCODE

            $migText = ($migOutput -join "`n")
            Write-Host $migText

            # Parse machine-readable migration state protocol marker
            $stateMatch = $migOutput | Where-Object { $_ -match "^MIGRATION_STATE=(NO_DB_CHANGE|BASELINE_COMMITTED|MIGRATION_COMMITTED)$" } | Select-Object -Last 1
            if ($stateMatch) {
                $migrationState = ($stateMatch -replace "^MIGRATION_STATE=", "").Trim()
            } elseif ($migExit -eq 2) {
                # Exit code 2 is strictly reserved for post-verification failure after migration commit
                $migrationState = "MIGRATION_COMMITTED"
            }

            if ($migExit -ne 0) {
                throw "Production database migration failed with exit code $migExit (state=$migrationState)."
            }
        } finally {
            Pop-Location
        }
        Write-Host "  Production database migration and verification completed successfully (state=$migrationState)." -ForegroundColor Green

        # 3. Switch junction
        if (Test-Path $currentJunction) {
            cmd /c "rmdir `"$currentJunction`"" | Out-Null
        }
        New-Item -ItemType Junction -Path $currentJunction -Target $releaseDir | Out-Null
        Write-Host "  Junction 'current' updated to point to $releaseDir." -ForegroundColor Green

        # Write temp task XML and register
        $tempTaskXml = Join-Path $configDir "task-pending.xml"
        Set-Content -Path $tempTaskXml -Value $taskXml -Encoding Unicode

        Write-Host "  Registering updated scheduled task..."
        Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $tempTaskXml, "/F") -Operation "Register Updated Task"
        Remove-Item -Path $tempTaskXml -Force -ErrorAction SilentlyContinue

        # Start task
        if (-not $NoStart) {
            Write-Host "  Starting updated production backend..."
            Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend") -Operation "Start Updated Task"
            Start-Sleep -Seconds 4
        } else {
            Write-Host "  [-NoStart] Skipping service startup (task remains in stopped state)." -ForegroundColor Yellow
        }

        # --------------------------------------------------------------
        # Verification 1-7: Verify Deployment State Invariants
        # --------------------------------------------------------------
        Write-Host "  Verifying registered deployment state invariants..." -ForegroundColor Yellow

        # 1. current junction resolves exactly to $releaseDir
        $resolvedJunction = Get-JunctionTarget -Path $currentJunction
        $expectedNorm = [System.IO.Path]::GetFullPath($releaseDir).TrimEnd('\')
        if (-not $resolvedJunction -or (-not [string]::Equals([System.IO.Path]::GetFullPath($resolvedJunction).TrimEnd('\'), $expectedNorm, [System.StringComparison]::OrdinalIgnoreCase))) {
            throw "Junction verification failed: 'current' resolved to '$resolvedJunction', expected '$expectedNorm'."
        }

        # 2. Query task XML and parse
        $queryLines = Invoke-SchtasksChecked -Arguments @("/Query", "/TN", "FootballTray-Backend", "/XML") -Operation "Query Registered Task"
        $taskInfo = Test-TaskXmlDefinition -XmlContent ($queryLines -join "`n").Trim()
        if (-not $taskInfo.IsValid) {
            throw "Task verification failed: Invalid registered XML."
        }

        # 3. Task user is SYSTEM
        $taskUser = $taskInfo.UserId
        if ($taskUser -ne "S-1-5-18" -and $taskUser -notmatch "SYSTEM") {
            throw "Task user verification failed: expected SYSTEM (S-1-5-18), found '$taskUser'."
        }

        # 4. Task trigger is BootTrigger
        if (-not $taskInfo.HasBootTrigger) {
            throw "Task trigger verification failed: missing BootTrigger."
        }

        # 5. Task command is expected Node executable
        $taskCommand = $taskInfo.Command
        if (-not [string]::Equals($taskCommand, "C:\Program Files\nodejs\node.exe", [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Task command verification failed: expected 'C:\Program Files\nodejs\node.exe', found '$taskCommand'."
        }

        # 6. Task working directory is C:\ProgramData\FootballTray\backend\current
        $taskWorkingDir = $taskInfo.WorkingDirectory
        $expectedWorkDir = [System.IO.Path]::GetFullPath($currentJunction).TrimEnd('\')
        if (-not [string]::Equals([System.IO.Path]::GetFullPath($taskWorkingDir).TrimEnd('\'), $expectedWorkDir, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Task working directory verification failed: expected '$expectedWorkDir', found '$taskWorkingDir'."
        }

        # 7. Task arguments contain config/.env.production.local and dist/server.js
        $taskArgs = $taskInfo.Arguments
        if ($taskArgs -notlike "*--env-file=*config\.env.production.local*dist/server.js*" -and $taskArgs -notlike "*--env-file=*config/.env.production.local*dist/server.js*") {
            throw "Task arguments verification failed: expected config/.env.production.local and dist/server.js in '$taskArgs'."
        }

        # --------------------------------------------------------------
        # Verification 8: Readiness Validation (/api/tray)
        # --------------------------------------------------------------
        if (-not $NoStart) {
            Write-Host "  Validating backend readiness via /api/tray..."
            $ready = $false
            $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            while ($stopwatch.Elapsed.TotalSeconds -lt 30) {
                try {
                    $tray = Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3
                    if ($null -ne $tray -and
                        $null -ne $tray.PSObject.Properties["schemaVersion"] -and
                        -not [string]::IsNullOrWhiteSpace($tray.schemaVersion) -and
                        $null -ne $tray.PSObject.Properties["data"] -and
                        $null -ne $tray.data -and
                        $null -ne $tray.data.PSObject.Properties["matches"] -and
                        ($tray.data.matches -is [System.Array])) {
                        $ready = $true
                        $matchCount = @($tray.data.matches).Count
                        Write-Host "  Backend readiness verified via /api/tray: matches=$matchCount" -ForegroundColor Green
                        break
                    }
                } catch {
                    Start-Sleep -Seconds 2
                }
            }

            if (-not $ready) {
                throw "Readiness check probe timed out or returned non-ready response within 30 seconds."
            }
        } else {
            Write-Host "  [-NoStart] Skipping HTTP readiness validation probe (service intentionally stopped)." -ForegroundColor Yellow

            # Confirm task is not running
            $statusLines = Invoke-SchtasksChecked -Arguments @("/Query", "/TN", "FootballTray-Backend", "/FO", "CSV", "/NH") -Operation "Query Registered Task Status"
            $statusCsv = $statusLines -join ""
            if ($statusCsv -match '"Running"') {
                throw "Task state verification failed: task is running despite -NoStart flag."
            }
            Write-Host "  Task state verified stopped/ready." -ForegroundColor Green
        }

        # --------------------------------------------------------------
        # Verification 9: Update Manifest to Verified Status
        # --------------------------------------------------------------
        Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "verified"
        Write-Host "  Deployment manifest updated to 'verified'." -ForegroundColor Green

    } catch {
        Write-Host "CRITICAL: Deployment verification failed ($($_.Exception.Message))! Initiating automatic two-way rollback..." -ForegroundColor Red

        if ($migrationState -eq "MIGRATION_COMMITTED") {
            Write-Warning "CRITICAL WARNING: The database migration had already committed before failure occurred. The database schema may already be forward-migrated, and no automatic database down-migration was attempted. Automatic database down-migration is never performed under any circumstance."
        } elseif ($migrationState -eq "BASELINE_COMMITTED") {
            Write-Warning "CRITICAL WARNING: Legacy baseline metadata (migrations 0000 and 0001) was committed to drizzle.__drizzle_migrations, but the subsequent Phase 2A migration (0002) rolled back. Verified legacy baseline metadata remains committed in the database. Phase 2A schema changes were not applied. No automatic database down-migration or baseline deletion was attempted."
        }

        # 1. Stop failed task
        try {
            Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend") -Operation "Stop Failed Task"
        } catch {
            Write-Warning "Could not stop failed task (may already be stopped): $_"
        }
        Start-Sleep -Seconds 2

        # 2. Mark new release manifest failed
        Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "failed"
        Write-Host "  Failed release manifest marked as 'failed'." -ForegroundColor Yellow

        # 3. Restore previous junction
        if ($previousJunctionTarget -and (Test-Path $previousJunctionTarget)) {
            Write-Host "  Restoring 'current' junction to '$previousJunctionTarget'..."
            if (Test-Path $currentJunction) {
                cmd /c "rmdir `"$currentJunction`"" | Out-Null
            }
            New-Item -ItemType Junction -Path $currentJunction -Target $previousJunctionTarget | Out-Null
        }

        # 4. Restore previous Task Scheduler XML
        if (Test-Path $taskBackupPath) {
            Write-Host "  Restoring previous Task Scheduler definition from '$taskBackupPath'..."
            Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $taskBackupPath, "/F") -Operation "Restore Task Definition from Backup"
        }

        # 5. Start restored task
        if (-not $NoStart) {
            Write-Host "  Starting restored backend service..."
            Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend") -Operation "Start Restored Backend Service"
            Start-Sleep -Seconds 4
        } else {
            Write-Host "  [-NoStart] Restored previous release configuration without starting the service." -ForegroundColor Yellow
        }

        # 6. Verify restored task configuration
        $restoredQuery = Invoke-SchtasksChecked -Arguments @("/Query", "/TN", "FootballTray-Backend", "/XML") -Operation "Verify Restored Task XML"
        $restoredInfo = Test-TaskXmlDefinition -XmlContent ($restoredQuery -join "`n").Trim()
        if (-not $restoredInfo.IsValid) {
            throw "CRITICAL: Restored task definition is invalid XML!"
        }

        # 7. Verify readiness of restored service via /api/tray
        if (-not $NoStart) {
            Write-Host "  Verifying readiness of restored service via /api/tray..."
            $rollbackReady = $false
            $rbSw = [System.Diagnostics.Stopwatch]::StartNew()
            while ($rbSw.Elapsed.TotalSeconds -lt 25) {
                try {
                    $rtray = Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3
                    if ($null -ne $rtray -and
                        $null -ne $rtray.PSObject.Properties["schemaVersion"] -and
                        -not [string]::IsNullOrWhiteSpace($rtray.schemaVersion) -and
                        $null -ne $rtray.PSObject.Properties["data"] -and
                        $null -ne $rtray.data -and
                        $null -ne $rtray.data.PSObject.Properties["matches"] -and
                        ($rtray.data.matches -is [System.Array])) {
                        $rollbackReady = $true
                        break
                    }
                } catch {
                    Start-Sleep -Seconds 2
                }
            }

            # 8. Report rollback result
            if ($rollbackReady) {
                Write-Host "  [ROLLBACK SUCCESS] Restored previous release and verified service readiness." -ForegroundColor Yellow
            } else {
                Write-Host "CRITICAL: Rollback readiness verification failed! The previous service did not recover within the timeout period." -ForegroundColor Red
                throw "Deployment failed and rollback service verification also failed."
            }
        } else {
            Write-Host "  [ROLLBACK SUCCESS] Restored previous release and verified task configuration (service stopped)." -ForegroundColor Yellow
        }

        if ($migrationState -eq "MIGRATION_COMMITTED") {
            throw "Deployment aborted due to verification failure ($($_.Exception.Message)). Application service was rolled back to '$previousJunctionTarget', but database schema may already be forward-migrated and no automatic database down-migration was attempted."
        } elseif ($migrationState -eq "BASELINE_COMMITTED") {
            throw "Deployment aborted due to verification failure ($($_.Exception.Message)). Application service was rolled back to '$previousJunctionTarget'. Phase 2A database migration rolled back, but verified legacy baseline metadata remains committed in the database. No automatic database down-migration or baseline deletion was attempted."
        } else {
            throw "Deployment aborted due to verification failure ($($_.Exception.Message)). System was successfully rolled back to '$previousJunctionTarget' (database unchanged)."
        }
    }
}

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " Production Backend Deployment Completed Successfully!" -ForegroundColor Green
Write-Host " Deployed Release: $releaseId (status: verified)" -ForegroundColor Green
Write-Host " Active Path: $currentJunction" -ForegroundColor Green
if ($NoStart) {
    Write-Host " Service Status: STOPPED (-NoStart mode -- ready for verification/audit)" -ForegroundColor Yellow
}
Write-Host "============================================================" -ForegroundColor Green
exit 0
