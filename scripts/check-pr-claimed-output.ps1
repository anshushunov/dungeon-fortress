[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BodyFile,

    [string]$RepoRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}
else {
    $RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
}

$gitReadOnlySubcommands = @(
    "status", "log", "show", "diff", "rev-parse", "describe",
    "ls-files", "ls-tree", "cat-file", "blame", "shortlog", "grep",
    "count-objects", "for-each-ref", "show-ref", "name-rev",
    "check-attr", "check-ignore", "check-mailmap"
)

$psAllowedSwitches = @(
    "-NoProfile", "-NoLogo", "-NonInteractive", "-STA",
    "-ExecutionPolicy", "-File", "-f"
)
$psForbiddenSwitches = @(
    "-Command", "-c", "-EncodedCommand", "-e", "-WindowStyle", "-NoExit"
)

function Get-ClaimPairs {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $lines = [IO.File]::ReadAllLines($Path, [Text.Encoding]::UTF8)
    $pairs = [System.Collections.Generic.List[object]]::new()
    $insideFence = $false
    $fenceStart = 0
    $fenceLanguage = ""
    $commandLines = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $fenceMatch = [regex]::Match($line, '^```\s*([^`]*)\s*$')
        if ($fenceMatch.Success) {
            if (-not $insideFence) {
                $insideFence = $true
                $fenceStart = $i + 1
                $fenceLanguage = $fenceMatch.Groups[1].Value.Trim().ToLowerInvariant()
                $commandLines.Clear()
            }
            else {
                $insideFence = $false
                $command = (($commandLines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
                $claim = $null
                $claimLine = 0
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    if ($lines[$j].Trim().Length -eq 0) {
                        continue
                    }
                    $claimMatch = [regex]::Match($lines[$j], '^\s*(Expected|Заявлено)\s*:\s*(.+?)\s*$')
                    if ($claimMatch.Success) {
                        $claim = $claimMatch.Groups[2].Value.Trim()
                        $claimLine = $j + 1
                    }
                    break
                }
                $pairs.Add([pscustomobject]@{
                    claimedFrom = ("{0}:{1}" -f $Path, $fenceStart)
                    command = $command
                    language = $fenceLanguage
                    claim = $claim
                    claimLine = $claimLine
                })
            }
            continue
        }

        if ($insideFence) {
            $commandLines.Add($line)
        }
    }

    if ($insideFence) {
        $command = (($commandLines | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
        $pairs.Add([pscustomobject]@{
            claimedFrom = ("{0}:{1}" -f $Path, $fenceStart)
            command = $command
            language = $fenceLanguage
            claim = $null
            claimLine = 0
            parseProblem = "unterminated fenced command block"
        })
    }

    return @($pairs)
}

function ConvertTo-CommandTokens {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command
    )

    $tokens = [System.Collections.Generic.List[string]]::new()
    $sb = [System.Text.StringBuilder]::new()
    $inToken = $false
    $quote = $null
    for ($i = 0; $i -lt $Command.Length; $i++) {
        $c = $Command[$i]
        if ($null -ne $quote) {
            if ($c -eq $quote) {
                $quote = $null
            }
            else {
                [void]$sb.Append($c)
            }
            continue
        }
        if ($c -eq '"' -or $c -eq "'") {
            $quote = $c
            $inToken = $true
            continue
        }
        if ([char]::IsWhiteSpace($c)) {
            if ($inToken) {
                $tokens.Add($sb.ToString())
                [void]$sb.Clear()
                $inToken = $false
            }
            continue
        }
        $inToken = $true
        [void]$sb.Append($c)
    }
    if ($inToken) {
        $tokens.Add($sb.ToString())
    }
    return @($tokens)
}

function Test-ForbiddenToken {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Token
    )

    return $Token -match '[\x60;|&<>]'
}

