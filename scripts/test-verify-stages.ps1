[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

# Stages exist so that an agent can verify what it changed without paying for the
# rest. That is only safe while three things hold, and none of them is visible in
# a green run:
#
#   1. every check belongs to a stage, so -Stage and -Skip report what really ran;
#   2. everything that calls dotnet runs before the first stage that repoints
#      APPDATA at the short Godot runtime profile, because after the switch there
#      is no NuGet configuration left to build with;
#   3. the documented stage table matches the script an agent chooses from.
#
# This test holds all three, costs no build and no engine, and runs inside the
# `scripts` stage.
#
# How rule 1 is enforced matters. It used to be a list of *known check command
# names*, and review of PR #70 walked straight through it: `Assert-SomeNewInvariant`
# inside the try block, before the stage loop, and the guard said ok (Issue #71).
# The list is now inverted. The question is no longer "is this one of the checks I
# know about" but "is this one of the few places a check may live at all":
#
#   - a stage body, and the body of any function a stage can reach, may contain
#     anything. That is what a stage is for;
#   - everywhere else - the top level, and the body of any function no stage can
#     reach - may only call the names in $allowedOutsideStages below.
#
# So a check added under a name this guard has never heard of fails by default,
# which is the only way a name-based rule can be honest about the future. Adding a
# name to the allowlist is a deliberate line in a diff, with a reason next to it.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$verifyScript = Join-Path $repoRoot "scripts\verify.ps1"
$environmentDoc = Join-Path $repoRoot "docs\engineering\ENVIRONMENT_SETUP.md"
$sandbox = Join-Path $repoRoot (".artifacts\verify-stage-guard-" + [Guid]::NewGuid().ToString("N"))

# Commands allowed to run outside every stage. Everything here is run setup or
# plumbing: it prepares or reports, it never decides whether the repository is
# healthy. A check does not belong on this list; it belongs in a stage.
$allowedOutsideStages = @(
    # PowerShell plumbing: paths, output, the run directory.
    "ConvertTo-Json",
    "ForEach-Object",
    "Join-Path",
    "New-Item",
    "Out-Null",
    "Select-Object",
    "Set-StrictMode",
    "Sort-Object",
    "Test-Path",
    "Where-Object",
    "Write-Host",
    # Stage selection itself, defined in verify.ps1.
    "Expand-StageNames",
    # Run setup that every stage depends on and no stage can own. It has to
    # happen exactly once, before the first stage, or a partial run would check
    # something other than what a full run checks. $preflightSequence below pins
    # the order these are called in.
    "Initialize-VerificationTemporaryRoot",
    "Resolve-GodotExecutable",
    "Assert-GodotVersion",
    "Get-GodotNuGetSource",
    "Initialize-GodotNuGetEnvironment",
    # Cleanup of the run directory, which is best effort by design (Issue #89).
    "Remove-TemporaryItemBestEffort"
)

# Invocations through a variable cannot be resolved by name, so they are matched
# as text. Anything else dynamic outside a stage is a hole big enough to hide a
# check in, and is reported.
$allowedDynamicInvocations = @(
    '. (Join-Path $PSScriptRoot "GodotTools.ps1")',
    '. (Join-Path $PSScriptRoot "HudVerification.ps1")',
    '. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")',
    '. $stageBody'
)

# Run setup, in the order it has to happen: the temporary directory first,
# because it is the cheapest refusal and no later step can repair it, then the
# engine, then the NuGet profile written from that engine's bundled packages.
$preflightSequence = @(
    "Initialize-VerificationTemporaryRoot",
    "Resolve-GodotExecutable",
    "Assert-GodotVersion",
    "Get-GodotNuGetSource",
    "Initialize-GodotNuGetEnvironment"
)

# The APPDATA invariant as two command names. Initialize-GodotRuntimeEnvironment
# rewrites APPDATA to a short profile with no NuGet configuration in it, so every
# dotnet invocation has to be done by the time it runs.
$profileSwitchCommand = "Initialize-GodotRuntimeEnvironment"
$dotnetCommand = "dotnet"

# The stage table is fenced by markers instead of being recognised by the shape
# of its rows. A row-shaped regex matched the first backtick cell of any table in
# the document, so a future table with a row like "| `assets` | ... |" would have
# been reported as a stage verify.ps1 had forgotten (Issue #71).
$stageTableBeginMarker = "<!-- stage-table:begin -->"
$stageTableEndMarker = "<!-- stage-table:end -->"
$stageRowPattern = '^\|\s*`([a-z][a-z0-9-]*)`\s*\|'

function Get-CommandFilePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Command
    )

    $elements = @($Command.CommandElements)
    for ($index = 0; $index -lt $elements.Count; $index++) {
        $element = $elements[$index]
        if (-not ($element -is [Management.Automation.Language.CommandParameterAst])) {
            continue
        }
        if ($element.ParameterName -ne "FilePath") {
            continue
        }

        # Both the `-FilePath "dotnet"` and `-FilePath:"dotnet"` spellings.
        $value = $element.Argument
        if ($null -eq $value -and $index + 1 -lt $elements.Count) {
            $value = $elements[$index + 1]
        }
        if ($value -is [Management.Automation.Language.StringConstantExpressionAst]) {
            return $value.Value
        }

        return $null
    }

    return $null
}

