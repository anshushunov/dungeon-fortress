[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Query,

    [string]$SessionsRoot,

    [int]$MaxHits = 20,

    [int]$SnippetChars = 2000,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $SessionsRoot) {
    $SessionsRoot = Join-Path $HOME ".codex\sessions"
}
if (-not (Test-Path -LiteralPath $SessionsRoot -PathType Container)) {
    Write-Host "Codex sessions root not found: $SessionsRoot"
    exit 2
}

$script:hits = [System.Collections.Generic.List[object]]::new()

function Add-Hit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Session,

        [Parameter(Mandatory = $true)]
        [int]$LineNumber,

        [Parameter(Mandatory = $true)]
        [string]$EventType,

        [Parameter(Mandatory = $true)]
        [string]$Summary
    )

    if ($script:hits.Count -ge $MaxHits) {
        return
    }
    $script:hits.Add([pscustomobject]@{
        session = $Session
        line = $LineNumber
        eventType = $EventType
        summary = $Summary
    })
}

function Get-PayloadSummary {
    param($Payload)

    $type = ""
    if ($null -ne $Payload) {
        $type = [string]$Payload.type
    }
    if ($type -eq "custom_tool_call") {
        $name = [string]$Payload.name
        $input = ""
        if ($null -ne $Payload.input) {
            $input = [string]$Payload.input
        }
        return "[tool:$name] $input"
    }
    if ($type -eq "custom_tool_call_output") {
        $texts = @()
        if ($null -ne $Payload.output) {
            foreach ($o in $Payload.output) {
                if ($null -ne $o -and $o.type -eq "input_text") {
                    $texts += [string]$o.text
                }
            }
        }
        return "[tool-output] " + ($texts -join " ")
    }
    if ($type -eq "message") {
        $texts = @()
        if ($null -ne $Payload.content) {
            foreach ($c in $Payload.content) {
                if ($null -ne $c -and $c.type -eq "input_text") {
                    $texts += [string]$c.text
                }
            }
        }
        return "[message] " + ($texts -join " ")
    }
    return "[$type]"
}

$jsonlFiles = Get-ChildItem -LiteralPath $SessionsRoot -Filter "*.jsonl" -File -Recurse -ErrorAction SilentlyContinue
foreach ($file in $jsonlFiles) {
    $lineNumber = 0
    try {
        $stream = [IO.File]::OpenText($file.FullName)
    }
    catch {
        continue
    }
    try {
        while ($null -ne ($line = $stream.ReadLine())) {
            $lineNumber++
            if ($line.IndexOf($Query, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }
            if ($script:hits.Count -ge $MaxHits) {
                break
            }
            $summary = ""
            try {
                $parsed = $line | ConvertFrom-Json
                $summary = Get-PayloadSummary -Payload $parsed.payload
            }
            catch {
                $summary = $line
            }
            if ($summary.Length -gt $SnippetChars) {
                $summary = $summary.Substring(0, $SnippetChars)
            }
            Add-Hit `
                -Session $file.Name `
                -LineNumber $lineNumber `
                -EventType ([string]$parsed.type) `
                -Summary $summary
        }
    }
    finally {
        $stream.Dispose()
    }
    if ($script:hits.Count -ge $MaxHits) {
        break
    }
}

if (-not $Quiet) {
    if ($script:hits.Count -eq 0) {
        Write-Host "No matches for '$Query' in $SessionsRoot"
    }
    else {
        foreach ($h in $script:hits) {
            $line = [ordered]@{
                session = $h.session
                line = $h.line
                eventType = $h.eventType
                summary = $h.summary
            } | ConvertTo-Json -Compress
            Write-Host $line
        }
        Write-Host ""
        Write-Host ("Hits: {0}" -f $script:hits.Count)
    }
}
elseif ($script:hits.Count -eq 0) {
    exit 1
}
