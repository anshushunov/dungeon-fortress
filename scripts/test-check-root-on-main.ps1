[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-root-on-main.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-root-on-main-test-" + [Guid]::NewGuid().ToString("N"))

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

    # Case 1: legitimate state - root on 'main', plus a sibling linked
    # worktree present (the shape every task worktree actually has). The
    # sibling must not make the guard fire on the root's own check, and is
    # exercised twice: once as the root's own -RepoRoot, once pointed
    # straight at the sibling itself (case 3 below), which is the situation
    # every task worktree's own verify.ps1 run is actually in.
    $siblingWorktree = Join-Path $testRoot "sibling-wt"
    & git -C $mainRepo worktree add -q $siblingWorktree -b "agent/task-branch"
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture setup: 'git worktree add' for the sibling failed."
    }

    $legitimateResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($legitimateResult.ExitCode -ne 0 -or
        $legitimateResult.Text -notmatch '"applicable":true' -or
        $legitimateResult.Text -notmatch '"status":"ok"') {
        throw "Legitimate state (root on main, sibling worktree present) was not reported ok. exit=$($legitimateResult.ExitCode) output=$($legitimateResult.Text)"
    }

    # Case 2: violation - the root copy's own HEAD is switched off 'main'.
    # This is the exact shape of both the 2026-08-01 (art task switched the
    # root's HEAD to its own branch) and the 2026-08-03 (#202 committed on
    # branch agent/202-worktree-command in the root copy) incidents named in
    # Issue #253.
    & git -C $mainRepo checkout -q -b "agent/202-worktree-command"
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture setup: switching the root copy off 'main' failed."
    }

    $violationResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($violationResult.ExitCode -ne 1 -or
        $violationResult.Text -notmatch '"applicable":true' -or
        $violationResult.Text -notmatch '"status":"fail"' -or
        $violationResult.Text -notmatch 'agent/202-worktree-command') {
        throw "Root copy with HEAD off 'main' was not reported as a violation. exit=$($violationResult.ExitCode) output=$($violationResult.Text)"
    }

    & git -C $mainRepo checkout -q main
    if ($LASTEXITCODE -ne 0) {
        throw "Fixture teardown: restoring the root copy to 'main' failed."
    }

    $restoredResult = Invoke-Guard -TargetRepoRoot $mainRepo
    if ($restoredResult.ExitCode -ne 0 -or $restoredResult.Text -notmatch '"status":"ok"') {
        throw "Root copy restored to 'main' was not reported ok again. exit=$($restoredResult.ExitCode) output=$($restoredResult.Text)"
    }

    # Case 3: the guard, run with -RepoRoot pointed at a *linked* worktree
    # whose own HEAD is (correctly, normally) not 'main', must report
    # "not applicable" rather than a violation. Without this the guard would
    # fire on every single task worktree's own verify.ps1 run, which is
    # exactly the opposite of what rule 17 asks for.
    $linkedWorktreeResult = Invoke-Guard -TargetRepoRoot $siblingWorktree
    if ($linkedWorktreeResult.ExitCode -ne 0 -or
        $linkedWorktreeResult.Text -notmatch '"applicable":false' -or
        $linkedWorktreeResult.Text -notmatch '"status":"ok"') {
        throw "A linked worktree on its own task branch was not reported not-applicable. exit=$($linkedWorktreeResult.ExitCode) output=$($linkedWorktreeResult.Text)"
    }

    [ordered]@{
        event = "check_root_on_main_test"
        status = "ok"
        legitimateStateOk = $true
        violationDetected = $true
        restoredStateOk = $true
        linkedWorktreeNotApplicable = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        # The sibling worktree is still registered against $mainRepo; prune
        # it before deleting the tree out from under git, or the main repo's
        # .git/worktrees bookkeeping would be left dangling on disk (it is
        # thrown away with $testRoot regardless, but pruning first keeps the
        # teardown honest about what it is doing).
        $mainRepoForPrune = Join-Path $resolvedTestRoot "root"
        if (Test-Path -LiteralPath $mainRepoForPrune) {
            & git -C $mainRepoForPrune worktree prune 2>&1 | Out-Null
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
