[CmdletBinding()]
param(
    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

# Issue #287: git worktree registered *inside* the root working copy (typically
# under .claude/worktrees/**) violates rule 17 (AGENTS.md, "Работа нескольких
# агентов") — the root belongs to the coordination session, and a worktree
# nested inside it lets a records-writing agent's checkout look like part of
# root's own tree, which is exactly what broke independent review's trust in
# Issue #253/#287. This guard reads the *registered* worktree list (not a
# filesystem scan of .claude/worktrees/, which can also hold non-worktree
# scratch dirs that are not this guard's concern) and fails if any entry's
# path sits inside $RepoRoot.

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
try {
    $porcelain = @(& git -C $RepoRoot worktree list --porcelain 2>&1)
    $gitExit = $LASTEXITCODE
}
finally {
    $ErrorActionPreference = $previousPreference
}

if ($gitExit -ne 0) {
    Write-Host "git worktree list failed (exit $gitExit)."
    exit 2
}

$rootFull = [IO.Path]::GetFullPath($RepoRoot).TrimEnd('\', '/')
$offenders = @()
$total = 0

foreach ($line in $porcelain) {
    if ($line -like "worktree *") {
        $total++
        $path = $line.Substring("worktree ".Length)
        $pathFull = [IO.Path]::GetFullPath($path).TrimEnd('\', '/')
        if ($pathFull -eq $rootFull) {
            continue
        }
        if ($pathFull.StartsWith("$rootFull\", [StringComparison]::OrdinalIgnoreCase) -or
            $pathFull.StartsWith("$rootFull/", [StringComparison]::OrdinalIgnoreCase)) {
            $offenders += $pathFull
        }
    }
}

[ordered]@{
    event = "worktrees_in_root_check"
    repoRoot = $rootFull
    totalRegisteredWorktrees = $total
    offenders = $offenders
    status = if ($offenders.Count -gt 0) { "fail" } else { "ok" }
} | ConvertTo-Json -Compress | Write-Host

if ($offenders.Count -gt 0) {
    exit 1
}
exit 0
