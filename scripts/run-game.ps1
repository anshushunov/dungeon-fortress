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
    [ValidateRange(32, 48)]
    [int]$TileSize = 40,
    # No default either, and for the same reason as the two below: 0.75 was
    # chosen for a 1280x720 window, and on the owner's maximized one it left a
    # 1120x640 map drawn at 1:1 in the middle of a viewport twice its size
    # (Issue #86). Omitted, the game picks the largest declared level at which
    # the whole map still fits the world viewport it ended up with; supplied, it
    # is an override the automatic rule never touches, including after a resize.
    [ValidateScript({ $_ -in @(0.5, 0.75, 1.0, 1.5, 2.0) })]
    [Nullable[double]]$CameraZoom,
    [ValidatePattern("^-?\d+(\.\d+)?,-?\d+(\.\d+)?$")]
    [string]$CameraPosition = "560,320",
    # FrameSize and UiScale have no default on purpose. 1280x720 at scale 1 is
    # the rectangle the HUD is authored against, not a description of anyone's
    # monitor, and using it as a launch default opened a small window with 8-15 px
    # text on the owner's screen twice (Issues #86 and #100). Omitted, both are
    # derived from the screen by the game; see "Startup frame and UI scale" in
    # Main.cs. Supplied, they behave exactly as before — which is what keeps
    # capture-evidence.ps1 and verify.ps1 machine-independent, since both always
    # pass every frame parameter explicitly.
    [ValidateRange(0.75, 2.0)]
    [Nullable[double]]$UiScale,
    [Alias("WindowSize")]
    [ValidatePattern("^\d{3,5}x\d{3,5}$")]
    [string]$FrameSize
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$projectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$projectFile = Join-Path $projectPath "DungeonFortress.Game.csproj"
$hasFrameSize = -not [string]::IsNullOrWhiteSpace($FrameSize)
$hasUiScale = $null -ne $UiScale
$hasCameraZoom = $null -ne $CameraZoom
$minimumLogicalWidth = 1024
$minimumLogicalHeight = 720
# Only a declared frame can be judged here. A frame derived from the screen is
# not known until the engine has asked the display, so the same rule is enforced
# there — see AssertLogicalFrameFits in Main.cs. This check survives because it
# rejects an impossible pair before restore and build, which is a minute of
# waiting, not because it is the only place the rule lives.
if ($hasFrameSize) {
    $frameParts = $FrameSize -split "x", 2
    $frameWidth = [int]::Parse($frameParts[0], [Globalization.CultureInfo]::InvariantCulture)
    $frameHeight = [int]::Parse($frameParts[1], [Globalization.CultureInfo]::InvariantCulture)
    # An omitted UiScale never scales a declared frame below scale 1, so scale 1
    # is the pair to judge that case by.
    $effectiveUiScale = if ($hasUiScale) { [double]$UiScale } else { 1.0 }
    if (($frameWidth / $effectiveUiScale) -lt $minimumLogicalWidth -or
        ($frameHeight / $effectiveUiScale) -lt $minimumLogicalHeight) {
        throw (
            "FrameSize $FrameSize at UiScale " +
            $effectiveUiScale.ToString([Globalization.CultureInfo]::InvariantCulture) +
            " is too small: the HUD requires at least ${minimumLogicalWidth}x" +
            "${minimumLogicalHeight} logical pixels. Increase FrameSize or reduce UiScale."
        )
    }
}

# A capture has to declare every pixel-affecting value; ViewLaunchOptions.Parse
# refuses one that does not, and that refusal is the rule. This says the same
# thing before restore and build rather than after them, which is the same trade
# the frame check above makes.
if (-not [string]::IsNullOrWhiteSpace($ScreenshotPath) -and -not $hasCameraZoom) {
    throw (
        "A screenshot capture has to name -CameraZoom, because a frame nobody " +
        "declared a zoom for would inherit whichever zoom the automatic rule " +
        "picked for this window and stop being reproducible."
    )
}

$resolvedScreenshotPath = if ([string]::IsNullOrWhiteSpace($ScreenshotPath)) {
    $null
} else {
    Resolve-RepositoryArtifactPath -RepositoryRoot $repoRoot -RelativePath $ScreenshotPath
}

$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot "dotnet-home"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
# Изолированный DOTNET_CLI_HOME заставляет .NET CLI дописать
# <CLI_HOME>\.dotnet\tools в пользовательский PATH при первом запуске, и
# DOTNET_SKIP_FIRST_TIME_EXPERIENCE этого не предотвращает — измерено 2026-08-02.
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = "0"
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
if ($hasFrameSize) {
    $arguments += "--resolution", $FrameSize
}
$arguments += @(
    "--", "--fixture", $Fixture,
    "--tile-size", $TileSize.ToString([Globalization.CultureInfo]::InvariantCulture),
    "--camera-position", $CameraPosition
)
# Absent, not empty: the game distinguishes "no frame declared" from a declared
# one, and an empty --ui-scale would parse as a value.
if ($hasCameraZoom) {
    $arguments += "--camera-zoom", ([double]$CameraZoom).ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if ($hasUiScale) {
    $arguments += "--ui-scale", ([double]$UiScale).ToString(
        [Globalization.CultureInfo]::InvariantCulture)
}
if ($hasFrameSize) {
    $arguments += "--frame-size", $FrameSize
}
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
