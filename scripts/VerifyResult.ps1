Set-StrictMode -Version Latest

# Issue #427. verify.ps1's own `verification_result` line - the four
# checksums a PR routinely quotes (deterministicChecksum, changedSeedChecksum,
# loadChecksum, viewInvariantChecksum) among everything else a full run
# reports - used to live only in stdout, and the run's own `finally` block
# unconditionally deletes $verifyRoot, and the stage-output.log inside it
# (Issue #284 gave that log a temporary home, not a durable one), on every
# outcome, green or red. PR #425 had to re-run the whole ten-stage suite a
# second time only to pipe the same numbers through Tee-Object, because the
# first run's numbers were already gone by the time anyone needed to cite
# them in a PR body.
#
# Save-VerificationResult gives the result a second, durable home: a fixed
# path under the worktree's own .artifacts/, which the temporary-directory
# cleanup rule ("Временный каталог", docs/engineering/ENVIRONMENT_SETUP.md)
# never owned in the first place and does not touch - the same directory
# already holds $env:DOTNET_CLI_HOME and the Godot NuGet tool profile across
# runs, so a result file surviving there is the existing convention, not a
# new exception to "leave no trace": it lives inside the repository
# worktree, is covered by the existing `.artifacts/` line in .gitignore, and
# is overwritten - never accumulated - by every run, so a thousand runs
# leave exactly one result file and, at most, one log behind.
#
# The stage log is a second question with its own decision (Issue #427 scope
# item 2). A green run's log has nothing in it worth reading later - every
# checksum a green run produces is already in verification_result itself -
# so it is not kept. A red run's log is exactly the thing a second full run
# would otherwise be needed to reproduce, so it is copied out before the
# temporary directory that held it is removed. verify.ps1 calls this
# function at most once per run - from the `try` block on success, or from
# the `catch` block on failure - and only the failure call passes
# -SourceStageLogPath.

function Save-VerificationResult {
    [CmdletBinding()]
    [OutputType([void])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResultPath,

        [Parameter(Mandatory = $true)]
        [string]$StageLogPath,

        [Parameter(Mandatory = $true)]
        [string]$Json,

        # The run's own stage-output.log (Issue #284), still sitting inside
        # the temporary $verifyRoot that the caller's `finally` block is
        # about to delete regardless of this function - cleanup itself stays
        # out of scope here, see the module comment above. Passed only when
        # this run failed; omitted on success, which is what keeps a green
        # run's directory free of any log at all.
        [string]$SourceStageLogPath
    )

    $resultsRoot = [IO.Path]::GetDirectoryName($ResultPath)
    [IO.Directory]::CreateDirectory($resultsRoot) | Out-Null
    [IO.File]::WriteAllText($ResultPath, $Json)

    if (-not [string]::IsNullOrEmpty($SourceStageLogPath) -and [IO.File]::Exists($SourceStageLogPath)) {
        [IO.File]::Copy($SourceStageLogPath, $StageLogPath, $true)
        return
    }

    # No log to keep for this run. One left over from an earlier failed run
    # in the same worktree would otherwise sit next to a result that says
    # "status":"ok", misreporting what the *current* run found.
    if ([IO.File]::Exists($StageLogPath)) {
        [IO.File]::Delete($StageLogPath)
    }
}
