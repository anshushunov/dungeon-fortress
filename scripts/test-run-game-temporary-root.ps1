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
# absence caused the outage.
#
# Callers are discovered by scanning scripts\**\*.ps1, not by a hand-written
# list. Review of this PR's first round found the list version: a fixed array
# of the three callers known at the time, which would leave any future script
# that calls Resolve-VerificationTemporaryRoot with the same defect green,
# unlisted and unnoticed - the same class of blindness Issue #329 itself is
# about, narrowed from "an unregistered existing caller" to "an unregistered
# future one". A scan cannot go stale that way, because it does not depend on
# anyone remembering to update it; the entry it would need updating is deleted
# together with this comment. What we pay for it: parsing every *.ps1 file
# under scripts\ on every `scripts` stage run instead of three named ones -
# 44 files today, milliseconds of AST parsing, no build and no engine, so the
# cost does not compete with the risk it closes.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptsRoot = Join-Path $repoRoot "scripts"

# Two files are read but deliberately excluded from the discovered caller set,
# each for a reason a scan cannot infer on its own:
#   - TemporaryRoot.ps1 is the resolver itself. Its own internal call, inside
#     Initialize-VerificationTemporaryRoot, is already excluded by the
#     top-level-only filter below (it lives inside a FunctionDefinitionAst),
#     but the file is skipped outright for the same reason a function is not
#     asked to review its own body.
#   - test-temporary-root.ps1 is the resolver's own regression harness. It
#     calls Resolve-VerificationTemporaryRoot four times at its top level, on
#     purpose and by design (proving resolution order across all three
#     tiers - see its own header comment), including once without
#     -RepositoryRoot specifically to prove the environment-variable tier
#     still wins without needing it. Get-TemporaryRootCallFindings below
#     assumes a caller makes exactly one call for its own operational use;
#     applying that assumption to a file whose entire job is to make several
#     calls with different shapes on purpose would either misreport a correct
#     file as broken or have to special-case counting away the one property
#     that makes it a regression harness at all. Its own correctness is what
#     evidence/302-*.json and the mutants in scripts\test-temporary-root.ps1
#     already prove; this contract does not need to prove it again.
$excludedFromScan = @(
    (Join-Path $scriptsRoot "TemporaryRoot.ps1"),
    (Join-Path $scriptsRoot "test-temporary-root.ps1")
)

function Get-ResolverCallerCandidates {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScriptsRoot,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$ExcludedPaths
    )

    $excluded = @{}
    foreach ($path in $ExcludedPaths) {
        $excluded[[IO.Path]::GetFullPath($path).ToLowerInvariant()] = $true
    }

    $candidates = @()
    foreach ($file in (Get-ChildItem -LiteralPath $ScriptsRoot -Filter "*.ps1" -Recurse -File)) {
        $fullPath = $file.FullName
        if ($excluded.Contains($fullPath.ToLowerInvariant())) {
            continue
        }

        $parseErrors = $null
        $tokens = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($fullPath, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            # A file that does not parse is a different problem than this
            # contract's to report; scripts\test-verify-stages.ps1 and the
            # solution build are where a syntax error would surface instead.
            continue
        }

        $hasTopLevelCall = @($ast.EndBlock.Statements | ForEach-Object {
            $_.FindAll({
                param($node)
                $node -is [Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq "Resolve-VerificationTemporaryRoot"
            }, $true)
        } | Where-Object {
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
        }).Count -gt 0

        if ($hasTopLevelCall) {
            $candidates += $fullPath
        }
    }

    return @($candidates | Sort-Object)
}

