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

function Get-JsonProperty {
    param(
        $Node,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    # Session logs are foreign input: a field may simply be absent, and under
    # Set-StrictMode a plain $node.field would then throw instead of returning
    # nothing.
    if ($null -eq $Node -or $Node -is [string]) {
        return $null
    }
    $property = $Node.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-PayloadSummary {
    param($Payload)

    $type = [string](Get-JsonProperty -Node $Payload -Name "type")
    if ($type -eq "custom_tool_call") {
        $name = [string](Get-JsonProperty -Node $Payload -Name "name")
        $input = [string](Get-JsonProperty -Node $Payload -Name "input")
        return "[tool:$name] $input"
    }
    if ($type -eq "custom_tool_call_output") {
        $texts = @()
        foreach ($o in @(Get-JsonProperty -Node $Payload -Name "output")) {
            if ((Get-JsonProperty -Node $o -Name "type") -eq "input_text") {
                $texts += [string](Get-JsonProperty -Node $o -Name "text")
            }
        }
        return "[tool-output] " + ($texts -join " ")
    }
    if ($type -eq "message") {
        $texts = @()
        foreach ($c in @(Get-JsonProperty -Node $Payload -Name "content")) {
            if ((Get-JsonProperty -Node $c -Name "type") -eq "input_text") {
                $texts += [string](Get-JsonProperty -Node $c -Name "text")
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
            # A truncated or otherwise unparsable line is still evidence: it is
            # reported as it stands and the scan goes on. The tool is meant for
            # situations where something is already broken.
            $summary = $line
            $eventType = "unparsed"
            try {
                $parsed = $line | ConvertFrom-Json
                $eventType = [string](Get-JsonProperty -Node $parsed -Name "type")
                $summary = Get-PayloadSummary -Payload (Get-JsonProperty -Node $parsed -Name "payload")
            }
            catch {
                $summary = $line
                $eventType = "unparsed"
            }
            if ($summary.Length -gt $SnippetChars) {
                $summary = $summary.Substring(0, $SnippetChars)
            }
            Add-Hit `
                -Session $file.Name `
                -LineNumber $lineNumber `
                -EventType $eventType `
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
