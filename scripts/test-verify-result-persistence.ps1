[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #427. Save-VerificationResult (scripts/VerifyResult.ps1) is the only
# thing standing between a full verify.ps1 run and the fate PR #425 measured:
# `finally` deletes $verifyRoot - and the stage-output.log inside it -
# unconditionally, on every outcome, so any checksum a PR wants to cite has
# to already be written somewhere else by the time cleanup runs. This test
# exercises that function directly, with no build, no engine and no real
# verify.ps1 run, and runs inside the `scripts` stage.
#
# Four claims are proven, each against real files on disk rather than a mock:
#   1. a green result (no -SourceStageLogPath) writes the JSON and leaves no
#      stage-output.log behind - the decision in the module comment, that a
#      green run's log has nothing worth keeping;
#   2. a red result (-SourceStageLogPath given) writes the JSON *and* copies
#      the log byte-for-byte to the durable path;
#   3. a green result following a red one in the same directory removes the
#      stale log - so a result that says "status":"ok" is never left sitting
#      next to a failure nobody asked about;
#   4. the results directory is created if it does not already exist,
#      including when its parent does not exist either - the first call any
#      worktree ever makes.
#
# A code change that breaks any one of these - for example dropping the
# WriteAllText call, or reversing the "copy on failure / delete otherwise"
# branch - fails the corresponding assertion below by turning it red; that is
# the mutant this file exists to catch (Issue #427 criterion 3), applied as an
# uncommitted, reverted edit to scripts/VerifyResult.ps1 rather than as a
# fixture, because the function itself is what has to misbehave.

. (Join-Path $PSScriptRoot "VerifyResult.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$sandbox = Join-Path $artifactsRoot ("verify-result-persistence-test-" + [Guid]::NewGuid().ToString("N"))

function Assert-FileContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Expected,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not [IO.File]::Exists($Path)) {
        throw "$Description : '$Path' does not exist."
    }
    $actual = [IO.File]::ReadAllText($Path)
    if ($actual -ne $Expected) {
        throw "$Description : '$Path' has unexpected content. Expected '$Expected', got '$actual'."
    }
}

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
try {
    # --- Claim 4: results directory does not exist yet, not even its parent.
    $resultsRoot = Join-Path $sandbox "nested\verify-results"
    $resultPath = Join-Path $resultsRoot "verify-result.json"
    $stageLogPath = Join-Path $resultsRoot "stage-output.log"

    if (Test-Path -LiteralPath $resultsRoot) {
        throw "Test setup bug: '$resultsRoot' already exists before the first call."
    }

    # --- Claim 1: a green result with no source log writes the JSON and
    # creates no log file.
    $greenJson = '{"event":"verification_result","status":"ok","scope":"full"}'
    Save-VerificationResult -ResultPath $resultPath -StageLogPath $stageLogPath -Json $greenJson
    Assert-FileContent -Path $resultPath -Expected $greenJson -Description "Green result"
    if ([IO.File]::Exists($stageLogPath)) {
        throw "A green result with no source log must not create '$stageLogPath'."
    }

    # --- Claim 2: a red result with a source log writes the JSON and copies
    # the log byte-for-byte.
    $sourceLogPath = Join-Path $sandbox "stage-output-source.log"
    $sourceLogContent = "--- stage godot: ...`nsome captured diagnostic line`n"
    [IO.File]::WriteAllText($sourceLogPath, $sourceLogContent)

    $redJson = '{"event":"verification_result","status":"error","failedStage":"godot"}'
    Save-VerificationResult `
        -ResultPath $resultPath `
        -StageLogPath $stageLogPath `
        -Json $redJson `
        -SourceStageLogPath $sourceLogPath
    Assert-FileContent -Path $resultPath -Expected $redJson -Description "Red result"
    Assert-FileContent -Path $stageLogPath -Expected $sourceLogContent -Description "Copied stage log"

    # Source untouched: the caller's own `finally` owns deleting it, not this
    # function.
    if (-not [IO.File]::Exists($sourceLogPath)) {
        throw "Save-VerificationResult must not delete its own -SourceStageLogPath ('$sourceLogPath')."
    }

    # --- Claim 3: a green result after a red one removes the stale log.
    $greenAgainJson = '{"event":"verification_result","status":"ok","scope":"partial"}'
    Save-VerificationResult -ResultPath $resultPath -StageLogPath $stageLogPath -Json $greenAgainJson
    Assert-FileContent -Path $resultPath -Expected $greenAgainJson -Description "Green result after a red one"
    if ([IO.File]::Exists($stageLogPath)) {
        throw (
            "A green result following a red one must remove the stale " +
            "'$stageLogPath', not leave it next to a `"status`":`"ok`" result.")
    }

    [ordered]@{
        event                     = "verify_result_persistence_test"
        status                    = "ok"
        resultsDirectoryCreated   = $true
        greenWritesNoLog          = $true
        redCopiesLogByteForByte   = $true
        greenAfterRedRemovesStale = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedSandbox = [IO.Path]::GetFullPath($sandbox)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedSandbox.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSandbox)) {
        Remove-Item -LiteralPath $resolvedSandbox -Recurse -Force
    }
}
