[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Stages exist so that an agent can verify what it changed without paying for the
# rest. That is only safe while two things hold, and neither is visible in a
# green run: every check belongs to a stage, and the documented stage table
# matches the script. This test holds both, costs no build and no engine, and
# runs inside the `scripts` stage.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$verifyScript = Join-Path $repoRoot "scripts\verify.ps1"
$environmentDoc = Join-Path $repoRoot "docs\engineering\ENVIRONMENT_SETUP.md"

# The stage table is the only table in that document whose first cell is a single
# lower-case name in backticks, so the rows are found without depending on a
# heading this file would have to spell in Russian.
$stageRowPattern = '^\|\s*`([a-z]+)`\s*\|'

# A check that runs outside a stage body cannot be selected, cannot be skipped
# and is not named in `stagesExecuted`, so a partial run would quietly claim to
# have done more or less than it did.
$policedCommands = @(
    "Invoke-Checked",
    "Invoke-Scenario",
    "Invoke-GodotChecked",
    "Invoke-GodotExpectedFailure",
    "Invoke-GoldenUiCapture",
    "Assert-FilesEqual",
    "Assert-SameNonEmptyValue",
    "Assert-GoldenUiFrame",
    "Assert-FramePacingIndependence",
    "Assert-GoblinSpriteDiagnostics",
    "Import-GodotProjectAssets"
)

$parseErrors = $null
$tokens = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($verifyScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "verify.ps1 does not parse: $(($parseErrors | ForEach-Object { $_.ToString() }) -join '; ')"
}

$catalogAssignment = $ast.Find({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left -is [Management.Automation.Language.VariableExpressionAst] -and
    $node.Left.VariablePath.UserPath -eq "stageCatalog"
}, $true)
if ($null -eq $catalogAssignment) {
    throw 'verify.ps1 no longer assigns $stageCatalog, so stage selection cannot be checked.'
}

$allowedRegions = @(
    [pscustomobject]@{
        Name = "stage catalog"
        Start = $catalogAssignment.Extent.StartOffset
        End = $catalogAssignment.Extent.EndOffset
    }
)

# Prerequisites are shared setup - restore, build, asset import - that several
# stages need. They are memoised and allowed to run checks, because skipping one
# would make the stage that needs it dishonest rather than cheaper.
$prerequisiteFunctions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -like "Initialize-*"
}, $true))
foreach ($function in $prerequisiteFunctions) {
    $allowedRegions += [pscustomobject]@{
        Name = "prerequisite $($function.Name)"
        Start = $function.Extent.StartOffset
        End = $function.Extent.EndOffset
    }
}

$strayChecks = @()
foreach ($command in @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true))) {
    $commandName = $command.GetCommandName()
    if ([string]::IsNullOrEmpty($commandName) -or $policedCommands -notcontains $commandName) {
        continue
    }

    $containing = @($allowedRegions | Where-Object {
        $command.Extent.StartOffset -ge $_.Start -and $command.Extent.EndOffset -le $_.End
    })
    if ($containing.Count -eq 0) {
        $strayChecks += "$commandName at line $($command.Extent.StartLineNumber)"
    }
}

if ($strayChecks.Count -gt 0) {
    throw (
        "verify.ps1 runs checks outside every stage body and prerequisite: " +
        ($strayChecks -join ", ") +
        ". Move them into a stage, otherwise -Stage and -Skip misreport what was verified."
    )
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
$documentedStages = @()
foreach ($line in [IO.File]::ReadAllLines($environmentDoc)) {
    if ($line -match $stageRowPattern) {
        $documentedStages += $Matches[1]
    }
}

if ($documentedStages.Count -eq 0) {
    throw "The stage table is missing from $environmentDoc, so an agent has nothing to choose a stage from."
}

$undocumented = @($stageNames | Where-Object { $documentedStages -notcontains $_ })
$stale = @($documentedStages | Where-Object { $stageNames -notcontains $_ })
if ($undocumented.Count -gt 0 -or $stale.Count -gt 0) {
    throw (
        "The stage table in ENVIRONMENT_SETUP.md disagrees with verify.ps1: " +
        "undocumented [$($undocumented -join ', ')], documented but absent [$($stale -join ', ')]."
    )
}

function Assert-VerifyRejects {
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

$firstStage = $stageNames[0]

Assert-VerifyRejects `
    -Arguments @("-Stage", "definitely-not-a-stage") `
    -Message "verify.ps1 accepted an unknown stage name instead of failing."

Assert-VerifyRejects `
    -Arguments @("-Stage", $firstStage, "-Skip", $firstStage) `
    -Message "verify.ps1 accepted an empty stage selection instead of failing."

[ordered]@{
    event = "verify_stages_test"
    status = "ok"
    stages = $stageNames
    documentedStages = $documentedStages.Count
    policedCommands = $policedCommands.Count
    prerequisites = @($prerequisiteFunctions | ForEach-Object { $_.Name })
    emptySelectionRejected = $true
    unknownStageRejected = $true
} | ConvertTo-Json -Compress | Write-Host
