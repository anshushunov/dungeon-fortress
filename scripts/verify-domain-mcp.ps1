[CmdletBinding()]
param(
    [UInt64]$Seed = 424242,
    [int]$Observations = 5,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($Observations -lt 1) {
    throw "Observations must be positive."
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = Join-Path $repoRoot ".artifacts"
$probeRoot = Join-Path $artifactsRoot ("domain-mcp-" + [Guid]::NewGuid().ToString("N"))
$serverProject = Join-Path $repoRoot "tools\DungeonFortress.DomainMcp\DungeonFortress.DomainMcp.csproj"
$serverAssembly = Join-Path $repoRoot "tools\DungeonFortress.DomainMcp\bin\Release\net8.0\DungeonFortress.DomainMcp.dll"
$scenarioAssembly = Join-Path $repoRoot "tests\DungeonFortress.Scenarios\bin\Release\net8.0\DungeonFortress.Scenarios.dll"
$commandsPath = Join-Path $repoRoot "scenarios\smoke.commands.json"
$cliSnapshotPath = Join-Path $probeRoot "cli.json"
$changedSeedSnapshotPath = Join-Path $probeRoot "cli-changed-seed.json"
$changedSeed = if ($Seed -eq [UInt64]::MaxValue) { $Seed - 1 } else { $Seed + 1 }

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Write-ProtocolMessage {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string]$Json
    )

    $Process.StandardInput.WriteLine($Json)
    $Process.StandardInput.Flush()
}

function Read-ProtocolMessage {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.Process]$Process
    )

    $line = $Process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) {
        throw "The domain MCP closed stdout before returning a protocol response."
    }

    try {
        return $line | ConvertFrom-Json
    }
    catch {
        throw "Domain MCP stdout contained non-protocol data: '$line'."
    }
}

function Assert-BytesEqual {
    param(
        [Parameter(Mandatory = $true)]
        [byte[]]$Expected,

        [Parameter(Mandatory = $true)]
        [byte[]]$Actual
    )

    if ($Expected.Length -ne $Actual.Length) {
        throw "CLI and MCP canonical snapshots differ in byte length."
    }

    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Expected[$index] -ne $Actual[$index]) {
            throw "CLI and MCP canonical snapshots differ at byte $index."
        }
    }
}

New-Item -ItemType Directory -Force -Path $probeRoot | Out-Null

