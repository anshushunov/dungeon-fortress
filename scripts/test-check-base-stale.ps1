[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-base-stale.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-base-stale-test-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $gitRoot = Join-Path $testRoot "repo"
    New-Item -ItemType Directory -Force -Path $gitRoot | Out-Null
    & git -C $gitRoot init -q
    & git -C $gitRoot config user.email "test@example.com"
    & git -C $gitRoot config user.name "Test"
    & git -C $gitRoot config commit.gpgsign false

    $file = Join-Path $gitRoot "a.txt"
    [IO.File]::WriteAllText($file, "one`n", [Text.UTF8Encoding]::new($false))
    & git -C $gitRoot add "a.txt"
    & git -C $gitRoot commit -q -m "one"
    $firstCommit = (& git -C $gitRoot rev-parse HEAD).Trim()

    [IO.File]::WriteAllText($file, "two`n", [Text.UTF8Encoding]::new($false))
    & git -C $gitRoot commit -q -am "two"
    $secondCommit = (& git -C $gitRoot rev-parse HEAD).Trim()

    # Set up the remote-tracking ref to the current HEAD before the fresh check.
    & git -C $gitRoot update-ref refs/remotes/origin/main $secondCommit

    $outputFresh = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -RepoRoot $gitRoot `
        -Remote "origin" `
        -Branch "main" 2>&1)
    $freshExit = $LASTEXITCODE
    $freshText = ($outputFresh | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($freshExit -ne 0 -or $freshText -notmatch '"status":"fresh"') {
        throw "Fresh base was not reported fresh. exit=$freshExit output=$freshText"
    }

    # Move remote main back one commit -> local HEAD is ahead and not behind
    & git -C $gitRoot update-ref refs/remotes/origin/main $firstCommit

    $outputBehind = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -RepoRoot $gitRoot `
        -Remote "origin" `
        -Branch "main" 2>&1)
    $behindExit = $LASTEXITCODE
    $behindText = ($outputBehind | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($behindText -notmatch '"behind":0' -or $behindText -notmatch '"ahead":1') {
        throw "Local-ahead base was not measured. output=$behindText"
    }

    # Move remote main forward beyond local HEAD -> local is behind.
    [IO.File]::WriteAllText($file, "three`n", [Text.UTF8Encoding]::new($false))
    & git -C $gitRoot commit -q -am "three"
    $thirdCommit = (& git -C $gitRoot rev-parse HEAD).Trim()
    # Point the remote-tracking ref at the new commit, then put local HEAD back
    # one commit so the base really is stale (remote ahead of local).
    & git -C $gitRoot update-ref refs/remotes/origin/main $thirdCommit
    & git -C $gitRoot reset -q --hard $secondCommit

    $outputStale = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -RepoRoot $gitRoot `
        -Remote "origin" `
        -Branch "main" 2>&1)
    $staleExit = $LASTEXITCODE
    $staleText = ($outputStale | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($staleExit -ne 1 -or $staleText -notmatch '"status":"stale"' -or $staleText -notmatch '"behind":1') {
        throw "Stale base was not reported stale. exit=$staleExit output=$staleText"
    }

    [ordered]@{
        event = "check_base_stale_test"
        status = "ok"
        freshExit0 = $true
        aheadMeasured = $true
        staleExit1 = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
