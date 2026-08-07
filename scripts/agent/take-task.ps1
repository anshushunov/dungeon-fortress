[CmdletBinding()]
param(
    [ValidateSet("fast", "standard", "deep", "critical")]
    [string]$Tier = "standard",

    [int]$Issue,

    [switch]$WhatIf,

    # -Issue bypasses the search, but not the partition invariant: without
    # -Force a ticket that lacks 'ready' or already carries 'claimed' is refused
    # (review B9). With -Force the ticket is claimed regardless.
    [switch]$Force
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding       = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "take-task.lib.ps1")

if ($null -eq (Get-Command $script:GhCommand -ErrorAction SilentlyContinue)) {
    Write-Output ("GitHub CLI is not available ('{0}'). Install gh, or point DF_TAKE_TASK_GH at a stub for testing." -f $script:GhCommand)
    exit 1
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

if ([string]::IsNullOrWhiteSpace($script:RepoName)) {
    try {
        $script:RepoName = Get-RemoteRepoName -RepoRoot $repoRoot
    }
    catch {
        Write-Output $_.Exception.Message
        exit 1
    }
}

# --- Step 1: selection -------------------------------------------------------
$candidates = @()

if ($Issue -gt 0) {
    $data = Get-IssueData -Num $Issue
    if ($null -eq $data) {
        Write-Output ("Could not fetch issue #{0}." -f $Issue)
        exit 1
    }

    $labels = @(Read-IssueLabels -Num $Issue)
    if (-not $Force -and ($labels -contains 'claimed')) {
        Write-Output ("Issue #{0} is already claimed. Refusing to take a ticket another agent may be working; use -Force to override." -f $Issue)
        exit 1
    }
    if (-not $Force -and ($labels -notcontains 'ready')) {
        Write-Output ("Issue #{0} is not marked 'ready', so the coordinator has not released it. Use -Force to claim it anyway." -f $Issue)
        exit 1
    }

    $candidates = @([pscustomobject]@{
        number = $data.number
        title  = $data.title
    })
}
else {
    # The partition invariant from Issue #182: 'ready' is set only by the
    # coordinator and only on tickets that are free and pairwise compatible, so
    # the script filters on labels instead of computing a partition.
    $search = "is:issue is:open label:tier:$Tier label:ready -label:claimed"
    $r = Invoke-Native -FilePath $script:GhCommand `
        -Arguments @("issue", "list", "--search", $search, "--json", "number,title", "--limit", "100", "--repo", $script:RepoName)
    if ($r.ExitCode -ne 0) {
        Write-Output ("Failed to search issues: {0}" -f $r.Text)
        exit 1
    }

    $found = @()
    if (-not [string]::IsNullOrWhiteSpace($r.Text)) {
        try {
            $found = @(ConvertFrom-Json -InputObject $r.Text)
        }
        catch {
            $found = @()
        }
    }

    # gh issue list orders by relevance, not by age; the oldest ticket is the
    # lowest number, so the candidates are sorted explicitly (review N2).
    $candidates = @($found | Sort-Object -Property @{ Expression = { [int]$_.number } })
}

if ($candidates.Count -eq 0) {
    Write-Output ("No open issue found with labels 'tier:{0}' and 'ready' (without 'claimed')." -f $Tier)
    exit 1
}

$firstCandidate = $candidates[0]
$firstSlug = ConvertTo-Slug -Title $firstCandidate.title

if ($WhatIf) {
    $labels = @(Read-IssueLabels -Num $firstCandidate.number)
    Write-Output ([string]::Format("WhatIf: would claim issue #{0}: {1}", $firstCandidate.number, $firstCandidate.title))
    Write-Output ("  tier    : {0}" -f $Tier)
    Write-Output ("  labels  : {0}" -f ($labels -join ', '))
    Write-Output ("  branch  : agent/{0}-{1}" -f $firstCandidate.number, $firstSlug)
    Write-Output ("  worktree: {0}" -f (Join-Path (Split-Path $repoRoot -Parent) ("_wt-{0}" -f $firstCandidate.number)))
    exit 0
}

# --- Step 2: claim the first claimable candidate -----------------------------
$won = $null
foreach ($candidate in $candidates) {
    $num = $candidate.number
    $branchName = "agent/$num-$((ConvertTo-Slug -Title $candidate.title))"
    $worktreePath = Join-Path (Split-Path $repoRoot -Parent) ("_wt-$num")

    if (Test-Path -LiteralPath $worktreePath) {
        Write-Output ("Skipping issue #{0}: worktree already exists at '{1}'." -f $num, $worktreePath)
        continue
    }
    $showRef = Invoke-Native -FilePath "git" -Arguments @("-C", $repoRoot, "show-ref", "--verify", "--quiet", "refs/heads/$branchName")
    if ($showRef.ExitCode -eq 0) {
        Write-Output ("Skipping issue #{0}: branch '{1}' already exists." -f $num, $branchName)
        continue
    }

    $attempt = Invoke-Claim -Num $num -Title $candidate.title -Force:$Force
    if ($attempt.Claimed) {
        $won = $attempt
        break
    }
    Write-Output ("Skipping issue #{0}: {1}" -f $num, $attempt.Reason)
}

if ($null -eq $won) {
    Write-Output "Could not claim any candidate issue; the reasons are listed above."
    exit 1
}

$issueNum    = $won.Num
$issueTitle  = $won.Title
$slug        = ConvertTo-Slug -Title $issueTitle
$branchName  = "agent/$issueNum-$slug"
$worktreePath = Join-Path (Split-Path $repoRoot -Parent) ("_wt-$issueNum")

Write-Output ("Claimed issue #{0}: {1}" -f $issueNum, $issueTitle)

# --- Step 3: prepare the working copy ----------------------------------------
$refCheck = Invoke-Native -FilePath "git" -Arguments @("-C", $repoRoot, "check-ref-format", "--branch", $branchName)
if ($refCheck.ExitCode -ne 0) {
    Write-Output ("Generated branch name '{0}' is not a valid git ref; refusing to create it." -f $branchName)
    Invoke-UndoClaim -Num $issueNum -Token $won.Token -RestoreReady:$won.PreClaimReady
    exit 1
}

$fetch = Invoke-Native -FilePath "git" -Arguments @("-C", $repoRoot, "fetch", "origin", "main")
if ($fetch.ExitCode -ne 0) {
    Write-Output ("git fetch origin main failed: {0}" -f $fetch.Text)
    Invoke-UndoClaim -Num $issueNum -Token $won.Token -RestoreReady:$won.PreClaimReady
    exit 1
}

if (Test-Path -LiteralPath $worktreePath) {
    Write-Output ("Worktree directory already exists at '{0}'; leaving the claim in place, another agent may be working." -f $worktreePath)
    exit 1
}

$worktreeAdd = Invoke-Native -FilePath "git" -Arguments @("-C", $repoRoot, "worktree", "add", $worktreePath, "-b", $branchName, "origin/main")
if ($worktreeAdd.ExitCode -ne 0) {
    if ($worktreeAdd.Text -match 'already exists|already checked out') {
        Write-Output ("Branch or worktree '{0}' already exists; leaving the claim in place, another agent may be working." -f $branchName)
    }
    else {
        Write-Output ("git worktree add failed:`n{0}" -f $worktreeAdd.Text)
        Invoke-UndoClaim -Num $issueNum -Token $won.Token -RestoreReady:$won.PreClaimReady
    }
    exit 1
}

