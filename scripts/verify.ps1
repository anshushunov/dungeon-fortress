[CmdletBinding()]
param(
    [string]$GodotPath,
    [UInt64]$Seed = 424242
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "HudVerification.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$verifyRoot = Join-Path $artifactsRoot ("verify-" + [Guid]::NewGuid().ToString("N"))
$solutionPath = Join-Path $repoRoot "DungeonFortress.sln"
$scenarioProject = Join-Path $repoRoot "tests\DungeonFortress.Scenarios\DungeonFortress.Scenarios.csproj"
$scenarioAssembly = Join-Path $repoRoot "tests\DungeonFortress.Scenarios\bin\Release\net8.0\DungeonFortress.Scenarios.dll"
$testProject = Join-Path $repoRoot "tests\DungeonFortress.Simulation.Tests\DungeonFortress.Simulation.Tests.csproj"
$presentationTestProject = Join-Path $repoRoot "tests\DungeonFortress.Presentation.Tests\DungeonFortress.Presentation.Tests.csproj"
$domainMcpTestProject = Join-Path $repoRoot "tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj"
$commandsPath = Join-Path $repoRoot "scenarios\smoke.commands.json"
$gameProjectPath = Join-Path $repoRoot "src\DungeonFortress.Game"
$gameProjectFile = Join-Path $gameProjectPath "DungeonFortress.Game.csproj"
$guardTestScript = Join-Path $repoRoot "scripts\test-godot-output-guard.ps1"
$screenshotOutputPathTestScript = Join-Path $repoRoot "scripts\test-screenshot-output-path.ps1"
$goblinImportTestScript = Join-Path $repoRoot "scripts\test-goblin-sprite-import.ps1"
$ivanMcpConfigTestScript = Join-Path $repoRoot "scripts\test-ivan-mcp-config.ps1"
$domainMcpVerificationScript = Join-Path $repoRoot "scripts\verify-domain-mcp.ps1"

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

    Invoke-Checked -FilePath "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $guardTestScript
    )
    Invoke-Checked -FilePath "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $screenshotOutputPathTestScript
    )
    Invoke-Checked -FilePath "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ivanMcpConfigTestScript
    )

    Write-Host "Restoring and building the .NET 8 solution..."
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "restore", $solutionPath
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "restore", $domainMcpTestProject, "--locked-mode"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $solutionPath, "--configuration", "Release", "--no-restore"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "test", $testProject, "--configuration", "Release", "--no-build", "--no-restore"
    )
    # The HUD and inspector text lives in DungeonFortress.Presentation, which does
    # not reference Godot. These run here and in CI; the golden UI comparison below
    # still starts an engine, because only it can also prove the adapter wires the
    # text through to the labels.
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "test", $presentationTestProject, "--configuration", "Release", "--no-build", "--no-restore"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "test", $domainMcpTestProject, "--configuration", "Release", "--no-build", "--no-restore"
    )
    Invoke-Checked -FilePath "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        $domainMcpVerificationScript, "-Seed", $Seed.ToString(
            [Globalization.CultureInfo]::InvariantCulture), "-NoBuild"
    )
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $gameProjectFile, "--configuration", "Debug", "--no-restore"
    )

    Invoke-Checked -FilePath "powershell" -Arguments @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $goblinImportTestScript,
        "-GodotPath", $godot
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
    Initialize-GodotRuntimeEnvironment -RepositoryRoot $repoRoot
    Import-GodotProjectAssets -GodotPath $godot -ProjectPath $gameProjectPath
    $godotResult = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments @(
            "--headless", "--path", $gameProjectPath,
            "--", "--smoke", "--seed", $Seed
        ) `
        -ExpectedSuccessEvent "godot_headless_smoke"
    $godotExitCode = $godotResult.ExitCode
    $controlsResult = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments @(
            "--headless", "--path", $gameProjectPath,
            "--", "--smoke-controls"
        ) `
        -ExpectedSuccessEvent "godot_controls_smoke"

    # Text before pixels: the HUD and the inspector are compared against committed
    # reference state, and the label overflow guard is required to still react.
    # Both run headless, because neither needs a window to be true.
    Write-Host "Comparing the golden UI state..."
    $goldenUiFrames = @()
    foreach ($frame in Get-GoldenUiFrames) {
        $capture = Invoke-GoldenUiCapture `
            -GodotPath $godot `
            -ProjectPath $gameProjectPath `
            -Frame $frame
        $document = ConvertTo-GoldenUiDocument -Frame $frame -Capture $capture
        Assert-GoldenUiFrame `
            -ExpectedPath (Get-GoldenUiPath -RepositoryRoot $repoRoot -Frame $frame) `
            -Actual $document `
            -FrameName $frame.Name
        $goldenUiFrames += $frame.Name
    }

    Write-Host "Checking that the HUD overflow guard still reacts..."
    Assert-HudFitGuardReacts -GodotPath $godot -ProjectPath $gameProjectPath

    $raidScreenshot = Join-Path $verifyRoot "prepared-raid.png"
    $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"
    $baselineResult = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments @(
            "--path", $gameProjectPath,
            "--", "--fixture", "baseline", "--screenshot", $baselineScreenshot, "--screenshot-ticks", "1"
        ) `
        -ExpectedSuccessEvent "godot_graybox_screenshot"
    Assert-GoblinSpriteDiagnostics -OutputLines $baselineResult.Output -EventName "godot_graybox_screenshot"
    if (-not (Test-Path -LiteralPath $baselineScreenshot -PathType Leaf)) {
        throw "Baseline visual smoke did not write its screenshot."
    }
    $raidResult = Invoke-GodotChecked `
        -GodotPath $godot `
        -Arguments @(
            "--path", $gameProjectPath,
            "--", "--fixture", "prepared", "--screenshot", $raidScreenshot, "--screenshot-ticks", "1540"
        ) `
        -ExpectedSuccessEvent "godot_graybox_screenshot"
    Assert-GoblinSpriteDiagnostics -OutputLines $raidResult.Output -EventName "godot_graybox_screenshot"
    if (-not (Test-Path -LiteralPath $raidScreenshot -PathType Leaf)) {
        throw "Prepared raid smoke did not write its screenshot."
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
        godotControlsExitCode = $controlsResult.ExitCode
        godotRaidExitCode = $raidResult.ExitCode
        goldenUiFrames = $goldenUiFrames
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