try {
    if (-not $NoBuild) {
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "restore", $serverProject, "--locked-mode"
        )
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "build", $serverProject, "--configuration", "Release", "--no-restore"
        )
    }

    if (-not (Test-Path -LiteralPath $serverAssembly -PathType Leaf)) {
        throw "Domain MCP Release assembly is missing. Build it before using -NoBuild."
    }
    if (-not (Test-Path -LiteralPath $scenarioAssembly -PathType Leaf)) {
        throw "Scenario Release assembly is missing. Build the solution first."
    }

    Invoke-Checked -FilePath "dotnet" -Arguments @(
        $scenarioAssembly,
        "--seed", $Seed.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--agents", "32",
        "--ticks", "256",
        "--commands", $commandsPath,
        "--snapshot", $cliSnapshotPath
    )
    $cliBytes = [IO.File]::ReadAllBytes($cliSnapshotPath)
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        $scenarioAssembly,
        "--seed", $changedSeed.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--agents", "32",
        "--ticks", "256",
        "--commands", $commandsPath,
        "--snapshot", $changedSeedSnapshotPath
    )
    $changedSeedCliBytes = [IO.File]::ReadAllBytes($changedSeedSnapshotPath)
    try {
        Assert-BytesEqual -Expected $cliBytes -Actual $changedSeedCliBytes
        throw "Different seeds unexpectedly produced identical CLI snapshots."
    }
    catch {
        if ($_.Exception.Message -notlike "CLI and MCP canonical snapshots differ*") {
            throw
        }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = "`"$serverAssembly`" --root `"$repoRoot`""

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Unable to start the domain MCP process."
    }

    $latencies = @()
    $failures = 0
    $referenceChecksum = $null
    $stderr = ""
    try {
        Write-ProtocolMessage -Process $process -Json (
            '{"jsonrpc":"2.0","id":1,"method":"initialize","params":' +
            '{"protocolVersion":"2025-06-18","capabilities":{},' +
            '"clientInfo":{"name":"verify-domain-mcp","version":"1.0"}}}'
        )
        $initialize = Read-ProtocolMessage -Process $process
        if ($initialize.id -ne 1) {
            throw "Domain MCP initialize response has an unexpected id."
        }

        Write-ProtocolMessage -Process $process -Json (
            '{"jsonrpc":"2.0","method":"notifications/initialized"}'
        )
        Write-ProtocolMessage -Process $process -Json (
            '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
        )
        $toolList = Read-ProtocolMessage -Process $process
        $toolNames = @($toolList.result.tools | ForEach-Object { $_.name } | Sort-Object)
        if (($toolNames -join ",") -ne "bridge_status,simulation_run") {
            throw "Domain MCP exposed an unexpected tool surface: $($toolNames -join ', ')."
        }

        $changedSeedRequest = [ordered]@{
            jsonrpc = "2.0"
            id = 9
            method = "tools/call"
            params = [ordered]@{
                name = "simulation_run"
                arguments = [ordered]@{
                    seed = $changedSeed
                    agentCount = 32
                    ticks = 256
                    commandsPath = "scenarios/smoke.commands.json"
                }
            }
        } | ConvertTo-Json -Compress -Depth 6
        Write-ProtocolMessage -Process $process -Json $changedSeedRequest
        $changedSeedResponse = Read-ProtocolMessage -Process $process
        $changedSeedIsErrorProperty =
            $changedSeedResponse.result.PSObject.Properties["isError"]
        if ($changedSeedResponse.id -ne 9 -or
            ($null -ne $changedSeedIsErrorProperty -and
                $changedSeedIsErrorProperty.Value -eq $true)) {
            throw "Different-seed MCP call failed."
        }
        $changedSeedCanonicalJson =
            [string]$changedSeedResponse.result.structuredContent.canonicalJson
        $changedSeedMcpBytes = [Text.Encoding]::UTF8.GetBytes(
            $changedSeedCanonicalJson)
        Assert-BytesEqual -Expected $changedSeedCliBytes -Actual $changedSeedMcpBytes
        $changedSeedChecksum =
            [string]$changedSeedResponse.result.structuredContent.checksum

        for ($index = 0; $index -lt $Observations; $index++) {
            $requestId = 10 + $index
            $request = [ordered]@{
                jsonrpc = "2.0"
                id = $requestId
                method = "tools/call"
                params = [ordered]@{
                    name = "simulation_run"
                    arguments = [ordered]@{
                        seed = $Seed
                        agentCount = 32
                        ticks = 256
                        commandsPath = "scenarios/smoke.commands.json"
                    }
                }
            } | ConvertTo-Json -Compress -Depth 6

            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Write-ProtocolMessage -Process $process -Json $request
            $response = Read-ProtocolMessage -Process $process
            $stopwatch.Stop()

            $isErrorProperty = $response.result.PSObject.Properties["isError"]
            $isError = $null -ne $isErrorProperty -and $isErrorProperty.Value -eq $true
            if ($response.id -ne $requestId -or $isError) {
                $failures++
                continue
            }

            $canonicalJson = [string]$response.result.structuredContent.canonicalJson
            $mcpBytes = [Text.Encoding]::UTF8.GetBytes($canonicalJson)
            Assert-BytesEqual -Expected $cliBytes -Actual $mcpBytes

            $checksum = [string]$response.result.structuredContent.checksum
            if ($null -eq $referenceChecksum) {
                $referenceChecksum = $checksum
                if ($referenceChecksum -eq $changedSeedChecksum) {
                    throw "Different seeds unexpectedly returned the same MCP checksum."
                }
            }
            elseif ($checksum -ne $referenceChecksum) {
                throw "Repeated MCP calls returned different checksums."
            }

            $latencies += [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        }
    }
    finally {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(5000)) {
            $process.Kill()
            throw "Domain MCP did not shut down within 5 seconds after stdin closed."
        }

        $remainingStdout = $process.StandardOutput.ReadToEnd()
        if (-not [string]::IsNullOrWhiteSpace($remainingStdout)) {
            foreach ($line in $remainingStdout -split "\r?\n") {
                if (-not [string]::IsNullOrWhiteSpace($line)) {
                    try {
                        $null = $line | ConvertFrom-Json
                    }
                    catch {
                        throw "Domain MCP stdout contained non-protocol data: '$line'."
                    }
                }
            }
        }

        $stderr = $process.StandardError.ReadToEnd()
        $process.Dispose()
    }

    $sorted = @($latencies | Sort-Object)
    $median = if ($sorted.Count -eq 0) {
        $null
    }
    elseif (($sorted.Count % 2) -eq 1) {
        $sorted[[int][Math]::Floor($sorted.Count / 2)]
    }
    else {
        ($sorted[($sorted.Count / 2) - 1] + $sorted[$sorted.Count / 2]) / 2
    }
    $maximum = if ($sorted.Count -eq 0) {
        $null
    }
    else {
        ($sorted | Measure-Object -Maximum).Maximum
    }

    [ordered]@{
        event = "domain_mcp_verification"
        status = if ($failures -eq 0) { "ok" } else { "error" }
        observations = $Observations
        successfulObservations = $latencies.Count
        failureCount = $failures
        medianMilliseconds = $median
        maximumMilliseconds = $maximum
        checksum = $referenceChecksum
        changedSeedChecksum = $changedSeedChecksum
        canonicalBytes = $cliBytes.Length
        toolCount = 2
        cleanShutdown = $true
        stderrLines = @($stderr -split "\r?\n" | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        }).Count
    } | ConvertTo-Json -Compress | Write-Host

    if ($failures -ne 0) {
        throw "Domain MCP verification recorded $failures failed observation(s)."
    }
}
finally {
    $resolvedProbeRoot = [IO.Path]::GetFullPath($probeRoot)
    $expectedPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if ($resolvedProbeRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedProbeRoot)) {
        Remove-Item -LiteralPath $resolvedProbeRoot -Recurse -Force
    }
}
