[CmdletBinding()]
param([int]$TimeoutSeconds = 60)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Starts the domain MCP server the way a client starts it and proves the two
# claims of Issue #38 on the real launcher, not on its source text:
#   1. the session speaks JSON-RPC over the pipes cmd handed to it;
#   2. it executes its own copy, so the Release build output stays writable.
# Without this, a typo in the batch file would leave verify.ps1 green while the
# owner's next client session fails to start.

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$launcherRelative = "scripts\domain-mcp-server.cmd"
$sessionsRoot = Join-Path $repoRoot ".artifacts\domain-mcp-sessions"
$buildOutputHost = Join-Path $repoRoot `
    "tools\DungeonFortress.DomainMcp\bin\Release\net8.0\DungeonFortress.DomainMcp.exe"
$hostName = "DungeonFortress.DomainMcp.exe"

if (-not (Test-Path -LiteralPath $buildOutputHost -PathType Leaf)) {
    throw "Release build output is missing. Build the solution before this check."
}

function Test-FileWritable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $stream = $null
    try {
        $stream = [IO.File]::Open(
            $Path,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
        return $true
    }
    catch {
        return $false
    }
    finally {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Get-SessionEntryNames {
    return @(Get-ChildItem -LiteralPath $sessionsRoot -ErrorAction SilentlyContinue |
        ForEach-Object { $_.Name })
}

# Other client sessions may be connected right now, so only entries created by
# this run are asserted on.
$before = Get-SessionEntryNames

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = "cmd.exe"
$startInfo.Arguments = "/c $launcherRelative"
$startInfo.WorkingDirectory = $repoRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true

$process = [Diagnostics.Process]::new()
$process.StartInfo = $startInfo
if (-not $process.Start()) {
    throw "Unable to start '$launcherRelative'."
}

$sessionDirectory = $null
$stderr = ""
$exitedCleanly = $false
try {
    $process.StandardInput.WriteLine(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":' +
        '{"protocolVersion":"2025-06-18","capabilities":{},' +
        '"clientInfo":{"name":"verify-domain-mcp-launcher","version":"1.0"}}}')
    $process.StandardInput.Flush()

    # A launcher that hangs must fail this check rather than hang verify.ps1.
    $pending = $process.StandardOutput.ReadLineAsync()
    if (-not $pending.Wait($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "The launcher did not answer initialize within $TimeoutSeconds seconds."
    }
    $line = $pending.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "The launcher closed stdout before answering initialize."
    }

    $response = $null
    try {
        $response = $line | ConvertFrom-Json
    }
    catch {
        throw "The launcher wrote non-protocol data to stdout: '$line'."
    }
    if ($response.id -ne 1 -or
        $response.result.serverInfo.name -ne "dungeon-fortress-domain") {
        throw "The launcher answered initialize with an unexpected payload: '$line'."
    }

    $new = @(Get-SessionEntryNames | Where-Object { $before -notcontains $_ })
    $newDirectories = @($new | Where-Object {
        Test-Path -LiteralPath (Join-Path $sessionsRoot $_) -PathType Container
    })
    if ($newDirectories.Count -ne 1) {
        throw "Expected exactly one new session copy, found $($newDirectories.Count)."
    }
    $sessionDirectory = Join-Path $sessionsRoot $newDirectories[0]

    $sessionHost = Join-Path $sessionDirectory $hostName
    if (-not (Test-Path -LiteralPath $sessionHost -PathType Leaf)) {
        throw "The session copy '$sessionDirectory' does not contain $hostName."
    }
    if (Test-FileWritable -Path $sessionHost) {
        throw "The running server does not hold its own copy, so it is executing something else."
    }
    if (-not (Test-FileWritable -Path $buildOutputHost)) {
        throw "A live session still holds the Release build output, which is what Issue #38 fixed."
    }
}
finally {
    # Never throw from here: it would hide why the checks above failed.
    try { $process.StandardInput.Close() } catch { }
    $exitedCleanly = $process.WaitForExit(20000)
    if (-not $exitedCleanly) {
        try { $process.Kill() } catch { }
        $null = $process.WaitForExit(5000)
    }
    try { $stderr = $process.StandardError.ReadToEnd() } catch { $stderr = "" }
}

$exitCode = $process.ExitCode
$process.Dispose()
if (-not $exitedCleanly) {
    throw "The launcher did not exit within 20 seconds after stdin closed."
}
if ($exitCode -ne 0) {
    throw "The launcher exited with code $exitCode."
}
if (Test-Path -LiteralPath $sessionDirectory) {
    throw "The launcher left its session copy '$sessionDirectory' behind."
}
$leftover = @(Get-SessionEntryNames | Where-Object { $before -notcontains $_ })
if ($leftover.Count -ne 0) {
    throw "The launcher left $($leftover -join ', ') behind in .artifacts/domain-mcp-sessions."
}

[ordered]@{
    event = "domain_mcp_launcher_test"
    status = "ok"
    launcher = $launcherRelative
    protocolAnswered = $true
    ranFromSessionCopy = $true
    buildOutputWritableDuringSession = $true
    sessionCopyRemoved = $true
    exitCode = $exitCode
    stderrLines = @($stderr -split "\r?\n" | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }).Count
} | ConvertTo-Json -Compress
