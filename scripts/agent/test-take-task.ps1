[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# The child runs write UTF-8 to their stdout (take-task.ps1 sets
# [Console]::OutputEncoding at startup); the parent must decode that stream
# with the same encoding or the Cyrillic assertions below would compare
# mojibake against real text.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

# Behavioural tests for take-task.ps1 (Issue #182). Review of the first PR
# found the test checked for strings in the source instead of executing it:
# the script had never run once, and the assigned mutant survived (findings
# B6, B7). This file runs the real code:
#
#   - in-process: the library functions are dot-sourced and driven by a stub
#     `gh` function over a state file, for the claim protocol (win, lost race,
#     non-persisting write, stale markers) and the slug contract;
#   - end to end: take-task.ps1 is copied byte-for-byte into a fixture git
#     repository (hash is asserted) and executed in a child PowerShell with
#     DF_TAKE_TASK_GH pointing at gh-stub.ps1, for the acceptance criteria
#     1-5 of Issue #182.
#
# The mutant assigned by the issue ("the script must not re-read labels after
# claiming") is proven dead by running two mutations against copies of the
# library and showing each one changes the outcome of the claim.
#
# This test is not wired into a verify.ps1 stage: adding it there is a
# coordinator decision (it would touch scripts/verify.ps1, which this task's
# partition keeps elsewhere).

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$agentDir = Join-Path $repoRoot "scripts\agent"
$libPath = Join-Path $agentDir "take-task.lib.ps1"
$entryPath = Join-Path $agentDir "take-task.ps1"
$stubPath = Join-Path $agentDir "gh-stub.ps1"
$boilerplatePath = Join-Path $repoRoot "docs\engineering\AGENT_ENTRY.md"

$sandbox = Join-Path $repoRoot (".artifacts\take-task-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

function Assert-True {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Message,

        [Parameter(Mandatory = $true)]
        [object]$Condition
    )
    if (-not $Condition) {
        throw "ASSERT FAILED: $Message"
    }
}

function New-FixtureRepository {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sandbox
    )

    $fixture = Join-Path $Sandbox "repo"
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null

    git init -b main $fixture 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "git init failed for the fixture repository."
    }
    git -C $fixture config user.email "fixture@example.com"
    git -C $fixture config user.name "fixture"
    git -C $fixture config core.autocrlf false
    [IO.File]::WriteAllText(
        (Join-Path $fixture "placeholder.txt"),
        "fixture`n",
        [Text.UTF8Encoding]::new($false))
    git -C $fixture add .
    git -C $fixture commit -q -m "fixture base" 2>$null

    # A bare repository stands in for the GitHub remote, so that
    # `git fetch origin main` inside take-task works without the network.
    $remote = Join-Path $Sandbox "remote.git"
    git init --bare $remote 2>$null | Out-Null
    git -C $fixture remote add origin $remote
    git -C $fixture push -q -u origin main 2>$null

    # The entry point and library must be the exact files under test. Their
    # hashes are asserted before the child runs, so a drift in the real files
    # turns this test red instead of silently testing a stale copy.
    New-Item -ItemType Directory -Force -Path (Join-Path $fixture "scripts\agent") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $fixture "docs\engineering") | Out-Null
    Copy-Item $entryPath (Join-Path $fixture "scripts\agent\take-task.ps1")
    Copy-Item $libPath (Join-Path $fixture "scripts\agent\take-task.lib.ps1")
    Copy-Item $boilerplatePath (Join-Path $fixture "docs\engineering\AGENT_ENTRY.md")

    foreach ($file in @("take-task.ps1", "take-task.lib.ps1")) {
        $real = Get-FileHash (Join-Path $agentDir $file)
        $copy = Get-FileHash (Join-Path $fixture "scripts\agent\$file")
        if ($real.Hash -ne $copy.Hash) {
            throw "Fixture copy of $file differs from the real file; the end-to-end run would not test the code under test."
        }
    }
    $realBoilerplate = Get-FileHash $boilerplatePath
    $copyBoilerplate = Get-FileHash (Join-Path $fixture "docs\engineering\AGENT_ENTRY.md")
    if ($realBoilerplate.Hash -ne $copyBoilerplate.Hash) {
        throw "Fixture copy of AGENT_ENTRY.md differs from the real file."
    }

    git -C $fixture add .
    git -C $fixture commit -q -m "fixture tooling" 2>$null
    return $fixture
}

