[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #329. A screenshot capture needs every pixel-affecting value declared
# explicitly - ViewLaunchOptions.Parse's own "requireExplicitCaptureParameters"
# refusal, unchanged since Issue #79/#81, is the rule; that part was never
# wrong. What was wrong is that scripts\run-game.ps1's own preflight only knew
# about one of the three parameters that rule actually requires (-CameraZoom),
# so a screenshot request missing -UiScale and/or -FrameSize sailed past this
# script's own refusal, through a full restore, build and asset import, and
# only then hit the engine's refusal - an ArgumentException stack trace
# pointing at Main.cs instead of a one-line reason before any of that work
# started. scripts\capture-evidence.ps1 already knew to always pass all three;
# run-game.ps1 did not, and that the two scripts silently disagreed about the
# same contract is what let the gap stand.
#
# Two checks, for two different failure shapes:
#   1. a static text-anchor contract on run-game.ps1's source, in the same
#      style as scripts\test-temporary-root.ps1's deletion contract: each of
#      the three parameters has its own "if (-not $hasX) { $missing += '-X' }"
#      guard, and exactly one throw fires once every guard has run, naming
#      whichever of the three are still missing together. A live invocation
#      cannot watch this shape drift - it can only observe the one message an
#      unlucky combination of arguments happens to produce - so drift that
#      still produces *a* refusal for *some* input would pass a purely
#      behavioural test unnoticed.
#   2. one real, live invocation of run-game.ps1 (missing -UiScale and
#      -FrameSize) proving the refusal actually fires the way the static
#      check assumes: fast, before any dotnet call, naming both parameters
#      by name. This is only cheap because the refusal in question sits
#      before every dotnet restore/build call in run-game.ps1 - the same
#      reason scripts\test-temporary-root.ps1 can invoke verify.ps1 for real
#      with a broken temporary root and stay inside the `scripts` stage's "no
#      build, no engine, no network" budget. The positive case - a capture
#      with all three parameters actually reaching the engine and producing a
#      PNG - needs the full restore/build/import/engine pipeline and is
#      proven once per PR as evidence instead (evidence/329-*.json), not on
#      every `scripts` stage run.
#
# It needs no build, no engine and no network beyond that one fast refusal,
# and runs inside the `scripts` stage.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptsRoot = Join-Path $repoRoot "scripts"
$runGamePath = Join-Path $scriptsRoot "run-game.ps1"

# Flag variable name to the -Parameter name it has to make run-game.ps1 name
# in its refusal message when omitted alongside -ScreenshotPath.
$requiredCaptureParameters = @(
    [pscustomobject]@{ Flag = "hasCameraZoom"; Name = "-CameraZoom" },
    [pscustomobject]@{ Flag = "hasUiScale"; Name = "-UiScale" },
    [pscustomobject]@{ Flag = "hasFrameSize"; Name = "-FrameSize" }
)

function Get-CaptureParameterContractFindings {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $fileName = [IO.Path]::GetFileName($Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @("'$fileName' does not exist, so its capture-parameter contract cannot be checked")
    }

    $text = [IO.File]::ReadAllText($Path)
    $findings = @()

    foreach ($parameter in $requiredCaptureParameters) {
        $guardPattern = (
            'if\s*\(-not\s+\$' + [regex]::Escape($parameter.Flag) + '\)\s*\{\s*' +
            '\$missingCaptureParameters\s*\+=\s*"' + [regex]::Escape($parameter.Name) + '"\s*\}'
        )
        $occurrences = ([regex]::Matches($text, $guardPattern)).Count
        if ($occurrences -eq 0) {
            $findings += (
                "'$fileName' no longer requires $($parameter.Name) for a screenshot " +
                "capture: no guard appends it to `$missingCaptureParameters when " +
                "`$$($parameter.Flag) is false"
            )
        }
        elseif ($occurrences -gt 1) {
            $findings += (
                "'$fileName' has $occurrences guards for $($parameter.Name) " +
                "instead of one; this contract assumes exactly one"
            )
        }
    }

    # One refusal naming everything missing, not the first offender found -
    # the whole point of Issue #329's fix over the -CameraZoom-only check it
    # replaced.
    $throwPattern = (
        'if\s*\(\$missingCaptureParameters\.Count\s+-gt\s+0\)\s*\{[\s\S]*?throw\s*\('
    )
    $throwOccurrences = ([regex]::Matches($text, $throwPattern)).Count
    if ($throwOccurrences -eq 0) {
        $findings += (
            "'$fileName' no longer throws once for every capture parameter " +
            "still missing after all three guards ran"
        )
    }
    elseif ($throwOccurrences -gt 1) {
        $findings += (
            "'$fileName' has $throwOccurrences refusals gated on " +
            "`$missingCaptureParameters.Count instead of one"
        )
    }

    return @($findings)
}

$liveFindings = @(Get-CaptureParameterContractFindings -Path $runGamePath)
if ($liveFindings.Count -gt 0) {
    throw (
        "run-game.ps1's screenshot capture-parameter contract is incomplete:" +
        [Environment]::NewLine + "  " + ($liveFindings -join ([Environment]::NewLine + "  ")))
}

# --- the static contract is watched failing, on copies -----------------------
$sandbox = Join-Path $repoRoot (".artifacts\run-game-capture-parameters-guard-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
try {
    # run-game.ps1 is committed with LF line endings (.gitattributes:
    # `* text=auto eol=lf`), so the anchors below use `n, not `r`n. A working
    # copy checked out with CRLF would make every occurrence count 0 rather
    # than silently matching something else - the guard below catches that
    # by treating "0 occurrences" as a hard failure, not a pass.
    $sourceText = [IO.File]::ReadAllText($runGamePath)
    $mutationCases = @(
        [pscustomobject]@{
            Name = "camera-zoom-guard-dropped"
            Find = "    if (-not `$hasCameraZoom) {`n        `$missingCaptureParameters += `"-CameraZoom`"`n    }`n"
            Expect = "-CameraZoom"
        },
        [pscustomobject]@{
            Name = "ui-scale-guard-dropped"
            Find = "    if (-not `$hasUiScale) {`n        `$missingCaptureParameters += `"-UiScale`"`n    }`n"
            Expect = "-UiScale"
        },
        [pscustomobject]@{
            Name = "frame-size-guard-dropped"
            Find = "    if (-not `$hasFrameSize) {`n        `$missingCaptureParameters += `"-FrameSize`"`n    }`n"
            Expect = "-FrameSize"
        }
    )

    foreach ($case in $mutationCases) {
        $occurrences = ([regex]::Matches($sourceText, [regex]::Escape($case.Find))).Count
        if ($occurrences -ne 1) {
            throw (
                "The mutation case '$($case.Name)' anchors on text appearing " +
                "$occurrences time(s) in run-game.ps1; it has to appear once, " +
                "byte for byte including line endings. Update the anchor, do " +
                "not delete the case.")
        }

        $mutatedPath = Join-Path $sandbox ($case.Name + ".ps1")
        [IO.File]::WriteAllText(
            $mutatedPath,
            $sourceText.Replace($case.Find, ""),
            [Text.UTF8Encoding]::new($false))

        $caseFindings = @(Get-CaptureParameterContractFindings -Path $mutatedPath)
        $matched = @($caseFindings | Where-Object { $_ -match [regex]::Escape($case.Expect) })
        if ($matched.Count -eq 0) {
            throw (
                "Dropping the $($case.Name) guard went unnoticed. Expected a " +
                "finding mentioning '$($case.Expect)'; got " +
                $(if ($caseFindings.Count -eq 0) { "nothing at all." } else { ($caseFindings -join "; ") }))
        }
    }

    $untouchedCopy = Join-Path $sandbox "untouched-run-game.ps1"
    [IO.File]::WriteAllText($untouchedCopy, $sourceText, [Text.UTF8Encoding]::new($false))
    $untouchedFindings = @(Get-CaptureParameterContractFindings -Path $untouchedCopy)
    if ($untouchedFindings.Count -gt 0) {
        throw (
            "An unmodified copy of run-game.ps1 was reported as broken, so the " +
            "mutation cases above prove nothing: " + ($untouchedFindings -join "; "))
    }

    # --- the live refusal actually fires the way the static check assumes ----
    $liveScreenshotPath = Join-Path $sandbox "live-capture-refusal-probe.png"
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $liveOutput = @(& powershell -NoProfile -ExecutionPolicy Bypass `
            -File $runGamePath `
            -ScreenshotPath $liveScreenshotPath `
            -CameraZoom 0.75 2>&1)
        $liveExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $liveText = ($liveOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine

    if ($liveExitCode -eq 0) {
        throw "run-game.ps1 accepted a screenshot request missing -UiScale and -FrameSize."
    }
    if ($liveText -notmatch [regex]::Escape("-UiScale") -or $liveText -notmatch [regex]::Escape("-FrameSize")) {
        throw (
            "run-game.ps1's live refusal for a request missing -UiScale and " +
            "-FrameSize does not name both: $liveText")
    }
    if ($liveText -match "ArgumentException" -or $liveText -match "ViewLaunchOptions") {
        throw (
            "run-game.ps1 did not refuse before reaching the engine - the " +
            "refusal text mentions the engine-level ArgumentException this " +
            "check exists to pre-empt: $liveText")
    }
    if (Test-Path -LiteralPath $liveScreenshotPath) {
        throw "run-game.ps1 wrote a screenshot for a request it should have refused."
    }

    [ordered]@{
        event = "run_game_capture_parameters_test"
        status = "ok"
        requiredParameters = @($requiredCaptureParameters | ForEach-Object { $_.Name })
        mutationCasesProven = @($mutationCases | ForEach-Object { $_.Name })
        liveRefusalExitCode = $liveExitCode
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}
