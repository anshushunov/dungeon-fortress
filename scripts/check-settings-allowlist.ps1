<#
.SYNOPSIS
Flags write-verb keywords inside .claude/settings.json's permissions.allow list.

.DESCRIPTION
This is the mutant-tested check from Issue #286 / PR #288, persisted so the
next reviewer can run it instead of trusting prose. It greps the *text* of
each allow rule for a fixed list of write/delete/mutate keywords.

Known blind spot, named on purpose: this is a keyword match on the rule
string, not an analysis of what the underlying command can actually do. It
would NOT have caught the review finding that started this script's own
addition - Bash(git diff:*), Bash(git log:*) and Bash(git show:*) contain
none of the listed keywords, yet each accepts a --output=<path> flag that
writes a file outside the repo. That finding is closed by .claude/settings.json's
own `deny` entries instead (see docs/engineering/MULTI_AGENT_WORKFLOW.md,
the "Claude Code" section, and the DEBT_LEDGER.md row dated 2026-08-07), not
by this script. This script only guards against the coarser mistake of an
allow rule that is a write/delete/merge/push command by name.
#>

[CmdletBinding()]
param(
    [string]$SettingsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $SettingsPath) {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $SettingsPath = Join-Path $repoRoot ".claude/settings.json"
}

if (-not (Test-Path -LiteralPath $SettingsPath)) {
    Write-Host "Settings file not found: $SettingsPath"
    exit 2
}

$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
$allow = @($settings.permissions.allow)

$writeVerbPattern = 'push|commit|merge|delete|--force|reset|clean\b|checkout|edit'
$flagged = @($allow | Where-Object { $_ -match $writeVerbPattern })

[ordered]@{
    event = "settings_allowlist_writeverb_check"
    status = if ($flagged.Count -gt 0) { "fail" } else { "pass" }
    settingsPath = $SettingsPath
    ruleCount = $allow.Count
    flagged = $flagged
    blindSpot = "keyword match on rule text only; does not catch a write-capable flag (e.g. --output) on an otherwise read-only command such as git diff/log/show - that class is closed by settings.json's own deny entries, not by this script"
} | ConvertTo-Json -Compress | Write-Host

if ($flagged.Count -gt 0) {
    exit 1
}
exit 0
