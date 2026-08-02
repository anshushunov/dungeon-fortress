[CmdletBinding()]
param(
    [ValidateSet("fast", "standard", "deep", "critical")]
    [string]$Tier = "standard",

    [int]$Issue,

    [switch]$WhatIf
)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding       = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

<#
.SYNOPSIS
  Transliterate a Cyrillic/Russian title into an ASCII slug for branch naming.
#>
function Transform-ToSlug {
    param([string]$Title)

    # Character-by-character loop with Unicode ordinal comparison.
    $sb = [System.Text.StringBuilder]::new()
    foreach ($c in $Title.ToCharArray()) {
        $o = [int]$c

        # Cyrillic lowercase (U+0430..U+044F)
        if ($o -ge 0x0430 -and $o -le 0x044F) {
            switch ($o) {
                0x0431 { [void]$sb.Append('b') }  # б
                0x0432 { [void]$sb.Append('v') }  # в
                0x0433 { [void]$sb.Append('g') }  # г
                0x0434 { [void]$sb.Append('d') }  # д
                0x0435 { [void]$sb.Append('e') }  # е
                0x0436 { [void]$sb.Append('zh') } # ж
                0x0437 { [void]$sb.Append('z') }  # з
                0x0438 { [void]$sb.Append('i') }  # и
                0x0439 { [void]$sb.Append('y') }  # й
                0x043A { [void]$sb.Append('k') }  # к
                0x043B { [void]$sb.Append('l') }  # л
                0x043C { [void]$sb.Append('m') }  # м
                0x043D { [void]$sb.Append('n') }  # н
                0x043E { [void]$sb.Append('o') }  # о
                0x043F { [void]$sb.Append('p') }  # п
                0x0440 { [void]$sb.Append('r') }  # р
                0x0441 { [void]$sb.Append('s') }  # с
                0x0442 { [void]$sb.Append('t') }  # т
                0x0443 { [void]$sb.Append('u') }  # у
                0x0444 { [void]$sb.Append('f') }  # ф
                0x0445 { [void]$sb.Append('kh') } # х
                0x0446 { [void]$sb.Append('ts') } # ц
                0x0447 { [void]$sb.Append('ch') } # ч
                0x0448 { [void]$sb.Append('sh') } # ш
                0x0449 { [void]$sb.Append('shh')} # щ
                0x044A { [void]$sb.Append('-') }  # ъ
                0x044B { [void]$sb.Append('y') }  # ы
                0x044C { [void]$sb.Append('-') }  # ь
                0x044D { [void]$sb.Append('e') }  # э
                0x044E { [void]$sb.Append('yu') } # ю
                0x044F { [void]$sb.Append('ya') } # я
                Default  { [void]$sb.Append('a') } # а (0x0430)
            }
        }
        # Cyrillic uppercase (U+0410..U+042F) — same mapping, just lowercased
        elseif ($o -ge 0x0410 -and $o -le 0x042F) {
            $lowerO = $o + 0x20
            switch ($lowerO) {
                0x0431 { [void]$sb.Append('b') }
                0x0432 { [void]$sb.Append('v') }
                0x0433 { [void]$sb.Append('g') }
                0x0434 { [void]$sb.Append('d') }
                0x0435 { [void]$sb.Append('e') }
                0x0436 { [void]$sb.Append('zh') }
                0x0437 { [void]$sb.Append('z') }
                0x0438 { [void]$sb.Append('i') }
                0x0439 { [void]$sb.Append('y') }
                0x043A { [void]$sb.Append('k') }
                0x043B { [void]$sb.Append('l') }
                0x043C { [void]$sb.Append('m') }
                0x043D { [void]$sb.Append('n') }
                0x043E { [void]$sb.Append('o') }
                0x043F { [void]$sb.Append('p') }
                0x0440 { [void]$sb.Append('r') }
                0x0441 { [void]$sb.Append('s') }
                0x0442 { [void]$sb.Append('t') }
                0x0443 { [void]$sb.Append('u') }
                0x0444 { [void]$sb.Append('f') }
                0x0445 { [void]$sb.Append('kh') }
                0x0446 { [void]$sb.Append('ts') }
                0x0447 { [void]$sb.Append('ch') }
                0x0448 { [void]$sb.Append('sh') }
                0x0449 { [void]$sb.Append('shh')}
                0x044A { [void]$sb.Append('-') }
                0x044B { [void]$sb.Append('y') }
                0x044C { [void]$sb.Append('-') }
                0x044D { [void]$sb.Append('e') }
                0x044E { [void]$sb.Append('yu') }
                0x044F { [void]$sb.Append('ya') }
                Default  { [void]$sb.Append('a') } # А (0x0410)
            }
        }
        elseif ([char]::IsLetterOrDigit($c) -or $c -eq ' ' -or $c -eq '-') {
            [void]$sb.Append([char][char]::ToLowerInvariant($c))
        }
        else {
            [void]$sb.Append('-')
        }
    }

    $result = $sb.ToString()
    # Collapse multiple dashes, trim edges
    $result = ($result -replace '-+', '-') -replace '^-', '' -replace '-$', ''
    return $result
}

