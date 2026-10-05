$ErrorActionPreference = "Stop"
$path = "$env:LOCALAPPDATA\FootballTray\preferences.json"

if (-not (Test-Path $path)) {
    Write-Host "[SANITIZE] Preferences file not found at: $path"
    exit 0
}

$raw = Get-Content $path -Raw
$prefs = $raw | ConvertFrom-Json

$initialComps = if ($prefs.followedCompetitions) { @($prefs.followedCompetitions).Count } else { 0 }
$initialTeams = if ($prefs.followedTeams) { @($prefs.followedTeams).Count } else { 0 }

if ($prefs.followedCompetitions) {
    $filteredComps = @($prefs.followedCompetitions | Where-Object {
        $name = $_.name
        $id = $_.id
        $code = $_.code
        -not ($name -match '(?i)mock' -or $id -match '(?i)mock' -or $code -match '(?i)mock')
    })
    $prefs.followedCompetitions = $filteredComps
}

if ($prefs.followedTeams) {
    $filteredTeams = @($prefs.followedTeams | Where-Object {
        $name = $_.name
        $id = $_.id
        $code = $_.code
        -not ($name -match '(?i)mock' -or $id -match '(?i)mock' -or $code -match '(?i)mock')
    })
    $prefs.followedTeams = $filteredTeams
}

if ($prefs.followedCompetitionIds) {
    $prefs.followedCompetitionIds = @($prefs.followedCompetitionIds | Where-Object { $_ -notmatch '(?i)mock' })
}

if ($prefs.followedTeamIds) {
    $prefs.followedTeamIds = @($prefs.followedTeamIds | Where-Object { $_ -notmatch '(?i)mock' })
}

$finalComps = if ($prefs.followedCompetitions) { @($prefs.followedCompetitions).Count } else { 0 }
$finalTeams = if ($prefs.followedTeams) { @($prefs.followedTeams).Count } else { 0 }

$prefs | ConvertTo-Json -Depth 10 | Set-Content $path -Encoding UTF8
Write-Host "[SANITIZE] Cleaned $path"
Write-Host "[SANITIZE] Purged $($initialComps - $finalComps) mock competitions and $($initialTeams - $finalTeams) mock teams."