function Get-VerifyStructure {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "'$Path' does not parse: $(($parseErrors | ForEach-Object { $_.ToString() }) -join '; ')"
    }

    $catalogAssignment = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $node.Left.VariablePath.UserPath -eq "stageCatalog"
    }, $true)
    if ($null -eq $catalogAssignment) {
        throw '$stageCatalog is gone from verify.ps1, so stage selection cannot be checked at all.'
    }

    $catalogHashtable = $catalogAssignment.Right.Find({
        param($node)
        $node -is [Management.Automation.Language.HashtableAst]
    }, $true)
    if ($null -eq $catalogHashtable) {
        throw '$stageCatalog is no longer a hashtable of stages.'
    }

    # Stage order is catalog order, and any selection runs its stages in that
    # order, so the pairs simulated below are the selections an agent can ask for.
    $stages = @()
    foreach ($pair in $catalogHashtable.KeyValuePairs) {
        $stageName = $pair.Item1.Extent.Text.Trim().Trim('"', "'")
        $stageHashtable = $pair.Item2.Find({
            param($node)
            $node -is [Management.Automation.Language.HashtableAst]
        }, $true)
        if ($null -eq $stageHashtable) {
            throw "Stage '$stageName' is not declared as a hashtable with a Body."
        }

        $bodyPairs = @($stageHashtable.KeyValuePairs | Where-Object {
            $_.Item1.Extent.Text.Trim() -eq "Body"
        })
        if ($bodyPairs.Count -ne 1) {
            throw "Stage '$stageName' declares $($bodyPairs.Count) Body entries; exactly one is required."
        }

        $bodyBlock = $bodyPairs[0].Item2.Find({
            param($node)
            $node -is [Management.Automation.Language.ScriptBlockExpressionAst]
        }, $true)
        if ($null -eq $bodyBlock) {
            throw "Stage '$stageName' has a Body that is not a script block."
        }

        $stages += [pscustomobject]@{
            Name = $stageName
            StartOffset = $bodyBlock.Extent.StartOffset
            EndOffset = $bodyBlock.Extent.EndOffset
        }
    }

    if ($stages.Count -lt 2) {
        throw "verify.ps1 declares $($stages.Count) stage(s); staging exists to split the run, not to rename it."
    }

    $functions = @{}
    foreach ($function in $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst]
    }, $true)) {
        $functions[$function.Name] = [pscustomobject]@{
            Name = $function.Name
            StartOffset = $function.Extent.StartOffset
            EndOffset = $function.Extent.EndOffset
            IsPrerequisite = ($function.Name -like "Initialize-*")
        }
    }

    $commands = @()
    foreach ($command in $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true)) {
        $commands += [pscustomobject]@{
            Name = $command.GetCommandName()
            Text = ($command.Extent.Text -replace '\s+', ' ').Trim()
            Line = $command.Extent.StartLineNumber
            StartOffset = $command.Extent.StartOffset
            EndOffset = $command.Extent.EndOffset
            FilePath = (Get-CommandFilePath -Command $command)
        }
    }
    $commands = @($commands | Sort-Object -Property StartOffset)

    $stageLoop = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.ForEachStatementAst] -and
        $node.Condition.Extent.Text -match 'selectedStages'
    }, $true)
    if ($null -eq $stageLoop) {
        throw "verify.ps1 no longer loops over the selected stages, so nothing runs them."
    }

    return [pscustomobject]@{
        Path = $Path
        Stages = @($stages)
        Functions = $functions
        Commands = $commands
        StageLoopStartOffset = $stageLoop.Extent.StartOffset
    }
}

