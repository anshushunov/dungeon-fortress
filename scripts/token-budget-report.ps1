<#
.SYNOPSIS
Executable version of the token-spend measurement methodology documented in
docs/engineering/TOKEN_BUDGET.md (Issue #303).

.DESCRIPTION
Reads Claude Code transcripts (<TranscriptsRoot>/*.jsonl for main sessions,
<TranscriptsRoot>/*/subagents/*.jsonl for subagents), aggregates token usage
for one closed date slice, and reports: calls, cache read/write, output,
model share, the busiest subagent's context-growth profile, and spend per
merged PR. This replaces the seven jq commands TOKEN_BUDGET.md previously
asked a human to retype for every re-measurement.

Stratification is not an option, it is the point (Issue #303): subagents are
split into "writer" (record-changing: implementation/fixup agents) and
"review" (independent read-only review agents) populations, each reported
per-agent, not as a raw sum - a raw sum conflates two populations of very
different size and cost and makes before/after comparisons vulnerable to a
shift in how many of each kind of agent ran. The split is read from the
launch prompt text (first "user" line of each subagent transcript), the
signal already used consistently across ~150 transcripts in this project's
history; see docs/engineering/TOKEN_BUDGET.md and evidence/303-baseline-
replay.json for the classification patterns and how they were checked.

The script refuses to compute a slice whose -To date is less than two full
closed days before "now": a day is not "closed" just because its calendar
date has passed - a subagent that started before midnight can still be
running (and still appending entries dated for that earlier day) well after
midnight, and Claude Code does not flush/finalize a subagent's transcript
file until it exits. Issue #303 was opened in part because evidence/283-
model-share.json, measured with a one-closed-day margin, moved from 87.99%
to 86.85% opus share within a few hours purely from one such day still being
open.

.PARAMETER From
Slice start date, inclusive, "yyyy-MM-dd".

.PARAMETER To
Slice end date, inclusive, "yyyy-MM-dd". Must be at least two closed days
before -Now (default: the real current time).

.PARAMETER TranscriptsRoot
Directory holding this project's Claude Code transcripts. Defaults to
"$env:USERPROFILE\.claude\projects\C--gamedev-Dungeon-fortress" - the slug
Claude Code has used for every session and worktree of this repository so
far (verified: it is the only project directory under
~/.claude/projects that matches this repository, regardless of which
worktree a given session's cwd was in).

.PARAMETER TaskClass
Optional substring filter (e.g. "tier:standard") applied to the writer
subagent population only. A writer subagent's task class is read from its
launch prompt (the "класс `...`" token); review-agent prompts do not declare
one; agents from before rule 34 introduced task classes do not either, and
are excluded when this filter is set - a known, named limitation, not a bug.

.PARAMETER Now
Override for "the current moment", "yyyy-MM-dd" or a full timestamp. Exists
so the two-closed-day refusal is testable without waiting for the clock.
Defaults to the real current time.

.PARAMETER Json
Emit the machine-readable report as JSON instead of the formatted text
summary.

.PARAMETER SkipMergedPr
Skip the "gh pr list" lookup (spend per merged PR). Useful offline; the rest
of the report does not depend on GitHub access.

.PARAMETER PrLimit
Page size passed to "gh pr list --limit". Default 500.

.PARAMETER DebugClassify
Print "path|role|taskClass|callsInSlice" to stderr for every subagent
transcript as it is classified. Used to audit the writer/review split
against an independent reference extraction; not needed for normal use.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$From,

    [Parameter(Mandatory = $true)]
    [string]$To,

    [string]$TranscriptsRoot,

    [string]$TaskClass,

    [string]$Now,

    [switch]$Json,

    [switch]$SkipMergedPr,

    [int]$PrLimit = 500,

    [switch]$DebugClassify
)

Set-StrictMode -Version Latest
$script:EmitClassifyDebug = [bool]$DebugClassify
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Safe JSON access. Set-StrictMode -Version Latest turns "access a
# NoteProperty a given object does not have" into a terminating error, and
# most transcript lines (tool_use / tool_result / attachment lines) do not
# carry message.usage or message.model at all. PSObject.Properties[...] is a
# lookup, not direct member access, so it never trips strict mode.
# ---------------------------------------------------------------------------
function Get-JsonProp {
    param([object]$Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Get-JsonNumber {
    param([object]$Object, [string]$Name)
    $v = Get-JsonProp -Object $Object -Name $Name
    if ($null -eq $v) { return 0.0 }
    return [double]$v
}

function Get-MessageText {
    param([object]$Message)
    $content = Get-JsonProp -Object $Message -Name "content"
    if ($null -eq $content) { return "" }
    if ($content -is [string]) { return $content }
    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($block in $content) {
        $t = Get-JsonProp -Object $block -Name "type"
        if ($t -eq "text") {
            $text = Get-JsonProp -Object $block -Name "text"
            if ($null -ne $text) { $parts.Add([string]$text) }
        }
    }
    return ($parts -join "`n")
}

# Role classification patterns. Order matters: review-agent prompts routinely
# contain the word "исполнитель" as a negation ("ты НЕ исполнитель этого
# PR"), so the review pattern must be tried first. Checked against the full
# transcript set for this repo (150 subagent files, 2026-08-07): this
# ordered pair classifies all 150 with zero "unclassified" results - see
# evidence/303-baseline-replay.json.
$script:ReviewPattern = '(?i)(review-агент|review agent|reviewer|read-only)'
$script:WriterPattern = '(?i)(записывающ|исполнитель|implementation-агент)'
$script:ClassPattern = '(?i)класс[^`]*`([^`]+)`'

function Read-TranscriptFile {
    param(
        [string]$Path,
        [datetime]$FromDate,
        [datetime]$ToDate,
        [bool]$Classify
    )

    $fromStr = $FromDate.ToString("yyyy-MM-dd")
    $toStr = $ToDate.ToString("yyyy-MM-dd")

    $role = $null
    $taskClass = $null
    $sawFirstUserTurn = $false
    $records = New-Object System.Collections.Generic.List[object]

    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $obj = $null
        try { $obj = $line | ConvertFrom-Json -ErrorAction Stop } catch { continue }

        if ($Classify -and -not $sawFirstUserTurn) {
            $type = Get-JsonProp -Object $obj -Name "type"
            if ($type -eq "user") {
                # Only the FIRST "user"-type line is the launch prompt. Every
                # later "user"-type line is a tool_result turn (the Anthropic
                # API represents tool results as role="user"), and its text
                # is arbitrary command/file output that can accidentally
                # contain "reviewer" or "исполнитель" - classifying on it
                # would silently reclassify agents based on what they
                # happened to read, not on who they were launched as. Stop
                # trying after this one line, matched or not.
                $sawFirstUserTurn = $true
                $text = Get-MessageText -Message (Get-JsonProp -Object $obj -Name "message")
                # Match only the opening line (up to the first real newline),
                # not the whole turn. Every launch prompt in this project's
                # history declares the agent's role in its first sentence
                # ("Ты - записывающий агент...", "Ты - независимый read-only
                # review-агент..."), but the same turn also carries a long
                # injected system-reminder (tool-use rules, the full skills
                # catalogue, CLAUDE.md contents) that routinely contains the
                # word "review" or "reviewer" regardless of the agent's own
                # role (e.g. the "diff-reviewer" subagent-type description,
                # or the "review" MCP server's own instructions block).
                # Matching the full text against $ReviewPattern produced a
                # false "review" classification for writer agents whose
                # prompt opened with "исполнитель" - caught by cross-checking
                # this script's split against a first-line-only reference
                # extraction over all 150 subagent transcripts (see
                # evidence/303-baseline-replay.json).
                $firstLine = ($text -split "`n", 2)[0]
                if ($firstLine.Length -gt 0) {
                    if ($firstLine -match $script:ReviewPattern) {
                        $role = "review"
                    }
                    elseif ($firstLine -match $script:WriterPattern) {
                        $role = "writer"
                    }
                    $m = [regex]::Match($firstLine, $script:ClassPattern)
                    if ($m.Success) { $taskClass = $m.Groups[1].Value.Trim() }
                }
            }
        }

        $timestamp = Get-JsonProp -Object $obj -Name "timestamp"
        if ($null -eq $timestamp) { continue }
        $ts = [string]$timestamp
        if ($ts.Length -lt 10) { continue }
        $day = $ts.Substring(0, 10)
        if ($day -lt $fromStr -or $day -gt $toStr) { continue }

        $message = Get-JsonProp -Object $obj -Name "message"
        $usage = Get-JsonProp -Object $message -Name "usage"
        if ($null -eq $usage) { continue }

        $input = Get-JsonNumber -Object $usage -Name "input_tokens"
        $cacheWrite = Get-JsonNumber -Object $usage -Name "cache_creation_input_tokens"
        $cacheRead = Get-JsonNumber -Object $usage -Name "cache_read_input_tokens"
        $output = Get-JsonNumber -Object $usage -Name "output_tokens"
        $model = Get-JsonProp -Object $message -Name "model"

        $records.Add([pscustomobject]@{
                Day        = $day
                Model      = $model
                Input      = $input
                CacheWrite = $cacheWrite
                CacheRead  = $cacheRead
                Output     = $output
                Context    = $input + $cacheWrite + $cacheRead
            })
    }

    if ($Classify -and $null -eq $role) { $role = "unclassified" }
    if ($Classify -and $script:EmitClassifyDebug) {
        [Console]::Error.WriteLine("$Path|$role|$taskClass|$($records.Count)")
    }

    return [pscustomobject]@{
        Path      = $Path
        Role      = $role
        TaskClass = $taskClass
        Records   = $records
    }
}

function New-Aggregate {
    param([System.Collections.Generic.List[object]]$Records)

    $calls = $Records.Count
    if ($calls -eq 0) {
        return [pscustomobject]@{
            Calls = 0; Input = 0.0; CacheWrite = 0.0; CacheRead = 0.0; Output = 0.0; AvgContext = 0
        }
    }

    $input = 0.0; $cacheWrite = 0.0; $cacheRead = 0.0; $output = 0.0; $context = 0.0
    foreach ($r in $Records) {
        $input += $r.Input
        $cacheWrite += $r.CacheWrite
        $cacheRead += $r.CacheRead
        $output += $r.Output
        $context += $r.Context
    }

    return [pscustomobject]@{
        Calls      = $calls
        Input      = $input
        CacheWrite = $cacheWrite
        CacheRead  = $cacheRead
        Output     = $output
        AvgContext = [math]::Floor($context / $calls)
    }
}

function Get-RecordsFromFiles {
    param([object[]]$Files)
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($f in $Files) { $list.AddRange($f.Records) }
    # The unary comma is load-bearing: PowerShell unrolls an IEnumerable
    # return value onto the output stream, so a zero-element List[object]
    # returned as plain "$list" is captured by the caller as $null, not as
    # an empty list - New-Aggregate's -Records parameter would then bind
    # $null and "$Records.Count" throws under Set-StrictMode.
    return , $list
}

# Per-agent normalization - this, not the raw sum, is what stratification is
# for (Issue #303 non-goal note: "нормирует на число субагентов, а не даёт
# сырую сумму").
function Get-PerAgent {
    param($Agg, [int]$AgentCount)
    if ($AgentCount -eq 0) {
        return [pscustomobject]@{ Calls = 0.0; CacheRead = 0.0; CacheWrite = 0.0; Output = 0.0 }
    }
    return [pscustomobject]@{
        Calls      = $Agg.Calls / $AgentCount
        CacheRead  = $Agg.CacheRead / $AgentCount
        CacheWrite = $Agg.CacheWrite / $AgentCount
        Output     = $Agg.Output / $AgentCount
    }
}

function Format-M {
    param([double]$Value)
    return [math]::Round($Value / 1000000.0, 1)
}

try {
    # -- resolve inputs -----------------------------------------------------
    if (-not $TranscriptsRoot) {
        $TranscriptsRoot = Join-Path $env:USERPROFILE ".claude\projects\C--gamedev-Dungeon-fortress"
    }
    if (-not (Test-Path -LiteralPath $TranscriptsRoot -PathType Container)) {
        throw "Transcripts root not found: $TranscriptsRoot"
    }

    $invariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
    $fromDate = [datetime]::ParseExact($From, "yyyy-MM-dd", $invariantCulture)
    $toDate = [datetime]::ParseExact($To, "yyyy-MM-dd", $invariantCulture)
    if ($toDate -lt $fromDate) {
        throw "-To ($To) is before -From ($From)."
    }

    $nowValue = if ($Now) { [datetime]::Parse($Now, $invariantCulture) } else { Get-Date }
    $today = $nowValue.Date
    $closedCutoff = $today.AddDays(-2)

    # -- refuse an unclosed slice, first, before any file IO -----------------
    if ($toDate -gt $closedCutoff) {
        $shortByDays = [int]([math]::Ceiling(($toDate - $closedCutoff).TotalDays))
        throw (
            "REFUSED_UNCLOSED_SLICE: slice ending $To is less than two closed " +
            "days before 'now' ($($today.ToString('yyyy-MM-dd'))). A calendar " +
            "day is not closed just because its date has passed: a subagent " +
            "that started before midnight can still be running - and Claude " +
            "Code does not finalize its transcript file until it exits - so a " +
            "day can keep gaining entries for hours after the date changes. " +
            "Measured on this project (evidence/283-model-share.json, PR " +
            "#291): opus share moved 87.99% -> 86.85% within a few hours, " +
            "purely from one such still-open day. Latest closed -To for " +
            "'now' = $($today.ToString('yyyy-MM-dd')) is " +
            "$($closedCutoff.ToString('yyyy-MM-dd')); move -To back " +
            "$shortByDays day(s), or re-run later."
        )
    }

    # -- gather transcript files ---------------------------------------------
    $mainFiles = @(Get-ChildItem -LiteralPath $TranscriptsRoot -Filter "*.jsonl" -File |
            Select-Object -ExpandProperty FullName)

    $subagentFiles = @(Get-ChildItem -LiteralPath $TranscriptsRoot -Recurse -Filter "*.jsonl" -File |
            Where-Object { $_.Directory.Name -eq "subagents" } |
            Select-Object -ExpandProperty FullName)

    # -- main sessions ---------------------------------------------------------
    $mainRecords = New-Object System.Collections.Generic.List[object]
    foreach ($f in $mainFiles) {
        $r = Read-TranscriptFile -Path $f -FromDate $fromDate -ToDate $toDate -Classify:$false
        $mainRecords.AddRange($r.Records)
    }
    $mainAgg = New-Aggregate -Records $mainRecords

    # -- subagents, classified -------------------------------------------------
    $subFiles = New-Object System.Collections.Generic.List[object]
    foreach ($f in $subagentFiles) {
        $subFiles.Add((Read-TranscriptFile -Path $f -FromDate $fromDate -ToDate $toDate -Classify:$true))
    }
    $activeSubFiles = @($subFiles | Where-Object { $_.Records.Count -gt 0 })

    $writerFilesAll = @($activeSubFiles | Where-Object { $_.Role -eq "writer" })
    $reviewFiles = @($activeSubFiles | Where-Object { $_.Role -eq "review" })
    $unclassifiedFiles = @($activeSubFiles | Where-Object { $_.Role -eq "unclassified" })

    $writerFiles = $writerFilesAll
    if ($TaskClass) {
        $writerFiles = @($writerFilesAll | Where-Object { $_.TaskClass -and $_.TaskClass -like "*$TaskClass*" })
    }

    $allSubRecords = Get-RecordsFromFiles -Files $activeSubFiles
    $writerRecords = Get-RecordsFromFiles -Files $writerFiles
    $reviewRecords = Get-RecordsFromFiles -Files $reviewFiles
    $unclassifiedRecords = Get-RecordsFromFiles -Files $unclassifiedFiles

    $subAllAgg = New-Aggregate -Records $allSubRecords
    $writerAgg = New-Aggregate -Records $writerRecords
    $reviewAgg = New-Aggregate -Records $reviewRecords
    $unclassifiedAgg = New-Aggregate -Records $unclassifiedRecords

    $writerPerAgent = Get-PerAgent -Agg $writerAgg -AgentCount $writerFiles.Count
    $reviewPerAgent = Get-PerAgent -Agg $reviewAgg -AgentCount $reviewFiles.Count

    # -- totals -----------------------------------------------------------------
    $totalCalls = $mainAgg.Calls + $subAllAgg.Calls
    $totalInput = $mainAgg.Input + $subAllAgg.Input
    $totalCacheWrite = $mainAgg.CacheWrite + $subAllAgg.CacheWrite
    $totalCacheRead = $mainAgg.CacheRead + $subAllAgg.CacheRead
    $totalOutput = $mainAgg.Output + $subAllAgg.Output

    # -- model distribution -------------------------------------------------------
    $allRecords = New-Object System.Collections.Generic.List[object]
    $allRecords.AddRange($mainRecords)
    $allRecords.AddRange($allSubRecords)

    $modelGroups = @($allRecords | Where-Object { $_.Model } | Group-Object -Property Model)
    $modelTotal = 0
    foreach ($g in $modelGroups) { $modelTotal += $g.Count }
    $models = @($modelGroups | ForEach-Object {
            [pscustomobject]@{
                Model        = $_.Name
                Calls        = $_.Count
                SharePercent = if ($modelTotal -gt 0) { [math]::Round(($_.Count / $modelTotal) * 100, 1) } else { 0 }
            }
        } | Sort-Object -Property Calls -Descending)

    # -- by day -------------------------------------------------------------------
    $byDay = @($allRecords | Group-Object -Property Day | Sort-Object -Property Name | ForEach-Object {
            $g = $_.Group
            [pscustomobject]@{
                Day        = $_.Name
                Calls      = $g.Count
                CacheRead  = ($g | Measure-Object -Property CacheRead -Sum).Sum
                CacheWrite = ($g | Measure-Object -Property CacheWrite -Sum).Sum
                Output     = ($g | Measure-Object -Property Output -Sum).Sum
            }
        })

    # -- context-growth profile of the single busiest subagent -----------------
    # "Busiest" = highest cache_read total in-slice, matching TOKEN_BUDGET.md's
    # "самые дорогие субагенты" ranking.
    $busiest = $null
    $busiestCacheRead = -1.0
    foreach ($sf in $activeSubFiles) {
        $cr = ($sf.Records | Measure-Object -Property CacheRead -Sum).Sum
        if ($cr -gt $busiestCacheRead) {
            $busiestCacheRead = $cr
            $busiest = $sf
        }
    }

    $growthSeries = @()
    if ($busiest) {
        for ($i = 0; $i -lt $busiest.Records.Count; $i++) {
            if ($i % 50 -eq 0) {
                $rec = $busiest.Records[$i]
                $growthSeries += [pscustomobject]@{
                    Call     = $i + 1
                    ContextK = [math]::Floor(($rec.CacheRead + $rec.CacheWrite) / 1000)
                }
            }
        }
    }

    # -- spend per merged PR --------------------------------------------------
    $mergedPr = $null
    if (-not $SkipMergedPr) {
        try {
            $raw = & gh pr list --state merged --limit $PrLimit --json number,mergedAt 2>$null
            if ($LASTEXITCODE -ne 0) { throw "gh pr list exited with code $LASTEXITCODE" }
            $prs = $raw | ConvertFrom-Json
            $lowerBound = $From
            $upperBoundExclusive = $toDate.AddDays(1).ToString("yyyy-MM-dd")
            $count = 0
            foreach ($pr in $prs) {
                $mergedAtProp = Get-JsonProp -Object $pr -Name "mergedAt"
                if ($null -eq $mergedAtProp) { continue }
                $mergedAt = [string]$mergedAtProp
                # mergedAt is a full ISO-8601 timestamp; string comparison
                # against plain "yyyy-MM-dd" bounds is exact because the
                # timestamp's date prefix sorts identically to the date
                # alone, and ">= lower, < upper(To+1)" makes the boundary
                # "strictly less than the day after To" rather than "<= To",
                # which silently drops the whole last day (a bug once found
                # in TOKEN_BUDGET.md's own worked example).
                if ($mergedAt -ge $lowerBound -and $mergedAt -lt $upperBoundExclusive) {
                    $count++
                }
            }
            $mergedPr = [pscustomobject]@{
                Count         = $count
                CacheReadPerPr = if ($count -gt 0) { $totalCacheRead / $count } else { $null }
                OutputPerPr    = if ($count -gt 0) { $totalOutput / $count } else { $null }
            }
        }
        catch {
            Write-Warning "Merged-PR lookup failed ($($_.Exception.Message)); 'mergedPr' omitted from the report."
            $mergedPr = $null
        }
    }

    # -- assemble result ------------------------------------------------------
    $result = [ordered]@{
        from            = $From
        to              = $To
        now             = $nowValue.ToString("yyyy-MM-dd HH:mm:ss")
        closedCutoff    = $closedCutoff.ToString("yyyy-MM-dd")
        transcriptsRoot = $TranscriptsRoot
        taskClassFilter = $TaskClass
        mainSessions    = [ordered]@{
            calls            = $mainAgg.Calls
            inputTokens      = $mainAgg.Input
            cacheWriteTokens = $mainAgg.CacheWrite
            cacheReadTokens  = $mainAgg.CacheRead
            outputTokens     = $mainAgg.Output
            avgContext       = $mainAgg.AvgContext
        }
        subagents       = [ordered]@{
            all           = [ordered]@{
                agentCount       = $activeSubFiles.Count
                calls            = $subAllAgg.Calls
                inputTokens      = $subAllAgg.Input
                cacheWriteTokens = $subAllAgg.CacheWrite
                cacheReadTokens  = $subAllAgg.CacheRead
                outputTokens     = $subAllAgg.Output
                avgContext       = $subAllAgg.AvgContext
            }
            writer        = [ordered]@{
                agentCount       = $writerFiles.Count
                calls            = $writerAgg.Calls
                cacheWriteTokens = $writerAgg.CacheWrite
                cacheReadTokens  = $writerAgg.CacheRead
                outputTokens     = $writerAgg.Output
                perAgent         = [ordered]@{
                    calls            = $writerPerAgent.Calls
                    cacheWriteTokens = $writerPerAgent.CacheWrite
                    cacheReadTokens  = $writerPerAgent.CacheRead
                    outputTokens     = $writerPerAgent.Output
                }
            }
            review        = [ordered]@{
                agentCount       = $reviewFiles.Count
                calls            = $reviewAgg.Calls
                cacheWriteTokens = $reviewAgg.CacheWrite
                cacheReadTokens  = $reviewAgg.CacheRead
                outputTokens     = $reviewAgg.Output
                perAgent         = [ordered]@{
                    calls            = $reviewPerAgent.Calls
                    cacheWriteTokens = $reviewPerAgent.CacheWrite
                    cacheReadTokens  = $reviewPerAgent.CacheRead
                    outputTokens     = $reviewPerAgent.Output
                }
            }
            unclassified  = [ordered]@{
                agentCount = $unclassifiedFiles.Count
                calls      = $unclassifiedAgg.Calls
            }
        }
        totals          = [ordered]@{
            calls            = $totalCalls
            inputTokens      = $totalInput
            cacheWriteTokens = $totalCacheWrite
            cacheReadTokens  = $totalCacheRead
            outputTokens     = $totalOutput
        }
        models          = @($models | ForEach-Object {
                [ordered]@{ model = $_.Model; calls = $_.Calls; sharePercent = $_.SharePercent }
            })
        byDay           = @($byDay | ForEach-Object {
                [ordered]@{
                    day        = $_.Day
                    calls      = $_.Calls
                    cacheRead  = $_.CacheRead
                    cacheWrite = $_.CacheWrite
                    output     = $_.Output
                }
            })
        contextGrowth   = if ($busiest) {
            [ordered]@{
                file            = [IO.Path]::GetFileName($busiest.Path)
                calls           = $busiest.Records.Count
                cacheReadTokens = $busiestCacheRead
                series          = @($growthSeries | ForEach-Object { [ordered]@{ call = $_.Call; contextK = $_.ContextK } })
            }
        }
        else { $null }
        mergedPr        = if ($mergedPr) {
            [ordered]@{ count = $mergedPr.Count; cacheReadPerPr = $mergedPr.CacheReadPerPr; outputPerPr = $mergedPr.OutputPerPr }
        }
        else { $null }
    }

    if ($Json) {
        $result | ConvertTo-Json -Depth 10
        return
    }

    # -- formatted text summary ------------------------------------------------
    Write-Host "Token budget report: $From .. $To (now $($result.now), closed cutoff $($result.closedCutoff))"
    Write-Host ""
    Write-Host "Summary (calls / input M / cache-write M / cache-read M / output M / avg context)"
    Write-Host ("  main sessions   {0,7} {1,8} {2,8} {3,8} {4,8} {5,10}" -f `
            $mainAgg.Calls, (Format-M $mainAgg.Input), (Format-M $mainAgg.CacheWrite), (Format-M $mainAgg.CacheRead), (Format-M $mainAgg.Output), $mainAgg.AvgContext)
    Write-Host ("  subagents (all) {0,7} {1,8} {2,8} {3,8} {4,8} {5,10}" -f `
            $subAllAgg.Calls, (Format-M $subAllAgg.Input), (Format-M $subAllAgg.CacheWrite), (Format-M $subAllAgg.CacheRead), (Format-M $subAllAgg.Output), $subAllAgg.AvgContext)
    Write-Host ("  total           {0,7} {1,8} {2,8} {3,8} {4,8}" -f `
            $totalCalls, (Format-M $totalInput), (Format-M $totalCacheWrite), (Format-M $totalCacheRead), (Format-M $totalOutput))
    Write-Host ""
    Write-Host ("Stratification: {0} agents/{1} classes filter='{2}'" -f $activeSubFiles.Count, $activeSubFiles.Count, $TaskClass)
    Write-Host ("  writer subagents: {0} agents, {1} calls, {2} M cache-read total, {3:N1} M cache-read PER AGENT" -f `
            $writerFiles.Count, $writerAgg.Calls, (Format-M $writerAgg.CacheRead), (Format-M $writerPerAgent.CacheRead))
    Write-Host ("  review subagents: {0} agents, {1} calls, {2} M cache-read total, {3:N1} M cache-read PER AGENT" -f `
            $reviewFiles.Count, $reviewAgg.Calls, (Format-M $reviewAgg.CacheRead), (Format-M $reviewPerAgent.CacheRead))
    if ($unclassifiedFiles.Count -gt 0) {
        Write-Host ("  UNCLASSIFIED: {0} agents did not match either role pattern - inspect before trusting this split." -f $unclassifiedFiles.Count) -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "Models:"
    foreach ($m in $models) {
        Write-Host ("  {0,6}  {1,-20} {2,5:N1}%" -f $m.Calls, $m.Model, $m.SharePercent)
    }
    Write-Host ""
    if ($busiest) {
        Write-Host ("Context growth, busiest subagent ({0}, {1} calls, {2} M cache-read):" -f `
                ([IO.Path]::GetFileName($busiest.Path)), $busiest.Records.Count, (Format-M $busiestCacheRead))
        foreach ($p in $growthSeries) {
            Write-Host ("  {0,4}: {1,4} k" -f $p.Call, $p.ContextK)
        }
    }
    Write-Host ""
    if ($mergedPr) {
        Write-Host ("Merged PRs in slice: {0}" -f $mergedPr.Count)
        if ($mergedPr.Count -gt 0) {
            Write-Host ("  {0:N1} M cache-read / PR, {1:N2} M output / PR" -f (Format-M $mergedPr.CacheReadPerPr), (Format-M $mergedPr.OutputPerPr))
        }
    }
    else {
        Write-Host "Merged PRs in slice: skipped or unavailable (see warnings above)."
    }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    [Console]::Error.WriteLine($_.ScriptStackTrace)
    exit 1
}
