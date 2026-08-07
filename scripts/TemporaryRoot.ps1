Set-StrictMode -Version Latest

# Every verification run needs a temporary directory it can create in, write to
# and, above all, delete from. Two things live there and neither is optional:
# the short Godot runtime profile, which has to sit outside the worktree so the
# shader cache path stays inside the length Godot can still enter (254
# characters, measured in evidence/184-cause.json; the budget itself lives in
# Assert-GodotShaderCachePathFits in GodotTools.ps1), and the isolated project
# the sprite import test builds and throws away.
#
# Issue #89: in a session whose TEMP pointed at C:\WINDOWS\TEMP the account could
# create both and delete neither. The run reached stage `godot`, the sprite
# import test printed {"event":"goblin_sprite_import_test","status":"ok"}, and
# then its own cleanup failed with "Access is denied". What verification
# reported was {"failedStage":"godot","reason":"'powershell' failed with exit
# code 1."} - which names neither the directory nor the permission, and sent
# three separate sessions looking for a defect in the change under review.
#
# So two rules live here. The directory is proven usable before any stage runs,
# with a message that says which directory, what failed and how to override it;
# and removing anything temporary is best effort, because cleanup runs after the
# work is done and must never turn a finished check into a red run.

$script:TemporaryRootVariableName = "DUNGEON_FORTRESS_TEMP"

# Issue #302. This used to fall back to [IO.Path]::GetTempPath(), i.e. whatever
# the ambient TMP/TEMP environment variables say - a machine-wide setting no
# run controls and no run can trust. Measured in
# evidence/302-temp-contention.json: on this machine TMP/TEMP resolves to
# C:\WINDOWS\TEMP, and this account can create directories there but never
# delete them (the exact Issue #89 failure mode), so every default run refused
# in preflight - independently of whether another agent was running at the
# same time. Five sessions read that as "agents racing for a shared directory"
# and each hand-picked a one-off -TemporaryRoot to work around it, because the
# fix - "stop trusting TMP/TEMP for the default" - had never been written into
# the script.
#
# The replacement default is a directory this run computes, owns and deletes
# itself: a short name that is a sibling of the repository root rather than
# nested inside TMP/TEMP or inside the worktree. Sibling-of-repository is
# writable by construction (this is where `git worktree add` itself writes),
# and it is short enough to leave headroom under the 254-character shader
# cache budget from Issue #184 - measured for four candidate roots in
# evidence/302-default-root-path-length.json; nesting the same per-run
# directory *inside* the worktree's own .artifacts left as little as 17
# characters of headroom on this repository's own worktree paths, which is
# not a safe margin on a machine with a longer username or a deeper checkout.
$script:OwnTemporaryRootDirectoryPrefix = "df-verify-"

function Get-OwnVerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        # A fixed suffix makes the resulting path predictable, which is what
        # lets scripts\test-temporary-root.ps1 pre-seed a conflict at the
        # exact path this function will compute and prove the usual diagnosis
        # still runs against it (Issue #302, mutant C). Left unset, every real
        # run gets its own GUID and two runs can never compute the same path.
        [string]$Suffix
    )

    $resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
    $parent = Split-Path -Parent $resolvedRepositoryRoot
    if ([string]::IsNullOrWhiteSpace($parent)) {
        # The repository root is itself a drive root (e.g. "C:\"). Vanishingly
        # unlikely in practice, but falling back to the drive root keeps this
        # a total function instead of one that throws on a path shape nothing
        # else in this file special-cases either.
        $parent = [IO.Path]::GetPathRoot($resolvedRepositoryRoot)
    }

    $resolvedSuffix = if ([string]::IsNullOrWhiteSpace($Suffix)) {
        [Guid]::NewGuid().ToString("N").Substring(0, 8)
    }
    else {
        $Suffix
    }

    return Join-Path $parent ($script:OwnTemporaryRootDirectoryPrefix + $resolvedSuffix)
}