function Get-CommandsInRange {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [int]$StartOffset,

        [Parameter(Mandatory = $true)]
        [int]$EndOffset
    )

    return @($Structure.Commands | Where-Object {
        $_.StartOffset -ge $StartOffset -and $_.EndOffset -le $EndOffset
    })
}

function Get-ReachableFunctions {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure
    )

    # Reachability starts at the stage bodies, because a stage is the only thing a
    # run can be asked to execute. Anything no stage can reach is, for the purpose
    # of this guard, ordinary top-level code - which is what closes the "hide the
    # check in a helper that only the top level calls" way around rule 1.
    $reachable = @{}
    $pending = New-Object Collections.Generic.Queue[string]

    foreach ($stage in $Structure.Stages) {
        foreach ($command in (Get-CommandsInRange -Structure $Structure `
                -StartOffset $stage.StartOffset -EndOffset $stage.EndOffset)) {
            if (-not [string]::IsNullOrEmpty($command.Name) -and
                $Structure.Functions.Contains($command.Name)) {
                $pending.Enqueue($command.Name)
            }
        }
    }

    while ($pending.Count -gt 0) {
        $name = $pending.Dequeue()
        if ($reachable.Contains($name)) {
            continue
        }
        $reachable[$name] = $true

        $function = $Structure.Functions[$name]
        foreach ($command in (Get-CommandsInRange -Structure $Structure `
                -StartOffset $function.StartOffset -EndOffset $function.EndOffset)) {
            if (-not [string]::IsNullOrEmpty($command.Name) -and
                $Structure.Functions.Contains($command.Name) -and
                -not $reachable.Contains($command.Name)) {
                $pending.Enqueue($command.Name)
            }
        }
    }

    return $reachable
}

function Get-StrayCheckFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [hashtable]$Reachable,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedCommands,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedDynamic
    )

    $allowedRegions = @()
    foreach ($stage in $Structure.Stages) {
        $allowedRegions += [pscustomobject]@{
            Start = $stage.StartOffset
            End = $stage.EndOffset
        }
    }
    foreach ($name in $Reachable.Keys) {
        $function = $Structure.Functions[$name]
        $allowedRegions += [pscustomobject]@{
            Start = $function.StartOffset
            End = $function.EndOffset
        }
    }

    $findings = @()
    foreach ($command in $Structure.Commands) {
        $inside = @($allowedRegions | Where-Object {
            $command.StartOffset -ge $_.Start -and $command.EndOffset -le $_.End
        })
        if ($inside.Count -gt 0) {
            continue
        }

        if ([string]::IsNullOrEmpty($command.Name)) {
            if ($AllowedDynamic -notcontains $command.Text) {
                $findings += (
                    "'$($command.Text)' at line $($command.Line) invokes something " +
                    "through a variable outside every stage")
            }
            continue
        }

        if ($AllowedCommands -notcontains $command.Name) {
            $findings += "'$($command.Name)' at line $($command.Line) runs outside every stage"
        }
    }

    return @($findings)
}

