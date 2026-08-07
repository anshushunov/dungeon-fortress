[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #329. scripts\verify.ps1 is not the only entry point that resolves a
# temporary directory before starting the engine - scripts\run-game.ps1 and
# scripts\update-golden-ui.ps1 do too, and neither is reached by any stage of
# verify.ps1 (stage `godot` starts the engine its own way). That is exactly
# why Issue #302's contract change to Resolve-VerificationTemporaryRoot broke
# both of them for a full day without a single one of ten merged PRs noticing:
# every -TemporaryRoot parameter in this repository is optional, an omitted
# one arrives as an empty string rather than as "absent" (PowerShell does not
# distinguish the two for a plain [string]), and an empty -ExplicitPath is not
# an override - it falls through to $env:DUNGEON_FORTRESS_TEMP and then to the
# own-directory tier, which throws without -RepositoryRoot to compute a
# default from.
#
# This is a static contract check, not a real invocation: actually running
# either script past this point means a dotnet restore and build (network,
# several minutes) and, for run-game.ps1 without -ScreenshotPath, an engine
# window that never exits on its own - neither fits a stage documented as
# "no build, no engine, no network" alongside the temporary-directory and
# stage-selection guards it runs next to. The static shape this checks -
# every call to Resolve-VerificationTemporaryRoot outside TemporaryRoot.ps1
# itself passes a non-empty -RepositoryRoot - is exactly the shape whose
# absence caused the outage, and scripts\test-goblin-sprite-import.ps1 already
# gets this right; it is used here as the positive control the other two are
# compared against.
#
# It needs no build, no engine and no network, and runs inside the `scripts`
# stage.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptsRoot = Join-Path $repoRoot "scripts"

# Every script outside TemporaryRoot.ps1 itself that calls
# Resolve-VerificationTemporaryRoot at its own top level (not inside a
# function - Initialize-VerificationTemporaryRoot's own internal call is
# TemporaryRoot.ps1's problem, not a caller's, and is exercised by
# scripts\test-temporary-root.ps1 instead). test-goblin-sprite-import.ps1 is
# the positive control: it already passes -RepositoryRoot and has to keep
# proving that this check does not simply always fail.
$callers = @(
    "run-game.ps1",
    "update-golden-ui.ps1",
    "test-goblin-sprite-import.ps1"
)

function Get-TemporaryRootCallFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fileName = [IO.Path]::GetFileName($Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @("'$fileName' does not exist, so its temporary-root call cannot be checked")
    }

    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        return @("'$fileName' does not parse: $(($parseErrors | ForEach-Object { $_.ToString() }) -join '; ')")
    }

    # Top-level only: inside a FunctionDefinitionAst is a different scope this
    # check does not police (see the module comment above). The call this
    # repository's callers make is always the right-hand side of an
    # assignment (`$x = Resolve-VerificationTemporaryRoot ...`), which is an
    # AssignmentStatementAst rather than a bare PipelineAst - so every
    # top-level statement is searched, not just pipelines.
    $topLevelCalls = @($ast.EndBlock.Statements | ForEach-Object {
        $_.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst] -and
            $node.GetCommandName() -eq "Resolve-VerificationTemporaryRoot"
        }, $true)
    } | Where-Object {
        # Exclude a call nested inside a function definition at the top level
        # (none of today's callers has one, but a future one might).
        $enclosingFunction = $null
        $parent = $_.Parent
        while ($null -ne $parent) {
            if ($parent -is [Management.Automation.Language.FunctionDefinitionAst]) {
                $enclosingFunction = $parent
                break
            }
            $parent = $parent.Parent
        }
        $null -eq $enclosingFunction
    })

    if ($topLevelCalls.Count -eq 0) {
        return @("'$fileName' no longer calls Resolve-VerificationTemporaryRoot at its top level; this check has nothing left to police there")
    }
    if ($topLevelCalls.Count -gt 1) {
        return @("'$fileName' calls Resolve-VerificationTemporaryRoot $($topLevelCalls.Count) times at its top level; this check assumes exactly one")
    }

    $call = $topLevelCalls[0]
    $elements = @($call.CommandElements)
    $repositoryRootValue = $null
    $found = $false
    for ($index = 1; $index -lt $elements.Count; $index++) {
        $element = $elements[$index]
        if (-not ($element -is [Management.Automation.Language.CommandParameterAst])) {
            continue
        }
        if ($element.ParameterName -ne "RepositoryRoot") {
            continue
        }
        $found = $true
        $value = $element.Argument
        if ($null -eq $value -and
            $index + 1 -lt $elements.Count -and
            -not ($elements[$index + 1] -is [Management.Automation.Language.CommandParameterAst])) {
            $value = $elements[$index + 1]
        }
        $repositoryRootValue = $value
        break
    }

    if (-not $found) {
        return @(
            "'$fileName' line $($call.Extent.StartLineNumber) calls " +
            "Resolve-VerificationTemporaryRoot without -RepositoryRoot. " +
            "-TemporaryRoot is an optional parameter of this script, so an " +
            "omitted one arrives here as an empty -ExplicitPath, and without " +
            "-RepositoryRoot the resolver throws instead of falling back to " +
            "its own-directory default (Issue #329)."
        )
    }

    if ($null -eq $repositoryRootValue) {
        return @(
            "'$fileName' line $($call.Extent.StartLineNumber) passes " +
            "-RepositoryRoot as a switch with no value, which PowerShell " +
            "binds as `$true - not a path the resolver can use."
        )
    }

    $valueText = $repositoryRootValue.Extent.Text.Trim()
    if ($valueText -eq '""' -or $valueText -eq "''") {
        return @(
            "'$fileName' line $($call.Extent.StartLineNumber) passes " +
            "-RepositoryRoot as a literal empty string, which is exactly the " +
            "'no override' shape Issue #329 was about - the resolver's " +
            "own-directory tier still has nothing to compute a default from."
        )
    }

    return @()
}

