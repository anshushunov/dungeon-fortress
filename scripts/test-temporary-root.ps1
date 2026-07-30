[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #89. A verification run depends on a temporary directory it can create
# in, write to and delete from, and until this test existed that dependency was
# invisible: a TEMP that refused deletion was reported as
# {"failedStage":"godot","reason":"'powershell' failed with exit code 1."}.
#
# Three claims are proven here, each with a real failure rather than a mock:
#   1. an unusable temporary directory is diagnosed by name and reason;
#   2. verify.ps1 refuses such a directory before it runs a single stage;
#   3. a cleanup that cannot delete its directory warns and returns, so a check
#      that already passed stays passed.
#
# It needs no build, no engine and no network, and runs inside the `scripts`
# stage.

. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$verifyScript = Join-Path $repoRoot "scripts\verify.ps1"
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$sandbox = Join-Path $artifactsRoot ("temporary-root-test-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

try {
    # --- resolution order ---------------------------------------------------
    # An override nobody can find is not an override, so the precedence is part
    # of the contract: parameter, then environment variable, then TMP/TEMP.
    $explicit = Resolve-VerificationTemporaryRoot -ExplicitPath $sandbox
    if ($explicit.Path -ne $sandbox -or $explicit.Source -ne "-TemporaryRoot") {
        throw "An explicit -TemporaryRoot was not the first choice: got '$($explicit.Path)' from '$($explicit.Source)'."
    }

    $previousVariable = $env:DUNGEON_FORTRESS_TEMP
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $env:DUNGEON_FORTRESS_TEMP = $sandbox
        $fromVariable = Resolve-VerificationTemporaryRoot
        if ($fromVariable.Path -ne $sandbox -or $fromVariable.Source -ne "`$env:DUNGEON_FORTRESS_TEMP") {
            throw "DUNGEON_FORTRESS_TEMP was not used: got '$($fromVariable.Path)' from '$($fromVariable.Source)'."
        }

        $overridden = Resolve-VerificationTemporaryRoot -ExplicitPath $artifactsRoot
        if ($overridden.Source -ne "-TemporaryRoot") {
            throw "-TemporaryRoot did not win over DUNGEON_FORTRESS_TEMP."
        }

        $env:DUNGEON_FORTRESS_TEMP = $null
        $fallback = Resolve-VerificationTemporaryRoot
        if ($fallback.Source -ne "TMP/TEMP") {
            throw "Without an override the run did not fall back to TMP/TEMP: got '$($fallback.Source)'."
        }

        # The override only helps if child processes and the engine see it, and
        # they read TMP and TEMP. Win32 GetTempPath prefers TMP, so both matter.
        $applied = Initialize-VerificationTemporaryRoot -ExplicitPath $sandbox
        if ($applied.Path -ne $sandbox) {
            throw "The applied temporary root is '$($applied.Path)' instead of '$sandbox'."
        }
        if ($env:TEMP -ne $sandbox -or $env:TMP -ne $sandbox) {
            throw "Applying the temporary root left TEMP='$env:TEMP' and TMP='$env:TMP' instead of '$sandbox'."
        }
        $seenByRuntime = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
            [IO.Path]::DirectorySeparatorChar)
        if ($seenByRuntime -ne $sandbox) {
            throw "GetTempPath still reports '$seenByRuntime' after the override was applied."
        }
    }
    finally {
        $env:DUNGEON_FORTRESS_TEMP = $previousVariable
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }

    # --- a usable directory is accepted -------------------------------------
    # The positive control matters as much as the refusals: a probe that always
    # complains would pass every negative test below and stop every run.
    $usableRoot = Join-Path $sandbox "usable"
    $usableDiagnosis = Get-TemporaryRootDiagnosis -Path $usableRoot
    if ($null -ne $usableDiagnosis) {
        throw "A writable temporary directory was rejected: $usableDiagnosis"
    }
    if (-not (Test-Path -LiteralPath $usableRoot -PathType Container)) {
        throw "The probe did not create the temporary directory it accepted."
    }
    $leftBehind = @(Get-ChildItem -LiteralPath $usableRoot -Force)
    if ($leftBehind.Count -ne 0) {
        throw "The probe left $($leftBehind.Count) item(s) behind in a directory it accepted."
    }

    # --- a file where a directory belongs -----------------------------------
    $fileRoot = Join-Path $sandbox "not-a-directory.txt"
    [IO.File]::WriteAllText($fileRoot, "x")
    $fileDiagnosis = Get-TemporaryRootDiagnosis -Path $fileRoot
    if ($null -eq $fileDiagnosis -or $fileDiagnosis -notmatch "is a file") {
        throw "A file was accepted as the temporary directory: '$fileDiagnosis'."
    }
    if ($fileDiagnosis -notmatch [regex]::Escape($fileRoot)) {
        throw "The diagnosis does not name the directory it rejected: '$fileDiagnosis'."
    }

    # --- created but not deletable ------------------------------------------
    # This is the reported incident. The permission itself cannot be revoked
    # portably from a test, so the delete is blocked the way Windows blocks it
    # anyway: an open handle with no sharing. What is under test is that the
    # probe performs the delete at all and reports the failure with the path.
    $undeletableRoot = Join-Path $sandbox "undeletable"
    $probeName = "verify-temp-probe-held-open"
    $heldDirectory = Join-Path $undeletableRoot $probeName
    New-Item -ItemType Directory -Force -Path $heldDirectory | Out-Null
    $heldFile = Join-Path $heldDirectory "held.bin"
    $heldStream = [IO.File]::Open(
        $heldFile,
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $undeletableDiagnosis = $null
    try {
        $undeletableDiagnosis = Get-TemporaryRootDiagnosis `
            -Path $undeletableRoot `
            -ProbeDirectoryName $probeName
    }
    finally {
        $heldStream.Dispose()
    }
    if ($null -eq $undeletableDiagnosis -or $undeletableDiagnosis -notmatch "could not be deleted") {
        throw "A temporary directory whose contents cannot be deleted was accepted: '$undeletableDiagnosis'."
    }
    if ($undeletableDiagnosis -notmatch [regex]::Escape($heldDirectory)) {
        throw "The diagnosis does not name the directory it could not delete: '$undeletableDiagnosis'."
    }

    # --- cleanup is best effort ---------------------------------------------
    $lockedRoot = Join-Path $sandbox "locked-cleanup"
    New-Item -ItemType Directory -Force -Path $lockedRoot | Out-Null
    $lockedStream = [IO.File]::Open(
        (Join-Path $lockedRoot "held.bin"),
        [IO.FileMode]::Create,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $cleanupOutput = @()
    try {
        # A throw here is the defect: the caller has already printed its result.
        # The information stream is merged in so the warning itself is asserted
        # rather than assumed, and stays visible as evidence.
        $cleanupOutput = @(Remove-TemporaryItemBestEffort `
            -Path $lockedRoot `
            -Description "locked cleanup probe" 6>&1)
    }
    catch {
        $lockedStream.Dispose()
        throw (
            "Cleanup threw instead of warning, which is exactly what turns a " +
            "finished check into a red run: $($_.Exception.Message)")
    }
    finally {
        $lockedStream.Dispose()
    }

    if ($cleanupOutput.Count -eq 0 -or $cleanupOutput[-1] -ne $false) {
        throw "Cleanup reported success for a directory that is still held open."
    }
    $warningLine = @($cleanupOutput | Where-Object {
        [string]$_ -match '"event":"temporary_cleanup"'
    }) | Select-Object -Last 1
    if ($null -eq $warningLine) {
        throw "Cleanup failed silently: no temporary_cleanup line was written."
    }
    Write-Host ([string]$warningLine)
    $warning = ([string]$warningLine | ConvertFrom-Json)
    if ([string]$warning.status -ne "warning" -or
        [string]$warning.path -ne $lockedRoot -or
        [string]::IsNullOrWhiteSpace([string]$warning.reason)) {
        throw "The cleanup warning does not name the path and the reason: $warningLine"
    }
    $cleanupWarned = $true
    if (-not (Remove-TemporaryItemBestEffort -Path $lockedRoot -Description "locked cleanup probe")) {
        throw "Cleanup failed on a directory that is no longer held open."
    }
    if (Test-Path -LiteralPath $lockedRoot) {
        throw "Cleanup reported success but the directory is still there."
    }

    # --- verify.ps1 refuses before any stage --------------------------------
    # End to end, through the real script, with the exit code and the structured
    # line an agent would actually read.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $rejectionOutput = & powershell -NoProfile -ExecutionPolicy Bypass `
            -File $verifyScript -TemporaryRoot $fileRoot 2>&1
        $rejectionExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($rejectionExitCode -eq 0) {
        throw "verify.ps1 accepted a temporary root that is a file."
    }

    $resultLine = $rejectionOutput | Where-Object { $_ -match '"event":"verification_result"' } |
        Select-Object -Last 1
    if ($null -eq $resultLine) {
        throw (
            "verify.ps1 refused the temporary root without a verification_result line, " +
            "so the refusal is only readable as an exit code.")
    }

    $result = ([string]$resultLine | ConvertFrom-Json)
    if ([string]$result.status -ne "error") {
        throw "verify.ps1 reported status '$($result.status)' for an unusable temporary root."
    }
    if ([string]$result.failedPhase -ne "preflight") {
        throw "The refusal was reported in phase '$($result.failedPhase)' instead of preflight."
    }
    if (@($result.stagesExecuted).Count -ne 0) {
        throw (
            "verify.ps1 ran $(@($result.stagesExecuted).Count) stage(s) before refusing the " +
            "temporary directory; the point of the preflight is that it refuses first.")
    }
    if ([string]$result.reason -notmatch [regex]::Escape($fileRoot)) {
        throw "The refusal does not name the directory it rejected: $($result.reason)"
    }
    if ([string]$result.reason -notmatch "-TemporaryRoot") {
        throw "The refusal does not say how to choose another directory: $($result.reason)"
    }

    [ordered]@{
        event = "temporary_root_test"
        status = "ok"
        resolutionOrder = @("-TemporaryRoot", "`$env:DUNGEON_FORTRESS_TEMP", "TMP/TEMP")
        usableRootAccepted = $true
        fileRejected = $true
        undeletableRejected = $true
        cleanupWarnedInsteadOfThrowing = $cleanupWarned
        preflightRejectedBeforeStages = $true
        rejectionExitCode = $rejectionExitCode
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-TemporaryItemBestEffort `
        -Path $sandbox `
        -Description "temporary root test sandbox" | Out-Null
}
