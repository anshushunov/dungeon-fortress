[CmdletBinding()]
param(
    [string]$GodotPath,
    [ValidateSet("baseline", "neglected")]
    [string]$Fixture = "baseline",
    [string]$ScreenshotPath,
    [ValidateRange(0, 1800)]
    [int]$ScreenshotTicks = 180,
    [int]$SelectCreature = -1,
    [switch]$DemoControls,
    [switch]$VisibleSmoke
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$projectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$projectFile = Join-Path $projectPath "DungeonFortress.Game.csproj"
$resolvedScreenshotPath = if ([string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $null
} else {
    Resolve-RepositoryArtifactPath -RepositoryRoot $repoRoot -RelativePath $ScreenshotPath
}

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

Initialize-GodotRuntimeEnvironment -RepositoryRoot $repoRoot

$arguments = @(
    "--path", $projectPath,
    "--",
    "--fixture", $Fixture
)
if ($VisibleSmoke) {
    $arguments += "--visible-smoke"
}
if ($null -ne $resolvedScreenshotPath) {
    $arguments += "--screenshot", $resolvedScreenshotPath, "--screenshot-ticks", $ScreenshotTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($SelectCreature -ge 0) {
    $arguments += "--select-creature", $SelectCreature.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($DemoControls) {
    $arguments += "--demo-controls"
}

if ($VisibleSmoke -and -not [string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    throw "-VisibleSmoke and -ScreenshotPath are separate deterministic run modes."
}

$expectedEvent = if ($VisibleSmoke) {
    "godot_visible_smoke"
} elseif ($null -ne $resolvedScreenshotPath) {
    "godot_graybox_screenshot"
} else {
    $null
}
try {
    $result = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments $arguments `
        -ExpectedSuccessEvent $expectedEvent
    exit $result.ExitCode
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