Write-Output ("Worktree created at: {0} (branch {1})." -f $worktreePath, $branchName)

# --- Step 4: print the brief --------------------------------------------------
$boilerplateFile = Join-Path $repoRoot "docs\engineering\AGENT_ENTRY.md"
if (-not (Test-Path -LiteralPath $boilerplateFile)) {
    Write-Output ("Boilerplate not found at '{0}'. The claim stands and the worktree is ready." -f $boilerplateFile)
    exit 1
}
$boilerplate = Get-Content -LiteralPath $boilerplateFile -Raw -Encoding UTF8

$issueData = Get-IssueData -Num $issueNum
if ($null -eq $issueData) {
    Write-Output ("Could not fetch the body of issue #{0}. The claim stands and the worktree is ready." -f $issueNum)
    exit 1
}

# Issue #282: the mandatory-reading package is assembled by task type
# (tier:* labels plus the file paths the Issue's "Партиция" section names)
# instead of a fixed document list. Insufficient signal prints the full
# package and says so, rather than guessing a narrower one silently.
$issueLabelNames = @($issueData.labels | ForEach-Object { $_.name })
$readingPackage = Get-ReadingPackage -Labels $issueLabelNames -Body $issueData.body

Write-Output "=========================================="
Write-Output ("  BRIEF FOR ISSUE #{0}" -f $issueNum)
Write-Output "=========================================="
Write-Output ""
Write-Output "--- Agent Entry Rules ---"
Write-Output $boilerplate
Write-Output ""
Write-Output "--- Issue Body ---"
Write-Output $issueData.body
Write-Output ""
Write-Output "--- Reading Package ---"
if (-not $readingPackage.Certain) {
    Write-Output "Область задачи не определена по меткам и партиции Issue; печатается полный пакет чтения, а не урезанный по догадке."
}
Write-Output ("  Areas: {0}" -f ($readingPackage.Areas -join ", "))
foreach ($line in $readingPackage.Lines) {
    Write-Output ("  - {0}" -f $line)
}
Write-Output ""
Write-Output "--- Workspace Info ---"
Write-Output ("  Worktree path : {0}" -f $worktreePath)
Write-Output ("  Branch name   : {0}" -f $branchName)
Write-Output ("  Repo root     : {0}" -f $repoRoot)
Write-Output ("  To start      : cd '{0}'" -f $worktreePath)
Write-Output "=========================================="
exit 0
