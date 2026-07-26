[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $repoRoot "scripts\ivan-mcp.ps1"
$gitignorePath = Join-Path $repoRoot ".gitignore"
$projectPath = Join-Path $repoRoot "src\DungeonFortress.Game\DungeonFortress.Game.csproj"
$generatedProps = Join-Path $repoRoot "src\DungeonFortress.Game\DungeonFortress.Game.IvanMcp.props"
$addonMarker = Join-Path $repoRoot "src\DungeonFortress.Game\addons\godot_mcp\.dungeon-fortress-ivan-mcp.json"
$codexConfigPath = Join-Path $repoRoot ".codex\config.toml"
$claudeConfigPath = Join-Path $repoRoot ".mcp.json"
$lockPath = Join-Path $repoRoot "config\ivan-mcp\packages.lock.json"
$adrPath = Join-Path $repoRoot "docs\decisions\0004-dev-only-ivan-mcp.md"

$scriptText = Get-Content -Raw -Encoding utf8 $scriptPath
$gitignoreText = Get-Content -Raw -Encoding utf8 $gitignorePath
$projectText = Get-Content -Raw -Encoding utf8 $projectPath
$codexConfig = Get-Content -Raw -Encoding utf8 $codexConfigPath
$claudeConfig = Get-Content -Raw -Encoding utf8 $claudeConfigPath | ConvertFrom-Json
$adrText = Get-Content -Raw -Encoding utf8 $adrPath

$requiredScriptFragments = @(
    '271b74e58631a7c07c451b205f5808ce8edc42e70f3f0e7b28e5422b82e30e03',
    'e0fe86cbebed4f376737086b781445c53f4bf5cf9111c923ba98da5c0bc4b69d',
    '--auth none --bind loopback',
    'Test-IvanServerIdentity',
    'Test-IvanEditorReady',
    'did not complete the Ivan handshake',
    'refusing to attach to an untracked server',
    'EnvironmentVariables.Clear()',
    'Initialize-IsolatedProcessEnvironment -StartInfo $startInfo',
    'EnvironmentVariables["APPDATA"]',
    'NUGET_PACKAGES = Join-Path $artifactRoot',
    'DOTNET_ROOT = $dotnetRoot',
    'UseShellExecute = $false',
    'startTimeUtcTicks',
    'was reused by another process',
    'GODOT_MCP_CONNECTION_MODE',
    'GODOT_MCP_DEV_CONTROL',
    'did not exit within 10 seconds',
    'trustedBroadToolSurface'
)
foreach ($fragment in $requiredScriptFragments) {
    if (-not $scriptText.Contains($fragment)) {
        throw "Ivan script is missing required fragment '$fragment'."
    }
}

if ($scriptText.Contains("ai-game.dev")) {
    throw "Ivan dev launcher must not reference the cloud host."
}
foreach ($ambientVariable in @("REDIS_URL", "ASPNETCORE_URLS", "GODOT_MCP_CLOUD_URL")) {
    if ($scriptText.Contains('$env:' + $ambientVariable)) {
        throw "Ivan server launcher must not inherit or forward '$ambientVariable'."
    }
}
if (-not $gitignoreText.Contains("src/DungeonFortress.Game/addons/godot_mcp/") -or
    -not $gitignoreText.Contains("DungeonFortress.Game.IvanMcp.props")) {
    throw "Ivan generated addon/props are not ignored."
}
if (-not $projectText.Contains("DungeonFortress.Game.IvanMcp.props") -or
    -not $projectText.Contains("Condition=`"Exists(") -or
    -not $projectText.Contains("'`$(Configuration)' == 'Debug'")) {
    throw "Game project does not restrict the dev-only Ivan props import to Debug."
}
if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
    throw "Committed Ivan dependency lock is missing."
}
$lock = Get-Content -Raw -Encoding utf8 $lockPath | ConvertFrom-Json
$framework = $lock.dependencies.'net8.0'
$mcpPlugin = $framework.'com.IvanMurzak.McpPlugin'
$reflector = $framework.'com.IvanMurzak.ReflectorNet'
if ($mcpPlugin.requested -ne "[7.3.0, 7.3.0]" -or
    $mcpPlugin.resolved -ne "7.3.0" -or
    $reflector.requested -ne "[5.3.2, 5.3.2]" -or
    $reflector.resolved -ne "5.3.2") {
    throw "Ivan direct NuGet dependencies are not exact-version locked."
}

$expectedUrl = "http://127.0.0.1:29541/mcp"
if (-not $codexConfig.Contains("url = `"$expectedUrl`"")) {
    throw "Codex editor MCP URL is not the expected loopback endpoint."
}
$claudeEditor = $claudeConfig.mcpServers.'dungeon-fortress-editor'
if ($null -eq $claudeEditor -or
    $claudeEditor.type -ne "http" -or
    $claudeEditor.url -ne $expectedUrl) {
    throw "Claude editor MCP config is not the expected loopback HTTP endpoint."
}
if ($adrText -notmatch "(?m)^- .+: Accepted$" -or
    $adrText -notmatch "filesystem-list" -or
    $adrText -notmatch "reflection") {
    throw "ADR 0004 does not explicitly accept and describe the Ivan security risk."
}

$addonExists = Test-Path -LiteralPath (Split-Path -Parent $addonMarker)
$markerExists = Test-Path -LiteralPath $addonMarker -PathType Leaf
$propsExists = Test-Path -LiteralPath $generatedProps -PathType Leaf
if ($addonExists -or $markerExists -or $propsExists) {
    if (-not ($addonExists -and $markerExists -and $propsExists)) {
        throw "Ivan local install is partial: addon, ownership marker, and generated props must appear together."
    }
}

[ordered]@{
    event = "ivan_mcp_config_test"
    status = "ok"
    mcpUrl = $expectedUrl
    addonInstalled = $addonExists
    devOnlyImport = $true
    cloudConfigured = $false
    trustedBroadToolSurfaceDocumented = $true
} | ConvertTo-Json -Compress
