# Functions for take-task.ps1, split out so the behavioural tests can dot-source
# them and drive them with a stub `gh` function in the caller's scope. This file
# has no top-level side effects: dot-sourcing it only defines functions.
#
# Two script-scope variables are the only state the functions need, and both are
# set by the entry script before the first call:
#
#   $script:GhCommand - the gh executable (or path to a stub) to invoke;
#   $script:RepoName  - owner/repo passed as --repo to every gh call.
#
# They are set here with sane defaults as well, so a dot-sourcing test that does
# not set them still has something to call.
$script:GhCommand = if ($env:DF_TAKE_TASK_GH) { $env:DF_TAKE_TASK_GH } else { "gh" }
# DF_TAKE_TASK_REPO is a test seam: the fixture repository has a local bare
# remote whose URL carries no owner/repo, so the tests pin the value. In real
# use the entry script derives it from origin.
$script:RepoName = if ($env:DF_TAKE_TASK_REPO) { $env:DF_TAKE_TASK_REPO } else { $null }

# A claim marker. The marker is the ownership record: GitHub labels have no
# values, so "claimed" alone cannot tell two racing agents apart. The marker is
# a comment whose body carries a random token, and the first marker on the issue
# is the owner. Marker regex must match what Invoke-Claim posts.
$script:ClaimMarkerPattern = '^take-task claim: ([0-9a-f]{32})$'

function Invoke-Native {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments
    )

    # Native stderr under $ErrorActionPreference="Stop" becomes a terminating
    # NativeCommandError in Windows PowerShell 5.1, even when merged with 2>&1:
    # git worktree add prints "Preparing worktree ..." to stderr and was dying
    # on it (review finding B1). Every native call therefore runs with the
    # preference weakened to Continue, and the merged output is converted to
    # plain strings so the caller sees text, not ErrorRecord objects.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $combined = @()
    $exitCode = -1
    try {
        # $LASTEXITCODE is only written by native executables. When the caller
        # points $script:GhCommand at a PowerShell function (the test stub),
        # nothing sets it, and reading it under Set-StrictMode throws. It is
        # initialised here and the stub sets it after each call, so both native
        # and function backends report through the same channel.
        $global:LASTEXITCODE = 0
        $combined = @(& $FilePath @Arguments 2>&1)
        $exitCode = $global:LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $textLines = @()
    foreach ($line in $combined) {
        if ($line -is [System.Management.Automation.ErrorRecord]) {
            $textLines += $line.Exception.Message
        }
        else {
            $textLines += ([string]$line)
        }
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text     = ($textLines -join [Environment]::NewLine)
    }
}

