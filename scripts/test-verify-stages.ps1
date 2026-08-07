[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")
# Dot-sourced so the engine-gate checks below can call Resolve-GodotExecutable
# directly, in-process, to derive the expected full-scope refusal text from
# the live function instead of a hand-copied string that could drift from it.
. (Join-Path $PSScriptRoot "GodotTools.ps1")

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
#     reach - may only call the names in $allowedOutsideStages below;
#   - run setup lives in functions this script dot-sources, so those bodies are
#     parsed too and may only call $allowedOutsideStages plus the plumbing named
#     in $allowedInsideRunSetup (Issue #102);
#   - with one exit that is easy to read past, so it is spelled out. Inside run
#     setup, a call to a function that is *itself* part of the run-setup closure
#     is allowed under any name, because the guard then goes on to police that
#     function's body instead. Membership is by reachability, not by permission:
#     define Assert-ProbeInvariant in TemporaryRoot.ps1, call it from
#     Initialize-VerificationTemporaryRoot, and it joins the closure and passes.
#     Measured: exit 0, no findings, the new name visible only as an entry in
#     runSetupFunctions in this test's JSON.
#
# So a check added under a name this guard has never heard of fails by default at
# the top level of verify.ps1 and inside any function no stage can reach - which
# is the only way a name-based rule can be honest about the future. It does not
# fail by default inside run setup. There the name is admitted by reachability
# and only the body is policed, so a check assembled from allowed plumbing gets
# through under a name of its own.
#
# That is not a separate hole. It is deferred item 2 below wearing a function
# name, it needs the same decision, and one rule would close both. The negative
# case `check-inside-dot-sourced-run-setup` proves the part that does hold - a
# name that is called but defined nowhere - and should not be read as proving
# more than that.
#
# Adding a name to either allowlist is a deliberate line in a diff, with a reason
# next to it.

# --- which way each rule falls over ------------------------------------------
#
# Two rules in this file fail in opposite directions, and until Issue #102 that
# was nowhere written down. A reader who assumes both are fail-closed will trust
# the second one further than it deserves.
#
#   fail-closed - "a check outside every stage". An unknown command name is a
#   finding. Adding a check is therefore visible by default, and the cost is
#   borne by whoever adds a legitimate new piece of run setup: they have to name
#   it in an allowlist and say why. That is the trade this rule is meant to make.
#
#   fail-open - the APPDATA order model. It follows what it can recognise -
#   `dotnet` as a command name and `-FilePath "dotnet"` as a string literal - and
#   says nothing about what it cannot. A program invoked in a way the model does
#   not read is simply absent from the simulated run, and absence looks exactly
#   like compliance.
#
# Issue #102 narrowed the open side rather than documenting it away: a `-FilePath`
# whose value is not a string literal is now a finding, because the model cannot
# tell whether it runs dotnet and refusing is cheaper than guessing. What stays
# open, deliberately: a program invoked through a variable with the call operator
# (`& $tool`), and anything a *child* script runs. The model follows dotnet inside
# verify.ps1 and its run setup, not inside every script they start.

# --- the six known ways past this guard, and what was decided about each ------
#
# Independent review of PR #97 built a harness that reproduced the analytic half
# of this guard and mutated verify.ps1 against it. Six ways through came back.
# None of them is a regression - the previous guard missed them too - so each got
# a decision rather than a reflex, and the decisions live here because this is
# where the rules live.
#
#   1. Only verify.ps1 was parsed. The bodies of the functions allowed to run
#      outside stages live in dot-sourced files and were never analysed; PR #97
#      added two more such names. CLOSED. Every file verify.ps1 dot-sources is
#      parsed with it, and the transitive bodies of the run-setup entry points
#      are policed by $allowedInsideRunSetup. Negative cases
#      `check-inside-dot-sourced-run-setup`, `check-at-the-top-level-of-a-module`
#      and `dot-source-target-through-a-variable`.
#
#   2. A check assembled only from allowed plumbing:
#      `if (-not (Test-Path ...)) { throw ... }` outside a stage still passes,
#      because every piece of it is separately legitimate. DEFERRED. Closing it
#      means deciding what separates "this script refuses bad input" from "this
#      run decided the repository is unhealthy", and both spellings are the same
#      code. verify.ps1 already refuses an unknown -Stage name and an empty
#      selection this way, and those refusals are correct. Condition to revisit:
#      a rule that tells those two apart without renaming the existing ones -
#      for example a marker a check has to carry - not a rule that bans `throw`.
#      It has a second spelling, found by review of this change and described at
#      the top of this file: a new function reachable from run setup joins the
#      closure, so its *name* is admitted and only its body is policed - and a
#      body of plumbing plus `throw` is this item again. Both spellings wait on
#      the same decision.
#
#   3. A check with no command in it at all:
#      `if ([IO.File]::ReadAllText(...) -notmatch ...) { throw ... }`. DEFERRED
#      for the same reason and under the same condition as 2: this model reasons
#      about commands, and a .NET method call is a check only if the answer to 2
#      says it is.
#
#   4. A canonical decoy in front of the deciding deletion. The deletion contract
#      in scripts\test-temporary-root.ps1 pins the shape of the *first* deletion
#      in source order, not that the diagnosis is derived from it, so a canonical
#      `Remove-Item -Recurse -Force -ErrorAction Stop` on a throwaway path
#      followed by a real `[IO.Directory]::Delete` would pass. REJECTED. Writing
#      that requires intent; drift does not produce it. This guard exists to
#      catch drift, and buying the malicious case costs data flow analysis.
#
#   5. -ErrorAction was matched by name, not by value, so
#      `-ErrorAction SilentlyContinue` satisfied the deletion contract. CLOSED in
#      scripts\test-temporary-root.ps1, which now requires the value `Stop` and
#      carries the measurement that says why.
#
#   6. `$t = "dotnet"; Invoke-Checked -FilePath $t` walked past the APPDATA
#      model. CLOSED by refusing, not by resolving: a non-literal -FilePath is a
#      finding. Every -FilePath in verify.ps1 is a string literal today, so the
#      rule costs nothing to adopt and turns silence into a question. Resolving
#      variable *values* in the AST stays out of scope - that is the separate,
#      expensive work Issue #102 rules out. Negative case
#      `program-name-through-a-variable`.

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
    "Remove-TemporaryItemBestEffort",
    # Issue #302. Defined in TemporaryRoot.ps1: decides whether this run owns
    # its temporary root at all (only the own-directory default does) before
    # delegating to Remove-TemporaryItemBestEffort above. Same reasoning as
    # that function - cleanup, not a check - and it has to run in `finally`
    # regardless of how far the run got, which is why $temporaryRootPath and
    # $temporaryRootOwned are read even on a preflight failure.
    "Complete-VerificationTemporaryRoot",
    # Issue #284. Defined in GodotTools.ps1: routes a line to the run's stage
    # log file when verify.ps1 set one, and to Write-Host otherwise. It never
    # decides whether the repository is healthy - it only decides where a
    # line that was going to be printed anyway ends up - so it is plumbing by
    # the same reasoning as Write-Host itself, which is already on this list.
    # The stage loop calls it directly (outside every stage, by definition,
    # since it announces a stage before that stage's body runs); calls from
    # inside Invoke-Checked, Invoke-Scenario, Invoke-GodotChecked and friends
    # do not need this entry at all, because those functions are themselves
    # reachable from a stage body and so is everything they call.
    "Write-VerifyDiagnostic"
)

