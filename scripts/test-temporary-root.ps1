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
#   4. the probe decides by the same call the real cleanup makes, spelled the
#      same way down to the value of -ErrorAction.
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
#
# -ErrorAction is part of that contract by *value*, not by name (Issue #102).
# The reason recorded here until then - "without it the failure is not even
# raised" - is not what a measurement says. Measured on Windows PowerShell
# 5.1.26100.8972, Remove-Item -Recurse -Force on a directory it cannot delete
# behaves in two different ways depending on why it cannot:
#
#   - the reported Issue #89 case, C:\WINDOWS\TEMP, where the account may create
#     but not delete: a Win32Exception "Access is denied" that reaches the caller
#     as a terminating error under *every* -ErrorAction value tried, including
#     Ignore, and under $ErrorActionPreference of both Stop and Continue. In that
#     environment the parameter changes nothing, which is what the review of
#     PR #97 measured;
#   - a file inside held open, which is the only mode a portable test can create
#     and the one this file uses below: an IOException that honours the
#     parameter. With Stop it is caught; with SilentlyContinue, Continue or
#     Ignore it is not, Get-TemporaryRootDiagnosis returns $null, and a directory
#     it just failed to delete is certified as usable.
#
# So the requirement is not stricter than necessary - it is necessary in the
# failure mode this repository can reproduce, and the value is the whole point.
# Omitting the parameter happens to work only while every caller keeps
# $ErrorActionPreference at "Stop"; measured with "Continue", the same held-open
# failure goes uncaught. The contract therefore pins the value.
$requiredRemoveItemParameters = @("Recurse", "Force")
$requiredErrorActionValue = "Stop"
$decidedByRemoveItem = @("Get-TemporaryRootDiagnosis", "Remove-TemporaryItemBestEffort")

# Not closed, and deliberately. A canonical Remove-Item -Recurse -Force
# -ErrorAction Stop on a throwaway path, placed in front of a real
# [IO.Directory]::Delete that the diagnosis is actually derived from, satisfies
# everything below: the contract pins the shape of the first deletion in source
# order, not that the answer comes from it. Closing that needs data flow, and
# writing it needs intent - drift does not produce a decoy. This guard is here
# to catch drift (Issue #102, item 4).
#
# One measured caveat, so nobody mistakes the tripwire for the contract. Writing
# the decoy means replacing the real delete, and the anchor of the negative case
# below then matches zero times, so the run does fail - with "anchors on text
# appearing 0 time(s) ... update the anchor". That message tells the reader to
# fix the anchor, not that the deciding delete moved. It is an accident of the
# anchor check, not a rule, and it should not be counted as coverage.