function Resolve-VerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [string]$ExplicitPath,

        # Only needed to compute the own-directory default below; the
        # -TemporaryRoot and $env:DUNGEON_FORTRESS_TEMP tiers never touch it.
        # This does NOT mean every caller that only ever passes an explicit
        # override is safe to leave without it. It means only a caller whose
        # -ExplicitPath is always non-empty is safe to leave without it - and
        # "always passes -ExplicitPath" is not the same claim as "always passes
        # a non-empty one". Issue #329: scripts\run-game.ps1 and
        # scripts\update-golden-ui.ps1 both declare -TemporaryRoot as an
        # optional [string] parameter and both pass it straight through as
        # -ExplicitPath here. An omitted -TemporaryRoot arrives as "", not as
        # "absent" - PowerShell does not distinguish the two for a plain
        # [string] - and an empty -ExplicitPath falls through the first tier
        # below to $env:DUNGEON_FORTRESS_TEMP and then to this one, which threw
        # on every argument-free invocation of either script until both were
        # updated to also pass -RepositoryRoot.
        [string]$RepositoryRoot,

        # Passed straight through to Get-OwnVerificationTemporaryRoot; see its
        # own parameter for why this exists.
        [string]$OwnDirectorySuffix
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return [pscustomobject]@{
            Path = $ExplicitPath
            Source = "-TemporaryRoot"
            Owned = $false
        }
    }

    $fromEnvironment = [Environment]::GetEnvironmentVariable($script:TemporaryRootVariableName)
    if (-not [string]::IsNullOrWhiteSpace($fromEnvironment)) {
        return [pscustomobject]@{
            Path = $fromEnvironment
            Source = "`$env:$($script:TemporaryRootVariableName)"
            Owned = $false
        }
    }

    if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
        throw (
            "Resolve-VerificationTemporaryRoot has neither -ExplicitPath nor " +
            "`$env:$($script:TemporaryRootVariableName) to work with, and no " +
            "-RepositoryRoot to compute its own default directory from.")
    }

    return [pscustomobject]@{
        Path = (Get-OwnVerificationTemporaryRoot -RepositoryRoot $RepositoryRoot -Suffix $OwnDirectorySuffix)
        Source = "own run directory"
        # Only this tier's directory is this run's alone to create and
        # destroy. An explicit -TemporaryRoot or $env:DUNGEON_FORTRESS_TEMP
        # names a directory the caller chose and may be reusing on purpose
        # (evidence/302-temp-contention.json's own workarounds did exactly
        # that across retries), so this run never deletes it.
        Owned = $true
    }
}

function ConvertTo-NormalizedRootPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    # Never throws: an unusable path has to reach the diagnosis, not blow up on
    # the way to it. GetTempPath returns a trailing separator and the override
    # may not, so both end up spelled the same way in the message and in TEMP.
    try {
        $full = [IO.Path]::GetFullPath($Path)
    }
    catch {
        return $Path
    }

    $trimmed = $full.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    if ($trimmed.Length -eq 0 -or $trimmed.EndsWith(":")) {
        return $full
    }

    return $trimmed
}

function Get-CleanReason {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Message
    )

    $reason = $Message.Trim()
    if ($reason.Length -gt 0 -and -not $reason.EndsWith(".")) {
        $reason += "."
    }

    return $reason
}

function Get-TemporaryRootDiagnosis {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        # Normally a fresh GUID nobody else can touch. The name is a parameter
        # so scripts\test-temporary-root.ps1 can leave an open file inside the
        # probe directory and prove that the delete step is really checked - the
        # delete step is the one the reported incident failed on.
        [string]$ProbeDirectoryName = ("verify-temp-probe-" + [Guid]::NewGuid().ToString("N"))
    )

    try {
        $resolvedRoot = [IO.Path]::GetFullPath($Path)
    }
    catch {
        return "'$Path' is not a usable path: $(Get-CleanReason -Message $_.Exception.Message)"
    }

    if (Test-Path -LiteralPath $resolvedRoot -PathType Leaf) {
        return "'$resolvedRoot' exists but is a file, not a directory."
    }

    try {
        [IO.Directory]::CreateDirectory($resolvedRoot) | Out-Null
    }
    catch {
        return (
            "'$resolvedRoot' does not exist and could not be created: " +
            (Get-CleanReason -Message $_.Exception.Message))
    }

    $probeDirectory = Join-Path $resolvedRoot $ProbeDirectoryName
    try {
        [IO.Directory]::CreateDirectory($probeDirectory) | Out-Null
    }
    catch {
        return (
            "no directory can be created inside '$resolvedRoot': " +
            (Get-CleanReason -Message $_.Exception.Message))
    }

    try {
        [IO.File]::WriteAllText(
            (Join-Path $probeDirectory "probe.txt"),
            "Dungeon Fortress verification probe.")
    }
    catch {
        return (
            "no file can be written inside '$probeDirectory': " +
            (Get-CleanReason -Message $_.Exception.Message))
    }

    $removalFailure = $null
    try {
        # The deciding call, and deliberately the same one the real cleanup
        # makes. It has to stay that way. Measured on the machine that reported
        # Issue #89, with TEMP at C:\WINDOWS\TEMP: [IO.Directory]::Delete on a
        # directory this account created there succeeds, while
        # Remove-Item -Recurse -Force on the same directory fails with a
        # Win32Exception "Access is denied". A probe that used the cheaper API
        # would certify a directory that every cleanup in this repository then
        # chokes on, which is the original defect with an extra step.
        #
        # -ErrorAction Stop is load-bearing, and by value rather than by
        # presence. Measured on Windows PowerShell 5.1: when the delete fails
        # because a file inside is held open, the error honours the parameter,
        # so with SilentlyContinue, Continue or Ignore the catch below never
        # runs, $removalFailure stays $null and this function returns "usable"
        # for a directory it just failed to empty. The Access-denied failure of
        # C:\WINDOWS\TEMP behaves differently - it is terminating under every
        # value - which is why the parameter looked decorative until someone
        # measured the other mode (Issue #102).
        #
        # The rule is not left to this comment: scripts\test-temporary-root.ps1
        # asserts over the AST that the first deletion here, and in
        # Remove-TemporaryItemBestEffort below, is this exact call with this
        # exact -ErrorAction.
        Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction Stop
    }
    catch {
        $removalFailure = Get-CleanReason -Message $_.Exception.Message
    }

    if ($null -eq $removalFailure) {
        return $null
    }

    # Tidy-up only, and only after the diagnosis is already decided above. In the
    # environment this Issue came from, the run is refused and the probe would
    # otherwise stay behind forever: that temporary directory cannot even be
    # listed by this account, so the leftover is findable only through the path
    # printed in the refusal. [IO.Directory]::Delete does succeed there, which is
    # exactly why it may clean up and may not decide anything.
    $leftover = " The probe directory is still there; delete it once the permissions are fixed."
    try {
        [IO.Directory]::Delete($probeDirectory, $true)
        $leftover = " The probe directory itself was removed by a fallback, so nothing was left behind."
    }
    catch {
        # Nothing to add: the diagnosis below already reports the real failure.
    }

    return (
        "'$probeDirectory' was created and then could not be deleted: " +
        $removalFailure +
        " A run creates a Godot runtime profile and an isolated import " +
        "project here and has to be able to remove them." + $leftover)
}