# Plumbing the run-setup bodies above may use on top of $allowedOutsideStages.
# These names are allowed *inside* those functions and nowhere else, so the top
# level of verify.ps1 does not inherit them. Each one is here because run setup
# genuinely cannot be written without it (Issue #102, item 1).
$allowedInsideRunSetup = @(
    # Finding the engine on PATH when no path was given.
    "Get-Command",
    # Issue #307. Finding the engine by disk layout - a Godot_v*-stable_mono_win64
    # directory next to the repository root, and the *_console.exe inside it -
    # when neither an explicit path nor the environment resolved it.
    "Get-ChildItem",
    # Collapsing the engine's --version output into one string.
    "Out-String",
    # The deciding delete of the temporary-directory probe, and the best-effort
    # cleanup of the run directory. Both are Issue #89 and both are why this
    # name may not appear at the top level.
    "Remove-Item",
    # Normalising an engine path that was given explicitly.
    "Resolve-Path",
    # Locating the NuGet source bundled next to the engine executable.
    "Split-Path"
)

# Invocations through a variable cannot be resolved by name, so they are matched
# as text. Anything else dynamic outside a stage is a hole big enough to hide a
# check in, and is reported.
$allowedDynamicInvocations = @(
    '. (Join-Path $PSScriptRoot "GodotTools.ps1")',
    '. (Join-Path $PSScriptRoot "HudVerification.ps1")',
    '. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")',
    '. $stageBody',
    # Run setup asks the engine for its own version. The executable is only
    # known at run time, so this one cannot be spelled as a literal.
    '& $GodotPath --version 2>&1'
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

function ConvertTo-ComparableCommandName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [AllowEmptyString()]
        [string]$Name
    )

    # Spelling, not identity. `dotnet`, `dotnet.exe`, `DotNet.exe` and a full
    # path ending in dotnet.exe are the same program, and a model that keys on
    # the literal text would let two of those four walk past the APPDATA rule -
    # the same brittleness Issue #71 was opened about, arriving inside its own
    # fix. PowerShell's -eq is already case-insensitive; the suffix and the
    # directory are not.
    #
    # This deliberately does not resolve variables. It no longer needs to: a
    # -FilePath that is not a string literal is refused outright by
    # Get-ProgramNameFindings, so an unreadable spelling is a question rather
    # than a silence (Issue #102, item 6).
    if ([string]::IsNullOrWhiteSpace($Name)) {
        return ""
    }

    $trimmed = $Name.Trim().Trim('"', "'")
    try {
        $leaf = [IO.Path]::GetFileName($trimmed)
    }
    catch {
        $leaf = $trimmed
    }
    if ([string]::IsNullOrEmpty($leaf)) {
        $leaf = $trimmed
    }
    if ($leaf.EndsWith(".exe", [StringComparison]::OrdinalIgnoreCase)) {
        $leaf = $leaf.Substring(0, $leaf.Length - 4)
    }

    return $leaf.ToLowerInvariant()
}

function Get-CommandFilePath {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Command
    )

    # Three answers, not two: no -FilePath at all, a -FilePath spelled as a
    # string literal, and a -FilePath the AST cannot read. The third used to be
    # indistinguishable from the first, which is how a program name behind a
    # variable became invisible to the APPDATA model.
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
            return [pscustomobject]@{
                Present = $true
                Literal = $true
                Value = $value.Value
                Text = ($value.Extent.Text -replace '\s+', ' ').Trim()
            }
        }

        return [pscustomobject]@{
            Present = $true
            Literal = $false
            Value = $null
            Text = $(if ($null -eq $value) { "" } else { ($value.Extent.Text -replace '\s+', ' ').Trim() })
        }
    }

    return [pscustomobject]@{
        Present = $false
        Literal = $false
        Value = $null
        Text = ""
    }
}

function Resolve-DotSourcedFile {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Command,

        [Parameter(Mandatory = $true)]
        [string]$ContainingFile
    )

    # Only two spellings are resolved, both of them literal: `. "Name.ps1"` and
    # `. (Join-Path $PSScriptRoot "Name.ps1")`. Anything else returns nothing and
    # falls through to the dynamic-invocation rule, which reports it. That is the
    # fail-closed direction on purpose: a file this guard cannot name is a file
    # it cannot police, and silence there would rebuild the hole Issue #102 named.
    if ($Command.InvocationOperator -ne [Management.Automation.Language.TokenKind]::Dot) {
        return $null
    }

    $elements = @($Command.CommandElements)
    if ($elements.Count -eq 0) {
        return $null
    }

    $leaf = $null
    $first = $elements[0]
    if ($first -is [Management.Automation.Language.StringConstantExpressionAst]) {
        $leaf = $first.Value
    }
    elseif ($first -is [Management.Automation.Language.ParenExpressionAst]) {
        $inner = $first.Find({
            param($node)
            $node -is [Management.Automation.Language.CommandAst]
        }, $true)
        if ($null -ne $inner -and $inner.GetCommandName() -eq "Join-Path") {
            $innerElements = @($inner.CommandElements)
            if ($innerElements.Count -eq 3 -and
                $innerElements[1] -is [Management.Automation.Language.VariableExpressionAst] -and
                $innerElements[1].VariablePath.UserPath -eq "PSScriptRoot" -and
                $innerElements[2] -is [Management.Automation.Language.StringConstantExpressionAst]) {
                $leaf = $innerElements[2].Value
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($leaf)) {
        return $null
    }

    return [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ContainingFile) $leaf))
}

function Get-ParsedScript {
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

    return [pscustomobject]@{
        Path = $Path
        Ast = $ast
    }
}