function Get-IssueData {
    param([int]$Num)

    $output = & gh issue view $Num --json number,title,body,state,labels 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (ConvertFrom-Json $output)
}

function Set-IssueLabels {
    param(
        [int]$Num,
        [string[]]$Add,
        [string[]]$Remove
    )

    if ($Add.Count -gt 0) {
        & gh issue edit $Num --add-label ($Add -join ',')
        if ($LASTEXITCODE -ne 0) {
            throw ([string]::Format("Failed to add labels for issue #{0}: {1}", $Num, ($Add -join ', ')))
        }
    }

    if ($Remove.Count -gt 0) {
        & gh issue edit $Num --remove-label ($Remove -join ',')
        if ($LASTEXITCODE -ne 0) {
            throw ([string]::Format("Failed to remove labels for issue #{0}: {1}", $Num, ($Remove -join ', ')))
        }
    }
}

function Read-IssueLabels {
    param([int]$Num)

    $output = & gh issue view $Num --json labels 2>$null
    if ($LASTEXITCODE -ne 0) { return @() }

    $data = ConvertFrom-Json $output
    if (-not $data.labels) { return @() }
    return $data.labels | ForEach-Object { $_.name }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

# --- Validate GitHub CLI availability ---
if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI is not installed or not in PATH."
    exit 1
}

# --- Step 1: Selection ---
$selectedIssue = $null

if ($Issue) {
    $data = Get-IssueData -Num $Issue
    if (-not $data) {
        Write-Error ([string]::Format("Could not fetch issue #{0}.", $Issue))
        exit 1
    }

    $labels = Read-IssueLabels -Num $Issue
    if ($labels -notcontains "ready") {
        Write-Warning ([string]::Format(
            "Issue #{0} does not have the ready label. Proceeding because --Issue was explicit.", $Issue))
    }

    $selectedIssue = @{
        number = $data.number
        title  = $data.title
        body   = $data.body
    }
}
else {
    # Auto-select: open issues with tier:<Tier> + ready, without claimed
    $search = "is:issue is:open label:`"tier:$Tier`" label:ready -label:claimed"

    $output = & gh issue list `
        --search $search `
        --json number,title `
        --limit 100 2>$null

    if ($LASTEXITCODE -ne 0) {
        Write-Error ([string]::Format("Failed to search issues. Output: {0}", $output))
        exit 1
    }

    $candidates = ConvertFrom-Json $output
    if (-not $candidates -or @($candidates).Count -eq 0) {
        Write-Host "No open issue found with labels 'tier:$Tier' and 'ready' (without 'claimed')."
        exit 1
    }

    # Pick the first candidate (oldest = lowest number)
    $pick = $candidates[0]
    $bodyData = Get-IssueData -Num $pick.number
    if (-not $bodyData) {
        Write-Error ([string]::Format("Could not fetch body for issue #{0}.", $pick.number))
        exit 1
    }

    $selectedIssue = @{
        number = $pick.number
        title  = $pick.title
        body   = $bodyData.body
    }
}

if (-not $selectedIssue.number) {
    Write-Error "No candidate selected."
    exit 1
}

$issueNum   = [int]$selectedIssue.number
$issueTitle = $selectedIssue.title

Write-Output ([string]::Format("Selected issue #{0}: {1}", $issueNum, $issueTitle))

