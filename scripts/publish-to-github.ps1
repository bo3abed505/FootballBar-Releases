<#
.SYNOPSIS
    Robust script to publish FootballTray release assets to GitHub Releases with retries.

.DESCRIPTION
    Creates the GitHub release if needed and uploads each asset individually with exponential
    backoff retry logic to handle transient GitHub API or upload gateway errors (HTTP 502 / 504 / 404).

.PARAMETER Tag
    The release tag (e.g. "v1.3.0"). Default is "v1.3.0".

.PARAMETER Repo
    The GitHub repository (e.g. "bo3abed505/FootballBar-Releases").

.PARAMETER Title
    The release title. Default is "FootballTray v1.3.0".

.PARAMETER Target
    The target branch for the release tag. Default is "main".

.PARAMETER ReleaseDir
    Path to the directory containing the built release artifacts.
#>

[CmdletBinding()]
param(
    [string]$Tag = "v1.3.1",
    [string]$Repo = "bo3abed505/FootballBar-Releases",
    [string]$Title = "FootballBar v1.3.1",
    [string]$Target = "main",
    [string]$ReleaseDir = "artifacts/releases/v1.3.1",
    [int]$MaxRetries = 5,
    [int]$RetryDelaySec = 5
)

$ErrorActionPreference = "Continue"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$resolvedReleaseDir = Join-Path $repoRoot $ReleaseDir

if (-not (Test-Path $resolvedReleaseDir)) {
    Write-Error "Release directory not found: $resolvedReleaseDir"
    exit 1
}

$notesFile = Join-Path $resolvedReleaseDir "RELEASE_NOTES.txt"
if (-not (Test-Path $notesFile)) {
    Write-Error "RELEASE_NOTES.txt not found in $resolvedReleaseDir"
    exit 1
}

$expectedAssets = @(
    (Join-Path $resolvedReleaseDir "release-manifest.json"),
    (Join-Path $resolvedReleaseDir "ota-telemetry-manifest.json"),
    (Join-Path $resolvedReleaseDir "RELEASE_NOTES.txt"),
    (Join-Path $resolvedReleaseDir "SHA256SUMS.txt"),
    (Join-Path $resolvedReleaseDir "VERSION.txt"),
    (Join-Path $resolvedReleaseDir "FootballTray-$Tag-win-x64.msix"),
    (Join-Path $resolvedReleaseDir "FootballTray-Setup-$Tag-win-x64.exe"),
    (Join-Path $resolvedReleaseDir "FootballTray-$Tag-win-x64.zip")
)

Write-Host "`n[1/4] Verifying local release artifacts on disk..." -ForegroundColor Yellow
foreach ($asset in $expectedAssets) {
    if (-not (Test-Path $asset)) {
        Write-Error "Required release asset missing on disk: $asset"
        exit 1
    }
    $size = (Get-Item $asset).Length
    $sizeFormatted = if ($size -gt 1MB) { "$([math]::Round($size / 1MB, 2)) MB" } else { "$size B" }
    Write-Host "  [OK] $([System.IO.Path]::GetFileName($asset)) ($sizeFormatted)" -ForegroundColor Green
}

# Function to execute a command with retries
function Invoke-WithRetry {
    param(
        [scriptblock]$ScriptBlock,
        [string]$Description,
        [int]$Attempts = $MaxRetries,
        [int]$DelaySec = $RetryDelaySec
    )

    $currentAttempt = 1
    while ($currentAttempt -le $Attempts) {
        Write-Host "  -> Attempt $currentAttempt of $Attempts - $Description" -ForegroundColor Cyan
        $result = & $ScriptBlock
        if ($LASTEXITCODE -eq 0) {
            return $result
        }

        Write-Warning "  Attempt $currentAttempt failed (ExitCode $LASTEXITCODE)."
        if ($currentAttempt -lt $Attempts) {
            $wait = $DelaySec * $currentAttempt
            Write-Host "     Waiting ${wait}s before retry..." -ForegroundColor Gray
            Start-Sleep -Seconds $wait
        } else {
            throw "Failed after $Attempts attempts: $Description."
        }
        $currentAttempt++
    }
}