function Get-CommandParameterValues {
    [CmdletBinding()]
    [OutputType([hashtable])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Command
    )

    # Parameter name to the text of its value, for the parameters that have one.
    # A switch maps to $null. Both `-ErrorAction Stop` and `-ErrorAction:Stop`
    # are read; a value that is not a bare word or a string stays as its own
    # source text, which is enough to say "that is not Stop".
    $values = @{}
    $elements = @($Command.CommandElements)
    for ($index = 1; $index -lt $elements.Count; $index++) {
        $element = $elements[$index]
        if (-not ($element -is [Management.Automation.Language.CommandParameterAst])) {
            continue
        }

        $value = $element.Argument
        if ($null -eq $value -and
            $index + 1 -lt $elements.Count -and
            -not ($elements[$index + 1] -is [Management.Automation.Language.CommandParameterAst])) {
            $value = $elements[$index + 1]
            $index++
        }

        $values[$element.ParameterName] = $(if ($null -eq $value) {
            $null
        } else {
            $value.Extent.Text.Trim().Trim('"', "'")
        })
    }

    return $values
}

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
            ParameterValues = (Get-CommandParameterValues -Command $command)
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
            ParameterValues = @{}
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
        [string[]]$RequiredParameters,

        [Parameter(Mandatory = $true)]
        [string]$ErrorActionValue
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
                "performs, so the probe would be answering a different question " +
                "from the one that matters")
        }

        # By value, not by name. -ErrorAction SilentlyContinue used to satisfy
        # this contract while turning the deciding failure into a non-terminating
        # error nobody catches - measured, on a directory holding a file open,
        # Get-TemporaryRootDiagnosis then returns $null and accepts it
        # (Issue #102, item 5).
        if ($first.Parameters -notcontains "ErrorAction") {
            $findings += (
                "$name deletes at line $($first.Line) without -ErrorAction " +
                "$ErrorActionValue, so whether its failure is raised at all is " +
                "decided by whichever `$ErrorActionPreference the caller happens " +
                "to have set")
            continue
        }

        $actual = $first.ParameterValues["ErrorAction"]
        if ([string]$actual -ne $ErrorActionValue) {
            $findings += (
                "$name deletes at line $($first.Line) with -ErrorAction " +
                "'$actual' instead of '$ErrorActionValue'. Measured: with a file " +
                "held open inside, anything weaker than Stop leaves the failure " +
                "non-terminating, the catch never runs and the probe certifies a " +
                "directory it just failed to delete")
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
        -RequiredParameters $requiredRemoveItemParameters `
        -ErrorActionValue $requiredErrorActionValue)
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
            # The case Issue #102 opened: the parameter is there, the value is
            # not, and until now that satisfied the contract while breaking the
            # probe on the one failure mode a test can reproduce.
            Name = "probe-decides-with-a-weaker-erroraction"
            Find = $probeDeleteCall
            Replace = 'Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction SilentlyContinue'
            Expect = "SilentlyContinue"
        },
        [pscustomobject]@{
            Name = "cleanup-drifts-away-from-the-probe"
            Find = $cleanupDeleteCall
            Replace = '[IO.Directory]::Delete($Path, $true)'
            Expect = "Remove-TemporaryItemBestEffort"
        },
        [pscustomobject]@{
            Name = "cleanup-drifts-to-a-weaker-erroraction"
            Find = $cleanupDeleteCall
            Replace = 'Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Continue'
            Expect = "Continue"
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
            -RequiredParameters $requiredRemoveItemParameters `
            -ErrorActionValue $requiredErrorActionValue)
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
        -RequiredParameters $requiredRemoveItemParameters `
        -ErrorActionValue $requiredErrorActionValue)
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "An unmodified copy of TemporaryRoot.ps1 was reported as broken, so " +
            "the cases above prove nothing: " + ($untouchedFindings -join "; "))
    }

    # --- resolution order ---------------------------------------------------
    # An override nobody can find is not an override, so the precedence is part
    # of the contract: parameter, then environment variable, then this run's
    # own directory. Issue #302 removed TMP/TEMP from the chain entirely -
    # evidence/302-temp-contention.json measured it resolving to
    # C:\WINDOWS\TEMP on this machine, a directory this account can create in
    # but never delete from, so trusting it by default is the defect, not a
    # feature to keep testing.
    $explicit = Resolve-VerificationTemporaryRoot -ExplicitPath $sandbox -RepositoryRoot $repoRoot
    if ($explicit.Path -ne $sandbox -or $explicit.Source -ne "-TemporaryRoot" -or $explicit.Owned) {
        throw (
            "An explicit -TemporaryRoot was not the first choice, or was marked " +
            "Owned: got '$($explicit.Path)' from '$($explicit.Source)' " +
            "(owned=$($explicit.Owned)).")
    }

    $previousVariable = $env:DUNGEON_FORTRESS_TEMP
    $previousTemp = $env:TEMP
    $previousTmp = $env:TMP
    try {
        $env:DUNGEON_FORTRESS_TEMP = $sandbox
        $fromVariable = Resolve-VerificationTemporaryRoot -RepositoryRoot $repoRoot
        if ($fromVariable.Path -ne $sandbox -or
            $fromVariable.Source -ne "`$env:DUNGEON_FORTRESS_TEMP" -or
            $fromVariable.Owned) {
            throw (
                "DUNGEON_FORTRESS_TEMP was not used, or was marked Owned: got " +
                "'$($fromVariable.Path)' from '$($fromVariable.Source)' " +
                "(owned=$($fromVariable.Owned)).")
        }

        $overridden = Resolve-VerificationTemporaryRoot -ExplicitPath $artifactsRoot -RepositoryRoot $repoRoot
        if ($overridden.Source -ne "-TemporaryRoot") {
            throw "-TemporaryRoot did not win over DUNGEON_FORTRESS_TEMP."
        }

        $env:DUNGEON_FORTRESS_TEMP = $null

        # --- Issue #302, mutant A: the default is not the ambient TMP/TEMP ---
        # A sentinel TMP/TEMP proves this without depending on what this
        # particular machine's TMP/TEMP happens to be: the assertion has to
        # hold everywhere, not just on the machine C:\WINDOWS\TEMP was measured
        # on.
        $ambientSentinel = Join-Path $sandbox "ambient-sentinel-temp"
        New-Item -ItemType Directory -Force -Path $ambientSentinel | Out-Null
        $env:TEMP = $ambientSentinel
        $env:TMP = $ambientSentinel
        $ownDefault = Resolve-VerificationTemporaryRoot -RepositoryRoot $repoRoot
        if ($ownDefault.Source -eq "TMP/TEMP") {
            throw (
                "Without -TemporaryRoot or `$env:DUNGEON_FORTRESS_TEMP, resolution " +
                "fell back to the ambient TMP/TEMP - the Issue #302 defect: this " +
                "machine's TMP/TEMP is a directory this account cannot delete " +
                "from (evidence/302-temp-contention.json), and a shared machine " +
                "directory is exactly what two concurrent agents collide on.")
        }
        if ($ownDefault.Source -ne "own run directory") {
            throw "Without an override, resolution reported an unexpected source '$($ownDefault.Source)'."
        }
        if (-not $ownDefault.Owned) {
            throw "The own-directory default was not marked Owned, so nothing will ever clean it up."
        }
        if ($ownDefault.Path.StartsWith($ambientSentinel, [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "The own-directory default '$($ownDefault.Path)' is inside the " +
                "sentinel TMP/TEMP '$ambientSentinel'; relabelling TMP/TEMP is " +
                "still the Issue #302 defect, not a fix for it.")
        }
        $expectedParent = Split-Path -Parent ([IO.Path]::GetFullPath($repoRoot))
        if (-not $ownDefault.Path.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw (
                "The own-directory default '$($ownDefault.Path)' is not a sibling " +
                "of the repository root '$repoRoot' (expected under '$expectedParent').")
        }

        # --- Issue #302, mutant C: the default still goes through the usual
        # preflight diagnosis, exactly like an explicit -TemporaryRoot always
        # has. A fixed suffix makes the computed path predictable, so a file
        # can be planted at the exact path the default would use.
        $conflictSuffix = "conflict-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
        $conflictPath = Get-OwnVerificationTemporaryRoot -RepositoryRoot $repoRoot -Suffix $conflictSuffix
        if (Test-Path -LiteralPath $conflictPath) {
            throw "The predicted own-directory default path '$conflictPath' already exists before the test could plant a conflict there."
        }
        [IO.File]::WriteAllText($conflictPath, "planted by scripts\test-temporary-root.ps1")
        try {
            $rejectedDefaultReason = $null
            try {
                Initialize-VerificationTemporaryRoot -RepositoryRoot $repoRoot -OwnDirectorySuffix $conflictSuffix | Out-Null
            }
            catch {
                $rejectedDefaultReason = $_.Exception.Message
            }
            if ($null -eq $rejectedDefaultReason) {
                throw (
                    "The own-directory default was accepted even though " +
                    "'$conflictPath' is a file, not a directory - the usual " +
                    "diagnosis did not run against it.")
            }
            if ($rejectedDefaultReason -notmatch [regex]::Escape($conflictPath) -or
                $rejectedDefaultReason -notmatch "is a file") {
                throw "The own-directory default was refused, but not for the planted conflict: $rejectedDefaultReason"
            }
        }
        finally {
            Remove-Item -LiteralPath $conflictPath -Force -ErrorAction SilentlyContinue
        }

        # The override only helps if child processes and the engine see it, and
        # they read TMP and TEMP. Win32 GetTempPath prefers TMP, so both matter.
        $applied = Initialize-VerificationTemporaryRoot -ExplicitPath $sandbox -RepositoryRoot $repoRoot
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

    # --- Issue #302, mutant B: a run that owns its directory removes it -----
    # In-process against the real function verify.ps1's `finally` block calls,
    # not a nested `verify.ps1` invocation: the `scripts` stage already runs
    # this very file, so spawning a full nested run here would have this
    # check trigger itself recursively through that stage - measured at 240 s
    # for a single `-Stage scripts` run instead of the usual tens of seconds,
    # once with the recursion this produced. Complete-VerificationTemporaryRoot
    # is the one place that decides whether a run's temporary root gets
    # removed, so calling it directly proves the same thing the nested run
    # would have, at the cost of a filesystem probe instead of a child process
    # tree.
    $ownedProbe = Join-Path $sandbox ("owned-cleanup-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $ownedProbe | Out-Null
    Complete-VerificationTemporaryRoot -Path $ownedProbe -Owned $true
    if (Test-Path -LiteralPath $ownedProbe) {
        throw (
            "Complete-VerificationTemporaryRoot left an owned directory " +
            "behind ('$ownedProbe'); a run that owns its temporary root has " +
            "to remove it (Issue #302).")
    }

    # The negative control: a caller-supplied directory (Owned=$false, the
    # shape an explicit -TemporaryRoot or $env:DUNGEON_FORTRESS_TEMP always
    # produces) must never be touched, even though it is passed to the exact
    # same function.
    $notOwnedProbe = Join-Path $sandbox ("not-owned-cleanup-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $notOwnedProbe | Out-Null
    Complete-VerificationTemporaryRoot -Path $notOwnedProbe -Owned $false
    if (-not (Test-Path -LiteralPath $notOwnedProbe)) {
        throw (
            "Complete-VerificationTemporaryRoot removed a directory marked " +
            "Owned=`$false ('$notOwnedProbe'); an explicit -TemporaryRoot or " +
            "`$env:DUNGEON_FORTRESS_TEMP names a directory the caller chose " +
            "and may be reusing on purpose - this must never delete it.")
    }
    Remove-Item -LiteralPath $notOwnedProbe -Recurse -Force -ErrorAction SilentlyContinue

    # A run whose preflight never got as far as choosing a directory - an
    # empty Path, exactly what verify.ps1's own pre-try initialisation leaves
    # $temporaryRootPath at - must be a silent no-op, not an error that
    # replaces whatever the run was reporting (Issue #89's own reasoning,
    # reapplied to this new function).
    Complete-VerificationTemporaryRoot -Path "" -Owned $false

    $realEndToEndCommand = (
        "powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify.ps1 " +
        "-Stage scripts")
    Write-Host (
        "Own-directory default and cleanup are also proven end to end by a " +
        "real, non-recursive command: run '$realEndToEndCommand' directly " +
        "(not from inside this test) and read its verification_temporary_root " +
        "and the directory's absence afterward - the evidence in " +
        "evidence/302-temp-contention.json was captured exactly that way.")

    [ordered]@{
        event = "temporary_root_test"
        status = "ok"
        resolutionOrder = @("-TemporaryRoot", "`$env:DUNGEON_FORTRESS_TEMP", "own run directory")
        decidedByRemoveItem = $decidedByRemoveItem
        requiredDeleteParameters = $requiredRemoveItemParameters
        requiredErrorAction = $requiredErrorActionValue
        deletionContractCasesProven = @($contractCases | ForEach-Object { $_.Name })
        usableRootAccepted = $true
        fileRejected = $true
        undeletableRejected = $true
        cleanupWarnedInsteadOfThrowing = $cleanupWarned
        preflightRejectedBeforeStages = $true
        rejectionExitCode = $rejectionExitCode
        ownDefaultNotAmbientTemp = $true
        ownDefaultStillDiagnosed = $true
        ownedTemporaryRootRemoved = $true
        notOwnedTemporaryRootLeftAlone = $true
    } | ConvertTo-Json -Compress -Depth 4 | Write-Host
}
finally {
    Remove-TemporaryItemBestEffort `
        -Path $sandbox `
        -Description "temporary root test sandbox" | Out-Null
}