function ConvertTo-Slug {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Title
    )

    # Russian transliteration with the two cases the earlier loop missed: ё (U+0451)
    # and Ё (U+0401) are outside the 0x430-0x44F / 0x410-0x42F ranges (review B3).
    # The map is exhaustive for Cyrillic; every other character is either kept when
    # it is an ASCII letter or digit, or becomes a dash. The result is therefore
    # pure ASCII and safe for git refs.
    $map = @{
        0x0451 = 'e';   0x0401 = 'e'
        0x0430 = 'a';   0x0410 = 'a'
        0x0431 = 'b';   0x0411 = 'b'
        0x0432 = 'v';   0x0412 = 'v'
        0x0433 = 'g';   0x0413 = 'g'
        0x0434 = 'd';   0x0414 = 'd'
        0x0435 = 'e';   0x0415 = 'e'
        0x0436 = 'zh';  0x0416 = 'zh'
        0x0437 = 'z';   0x0417 = 'z'
        0x0438 = 'i';   0x0418 = 'i'
        0x0439 = 'y';   0x0419 = 'y'
        0x043A = 'k';   0x041A = 'k'
        0x043B = 'l';   0x041B = 'l'
        0x043C = 'm';   0x041C = 'm'
        0x043D = 'n';   0x041D = 'n'
        0x043E = 'o';   0x041E = 'o'
        0x043F = 'p';   0x041F = 'p'
        0x0440 = 'r';   0x0420 = 'r'
        0x0441 = 's';   0x0421 = 's'
        0x0442 = 't';   0x0422 = 't'
        0x0443 = 'u';   0x0423 = 'u'
        0x0444 = 'f';   0x0424 = 'f'
        0x0445 = 'kh';  0x0425 = 'kh'
        0x0446 = 'ts';  0x0426 = 'ts'
        0x0447 = 'ch';  0x0427 = 'ch'
        0x0448 = 'sh';  0x0428 = 'sh'
        0x0449 = 'shh'; 0x0429 = 'shh'
        0x044A = '-';   0x042A = '-'
        0x044B = 'y';   0x042B = 'y'
        0x044C = '-';   0x042C = '-'
        0x044D = 'e';   0x042D = 'e'
        0x044E = 'yu';  0x042E = 'yu'
        0x044F = 'ya';  0x042F = 'ya'
    }

    $sb = [System.Text.StringBuilder]::new()
    foreach ($c in $Title.ToCharArray()) {
        $o = [int]$c
        if ($map.ContainsKey($o)) {
            [void]$sb.Append($map[$o])
        }
        # Space must become a dash: a space is not a valid git ref character and
        # the old code passed it through unchanged (review B2).
        elseif ($c -eq ' ' -or $c -eq '-' -or $c -eq '_') {
            [void]$sb.Append('-')
        }
        elseif (($o -ge 0x30 -and $o -le 0x39) -or
                ($o -ge 0x41 -and $o -le 0x5A) -or
                ($o -ge 0x61 -and $o -le 0x7A)) {
            [void]$sb.Append([char][char]::ToLowerInvariant($c))
        }
        else {
            [void]$sb.Append('-')
        }
    }

    $result = $sb.ToString()
    $result = ($result -replace '-{2,}', '-') -replace '^-+', '' -replace '-+$', ''
    if ([string]::IsNullOrWhiteSpace($result)) {
        $result = "issue"
    }
    return $result
}

function Get-RemoteRepoName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    # gh works on the repository this script computes, not on the caller's cwd
    # (review N1): --repo is derived from origin and passed to every gh call, so
    # running the script from inside another checkout cannot touch the wrong
    # repository.
    $r = Invoke-Native -FilePath "git" -Arguments @("-C", $RepoRoot, "config", "--get", "remote.origin.url")
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Text)) {
        throw "No 'origin' remote found for '$RepoRoot'. take-task must run inside a git working copy that has a GitHub remote."
    }

    $url = $r.Text.Trim()
    $path = $null
    if ($url -match '^[a-zA-Z][a-zA-Z0-9+.-]*://([^/]+/)?(.+)$') {
        $path = $Matches[2]
    }
    elseif ($url -match '^[^@/]+@[^:/]+:(.+)$') {
        $path = $Matches[1]
    }
    else {
        $path = $url
    }

    $path = ($path -replace '\.git$', '').Trim('/')
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Could not parse the origin remote url '$url'."
    }
    return $path
}

function Get-IssueData {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num
    )

    $r = Invoke-Native -FilePath $script:GhCommand `
        -Arguments @("issue", "view", "$Num", "--json", "number,title,body,state,labels", "--repo", $script:RepoName)
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Text)) {
        return $null
    }
    try {
        return (ConvertFrom-Json -InputObject $r.Text)
    }
    catch {
        return $null
    }
}

function Read-IssueLabels {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num
    )

    $r = Invoke-Native -FilePath $script:GhCommand `
        -Arguments @("issue", "view", "$Num", "--json", "labels", "--repo", $script:RepoName)
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Text)) {
        return @()
    }
    $data = $null
    try {
        $data = ConvertFrom-Json -InputObject $r.Text
    }
    catch {
        return @()
    }
    if ($null -eq $data -or $null -eq $data.labels) {
        return @()
    }
    return @($data.labels | ForEach-Object { $_.name })
}