function Get-VerifyStructure {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $mainPath = [IO.Path]::GetFullPath($Path)
    $findings = @()
    $functions = @{}
    $commands = @()
    $files = @()

    # Breadth first over the dot-source graph. verify.ps1 is a script, not a
    # module, so its run setup is spread across the files it dot-sources; parsing
    # only the entry point is what let PR #97 add two allowlisted names pointing
    # into a file nothing ever read (Issue #102, item 1).
    $visited = @{}
    $parsedAsts = @{}
    $pending = New-Object Collections.Generic.Queue[string]
    $pending.Enqueue($mainPath)

    while ($pending.Count -gt 0) {
        $filePath = $pending.Dequeue()
        $key = $filePath.ToLowerInvariant()
        if ($visited.Contains($key)) {
            continue
        }
        $visited[$key] = $true

        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            $findings += "'$filePath' is dot-sourced but does not exist, so its contents are never checked"
            continue
        }

        $parsed = Get-ParsedScript -Path $filePath
        $parsedAsts[$key] = $parsed.Ast
        $files += $filePath
        $isMain = ($filePath -eq $mainPath)

        foreach ($function in $parsed.Ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst]
        }, $true)) {
            if ($functions.Contains($function.Name)) {
                $findings += (
                    "function $($function.Name) is defined in both " +
                    "'$($functions[$function.Name].File)' and '$filePath'; this guard would " +
                    "model whichever body it saw first, which is not the one that runs")
                continue
            }
            $functions[$function.Name] = [pscustomobject]@{
                Name = $function.Name
                File = $filePath
                StartOffset = $function.Extent.StartOffset
                EndOffset = $function.Extent.EndOffset
                IsPrerequisite = ($function.Name -like "Initialize-*")
                IsMain = $isMain
            }
        }

        foreach ($command in $parsed.Ast.FindAll({
            param($node)
            $node -is [Management.Automation.Language.CommandAst]
        }, $true)) {
            $commandName = $command.GetCommandName()
            $filePathArgument = Get-CommandFilePath -Command $command
            $commands += [pscustomobject]@{
                Name = $commandName
                File = $filePath
                Text = ($command.Extent.Text -replace '\s+', ' ').Trim()
                Line = $command.Extent.StartLineNumber
                StartOffset = $command.Extent.StartOffset
                EndOffset = $command.Extent.EndOffset
                FilePathArgument = $filePathArgument
                ComparableName = (ConvertTo-ComparableCommandName -Name $commandName)
                ComparableFilePath = (ConvertTo-ComparableCommandName -Name $filePathArgument.Value)
            }

            $dotSourced = Resolve-DotSourcedFile -Command $command -ContainingFile $filePath
            if ($null -ne $dotSourced) {
                $pending.Enqueue($dotSourced)
            }
        }
    }

    $commands = @($commands | Sort-Object -Property File, StartOffset)

    $functionsByFile = @{}
    foreach ($name in $functions.Keys) {
        $function = $functions[$name]
        if (-not $functionsByFile.Contains($function.File)) {
            $functionsByFile[$function.File] = @()
        }
        $functionsByFile[$function.File] += $function
    }

    $mainAst = $parsedAsts[$mainPath.ToLowerInvariant()]
    if ($null -eq $mainAst) {
        throw "'$mainPath' could not be parsed, so nothing about the stages can be checked."
    }

    $catalogAssignment = $mainAst.Find({
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
            File = $mainPath
            StartOffset = $bodyBlock.Extent.StartOffset
            EndOffset = $bodyBlock.Extent.EndOffset
        }
    }

    if ($stages.Count -lt 2) {
        throw "verify.ps1 declares $($stages.Count) stage(s); staging exists to split the run, not to rename it."
    }

    $stageLoop = $mainAst.Find({
        param($node)
        $node -is [Management.Automation.Language.ForEachStatementAst] -and
        $node.Condition.Extent.Text -match 'selectedStages'
    }, $true)
    if ($null -eq $stageLoop) {
        throw "verify.ps1 no longer loops over the selected stages, so nothing runs them."
    }

    return [pscustomobject]@{
        Path = $mainPath
        Files = @($files)
        Stages = @($stages)
        Functions = $functions
        Commands = $commands
        StageLoopStartOffset = $stageLoop.Extent.StartOffset
        StructureFindings = @($findings)
        FunctionsByFile = $functionsByFile
        # Simulating 45 selections revisits the same two dozen scopes over and
        # over. Without this the guard spends ten seconds re-filtering the same
        # command list, and the cheapest stage in the run stops being cheap.
        RangeCache = @{}
        # Same reason, for the "which scope is this command in" question: it is
        # asked once per command per rule, and the answer never changes.
        ZoneCache = @{}
    }
}

