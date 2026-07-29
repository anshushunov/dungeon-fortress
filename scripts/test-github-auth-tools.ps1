[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GitHubAuthTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

$ghCases = @(
    [pscustomobject]@{ Exit = 0; Text = ""; Expected = "authenticated" },
    [pscustomobject]@{ Exit = 1; Text = "The token is invalid."; Expected = "invalid" },
    [pscustomobject]@{ Exit = 1; Text = "You are not logged into any GitHub hosts."; Expected = "missing" },
    [pscustomobject]@{ Exit = 1; Text = "Could not resolve host."; Expected = "network_unavailable" }
)
foreach ($case in $ghCases) {
    $actual = Get-GhAuthState -ExitCode $case.Exit -Output $case.Text
    if ($actual -cne $case.Expected) {
        throw "gh auth classifier returned '$actual', expected '$($case.Expected)'."
    }
}

$gitCases = @(
    [pscustomobject]@{
        Exit = 0
        Text = ""
        Sandbox = $true
        Expected = "authenticated"
    },
    [pscustomobject]@{
        Exit = 128
        Text = "schannel: failed to receive handshake, SSL/TLS connection failed: SEC_E_NO_CREDENTIALS"
        Sandbox = $true
        Expected = "sandbox_credential_unavailable"
    },
    [pscustomobject]@{
        Exit = 128
        Text = "fatal: Authentication failed"
        Sandbox = $false
        Expected = "rejected"
    }
)
foreach ($case in $gitCases) {
    $actual = Get-GitRemoteAuthState `
        -ExitCode $case.Exit `
        -Output $case.Text `
        -InCodexSandbox $case.Sandbox
    if ($actual -cne $case.Expected) {
        throw "Git auth classifier returned '$actual', expected '$($case.Expected)'."
    }
}

if (Test-GitHubAuthReady `
    -GhState "authenticated" `
    -GitWriteState "authenticated" `
    -CredentialHelperConfigured $false `
    -EmbeddedCredential $false) {
    throw "Auth readiness accepted missing credential helper."
}
if (Test-GitHubAuthReady `
    -GhState "authenticated" `
    -GitWriteState "not_checked" `
    -CredentialHelperConfigured $true `
    -EmbeddedCredential $false) {
    throw "Auth readiness accepted an unproven Git write state."
}
if (-not (Test-GitHubAuthReady `
    -GhState "authenticated" `
    -GitWriteState "authenticated" `
    -CredentialHelperConfigured $true `
    -EmbeddedCredential $false)) {
    throw "Auth readiness rejected a fully proven setup."
}

$report = Get-GitHubAuthReport -RepositoryRoot $repoRoot -SkipRemoteProbe
$serialized = $report | ConvertTo-Json -Depth 8 -Compress
foreach ($forbidden in @(
    '"Text"',
    '"ExitCode"',
    "credential.helper",
    "https://github.com/anshushunov/dungeon-fortress.git",
    "ghp_",
    "github_pat_"
)) {
    if ($serialized -match [regex]::Escape($forbidden)) {
        throw "Structured auth diagnostic leaked forbidden raw probe material '$forbidden'."
    }
}
if ($report.git.writeAuthState -cne "not_checked" -or
    -not $report.git.originConfigured -or
    $report.git.originHost -cne "github.com") {
    throw "Auth diagnostic does not preserve safe remote metadata."
}
$authToolsText = [IO.File]::ReadAllText(
    (Join-Path $repoRoot "scripts\GitHubAuthTools.ps1"),
    [Text.Encoding]::UTF8)
if ($authToolsText -match '"ls-remote"' -or
    $authToolsText -notmatch '"push", "--dry-run"') {
    throw "Auth diagnostic does not use a non-mutating Git write probe."
}

[ordered]@{
    event = "github_auth_tools_test"
    status = "ok"
    ghCases = $ghCases.Count
    gitCases = $gitCases.Count
    liveRemoteProbe = $false
    writeProbe = "git_push_dry_run"
    rawProbeMaterialEmitted = $false
} | ConvertTo-Json -Compress | Write-Host
