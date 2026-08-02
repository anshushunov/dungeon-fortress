[CmdletBinding()]
param(
    [string]$GodotPath,
    # Issue #184: every entry point that starts the engine picks the temporary
    # directory the same way, because that directory decides where the Godot
    # runtime profile and its shader cache end up.
    [string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "HudVerification.ps1")
. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))

$temporaryRootSelection = Resolve-VerificationTemporaryRoot -ExplicitPath $TemporaryRoot
$env:TEMP = ConvertTo-NormalizedRootPath -Path $temporaryRootSelection.Path
$env:TMP = $env:TEMP
$gameProjectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$gameProjectFile = Join-Path $gameProjectPath "DungeonFortress.Game.csproj"

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

& dotnet restore $gameProjectFile
if ($LASTEXITCODE -ne 0) {
    throw "Godot project restore failed with exit code $LASTEXITCODE."
}

& dotnet build $gameProjectFile --configuration Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Godot project build failed with exit code $LASTEXITCODE."
}

Initialize-GodotRuntimeEnvironment -RepositoryRoot $repoRoot

$written = @()
foreach ($frame in Get-GoldenUiFrames) {
    $capture = Invoke-GoldenUiCapture `
        -GodotPath $godot `
        -ProjectPath $gameProjectPath `
        -Frame $frame
    $document = ConvertTo-GoldenUiDocument -Frame $frame -Capture $capture
    $path = Get-GoldenUiPath -RepositoryRoot $repoRoot -Frame $frame
    Write-GoldenUiDocument -Path $path -Document $document
    $written += $path
}

[ordered]@{
    event  = "golden_ui_update"
    status = "ok"
    frames = @($written | ForEach-Object { [IO.Path]::GetFileName($_) })
} | ConvertTo-Json -Compress | Write-Host

Write-Host "Review the diff before committing: these files are the reference the verification compares against."