function Get-CommandsInRange {
    [CmdletBinding()]
    [OutputType([object[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [string]$File,

        [Parameter(Mandatory = $true)]
        [int]$StartOffset,

        [Parameter(Mandatory = $true)]
        [int]$EndOffset
    )

    $key = "$File|$StartOffset-$EndOffset"
    if (-not $Structure.RangeCache.Contains($key)) {
        $Structure.RangeCache[$key] = @($Structure.Commands | Where-Object {
            $_.File -eq $File -and
            $_.StartOffset -ge $StartOffset -and $_.EndOffset -le $EndOffset
        })
    }

    return @($Structure.RangeCache[$key])
}

function Get-ReachableFunctions {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Roots
    )

    $reachable = @{}
    $pending = New-Object Collections.Generic.Queue[string]
    foreach ($root in $Roots) {
        if ($Structure.Functions.Contains($root)) {
            $pending.Enqueue($root)
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
                -File $function.File `
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

function Get-StageReachableFunctions {
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
    $roots = @()
    foreach ($stage in $Structure.Stages) {
        foreach ($command in (Get-CommandsInRange -Structure $Structure `
                -File $stage.File `
                -StartOffset $stage.StartOffset -EndOffset $stage.EndOffset)) {
            if (-not [string]::IsNullOrEmpty($command.Name) -and
                $Structure.Functions.Contains($command.Name)) {
                $roots += $command.Name
            }
        }
    }

    return (Get-ReachableFunctions -Structure $Structure -Roots $roots)
}

function Get-RunSetupFunctions {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedCommands
    )

    # Run setup is whatever the allowlist lets the top level call and no stage can
    # reach: the temporary-directory preflight, the engine resolution, the NuGet
    # profile and the cleanup. Those bodies mostly live in dot-sourced files, so
    # until Issue #102 nothing looked inside them at all.
    #
    # A run-setup function that reaches a stage-reachable one drops out of this
    # set on purpose. Its body would then be exempt from every rule, which is the
    # hole this closes rather than a shortcut through it.
    $roots = @($AllowedCommands | Where-Object {
        $Structure.Functions.Contains($_) -and -not $StageReachable.Contains($_)
    })

    $closure = Get-ReachableFunctions -Structure $Structure -Roots $roots
    $runSetup = @{}
    foreach ($name in $closure.Keys) {
        if (-not $StageReachable.Contains($name)) {
            $runSetup[$name] = $true
        }
    }

    return $runSetup
}

function Get-EnclosingFunction {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [object]$Command
    )

    if (-not $Structure.FunctionsByFile.Contains($Command.File)) {
        return $null
    }

    $innermost = $null
    foreach ($function in $Structure.FunctionsByFile[$Command.File]) {
        if ($Command.StartOffset -lt $function.StartOffset -or
            $Command.EndOffset -gt $function.EndOffset) {
            continue
        }
        if ($null -eq $innermost -or $function.StartOffset -gt $innermost.StartOffset) {
            $innermost = $function
        }
    }

    return $innermost
}

function Get-CommandZone {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [object]$Command,

        [Parameter(Mandatory = $true)]
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [hashtable]$RunSetup
    )

    # Four zones, and the fourth is why dot-sourcing the files is safe. A helper
    # in GodotTools.ps1 or HudVerification.ps1 that verification never reaches
    # belongs to another consumer - update-golden-ui.ps1, run-game.ps1 - and
    # policing it here would report their code as verification's problem.
    $cacheKey = "$($Command.File)|$($Command.StartOffset)-$($Command.EndOffset)"
    if ($Structure.ZoneCache.Contains($cacheKey)) {
        return [string]$Structure.ZoneCache[$cacheKey]
    }

    $zone = Get-CommandZoneCore `
        -Structure $Structure `
        -Command $Command `
        -StageReachable $StageReachable `
        -RunSetup $RunSetup
    $Structure.ZoneCache[$cacheKey] = $zone
    return $zone
}

function Get-CommandZoneCore {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [object]$Command,

        [Parameter(Mandatory = $true)]
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [hashtable]$RunSetup
    )

    $enclosing = Get-EnclosingFunction -Structure $Structure -Command $Command
    if ($null -ne $enclosing) {
        if ($StageReachable.Contains($enclosing.Name)) {
            return "stage"
        }
        if ($RunSetup.Contains($enclosing.Name)) {
            return "run-setup"
        }
        if ($enclosing.IsMain) {
            return "outside"
        }
        return "foreign"
    }

    foreach ($stage in $Structure.Stages) {
        if ($Command.File -eq $stage.File -and
            $Command.StartOffset -ge $stage.StartOffset -and
            $Command.EndOffset -le $stage.EndOffset) {
            return "stage"
        }
    }

    return "outside"
}

function Get-StrayCheckFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [hashtable]$RunSetup,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedCommands,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedInRunSetup,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$AllowedDynamic
    )

    $findings = @()
    foreach ($command in $Structure.Commands) {
        $zone = Get-CommandZone `
            -Structure $Structure `
            -Command $command `
            -StageReachable $StageReachable `
            -RunSetup $RunSetup
        if ($zone -eq "stage" -or $zone -eq "foreign") {
            continue
        }

        $where = $(if ($command.File -eq $Structure.Path) {
            "line $($command.Line)"
        } else {
            "$([IO.Path]::GetFileName($command.File)) line $($command.Line)"
        })

        if ([string]::IsNullOrEmpty($command.Name)) {
            if ($AllowedDynamic -notcontains $command.Text) {
                $findings += (
                    "'$($command.Text)' at $where invokes something " +
                    "through a variable outside every stage")
            }
            continue
        }

        if ($zone -eq "run-setup") {
            # The exit named at the top of this file. A function inside the
            # closure is allowed under any name because its own body is policed
            # next - so admission here is by reachability, not by permission,
            # and a check whose body is only allowed plumbing survives it. That
            # is deferred item 2, not a defect in this branch.
            if ($RunSetup.Contains($command.Name)) {
                continue
            }
            if ($AllowedCommands -notcontains $command.Name -and
                $AllowedInRunSetup -notcontains $command.Name) {
                $findings += (
                    "'$($command.Name)' at $where runs inside run setup, which " +
                    "happens outside every stage")
            }
            continue
        }

        if ($AllowedCommands -notcontains $command.Name) {
            $findings += "'$($command.Name)' at $where runs outside every stage"
        }
    }

    return @($findings)
}

function Get-ProgramNameFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Structure,

        [Parameter(Mandatory = $true)]
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [hashtable]$RunSetup
    )

    # The APPDATA model reads -FilePath as a string. A -FilePath it cannot read
    # is not evidence of compliance, so it is refused rather than ignored: this
    # is the one place the model was made to fail closed (Issue #102, item 6).
    # Every -FilePath in verify.ps1 is a literal today, so the rule costs nothing
    # to adopt and only ever fires on something new.
    $findings = @()
    foreach ($command in $Structure.Commands) {
        if (-not $command.FilePathArgument.Present -or $command.FilePathArgument.Literal) {
            continue
        }

        $zone = Get-CommandZone `
            -Structure $Structure `
            -Command $command `
            -StageReachable $StageReachable `
            -RunSetup $RunSetup
        if ($zone -eq "foreign") {
            continue
        }

        $where = $(if ($command.File -eq $Structure.Path) {
            "line $($command.Line)"
        } else {
            "$([IO.Path]::GetFileName($command.File)) line $($command.Line)"
        })
        $spelling = $(if ([string]::IsNullOrWhiteSpace($command.FilePathArgument.Text)) {
            "nothing at all"
        } else {
            "'$($command.FilePathArgument.Text)'"
        })

        $findings += (
            "'$($command.Name)' at $where passes $spelling as -FilePath. The " +
            "APPDATA order model reads that value as text, so a program named " +
            "anywhere but in a string literal is invisible to it; spell the " +
            "program out or move the call into a stage that owns it")
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
        [string]$File,

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
    $comparableDotnet = ConvertTo-ComparableCommandName -Name $DotnetCommand
    $comparableSwitch = ConvertTo-ComparableCommandName -Name $ProfileSwitchCommand

    $events = @()
    foreach ($command in (Get-CommandsInRange -Structure $Structure `
            -File $File -StartOffset $StartOffset -EndOffset $EndOffset)) {
        $name = $command.Name
        if ([string]::IsNullOrEmpty($name)) {
            continue
        }

        if ($command.ComparableName -eq $comparableDotnet -or
            $command.ComparableFilePath -eq $comparableDotnet) {
            $events += [pscustomobject]@{ Kind = "dotnet"; Line = $command.Line; Name = $name }
            continue
        }

        if ($command.ComparableName -eq $comparableSwitch) {
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
            -File $function.File `
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
                -File $stage.File `
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

    # Offsets only compare inside one file, and the stage loop this measures
    # against is in verify.ps1, so run setup is looked for there and nowhere else.
    $findings = @()
    $previousName = $null
    $previousOffset = -1

    foreach ($name in $Sequence) {
        $calls = @($Structure.Commands | Where-Object {
            $_.File -eq $Structure.Path -and $_.Name -eq $name
        })
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
        [hashtable]$StageReachable,

        [Parameter(Mandatory = $true)]
        [hashtable]$RunSetup
    )

    # Run setup counts as a way to run something. Before Issue #102 this rule
    # only knew about stages, which is why it could not be applied to the
    # dot-sourced Initialize-* names at all: Initialize-VerificationTemporaryRoot
    # is reached from the preflight and from nowhere else, and calling that dead
    # code would have been wrong.
    #
    # A prerequisite defined in a dot-sourced file that verification never
    # reaches is deliberately not reported: it belongs to another consumer of
    # that file, and this guard does not own their code.
    $findings = @()
    foreach ($name in @($Structure.Functions.Keys | Sort-Object)) {
        $function = $Structure.Functions[$name]
        if (-not $function.IsPrerequisite) {
            continue
        }
        if ($StageReachable.Contains($name) -or $RunSetup.Contains($name)) {
            continue
        }
        if (-not $function.IsMain) {
            continue
        }
        $findings += (
            "prerequisite $name is not reachable from any stage or from run " +
            "setup, so nothing can ever run it and no stage is honest about needing it")
    }

    return @($findings)
}

function Get-VerifyAnalysis {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $structure = Get-VerifyStructure -Path $Path
    $stageReachable = Get-StageReachableFunctions -Structure $structure
    $runSetup = Get-RunSetupFunctions `
        -Structure $structure `
        -StageReachable $stageReachable `
        -AllowedCommands $allowedOutsideStages

    return [pscustomobject]@{
        Structure = $structure
        StageReachable = $stageReachable
        RunSetup = $runSetup
    }
}

function Get-VerifyStructureFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [string]$Path,

        [object]$Analysis
    )

    if ($null -eq $Analysis) {
        $Analysis = Get-VerifyAnalysis -Path $Path
    }
    $structure = $Analysis.Structure
    $stageReachable = $Analysis.StageReachable
    $runSetup = $Analysis.RunSetup

    $findings = @()
    $findings += @($structure.StructureFindings)
    $findings += @(Get-StrayCheckFindings `
        -Structure $structure `
        -StageReachable $stageReachable `
        -RunSetup $runSetup `
        -AllowedCommands $allowedOutsideStages `
        -AllowedInRunSetup $allowedInsideRunSetup `
        -AllowedDynamic $allowedDynamicInvocations)
    $findings += @(Get-ProgramNameFindings `
        -Structure $structure `
        -StageReachable $stageReachable `
        -RunSetup $runSetup)
    $findings += @(Get-PrerequisiteFindings `
        -Structure $structure `
        -StageReachable $stageReachable `
        -RunSetup $runSetup)
    $findings += @(Get-PreflightOrderFindings -Structure $structure -Sequence $preflightSequence)
    $findings += @(Get-AppDataOrderFindings `
        -Structure $structure `
        -ProfileSwitchCommand $profileSwitchCommand `
        -DotnetCommand $dotnetCommand)

    return @($findings)
}

function Get-AnalysedScriptSurface {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Analysis
    )

    # What the run actually reports about itself: which files were parsed and
    # which functions the guard treats as run setup. Both used to be invisible,
    # and the second one is the answer to "does the allowlist still point at
    # something this guard reads".
    return [pscustomobject]@{
        Files = @($Analysis.Structure.Files | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object)
        FilePaths = @($Analysis.Structure.Files)
        RunSetupFunctions = @($Analysis.RunSetup.Keys | Sort-Object)
        StageReachableFunctions = @($Analysis.StageReachable.Keys | Sort-Object)
    }
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

function New-MutatedTree {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$SourceFiles,

        [Parameter(Mandatory = $true)]
        [string]$MainFileName,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$TargetFileName,

        [string]$Find,

        [string]$Replace,

        [string]$Append
    )

    # The whole set is copied, not just verify.ps1, because the guard now follows
    # dot-source lines: a lone copy would resolve GodotTools.ps1 to a file that is
    # not there, and every negative case would "pass" on a missing file instead of
    # on the mutation.
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    $mutatedPath = $null
    foreach ($source in $SourceFiles) {
        $leaf = [IO.Path]::GetFileName($source)
        $copy = Join-Path $Destination $leaf
        $text = [IO.File]::ReadAllText($source)

        if ($leaf -eq $TargetFileName) {
            $original = $text
            if (-not [string]::IsNullOrEmpty($Find)) {
                # A mutation that silently stops applying is a negative test that
                # passes for the wrong reason, so a missing or ambiguous anchor
                # fails loudly.
                $occurrences = ([regex]::Matches($text, [regex]::Escape($Find))).Count
                if ($occurrences -ne 1) {
                    throw (
                        "The negative case '$Name' anchors on text that appears " +
                        "$occurrences time(s) in $leaf; it has to appear exactly once. " +
                        "Update the anchor, do not delete the case.")
                }
                $text = $text.Replace($Find, $Replace)
            }
            if (-not [string]::IsNullOrEmpty($Append)) {
                $text = $text + $Append
            }
            if ($text -eq $original) {
                throw "The negative case '$Name' did not change $leaf at all."
            }
            $mutatedPath = $copy
        }

        [IO.File]::WriteAllText($copy, $text, [Text.UTF8Encoding]::new($false))
    }

    if ($null -eq $mutatedPath) {
        throw (
            "The negative case '$Name' names '$TargetFileName', which verify.ps1 " +
            "does not dot-source; the case would have proven nothing.")
    }

    return (Join-Path $Destination $MainFileName)
}

# --- the real script and the real document ---------------------------------

$analysis = Get-VerifyAnalysis -Path $verifyScript
$structureFindings = @(Get-VerifyStructureFindings -Analysis $analysis)
if ($structureFindings.Count -gt 0) {
    throw (
        "verify.ps1 breaks the stage contract:" + [Environment]::NewLine + "  " +
        ($structureFindings -join ([Environment]::NewLine + "  ")) +
        [Environment]::NewLine +
        "A check belongs in a stage body. Run setup that genuinely cannot live in " +
        "one goes into `$allowedOutsideStages in scripts\test-verify-stages.ps1 " +
        "with the reason next to it - otherwise -Stage and -Skip misreport what ran.")
}

$surface = Get-AnalysedScriptSurface -Analysis $analysis

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

# --- the engine gate: only a selection that needs it may require it --------
#
# Issue #285. Before this, the preflight resolved the engine unconditionally,
# so `-Stage scripts` refused on a machine without Godot even though nothing
# in that stage's body calls dotnet or the engine - checked structurally
# above (its own Summary says "Dependency-free"). Measured in
# evidence/285-stage-engine-need.json: `scripts` is the only stage that
# reaches neither Initialize-SolutionRestore / Initialize-SolutionBuild
# (build, tests, mcp, and - through Initialize-ScenarioAssembly - sim and
# load) nor the Godot executable itself (through Initialize-GameHostBuild and
# Initialize-EngineRuntime - godot, ui, screenshots).
#
# A bogus -GodotPath makes every case below deterministic regardless of
# whether the machine running this test happens to have Godot on PATH or in
# $env:GODOT4_CONSOLE: Resolve-GodotExecutable rejects an explicit path that
# does not resolve to an executable before it ever looks at the environment.
#
# Independent review of PR #289 applied the three mutants the Issue requires
# (A: the unconditional resolve reinstated; B: the engine made optional for
# `godot` specifically; C: the stage name stripped from the refusal message)
# and found all three died on the *same* assertion below - the phrase check
# in a shared loop - which proved coverage existed but not that each
# mutation was caught for its own reason. The four checks below are ordered
# and scoped so each of A, B and C is the first one to fail for its own
# mutation:
#   1. `-Stage scripts` must succeed without the engine - the property A
#      removes (an unconditional resolve makes `scripts` stop being
#      engine-free at all, so this is the first thing to break).
#   2. an engine-requiring stage must refuse in *preflight*, not partway
#      through its body - the property B removes (`godot` stops refusing in
#      preflight and instead runs into its body, which then crashes on its
#      own unresolved $godot; checked by failedPhase and an empty
#      stagesExecuted, not by message wording, so a message-only mutation
#      cannot satisfy it by accident).
#   3. that preflight refusal must name the stage - the property C removes.
#      Deliberately last and separate from check 2: a bare substring match on
#      the stage name would still pass even with the naming wrapper deleted,
#      because the underlying Godot-missing message already contains the
#      word "Godot" - the engine's own name, not the stage's.
#   4. a *full*-scope refusal is byte-for-byte the original message, with no
#      "Stage(s) ... require" prefix ever. None of A, B or C touch this - it
#      is Finding 1 from that same review: the guarantee existed only as a
#      manual check in the PR body, so a future change that starts applying
#      the stage-naming prefix to a full-scope refusal too would go
#      unnoticed. The expected text is derived from the live
#      Resolve-GodotExecutable rather than a hand-copied string, so it cannot
#      drift from GodotTools.ps1 on its own.

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$bogusGodotPath = Join-Path $sandbox "definitely-not-a-godot-binary-285.exe"

function Get-VerifyRunResult {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    # Same pattern as Assert-VerifyRejects: the child's refusal goes to
    # stderr, which this session has to read as output, not as its own
    # terminating error.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $verifyScript @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output | Out-String)
    }
}

function Get-VerificationResultEvent {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    # Parsed, not pattern-matched: failedPhase/stagesExecuted/scope/reason are
    # read as structured fields so a check on one of them cannot be satisfied
    # by wording that merely looks right elsewhere in the output.
    $line = @($Text -split "\r?\n" | Where-Object {
        $_ -match '"event":"verification_result"'
    }) | Select-Object -Last 1
    if ($null -eq $line) {
        throw "No verification_result event in output: $Text"
    }

    return ($line | ConvertFrom-Json)
}

# All four checks below run only at the top level, never in a nested
# invocation of this file. Check 1 spawns a real `-Stage scripts` run, whose
# body invokes test-verify-stages.ps1 on the very same (possibly mutated)
# verify.ps1 - so if checks 2-4 were not also gated here, a mutation that
# breaks one of *them* would make that nested invocation fail too, cascading
# up through Invoke-Checked inside the scripts stage and surfacing as check 1
# going red instead of the check the mutation actually broke. Gating all four
# the same way keeps them independent: the nested invocation (guard already
# set) skips this whole section and only runs the rest of the file - the
# structural checks below still cover the mutated script fully, just not via
# a second, redundant pass through this section.
if ($env:DUNGEON_FORTRESS_SKIP_ENGINE_GATE_SMOKE -ne "1") {
    # --- check 1: `-Stage scripts` must succeed without the engine ---------
    $env:DUNGEON_FORTRESS_SKIP_ENGINE_GATE_SMOKE = "1"
    try {
        $scriptsResult = Get-VerifyRunResult -Arguments @(
            "-Stage", "scripts", "-TemporaryRoot", $sandbox, "-GodotPath", $bogusGodotPath
        )
    }
    finally {
        $env:DUNGEON_FORTRESS_SKIP_ENGINE_GATE_SMOKE = $null
    }
    if ($scriptsResult.ExitCode -ne 0) {
        throw (
            "verify.ps1 -Stage scripts refused with a bogus -GodotPath even " +
            "though its body never calls dotnet or Godot: $($scriptsResult.Text)")
    }
    $scriptsEvent = Get-VerificationResultEvent -Text $scriptsResult.Text
    if ([string]$scriptsEvent.status -ne "ok") {
        throw (
            "verify.ps1 -Stage scripts exited 0 but did not report a " +
            "successful verification_result: $($scriptsResult.Text)")
    }

    # --- checks 2 and 3: preflight refusal, then its wording ---------------
    foreach ($engineStage in @("godot", "build")) {
        $result = Get-VerifyRunResult -Arguments @(
            "-Stage", $engineStage, "-TemporaryRoot", $sandbox, "-GodotPath", $bogusGodotPath
        )
        if ($result.ExitCode -eq 0) {
            throw (
                "verify.ps1 -Stage $engineStage accepted a nonexistent -GodotPath " +
                "instead of refusing in preflight.")
        }

        $event = Get-VerificationResultEvent -Text $result.Text
        if ([string]$event.failedPhase -ne "preflight" -or @($event.stagesExecuted).Count -ne 0) {
            throw (
                "verify.ps1 -Stage $engineStage did not refuse in *preflight* - " +
                "failedPhase was '$($event.failedPhase)' and stagesExecuted was " +
                "[$($event.stagesExecuted -join ', ')]. An engine-requiring stage " +
                "that resolves the engine too late, or not at all, fails inside " +
                "the stage body instead, which is what this catches.")
        }

        $expectedPhrase = "Stage(s) $engineStage require"
        if ([string]$event.reason -notmatch [regex]::Escape($expectedPhrase)) {
            throw (
                "verify.ps1 -Stage $engineStage did not name the stage in its " +
                "refusal. Expected to find '$expectedPhrase'; got: $($event.reason)")
        }
    }

    # --- check 4: a full-scope refusal is byte-for-byte the original message
    $expectedFullScopeMessage = $null
    try {
        Resolve-GodotExecutable -ExplicitPath $bogusGodotPath | Out-Null
        throw (
            "Resolve-GodotExecutable unexpectedly succeeded against a bogus " +
            "path; the full-scope byte-for-byte check has nothing to compare " +
            "against.")
    }
    catch {
        $expectedFullScopeMessage = $_.Exception.Message
    }

    $fullScopeResult = Get-VerifyRunResult -Arguments @(
        "-TemporaryRoot", $sandbox, "-GodotPath", $bogusGodotPath
    )
    if ($fullScopeResult.ExitCode -eq 0) {
        throw "verify.ps1 (full scope, no -Stage) accepted a nonexistent -GodotPath instead of refusing in preflight."
    }
    $fullScopeEvent = Get-VerificationResultEvent -Text $fullScopeResult.Text
    if ([string]$fullScopeEvent.scope -ne "full") {
        throw (
            "verify.ps1 with no -Stage argument reported scope " +
            "'$($fullScopeEvent.scope)', not 'full'; the byte-for-byte check " +
            "needs a real full-scope run.")
    }
    if (-not [string]::Equals(
            [string]$fullScopeEvent.reason, $expectedFullScopeMessage, [StringComparison]::Ordinal)) {
        throw (
            "verify.ps1's full-scope refusal is not byte-for-byte the " +
            "original message.`nExpected: $expectedFullScopeMessage`n" +
            "Got:      $($fullScopeEvent.reason)")
    }
}

# --- stage output routing: raw dumps move to a file, failures stay loud ----
#
# Issue #284. Before this, Invoke-GodotChecked (and Invoke-GodotExpectedFailure,
# Invoke-Checked, Invoke-Scenario, and the golden-UI/frame-pacing helpers)
# printed every line of a stage's own work straight to Write-Host regardless
# of the outcome, which is why a full green run's stdout was 352330 bytes -
# evidence/284-stdout-volume.json measured 96% of that as exactly this dump,
# on calls that succeeded and were never read. The fix routes it through
# Write-VerifyDiagnostic in GodotTools.ps1: to the stage log file when
# verify.ps1 set $script:VerifyStageLogPath, to Write-Host otherwise. A
# failing call still writes its dump and its structured report with a plain,
# unconditional Write-Host, on purpose, so a stage that actually fails stays
# diagnosable from stdout without opening that file.
#
# This proves the routing itself, in-process against the real
# Invoke-GodotChecked, with no real Godot and no dotnet build: $GodotPath is
# a tiny PowerShell stub that prints fixed lines and exits with a fixed code,
# invoked in a child process exactly the way Invoke-GodotChecked invokes the
# real engine, and a *second* child process runs the stub run itself so this
# session's own stdout stays uncontaminated by whatever the call under test
# prints. That keeps this inside the dependency-free `scripts` stage.

$stageOutputSandbox = Join-Path $sandbox "stage-output-284"
New-Item -ItemType Directory -Force -Path $stageOutputSandbox | Out-Null

$stageOutputRunnerPath = Join-Path $stageOutputSandbox "runner.ps1"
[IO.File]::WriteAllText($stageOutputRunnerPath, @'
param(
    [Parameter(Mandatory = $true)]
    [string]$GodotToolsPath,

    [Parameter(Mandatory = $true)]
    [string]$StubPath,

    [Parameter(Mandatory = $true)]
    [string]$LogPath,

    [Parameter(Mandatory = $true)]
    [string]$PowerShellPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. $GodotToolsPath
$script:VerifyStageLogPath = $LogPath
Invoke-GodotChecked -GodotPath $PowerShellPath `
    -Arguments @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $StubPath) `
    -ExpectedSuccessEvent "godot_headless_smoke" | Out-Null
'@, [Text.UTF8Encoding]::new($false))

$stubGodotOkPath = Join-Path $stageOutputSandbox "stub-godot-ok.ps1"
[IO.File]::WriteAllText($stubGodotOkPath, @'
Write-Host (
    '{"event":"godot_headless_smoke","status":"ok","tick":1,"checksum":"stub-ok-284"}')
exit 0
'@, [Text.UTF8Encoding]::new($false))

$stubGodotErrorPath = Join-Path $stageOutputSandbox "stub-godot-error.ps1"
[IO.File]::WriteAllText($stubGodotErrorPath, @'
Write-Host (
    '{"event":"godot_headless_smoke","status":"ok","tick":1,"checksum":"stub-partial-284"}')
Write-Host "ERROR: stub engine failure line for issue 284 diagnostics test"
exit 1
'@, [Text.UTF8Encoding]::new($false))

$stagePowerShellPath = (Get-Command "powershell" -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1).Source
if ([string]::IsNullOrEmpty($stagePowerShellPath)) {
    throw "Cannot find 'powershell' on PATH; the stage-output-routing test needs it to stand in for Godot."
}
$godotToolsPathForStageOutputTest = Join-Path $repoRoot "scripts\GodotTools.ps1"

function Invoke-StageOutputRunner {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$StubPath,

        [Parameter(Mandatory = $true)]
        [string]$LogPath
    )

    # Same reason as Assert-VerifyRejects and Get-VerifyRunResult above: the
    # child's own failure goes to stderr, which this session has to read as
    # output, not as its own terminating error.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $stageOutputRunnerPath `
            -GodotToolsPath $godotToolsPathForStageOutputTest `
            -StubPath $StubPath `
            -LogPath $LogPath `
            -PowerShellPath $stagePowerShellPath 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Text = ($output | Out-String)
    }
}

# --- the success case: the dump and the compact summary both move to the file
$stageOutputOkLog = Join-Path $stageOutputSandbox "ok.log"
$stageOutputOkResult = Invoke-StageOutputRunner -StubPath $stubGodotOkPath -LogPath $stageOutputOkLog
if ($stageOutputOkResult.ExitCode -ne 0) {
    throw (
        "The stage-output-routing success case failed unexpectedly: " +
        $stageOutputOkResult.Text)
}
if ($stageOutputOkResult.Text -match [regex]::Escape("stub-ok-284")) {
    throw (
        "Invoke-GodotChecked printed its raw dump to stdout on a successful " +
        "call even though a stage log path was set. Issue #284's whole point " +
        "- a green run stays small - is not held: " + $stageOutputOkResult.Text)
}
if ($stageOutputOkResult.Text -match [regex]::Escape('"event":"godot_process_guard"')) {
    throw (
        "Invoke-GodotChecked printed its compact status event to stdout on a " +
        "successful call even though a stage log path was set: " +
        $stageOutputOkResult.Text)
}
if (-not (Test-Path -LiteralPath $stageOutputOkLog -PathType Leaf)) {
    throw "Invoke-GodotChecked did not write to the stage log path at all on success."
}
$stageOutputOkLogText = [IO.File]::ReadAllText($stageOutputOkLog)
if ($stageOutputOkLogText -notmatch [regex]::Escape("stub-ok-284") -or
    $stageOutputOkLogText -notmatch [regex]::Escape('"event":"godot_process_guard"')) {
    throw (
        "The stage log file is missing the raw dump or the compact status " +
        "event Invoke-GodotChecked was supposed to move there, so the " +
        "content did not just move - it disappeared: $stageOutputOkLogText")
}

# --- the failure case: stdout must stay diagnosable without opening the file
$stageOutputErrorLog = Join-Path $stageOutputSandbox "error.log"
$stageOutputErrorResult = Invoke-StageOutputRunner -StubPath $stubGodotErrorPath -LogPath $stageOutputErrorLog
if ($stageOutputErrorResult.ExitCode -eq 0) {
    throw (
        "The stage-output-routing failure case did not fail at all, so it " +
        "proves nothing about diagnostics on failure: " + $stageOutputErrorResult.Text)
}
if ($stageOutputErrorResult.Text -notmatch [regex]::Escape(
        "ERROR: stub engine failure line for issue 284 diagnostics test")) {
    throw (
        "A failing Invoke-GodotChecked call did not print the engine's own " +
        "ERROR: line to stdout, even though a stage log path was set. A stage " +
        "that fails has to stay diagnosable without opening the log file " +
        "(Issue #284): " + $stageOutputErrorResult.Text)
}
if ($stageOutputErrorResult.Text -notmatch [regex]::Escape('"status":"error"')) {
    throw (
        "A failing Invoke-GodotChecked call did not print its structured " +
        "godot_process_guard error report to stdout: " + $stageOutputErrorResult.Text)
}
# "stub-partial-284" only ever appears in the raw per-line dump, never in the
# compact godot_process_guard report (whose own firstEngineError field would
# still carry the ERROR: text above even if the raw dump itself were dropped,
# which is exactly the gap a weaker version of this check missed - the report
# alone could satisfy the two checks above without the dump ever running).
# Requiring this line specifically pins the dump itself, not just one field
# a compact report happens to duplicate it into.
if ($stageOutputErrorResult.Text -notmatch [regex]::Escape("stub-partial-284")) {
    throw (
        "A failing Invoke-GodotChecked call did not print its raw pre-failure " +
        "output (the stub's own godot_headless_smoke line) to stdout - only a " +
        "compact report would not be enough to diagnose a real failure " +
        "(Issue #284): " + $stageOutputErrorResult.Text)
}

# --- verification_result and its checksums must never be routed to the file
#
# A different failure mode from the two checks above on purpose: those prove
# Invoke-GodotChecked's own routing behaves correctly at runtime; this one is
# static, because the summary it protects is assembled once, at the very end
# of a *full* run, and a full run needs a real Godot engine and a real
# solution build - not available inside the dependency-free `scripts` stage
# this test itself runs in.
$verifyScriptTextForOutputCheck = [IO.File]::ReadAllText($verifyScript)
if ($verifyScriptTextForOutputCheck -notmatch
        '\$summary\s*\|\s*ConvertTo-Json\s+-Compress\s*\|\s*Write-Host') {
    throw (
        "verify.ps1's final verification_result summary is no longer printed " +
        "with an unconditional Write-Host. It must never be routed to the " +
        "stage log file - a run's checksums have to reach stdout regardless " +
        "of the outcome (Issue #284).")
}
foreach ($requiredChecksumField in @(
        '$summary["deterministicChecksum"]',
        '$summary["changedSeedChecksum"]',
        '$summary["loadChecksum"]',
        '$summary["viewInvariantChecksum"]')) {
    if (-not $verifyScriptTextForOutputCheck.Contains($requiredChecksumField)) {
        throw (
            "verify.ps1 no longer assigns $requiredChecksumField on the " +
            "verification_result summary; a checksum went missing from " +
            "stdout (Issue #284).")
    }
}

# --- the guard against itself ----------------------------------------------

try {
    $mainFileName = [IO.Path]::GetFileName($verifyScript)
    $sourceFiles = @($surface.FilePaths)
    $originalText = [IO.File]::ReadAllText($verifyScript)
    $newline = if ($originalText.Contains("`r`n")) { "`r`n" } else { "`n" }

    # A guard nobody has watched fail is a guard nobody knows works. Every case
    # below is a change someone could plausibly make, applied to a copy of the
    # whole script set, and each one has to come back named in the findings.
    $negativeCases = @(
        [pscustomobject]@{
            Name = "check-outside-a-stage-under-a-new-name"
            Why = "a check outside every stage, under a name this guard has never seen"
            File = "verify.ps1"
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
            File = "verify.ps1"
            Find = '$scope = if ($notRunStages.Count -eq 0) { "full" } else { "partial" }'
            Replace = @(
                '& $strayCheck',
                '$scope = if ($notRunStages.Count -eq 0) { "full" } else { "partial" }'
            ) -join $newline
            Append = ""
            Expect = @('& $strayCheck')
        },
        [pscustomobject]@{
            # Proves exactly one thing: a name that is called from run setup and
            # defined nowhere is a finding. A name that is *also defined* in one
            # of these files joins the run-setup closure and is admitted - see
            # the exit described at the top of this file, and deferred item 2.
            Name = "check-inside-dot-sourced-run-setup"
            Why = "a check hidden in the body of a run-setup function in another file"
            File = "TemporaryRoot.ps1"
            Find = '    $selection = Resolve-VerificationTemporaryRoot -ExplicitPath $ExplicitPath'
            Replace = @(
                '    Assert-SomeNewInvariant -Path $ExplicitPath',
                '    $selection = Resolve-VerificationTemporaryRoot -ExplicitPath $ExplicitPath'
            ) -join $newline
            Append = ""
            Expect = @("Assert-SomeNewInvariant", "run setup")
        },
        [pscustomobject]@{
            Name = "check-at-the-top-level-of-a-module"
            Why = "a check that runs when a dot-sourced file is loaded"
            File = "GodotTools.ps1"
            Find = ""
            Replace = ""
            Append = @(
                '',
                'function Assert-ToolsetAvailable {',
                '    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot "verify.ps1") -PathType Leaf)) {',
                '        throw "verify.ps1 is missing."',
                '    }',
                '}',
                '',
                'Assert-ToolsetAvailable',
                ''
            ) -join $newline
            Expect = @("Assert-ToolsetAvailable", "GodotTools.ps1")
        },
        [pscustomobject]@{
            Name = "dot-source-target-through-a-variable"
            Why = "a dot-sourced file this guard cannot name and therefore cannot read"
            File = "verify.ps1"
            Find = '. (Join-Path $PSScriptRoot "GodotTools.ps1")'
            Replace = @(
                '$toolsModule = "GodotTools.ps1"',
                '. (Join-Path $PSScriptRoot $toolsModule)'
            ) -join $newline
            Append = ""
            Expect = @('through a variable')
        },
        [pscustomobject]@{
            Name = "program-name-through-a-variable"
            Why = "a program named by a variable, which the APPDATA model cannot read"
            File = "verify.ps1"
            Find = '            $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"'
            Replace = @(
                '            $program = "dotnet"',
                '            Invoke-Checked -FilePath $program -Arguments @("--version")',
                '            $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"'
            ) -join $newline
            Append = ""
            Expect = @("-FilePath", "string literal")
        },
        [pscustomobject]@{
            Name = "dotnet-in-a-stage-after-the-profile-switch"
            Why = "a stage that calls dotnet after APPDATA moved to the Godot profile"
            File = "verify.ps1"
            Find = '            $raidScreenshot = Join-Path $verifyRoot "prepared-raid.png"'
            Replace = @(
                '            Invoke-Checked -FilePath "dotnet" -Arguments @("--version")',
                '            $raidScreenshot = Join-Path $verifyRoot "prepared-raid.png"'
            ) -join $newline
            Append = ""
            Expect = @("screenshots", "APPDATA")
        },
        [pscustomobject]@{
            Name = "dotnet-after-the-switch-spelled-differently"
            Why = "the same late dotnet call written as a path with an .exe suffix"
            File = "verify.ps1"
            Find = '            $baselineRepeatScreenshot = Join-Path $verifyRoot "baseline-t1-repeat.png"'
            Replace = @(
                '            Invoke-Checked -FilePath "C:\Program Files\dotnet\DotNet.exe" -Arguments @("--version")',
                '            $baselineRepeatScreenshot = Join-Path $verifyRoot "baseline-t1-repeat.png"'
            ) -join $newline
            Append = ""
            Expect = @("screenshots", "APPDATA")
        },
        [pscustomobject]@{
            Name = "prerequisites-reordered-inside-a-stage"
            Why = "Initialize-EngineRuntime moved in front of Initialize-GameHostBuild"
            File = "verify.ps1"
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
            File = "verify.ps1"
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
            File = "verify.ps1"
            Find = '    $temporaryRootSelection = Initialize-VerificationTemporaryRoot -ExplicitPath $TemporaryRoot -RepositoryRoot $repoRoot'
            Replace = '    $temporaryRootSelection = [pscustomobject]@{ Path = $null; Source = $null; Owned = $false }'
            Append = ""
            Expect = @("Initialize-VerificationTemporaryRoot", "never called")
        }
    )

    foreach ($case in $negativeCases) {
        $copy = New-MutatedTree `
            -Name $case.Name `
            -SourceFiles $sourceFiles `
            -MainFileName $mainFileName `
            -Destination (Join-Path $sandbox $case.Name) `
            -TargetFileName $case.File `
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
    # the copy is broken rather than because the mutation was caught. The whole
    # set is copied, so it also proves the dot-source lines still resolve when
    # the tree lives somewhere other than scripts\.
    $untouchedRoot = Join-Path $sandbox "untouched"
    New-Item -ItemType Directory -Force -Path $untouchedRoot | Out-Null
    foreach ($source in $sourceFiles) {
        [IO.File]::WriteAllText(
            (Join-Path $untouchedRoot ([IO.Path]::GetFileName($source))),
            [IO.File]::ReadAllText($source),
            [Text.UTF8Encoding]::new($false))
    }
    $untouched = Join-Path $untouchedRoot $mainFileName
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
        analysedFiles = @($surface.Files)
        runSetupFunctions = @($surface.RunSetupFunctions)
        allowedOutsideStages = $allowedOutsideStages.Count
        allowedInsideRunSetup = $allowedInsideRunSetup.Count
        preflightSequence = $preflightSequence
        stageSelectionsChecked = $stageCount + ($stageCount * ($stageCount - 1) / 2)
        negativeCasesProven = @($negativeCases | ForEach-Object { $_.Name })
        documentationCasesProven = @("foreign-table-ignored", "dropped-row-caught")
        emptySelectionRejected = $true
        unknownStageRejected = $true
        stageOutputRoutingChecked = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-TemporaryItemBestEffort `
        -Path $sandbox `
        -Description "stage guard negative test sandbox" | Out-Null
}
