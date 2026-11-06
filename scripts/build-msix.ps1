<#
.SYNOPSIS
    Builds a packaged Windows Desktop Bridge MSIX package for Microsoft Store release.
.DESCRIPTION
    Packages the compiled win-x64 standalone publish directory along with Package.appxmanifest
    and required Store visual assets into an MSIX container using makeappx.exe.
.PARAMETER Version
    Target release version (e.g. 1.3.0). Defaults to the content of the root VERSION file.
.PARAMETER ReleaseDir
    Release output directory. Defaults to artifacts/releases/v$Version.
.PARAMETER PublishDir
    Directory containing published win-x64 binaries. Defaults to $ReleaseDir/win-x64.
.PARAMETER OutputFile
    Target MSIX file path. Defaults to $ReleaseDir/FootballTray-v$Version-win-x64.msix.
.PARAMETER SkipPublish
    When specified, reuses the existing PublishDir without running dotnet publish.
#>

[CmdletBinding()]
param (
    [string]$Version,
    [string]$ReleaseDir,
    [string]$PublishDir,
    [string]$OutputFile,
    [string]$IdentityName = "FootballTray.Client",
    [string]$Publisher = "CN=FootballTray",
    [switch]$SkipSign,
    [switch]$SkipPublish
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
        $Version = "1.3.0"
    }
}

$numericCore = $null
if ($Version -match '^(\d+\.\d+\.\d+)(?:[+-].*)?$') {
    $numericCore = $Matches[1]
} else {
    throw "Target version '$Version' cannot produce a valid numeric Major.Minor.Patch core."
}
$packageVersion = "$numericCore.0"

if ([string]::IsNullOrWhiteSpace($ReleaseDir)) {
    $ReleaseDir = Join-Path $RootDir "artifacts\releases\v$Version"
}

if ([string]::IsNullOrWhiteSpace($PublishDir)) {
    $PublishDir = Join-Path $ReleaseDir "win-x64"
}

