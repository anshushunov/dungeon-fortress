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
    # Staging the CRLF fixture below is expected to rewrite line endings; the
    # default safecrlf warning on stderr would abort this test under Stop.
    & git -C $gitRoot config core.safecrlf false

    # The trap of Issue #179 needs the repository-level line-ending policy: the
    # index keeps LF while the working copy may still hold CRLF.
    $attributesPath = Join-Path $gitRoot ".gitattributes"
    [IO.File]::WriteAllText($attributesPath, "* text=auto eol=lf`n", $utf8)
    & git -C $gitRoot add ".gitattributes"
    & git -C $gitRoot commit -q -m "attributes"

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
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"blob-match"') {
        throw "Blob-matching claim was rejected. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $goodPath -Force

    # The literal trap of Issue #179: a CRLF working copy over an LF blob. The
    # claimed hash is the one a naive Get-FileHash of the working copy produces,
    # so the only thing separating it from a true tree hash is which side the
    # script compares against first.
    $crlfPath = Join-Path $gitRoot "crlf.txt"
    [IO.File]::WriteAllText($crlfPath, "line one`r`nline two`r`n", $utf8)
    & git -C $gitRoot add "crlf.txt"
    & git -C $gitRoot commit -q -m "crlf"
    $crlfWorkingHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $crlfPath).Hash.ToLowerInvariant()
    $crlfBlobBytes = Join-Path $testRoot "crlf-blob.bin"
    & cmd /c "git -C `"$gitRoot`" cat-file blob HEAD:crlf.txt > `"$crlfBlobBytes`"" | Out-Null
    $crlfCommittedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $crlfBlobBytes).Hash.ToLowerInvariant()
    Remove-Item -LiteralPath $crlfBlobBytes -Force
    if ($crlfWorkingHash -eq $crlfCommittedHash) {
        throw "The CRLF trap did not materialize: working copy and blob hash alike ($crlfWorkingHash)."
    }

    $workingCopyOnlyJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "b", "path": "crlf.txt", "imageSha256": "$crlfWorkingHash" }
  ]
}
"@
    $workingCopyOnlyPath = Join-Path $evidenceDir "working-copy-only.json"
    [IO.File]::WriteAllText($workingCopyOnlyPath, $workingCopyOnlyJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    # Assert the per-claim status, not the summary line: the summary prints the
    # words "working-copy-only" on every failing run whatever the cause.
    if ($result.ExitCode -ne 1 -or $result.Output -notmatch '"status":"working-copy-only"') {
        throw "Working-copy hash of a tracked file was not flagged as working-copy-only. Output: $($result.Output)"
    }
    if ($result.Output -match '"status":"blob-match"') {
        throw "Working-copy hash was accepted as a tree hash. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $workingCopyOnlyPath -Force

    # The mirror case: the committed blob hash of the same trapped file must be
    # accepted, so the check cannot pass by rejecting everything.
    $blobOfTrappedJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "b2", "path": "crlf.txt", "imageSha256": "$crlfCommittedHash" }
  ]
}
"@
    $blobOfTrappedPath = Join-Path $evidenceDir "blob-of-trapped.json"
    [IO.File]::WriteAllText($blobOfTrappedPath, $blobOfTrappedJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"blob-match"') {
        throw "Committed blob hash of the trapped file was rejected. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $blobOfTrappedPath -Force

    $mismatchJson = @"
{
  "schemaVersion": 1,
  "captures": [
    { "name": "c", "path": "tracked.txt", "imageSha256": "$($untrackedHash)" }
  ]
}
"@
    $mismatchPath = Join-Path $evidenceDir "mismatch.json"
    [IO.File]::WriteAllText($mismatchPath, $mismatchJson, $utf8)
    $result = Invoke-Checker -Root $gitRoot
    if ($result.ExitCode -ne 1 -or $result.Output -notmatch '"status":"mismatch"') {
        throw "Tracked file with a hash of neither side was not flagged. Output: $($result.Output)"
    }
    Remove-Item -LiteralPath $mismatchPath -Force

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
        workingCopyOnlyFlagged = $true
        blobOfTrappedFileAccepted = $true
        mismatchFlagged = $true
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
