Set-StrictMode -Version Latest

function Test-CodexSandboxEnvironment {
    [CmdletBinding()]
    [OutputType([bool])]
    param()

    return (
        -not [string]::IsNullOrWhiteSpace(
            [Environment]::GetEnvironmentVariable("CODEX_PERMISSION_PROFILE")) -or
        -not [string]::IsNullOrWhiteSpace(
            [Environment]::GetEnvironmentVariable("CODEX_SANDBOX_NETWORK_DISABLED"))
    )
}

function Get-GhAuthState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [AllowEmptyString()]
        [string]$Output
    )

    if ($ExitCode -eq 0) {
        return "authenticated"
    }
    if ($Output -match '(?i)invalid token|token.*invalid|failed to log in') {
        return "invalid"
    }
    if ($Output -match '(?i)not logged|no accounts|authentication required') {
        return "missing"
    }
    if ($Output -match '(?i)could not resolve|timed out|connection|network') {
        return "network_unavailable"
    }
    return "error"
}

function Get-GitRemoteAuthState {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [int]$ExitCode,

        [AllowEmptyString()]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [bool]$InCodexSandbox
    )

    if ($ExitCode -eq 0) {
        return "authenticated"
    }
    if ($Output -match '(?i)SEC_E_NO_CREDENTIALS|no credentials are available') {
        if ($InCodexSandbox) {
            return "sandbox_credential_unavailable"
        }
        return "credential_unavailable"
    }
    if ($Output -match '(?i)terminal prompts disabled|could not read Username') {
        return "credential_unavailable"
    }
    if ($Output -match '(?i)authentication failed|invalid username or password|403') {
        return "rejected"
    }
    if ($Output -match '(?i)could not resolve|timed out|connection|network') {
        return "network_unavailable"
    }
    return "error"
}

function Invoke-AuthProbe {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    # The caller receives raw text only for in-memory classification. It must
    # never put this object into structured output: auth tools may include a
    # redacted token fragment or a credential-bearing remote URL.
    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

function Test-GitHubAuthReady {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GhState,

        [Parameter(Mandatory = $true)]
        [string]$GitWriteState,

        [Parameter(Mandatory = $true)]
        [bool]$CredentialHelperConfigured,

        [Parameter(Mandatory = $true)]
        [bool]$EmbeddedCredential
    )

    return (
        $GhState -eq "authenticated" -and
        $GitWriteState -eq "authenticated" -and
        $CredentialHelperConfigured -and
        -not $EmbeddedCredential
    )
}