function Test-PowerShellInvocation {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Tokens,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    $i = 1
    $filePath = $null
    $scriptArgs = [System.Collections.Generic.List[string]]::new()
    $sawFile = $false
    while ($i -lt $Tokens.Count) {
        $token = $Tokens[$i]
        if ($token -in $psForbiddenSwitches) {
            return [pscustomobject]@{
                Runnable = $false
                Reason = "inline execution switch '$token' is not allowed"
            }
        }
        if ($token -eq "-ExecutionPolicy") {
            if ($i + 1 -ge $Tokens.Count) {
                return [pscustomobject]@{ Runnable = $false; Reason = "-ExecutionPolicy without a value" }
            }
            $i += 2
            continue
        }
        if ($token -in @("-File", "-f")) {
            if ($i + 1 -ge $Tokens.Count) {
                return [pscustomobject]@{ Runnable = $false; Reason = "-File without a path" }
            }
            $filePath = $Tokens[$i + 1]
            $sawFile = $true
            $i += 2
            break
        }
        if ($token -in $psAllowedSwitches) {
            $i++
            continue
        }
        return [pscustomobject]@{
            Runnable = $false
            Reason = "unexpected token '$token' in powershell invocation"
        }
    }

    if (-not $sawFile -or $null -eq $filePath) {
        return [pscustomobject]@{ Runnable = $false; Reason = "powershell invocation requires -File" }
    }
    if ((Test-ForbiddenToken -Token $filePath)) {
        return [pscustomobject]@{ Runnable = $false; Reason = "script path contains shell metacharacters" }
    }

    $resolved = [IO.Path]::GetFullPath((Join-Path $RepoRoot $filePath))
    $rootPrefix = $RepoRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{ Runnable = $false; Reason = "script path resolves outside the working copy" }
    }

    while ($i -lt $Tokens.Count) {
        $arg = $Tokens[$i]
        if ((Test-ForbiddenToken -Token $arg)) {
            return [pscustomobject]@{
                Runnable = $false
                Reason = "script argument contains shell metacharacters"
            }
        }
        $scriptArgs.Add($arg)
        $i++
    }

    return [pscustomobject]@{
        Runnable = $true
        FileName = "powershell"
        Arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $resolved) + @($scriptArgs)
    }
}

function Test-GitInvocation {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Tokens
    )

    if ($Tokens.Count -lt 2) {
        return [pscustomobject]@{ Runnable = $false; Reason = "git subcommand is missing" }
    }
    $subcommand = $Tokens[1]
    if ($subcommand -notin $gitReadOnlySubcommands) {
        return [pscustomobject]@{
            Runnable = $false
            Reason = "git subcommand '$subcommand' is not on the read-only allow-list"
        }
    }
    for ($i = 2; $i -lt $Tokens.Count; $i++) {
        if ($Tokens[$i] -eq "--output" -or $Tokens[$i] -like "--output=*" -or
            (Test-ForbiddenToken -Token $Tokens[$i])) {
            return [pscustomobject]@{
                Runnable = $false
                Reason = "git argument '$($Tokens[$i])' is not allowed"
            }
        }
    }
    return [pscustomobject]@{
        Runnable = $true
        FileName = "git"
        Arguments = @($Tokens[1..($Tokens.Count - 1)])
    }
}

function Test-RunnableCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$Language,

        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    if ($Command.Trim().Length -eq 0) {
        return [pscustomobject]@{ Runnable = $false; Reason = "empty command block" }
    }
    if ($Language -notin @("powershell", "pwsh", "ps1", "")) {
        return [pscustomobject]@{ Runnable = $false; Reason = "unsupported fence language '$Language'" }
    }

    $tokens = ConvertTo-CommandTokens -Command $Command
    if ($tokens.Count -eq 0) {
        return [pscustomobject]@{ Runnable = $false; Reason = "empty command block" }
    }

    switch ($tokens[0].ToLowerInvariant()) {
        "powershell" { return Test-PowerShellInvocation -Tokens $tokens -RepoRoot $RepoRoot }
        "pwsh" {
            $plan = Test-PowerShellInvocation -Tokens $tokens -RepoRoot $RepoRoot
            if ($plan.Runnable) {
                $plan.FileName = "pwsh"
            }
            return $plan
        }
        "git" { return Test-GitInvocation -Tokens $tokens }
        default {
            return [pscustomobject]@{
                Runnable = $false
                Reason = "executable '$($tokens[0])' is not on the allow-list"
            }
        }
    }
}