Write-Host "`n[2/4] Checking remote release status for $Tag on $Repo..." -ForegroundColor Yellow
$releaseExists = $false
$checkOutput = gh release view $Tag --repo $Repo --json tagName,url 2>&1
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($checkOutput)) {
    try {
        $existing = $checkOutput | ConvertFrom-Json
        if ($existing -and $existing.tagName -eq $Tag) {
            $releaseExists = $true
            Write-Host "  Release $Tag already exists ($($existing.url))." -ForegroundColor Green
        }
    } catch { }
}

if (-not $releaseExists) {
    Write-Host "`nCreating GitHub release entity $Tag..." -ForegroundColor Yellow
    Invoke-WithRetry -Description "Create release entity $Tag on $Repo" -Attempts 3 -ScriptBlock {
        $createOutput = gh release create $Tag `
            --target $Target `
            --repo $Repo `
            --title $Title `
            --notes-file $notesFile 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "gh release create output: $createOutput"
        }
        return $createOutput
    }
} else {
    Write-Host "Ensuring release metadata for $Tag is up to date..." -ForegroundColor Yellow
    gh release edit $Tag --repo $Repo --title $Title --notes-file $notesFile 2>&1 | Out-Null
}

Write-Host "`n[3/4] Uploading release assets with retry logic..." -ForegroundColor Yellow

# Query currently uploaded assets on remote release to skip already uploaded and verified ones
$remoteAssetsMap = @{}
$assetsOutput = gh release view $Tag --repo $Repo --json assets 2>&1
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($assetsOutput)) {
    try {
        $currentJson = $assetsOutput | ConvertFrom-Json
        if ($currentJson -and $currentJson.assets) {
            foreach ($ra in $currentJson.assets) {
                $remoteAssetsMap[$ra.name] = $ra.size
            }
        }
    } catch { }
}

foreach ($asset in $expectedAssets) {
    $fileName = [System.IO.Path]::GetFileName($asset)
    $fileSize = (Get-Item $asset).Length

    if ($remoteAssetsMap.ContainsKey($fileName) -and $remoteAssetsMap[$fileName] -eq $fileSize) {
        Write-Host "  [SKIP] $fileName is already uploaded with matching size ($fileSize bytes)." -ForegroundColor Gray
        continue
    }

    Invoke-WithRetry -Description "Upload asset: $fileName ($fileSize bytes)" -Attempts $MaxRetries -DelaySec $RetryDelaySec -ScriptBlock {
        $uploadOutput = gh release upload $Tag $asset --repo $Repo --clobber 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "gh release upload output: $uploadOutput"
        }
        return $uploadOutput
    }
}

Write-Host "`n[4/4] Verifying live release and all assets..." -ForegroundColor Yellow
$finalVerificationRaw = gh release view $Tag --repo $Repo --json tagName,name,url,assets 2>&1
$finalVerification = $finalVerificationRaw | ConvertFrom-Json

Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " RELEASE IS LIVE: $($finalVerification.url)" -ForegroundColor Green
Write-Host " Tag:             $($finalVerification.tagName)" -ForegroundColor Green
Write-Host " Title:           $($finalVerification.name)" -ForegroundColor Green
Write-Host " Assets Uploaded: $($finalVerification.assets.Count)" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

foreach ($a in $finalVerification.assets) {
    Write-Host "  * $($a.name) ($($a.size) bytes)" -ForegroundColor Cyan
}

# Verify all expected files are present
$missing = @()
$uploadedNames = $finalVerification.assets | ForEach-Object { $_.name }
foreach ($asset in $expectedAssets) {
    $name = [System.IO.Path]::GetFileName($asset)
    if ($name -notin $uploadedNames) {
        $missing += $name
    }
}

if ($missing.Count -gt 0) {
    Write-Error "Verification failed: The following expected assets are missing on the remote release: $($missing -join ', ')"
    exit 1
}

Write-Host "`nAll $($expectedAssets.Count) verified release assets are live on GitHub!" -ForegroundColor Green
exit 0
