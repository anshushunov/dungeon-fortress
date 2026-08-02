[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-claimed-sha256.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-claimed-sha256-test-" + [Guid]::NewGuid().ToString("N"))
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-Checker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Root,

        [switch]$IncludeDocs
    )

    $args = @("-RepoRoot", $Root)
    if ($IncludeDocs) {
        $args += "-IncludeDocs"
    }
    $output = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        @args 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $gitRoot = Join-Path $testRoot "repo"
    New-Item -ItemType Directory -Force -Path $gitRoot | Out-Null
    & git -C $gitRoot init -q
    & git -C $gitRoot config user.email "test@example.com"
    & git -C $gitRoot config user.name "Test"
    & git -C $gitRoot config commit.gpgsign false

    $trackedPath = Join-Path $gitRoot "tracked.txt"
    [IO.File]::WriteAllText($trackedPath, "tracked content`n", $utf8)
    & git -C $gitRoot add "tracked.txt"
    & git -C $gitRoot commit -q -m "tracked"
    $trackedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $trackedPath).Hash.ToLowerInvariant()

    $untrackedPath = Join-Path $gitRoot "untracked.txt"
    [IO.File]::WriteAllText($untrackedPath, "untracked content`n", $utf8)
    $untrackedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $untrackedPath).Hash.ToLowerInvariant()

    $evidenceDir = Join-Path $gitRoot "evidence"
    New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null

    $goodJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "a", "path": "tracked.txt", "imageSha256": "$trackedHash" }
  ]
}
"@
    $goodPath = Join-Path $evidenceDir "good.json"
    [IO.File]::WriteAllText($goodPath, $goodJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 0) {
        throw "Blob-matching claim was rejected. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $goodPath -Force

    $crlfTrapJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "b", "path": "tracked.txt", "imageSha256": "$($untrackedHash)" }
  ]
}
"@
    $crlfTrapPath = Join-Path $evidenceDir "crlf-trap.json"
    [IO.File]::WriteAllText($crlfTrapPath, $crlfTrapJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 1 -or $result.Output -notmatch "mismatch") {
        throw "Tracked file with non-blob hash was not flagged. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $crlfTrapPath -Force

    $untrackedJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "c", "path": "untracked.txt", "imageSha256": "$untrackedHash" }
  ]
}
"@
    $untrackedPathJson = Join-Path $evidenceDir "untracked.json"
    [IO.File]::WriteAllText($untrackedPathJson, $untrackedJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 0) {
        throw "Untracked working-copy claim was rejected. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $untrackedPathJson -Force

    $missingJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "d", "path": "missing.txt", "imageSha256": "$untrackedHash" }
  ]
}
"@
    $missingPathJson = Join-Path $evidenceDir "missing.json"
    [IO.File]::WriteAllText($missingPathJson, $missingJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 0) {
        throw "Missing-file claim should be informational, not a failure. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $missingPathJson -Force

    [ordered]@{
        event = "check_claimed_sha256_test"
        status = "ok"
        blobMatch = $true
        crlfTrapFlagged = $true
        untrackedWorkingAccepted = $true
        missingInformational = $true
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