function ConvertTo-ArgumentString {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $quoted = foreach ($a in $Arguments) {
        if ($a -match '^[\w.:/\\-]+$') {
            $a
        }
        else {
            '"' + $a.Replace('"', '\"') + '"'
        }
    }
    return ($quoted -join " ")
}

function Invoke-ClaimProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FileName
    $psi.Arguments = ConvertTo-ArgumentString -Arguments $Arguments
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::new()
    $proc.StartInfo = $psi
    try {
        [void]$proc.Start()
    }
    catch {
        return [pscustomobject]@{
            Started = $false
            ExitCode = -1
            Stdout = ""
            Stderr = $_.Exception.Message
            Output = $_.Exception.Message
        }
    }

    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()
    $exit = $proc.ExitCode
    $proc.Dispose()

    $combined = if ($stderr) { $stdout + [Environment]::NewLine + $stderr } else { $stdout }
    return [pscustomobject]@{
        Started = $true
        ExitCode = $exit
        Stdout = $stdout
        Stderr = $stderr
        Output = $combined
    }
}

$mismatchCount = 0
$pairs = Get-ClaimPairs -Path $BodyFile

foreach ($pair in $pairs) {
    try {
        $parseProblem = if ($pair.PSObject.Properties.Name -contains "parseProblem") { $pair.parseProblem } else { $null }
        if ($null -ne $parseProblem) {
            [ordered]@{
                claimedFrom = $pair.claimedFrom
                command = $pair.command
                claim = $pair.claim
                status = "not-runnable"
                reason = $parseProblem
            } | ConvertTo-Json -Compress | Write-Host
            continue
        }

        if ($null -eq $pair.claim -or $pair.claim.Length -eq 0) {
            [ordered]@{
                claimedFrom = $pair.claimedFrom
                command = $pair.command
                claim = $pair.claim
                status = "not-runnable"
                reason = "missing Expected: or Заявлено: claim line"
            } | ConvertTo-Json -Compress | Write-Host
            continue
        }

        $plan = Test-RunnableCommand -Command $pair.command -Language $pair.language -RepoRoot $RepoRoot
        if (-not $plan.Runnable) {
            [ordered]@{
                claimedFrom = $pair.claimedFrom
                command = $pair.command
                claim = $pair.claim
                status = "not-runnable"
                reason = $plan.Reason
            } | ConvertTo-Json -Compress | Write-Host
            continue
        }

        $run = Invoke-ClaimProcess `
            -FileName $plan.FileName `
            -Arguments $plan.Arguments `
            -WorkingDirectory $RepoRoot

        if (-not $run.Started) {
            [ordered]@{
                claimedFrom = $pair.claimedFrom
                command = $pair.command
                claim = $pair.claim
                status = "not-runnable"
                reason = "command could not start: $($run.Output)"
            } | ConvertTo-Json -Compress | Write-Host
            continue
        }

        if ($run.ExitCode -ne 0) {
            $stderrLine = ($run.Stderr -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
            if (-not $stderrLine) {
                $stderrLine = ($run.Stdout -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
            }
            $reason = "command exited with code $($run.ExitCode)"
            if ($stderrLine) {
                $reason += ": $stderrLine"
            }
            [ordered]@{
                claimedFrom = $pair.claimedFrom
                command = $pair.command
                claim = $pair.claim
                status = "not-runnable"
                reason = $reason
            } | ConvertTo-Json -Compress | Write-Host
            continue
        }

        $status = if ($run.Output.Contains($pair.claim)) { "match" } else { "mismatch" }
        if ($status -eq "mismatch") {
            $mismatchCount++
        }
        [ordered]@{
            claimedFrom = $pair.claimedFrom
            command = $pair.command
            claim = $pair.claim
            status = $status
            exitCode = $run.ExitCode
        } | ConvertTo-Json -Compress | Write-Host
    }
    catch {
        [ordered]@{
            claimedFrom = $pair.claimedFrom
            command = $pair.command
            claim = $pair.claim
            status = "not-runnable"
            reason = "unexpected error: $($_.Exception.Message)"
        } | ConvertTo-Json -Compress | Write-Host
    }
}

if ($mismatchCount -gt 0) {
    exit 1
}
exit 0