function Get-GitHubAuthReport {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [switch]$SkipRemoteProbe
    )

    $inCodexSandbox = Test-CodexSandboxEnvironment
    $ghCommand = Get-Command gh -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $gitCommand = Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1

    $ghState = if ($null -eq $ghCommand) {
        "missing_cli"
    }
    else {
        $probe = Invoke-AuthProbe `
            -FilePath $ghCommand.Source `
            -Arguments @("auth", "status", "--hostname", "github.com")
        Get-GhAuthState -ExitCode $probe.ExitCode -Output $probe.Text
    }

    $helperConfigured = $false
    $helperCount = 0
    $remoteConfigured = $false
    $remoteProtocol = $null
    $remoteHost = $null
    $embeddedCredential = $false
    $gitState = if ($null -eq $gitCommand) { "missing_cli" } else { "not_checked" }

    if ($null -ne $gitCommand) {
        $helperProbe = Invoke-AuthProbe `
            -FilePath $gitCommand.Source `
            -Arguments @(
                "-c", ("safe.directory=" + $RepositoryRoot),
                "-C", $RepositoryRoot,
                "config", "--get-all", "credential.helper"
            )
        if ($helperProbe.ExitCode -eq 0) {
            $helperLines = @($helperProbe.Text -split "\r?\n" | Where-Object {
                -not [string]::IsNullOrWhiteSpace($_)
            })
            $helperCount = $helperLines.Count
            $helperConfigured = $helperCount -gt 0
        }

        $remoteProbe = Invoke-AuthProbe `
            -FilePath $gitCommand.Source `
            -Arguments @(
                "-c", ("safe.directory=" + $RepositoryRoot),
                "-C", $RepositoryRoot,
                "remote", "get-url", "origin"
            )
        if ($remoteProbe.ExitCode -eq 0) {
            $remoteUrl = $remoteProbe.Text.Trim()
            $remoteConfigured = -not [string]::IsNullOrWhiteSpace($remoteUrl)
            $embeddedCredential = $remoteUrl -match '^[a-z][a-z0-9+.-]*://[^/@]+@'
            if ($remoteUrl -match '^https?://') {
                $remoteProtocol = "https"
                try {
                    $uri = [Uri]$remoteUrl
                    $remoteHost = $uri.Host
                    $embeddedCredential = $embeddedCredential -or
                        -not [string]::IsNullOrWhiteSpace($uri.UserInfo)
                }
                catch {
                    $remoteHost = "invalid"
                }
            }
            elseif ($remoteUrl -match '^[^@]+@([^:]+):') {
                $remoteProtocol = "ssh"
                $remoteHost = $Matches[1]
            }
            else {
                $remoteProtocol = "other"
            }
        }

        if (-not $SkipRemoteProbe -and $remoteConfigured) {
            $oldPrompt = [Environment]::GetEnvironmentVariable("GIT_TERMINAL_PROMPT")
            $oldInteractive = [Environment]::GetEnvironmentVariable("GCM_INTERACTIVE")
            try {
                [Environment]::SetEnvironmentVariable("GIT_TERMINAL_PROMPT", "0")
                [Environment]::SetEnvironmentVariable("GCM_INTERACTIVE", "Never")
                $headProbe = Invoke-AuthProbe `
                    -FilePath $gitCommand.Source `
                    -Arguments @(
                        "-c", ("safe.directory=" + $RepositoryRoot),
                        "-C", $RepositoryRoot,
                        "rev-parse", "--short=12", "HEAD"
                    )
                if ($headProbe.ExitCode -ne 0 -or
                    $headProbe.Text.Trim() -cnotmatch '^[0-9a-f]{12}$') {
                    throw "Cannot resolve HEAD for Git write-authentication probe."
                }
                $probeRef = "refs/heads/codex-auth-probe-" + $headProbe.Text.Trim()
                $gitProbe = Invoke-AuthProbe `
                    -FilePath $gitCommand.Source `
                    -Arguments @(
                        "-c", ("safe.directory=" + $RepositoryRoot),
                        "-C", $RepositoryRoot,
                        "push", "--dry-run", "--porcelain",
                        "origin", ("HEAD:" + $probeRef)
                    )
            }
            finally {
                [Environment]::SetEnvironmentVariable("GIT_TERMINAL_PROMPT", $oldPrompt)
                [Environment]::SetEnvironmentVariable("GCM_INTERACTIVE", $oldInteractive)
            }
            $gitState = Get-GitRemoteAuthState `
                -ExitCode $gitProbe.ExitCode `
                -Output $gitProbe.Text `
                -InCodexSandbox $inCodexSandbox
        }
        elseif (-not $remoteConfigured) {
            $gitState = "missing_remote"
        }
    }

    $remediation = @()
    if ($ghState -in @("missing", "invalid", "error")) {
        $remediation += "Run outside the sandbox: gh auth login -h github.com"
        $remediation += "Then run: gh auth setup-git"
    }
    elseif ($ghState -eq "missing_cli") {
        $remediation += "Install GitHub CLI, then run gh auth login -h github.com."
    }
    if (-not $helperConfigured -and $null -ne $gitCommand) {
        $remediation += "Configure Git credential access with: gh auth setup-git"
    }
    if ($embeddedCredential) {
        $remediation += "Remove credentials embedded in the origin URL; use a credential helper."
    }
    if ($gitState -eq "sandbox_credential_unavailable") {
        $remediation += (
            "Windows credentials are not mounted in this Codex sandbox. " +
            "Use the GitHub connector for API mutations and an approved/elevated git push; " +
            "do not copy a token into the repository or command line."
        )
    }
    elseif ($gitState -in @("credential_unavailable", "rejected")) {
        $remediation += "Refresh GitHub CLI login outside the sandbox, then run gh auth setup-git."
    }
    elseif ($gitState -eq "missing_remote") {
        $remediation += "Configure the repository origin remote before probing Git authentication."
    }
    elseif ($gitState -eq "not_checked") {
        $remediation += "Run without -SkipRemoteProbe to prove Git write authentication."
    }

    $ready = Test-GitHubAuthReady `
        -GhState $ghState `
        -GitWriteState $gitState `
        -CredentialHelperConfigured $helperConfigured `
        -EmbeddedCredential $embeddedCredential
    return [pscustomobject][ordered]@{
        event = "github_auth_diagnostic"
        status = if ($ready) { "ok" } else { "action_required" }
        inCodexSandbox = $inCodexSandbox
        gh = [ordered]@{
            cliAvailable = $null -ne $ghCommand
            authState = $ghState
        }
        git = [ordered]@{
            cliAvailable = $null -ne $gitCommand
            originConfigured = $remoteConfigured
            originProtocol = $remoteProtocol
            originHost = $remoteHost
            embeddedCredential = $embeddedCredential
            credentialHelperConfigured = $helperConfigured
            credentialHelperCount = $helperCount
            writeAuthState = $gitState
            writeProbe = "git_push_dry_run"
        }
        ready = $ready
        remediation = @($remediation | Select-Object -Unique)
    }
}
