[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

# Issue #253. Rule 17 (AGENTS.md, "Работа нескольких агентов") says the root
# working copy belongs to the coordination session and nobody switches its
# branch or commits from it - a records-writing agent of any tool, and the
# coordination session itself for its own records-writing work (Issue #398
# closed the earlier reading gap where the text bound only the former). Two
# of the three
# measured violations were exactly that: 2026-08-01 (an art task switched the
# root's HEAD to its own branch twice while coordination was live) and
# 2026-08-03 (#202 was committed straight onto the root copy on branch
# agent/202-worktree-command, per its own reflog and the DEBT_LEDGER entry
# that PR #213/#214's review added). Both leave the same observable trace:
# the root's HEAD is not on 'main'. The third violation (#243, a worktree
# nested inside the root) is a different trace and is what
# check-no-worktrees-in-root.ps1 (Issue #287) already catches; this script
# is deliberately single-purpose and does not duplicate that check.
#
# "Root working copy" is identified the same way git itself tells a main
# checkout apart from a linked worktree: 'git rev-parse --git-dir
# --git-common-dir' returns the same path for the main checkout and two
# different paths (git-dir under .git/worktrees/<name>) for a linked one.
# That means this same script, run unmodified from inside any task worktree
# (which is what every agent's own verify.ps1 run does), reports "not
# applicable" and exits 0 - the invariant only ever applies to the one
# checkout that is the coordination session's own copy, never to a task
# worktree's HEAD, which is expected and required to be off 'main'.
#
# --path-format=absolute needs a modern git (present here: 2.52.0). Without
# it 'rev-parse --git-dir' can return a path relative to the invocation
# directory instead of $RepoRoot, which would make the two paths compare
# unequal even for the actual main checkout.

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $dirOutput = @(& git -C $RepoRoot rev-parse --path-format=absolute --git-dir --git-common-dir 2>&1)
    $dirExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
}

if ($dirExit -ne 0 -or $dirOutput.Count -lt 2) {
    Write-Host "git rev-parse --git-dir --git-common-dir failed (exit $dirExit): $($dirOutput -join ' ')"
    exit 2
}

$gitDir = ([string]$dirOutput[0]).TrimEnd('\', '/')
$gitCommonDir = ([string]$dirOutput[1]).TrimEnd('\', '/')
$isMainCheckout = [string]::Equals($gitDir, $gitCommonDir, [StringComparison]::OrdinalIgnoreCase)

if (-not $isMainCheckout) {
    [ordered]@{
        event = "root_on_main_check"
        repoRoot = [IO.Path]::GetFullPath($RepoRoot)
        applicable = $false
        reason = "this checkout is a linked worktree (git-dir differs from git-common-dir), not the main working copy; rule 17 does not constrain its branch"
        status = "ok"
    } | ConvertTo-Json -Compress | Write-Host
    exit 0
}

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $branchOutput = @(& git -C $RepoRoot rev-parse --abbrev-ref HEAD 2>&1)
    $branchExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
}

if ($branchExit -ne 0 -or $branchOutput.Count -eq 0) {
    Write-Host "git rev-parse --abbrev-ref HEAD failed (exit $branchExit): $($branchOutput -join ' ')"
    exit 2
}

$headBranch = ([string]$branchOutput[-1]).Trim()
$onMain = [string]::Equals($headBranch, "main", [StringComparison]::Ordinal)

[ordered]@{
    event = "root_on_main_check"
    repoRoot = [IO.Path]::GetFullPath($RepoRoot)
    applicable = $true
    headBranch = $headBranch
    status = if ($onMain) { "ok" } else { "fail" }
} | ConvertTo-Json -Compress | Write-Host

if (-not $onMain) {
    Write-Host (
        "The root working copy's HEAD is on '$headBranch', not 'main'. Rule 17 " +
        "(AGENTS.md, 'Работа нескольких агентов') reserves the root working copy " +
        "for the coordination session; nobody switches its branch or commits " +
        "from it - not a records-writing agent, and not the coordination " +
        "session itself for its own records-writing work. Fix: " +
        "'git -C <root> checkout main' from the coordination session, after " +
        "making sure no uncommitted work on '$headBranch' is lost - it belongs " +
        "in its own worktree instead " +
        "('git worktree add ../_wt-<slug> -b <branch> origin/main')."
    )
    exit 1
}
exit 0
