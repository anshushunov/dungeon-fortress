[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #253. scripts/check-no-worktrees-in-root.ps1 (Issue #287) has existed
# since PR #297 and its RED/GREEN behaviour was proven once by hand against
# the real root copy (evidence/287-worktree-cleanup-result.json), but it was
# never wired into any automatic run and never got a regression test of its
# own - the same "only proven once, by hand" gap rule 17 itself was in
# before this Issue. This is that test, built the same way every other
# dependency-free scripts-stage guard's test is (see test-check-base-stale.ps1,
# test-check-root-on-main.ps1): a disposable fixture git repository under
# .artifacts, never the real root copy.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-no-worktrees-in-root.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-no-worktrees-in-root-test-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Guard {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetRepoRoot
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $scriptPath -RepoRoot $TargetRepoRoot 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
        }
    }
    finally {
        $ErrorActionPreference = $previous
    }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $mainRepo = Join-Path $testRoot "root"
    New-Item -ItemType Directory -Force -Path $mainRepo | Out-Null
    & git init -q -b main $mainRepo
    & git -C $mainRepo config user.email "test@example.com"
    & git -C $mainRepo config user.name "Test"
    & git -C $mainRepo config commit.gpgsign false

    $file = Join-Path $mainRepo "a.txt"
    [IO.File]::WriteAllText($file, "one`n", [Text.UTF8Encoding]::new($false))
    & git -C $mainRepo add "a.txt"
    & git -C $mainRepo commit -q -m "one"

    # Case 1: legitimate state - a sibling worktree next to the root, the
    # shape 'git worktree add ../_wt-<slug> ...' (AGENTS.md, rule 17) always
    # produces.
    $siblingWorktree = Join-Path $testRoot "sibling-wt"
    & git -C $mainRepo worktree add -q $siblingWorktree -b "agent/task-branch"
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture setup: 'git worktree add' for the sibling failed."
    }

    $legitimateResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($legitimateResult.ExitCode -ne 0 -or $legitimateResult.Text -notmatch '"status":"ok"') {
        throw "Legitimate state (sibling worktree only) was not reported ok. exit=$($legitimateResult.ExitCode) output=$($legitimateResult.Text)"
    }

    # Case 2: violation - a worktree registered at a path nested inside the
    # root copy. This is the exact shape of the 2026-08-04 incident named in
    # Issue #253: 'git worktree list' -> 'C:/gamedev/Dungeon fortress/w243'.
    $nestedWorktree = Join-Path $mainRepo "w243"
    & git -C $mainRepo worktree add -q $nestedWorktree -b "art/243-nested"
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture setup: 'git worktree add' nested inside the root failed."
    }

    $violationResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($violationResult.ExitCode -ne 1 -or
        $violationResult.Text -notmatch '"status":"fail"' -or
        $violationResult.Text -notmatch 'w243') {
        throw "A worktree nested inside the root was not reported as a violation. exit=$($violationResult.ExitCode) output=$($violationResult.Text)"
    }

    & git -C $mainRepo worktree remove --force $nestedWorktree
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture teardown: removing the nested worktree failed."
    }
    & git -C $mainRepo worktree prune

    $restoredResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($restoredResult.ExitCode -ne 0 -or $restoredResult.Text -notmatch '"status":"ok"') {
        throw "State after removing the nested worktree was not reported ok again. exit=$($restoredResult.ExitCode) output=$($restoredResult.Text)"
    }

    [ordered]@{
        event = "check_no_worktrees_in_root_test"
        status = "ok"
        legitimateStateOk = $true
        nestedWorktreeDetected = $true
        restoredStateOk = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        $mainRepoForPrune = Join-Path $resolvedTestRoot "root"
        if (Test-Path -LiteralPath $mainRepoForPrune) {
            & git -C $mainRepoForPrune worktree prune 2>&1 | Out-Null
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
