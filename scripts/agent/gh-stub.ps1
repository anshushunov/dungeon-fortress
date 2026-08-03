# A fixture stub for the gh CLI, used by test-take-task.ps1. It emulates the
# small subset of gh that take-task.ps1 calls, against a state file instead of
# the GitHub API, so the tests can exercise the real script end to end without
# touching the network or the real tracker.
#
# Dot-sourced by the test file to get Invoke-FixtureGh for in-process function
# tests, and invoked as a script by the child-process runs via DF_TAKE_TASK_GH.
# The state file path comes from DF_TAKE_TASK_STATE.
#
# Emulated state file (JSON):
# {
#   "issues": {
#     "5": {
#       "number": 5, "title": "...", "body": "...", "state": "OPEN",
#       "labels": [ { "name": "ready" }, ... ],
#       "comments": [
#         { "id": "IC_1001", "body": "...", "createdAt": "2026-...Z",
#           "url": "https://github.com/owner/repo/issues/5#issuecomment-1001" }
#       ]
#     }
#   },
#   "nextCommentNumber": 2000,
#   "failClaimPersistence": false,
#   "injectCompetitorOnClaim": false
# }
#
# - failClaimPersistence: an issue edit that would add 'claimed' silently drops
#   the write, simulating a lost API call. Used to prove the re-read catches a
#   claim that did not persist.
# - injectCompetitorOnClaim: when a claim marker comment is posted, a competitor
#   marker with an earlier createdAt is inserted first, simulating another agent
#   that claimed in the window between this agent's pre-read and its own marker
#   post. Used to prove the ownership check detects a lost race.

function ConvertTo-StubCommentId {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Number
    )
    return ("IC_" + $Number)
}

function Read-StubState {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param([string]$StateFile)

    $state = $null
    if (Test-Path -LiteralPath $StateFile) {
        try {
            $text = [IO.File]::ReadAllText($StateFile)
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $state = ConvertFrom-Json -InputObject $text
            }
        }
        catch {
            $state = $null
        }
    }
    if ($null -eq $state) {
        $state = ConvertFrom-Json -InputObject '{"issues":{},"nextCommentNumber":2000,"failClaimPersistence":false,"injectCompetitorOnClaim":false}'
    }
    return $state
}

function Write-StubState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [string]$StateFile
    )
    $json = $State | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($StateFile, $json, [Text.UTF8Encoding]::new($false))
}

function Get-StubIssue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )
    $names = @($State.issues.PSObject.Properties | ForEach-Object { $_.Name })
    if ($names -notcontains $Key) {
        return $null
    }
    return $State.issues.$Key
}

function Invoke-StubIssueView {
    [CmdletBinding()]
    [OutputType([object])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [int]$Number,

        [Parameter(Mandatory = $true)]
        [string[]]$JsonFields
    )

    $issue = Get-StubIssue -State $State -Key ([string]$Number)
    if ($null -eq $issue) {
        return $null
    }

    $result = [ordered]@{}
    foreach ($field in $JsonFields) {
        if ($field -eq "labels") {
            $result.labels = @($issue.labels)
        }
        elseif ($field -eq "comments") {
            $result.comments = @($issue.comments)
        }
        elseif ($field -eq "number") {
            $result.number = [int]$issue.number
        }
        elseif ($field -eq "title") {
            $result.title = [string]$issue.title
        }
        elseif ($field -eq "body") {
            $result.body = [string]$issue.body
        }
        elseif ($field -eq "state") {
            $result.state = [string]$issue.state
        }
    }
    return [pscustomobject]$result
}

function Test-StubSearchMatch {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Issue,

        [Parameter(Mandatory = $true)]
        [string]$Search
    )

    $tokens = @($Search -split '\s+')
    $names = @($Issue.labels | ForEach-Object { $_.name })

    foreach ($token in $tokens) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }
        if ($token -eq "is:issue" -or $token -eq "is:open") {
            continue
        }
        if ($token -match '^-label:(.+)$') {
            if ($names -contains $Matches[1]) {
                return $false
            }
            continue
        }
        if ($token -match '^label:(.+)$') {
            if ($names -notcontains $Matches[1]) {
                return $false
            }
            continue
        }
        if ($token -eq "is:closed") {
            if ([string]$Issue.state -ne "CLOSED") {
                return $false
            }
            continue
        }
    }
    return $true
}