function Read-IssueComments {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num
    )

    $r = Invoke-Native -FilePath $script:GhCommand `
        -Arguments @("issue", "view", "$Num", "--json", "comments", "--repo", $script:RepoName)
    if ($r.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($r.Text)) {
        return @()
    }
    $data = $null
    try {
        $data = ConvertFrom-Json -InputObject $r.Text
    }
    catch {
        return @()
    }
    if ($null -eq $data -or $null -eq $data.comments) {
        return @()
    }
    return @($data.comments)
}

function Set-IssueLabels {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num,

        [string[]]$Add,

        [string[]]$Remove
    )

    # gh issue edit accepts both flags in one call. The old code called the
    # helper twice; with only a -Remove list the first call had $Add = $null and
    # `if ($Add.Count -gt 0)` threw under Set-StrictMode, and the catch blamed
    # the GitHub API for what was a code defect (review B8).
    $arguments = @("issue", "edit", "$Num")
    if (@($Add).Count -gt 0) {
        $arguments += @("--add-label", (@($Add) -join ","))
    }
    if (@($Remove).Count -gt 0) {
        $arguments += @("--remove-label", (@($Remove) -join ","))
    }
    $arguments += @("--repo", $script:RepoName)

    $r = Invoke-Native -FilePath $script:GhCommand -Arguments $arguments
    if ($r.ExitCode -ne 0) {
        throw ("Failed to update labels for issue #{0}: {1}" -f $Num, $r.Text)
    }
}

function Invoke-RemoveClaimMarker {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num,

        [Parameter(Mandatory = $true)]
        [string]$Token
    )

    # Best effort: a comment the claimant no longer owns is deleted so a later
    # claim is not misled by it. gh returns the GraphQL node id in the JSON, but
    # the REST DELETE needs the numeric id, which lives in the html url as
    # #issuecomment-<digits>.
    $comments = @(Read-IssueComments -Num $Num)
    foreach ($comment in $comments) {
        if ([string]$comment.body -ne ("take-task claim: " + $Token)) {
            continue
        }
        $match = [regex]::Match([string]$comment.url, '#issuecomment-(\d+)')
        if (-not $match.Success) {
            return
        }
        $numericId = $match.Groups[1].Value
        Invoke-Native -FilePath $script:GhCommand `
            -Arguments @("api", "repos/$script:RepoName/issues/comments/$numericId", "-X", "DELETE") | Out-Null
        return
    }
}

function Invoke-CleanStaleClaimMarkers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num
    )

    # A claim marker is only meaningful while the 'claimed' label is present.
    # Once the coordinator re-issues the ticket (removes 'claimed', re-adds
    # 'ready'), the old markers are stale, and a later claim would otherwise
    # mistake the first stale marker for a live owner and lose forever. Since
    # the caller only reaches here when 'claimed' is absent, every marker found
    # is either stale or belongs to a claim still in its posting window; the
    # former is deleted here, the latter is caught by the ownership check.
    $comments = @(Read-IssueComments -Num $Num)
    foreach ($comment in $comments) {
        if ($comment.body -match $script:ClaimMarkerPattern) {
            Invoke-RemoveClaimMarker -Num $Num -Token $Matches[1]
        }
    }
}

function Test-ClaimOwner {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num,

        [Parameter(Mandatory = $true)]
        [string]$Token
    )

    # Ownership is "the first claim marker on the issue", because exactly one
    # agent can hold that position and every other claimant sees it. gh returns
    # comments in chronological order, and the createdAt timestamps are ISO-8601,
    # so sorting on createdAt orders them.
    $claims = @()
    foreach ($comment in @(Read-IssueComments -Num $Num)) {
        if ($comment.body -match $script:ClaimMarkerPattern) {
            $claims += [pscustomobject]@{
                CreatedAt = [string]$comment.createdAt
                Token     = $Matches[1]
            }
        }
    }
    if ($claims.Count -eq 0) {
        return $false
    }
    $claims = @($claims | Sort-Object -Property CreatedAt)
    return ($claims[0].Token -eq $Token)
}