$callerPaths = @(Get-ResolverCallerCandidates -ScriptsRoot $scriptsRoot -ExcludedPaths $excludedFromScan)
if ($callerPaths.Count -eq 0) {
    throw (
        "No script under '$scriptsRoot' calls Resolve-VerificationTemporaryRoot " +
        "at its top level. Either every caller moved, or the scan itself is " +
        "broken - either way this check has nothing to prove and that is " +
        "itself worth failing loudly on.")
}
$callers = @($callerPaths | ForEach-Object { [IO.Path]::GetFileName($_) })

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
foreach ($callerPath in $callerPaths) {
    $liveFindings += @(Get-TemporaryRootCallFindings -Path $callerPath)
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

    # The positive control, restated: an unmutated copy of every discovered
    # caller is clean, so the cases above are proving something real rather
    # than a check that always fires.
    $untouchedFindings = @()
    foreach ($callerPath in $callerPaths) {
        $callerName = [IO.Path]::GetFileName($callerPath)
        $untouchedCopy = Join-Path $sandbox ("untouched-" + $callerName)
        Copy-Item -LiteralPath $callerPath -Destination $untouchedCopy -Force
        $untouchedFindings += @(Get-TemporaryRootCallFindings -Path $untouchedCopy)
    }
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "Unmodified copies of $($callers -join ', ') were reported as " +
            "broken, so the mutation cases above prove nothing: " +
            ($untouchedFindings -join "; "))
    }

    # A discovered caller list is only as trustworthy as the scan that found
    # it. This proves the scan itself would catch a new, unregistered caller
    # with the exact Issue #329 defect - not just that Get-TemporaryRootCall
    # Findings flags it once handed a path, which the cases above already
    # cover for the two callers known when this file was written.
    # In its own subdirectory, isolated from the mutation-case copies above:
    # those still contain (mutated and unmutated) calls to
    # Resolve-VerificationTemporaryRoot, and scanning them together with the
    # planted caller would make this control meaningless.
    $scanProbeRoot = Join-Path $sandbox "scan-probe"
    New-Item -ItemType Directory -Force -Path $scanProbeRoot | Out-Null
    $plantedCallerPath = Join-Path $scanProbeRoot "planted-unregistered-caller.ps1"
    [IO.File]::WriteAllText(
        $plantedCallerPath,
        (
            "Set-StrictMode -Version Latest`n" +
            ". (Join-Path `$PSScriptRoot `"TemporaryRoot.ps1`")`n" +
            "`$selection = Resolve-VerificationTemporaryRoot -ExplicitPath `$TemporaryRoot`n"
        ),
        [Text.UTF8Encoding]::new($false))
    $discoveredInSandbox = @(Get-ResolverCallerCandidates `
        -ScriptsRoot $scanProbeRoot `
        -ExcludedPaths @())
    if ($discoveredInSandbox.Count -ne 1 -or
        [IO.Path]::GetFullPath($discoveredInSandbox[0]) -ne [IO.Path]::GetFullPath($plantedCallerPath)) {
        throw (
            "Get-ResolverCallerCandidates did not discover a freshly planted " +
            "caller with a genuine top-level call to " +
            "Resolve-VerificationTemporaryRoot; found " +
            "$($discoveredInSandbox.Count) candidate(s) instead of 1. The scan " +
            "this check depends on to find tomorrow's caller cannot be trusted " +
            "if it cannot find today's planted one.")
    }
    $plantedFindings = @(Get-TemporaryRootCallFindings -Path $plantedCallerPath)
    $plantedMatched = @($plantedFindings | Where-Object { $_ -match "planted-unregistered-caller\.ps1" })
    if ($plantedMatched.Count -eq 0) {
        throw (
            "The scan found the planted unregistered caller but its missing " +
            "-RepositoryRoot was not reported: " +
            $(if ($plantedFindings.Count -eq 0) { "no findings at all." } else { ($plantedFindings -join "; ") }))
    }

    [ordered]@{
        event = "run_game_temporary_root_test"
        status = "ok"
        callersDiscovered = $callers
        excludedFromScan = @($excludedFromScan | ForEach-Object { [IO.Path]::GetFileName($_) })
        mutationCasesProven = @($mutationCases | ForEach-Object { $_.Name })
        scanDiscoversUnregisteredCaller = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
