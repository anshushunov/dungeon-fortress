[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GodotPath,
    # Issue #184: every entry point that starts the engine picks the temporary
    # directory the same way. Run from verify.ps1 this changes nothing, because
    # the parent has already put its choice in TEMP; run on its own it stops
    # this script from being the one that disagrees.
    [string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

$temporaryRootSelection = Resolve-VerificationTemporaryRoot -ExplicitPath $TemporaryRoot
$env:TEMP = ConvertTo-NormalizedRootPath -Path $temporaryRootSelection.Path
$env:TMP = $env:TEMP

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryRoot ("dungeon-fortress-sprite-import-" + [Guid]::NewGuid().ToString("N"))
$sourceAssets = Join-Path $repoRoot "src\DungeonFortress.Game\assets\generated\goblins"
# Six states on the v2 pack since Issue #77; the list mirrors
# DungeonFortress.Presentation.BodySprites.States.
$requiredStates = @("idle", "work", "combat", "windup", "flinch", "downed")

function Assert-UnderRoot {
    param([string]$Path, [string]$Root)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use path outside isolated sprite-import test root: '$resolvedPath'."
    }
}

try {
    New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
    Assert-UnderRoot -Path $testRoot -Root $temporaryRoot
    $testAssets = Join-Path $testRoot "assets\generated\goblins"
    New-Item -ItemType Directory -Force -Path $testAssets | Out-Null
    Get-ChildItem -LiteralPath $sourceAssets -Filter "goblin_*_v2.png" -File |
        Copy-Item -Destination $testAssets
    [IO.File]::WriteAllText((Join-Path $testRoot "project.godot"), @"
; Isolated import-only project: no scene or plugins are needed for PNG import.
config_version=5

[application]
config/name="Dungeon Fortress sprite import test"
"@, [Text.UTF8Encoding]::new($false))

    $importedRoot = Join-Path $testRoot ".godot\imported"
    if (Test-Path -LiteralPath $importedRoot) {
        throw "Fresh isolated sprite-import project unexpectedly already has an import cache."
    }

    Initialize-GodotRuntimeEnvironment -RepositoryRoot $repoRoot
    Import-GodotProjectAssets -GodotPath $GodotPath -ProjectPath $testRoot

    $importedNames = @(Get-ChildItem -LiteralPath $importedRoot -Filter "goblin_*" -File -ErrorAction Stop | Select-Object -ExpandProperty Name)
    foreach ($state in $requiredStates) {
        $matchingImports = @($importedNames | Where-Object {
            $_ -match ("^goblin_" + $state + "_v2\.png-")
        })
        if ($matchingImports.Count -eq 0) {
            throw "Godot did not import goblin '$state' into the fresh project cache."
        }
    }

    [ordered]@{
        event = "goblin_sprite_import_test"
        status = "ok"
        freshCache = $true
        importedSpriteStates = $requiredStates
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Assert-UnderRoot -Path $testRoot -Root $temporaryRoot
        # The import result is already decided and printed above. Failing here
        # would report a permission problem in the temporary directory as a
        # failed sprite import, which is what Issue #89 was opened about. The
        # run refuses to start on a temporary directory it cannot delete from,
        # so reaching this warning means something took the directory after the
        # preflight - an antivirus, an editor, another process.
        Remove-TemporaryItemBestEffort `
            -Path $testRoot `
            -Description "isolated sprite-import project" | Out-Null
    }
}