function New-InitialState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Sandbox,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [int]$Number = 5,

        [string]$Title = "Настройка панели клетки",

        [string]$Body = "BODY-OF-ISSUE-$Number",

        [string[]]$Labels = @("tier:standard", "ready"),

        [bool]$FailClaimPersistence = $false,

        [bool]$InjectCompetitor = $false
    )

    $path = Join-Path $Sandbox ($Name + ".json")
    $labelJson = @($Labels | ForEach-Object {
        "{`"name`":`"$_`"}"
    }) -join ","
    # JSON booleans are lowercase; interpolating $false would write "False",
    # which ConvertFrom-Json rejects and which would silently turn the state
    # into the empty default.
    $failJson = if ($FailClaimPersistence) { "true" } else { "false" }
    $injectJson = if ($InjectCompetitor) { "true" } else { "false" }
    $state = "{`"issues`":{`"$Number`":{`"number`":$Number,`"title`":`"$Title`",`"body`":`"$Body`",`"state`":`"OPEN`",`"labels`":[$labelJson],`"comments`":[]}},`"nextCommentNumber`":2000,`"failClaimPersistence`":$failJson,`"injectCompetitorOnClaim`":$injectJson}"
    [IO.File]::WriteAllText($path, $state, [Text.UTF8Encoding]::new($false))
    return $path
}

function Read-StateIssue {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$StateFile,

        [Parameter(Mandatory = $true)]
        [int]$Number
    )
    $state = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    return $state.issues.($Number.ToString())
}

function Invoke-TakeTaskChild {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Fixture,

        [Parameter(Mandatory = $true)]
        [string]$StateFile,

        [string[]]$Arguments = @()
    )

    $previousGh = $env:DF_TAKE_TASK_GH
    $previousState = $env:DF_TAKE_TASK_STATE
    $previousRepo = $env:DF_TAKE_TASK_REPO
    $previousNoSystem = $env:GIT_CONFIG_NOSYSTEM
    $previousPreference = $ErrorActionPreference
    try {
        $env:DF_TAKE_TASK_GH = $stubPath
        $env:DF_TAKE_TASK_STATE = $StateFile
        $env:DF_TAKE_TASK_REPO = "fixture/dungeon-fortress"
        $env:GIT_CONFIG_NOSYSTEM = "1"

        # The child's stderr is merged with 2>&1, and a merged native stderr
        # line becomes a terminating NativeCommandError under $ErrorActionPreference
        # "Stop" (the same defect that broke take-task itself). The call runs
        # under "Continue" so the exit code and the output are what is read.
        $ErrorActionPreference = "Continue"
        $entry = Join-Path $Fixture "scripts\agent\take-task.ps1"
        $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $entry @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:DF_TAKE_TASK_GH = $previousGh
        $env:DF_TAKE_TASK_STATE = $previousState
        $env:DF_TAKE_TASK_REPO = $previousRepo
        $env:GIT_CONFIG_NOSYSTEM = $previousNoSystem
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $output
        Text     = ($output -join [Environment]::NewLine)
    }
}

# --- set up the in-process harness -------------------------------------------
. $stubPath
function global:gh {
    # The in-process stub for the library functions. It reports through
    # $global:LASTEXITCODE exactly like a native gh process would.
    $result = Invoke-FixtureGh -Arguments $args
    $global:LASTEXITCODE = $result.ExitCode
    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {
        $result.Text
    }
}

$env:DF_TAKE_TASK_REPO = "fixture/dungeon-fortress"
. $libPath
$script:RepoName = "fixture/dungeon-fortress"

$checks = [ordered]@{}