$liveFindings = @()
foreach ($caller in $callers) {
    $liveFindings += @(Get-TemporaryRootCallFindings -Path (Join-Path $scriptsRoot $caller))
}
if ($liveFindings.Count -gt 0) {
    throw (
        "Resolve-VerificationTemporaryRoot is called without a usable " +
        "-RepositoryRoot:" + [Environment]::NewLine + "  " +
        ($liveFindings -join ([Environment]::NewLine + "  ")))
}

# ...and the check is watched failing, on copies, so a caller that regresses
# is known to turn this from green to red rather than trusted to on faith.
$sandbox = Join-Path $repoRoot (".artifacts\run-game-temp-root-guard-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
try {
    $mutationCases = @(
        [pscustomobject]@{
            Name = "run-game-loses-repository-root"
            File = "run-game.ps1"
            Find = '-ExplicitPath $TemporaryRoot -RepositoryRoot $repoRoot'
            Replace = '-ExplicitPath $TemporaryRoot'
            Expect = "run-game.ps1"
        },
        [pscustomobject]@{
            Name = "update-golden-ui-loses-repository-root"
            File = "update-golden-ui.ps1"
            Find = '-ExplicitPath $TemporaryRoot -RepositoryRoot $repoRoot'
            Replace = '-ExplicitPath $TemporaryRoot'
            Expect = "update-golden-ui.ps1"
        },
        [pscustomobject]@{
            Name = "run-game-passes-an-empty-literal"
            File = "run-game.ps1"
            Find = '-ExplicitPath $TemporaryRoot -RepositoryRoot $repoRoot'
            Replace = '-ExplicitPath $TemporaryRoot -RepositoryRoot ""'
            Expect = "run-game.ps1"
        }
    )

    foreach ($case in $mutationCases) {
        $sourcePath = Join-Path $scriptsRoot $case.File
        $sourceText = [IO.File]::ReadAllText($sourcePath)
        $occurrences = ([regex]::Matches($sourceText, [regex]::Escape($case.Find))).Count
        if ($occurrences -ne 1) {
            throw (
                "The mutation case '$($case.Name)' anchors on text appearing " +
                "$occurrences time(s) in '$($case.File)'; it has to appear " +
                "once. Update the anchor, do not delete the case.")
        }

        $mutatedPath = Join-Path $sandbox $case.File
        [IO.File]::WriteAllText(
            $mutatedPath,
            $sourceText.Replace($case.Find, $case.Replace),
            [Text.UTF8Encoding]::new($false))

        $caseFindings = @(Get-TemporaryRootCallFindings -Path $mutatedPath)
        $matched = @($caseFindings | Where-Object { $_ -match [regex]::Escape($case.Expect) })
        if ($matched.Count -eq 0) {
            throw (
                "Mutating '$($case.File)' as case '$($case.Name)' went " +
                "unnoticed. Expected a finding mentioning '$($case.Expect)'; " +
                "got " +
                $(if ($caseFindings.Count -eq 0) { "nothing at all." } else { ($caseFindings -join "; ") }))
        }
    }

    # The positive control, restated: an unmutated copy of every caller is
    # clean, so the cases above are proving something real rather than a
    # check that always fires.
    $untouchedFindings = @()
    foreach ($caller in $callers) {
        $sourcePath = Join-Path $scriptsRoot $caller
        $untouchedCopy = Join-Path $sandbox ("untouched-" + $caller)
        Copy-Item -LiteralPath $sourcePath -Destination $untouchedCopy -Force
        $untouchedFindings += @(Get-TemporaryRootCallFindings -Path $untouchedCopy)
    }
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "Unmodified copies of $($callers -join ', ') were reported as " +
            "broken, so the mutation cases above prove nothing: " +
            ($untouchedFindings -join "; "))
    }

    [ordered]@{
        event = "run_game_temporary_root_test"
        status = "ok"
        callersChecked = $callers
        mutationCasesProven = @($mutationCases | ForEach-Object { $_.Name })
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
