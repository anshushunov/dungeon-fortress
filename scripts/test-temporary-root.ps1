[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #89. A verification run depends on a temporary directory it can create
# in, write to and delete from, and until this test existed that dependency was
# invisible: a TEMP that refused deletion was reported as
# {"failedStage":"godot","reason":"'powershell' failed with exit code 1."}.
#
# Four claims are proven here, each with a real failure rather than a mock:
#   1. an unusable temporary directory is diagnosed by name and reason;
#   2. verify.ps1 refuses such a directory before it runs a single stage;
#   3. a cleanup that cannot delete its directory warns and returns, so a check
#      that already passed stays passed;
#   4. the probe decides by the same call the real cleanup makes.
#
# It needs no build, no engine and no network, and runs inside the `scripts`
# stage.

. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$verifyScript = Join-Path $repoRoot "scripts\verify.ps1"
$temporaryRootModule = Join-Path $repoRoot "scripts\TemporaryRoot.ps1"
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$sandbox = Join-Path $artifactsRoot ("temporary-root-test-" + [Guid]::NewGuid().ToString("N"))

# Claim 4 is the one this file used to leave to a comment, and a comment is not
# enough here for a specific reason. The delete below is blocked with an open
# file handle, and [IO.Directory]::Delete fails on an open handle just as
# Remove-Item does - so swapping the probe to the cheaper API would keep every
# test in this repository green while silently restoring the Issue #89 defect,
# because the two calls only disagree on the permissions of C:\WINDOWS\TEMP.
# Hence an assertion over the AST rather than more prose.
$requiredRemoveItemParameters = @("Recurse", "Force", "ErrorAction")
$decidedByRemoveItem = @("Get-TemporaryRootDiagnosis", "Remove-TemporaryItemBestEffort")

function Get-FirstDeletion {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$FunctionAst
    )

    # Every way this repository deletes a directory, in source order: the
    # cmdlet, and any .NET Delete call. The first one is the one whose result
    # the caller acts on.
    $deletions = @()
    foreach ($command in $FunctionAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true)) {
        if ($command.GetCommandName() -ne "Remove-Item") {
            continue
        }
        $deletions += [pscustomobject]@{
            Kind = "Remove-Item"
            Parameters = @($command.CommandElements |
                Where-Object { $_ -is [Management.Automation.Language.CommandParameterAst] } |
                ForEach-Object { $_.ParameterName })
            Offset = $command.Extent.StartOffset
            Line = $command.Extent.StartLineNumber
            Text = ($command.Extent.Text -replace '\s+', ' ').Trim()
        }
    }
    foreach ($invocation in $FunctionAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.InvokeMemberExpressionAst]
    }, $true)) {
        if ($invocation.Member.Extent.Text -ne "Delete") {
            continue
        }
        $deletions += [pscustomobject]@{
            Kind = "dotnet-delete"
            Parameters = @()
            Offset = $invocation.Extent.StartOffset
            Line = $invocation.Extent.StartLineNumber
            Text = ($invocation.Extent.Text -replace '\s+', ' ').Trim()
        }
    }

    $ordered = @($deletions | Sort-Object -Property Offset)
    if ($ordered.Count -eq 0) {
        return $null
    }

    return $ordered[0]
}

function Get-DeletionContractFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$FunctionNames,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$RequiredParameters
    )

    $parseErrors = $null
    $tokens = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "'$Path' does not parse: $(($parseErrors | ForEach-Object { $_.ToString() }) -join '; ')"
    }

    $findings = @()
    foreach ($name in $FunctionNames) {
        $function = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $name
        }, $true)
        if ($null -eq $function) {
            $findings += "$name is gone from TemporaryRoot.ps1"
            continue
        }

        $first = Get-FirstDeletion -FunctionAst $function
        if ($null -eq $first) {
            $findings += "$name no longer deletes anything"
            continue
        }

        if ($first.Kind -ne "Remove-Item") {
            $findings += (
                "$name decides on '$($first.Text)' at line $($first.Line). The " +
                "deciding delete has to be Remove-Item -Recurse -Force, the same " +
                "call the real cleanup makes: in C:\WINDOWS\TEMP the .NET Delete " +
                "succeeds where Remove-Item is denied, so this probe would accept " +
                "a directory every cleanup then fails on")
            continue
        }

        $missing = @($RequiredParameters | Where-Object { $first.Parameters -notcontains $_ })
        if ($missing.Count -gt 0) {
            $findings += (
                "$name deletes at line $($first.Line) without [$($missing -join ', ')]. " +
                "Without -Recurse and -Force the delete is not the one cleanup " +
                "performs, and without -ErrorAction Stop its failure is not even " +
                "raised, so the probe would report success on a directory it " +
                "could not remove")
        }
    }

    return @($findings)
}