function Get-ScopeEvents {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [int]$StartOffset,

        [Parameter(Mandatory = $true)]
        [int]$EndOffset,

        [Parameter(Mandatory = $true)]
        [hashtable]$Fired,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Stack,

        [Parameter(Mandatory = $true)]
        [string]$ProfileSwitchCommand,

        [Parameter(Mandatory = $true)]
        [string]$DotnetCommand
    )

    # The ordered list of things this scope does that the APPDATA invariant cares
    # about. Prerequisites are memoised in verify.ps1, so here they fire at most
    # once per simulated run, at their first invocation - which is exactly why
    # `ui` is allowed to call Initialize-GameHostBuild after the `godot` stage has
    # already switched the profile.
    $events = @()
    foreach ($command in (Get-CommandsInRange -Structure $Structure `
            -StartOffset $StartOffset -EndOffset $EndOffset)) {
        $name = $command.Name
        if ([string]::IsNullOrEmpty($name)) {
            continue
        }

        if ($name -eq $DotnetCommand -or
            ($null -ne $command.FilePath -and $command.FilePath -eq $DotnetCommand)) {
            $events += [pscustomobject]@{ Kind = "dotnet"; Line = $command.Line; Name = $name }
            continue
        }

        if ($name -eq $ProfileSwitchCommand) {
            $events += [pscustomobject]@{ Kind = "profile"; Line = $command.Line; Name = $name }
            continue
        }

        if (-not $Structure.Functions.Contains($name)) {
            continue
        }

        $function = $Structure.Functions[$name]
        if ($function.IsPrerequisite) {
            if ($Fired.Contains($name)) {
                continue
            }
            $Fired[$name] = $true
        }
        if ($Stack -contains $name) {
            continue
        }

        $events += @(Get-ScopeEvents `
            -Structure $Structure `
            -StartOffset $function.StartOffset `
            -EndOffset $function.EndOffset `
            -Fired $Fired `
            -Stack (@($Stack) + $name) `
            -ProfileSwitchCommand $ProfileSwitchCommand `
            -DotnetCommand $DotnetCommand)
    }

    return @($events)
}

