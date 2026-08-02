[CmdletBinding()]
param(
    [string]$RepoRoot,

    [string]$Remote = "origin",

    [string]$Branch = "main",

    [switch]$Fetch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$previousPreference = $ErrorActionPreference

function Invoke-GitCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $ErrorActionPreference = "Continue"
    try {
        $output = @(& git -C $RepoRoot @Arguments 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Lines = @($output | ForEach-Object { [string]$_ })
        }
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

if ($Fetch) {
    $result = Invoke-GitCapture -Arguments @("fetch", $Remote, $Branch)
    if ($result.ExitCode -ne 0) {
        Write-Host "Could not fetch $Remote/$Branch"
        exit 2
    }
}

$remoteRef = "$Remote/$Branch"

$mergeBaseResult = Invoke-GitCapture -Arguments @("merge-base", "HEAD", $remoteRef)
if ($mergeBaseResult.ExitCode -ne 0 -or $mergeBaseResult.Lines.Count -eq 0) {
    Write-Host "No common ancestor with $remoteRef; cannot measure staleness."
    exit 2
}

$behindResult = Invoke-GitCapture -Arguments @("rev-list", "--count", "$($mergeBaseResult.Lines[0])..$remoteRef")
$aheadResult = Invoke-GitCapture -Arguments @("rev-list", "--count", "$remoteRef..HEAD")
if ($behindResult.ExitCode -ne 0 -or $aheadResult.ExitCode -ne 0) {
    Write-Host "Could not count commits against $remoteRef."
    exit 2
}

$headResult = Invoke-GitCapture -Arguments @("rev-parse", "--short", "HEAD")
$remoteHeadResult = Invoke-GitCapture -Arguments @("rev-parse", "--short", $remoteRef)

$behindCount = [int]($behindResult.Lines[-1])
$aheadCount = [int]($aheadResult.Lines[-1])

[ordered]@{
    event = "base_stale_check"
    status = if ($behindCount -gt 0) { "stale" } else { "fresh" }
    remote = $Remote
    branch = $Branch
    behind = $behindCount
    ahead = $aheadCount
    head = $headResult.Lines[-1]
    remoteHead = $remoteHeadResult.Lines[-1]
} | ConvertTo-Json -Compress | Write-Host

if ($behindCount -gt 0) {
    exit 1
}
exit 0