New-Item -ItemType Directory -Force -Path $sandbox | Out-Null

try {
    # --- the deciding delete is the cleanup delete ---------------------------
    $contractFindings = @(Get-DeletionContractFindings `
        -Path $temporaryRootModule `
        -FunctionNames $decidedByRemoveItem `
        -RequiredParameters $requiredRemoveItemParameters)
    if ($contractFindings.Count -gt 0) {
        throw (
            "The probe and the real cleanup no longer delete the same way:" +
            [Environment]::NewLine + "  " +
            ($contractFindings -join ([Environment]::NewLine + "  ")))
    }

    # ...and the assertion is watched failing, on copies, so it is known to work.
    $moduleText = [IO.File]::ReadAllText($temporaryRootModule)
    $probeDeleteCall = 'Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction Stop'
    $cleanupDeleteCall = 'Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop'
    $contractCases = @(
        [pscustomobject]@{
            Name = "probe-decides-on-the-cheaper-api"
            Find = $probeDeleteCall
            Replace = '[IO.Directory]::Delete($probeDirectory, $true)'
            Expect = "Get-TemporaryRootDiagnosis"
        },
        [pscustomobject]@{
            Name = "probe-cannot-see-its-own-failure"
            Find = $probeDeleteCall
            Replace = 'Remove-Item -LiteralPath $probeDirectory -Recurse -Force'
            Expect = "ErrorAction"
        },
        [pscustomobject]@{
            Name = "cleanup-drifts-away-from-the-probe"
            Find = $cleanupDeleteCall
            Replace = '[IO.Directory]::Delete($Path, $true)'
            Expect = "Remove-TemporaryItemBestEffort"
        }
    )

    foreach ($case in $contractCases) {
        $occurrences = ([regex]::Matches($moduleText, [regex]::Escape($case.Find))).Count
        if ($occurrences -ne 1) {
            throw (
                "The negative case '$($case.Name)' anchors on text appearing " +
                "$occurrences time(s) in TemporaryRoot.ps1; it has to appear once. " +
                "Update the anchor, do not delete the case.")
        }

        $copy = Join-Path $sandbox ($case.Name + ".ps1")
        [IO.File]::WriteAllText(
            $copy,
            $moduleText.Replace($case.Find, $case.Replace),
            [Text.UTF8Encoding]::new($false))

        $caseFindings = @(Get-DeletionContractFindings `
            -Path $copy `
            -FunctionNames $decidedByRemoveItem `
            -RequiredParameters $requiredRemoveItemParameters)
        $matched = @($caseFindings | Where-Object { $_ -match [regex]::Escape($case.Expect) })
        if ($matched.Count -eq 0) {
            throw (
                "Swapping the deciding delete went unnoticed for case " +
                "'$($case.Name)'. Expected a finding mentioning " +
                "'$($case.Expect)'; got " +
                $(if ($caseFindings.Count -eq 0) { "nothing at all." } else { ($caseFindings -join "; ") }))
        }
    }

    $untouchedModule = Join-Path $sandbox "untouched-temporary-root.ps1"
    [IO.File]::WriteAllText($untouchedModule, $moduleText, [Text.UTF8Encoding]::new($false))
    $untouchedFindings = @(Get-DeletionContractFindings `
        -Path $untouchedModule `
        -FunctionNames $decidedByRemoveItem `
        -RequiredParameters $requiredRemoveItemParameters)
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "An unmodified copy of TemporaryRoot.ps1 was reported as broken, so " +
            "the cases above prove nothing: " + ($untouchedFindings -join "; "))
    }

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
        decidedByRemoveItem = $decidedByRemoveItem
        deletionContractCasesProven = @($contractCases | ForEach-Object { $_.Name })
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
