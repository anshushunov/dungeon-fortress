[CmdletBinding()]
param(
    [string]$GodotPath,
    [string]$OutputRoot = "evidence",
    [ValidateSet(100, 150, 200)]
    [int[]]$Scales = @(100, 150, 200)
)

# Issue #142 scale spike capture.
#
# NOT scripts/capture-evidence.ps1: that pipeline always launches the
# production DungeonFortress.Game project through run-game.ps1's
# fixture/tile-size/camera-zoom contract, and this spike is a separate,
# throwaway GDScript project (spikes/142-scale-spike) that must not touch
# src/DungeonFortress.Game at all — see the Issue #142 brief's non-goals.
#
# This script follows the same discipline instead: an explicit, reproducible
# Godot invocation per capture, checked the same way GodotTools.ps1 checks
# every other Godot run in this repository (no ERROR: lines, exit code 0, an
# expected structured success event) via the shared Invoke-GodotChecked
# helper. Godot resolution reuses Resolve-GodotExecutable/Assert-GodotVersion
# from scripts/GodotTools.ps1 (read, not modified) so this spike follows the
# same -GodotPath / $env:GODOT4_CONSOLE / PATH order documented in
# docs/engineering/ENVIRONMENT_SETUP.md.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $repoRoot "scripts\GodotTools.ps1")

$spikeProjectPath = $PSScriptRoot
$resolvedOutputRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null

function Get-SpikeRelativePath {
    param(
        [Parameter(Mandatory = $true)] [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)] [string]$Path
    )
    $normalizedRoot = $RepositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    return $Path.Substring($normalizedRoot.Length).Replace('\', '/')
}

$godot = Resolve-GodotExecutable -ExplicitPath $GodotPath
$version = Assert-GodotVersion -GodotPath $godot
Write-Host "Using Godot $version at $godot"

$results = @()
foreach ($scale in $Scales) {
    $fileName = "142-scale-spike-$scale.png"
    $screenshotPath = Join-Path $resolvedOutputRoot $fileName
    $arguments = @(
        "--path", $spikeProjectPath,
        "--resolution", "1600x900",
        "--",
        "--scale", $scale.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--screenshot", $screenshotPath
    )
    Write-Host "Capturing scale spike at $scale%..."
    $result = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments $arguments `
        -ExpectedSuccessEvent "scale_spike_capture"

    $results += [pscustomobject]@{
        scalePercent = $scale
        path = Get-SpikeRelativePath -RepositoryRoot $repoRoot -Path $screenshotPath
        exitCode = $result.ExitCode
    }
}

[ordered]@{
    event = "scale_spike_evidence"
    status = "ok"
    captures = $results
} | ConvertTo-Json -Compress -Depth 5 | Write-Host