function Get-AppDataOrderFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [string]$ProfileSwitchCommand,

        [Parameter(Mandatory = $true)]
        [string]$DotnetCommand
    )

    $findings = @()

    # Every selection runs its stages in catalog order, so a violation always
    # shows up either in a single stage or in some ordered pair of them. Checking
    # both covers every selection an agent can ask for: adding a third stage can
    # only make a pair safer, by firing a memoised prerequisite earlier.
    $selections = @()
    foreach ($stage in $Structure.Stages) {
        $selections += ,@($stage)
    }
    for ($first = 0; $first -lt $Structure.Stages.Count; $first++) {
        for ($second = $first + 1; $second -lt $Structure.Stages.Count; $second++) {
            $selections += ,@($Structure.Stages[$first], $Structure.Stages[$second])
        }
    }

    foreach ($selection in $selections) {
        $fired = @{}
        $events = @()
        foreach ($stage in $selection) {
            $events += @(Get-ScopeEvents `
                -Structure $Structure `
                -StartOffset $stage.StartOffset `
                -EndOffset $stage.EndOffset `
                -Fired $fired `
                -Stack @() `
                -ProfileSwitchCommand $ProfileSwitchCommand `
                -DotnetCommand $DotnetCommand)
        }

        $switch = $null
        foreach ($event in $events) {
            if ($event.Kind -eq "profile") {
                if ($null -eq $switch) {
                    $switch = $event
                }
                continue
            }
            if ($event.Kind -eq "dotnet" -and $null -ne $switch) {
                $findings += (
                    "-Stage $(($selection | ForEach-Object { $_.Name }) -join ',') runs " +
                    "dotnet at line $($event.Line) after $ProfileSwitchCommand switched " +
                    "APPDATA to the short Godot runtime profile at line $($switch.Line). " +
                    "That profile has no NuGet configuration, so everything calling " +
                    "dotnet has to run before the switch")
                break
            }
        }
    }

    return @($findings)
}

function Get-PreflightOrderFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Sequence
    )

    $findings = @()
    $previousName = $null
    $previousOffset = -1

    foreach ($name in $Sequence) {
        $calls = @($Structure.Commands | Where-Object { $_.Name -eq $name })
        if ($calls.Count -eq 0) {
            $findings += (
                "$name is never called, so run setup every stage depends on is missing")
            continue
        }

        foreach ($call in $calls) {
            if ($call.StartOffset -ge $Structure.StageLoopStartOffset) {
                $findings += (
                    "$name is called at line $($call.Line), after the stage loop " +
                    "starts; run setup has to be complete before the first stage")
            }
        }

        $first = @($calls | Sort-Object -Property StartOffset)[0]
        if ($null -ne $previousName -and $first.StartOffset -lt $previousOffset) {
            $findings += (
                "$name is called at line $($first.Line), before $previousName; run " +
                "setup depends on the step before it and has to keep this order")
        }
        $previousName = $name
        $previousOffset = $first.StartOffset
    }

    return @($findings)
}

function Get-PrerequisiteFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [hashtable]$Reachable
    )

    $findings = @()
    foreach ($name in @($Structure.Functions.Keys | Sort-Object)) {
        if (-not $Structure.Functions[$name].IsPrerequisite) {
            continue
        }
        if ($Reachable.Contains($name)) {
            continue
        }
        $findings += (
            "prerequisite $name is not reachable from any stage, so nothing can " +
            "ever run it and no stage is honest about needing it")
    }

    return @($findings)
}

function Get-VerifyStructureFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $structure = Get-VerifyStructure -Path $Path
    $reachable = Get-ReachableFunctions -Structure $structure

    $findings = @()
    $findings += @(Get-StrayCheckFindings `
        -Structure $structure `
        -Reachable $reachable `
        -AllowedCommands $allowedOutsideStages `
        -AllowedDynamic $allowedDynamicInvocations)
    $findings += @(Get-PrerequisiteFindings -Structure $structure -Reachable $reachable)
    $findings += @(Get-PreflightOrderFindings -Structure $structure -Sequence $preflightSequence)
    $findings += @(Get-AppDataOrderFindings `
        -Structure $structure `
        -ProfileSwitchCommand $profileSwitchCommand `
        -DotnetCommand $dotnetCommand)

    return @($findings)
}

function Get-DocumentationFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$StageNames,

        [Parameter(Mandatory = $true)]
        [string]$DocumentPath
    )

    $findings = @()
    $documented = @()
    $insideTable = $false
    $beginSeen = $false
    $endSeen = $false

    foreach ($line in [IO.File]::ReadAllLines($DocumentPath)) {
        $trimmed = $line.Trim()
        if ($trimmed -eq $stageTableBeginMarker) {
            if ($beginSeen) {
                $findings += "the stage table begin marker appears more than once"
            }
            $beginSeen = $true
            $insideTable = $true
            continue
        }
        if ($trimmed -eq $stageTableEndMarker) {
            $endSeen = $true
            $insideTable = $false
            continue
        }
        if ($insideTable -and $line -match $stageRowPattern) {
            $documented += $Matches[1]
        }
    }

    if (-not $beginSeen -or -not $endSeen) {
        $findings += (
            "the stage table in $DocumentPath is not fenced by " +
            "$stageTableBeginMarker and $stageTableEndMarker, so there is nothing " +
            "to compare verify.ps1 against")
        return @($findings)
    }

    if ($documented.Count -eq 0) {
        $findings += "the fenced stage table in $DocumentPath has no stage rows"
        return @($findings)
    }

    $undocumented = @($StageNames | Where-Object { $documented -notcontains $_ })
    $stale = @($documented | Where-Object { $StageNames -notcontains $_ })
    if ($undocumented.Count -gt 0 -or $stale.Count -gt 0) {
        $findings += (
            "the stage table disagrees with verify.ps1: undocumented " +
            "[$($undocumented -join ', ')], documented but absent [$($stale -join ', ')]")
    }

    return @($findings)
}

