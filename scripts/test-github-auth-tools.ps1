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

$reportArguments = @{
    InCodexSandbox = $true
    GhCliAvailable = $true
    GhState = "authenticated"
    GitCliAvailable = $true
    OriginConfigured = $false
    OriginProtocol = $null
    OriginHost = $null
    EmbeddedCredential = $false
    CredentialHelperConfigured = $true
    CredentialHelperCount = 1
    GitWriteState = "missing_remote"
}
$missingOriginReport = New-GitHubAuthReport @reportArguments
$reportArguments.OriginConfigured = $true
$reportArguments.OriginProtocol = "other"
$reportArguments.OriginHost = $null
$reportArguments.GitWriteState = "not_checked"
$otherOriginReport = New-GitHubAuthReport @reportArguments
$serialized = @($missingOriginReport, $otherOriginReport) |
    ConvertTo-Json -Depth 8 -Compress
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
if ($missingOriginReport.git.writeAuthState -cne "missing_remote" -or
    $missingOriginReport.git.originConfigured -or
    $missingOriginReport.status -cne "action_required") {
    throw "Pure auth report mishandled a repository with no origin."
}
if ($otherOriginReport.git.writeAuthState -cne "not_checked" -or
    -not $otherOriginReport.git.originConfigured -or
    $otherOriginReport.git.originProtocol -cne "other" -or
    $otherOriginReport.status -cne "action_required") {
    throw "Pure auth report mishandled a non-GitHub origin."
}
$authToolsText = [IO.File]::ReadAllText(
    (Join-Path $repoRoot "scripts\GitHubAuthTools.ps1"),
    [Text.Encoding]::UTF8)
if ($authToolsText -match '"ls-remote"' -or
    $authToolsText -notmatch '"push", "--dry-run"') {
    throw "Auth diagnostic does not use a non-mutating Git write probe."
}

$oldPermissionProfile = [Environment]::GetEnvironmentVariable("CODEX_PERMISSION_PROFILE")
try {
    [Environment]::SetEnvironmentVariable("CODEX_PERMISSION_PROFILE", "test")
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $setupOutput = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File (Join-Path $repoRoot "scripts\github-auth.ps1") `
            -Action Setup 2>&1)
        $setupExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}
finally {
    [Environment]::SetEnvironmentVariable("CODEX_PERMISSION_PROFILE", $oldPermissionProfile)
}
$setupText = ($setupOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
if ($setupExitCode -ne 1 -or
    $setupText -cnotmatch "GitHub login must run in the owner's normal PowerShell") {
    throw "github-auth Setup did not reject execution inside the Codex sandbox."
}

[ordered]@{
    event = "github_auth_tools_test"
    status = "ok"
    ghCases = $ghCases.Count
    gitCases = $gitCases.Count
    liveRemoteProbe = $false
    externalAuthCommandsRun = $false
    missingOriginCovered = $true
    nonGitHubOriginCovered = $true
    sandboxSetupRejected = $true
    writeProbe = "git_push_dry_run"
    rawProbeMaterialEmitted = $false
} | ConvertTo-Json -Compress | Write-Host
