Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$cleanOutput = @(
    "Godot Engine v4.7.1.stable.mono.official.a13da4feb",
    '{"event":"godot_visible_smoke","status":"ok"}',
    "WARNING: This line is not an engine error."
)
$cleanErrors = @(Get-GodotErrorLines -OutputLines $cleanOutput)
if ($cleanErrors.Count -ne 0) {
    throw "The Godot output guard rejected clean output."
}

$errorOutput = @(
    'ERROR: Condition "err != OK" is true.',
    "Godot_console.exe : ERROR: Failed to initialize renderer.",
    "SCRIPT ERROR: Invalid call."
)
$detectedErrors = @(Get-GodotErrorLines -OutputLines $errorOutput)
if ($detectedErrors.Count -ne $errorOutput.Count) {
    throw "The Godot output guard did not detect every ERROR signature."
}

$powershellPath = (Get-Command "powershell" -CommandType Application).Source
$exitZeroErrorRejected = $false
try {
    Invoke-GodotChecked `
        -GodotPath $powershellPath `
        -Arguments @(
            "-NoProfile",
            "-Command",
            '[Console]::Error.WriteLine("ERROR: synthetic engine failure"); exit 0'
        ) 6>$null | Out-Null
}
catch {
    if ($_.Exception.Message -match "unexpected ERROR") {
        $exitZeroErrorRejected = $true
    }
    else {
        throw
    }
}

if (-not $exitZeroErrorRejected) {
    throw "The Godot output guard accepted ERROR output with exit code 0."
}

[ordered]@{
    event = "godot_output_guard_test"
    status = "ok"
    cleanLines = $cleanOutput.Count
    detectedErrorLines = $detectedErrors.Count
    exitZeroErrorRejected = $exitZeroErrorRejected
} | ConvertTo-Json -Compress | Write-Host
