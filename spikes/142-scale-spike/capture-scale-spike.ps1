[CmdletBinding()]
param(
    [string]$GodotPath,
    # Relative to repository .artifacts/, same convention as
    # scripts/capture-evidence.ps1's -OutputRoot (see
    # docs/engineering/EVIDENCE_WORKFLOW.md): a bare subdirectory name, not a
    # path that already contains ".artifacts". Resolve-RepositoryArtifactPath
    # rejects anything that would land outside it.
    [string]$OutputRoot = "142-scale-spike",
    [ValidateSet(100, 150, 200)]
    [int[]]$Scales = @(100, 150, 200)
)

# Issue #142 scale spike capture. Opens a real (non-headless) window: it
# reads pixels back with get_viewport().get_texture().get_image(), which
# needs a live render target the way scripts/run-game.ps1's own screenshot
# captures do. In the agent shell -GodotPath is required, the same as every
# other Godot invocation in this repository — see "Требование к запуску
# проверок агентом" in docs/engineering/ENVIRONMENT_SETUP.md.
#
# NOT scripts/capture-evidence.ps1: that pipeline always launches the
# production DungeonFortress.Game project through run-game.ps1's
# fixture/tile-size/camera-zoom contract, and this spike is a separate
# GDScript project (spikes/142-scale-spike) that must not touch
# src/DungeonFortress.Game at all — see the Issue #142 brief's non-goals.
#
# It follows the same output-boundary and correctness discipline instead:
# -OutputRoot resolves under repository .artifacts/ through the same
# Resolve-RepositoryArtifactPath guard capture-evidence.ps1 uses (nothing
# PNG-shaped is committed — AGENTS.md forbids large derived files in Git and
# docs/engineering/EVIDENCE_WORKFLOW.md routes captures to .artifacts/ for
# exactly that reason), and every invocation is checked the same way
# GodotTools.ps1 checks every other Godot run (no ERROR: lines, exit code 0,
# an expected structured success event) via the shared Invoke-GodotChecked
# helper. Godot resolution reuses Resolve-GodotExecutable/Assert-GodotVersion
# from scripts/GodotTools.ps1 (read, not modified) so this spike follows the
# same -GodotPath / $env:GODOT4_CONSOLE / PATH order documented in
# docs/engineering/ENVIRONMENT_SETUP.md.
#
# A provenance manifest is committed instead: evidence/142-scale-spike.json
# records the Godot version, the exact command and the SHA-256 of each PNG
# this script produced, the same role manifest.json plays for
# capture-evidence.ps1 output.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
. (Join-Path $repoRoot "scripts\GodotTools.ps1")

$spikeProjectPath = $PSScriptRoot

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
    $relativeScreenshotPath = Join-Path $OutputRoot $fileName
    $screenshotPath = Resolve-RepositoryArtifactPath `
        -RepositoryRoot $repoRoot `
        -RelativePath $relativeScreenshotPath `
        -ParameterName "OutputRoot"
    New-Item -ItemType Directory -Force -Path (Split-Path -Path $screenshotPath -Parent) | Out-Null

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
        sha256 = (Get-FileHash -LiteralPath $screenshotPath -Algorithm SHA256).Hash.ToLowerInvariant()
        exitCode = $result.ExitCode
    }
}

[ordered]@{
    event = "scale_spike_evidence"
    status = "ok"
    godotVersion = $version
    captures = $results
} | ConvertTo-Json -Compress -Depth 5 | Write-Host
