[CmdletBinding()]
param(
    [ValidateSet("Status", "Setup")]
    [string]$Action = "Status",

    [switch]$SkipRemoteProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GitHubAuthTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

try {
    if ($Action -eq "Setup") {
        if (Test-CodexSandboxEnvironment) {
            throw (
                "GitHub login must run in the owner's normal PowerShell, not inside " +
                "the Codex sandbox. No token was requested or stored."
            )
        }
        if ($null -eq (Get-Command gh -CommandType Application -ErrorAction SilentlyContinue)) {
            throw "GitHub CLI is not installed."
        }

        & gh auth login --hostname github.com --git-protocol https --web
        if ($LASTEXITCODE -ne 0) {
            throw "gh auth login failed with exit code $LASTEXITCODE."
        }
        & gh auth setup-git
        if ($LASTEXITCODE -ne 0) {
            throw "gh auth setup-git failed with exit code $LASTEXITCODE."
        }
    }

    $report = Get-GitHubAuthReport `
        -RepositoryRoot $repoRoot `
        -SkipRemoteProbe:$SkipRemoteProbe
    $report | ConvertTo-Json -Depth 8 -Compress | Write-Host
    if (-not $report.ready) {
        exit 1
    }
}
catch {
    [ordered]@{
        event = "github_auth_diagnostic"
        status = "error"
        reason = $_.Exception.Message
        secretMaterialEmitted = $false
    } | ConvertTo-Json -Compress | Write-Host
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
