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
# Six claims are proven, each against real files (and, for 5-6, a real
# spawned process) rather than a mock:
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
#      worktree ever makes;
#   5. Save-VerificationResult really does throw when its results directory
#      is blocked (a file sits where a directory needs to be created) - the
#      realistic disk/permission failure claim 6 depends on, so claim 6 does
#      not silently pass because the fixture stopped reproducing a failure;
#   6. Publish-VerificationResult, called the way verify.ps1 calls it - in a
#      real child process, so a bug that actually crashes the host shows up
#      as a real non-zero exit rather than an exception this same session
#      happens to catch - never throws under that same forced failure, and
#      reports it truthfully as its own verification_result_file event
#      (status:error, with a reason), never status:ok. This is the review
#      round 2 finding (Issue #427): an earlier version called
#      Save-VerificationResult directly, before verify.ps1's own final
#      Write-Host of the checksums, so a save failure there would have
#      thrown into `catch`, where the same save was retried and failed
#      again - losing both the checksums and the structured error report to
#      an unrelated disk problem.
#
# A code change that breaks any of claims 1-4 - for example dropping the
# WriteAllText call, or reversing the "copy on failure / delete otherwise"
# branch - fails the corresponding assertion below by turning it red. A
# change that breaks claim 5 or 6 - for example Publish-VerificationResult
# losing its try/catch, or verify.ps1 going back to calling
# Save-VerificationResult directly - fails claim 5 or 6 instead. Either way
# that is the mutant this file exists to catch (Issue #427 criterion 3),
# applied as an uncommitted, reverted edit to scripts/VerifyResult.ps1 or
# scripts/verify.ps1 rather than as a fixture, because the code itself is
# what has to misbehave.

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

    # --- Claims 5-6: a forced save failure. Poisoning the results directory
    # with a file where Save-VerificationResult wants to create a directory
    # is the same real failure its own [IO.Directory]::CreateDirectory call
    # raises for a real full-disk/permissions problem - no mock needed.
    $forcedFailureRoot = Join-Path $sandbox "forced-failure"
    New-Item -ItemType Directory -Force -Path $forcedFailureRoot | Out-Null
    $blockedResultsDir = Join-Path $forcedFailureRoot "verify-results"
    [IO.File]::WriteAllText($blockedResultsDir, "a file sits where the results directory should be")
    $forcedFailureResultPath = Join-Path $blockedResultsDir "verify-result.json"
    $forcedFailureStageLogPath = Join-Path $blockedResultsDir "stage-output.log"

    # Claim 5: the underlying primitive really does throw for this fixture.
    $rawThrew = $false
    try {
        Save-VerificationResult -ResultPath $forcedFailureResultPath -StageLogPath $forcedFailureStageLogPath -Json '{"probe":true}'
    }
    catch {
        $rawThrew = $true
    }
    if (-not $rawThrew) {
        throw (
            "Save-VerificationResult did not throw when its results " +
            "directory is blocked by a file; the forced-failure fixture no " +
            "longer reproduces a real save error, so claim 6 below would " +
            "prove nothing.")
    }

    # Claim 6: Publish-VerificationResult, in a real spawned process, must
    # not throw and must report status:error with a reason - not silently
    # swallow the failure, and not claim status:ok.
    $publishRunnerPath = Join-Path $sandbox "publish-runner.ps1"
    [IO.File]::WriteAllText($publishRunnerPath, @'
param($ModulePath, $ResultPath, $StageLogPath, $Json)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. $ModulePath
Publish-VerificationResult -ResultPath $ResultPath -StageLogPath $StageLogPath -Json $Json
Write-Host "RUNNER_COMPLETED_WITHOUT_THROWING"
'@)
    $verifyResultModulePath = Join-Path $PSScriptRoot "VerifyResult.ps1"
    $publishOutput = @(& powershell -NoProfile -ExecutionPolicy Bypass -File $publishRunnerPath `
            -ModulePath $verifyResultModulePath `
            -ResultPath $forcedFailureResultPath `
            -StageLogPath $forcedFailureStageLogPath `
            -Json '{"event":"verification_result","status":"ok","probe":"claim6"}' 2>&1)
    $publishExitCode = $LASTEXITCODE
    $publishText = ($publishOutput | Out-String)
    if ($publishExitCode -ne 0) {
        throw (
            "Publish-VerificationResult's own call terminated the process " +
            "(exit $publishExitCode) instead of catching the save failure: $publishText")
    }
    if ($publishText -notmatch [regex]::Escape("RUNNER_COMPLETED_WITHOUT_THROWING")) {
        throw "The runner script did not reach its own last line, so Publish-VerificationResult must have thrown: $publishText"
    }
    if ($publishText -notmatch '"event":"verification_result_file"' -or
        $publishText -notmatch '"status":"error"') {
        throw "Publish-VerificationResult did not report status:error for a forced save failure: $publishText"
    }
    if ($publishText -match '"event":"verification_result_file"[^{}]*"status":"ok"') {
        throw "Publish-VerificationResult claimed status:ok despite the forced save failure: $publishText"
    }
    if ($publishText -notmatch '"reason":') {
        throw "Publish-VerificationResult's error event carries no reason field: $publishText"
    }

    [ordered]@{
        event                       = "verify_result_persistence_test"
        status                      = "ok"
        resultsDirectoryCreated     = $true
        greenWritesNoLog            = $true
        redCopiesLogByteForByte     = $true
        greenAfterRedRemovesStale   = $true
        rawSaveThrowsOnForcedFailure = $true
        publishCatchesAndReportsError = $true
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