if ([string]::IsNullOrWhiteSpace($OutputFile)) {
    $OutputFile = Join-Path $ReleaseDir "FootballTray-v$Version-win-x64.msix"
}

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " FootballTray MSIX Packaging — v$Version ($packageVersion)" -ForegroundColor Cyan
Write-Host " Target MSIX: $OutputFile" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Locate makeappx.exe
Write-Host "`n[1/5] Locating makeappx.exe from Windows SDK..." -ForegroundColor Yellow
$sdkKitsPath = "C:\Program Files (x86)\Windows Kits\10\bin"
$makeAppxCandidates = @()
if (Test-Path $sdkKitsPath) {
    $makeAppxCandidates = @(Get-ChildItem -Path "$sdkKitsPath\*\x64\makeappx.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
}

if ($makeAppxCandidates.Count -eq 0) {
    throw "makeappx.exe not found in '$sdkKitsPath\*\x64\makeappx.exe'. Ensure Windows 10/11 SDK is installed."
}

$makeAppx = $makeAppxCandidates[0].FullName
Write-Host "  Found makeappx.exe at: $makeAppx" -ForegroundColor Green

# 2. Check or build publish directory
$exePath = Join-Path $PublishDir "FootballTray.exe"
if (-not $SkipPublish -and -not (Test-Path $exePath)) {
    Write-Host "`n[2/5] Publishing client for win-x64..." -ForegroundColor Yellow
    $projectFile = Join-Path $RootDir "src\client\FootballTray.Client\FootballTray.Client.csproj"
    & dotnet publish $projectFile -c Release -r win-x64 --self-contained -o $PublishDir /p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host "`n[2/5] Using existing publish directory: $PublishDir" -ForegroundColor Green
}

if (-not (Test-Path $exePath)) {
    throw "Missing required executable at '$exePath'."
}

# 3. Prepare MSIX staging directory
Write-Host "`n[3/5] Staging files for MSIX packaging..." -ForegroundColor Yellow
$stagingDir = Join-Path $ReleaseDir "msix-staging"
if (Test-Path $stagingDir) {
    Remove-Item -Path $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

# Copy binary files from publish folder, skipping unpackaged scripts
Get-ChildItem -Path $PublishDir | Where-Object {
    $_.Name -ne "install-client.ps1" -and $_.Name -ne "uninstall-client.ps1"
} | ForEach-Object {
    Copy-Item -Path $_.FullName -Destination $stagingDir -Recurse -Force
}

# Copy Assets
$clientAssetsDir = Join-Path $RootDir "src\client\FootballTray.Client\Assets"
$stagingAssetsDir = Join-Path $stagingDir "Assets"
if (-not (Test-Path $stagingAssetsDir)) {
    New-Item -ItemType Directory -Path $stagingAssetsDir -Force | Out-Null
}
if (Test-Path $clientAssetsDir) {
    Copy-Item -Path "$clientAssetsDir\*" -Destination $stagingAssetsDir -Recurse -Force
}

# 4. Generate AppxManifest.xml
Write-Host "`n[4/5] Configuring AppxManifest.xml with version $packageVersion..." -ForegroundColor Yellow
$manifestSource = Join-Path $RootDir "src\client\FootballTray.Client\Package.appxmanifest"
if (-not (Test-Path $manifestSource)) {
    throw "Missing Package.appxmanifest at '$manifestSource'."
}

$manifestContent = Get-Content -LiteralPath $manifestSource -Raw
# Ensure version in manifest matches current build
$manifestContent = [System.Text.RegularExpressions.Regex]::Replace(
    $manifestContent,
    'Version="[^"]+"',
    "Version=`"$packageVersion`""
)

if (-not [string]::IsNullOrWhiteSpace($IdentityName)) {
    $manifestContent = [System.Text.RegularExpressions.Regex]::Replace(
        $manifestContent,
        '(<Identity\s+[^>]*Name=")[^"]+(")',
        "${1}$IdentityName${2}"
    )
}
if (-not [string]::IsNullOrWhiteSpace($Publisher)) {
    $manifestContent = [System.Text.RegularExpressions.Regex]::Replace(
        $manifestContent,
        '(<Identity\s+[^>]*Publisher=")[^"]+(")',
        "${1}$Publisher${2}"
    )
}

$targetManifest = Join-Path $stagingDir "AppxManifest.xml"
[System.IO.File]::WriteAllText($targetManifest, $manifestContent, [System.Text.UTF8Encoding]::new($false))
Write-Host "  AppxManifest.xml written to staging directory." -ForegroundColor Green

# 5. Execute makeappx.exe
Write-Host "`n[5/5] Creating MSIX package: $OutputFile..." -ForegroundColor Yellow
$outDir = Split-Path -Parent $OutputFile
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

& $makeAppx pack /v /h SHA256 /o /d $stagingDir /p $OutputFile
if ($LASTEXITCODE -ne 0) {
    throw "makeappx.exe failed with exit code $LASTEXITCODE"
}

# Cleanup staging
Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

# 6. Signing & Certificate Export
if ($SkipSign) {
    Write-Host "`n[6/6] Skipping signing (-SkipSign specified). MSIX is ready for Microsoft Partner Center upload." -ForegroundColor Cyan
} else {
    Write-Host "`n[6/6] Signing MSIX package..." -ForegroundColor Yellow
    $signtoolCandidates = @()
    if (Test-Path $sdkKitsPath) {
        $signtoolCandidates = @(Get-ChildItem -Path "$sdkKitsPath\*\x64\signtool.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending)
    }
    if ($signtoolCandidates.Count -eq 0) {
        throw "signtool.exe not found in '$sdkKitsPath\*\x64\signtool.exe'."
    }
    $signtool = $signtoolCandidates[0].FullName
    Write-Host "  Found signtool.exe at: $signtool" -ForegroundColor Green

    $cert = Get-ChildItem -Path Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey } |
        Select-Object -First 1

    if (-not $cert) {
        Write-Host "  Generating self-signed code signing certificate for '$Publisher'..." -ForegroundColor Yellow
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -CertStoreLocation "Cert:\CurrentUser\My" -HashAlgorithm "SHA256"
    }

    $cerPath = Join-Path $ReleaseDir "FootballTray-v$Version-win-x64.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
    Write-Host "  Exported public certificate: $cerPath" -ForegroundColor Green

    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $OutputFile
    if ($LASTEXITCODE -ne 0) {
        throw "signtool.exe failed to sign MSIX package with exit code $LASTEXITCODE"
    }
    Write-Host "  Signed MSIX package using cert thumbprint $($cert.Thumbprint)" -ForegroundColor Green
}

$msixSizeMb = [math]::Round(((Get-Item $OutputFile).Length / 1MB), 2)
Write-Host "`n============================================================" -ForegroundColor Green
Write-Host " MSIX package created successfully! ($msixSizeMb MB)" -ForegroundColor Green
Write-Host " Output: $OutputFile" -ForegroundColor Green
Write-Host "============================================================" -ForegroundColor Green

exit 0
