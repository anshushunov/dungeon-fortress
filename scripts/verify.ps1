[CmdletBinding()]
param(
    [string]$GodotPath,
    [UInt64]$Seed = 424242
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$verifyRoot = Join-Path $artifactsRoot ("verify-" + [Guid]::NewGuid().ToString("N"))
$solutionPath = Join-Path $repoRoot "DungeonFortress.sln"
$scenarioProject = Join-Path $repoRoot "tests\DungeonFortress.Scenarios\DungeonFortress.Scenarios.csproj"
$scenarioAssembly = Join-Path $repoRoot "tests\DungeonFortress.Scenarios\bin\Release\net8.0\DungeonFortress.Scenarios.dll"
$testProject = Join-Path $repoRoot "tests\DungeonFortress.Simulation.Tests\DungeonFortress.Simulation.Tests.csproj"
$commandsPath = Join-Path $repoRoot "scenarios\smoke.commands.json"
$gameProjectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$gameProjectFile = Join-Path $gameProjectPath "DungeonFortress.Game.csproj"

$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot "dotnet-home"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

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

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)]
        [UInt64]$ScenarioSeed,

        [Parameter(Mandatory = $true)]
        [int]$AgentCount,

        [Parameter(Mandatory = $true)]
        [int]$TickCount,

        [Parameter(Mandatory = $true)]
        [string]$SnapshotPath
    )

    $arguments = @(
        $scenarioAssembly,
        "--seed", $ScenarioSeed.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--agents", $AgentCount.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--ticks", $TickCount.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--commands", $commandsPath,
        "--snapshot", $SnapshotPath
    )

    $output = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { Write-Host $_ }
    if ($exitCode -ne 0) {
        throw "Scenario runner failed with exit code $exitCode."
    }

    $resultLine = $output | Where-Object { $_ -match '"event":"scenario_result"' } |
        Select-Object -Last 1
    if ($null -eq $resultLine) {
        throw "Scenario runner did not emit a scenario_result event."
    }

    return $resultLine | ConvertFrom-Json
}

function Assert-FilesEqual {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPath,

        [Parameter(Mandatory = $true)]
        [string]$ActualPath,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $expected = [IO.File]::ReadAllBytes($ExpectedPath)
    $actual = [IO.File]::ReadAllBytes($ActualPath)

    if ($expected.Length -ne $actual.Length) {
        throw "$Description differs in byte length."
    }

    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($expected[$index] -ne $actual[$index]) {
            throw "$Description differs at byte $index."
        }
    }
}

New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

try {
    $godot = Resolve-GodotExecutable -ExplicitPath $GodotPath
    $godotVersion = Assert-GodotVersion -GodotPath $godot
    $godotNuGetSource = Get-GodotNuGetSource -GodotPath $godot
    Initialize-GodotNuGetEnvironment `
        -ProfileRoot (Join-Path $artifactsRoot "tool-profile") `
        -GodotNuGetSource $godotNuGetSource

    Write-Host "Restoring and building the .NET 8 solution..."
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "restore", $solutionPath
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $solutionPath, "--configuration", "Release", "--no-restore"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "test", $testProject, "--configuration", "Release", "--no-build", "--no-restore"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $gameProjectFile, "--configuration", "Debug", "--no-restore"
    )

    if (-not (Test-Path -LiteralPath $scenarioAssembly -PathType Leaf)) {
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "build", $scenarioProject, "--configuration", "Release", "--no-restore"
        )
    }

    Write-Host "Checking byte-for-byte deterministic snapshots..."
    $sameAPath = Join-Path $verifyRoot "same-a.json"
    $sameBPath = Join-Path $verifyRoot "same-b.json"
    $differentPath = Join-Path $verifyRoot "different.json"
    $differentSeed = if ($Seed -eq [UInt64]::MaxValue) { [UInt64]0 } else { $Seed + 1 }
    $sameA = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 32 -TickCount 256 -SnapshotPath $sameAPath
    $sameB = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 32 -TickCount 256 -SnapshotPath $sameBPath
    $different = Invoke-Scenario -ScenarioSeed $differentSeed -AgentCount 32 -TickCount 256 -SnapshotPath $differentPath
    Assert-FilesEqual -ExpectedPath $sameAPath -ActualPath $sameBPath -Description "Same-seed snapshots"
    if ($sameA.checksum -ne $sameB.checksum) {
        throw "Same-seed scenario checksums differ."
    }
    if ($sameA.checksum -eq $different.checksum) {
        throw "Changing the seed did not change the canonical snapshot checksum."
    }

    Write-Host "Measuring and repeating 1,000 agents x 10,000 ticks..."
    $loadAPath = Join-Path $verifyRoot "load-a.json"
    $loadBPath = Join-Path $verifyRoot "load-b.json"
    $loadA = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 1000 -TickCount 10000 -SnapshotPath $loadAPath
    $loadB = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 1000 -TickCount 10000 -SnapshotPath $loadBPath
    Assert-FilesEqual -ExpectedPath $loadAPath -ActualPath $loadBPath -Description "Load scenario snapshots"

    Write-Host "Running Godot headless smoke..."
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $godotOutput = & $godot --headless --path $gameProjectPath -- --smoke --seed $Seed 2>&1
        $godotExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    $godotOutput | ForEach-Object { Write-Host $_ }
    if ($godotExitCode -ne 0) {
        throw "Godot headless smoke failed with exit code $godotExitCode."
    }

    $successEvent = $godotOutput | Where-Object {
        $_ -match '"event":"godot_headless_smoke"' -and $_ -match '"status":"ok"'
    } | Select-Object -Last 1
    if ($null -eq $successEvent) {
        throw "Godot exited successfully but did not emit a structured success event."
    }

    [ordered]@{
        event = "verification_result"
        status = "ok"
        seed = $Seed
        deterministicChecksum = $sameA.checksum
        changedSeedChecksum = $different.checksum
        loadChecksum = $loadA.checksum
        loadElapsedMilliseconds = @(
            $loadA.elapsedMilliseconds,
            $loadB.elapsedMilliseconds
        )
        godotVersion = $godotVersion
        godotExitCode = $godotExitCode
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedVerifyRoot = [IO.Path]::GetFullPath($verifyRoot)
    $expectedPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if ($resolvedVerifyRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedVerifyRoot)) {
        Remove-Item -LiteralPath $resolvedVerifyRoot -Recurse -Force
    }
}