try {
    # =========================================================================
    # Part A: slug contract (B2, B3)
    # =========================================================================
    $slugCases = @(
        @{ Title = "Настройка панели клетки: ёж и Ёж под столом"; ContainsNoSpace = $true }
        @{ Title = "One command instead of a portable brief: take-task picks a ticket"; ContainsNoSpace = $true }
        @{ Title = "R\u00e9sum\u00e9 d'un agent"; ContainsNoSpace = $true }
        @{ Title = "!!!"; ContainsNoSpace = $true }
    )
    foreach ($case in $slugCases) {
        $title = $case.Title
        $slug = ConvertTo-Slug -Title $title
        if ($slug -match '\s' -or $slug -notmatch '^[a-z0-9-]+$') {
            throw "Slug for '$title' is not pure ASCII/dash text: '$slug'."
        }
        $branch = "agent/5-$slug"
        $refCheck = Invoke-Native -FilePath "git" -Arguments @("check-ref-format", "--branch", $branch)
        if ($refCheck.ExitCode -ne 0) {
            throw "Slug for '$title' produced an invalid git ref: '$branch'."
        }
    }
    $slugForSpaces = ConvertTo-Slug -Title "Ёж в клетке"
    Assert-True "slug must transliterate ё and keep spaces out" ($slugForSpaces -eq "ezh-v-kletke")
    $checks.slug = "ok ($($slugCases.Count) cases, git check-ref-format green)"

    # =========================================================================
    # Part A: remote repo derivation (N1)
    # =========================================================================
    $repoProbe = Join-Path $sandbox "repo-probe"
    New-Item -ItemType Directory -Force -Path $repoProbe | Out-Null
    git init $repoProbe 2>$null | Out-Null
    git -C $repoProbe config user.email "probe@example.com"
    git -C $repoProbe config user.name "probe"
    [IO.File]::WriteAllText((Join-Path $repoProbe "x.txt"), "x", [Text.UTF8Encoding]::new($false))
    git -C $repoProbe add .
    git -C $repoProbe commit -q -m probe 2>$null
    git -C $repoProbe remote add origin "https://github.com/anshushunov/dungeon-fortress.git"
    $derived = Get-RemoteRepoName -RepoRoot $repoProbe
    Assert-True "https origin must derive owner/repo" ($derived -eq "anshushunov/dungeon-fortress")
    git -C $repoProbe remote set-url origin "git@github.com:anshushunov/dungeon-fortress.git"
    $derivedScp = Get-RemoteRepoName -RepoRoot $repoProbe
    Assert-True "scp-style origin must derive owner/repo" ($derivedScp -eq "anshushunov/dungeon-fortress")
    $checks.remoteRepo = "ok (https + scp parse)"

    # =========================================================================
    # Part A: the claim protocol, behaviourally
    # =========================================================================
    $winState = New-InitialState -Sandbox $sandbox -Name "claim-win" -Number 5
    $env:DF_TAKE_TASK_STATE = $winState
    $win = Invoke-Claim -Num 5 -Title "Настройка панели клетки"
    Assert-True "a free ready ticket must be claimable" $win.Claimed
    $winLabels = @(Read-IssueLabels -Num 5)
    Assert-True "claim must add 'claimed'" ($winLabels -contains "claimed")
    Assert-True "claim must remove 'ready'" ($winLabels -notcontains "ready")
    $winComments = @(Read-IssueComments -Num 5)
    $winMarkers = @($winComments | Where-Object { $_.body -match $script:ClaimMarkerPattern })
    Assert-True "claim must leave exactly one ownership marker" ($winMarkers.Count -eq 1)

    $again = Invoke-Claim -Num 5 -Title "Настройка панели клетки"
    Assert-True "a second claim of the same ticket must be refused" (-not $again.Claimed)
    Assert-True "refusal reason must say it is already claimed" ($again.Reason -match "already claimed")

    $checks.claimNormal = "ok (win, second claim refused, marker present)"

    # --- lost race (B5): another agent's marker lands earlier ----------------
    $raceState = New-InitialState -Sandbox $sandbox -Name "claim-race" -Number 6 -InjectCompetitor $true
    $env:DF_TAKE_TASK_STATE = $raceState
    $race = Invoke-Claim -Num 6 -Title "T"
    Assert-True "a claim whose marker is not first must lose the race" (-not $race.Claimed)
    Assert-True "the loser reason must name the race" ($race.Reason -match "race")

    # --- non-persisting write (B6/B8): claimed is dropped by the API ---------
    $persistState = New-InitialState -Sandbox $sandbox -Name "claim-persist" -Number 7 -FailClaimPersistence $true
    $env:DF_TAKE_TASK_STATE = $persistState
    $persist = Invoke-Claim -Num 7 -Title "T"
    Assert-True "a claim whose 'claimed' write is lost must be refused" (-not $persist.Claimed)
    Assert-True "the refusal must say the write did not persist" ($persist.Reason -match "persist")

    # --- stale markers are cleaned before a fresh claim ----------------------
    $staleState = Join-Path $sandbox "claim-stale.json"
    $staleStateJson = @(
        '{"issues":{"8":{"number":8,"title":"T","body":"B","state":"OPEN",',
        '"labels":[{"name":"tier:standard"},{"name":"ready"}],"comments":[',
        '{"id":"IC_1000","body":"take-task claim: 0123456789abcdef0123456789abcdef",',
        '"createdAt":"2020-01-01T00:00:00Z",',
        '"url":"https://github.com/owner/repo/issues/8#issuecomment-1000"}]}},',
        '"nextCommentNumber":2000,"failClaimPersistence":false,"injectCompetitorOnClaim":false}'
    ) -join ""
    [IO.File]::WriteAllText($staleState, $staleStateJson, [Text.UTF8Encoding]::new($false))
    $env:DF_TAKE_TASK_STATE = $staleState
    $stale = Invoke-Claim -Num 8 -Title "T"
    Assert-True "a claim with a stale marker present must still win" $stale.Claimed
    $staleComments = @(Read-IssueComments -Num 8)
    $staleMarkers = @($staleComments | Where-Object { $_.body -match $script:ClaimMarkerPattern })
    Assert-True "the stale marker must be gone after the fresh claim" ($staleMarkers.Count -eq 1)

    # --- undo claim (B10): rollback restores the labels it owns --------------
    $env:DF_TAKE_TASK_STATE = $winState
    Invoke-UndoClaim -Num 5 -Token $win.Token -RestoreReady
    $undoLabels = @(Read-IssueLabels -Num 5)
    Assert-True "undo must remove 'claimed'" ($undoLabels -notcontains "claimed")
    Assert-True "undo must restore 'ready'" ($undoLabels -contains "ready")

    $checks.claimProtocol = "ok (race, persistence, stale markers, undo)"

    # =========================================================================
    # Part A: the assigned mutant must die (B6, B7)
    # =========================================================================
    $libText = [IO.File]::ReadAllText($libPath)
    $mutationCases = @(
        [pscustomobject]@{
            Name                  = "ownership-check-neutralized"
            Find                  = 'if (-not (Test-ClaimOwner -Num $Num -Token $token)) {'
            Replace               = 'if ($false) {'
            StateName             = "mut-race"
            Number                = 6
            InjectCompetitor      = $true
            FailClaimPersistence  = $false
        },
        [pscustomobject]@{
            Name                  = "persistence-check-neutralized"
            Find                  = "if (`$reRead -notcontains 'claimed') {"
            Replace               = 'if ($false) {'
            StateName             = "mut-persist"
            Number                = 7
            InjectCompetitor      = $false
            FailClaimPersistence  = $true
        }
    )

    foreach ($mutation in $mutationCases) {
        $occurrences = ([regex]::Matches($libText, [regex]::Escape($mutation.Find))).Count
        if ($occurrences -ne 1) {
            throw "Mutation '$($mutation.Name)' anchors on text appearing $occurrences time(s); it has to appear exactly once."
        }

        # The unmutated library refuses the scenario.
        $statePath = New-InitialState -Sandbox $sandbox -Name $mutation.StateName -Number $mutation.Number `
            -InjectCompetitor $mutation.InjectCompetitor -FailClaimPersistence $mutation.FailClaimPersistence
        $env:DF_TAKE_TASK_STATE = $statePath
        $cleanResult = Invoke-Claim -Num $mutation.Number -Title "T"
        if ($cleanResult.Claimed) {
            throw "The scenario for '$($mutation.Name)' does not exercise the check: the clean library claimed the ticket."
        }

        # The mutated library changes the outcome: the mutant survives the test,
        # which is exactly what makes it detectable. The state is re-seeded first
        # because the clean run already mutated it (posted its marker, set labels).
        $mutatedPath = Join-Path $sandbox ("lib-" + $mutation.Name + ".ps1")
        [IO.File]::WriteAllText(
            $mutatedPath,
            $libText.Replace($mutation.Find, $mutation.Replace),
            [Text.UTF8Encoding]::new($false))
        . $mutatedPath
        $null = New-InitialState -Sandbox $sandbox -Name $mutation.StateName -Number $mutation.Number `
            -InjectCompetitor $mutation.InjectCompetitor -FailClaimPersistence $mutation.FailClaimPersistence
        $env:DF_TAKE_TASK_STATE = $statePath
        $mutatedResult = Invoke-Claim -Num $mutation.Number -Title "T"
        if ($mutatedResult.Claimed -ne $true) {
            throw "Mutation '$($mutation.Name)' did not change the claim outcome; expected the mutant to proceed and claim the ticket."
        }
        # Restore the unmutated functions for the next scenario.
        . $libPath
    }
    $checks.mutant = "dead: both mutations change the claim outcome"

    # =========================================================================
    # Part B: acceptance criteria 1-5, end to end
    # =========================================================================
    $fixture = New-FixtureRepository -Sandbox $sandbox

    # --- CR1: -WhatIf prints the selection and changes no labels -------------
    $whatIfState = New-InitialState -Sandbox $sandbox -Name "e2e-whatif" -Number 5
    $beforeWhatIf = Read-StateIssue -StateFile $whatIfState -Number 5
    $whatIf = Invoke-TakeTaskChild -Fixture $fixture -StateFile $whatIfState -Arguments @("-Tier", "standard", "-WhatIf")
    Assert-True "-WhatIf must exit 0" ($whatIf.ExitCode -eq 0)
    Assert-True "-WhatIf must name the ticket" ($whatIf.Text -match "WhatIf: would claim issue #5")
    Assert-True "-WhatIf must name the title" ($whatIf.Text -match "Настройка панели клетки")
    $afterWhatIf = Read-StateIssue -StateFile $whatIfState -Number 5
    $beforeLabels = @($beforeWhatIf.labels | ForEach-Object { $_.name })
    $afterLabels = @($afterWhatIf.labels | ForEach-Object { $_.name })
    Assert-True "-WhatIf must not change a single label" ((($beforeLabels | Sort-Object) -join ",") -eq (($afterLabels | Sort-Object) -join ","))
    Assert-True "-WhatIf must not add a marker comment" (@($afterWhatIf.comments).Count -eq 0)
    Assert-True "-WhatIf must not create a worktree" (-not (Test-Path (Join-Path $sandbox "_wt-5")))

    # --- CR2: two runs in a row; the second must not return the same ticket ---
    $runState = New-InitialState -Sandbox $sandbox -Name "e2e-run" -Number 5
    $run1 = Invoke-TakeTaskChild -Fixture $fixture -StateFile $runState -Arguments @("-Tier", "standard")
    Assert-True "first run must claim and exit 0" ($run1.ExitCode -eq 0)
    $run1Labels = @((Read-StateIssue -StateFile $runState -Number 5).labels | ForEach-Object { $_.name })
    Assert-True "first run must leave the ticket claimed" ($run1Labels -contains "claimed")
    $worktree = Join-Path $sandbox "_wt-5"
    Assert-True "first run must create the worktree" (Test-Path $worktree)
    Assert-True "first run must create the branch" ((git -C $fixture branch --list "agent/5-nastroyka-paneli-kletki") -match "nastroyka-paneli-kletki")

    $run2 = Invoke-TakeTaskChild -Fixture $fixture -StateFile $runState -Arguments @("-Tier", "standard")
    Assert-True "second run must exit non-zero" ($run2.ExitCode -ne 0)
    Assert-True "second run must not claim the same ticket" ($run2.Text -notmatch "Claimed issue #5")
    Assert-True "second run must report no free ticket" ($run2.Text -match "No open issue found")
    $run2Labels = @((Read-StateIssue -StateFile $runState -Number 5).labels | ForEach-Object { $_.name })
    Assert-True "second run must not double-claim" (@($run2Labels | Where-Object { $_ -eq "claimed" }).Count -eq 1)

    # --- CR3: the brief is enough to start without a question ----------------
    Assert-True "brief must contain the worktree path" ($run1.Text -match [regex]::Escape($worktree))
    Assert-True "brief must contain the branch name" ($run1.Text -match "agent/5-nastroyka-paneli-kletki")
    Assert-True "brief must contain the issue body" ($run1.Text -match "BODY-OF-ISSUE-5")
    Assert-True "brief must contain the agent entry rules" ($run1.Text -match "тело PR и есть отчёт")

    # --- CR4: no suitable ticket -> human message and non-zero code ----------
    $emptyState = New-InitialState -Sandbox $sandbox -Name "e2e-empty" -Number 9 -Labels @("tier:deep", "ready")
    $empty = Invoke-TakeTaskChild -Fixture $fixture -StateFile $emptyState -Arguments @("-Tier", "standard")
    Assert-True "no candidate must exit non-zero" ($empty.ExitCode -ne 0)
    Assert-True "no candidate must say so in words" ($empty.Text -match "No open issue found")

    # --- CR5: nothing written outside the sandbox ----------------------------
    $porcelain = @(git -C $fixture status --porcelain)
    Assert-True "the fixture repo must stay clean" ($porcelain.Count -eq 0)
    $stray = @(Get-ChildItem -LiteralPath $sandbox -Force | Where-Object {
        $_.Name -notin @("repo", "remote.git", "repo-probe") -and
        -not ($_.Name -like "*.json") -and
        -not ($_.Name -like "lib-*.ps1") -and
        -not ($_.Name -like "_wt-*")
    })
    Assert-True "no unexpected files may appear in the sandbox" ($stray.Count -eq 0)

    # --- refusal to overwrite an existing worktree (B10 leak avoidance) ------
    $existsState = New-InitialState -Sandbox $sandbox -Name "e2e-exists" -Number 5
    New-Item -ItemType Directory -Force -Path (Join-Path $sandbox "_wt-5") | Out-Null
    $existsRun = Invoke-TakeTaskChild -Fixture $fixture -StateFile $existsState -Arguments @("-Tier", "standard")
    Assert-True "an existing worktree must abort the run" ($existsRun.ExitCode -ne 0)
    Assert-True "the run must name the existing worktree" ($existsRun.Text -match "already exists")
    $existsLabels = @((Read-StateIssue -StateFile $existsState -Number 5).labels | ForEach-Object { $_.name })
    Assert-True "the run must not claim a ticket it cannot build" ($existsLabels -notcontains "claimed")

    # --- -Issue and -Force (B9): the partition invariant is not bypassed -----
    $issueRefuseState = New-InitialState -Sandbox $sandbox -Name "e2e-issue-refuse" -Number 3 -Labels @("tier:standard", "claimed")
    $issueRefuse = Invoke-TakeTaskChild -Fixture $fixture -StateFile $issueRefuseState -Arguments @("-Issue", "3")
    Assert-True "-Issue on a claimed ticket must refuse" ($issueRefuse.ExitCode -ne 0)
    Assert-True "-Issue refusal must name 'claimed'" ($issueRefuse.Text -match "claimed")
    $issueRefuseLabels = @((Read-StateIssue -StateFile $issueRefuseState -Number 3).labels | ForEach-Object { $_.name })
    Assert-True "-Issue refusal must not change the labels" ($issueRefuseLabels -contains "claimed")

    $issueForceState = New-InitialState -Sandbox $sandbox -Name "e2e-issue-force" -Number 4 -Labels @("tier:standard", "claimed")
    $issueForce = Invoke-TakeTaskChild -Fixture $fixture -StateFile $issueForceState -Arguments @("-Issue", "4", "-Force")
    Assert-True "-Issue -Force on a claimed ticket must proceed" ($issueForce.ExitCode -eq 0)
    Assert-True "-Issue -Force must still claim the ticket" ($issueForce.Text -match "Claimed issue #4")

    $checks.acceptance = "ok (CR1-CR5 + overwrite refusal + -Issue/-Force)"

    [ordered]@{
        event  = "take_task_test"
        status = "ok"
        slug   = $checks.slug
        remoteRepo = $checks.remoteRepo
        claimProtocol = $checks.claimProtocol
        mutant = $checks.mutant
        acceptance = $checks.acceptance
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    if (Test-Path -LiteralPath $sandbox) {
        try {
            Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction Stop
        }
        catch {
            Write-Host "Note: could not remove the test sandbox '$sandbox' ($($_.Exception.Message))."
        }
    }
}
