<#
.SYNOPSIS
Regression test for scripts/token-budget-report.ps1 (Issue #303). Exercises
the three properties the script exists to guarantee - each with its own,
distinguishable assert, so that breaking any one of them fails visibly and
does not get masked by the other two still passing:

  A. stratification: writer and review subagent populations are genuinely
     split, not one bucket, and differ materially per agent;
  B. the unclosed-slice guard actually refuses (non-zero exit, a specific
     marker), rather than silently returning a number;
  C. the reported per-agent figures are a real division, not the raw sum
     relabeled.

.DESCRIPTION
This test runs against this machine's real Claude Code transcripts (the
same ones docs/engineering/TOKEN_BUDGET.md's baseline is measured from),
not a synthetic fixture. That mirrors the domain itself, which
TOKEN_BUDGET.md already states as a boundary of the method ("Замер
локален. Учитываются только транскрипты на этой машине.") - there is no
portable substitute for "does the classifier actually split this
project's real launch prompts correctly". Consequently this test requires
~/.claude/projects/C--gamedev-Dungeon-fortress to exist with its
2026-07-26..2026-08-05 history intact; it is not currently wired into
verify.ps1 (scripts/verify.ps1 is owned by a concurrent PR at the time
this test was added - see Issue #303's partition notes) and is run
directly.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "token-budget-report.ps1"

function Invoke-Report {
    param([string[]]$ExtraArgs)
    $args = @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $scriptPath
    ) + $ExtraArgs
    # The refusal path writes to stderr via [Console]::Error.WriteLine. For a
    # native child process (powershell.exe here), PowerShell turns every
    # redirected stderr line into a terminating NativeCommandError as long as
    # $ErrorActionPreference is "Stop" - regardless of how the child wrote it
    # - so capturing "2>&1" under Stop throws instead of returning the text.
    # Relax it for just this call.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell @args 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $text = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    return [pscustomobject]@{ ExitCode = $exitCode; Text = $text }
}

$failures = New-Object System.Collections.Generic.List[string]

# ---------------------------------------------------------------------------
# Setup: one baseline run, reused by checks A and C.
# ---------------------------------------------------------------------------
$baseline = Invoke-Report -ExtraArgs @(
    "-From", "2026-07-26", "-To", "2026-08-05", "-SkipMergedPr", "-Json"
)
if ($baseline.ExitCode -ne 0) {
    throw "Baseline run itself failed (exit $($baseline.ExitCode)); cannot run the mutant checks on top of a broken script. Output: $($baseline.Text)"
}
$result = $baseline.Text | ConvertFrom-Json

# ---------------------------------------------------------------------------
# Check A - stratification is real, not one bucket, and the split is not
# cosmetic (assert on both existence and magnitude of the per-agent gap).
# Mutant: collapse the classifier so every subagent gets the same role.
# ---------------------------------------------------------------------------
$writerCount = $result.subagents.writer.agentCount
$reviewCount = $result.subagents.review.agentCount
$allCount = $result.subagents.all.agentCount
$writerPerAgent = 0.0
$reviewPerAgent = 0.0

if ($writerCount -le 0 -or $reviewCount -le 0) {
    $failures.Add(
        "CHECK A (stratification) FAILED: writer=$writerCount review=$reviewCount - " +
        "both populations must be non-empty on the 2026-07-26..2026-08-05 slice " +
        "(documented: 47 writer / 80 review). A zero here means the classifier put " +
        "every subagent into one bucket."
    )
}
elseif (($writerCount + $reviewCount) -gt $allCount) {
    $failures.Add(
        "CHECK A (stratification) FAILED: writer($writerCount) + review($reviewCount) " +
        "= $($writerCount + $reviewCount) exceeds all($allCount) - the two buckets are " +
        "not disjoint subsets of the same population."
    )
}
else {
    $writerPerAgent = $result.subagents.writer.perAgent.cacheReadTokens
    $reviewPerAgent = $result.subagents.review.perAgent.cacheReadTokens
    if ($reviewPerAgent -le 0) {
        $failures.Add("CHECK A (stratification) FAILED: review per-agent cache-read is $reviewPerAgent, cannot compute a ratio.")
    }
    else {
        $ratio = $writerPerAgent / $reviewPerAgent
        if ($ratio -lt 1.5) {
            $failures.Add(
                "CHECK A (stratification) FAILED: writer/review per-agent cache-read ratio " +
                "is only $([math]::Round($ratio, 2))x (measured on this baseline: ~4.4x). " +
                "A ratio near 1 means the split stopped distinguishing the two populations."
            )
        }
    }
}

# ---------------------------------------------------------------------------
# Check B - the unclosed-slice guard actually refuses.
# Mutant: remove or weaken the -To/-Now closed-day comparison.
# ---------------------------------------------------------------------------
$refusal = Invoke-Report -ExtraArgs @(
    "-From", "2026-07-26", "-To", "2026-08-06", "-SkipMergedPr", "-Now", "2026-08-07"
)
if ($refusal.ExitCode -eq 0) {
    $failures.Add(
        "CHECK B (unclosed-slice guard) FAILED: -To 2026-08-06 with -Now 2026-08-07 " +
        "(one closed day of margin, not two) exited 0 instead of refusing. This exact " +
        "slice measured 87.99% vs 86.85% opus share a few hours apart on PR #291 " +
        "(evidence/283-model-share.json, evidence/303-unclosed-slice-refusal.json)."
    )
}
elseif ($refusal.Text -notmatch "REFUSED_UNCLOSED_SLICE") {
    $failures.Add(
        "CHECK B (unclosed-slice guard) FAILED: exited non-zero ($($refusal.ExitCode)) " +
        "but without the REFUSED_UNCLOSED_SLICE marker - refused for the wrong reason, " +
        "or the marker text was changed without updating this check. Output: $($refusal.Text)"
    )
}

# Control: the same call one day further back must succeed - proves check B
# above tests the guard specifically, not "the script always fails".
$control = Invoke-Report -ExtraArgs @(
    "-From", "2026-07-26", "-To", "2026-08-05", "-SkipMergedPr", "-Now", "2026-08-07"
)
if ($control.ExitCode -ne 0) {
    $failures.Add(
        "CHECK B control FAILED: -To 2026-08-05 with -Now 2026-08-07 (exactly two closed " +
        "days) should succeed but exited $($control.ExitCode). Output: $($control.Text)"
    )
}

# ---------------------------------------------------------------------------
# Check C - per-agent figures are total/count, not the raw sum relabeled.
# Mutant: return cacheReadTokens (the total) as perAgent.cacheReadTokens too.
# ---------------------------------------------------------------------------
if ($writerCount -gt 1) {
    $writerTotal = $result.subagents.writer.cacheReadTokens
    $writerPerAgentC = $result.subagents.writer.perAgent.cacheReadTokens
    $expectedPerAgent = $writerTotal / $writerCount
    $diff = [math]::Abs($writerPerAgentC - $expectedPerAgent)
    $tolerance = [math]::Max(1.0, $expectedPerAgent * 0.001)
    if ($diff -gt $tolerance) {
        $failures.Add(
            "CHECK C (per-agent normalization) FAILED: writer perAgent.cacheReadTokens " +
            "= $writerPerAgentC, but total($writerTotal) / agentCount($writerCount) = " +
            "$expectedPerAgent (diff $diff > tolerance $tolerance)."
        )
    }
    if ([math]::Abs($writerPerAgentC - $writerTotal) -lt ($writerTotal * 0.01)) {
        $failures.Add(
            "CHECK C (per-agent normalization) FAILED: writer perAgent.cacheReadTokens " +
            "($writerPerAgentC) is within 1% of the raw total ($writerTotal) with " +
            "agentCount=$writerCount - looks like the raw sum was returned unchanged " +
            "instead of being divided by the agent count."
        )
    }
}
else {
    $failures.Add("CHECK C setup FAILED: writer agentCount is $writerCount (<=1), cannot distinguish a sum from a per-agent value.")
}

# ---------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    foreach ($f in $failures) { [Console]::Error.WriteLine($f) }
    throw "$($failures.Count) check(s) failed."
}

[ordered]@{
    event               = "token_budget_report_test"
    status              = "ok"
    stratificationRatio = [math]::Round($writerPerAgent / $reviewPerAgent, 2)
    writerAgentCount    = $writerCount
    reviewAgentCount    = $reviewCount
} | ConvertTo-Json -Compress | Write-Host