function Invoke-Claim {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num,

        [Parameter(Mandatory = $true)]
        [string]$Title,

        [switch]$Force
    )

    # The claim protocol, in the order that matters:
    #   1. a marker comment carries a fresh random token - the ownership record
    #      (review B5), because "claimed" alone is idempotent and indistinguishable;
    #   2. the labels are changed afterwards;
    #   3. the labels are re-read to prove the write persisted (review B6/B8) and
    #      that 'ready' is really gone;
    #   4. ownership is re-read to detect a lost race (review B5): if another
    #      agent's marker is earlier, this claim lost and the caller takes the
    #      next candidate (review B4).
    # A failed claim removes its own marker and reports why, leaving no trace.
    $labels = @(Read-IssueLabels -Num $Num)
    $hadReady = $labels -contains 'ready'

    if (-not $Force) {
        if ($labels -contains 'claimed') {
            return [pscustomobject]@{ Claimed = $false; Reason = "already claimed"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $null }
        }
        if (-not $hadReady) {
            return [pscustomobject]@{ Claimed = $false; Reason = "not marked 'ready'"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $null }
        }
        Invoke-CleanStaleClaimMarkers -Num $Num
    }

    $token = [Guid]::NewGuid().ToString("N")
    $marker = "take-task claim: $token"

    $comment = Invoke-Native -FilePath $script:GhCommand `
        -Arguments @("issue", "comment", "$Num", "--body", $marker, "--repo", $script:RepoName)
    if ($comment.ExitCode -ne 0) {
        return [pscustomobject]@{ Claimed = $false; Reason = "could not post claim marker: $($comment.Text)"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
    }

    try {
        Set-IssueLabels -Num $Num -Add @("claimed") -Remove @("ready")
    }
    catch {
        Invoke-RemoveClaimMarker -Num $Num -Token $token
        return [pscustomobject]@{ Claimed = $false; Reason = "could not set labels: $($_.Exception.Message)"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
    }

    $reRead = @(Read-IssueLabels -Num $Num)
    if ($reRead -notcontains 'claimed') {
        Invoke-RemoveClaimMarker -Num $Num -Token $token
        return [pscustomobject]@{ Claimed = $false; Reason = "'claimed' did not persist after the claim"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
    }
    if ($reRead -contains 'ready') {
        try {
            Set-IssueLabels -Num $Num -Remove @("ready")
        }
        catch {
        }
        $reReadAgain = @(Read-IssueLabels -Num $Num)
        if ($reReadAgain -contains 'ready') {
            Invoke-RemoveClaimMarker -Num $Num -Token $token
            return [pscustomobject]@{ Claimed = $false; Reason = "could not remove 'ready'"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
        }
    }

    if (-not $Force) {
        if (-not (Test-ClaimOwner -Num $Num -Token $token)) {
            Invoke-RemoveClaimMarker -Num $Num -Token $token
            return [pscustomobject]@{ Claimed = $false; Reason = "lost the race: another agent's claim marker is earlier"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
        }
    }

    return [pscustomobject]@{ Claimed = $true; Reason = "won"; Num = $Num; Title = $Title; PreClaimReady = $hadReady; Token = $token }
}

function Invoke-UndoClaim {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [int]$Num,

        [Parameter(Mandatory = $true)]
        [string]$Token,

        [switch]$RestoreReady
    )

    # Rollback of a claim whose work never started (review B10): the issue must
    # not stay invisible to other agents because the script died on the way. The
    # rollback only touches labels it verifiably owns, so a loser never damages
    # the winner's claim. Best effort: a failed rollback is reported, not thrown.
    if (-not (Test-ClaimOwner -Num $Num -Token $Token)) {
        Write-Output ("Not rolling back the claim on issue #{0}: another agent holds it." -f $Num)
        return
    }

    $arguments = @("issue", "edit", "$Num", "--remove-label", "claimed")
    if ($RestoreReady) {
        $arguments += @("--add-label", "ready")
    }
    $arguments += @("--repo", $script:RepoName)

    $r = Invoke-Native -FilePath $script:GhCommand -Arguments $arguments
    if ($r.ExitCode -ne 0) {
        Write-Output ("Could not roll back the claim on issue #{0}: {1}" -f $Num, $r.Text)
        return
    }

    if ($RestoreReady) {
        Write-Output ("Rolled back the claim on issue #{0}: removed 'claimed', restored 'ready'." -f $Num)
    }
    else {
        Write-Output ("Rolled back the claim on issue #{0}: removed 'claimed'." -f $Num)
    }
}
