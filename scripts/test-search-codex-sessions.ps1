[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "search-codex-sessions.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("search-codex-sessions-test-" + [Guid]::NewGuid().ToString("N"))
$utf8 = [Text.UTF8Encoding]::new($false)

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $sessionDir = Join-Path $testRoot "sessions"
    New-Item -ItemType Directory -Force -Path $sessionDir | Out-Null
    $sessionPath = Join-Path $sessionDir "rollout-test.jsonl"
    $sessionLines = @(
        '{"timestamp":"2026-08-02T08:40:44Z","type":"session_meta","payload":{"type":"session_meta"}}',
        '{"timestamp":"2026-08-02T08:41:00Z","type":"response_item","payload":{"type":"custom_tool_call","name":"exec","input":"python remove_chroma_key.py --input src.png --despill"}}',
        '{"timestamp":"2026-08-02T08:41:10Z","type":"response_item","payload":{"type":"custom_tool_call_output","output":[{"type":"input_text","text":"Transparent pixels: 1254108/1572864"}]}}',
        '{"timestamp":"2026-08-02T08:41:20Z","type":"response_item","payload":{"type":"image_generation_end"}}'
    )
    [IO.File]::WriteAllLines($sessionPath, $sessionLines, $utf8)

    $output = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -Query "remove_chroma_key.py" `
        -SessionsRoot $testRoot `
        -MaxHits 5 2>&1)
    $outputText = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($outputText -notmatch "rollout-test.jsonl" -or
        $outputText -notmatch "\[tool:exec\]" -or
        $outputText -notmatch "remove_chroma_key.py --input src.png --despill") {
        throw "Tool-call input was not found or not summarized. Output: $outputText"
    }

    $outputNoHit = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -Query "this-string-does-not-exist" `
        -SessionsRoot $testRoot `
        -MaxHits 5 `
        -Quiet 2>&1)
    if ($LASTEXITCODE -ne 1) {
        throw "No-hit quiet run should exit 1, got $LASTEXITCODE."
    }

    $outputToolOutput = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -Query "Transparent pixels" `
        -SessionsRoot $testRoot `
        -MaxHits 5 2>&1)
    $toolOutputText = ($outputToolOutput | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($toolOutputText -notmatch "\[tool-output\]" -or
        $toolOutputText -notmatch "1254108/1572864") {
        throw "Tool output was not found or not summarized. Output: $toolOutputText"
    }

    [ordered]@{
        event = "search_codex_sessions_test"
        status = "ok"
        toolCallFound = $true
        toolOutputFound = $true
        noHitQuietExits1 = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
