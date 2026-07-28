[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Guards the launch path of the project-owned domain MCP server (Issue #38).
# A client session must never execute the Release build output directly: that
# directory is the target of "dotnet build DungeonFortress.sln", and a live
# session would fail the build with MSB3027 before the first test runs.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$claudeConfigPath = Join-Path $repoRoot ".mcp.json"
$codexConfigPath = Join-Path $repoRoot ".codex\config.toml"
$launcherPath = Join-Path $repoRoot "scripts\domain-mcp-server.cmd"
$launcherRelative = "scripts\domain-mcp-server.cmd"
$buildOutputFragment = "bin\Release\net8.0"

if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw "Domain MCP launcher '$launcherRelative' is missing."
}

$claudeConfig = Get-Content -Raw -Encoding utf8 $claudeConfigPath | ConvertFrom-Json
$codexConfig = Get-Content -Raw -Encoding utf8 $codexConfigPath
$launcherText = Get-Content -Raw -Encoding utf8 $launcherPath
$launcherLines = Get-Content -Encoding utf8 $launcherPath

$domainServer = $claudeConfig.mcpServers.'dungeon-fortress-domain'
if ($null -eq $domainServer) {
    throw "Claude project config does not declare the domain MCP server."
}
if ($domainServer.type -ne "stdio") {
    throw "Domain MCP server must stay a stdio process."
}
if ($domainServer.command -ne "cmd") {
    throw "Domain MCP server must be started through cmd so the client pipes reach the server."
}

$claudeArguments = @($domainServer.args)
if ($claudeArguments.Count -lt 2 -or $claudeArguments[0] -ne "/c") {
    throw "Claude domain MCP arguments must invoke the launcher through 'cmd /c'."
}
if (-not $claudeArguments[-1].EndsWith($launcherRelative)) {
    throw "Claude domain MCP arguments must end with '$launcherRelative'."
}

# The repository is shared, so no configuration may carry a machine path.
foreach ($argument in $claudeArguments) {
    if ($argument -match '^[A-Za-z]:[\\/]' -or $argument.StartsWith("\\")) {
        throw "Claude domain MCP argument '$argument' is a machine-specific absolute path."
    }
}

$codexSection = [regex]::Match(
    $codexConfig,
    '(?ms)^\[mcp_servers\.dungeon_fortress_domain\](.*?)(?=^\[|\z)')
if (-not $codexSection.Success) {
    throw "Codex project config does not declare the domain MCP server."
}
$codexSectionText = $codexSection.Groups[1].Value
if ($codexSectionText -notmatch '(?m)^command\s*=\s*"cmd"$') {
    throw "Codex domain MCP server must be started through cmd."
}
if ($codexSectionText -notmatch '(?m)^args\s*=\s*\["/c",\s*"scripts\\\\domain-mcp-server\.cmd"\]$') {
    throw "Codex domain MCP arguments must invoke '$launcherRelative' through 'cmd /c'."
}
if ($codexSectionText -match '[A-Za-z]:\\\\' -or $codexSectionText -match '[A-Za-z]:/') {
    throw "Codex domain MCP section contains a machine-specific absolute path."
}

# Both clients must reach the server through the launcher, never through the
# build output or through "dotnet run" against the tool project.
foreach ($configuration in @(
        @{ Name = ".mcp.json"; Text = (Get-Content -Raw -Encoding utf8 $claudeConfigPath) },
        @{ Name = ".codex/config.toml"; Text = $codexSectionText })) {
    if ($configuration.Text.Contains($buildOutputFragment) -or
        $configuration.Text.Contains("bin/Release")) {
        throw "$($configuration.Name) still points a client session at the Release build output."
    }
}
if ($codexSectionText -match '(?m)^command\s*=\s*"dotnet"$') {
    throw ".codex/config.toml still starts the domain MCP server with dotnet run."
}

# The launcher itself must copy the build output and run the copy.
foreach ($fragment in @(
        '.artifacts\domain-mcp-sessions',
        'tools\DungeonFortress.DomainMcp\bin\Release\net8.0',
        'robocopy "%BUILD_OUTPUT%" "%SESSION_ROOT%"',
        '"%SESSION_ROOT%\%HOST_NAME%" --root "%REPO_ROOT%"',
        'rd /s /q "%SESSION_ROOT%"',
        ':remove_if_dead')) {
    if (-not $launcherText.Contains($fragment)) {
        throw "Domain MCP launcher is missing required fragment '$fragment'."
    }
}
if ($launcherText.Contains('"%BUILD_OUTPUT%\%HOST_NAME%" --root')) {
    throw "Domain MCP launcher executes the build output instead of a session copy."
}

# The client speaks JSON-RPC over the launcher's stdout, so nothing may be
# written there: every diagnostic has to be redirected to stderr.
$echoLines = @($launcherLines | Where-Object { $_ -match '(?i)\becho\b' })
foreach ($line in $echoLines) {
    $trimmed = $line.Trim()
    if ($trimmed -eq "@echo off" -or $trimmed.StartsWith("rem ")) {
        continue
    }
    if (-not $trimmed.StartsWith(">&2 echo")) {
        throw "Domain MCP launcher writes to stdout: '$trimmed'."
    }
}

[ordered]@{
    event = "domain_mcp_config_test"
    status = "ok"
    launcher = $launcherRelative
    clients = @("claude", "codex")
    sessionCopyRoot = ".artifacts/domain-mcp-sessions"
    executesBuildOutput = $false
    absolutePathsInConfig = $false
} | ConvertTo-Json -Compress
