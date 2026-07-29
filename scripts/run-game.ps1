[CmdletBinding()]
param(
    [string]$GodotPath,
    [ValidateSet("baseline", "prepared", "neglected")]
    [string]$Fixture = "baseline",
    [string]$ScreenshotPath,
    # Upper bound = T.session_ticks, the fuse a party can never run past. A
    # PowerShell attribute argument has to be a literal, so this number is a copy
    # and not a reference: the source of truth is PrototypeTuning.SessionTicks in
    # src\DungeonFortress.Simulation\PrototypeTuning.cs, and the two must be
    # changed together. A party normally ends by itself well before the fuse —
    # around tick 2400 with today's four waves — so this bound exists to catch a
    # typo, not to describe the length of a session.
    [ValidateRange(0, 2700)]
    [int]$ScreenshotTicks = 180,
    [int]$SelectCreature = -1,
    [ValidatePattern("^\d{1,2},\d{1,2}$")]
    [string]$SelectCell,
    [switch]$DemoControls,
    [switch]$DemoDig,
    [switch]$DemoStone,
    [switch]$DemoBuild,
    [switch]$VisibleSmoke,
    [ValidatePattern("^\d{3,5}x\d{3,5}$")]
    [string]$WindowSize
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
Import-GodotProjectAssets -GodotPath $godot -ProjectPath $projectPath

$arguments = @("--path", $projectPath)
if (-not [string]::IsNullOrWhiteSpace($WindowSize)) {
    $arguments += "--resolution", $WindowSize
}
$arguments += "--", "--fixture", $Fixture
if ($VisibleSmoke) {
    $arguments += "--visible-smoke"
}
if ($null -ne $resolvedScreenshotPath) {
    $arguments += "--screenshot", $resolvedScreenshotPath, "--screenshot-ticks", $ScreenshotTicks.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($SelectCreature -ge 0) {
    $arguments += "--select-creature", $SelectCreature.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if (-not [string]::IsNullOrWhiteSpace($SelectCell)) {
    $arguments += "--select-cell", $SelectCell
}
if ($DemoControls) {
    $arguments += "--demo-controls"
}
if ($DemoDig) {
    $arguments += "--demo-dig"
}
if ($DemoStone) {
    $arguments += "--demo-stone"
}
if ($DemoBuild) {
    $arguments += "--demo-build"
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
    if ($expectedEvent -in @("godot_visible_smoke", "godot_graybox_screenshot")) {
        Assert-GoblinSpriteDiagnostics -OutputLines $result.Output -EventName $expectedEvent
    }
    exit $result.ExitCode
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