function Invoke-StubIssueList {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [string]$Search
    )

    $matches = @()
    foreach ($name in @($State.issues.PSObject.Properties | ForEach-Object { $_.Name })) {
        $issue = $State.issues.$name
        if (Test-StubSearchMatch -Issue $issue -Search $Search) {
            $matches += [pscustomobject]@{
                number = [int]$issue.number
                title  = [string]$issue.title
            }
        }
    }
    return @($matches)
}

function Invoke-StubIssueEdit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [int]$Number,

        [string[]]$AddLabels,

        [string[]]$RemoveLabels
    )

    $issue = Get-StubIssue -State $State -Key ([string]$Number)
    if ($null -eq $issue) {
        return $false
    }

    $names = @($issue.labels | ForEach-Object { $_.name })

    $dropClaimed = ($State.failClaimPersistence -eq $true)
    foreach ($label in $AddLabels) {
        if ($dropClaimed -and $label -eq "claimed") {
            continue
        }
        if ($names -notcontains $label) {
            $names += $label
        }
    }
    foreach ($label in $RemoveLabels) {
        $names = @($names | Where-Object { $_ -ne $label })
    }

    $issue.labels = @($names | ForEach-Object {
        [pscustomobject]@{ name = $_ }
    })
    return $true
}

function Invoke-StubIssueComment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [int]$Number,

        [Parameter(Mandatory = $true)]
        [string]$Body
    )

    $issue = Get-StubIssue -State $State -Key ([string]$Number)
    if ($null -eq $issue) {
        return $false
    }

    $isClaimMarker = ($Body -match '^take-task claim: [0-9a-f]{32}$')

    if ($isClaimMarker -and $State.injectCompetitorOnClaim -eq $true) {
        $hasCompetitor = $false
        foreach ($existing in @($issue.comments)) {
            if ($existing.body -match '^take-task claim: [0-9a-f]{32}$') {
                $hasCompetitor = $true
            }
        }
        if (-not $hasCompetitor) {
            $competitorNumber = [int]$State.nextCommentNumber
            $issue.comments = @($issue.comments) + @([pscustomobject]@{
                id        = (ConvertTo-StubCommentId -Number $competitorNumber)
                body      = "take-task claim: 0123456789abcdef0123456789abcdef"
                createdAt = "2020-01-01T00:00:00Z"
                url       = "https://github.com/owner/repo/issues/$Number#issuecomment-$competitorNumber"
            })
            $State.nextCommentNumber = $competitorNumber + 1
        }
    }

    $commentNumber = [int]$State.nextCommentNumber
    $issue.comments = @($issue.comments) + @([pscustomobject]@{
        id        = (ConvertTo-StubCommentId -Number $commentNumber)
        body      = $Body
        createdAt = "2026-08-03T12:00:00Z"
        url       = "https://github.com/owner/repo/issues/$Number#issuecomment-$commentNumber"
    })
    $State.nextCommentNumber = $commentNumber + 1
    return $true
}

function Invoke-StubApi {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$State,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Method
    )

    if ($Method -ne "DELETE") {
        return $false
    }
    if ($Path -notmatch '^repos/[^/]+/[^/]+/issues/comments/(\d+)$') {
        return $false
    }
    $numericId = $Matches[1]
    $target = ConvertTo-StubCommentId -Number ([int]$numericId)
    foreach ($name in @($State.issues.PSObject.Properties | ForEach-Object { $_.Name })) {
        $issue = $State.issues.$name
        $remaining = @()
        $removed = $false
        foreach ($comment in @($issue.comments)) {
            if ([string]$comment.id -eq $target) {
                $removed = $true
                continue
            }
            $remaining += $comment
        }
        if ($removed) {
            $issue.comments = @($remaining)
            return $true
        }
    }
    return $false
}

