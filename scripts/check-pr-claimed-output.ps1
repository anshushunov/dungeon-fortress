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

function Test-RunnableCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$Language
    )

    if ($Command.Trim().Length -eq 0) {
        return [pscustomobject]@{ Runnable = $false; Reason = "empty command block" }
    }
    if ($Language -notin @("powershell", "pwsh", "ps1", "")) {
        return [pscustomobject]@{ Runnable = $false; Reason = "unsupported fence language '$Language'" }
    }
    if ($Command -match '[>|&;]') {
        return [pscustomobject]@{ Runnable = $false; Reason = "shell operators are not allowed" }
    }
    if ($Command -match '\b(Remove-Item|New-Item|Set-Content|Add-Content|Out-File|Start-Process|Invoke-WebRequest|curl|wget)\b') {
        return [pscustomobject]@{ Runnable = $false; Reason = "mutating or network command is not allowed" }
    }
    if ($Command -match '^\s*git\s+(commit|push|reset|checkout|switch|merge|rebase|clean|worktree)\b') {
        return [pscustomobject]@{ Runnable = $false; Reason = "mutating git command is not allowed" }
    }
    if ($Command -notmatch '^\s*(powershell|pwsh|git)\b') {
        return [pscustomobject]@{ Runnable = $false; Reason = "executable is not on the allowlist" }
    }
    return [pscustomobject]@{ Runnable = $true; Reason = $null }
}

function Invoke-ClaimCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -Command $Command 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

$mismatchCount = 0
$pairs = Get-ClaimPairs -Path $BodyFile

foreach ($pair in $pairs) {
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

    $policy = Test-RunnableCommand -Command $pair.command -Language $pair.language
    if (-not $policy.Runnable) {
        [ordered]@{
            claimedFrom = $pair.claimedFrom
            command = $pair.command
            claim = $pair.claim
            status = "not-runnable"
            reason = $policy.Reason
        } | ConvertTo-Json -Compress | Write-Host
        continue
    }

    Push-Location -LiteralPath $RepoRoot
    try {
        $run = Invoke-ClaimCommand -Command $pair.command -WorkingDirectory $RepoRoot
    }
    finally {
        Pop-Location
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

if ($mismatchCount -gt 0) {
    exit 1
}
exit 0