function Assert-VerifyRejects {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    # The child writes its refusal to stderr, which this session must read as
    # output rather than as its own terminating error.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $null = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($exitCode -eq 0) {
        throw $Message
    }
}

function New-MutatedCopy {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$OriginalText,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [string]$Find,

        [string]$Replace,

        [string]$Append
    )

    $mutated = $OriginalText
    if (-not [string]::IsNullOrEmpty($Find)) {
        # A mutation that silently stops applying is a negative test that passes
        # for the wrong reason, so a missing or ambiguous anchor fails loudly.
        $occurrences = ([regex]::Matches($OriginalText, [regex]::Escape($Find))).Count
        if ($occurrences -ne 1) {
            throw (
                "The negative case '$Name' anchors on text that appears " +
                "$occurrences time(s) in verify.ps1; it has to appear exactly once. " +
                "Update the anchor, do not delete the case.")
        }
        $mutated = $OriginalText.Replace($Find, $Replace)
    }
    if (-not [string]::IsNullOrEmpty($Append)) {
        $mutated = $mutated + $Append
    }
    if ($mutated -eq $OriginalText) {
        throw "The negative case '$Name' did not change verify.ps1 at all."
    }

    [IO.File]::WriteAllText($Destination, $mutated, [Text.UTF8Encoding]::new($false))
    return $Destination
}

# --- the real script and the real document ---------------------------------

$structureFindings = @(Get-VerifyStructureFindings -Path $verifyScript)
if ($structureFindings.Count -gt 0) {
    throw (
        "verify.ps1 breaks the stage contract:" + [Environment]::NewLine + "  " +
        ($structureFindings -join ([Environment]::NewLine + "  ")) +
        [Environment]::NewLine +
        "A check belongs in a stage body. Run setup that genuinely cannot live in " +
        "one goes into `$allowedOutsideStages in scripts\test-verify-stages.ps1 " +
        "with the reason next to it - otherwise -Stage and -Skip misreport what ran.")
}

$listOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript -ListStages
if ($LASTEXITCODE -ne 0) {
    throw "verify.ps1 -ListStages failed with exit code $LASTEXITCODE."
}

$catalogLine = $listOutput | Where-Object { $_ -match '"event":"verification_stages"' } |
    Select-Object -Last 1
if ($null -eq $catalogLine) {
    throw "verify.ps1 -ListStages did not emit a verification_stages event."
}

$catalog = ([string]$catalogLine | ConvertFrom-Json)
$stageNames = @($catalog.stages | ForEach-Object { [string]$_.name })
if ($stageNames.Count -lt 2) {
    throw "verify.ps1 published $($stageNames.Count) stage(s); staging exists to split the run, not to rename it."
}

foreach ($stage in $catalog.stages) {
    if ([string]::IsNullOrWhiteSpace([string]$stage.summary)) {
        throw "Stage '$($stage.name)' has no summary, so -ListStages cannot tell an agent when to pick it."
    }
}

# The documented table is the only place an agent looks before choosing a stage.
# A stage missing from it is unreachable in practice; a row left behind after a
# rename sends the agent to a stage that no longer exists.
$documentationFindings = @(Get-DocumentationFindings `
    -StageNames $stageNames `
    -DocumentPath $environmentDoc)
if ($documentationFindings.Count -gt 0) {
    throw (
        "The stage table in ENVIRONMENT_SETUP.md does not match verify.ps1:" +
        [Environment]::NewLine + "  " +
        ($documentationFindings -join ([Environment]::NewLine + "  ")))
}

Assert-VerifyRejects `
    -Arguments @("-Stage", "definitely-not-a-stage") `
    -Message "verify.ps1 accepted an unknown stage name instead of failing."