function Invoke-FixtureGh {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $stateFile = $env:DF_TAKE_TASK_STATE
    if ([string]::IsNullOrWhiteSpace($stateFile)) {
        throw "DF_TAKE_TASK_STATE is not set; the gh stub has nowhere to keep its state."
    }

    $state = Read-StubState -StateFile $stateFile

    $positional = @()
    $jsonFields = @()
    $search = $null
    $addLabels = @()
    $removeLabels = @()
    $body = $null
    $method = $null

    $index = 0
    $argList = @($Arguments)
    while ($index -lt $argList.Count) {
        $arg = $argList[$index]
        switch -Regex ($arg) {
            '^(--json|-j)$' {
                $index++
                $jsonFields = @(($argList[$index] -split ',') | ForEach-Object { $_.Trim() })
            }
            '^(--search)$' {
                $index++
                $search = $argList[$index]
            }
            '^(--limit)$' {
                $index++
            }
            '^(--repo|-R)$' {
                $index++
            }
            '^(--add-label)$' {
                $index++
                $addLabels = @(($argList[$index] -split ',') | ForEach-Object { $_.Trim() })
            }
            '^(--remove-label)$' {
                $index++
                $removeLabels = @(($argList[$index] -split ',') | ForEach-Object { $_.Trim() })
            }
            '^(--body)$' {
                $index++
                $body = $argList[$index]
            }
            '^(-X)$' {
                $index++
                $method = $argList[$index]
            }
            default {
                $positional += $arg
            }
        }
        $index++
    }

    $subcommand = $null
    $operation = $null
    $number = $null
    $apiPath = $null
    if ($positional.Count -ge 1) {
        $subcommand = $positional[0]
    }
    if ($positional.Count -ge 2) {
        $operation = $positional[1]
    }
    if ($subcommand -eq "issue" -and $operation -ne "list" -and $positional.Count -ge 3) {
        $number = [int]$positional[2]
    }
    if ($subcommand -eq "api" -and $positional.Count -ge 2) {
        $apiPath = $positional[1]
    }

    $result = $null

    if ($subcommand -eq "issue" -and $operation -eq "view") {
        $data = Invoke-StubIssueView -State $state -Number $number -JsonFields $jsonFields
        if ($null -eq $data) {
            $result = [pscustomobject]@{ ExitCode = 1; Text = "issue not found" }
        }
        else {
            $result = [pscustomobject]@{ ExitCode = 0; Text = ($data | ConvertTo-Json -Compress -Depth 8) }
        }
    }
    elseif ($subcommand -eq "issue" -and $operation -eq "list") {
        $matches = @(Invoke-StubIssueList -State $state -Search $search)
        $result = [pscustomobject]@{ ExitCode = 0; Text = ($matches | ConvertTo-Json -Compress -Depth 4) }
    }
    elseif ($subcommand -eq "issue" -and $operation -eq "edit") {
        $ok = Invoke-StubIssueEdit -State $state -Number $number -AddLabels $addLabels -RemoveLabels $removeLabels
        if ($ok) {
            $result = [pscustomobject]@{ ExitCode = 0; Text = "" }
        }
        else {
            $result = [pscustomobject]@{ ExitCode = 1; Text = "issue not found" }
        }
    }
    elseif ($subcommand -eq "issue" -and $operation -eq "comment") {
        $ok = Invoke-StubIssueComment -State $state -Number $number -Body $body
        if ($ok) {
            $result = [pscustomobject]@{ ExitCode = 0; Text = "" }
        }
        else {
            $result = [pscustomobject]@{ ExitCode = 1; Text = "issue not found" }
        }
    }
    elseif ($subcommand -eq "api") {
        $ok = Invoke-StubApi -State $state -Path $apiPath -Method $method
        if ($ok) {
            $result = [pscustomobject]@{ ExitCode = 0; Text = "" }
        }
        else {
            $result = [pscustomobject]@{ ExitCode = 1; Text = "not found" }
        }
    }
    else {
        $result = [pscustomobject]@{ ExitCode = 1; Text = "unsupported gh call: $($Arguments -join ' ')" }
    }

    Write-StubState -State $state -StateFile $stateFile
    return $result
}

if ($MyInvocation.InvocationName -ne '.') {
    # The result must go through the PowerShell output streams, not the console
    # handle: the caller runs this script through `& <path> 2>&1`, and
    # [Console]::Out bypasses the pipeline and leaks past the capture.
    $result = Invoke-FixtureGh -Arguments $args
    if ($result.ExitCode -ne 0) {
        if (-not [string]::IsNullOrWhiteSpace($result.Text)) {
            Write-Error $result.Text
        }
        exit $result.ExitCode
    }
    if (-not [string]::IsNullOrWhiteSpace($result.Text)) {
        Write-Output $result.Text
    }
    exit 0
}
