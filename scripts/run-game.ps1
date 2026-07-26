[CmdletBinding()]
param(
    [string]$GodotPath,
    [UInt64]$Seed = 424242,
    [switch]$VisibleSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$projectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$projectFile = Join-Path $projectPath "DungeonFortress.Game.csproj"

$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot "dotnet-home"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

$godot = Resolve-GodotExecutable -ExplicitPath $GodotPath
$null = Assert-GodotVersion -GodotPath $godot
$godotNuGetSource = Get-GodotNuGetSource -GodotPath $godot
Initialize-GodotNuGetEnvironment `
    -ProfileRoot (Join-Path $artifactsRoot "tool-profile") `
    -GodotNuGetSource $godotNuGetSource

& dotnet restore $projectFile
if ($LASTEXITCODE -ne 0) {
    throw "Godot project restore failed with exit code $LASTEXITCODE."
}

& dotnet build $projectFile --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Godot project build failed with exit code $LASTEXITCODE."
}

$arguments = @(
    "--path", $projectPath,
    "--",
    "--seed", $Seed.ToString([Globalization.CultureInfo]::InvariantCulture)
)
if ($VisibleSmoke) {
    $arguments += "--visible-smoke"
}

& $godot @arguments
exit $LASTEXITCODE