Assert-VerifyRejects `
    -Arguments @("-Stage", $stageNames[0], "-Skip", $stageNames[0]) `
    -Message "verify.ps1 accepted an empty stage selection instead of failing."

# --- the guard against itself ----------------------------------------------

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

try {
    $originalText = [IO.File]::ReadAllText($verifyScript)
    $newline = if ($originalText.Contains("`r`n")) { "`r`n" } else { "`n" }

    # A guard nobody has watched fail is a guard nobody knows works. Every case
    # below is a change someone could plausibly make, applied to a copy, and each
    # one has to come back named in the findings.
    $negativeCases = @(
        [pscustomobject]@{
            Name = "check-outside-a-stage-under-a-new-name"
            Why = "a check outside every stage, under a name this guard has never seen"
            Find = '    foreach ($stageName in $selectedStages) {'
            Replace = @(
                '    Assert-SomeNewInvariant -Path $repoRoot',
                '    foreach ($stageName in $selectedStages) {'
            ) -join $newline
            Append = ""
            Expect = @("Assert-SomeNewInvariant")
        },
        [pscustomobject]@{
            Name = "check-outside-a-stage-through-a-variable"
            Why = "a check outside every stage, invoked through a variable"
            Find = '$scope = if ($notRunStages.Count -eq 0) { "full" } else { "partial" }'
            Replace = @(
                '& $strayCheck',
                '$scope = if ($notRunStages.Count -eq 0) { "full" } else { "partial" }'
            ) -join $newline
            Append = ""
            Expect = @('& $strayCheck')
        },
        [pscustomobject]@{
            Name = "dotnet-in-a-stage-after-the-profile-switch"
            Why = "a stage that calls dotnet after APPDATA moved to the Godot profile"
            Find = '            $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"'
            Replace = @(
                '            Invoke-Checked -FilePath "dotnet" -Arguments @("--version")',
                '            $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"'
            ) -join $newline
            Append = ""
            Expect = @("screenshots", "APPDATA")
        },
        [pscustomobject]@{
            Name = "prerequisites-reordered-inside-a-stage"
            Why = "Initialize-EngineRuntime moved in front of Initialize-GameHostBuild"
            Find = @(
                '            Initialize-GameHostBuild',
                '            Initialize-EngineRuntime',
                '',
                '            # Text before pixels:'
            ) -join $newline
            Replace = @(
                '            Initialize-EngineRuntime',
                '            Initialize-GameHostBuild',
                '',
                '            # Text before pixels:'
            ) -join $newline
            Append = ""
            Expect = @("ui", "APPDATA")
        },
        [pscustomobject]@{
            Name = "prerequisite-no-stage-can-reach"
            Why = "shared setup nothing is able to trigger"
            Find = ""
            Replace = ""
            Append = @(
                '',
                'function Initialize-Orphan {',
                '    Invoke-Checked -FilePath "dotnet" -Arguments @("--info")',
                '}',
                ''
            ) -join $newline
            Expect = @("Initialize-Orphan", "not reachable")
        },
        [pscustomobject]@{
            Name = "temporary-directory-preflight-dropped"
            Why = "the Issue #89 preflight taken out of the run"
            Find = '    $temporaryRootSelection = Initialize-VerificationTemporaryRoot -ExplicitPath $TemporaryRoot'
            Replace = '    $temporaryRootSelection = [pscustomobject]@{ Path = $null; Source = $null }'
            Append = ""
            Expect = @("Initialize-VerificationTemporaryRoot", "never called")
        }
    )

    foreach ($case in $negativeCases) {
        $copy = New-MutatedCopy `
            -Name $case.Name `
            -OriginalText $originalText `
            -Destination (Join-Path $sandbox ($case.Name + ".ps1")) `
            -Find $case.Find `
            -Replace $case.Replace `
            -Append $case.Append

        $caseFindings = @(Get-VerifyStructureFindings -Path $copy)
        foreach ($expected in @($case.Expect)) {
            $matched = @($caseFindings | Where-Object { $_ -match [regex]::Escape($expected) })
            if ($matched.Count -eq 0) {
                throw (
                    "The stage guard did not catch $($case.Why). Expected a finding " +
                    "mentioning '$expected'; got " +
                    $(if ($caseFindings.Count -eq 0) { "nothing at all." } else { ($caseFindings -join "; ") }))
            }
        }
    }

    # The positive control. Without it every case above could be passing because
    # the copy is broken rather than because the mutation was caught.
    $untouched = Join-Path $sandbox "untouched.ps1"
    [IO.File]::WriteAllText($untouched, $originalText, [Text.UTF8Encoding]::new($false))
    $untouchedFindings = @(Get-VerifyStructureFindings -Path $untouched)
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "An unmodified copy of verify.ps1 was reported as broken, so the " +
            "negative cases above prove nothing: " + ($untouchedFindings -join "; "))
    }

    # --- and the documentation check against itself -------------------------

    $documentText = [IO.File]::ReadAllText($environmentDoc)
    $documentNewline = if ($documentText.Contains("`r`n")) { "`r`n" } else { "`n" }

    # A second table whose first cell is a name in backticks is exactly what used
    # to produce a false "documented but absent".
    $foreignTableDoc = Join-Path $sandbox "foreign-table.md"
    [IO.File]::WriteAllText(
        $foreignTableDoc,
        $documentText + (@(
            '',
            '| Directory | What it is |',
            '|---|---|',
            '| `assets` | an unrelated table whose first cell is a name in backticks |',
            ''
        ) -join $documentNewline),
        [Text.UTF8Encoding]::new($false))
    $foreignFindings = @(Get-DocumentationFindings `
        -StageNames $stageNames `
        -DocumentPath $foreignTableDoc)
    if ($foreignFindings.Count -gt 0) {
        throw (
            "A table that has nothing to do with stages was read as the stage " +
            "table: " + ($foreignFindings -join "; "))
    }

    # ...and the check still has to fail when the stage table itself is wrong.
    $lastStage = $stageNames[-1]
    $droppedRowDoc = Join-Path $sandbox "dropped-row.md"
    $keptLines = @()
    $droppedRows = 0
    foreach ($line in [IO.File]::ReadAllLines($environmentDoc)) {
        if ($line -match ('^\|\s*`' + [regex]::Escape($lastStage) + '`\s*\|')) {
            $droppedRows++
            continue
        }
        $keptLines += $line
    }
    if ($droppedRows -ne 1) {
        throw "Expected exactly one documented row for stage '$lastStage'; found $droppedRows."
    }
    [IO.File]::WriteAllText(
        $droppedRowDoc,
        ($keptLines -join $documentNewline) + $documentNewline,
        [Text.UTF8Encoding]::new($false))
    $droppedFindings = @(Get-DocumentationFindings `
        -StageNames $stageNames `
        -DocumentPath $droppedRowDoc)
    if (@($droppedFindings | Where-Object { $_ -match [regex]::Escape($lastStage) }).Count -eq 0) {
        throw (
            "Removing stage '$lastStage' from the documented table went unnoticed: " +
            $(if ($droppedFindings.Count -eq 0) { "no findings." } else { ($droppedFindings -join "; ") }))
    }

    $stageCount = @($stageNames).Count
    [ordered]@{
        event = "verify_stages_test"
        status = "ok"
        stages = $stageNames
        documentedStages = $stageCount
        allowedOutsideStages = $allowedOutsideStages.Count
        preflightSequence = $preflightSequence
        stageSelectionsChecked = $stageCount + ($stageCount * ($stageCount - 1) / 2)
        negativeCasesProven = @($negativeCases | ForEach-Object { $_.Name })
        documentationCasesProven = @("foreign-table-ignored", "dropped-row-caught")
        emptySelectionRejected = $true
        unknownStageRejected = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-TemporaryItemBestEffort `
        -Path $sandbox `
        -Description "stage guard negative test sandbox" | Out-Null
}
