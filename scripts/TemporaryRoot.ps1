Set-StrictMode -Version Latest

# Every verification run needs a temporary directory it can create in, write to
# and, above all, delete from. Two things live there and neither is optional:
# the short Godot runtime profile, which has to sit outside the worktree so the
# shader cache path stays under the Windows CreateDirectory limit, and the
# isolated project the sprite import test builds and throws away.
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

function Resolve-VerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return [pscustomobject]@{
            Path = $ExplicitPath
            Source = "-TemporaryRoot"
        }
    }

    $fromEnvironment = [Environment]::GetEnvironmentVariable($script:TemporaryRootVariableName)
    if (-not [string]::IsNullOrWhiteSpace($fromEnvironment)) {
        return [pscustomobject]@{
            Path = $fromEnvironment
            Source = "`$env:$($script:TemporaryRootVariableName)"
        }
    }

    return [pscustomobject]@{
        Path = [IO.Path]::GetTempPath()
        Source = "TMP/TEMP"
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

    try {
        # Deliberately the same call the real cleanup makes, and it has to stay
        # that way. Measured on the machine that reported Issue #89, with TEMP at
        # C:\WINDOWS\TEMP: [IO.Directory]::Delete on a directory this account
        # created there succeeds, while Remove-Item -Recurse -Force on the same
        # directory fails with a Win32Exception "Access is denied". A probe that
        # used the cheaper API would certify a directory that every cleanup in
        # this repository then chokes on, which is the original defect with an
        # extra step.
        Remove-Item -LiteralPath $probeDirectory -Recurse -Force -ErrorAction Stop
    }
    catch {
        return (
            "'$probeDirectory' was created and then could not be deleted: " +
            (Get-CleanReason -Message $_.Exception.Message) +
            " A run creates a Godot runtime profile and an isolated import " +
            "project here and has to be able to remove them. The probe " +
            "directory is still there; delete it once the permissions are fixed.")
    }

    return $null
}

function Initialize-VerificationTemporaryRoot {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [string]$ExplicitPath
    )

    $selection = Resolve-VerificationTemporaryRoot -ExplicitPath $ExplicitPath
    $candidate = ConvertTo-NormalizedRootPath -Path $selection.Path
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
  point TMP and TEMP at such a directory
Keep it short and outside the worktree: the Godot runtime profile is created
there, and a long path brings back the CreateDirectory limit behind the shader
cache ERROR recorded in docs/engineering/ENVIRONMENT_SETUP.md.
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