function Initialize-VerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [string]$ExplicitPath,

        # Only consulted when neither -ExplicitPath nor
        # $env:DUNGEON_FORTRESS_TEMP apply; see Resolve-VerificationTemporaryRoot.
        [string]$RepositoryRoot,

        [string]$OwnDirectorySuffix
    )

    $selection = Resolve-VerificationTemporaryRoot -ExplicitPath $ExplicitPath -RepositoryRoot $RepositoryRoot -OwnDirectorySuffix $OwnDirectorySuffix
    $candidate = ConvertTo-NormalizedRootPath -Path $selection.Path
    # Unconditional, on purpose: the own-directory default is not exempt from
    # this. It goes through the exact same probe - create, write, delete - as
    # an explicit -TemporaryRoot always has, so a default that lands on an
    # unusable directory (a file already there, a parent it cannot write to)
    # is still refused by name and reason in preflight rather than accepted
    # because this run picked it itself (Issue #302, mutant C).
    $diagnosis = Get-TemporaryRootDiagnosis -Path $candidate
    if ($null -ne $diagnosis) {
        throw @"
The temporary directory this run would use is not usable.
  directory: $candidate
  chosen by: $($selection.Source)
  failure:   $diagnosis
Use one of:
  -TemporaryRoot <directory this account can create and delete in>
  `$env:$($script:TemporaryRootVariableName)=<the same directory>
Without either, this run computes its own directory next to the repository
root and TMP/TEMP are not consulted (Issue #302) - pointing TMP/TEMP
elsewhere will not change what this run picked.
Keep it short and outside the worktree: the Godot runtime profile is created
there, and past 254 characters the engine can create its shader cache
directories but never enter them again, which is the ERROR recorded in
docs/engineering/ENVIRONMENT_SETUP.md. That length is measured before the engine
starts, so a directory that is usable but too deep is refused by name rather
than silently.
"@
    }

    # Child processes read TMP and TEMP, and Win32 GetTempPath prefers TMP over
    # TEMP, so both are set. This is what makes the override reach the sprite
    # import test and the engine rather than only this script.
    $env:TEMP = $candidate
    $env:TMP = $candidate

    return [pscustomobject]@{
        Path = $candidate
        Source = $selection.Source
        Owned = $selection.Owned
    }
}

function Remove-TemporaryItemBestEffort {
    [CmdletBinding()]
    [OutputType([bool])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return $true
    }

    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return $true
    }
    catch {
        # Cleanup is not a check. Whatever this run was asked to prove is already
        # proven and reported by the time anything is removed, so a directory
        # that refuses to go away is a warning in the structured output, not a
        # failed verification (Issue #89).
        [ordered]@{
            event = "temporary_cleanup"
            status = "warning"
            description = $Description
            path = $Path
            reason = $_.Exception.Message
        } | ConvertTo-Json -Compress | Write-Host
        return $false
    }
}

function Complete-VerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        # Empty is accepted on purpose: a preflight failure throws before
        # verify.ps1's own $temporaryRootPath is ever assigned, and the
        # `finally` block that calls this runs regardless (Issue #89's own
        # reasoning - cleanup must not depend on how far the run got, and
        # must never itself throw and hide what the run was reporting).
        [string]$Path,

        [bool]$Owned
    )

    # Issue #302: only the own-directory default is this run's to delete. An
    # explicit -TemporaryRoot or $env:DUNGEON_FORTRESS_TEMP names a directory
    # the caller chose, possibly to reuse across runs on purpose - this never
    # touches those, exactly like Remove-TemporaryItemBestEffort never touched
    # an explicit -TemporaryRoot before this function existed.
    if (-not $Owned -or [string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    Remove-TemporaryItemBestEffort `
        -Path $Path `
        -Description "own temporary root directory" | Out-Null
}