if ($WhatIf) {
    Write-Output ([string]::Format("[WhatIf] Would claim issue #{0} and prepare worktree.", $issueNum))
    Write-Output "  Tier : $Tier"
    Write-Output "  Title: $issueTitle"
    Write-Host ""
    Write-Host "Labels check:"

    $labels = Read-IssueLabels -Num $issueNum
    Write-Output ([string]::Format("  Current labels: {0}", ($labels -join ', ')))
    $hasReady   = $labels -contains 'ready'
    $hasClaimed = $labels -contains 'claimed'
    Write-Output "  ready   : $hasReady"
    Write-Output "  claimed : $hasClaimed"

    exit 0
}

# --- Step 2: Claim the issue ---
Write-Host ""
Write-Output ([string]::Format("Claiming issue #{0}...", $issueNum))

try {
    Set-IssueLabels -Num $issueNum -Add @("claimed") -Remove @("ready")
}
catch {
    Write-Error ([string]::Format("Failed to claim issue #{0}: {1}", $issueNum, $_))
    exit 1
}

# Re-read labels to verify persistence (race detection)
$verifiedLabels = Read-IssueLabels -Num $issueNum
$hasClaimed = $verifiedLabels -contains 'claimed'
$hasReady   = $verifiedLabels -contains 'ready'

if (-not $hasClaimed) {
    Write-Error ([string]::Format(
        "Race condition detected: 'claimed' label did not persist on issue #{0}. Aborting.", $issueNum))
    exit 1
}

if ($hasReady) {
    Write-Warning ([string]::Format(
        "'ready' label still present on issue #{0} after removal attempt. Retrying...", $issueNum))
    try {
        Set-IssueLabels -Num $issueNum -Remove @("ready")
    }
    catch {
        Write-Host "Note: could not remove 'ready' (API rate limit or permission). Continuing with claim."
    }
}

Write-Output ([string]::Format("Issue #{0} claimed successfully.", $issueNum))

# --- Step 3: Create worktree ---
$slug         = Transform-ToSlug -Title $issueTitle
$branchName   = [string]::Format("agent/{0}-{1}", $issueNum, $slug)
$wtSuffix     = [string]::Format("_wt-{0}", $issueNum)
$worktreePath = Join-Path (Split-Path $repoRoot -Parent) $wtSuffix

if (Test-Path $worktreePath) {
    Write-Error ([string]::Format(
        "Worktree directory already exists at '{0}'. Refusing to overwrite.", $worktreePath))
    exit 1
}

Write-Host ""
Write-Output ([string]::Format("Creating worktree for branch '{0}'...", $branchName))

$gitResult = & git worktree add "$worktreePath" "-b" "$branchName" origin/main 2>&1

if ($LASTEXITCODE -ne 0) {
    if ($gitResult -match 'already\s+exists') {
        Write-Error ([string]::Format(
            "Branch or worktree '{0}' / '{1}' already exists. Refusing to overwrite.", $branchName, $worktreePath))
    }
    else {
        Write-Error ([string]::Format("git worktree failed:`n{0}", $gitResult))
    }

    exit 1
}

Write-Output ([string]::Format("Worktree created at: {0}", $worktreePath))

# --- Step 4: Print brief ---
$boilerplateFile = Join-Path (Join-Path $repoRoot "docs\engineering") "AGENT_ENTRY.md"

if (-not (Test-Path $boilerplateFile)) {
    Write-Error ([string]::Format("Boilerplate file not found at '{0}'", $boilerplateFile))
    exit 1
}

$boilerplate = Get-Content -Path $boilerplateFile -Raw

Write-Host ""
Write-Host "=========================================="
Write-Output ([string]::Format("  BRIEF FOR ISSUE #{0}", $issueNum))
Write-Host "=========================================="
Write-Host ""
Write-Host "--- Agent Entry Rules ---"
Write-Host $boilerplate
Write-Host ""
Write-Host "--- Issue Body ---"
Write-Host $selectedIssue.body
Write-Host ""
Write-Host "--- Workspace Info ---"
Write-Output ([string]::Format("  Worktree path : {0}", $worktreePath))
Write-Output ([string]::Format("  Branch name   : {0}", $branchName))
Write-Output ([string]::Format("  Repo root     : {0}", $repoRoot))
Write-Host ""
Write-Host "=========================================="
Write-Output ([string]::Format("  To start working, run: cd '{0}'", $worktreePath))
Write-Host "=========================================="