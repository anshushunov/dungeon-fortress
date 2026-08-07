[CmdletBinding()]
param(
    [string]$GodotPath,
    [UInt64]$Seed = 424242,
    [string]$TemporaryRoot,
    [string[]]$Stage,
    [string[]]$Skip,
    [switch]$ListStages
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Issue #284. Invoke-Checked below now captures every native command's output
# (`2>&1`) instead of letting it flow straight through, so it can route a
# successful run's text into the stage log file. That capture decodes the
# child's bytes using [Console]::OutputEncoding, which on a machine whose
# console codepage is not UTF-8 (measured here: cp866) turns a .NET 8 CLI's
# UTF-8 non-ASCII text - e.g. its Russian-locale "Восстановлен ..." lines -
# into mojibake, silently, the moment it is captured. The raw byte
# pass-through this replaced never went through that decode step at all, so
# it never showed the problem. Godot's own output is ASCII-only JSON, so
# Invoke-GodotChecked's and Invoke-Scenario's pre-existing `2>&1` capture
# never exposed this either. Setting it once, here, before anything is
# captured, is enough for all three.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "HudVerification.ps1")
. (Join-Path $PSScriptRoot "TemporaryRoot.ps1")

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
$verifyStagesTestScript = Join-Path $repoRoot "scripts\test-verify-stages.ps1"
$temporaryRootTestScript = Join-Path $repoRoot "scripts\test-temporary-root.ps1"
$screenshotOutputPathTestScript = Join-Path $repoRoot "scripts\test-screenshot-output-path.ps1"
$evidenceToolsTestScript = Join-Path $repoRoot "scripts\test-evidence-tools.ps1"
$claimedSha256TestScript = Join-Path $repoRoot "scripts\test-check-claimed-sha256.ps1"
$codexSessionsSearchTestScript = Join-Path $repoRoot "scripts\test-search-codex-sessions.ps1"
$baseStaleTestScript = Join-Path $repoRoot "scripts\test-check-base-stale.ps1"
$githubAuthToolsTestScript = Join-Path $repoRoot "scripts\test-github-auth-tools.ps1"
$goblinImportTestScript = Join-Path $repoRoot "scripts\test-goblin-sprite-import.ps1"
$ivanMcpConfigTestScript = Join-Path $repoRoot "scripts\test-ivan-mcp-config.ps1"
$domainMcpConfigTestScript = Join-Path $repoRoot "scripts\test-domain-mcp-config.ps1"
$domainMcpLauncherTestScript = Join-Path $repoRoot "scripts\test-domain-mcp-launcher.ps1"
$domainMcpVerificationScript = Join-Path $repoRoot "scripts\verify-domain-mcp.ps1"
$takeTaskTestScript = Join-Path $repoRoot "scripts\agent\test-take-task.ps1"

$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot "dotnet-home"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
# A run-local DOTNET_CLI_HOME makes the .NET CLI append "<CLI_HOME>\.dotnet\tools"
# to the *user's* PATH on its first launch under that home, and
# DOTNET_SKIP_FIRST_TIME_EXPERIENCE does not prevent it. Measured 2026-08-02 on a
# two-armed experiment: a fresh CLI home plus `dotnet new console` added one PATH
# entry with the skip flag alone and none with the variable below, which also
# left the tools directory uncreated. Every worktree and every temporary run has
# its own artifacts root, so the entries accumulate and never point at a
# directory that still exists: 135 such tails were removed from the owner's PATH
# that day, out of 145 entries and 13302 characters.
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = "0"

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    # Issue #284: this used to run the command with its output flowing
    # straight through to whatever this process's own stdout was, uncaptured
    # - the reason 'dotnet restore'/'build'/'test' and every dependency-free
    # test script's own console text used to land directly in a full run's
    # stdout. Capturing it here and routing it below is the same pattern
    # Invoke-GodotChecked already uses, including the -ErrorActionPreference
    # guard: a native command's stderr text becomes PowerShell error records
    # under 2>&1, and this script sets $ErrorActionPreference = "Stop" at the
    # top, so without the guard a single warning line would abort the capture.
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $FilePath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "'$FilePath' failed with exit code $exitCode."
    }

    $output | ForEach-Object { Write-VerifyDiagnostic -Text $_ }
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

    # Same capture-and-route pattern as Invoke-Checked and Invoke-GodotChecked,
    # and the same reason for the -ErrorActionPreference guard around it
    # (Issue #284).
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& dotnet @arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        $output | ForEach-Object { Write-Host $_ }
        throw "Scenario runner failed with exit code $exitCode."
    }

    $resultLine = $output | Where-Object { $_ -match '"event":"scenario_result"' } |
        Select-Object -Last 1
    if ($null -eq $resultLine) {
        $output | ForEach-Object { Write-Host $_ }
        throw "Scenario runner did not emit a scenario_result event."
    }

    $output | ForEach-Object { Write-VerifyDiagnostic -Text $_ }
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

function Get-GodotEvent {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$OutputLines,

        [Parameter(Mandatory = $true)]
        [string]$EventName
    )

    $line = $OutputLines | Where-Object {
        $_ -match ('"event":"' + [regex]::Escape($EventName) + '"') -and
        $_ -match '"status":"ok"'
    } | Select-Object -Last 1
    if ($null -eq $line) {
        throw "Godot output did not contain successful event '$EventName'."
    }

    return [string]$line | ConvertFrom-Json
}

# Prerequisites are the shared work that several stages need: a restore, a build
# or an imported Godot project. They are not stages, because running one proves
# nothing on its own, and they are memoised, so a full run pays for each exactly
# once while a single stage still gets everything it needs to be honest.
$completedPrerequisites = [ordered]@{}

function Test-Prerequisite {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $completedPrerequisites.Contains($Name)
}

function Set-Prerequisite {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $completedPrerequisites[$Name] = $true
}

function Initialize-SolutionRestore {
    if (Test-Prerequisite -Name "restore") {
        return
    }

    Write-VerifyDiagnostic -Text "Restoring the .NET 8 solution..."
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "restore", $solutionPath
    )
    Set-Prerequisite -Name "restore"
}

function Initialize-SolutionBuild {
    if (Test-Prerequisite -Name "solution-build") {
        return
    }

    Initialize-SolutionRestore
    Write-VerifyDiagnostic -Text "Building the .NET 8 solution in Release..."
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $solutionPath, "--configuration", "Release", "--no-restore"
    )
    Set-Prerequisite -Name "solution-build"
}

function Initialize-ScenarioAssembly {
    if (Test-Prerequisite -Name "scenario-build") {
        return
    }

    # The Release solution build already produces this assembly and is the path a
    # full run takes. A partial run that skipped the build stage must not measure
    # yesterday's binary, so it builds the scenario runner itself instead of
    # trusting whatever is left in bin\Release.
    if (-not (Test-Prerequisite -Name "solution-build")) {
        Initialize-SolutionRestore
        Write-VerifyDiagnostic -Text "Building the scenario runner in Release..."
        Invoke-Checked -FilePath "dotnet" -Arguments @(
            "build", $scenarioProject, "--configuration", "Release", "--no-restore"
        )
    }

    if (-not (Test-Path -LiteralPath $scenarioAssembly -PathType Leaf)) {
        throw "The scenario runner assembly is missing at '$scenarioAssembly'."
    }

    Set-Prerequisite -Name "scenario-build"
}

function Initialize-GameHostBuild {
    if (Test-Prerequisite -Name "game-host-build") {
        return
    }

    Initialize-SolutionRestore
    Write-VerifyDiagnostic -Text "Building the Godot host in Debug..."
    Invoke-Checked -FilePath "dotnet" -Arguments @(
        "build", $gameProjectFile, "--configuration", "Debug", "--no-restore"
    )
    Set-Prerequisite -Name "game-host-build"
}

function Initialize-EngineRuntime {
    if (Test-Prerequisite -Name "engine-runtime") {
        return
    }

    # This repoints APPDATA at a short runtime profile, so it has to happen after
    # every dotnet invocation that needs the NuGet profile written above.
    Initialize-GodotRuntimeEnvironment -RepositoryRoot $repoRoot
    Import-GodotProjectAssets -GodotPath $godot -ProjectPath $gameProjectPath
    Set-Prerequisite -Name "engine-runtime"
}

# The catalogue is the single source of truth for what a full run is: the order
# below is the order of a full run, and no check exists outside a stage. A stage
# is a group that fails for one reason and that one kind of change needs, so an
# agent can verify what it touched without paying for the rest.
$stageCatalog = [ordered]@{
    scripts = [pscustomobject]@{
        Summary = "Dependency-free script guards: stage selection, temporary directory, Godot output, screenshot/evidence paths, GitHub auth diagnostics, Ivan and domain MCP config, take-task behavioural test."
        Body = {
            # Stage selection is only honest while every check lives in a stage and
            # the documented table matches this script. Neither is visible in a green
            # run, so it is checked first and without a build.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $verifyStagesTestScript
            )
            # The preflight above this stage refused to start on an unusable
            # temporary directory. This proves the refusal still happens, still
            # names the directory, and still lets cleanup fail without failing
            # the run.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $temporaryRootTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $guardTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $screenshotOutputPathTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $evidenceToolsTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $claimedSha256TestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $codexSessionsSearchTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $baseStaleTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $githubAuthToolsTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $ivanMcpConfigTestScript
            )
            # A client session must run its own copy of the domain MCP server. If it ever
            # goes back to executing the build output, the solution build below fails with
            # MSB3027 whenever an agent has the server connected.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $domainMcpConfigTestScript
            )
            # The behavioural test for take-task.ps1 (Issue #182) runs real code
            # through a stub gh harness and an end-to-end fixture repository. It does
            # not depend on the solution build, the engine, or network access.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $takeTaskTestScript
            )
        }
    }

    build = [pscustomobject]@{
        Summary = "Restore of the solution, locked-mode restore of the domain MCP tests, Release build of everything."
        Body = {
            Initialize-SolutionRestore
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "restore", $domainMcpTestProject, "--locked-mode"
            )
            Initialize-SolutionBuild
        }
    }

    tests = [pscustomobject]@{
        Summary = "dotnet test for Simulation, Presentation and domain MCP."
        Body = {
            Initialize-SolutionBuild
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "test", $testProject, "--configuration", "Release", "--no-build", "--no-restore"
            )
            # The HUD and inspector text lives in DungeonFortress.Presentation, which does
            # not reference Godot. These run here and in CI; the golden UI comparison in the
            # ui stage still starts an engine, because only it can also prove the adapter
            # wires the text through to the labels.
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "test", $presentationTestProject, "--configuration", "Release", "--no-build", "--no-restore"
            )
            Invoke-Checked -FilePath "dotnet" -Arguments @(
                "test", $domainMcpTestProject, "--configuration", "Release", "--no-build", "--no-restore"
            )
        }
    }

    mcp = [pscustomobject]@{
        Summary = "Domain MCP launcher started for real plus the stdio contract check in verify-domain-mcp.ps1."
        Body = {
            Initialize-SolutionBuild
            # The text guard in the scripts stage cannot tell whether the batch launcher
            # still runs. A typo in it would leave this script green and break the owner's
            # next client session, so the launcher is started for real once the build
            # output exists.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $domainMcpLauncherTestScript
            )
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                $domainMcpVerificationScript, "-Seed", $Seed.ToString(
                    [Globalization.CultureInfo]::InvariantCulture), "-NoBuild"
            )
        }
    }

    sim = [pscustomobject]@{
        Summary = "Byte-for-byte determinism: 32 agents x 256 ticks twice on one seed and once on another."
        Body = {
            Initialize-ScenarioAssembly

            Write-VerifyDiagnostic -Text "Checking byte-for-byte deterministic snapshots..."
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

            $script:simResult = [pscustomobject]@{
                DeterministicChecksum = $sameA.checksum
                ChangedSeedChecksum = $different.checksum
            }
        }
    }

    load = [pscustomobject]@{
        Summary = "Load and repeatability: 1,000 agents x 10,000 ticks twice, compared byte for byte."
        Body = {
            Initialize-ScenarioAssembly

            Write-VerifyDiagnostic -Text "Measuring and repeating 1,000 agents x 10,000 ticks..."
            $loadAPath = Join-Path $verifyRoot "load-a.json"
            $loadBPath = Join-Path $verifyRoot "load-b.json"
            $loadA = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 1000 -TickCount 10000 -SnapshotPath $loadAPath
            $loadB = Invoke-Scenario -ScenarioSeed $Seed -AgentCount 1000 -TickCount 10000 -SnapshotPath $loadBPath
            Assert-FilesEqual -ExpectedPath $loadAPath -ActualPath $loadBPath -Description "Load scenario snapshots"

            $script:loadResult = [pscustomobject]@{
                Checksum = $loadA.checksum
                ElapsedMilliseconds = @(
                    $loadA.elapsedMilliseconds,
                    $loadB.elapsedMilliseconds
                )
            }
        }
    }

    godot = [pscustomobject]@{
        Summary = "Godot host, sprite import, smoke, camera input, HUD readability, view-state checksum and frame pacing independence."
        Body = {
            Initialize-GameHostBuild

            # Issue #302: the default temporary root is no longer read back from
            # inherited TMP/TEMP - it is a directory this process computed for
            # itself and may not compute again the same way if asked twice (the
            # own-directory tier of Resolve-VerificationTemporaryRoot picks a
            # fresh name every time it is not given one explicitly). Passing the
            # already-resolved path down explicitly is what keeps this child
            # using the very directory the preflight already proved usable,
            # instead of silently picking a second one of its own.
            Invoke-Checked -FilePath "powershell" -Arguments @(
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $goblinImportTestScript,
                "-GodotPath", $godot,
                "-TemporaryRoot", $temporaryRootPath
            )

            Initialize-EngineRuntime

            Write-VerifyDiagnostic -Text "Running Godot headless smoke..."
            $godotResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--path", $gameProjectPath,
                    "--", "--smoke", "--seed", $Seed
                ) `
                -ExpectedSuccessEvent "godot_headless_smoke"
            $controlsResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--path", $gameProjectPath,
                    "--", "--smoke-controls"
                ) `
                -ExpectedSuccessEvent "godot_controls_smoke"

            Write-VerifyDiagnostic -Text "Proving invalid startup parameters report JSON and exit instead of hanging..."
            $invalidViewCases = @(
                [pscustomobject]@{
                    Name = "zoom"
                    Arguments = @("--smoke", "--camera-zoom", "1.1")
                    Message = "Zoom must be one of"
                },
                [pscustomobject]@{
                    Name = "tile-size"
                    Arguments = @("--smoke", "--tile-size", "50")
                    Message = "Tile size must be between"
                },
                [pscustomobject]@{
                    Name = "ui-scale"
                    Arguments = @("--smoke", "--ui-scale", "3")
                    Message = "UI scale must be between"
                },
                [pscustomobject]@{
                    Name = "camera-position"
                    Arguments = @("--smoke", "--camera-position", "invalid")
                    Message = "camera-position"
                },
                [pscustomobject]@{
                    Name = "fixture"
                    Arguments = @("--smoke", "--fixture")
                    Message = "Missing value after --fixture"
                }
            )
            foreach ($invalidViewCase in $invalidViewCases) {
                $invalidArguments = @(
                    "--headless", "--path", $gameProjectPath, "--"
                ) + @($invalidViewCase.Arguments)
                Invoke-GodotExpectedFailure `
                    -GodotPath $godot `
                    -Arguments $invalidArguments `
                    -ExpectedErrorEvent "godot_headless_smoke" `
                    -MessagePattern $invalidViewCase.Message | Out-Null
            }

            Write-VerifyDiagnostic -Text "Proving the HUD guard rejects overflow at logical width 1024..."
            $hudGuardFailure = Invoke-GodotExpectedFailure `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--resolution", "1024x768", "--path", $gameProjectPath,
                    "--", "--smoke", "--smoke-hud-guard-regression",
                    "--tile-size", "40",
                    "--camera-zoom", "1",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1024x768"
                ) `
                -ExpectedErrorEvent "godot_headless_smoke" `
                -MessagePattern "HUD loses text.*1024"

            # Fitting and being readable are different questions. The run above
            # proves the first guard reacts to text that does not fit; this one
            # proves the second reacts to text that fits perfectly and is too
            # small to read, which is the defect Issue #86 was opened about and
            # the one no check could see.
            Write-VerifyDiagnostic -Text "Proving the readability policy rejects HUD text under the physical floor..."
            $hudReadabilityFailure = Invoke-GodotExpectedFailure `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--resolution", "1280x720", "--path", $gameProjectPath,
                    "--", "--smoke", "--smoke-hud-readability-regression",
                    "--tile-size", "40",
                    "--camera-zoom", "1",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedErrorEvent "godot_headless_smoke" `
                -MessagePattern "HUD text is unreadable.*physical pixels"

            # Issue #127: the tooltip is the one HUD text surface the guard could
            # not reach until CreateControlStrips started keeping a permanent,
            # invisible sample of it. This is that guard's own negative run,
            # exact counterpart of the one above, shrinking the sample instead of
            # a legend row.
            Write-VerifyDiagnostic -Text "Proving the readability policy rejects an unreadable tooltip..."
            $hudTooltipReadabilityFailure = Invoke-GodotExpectedFailure `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--resolution", "1280x720", "--path", $gameProjectPath,
                    "--", "--smoke", "--smoke-hud-tooltip-readability-regression",
                    "--tile-size", "40",
                    "--camera-zoom", "1",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedErrorEvent "godot_headless_smoke" `
                -MessagePattern "Label\[TooltipBody\].*physical pixels"

            Write-VerifyDiagnostic -Text "Proving a misplaced Camera2D fails the independent transform check..."
            $cameraTransformFailure = Invoke-GodotExpectedFailure `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--resolution", "1280x720", "--path", $gameProjectPath,
                    "--", "--smoke-camera-transform-regression",
                    "--tile-size", "40",
                    "--camera-zoom", "1",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedErrorEvent "godot_camera_smoke" `
                -MessagePattern "Camera2D transform disagrees with CameraFrame"

            Write-VerifyDiagnostic -Text "Checking Camera2D input at every discrete zoom against the pure frame..."
            $cameraResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--headless", "--resolution", "1280x720", "--path", $gameProjectPath,
                    "--", "--smoke-camera",
                    "--tile-size", "40",
                    "--camera-zoom", "1",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedSuccessEvent "godot_camera_smoke"
            $cameraEvent = Get-GodotEvent `
                -OutputLines $cameraResult.Output `
                -EventName "godot_camera_smoke"
            if ([string]$cameraEvent.view.displayServer -ne "headless" -or
                [int]$cameraEvent.view.cameraInputChecks -ne 15 -or
                [int]$cameraEvent.view.cameraTransformChecks -ne 15 -or
                [int]$cameraEvent.view.cameraBoundsChecks -ne 10 -or
                [int]$cameraEvent.view.cameraPanChecks -ne 5 -or
                -not [bool]$cameraEvent.view.hudInputRejected) {
                throw "Camera smoke did not prove live transform agreement, input mapping, map bounds, panning and HUD rejection."
            }

            Write-VerifyDiagnostic -Text "Comparing canonical checksum across camera, frame and UI parameters..."
            $viewCases = @(
                [pscustomobject]@{
                    Name = "base"
                    Frame = "1280x720"
                    Zoom = "1"
                    Position = "560,320"
                    UiScale = "1"
                },
                [pscustomobject]@{
                    Name = "overview-shifted"
                    Frame = "1024x768"
                    Zoom = "0.5"
                    Position = "420,280"
                    UiScale = "0.8"
                },
                [pscustomobject]@{
                    Name = "detail-scaled-ui"
                    Frame = "1920x1080"
                    Zoom = "1.5"
                    Position = "720,400"
                    UiScale = "1.25"
                },
                [pscustomobject]@{
                    Name = "large-same-zoom"
                    Frame = "1600x900"
                    Zoom = "1"
                    Position = "560,320"
                    UiScale = "1"
                }
            )
            $viewEvents = @{}
            foreach ($viewCase in $viewCases) {
                $result = Invoke-GodotChecked `
                    -GodotPath $godot `
                    -Arguments @(
                        "--headless", "--resolution", $viewCase.Frame, "--path", $gameProjectPath,
                        "--", "--smoke", "--fixture", "baseline",
                        "--demo-stone", "--screenshot-ticks", "190",
                        "--tile-size", "40",
                        "--camera-zoom", $viewCase.Zoom,
                        "--camera-position", $viewCase.Position,
                        "--ui-scale", $viewCase.UiScale,
                        "--frame-size", $viewCase.Frame
                    ) `
                    -ExpectedSuccessEvent "godot_headless_smoke"
                $viewEvents[$viewCase.Name] = Get-GodotEvent `
                    -OutputLines $result.Output `
                    -EventName "godot_headless_smoke"
                if ([string]$viewEvents[$viewCase.Name].view.displayServer -ne "headless") {
                    throw "View case '$($viewCase.Name)' did not run on the headless display server."
                }
            }

            $viewChecksum = Assert-SameNonEmptyValue `
                -Values @($viewEvents.Values | ForEach-Object { $_.checksum }) `
                -Description "Canonical checksum across camera position, zoom, frame size and UI scale"
            if ([double]$viewEvents["large-same-zoom"].view.visibleWorldSize[0] -le
                [double]$viewEvents["base"].view.visibleWorldSize[0] -or
                [double]$viewEvents["large-same-zoom"].view.visibleWorldSize[1] -le
                [double]$viewEvents["base"].view.visibleWorldSize[1]) {
                throw "A larger frame did not expose more world at the same zoom."
            }
            # The body is drawn at the owner's 170 % since Issue #77, so these are
            # the old numbers times 1.7: 30.9 px in the overview, 61.8 at 1x and
            # 92.7 in the detail case, which is zoom 1.5. The floor moves with the
            # decision: an overview body used to bottom out at 18.2 px and now may
            # not drop below 30.
            #
            # The ceiling is the source the run is drawing from, and it moved with
            # the pack rather than with the decision. It was 96 while the runtime
            # loaded the square v1 sheet; the second subtask of #77 connected the
            # 272x192 v2 pack the scale was authored for, so it is 192 — and the
            # zoom this stage does not visit, 2x at 123.6 px, is inside it again
            # instead of magnifying a 96 px source by 1.29.
            # CameraViewTests.The_selected_scale_states_what_it_asks_of_the_art_at_
            # both_ends_of_the_zoom_range is where the whole range is stated.
            #
            # The width is checked too, and separately: the ceiling above says
            # nothing about the shape, so a run that drew the 17:12 canvas as a
            # square would pass every bound here. 17/12 is the pack's own aspect
            # ratio (docs/art/goblin-v2-provenance.md).
            $overviewGoblinPixels = [double]$viewEvents["overview-shifted"].view.goblinScreenSize
            $baseGoblinPixels = [double]$viewEvents["base"].view.goblinScreenSize
            $detailGoblinPixels = [double]$viewEvents["detail-scaled-ui"].view.goblinScreenSize
            if ($overviewGoblinPixels -lt 30 -or
                $baseGoblinPixels -le $overviewGoblinPixels -or
                $detailGoblinPixels -le $baseGoblinPixels -or
                $detailGoblinPixels -ge 192) {
                throw "Tile-relative goblin art is not readable across overview, base and detail views."
            }

            foreach ($viewCaseName in $viewEvents.Keys) {
                $caseView = $viewEvents[$viewCaseName].view
                $caseHeight = [double]$caseView.goblinScreenSize
                $caseWidth = [double]$caseView.goblinScreenWidth
                if ($caseHeight -le 0 -or
                    [Math]::Abs(($caseWidth / $caseHeight) - (17.0 / 12.0)) -gt 1e-9) {
                    throw (
                        "View case '$viewCaseName' draws a body $caseWidth x $caseHeight px, " +
                        "which is not the 17:12 canvas the connected creature pack was drawn on."
                    )
                }
            }

            # HUD text is measured in the same spirit as the goblin above: the
            # run states how many physical pixels its smallest text ends up
            # being, on its own frame and on every supported one. The authored
            # 1280x720 pair has to stay exactly where it was, and the owner's
            # maximized 3044x1722 has to leave the 8-15 px band Issue #86 was
            # opened about.
            Write-VerifyDiagnostic -Text "Checking the physical size of HUD text on the supported frame matrix..."
            $baseReadability = $viewEvents["base"].view.hudReadability
            if ([double]$baseReadability.uiScale -ne 1 -or
                [double]$baseReadability.logicalDensity -ne 1 -or
                [double]$baseReadability.smallestPhysicalTextPixels -ne 8 -or
                -not [bool]$baseReadability.readable -or
                @($baseReadability.violations).Count -ne 0) {
                throw (
                    "The authored 1280x720 frame no longer reports UI scale 1, density 1, " +
                    "8 px smallest HUD text and no violations: it reports scale " +
                    "$($baseReadability.uiScale), density $($baseReadability.logicalDensity), " +
                    "$($baseReadability.smallestPhysicalTextPixels) px and " +
                    "$(@($baseReadability.violations).Count) violation(s)."
                )
            }
            $ownerFrame = @($baseReadability.checkedFrames | Where-Object {
                [double]$_.frame[0] -eq 3044 -and [double]$_.frame[1] -eq 1722
            })
            if ($ownerFrame.Count -ne 1) {
                throw (
                    "The readability matrix no longer measures the owner's maximized 3044x1722 " +
                    "frame, which is the one Issue #86 was reported on."
                )
            }
            $ownerSmallestTextPixels = [double]$ownerFrame[0].smallestPhysicalTextPixels
            if ($ownerSmallestTextPixels -lt 16 -or
                [double]$ownerFrame[0].uiScale -ne 2 -or
                [double]$ownerFrame[0].logicalDensity -gt 1.25) {
                throw (
                    "At 3044x1722 the automatic policy leaves HUD text at " +
                    "$ownerSmallestTextPixels physical pixels (UI scale " +
                    "$($ownerFrame[0].uiScale), density $($ownerFrame[0].logicalDensity)). " +
                    "Issue #86 is 8-15 px text on exactly that frame."
                )
            }

            # Rendering was separated from the tick, so the simulation must not be able to
            # tell. The same fixture is driven through the real _Process loop at two frame
            # rates and both have to land on the checksum a frameless replay produces.
            Write-VerifyDiagnostic -Text "Comparing canonical state across frame rates..."
            Assert-FramePacingIndependence `
                -GodotPath $godot `
                -ProjectPath $gameProjectPath `
                -TargetTick 200 `
                -FixedFps @(20, 60) | Out-Null

            $script:godotStageResult = [pscustomobject]@{
                SmokeExitCode = $godotResult.ExitCode
                ControlsExitCode = $controlsResult.ExitCode
                CameraExitCode = $cameraResult.ExitCode
                InvalidViewFailuresChecked = $invalidViewCases.Count
                HudGuardRegressionExitCode = $hudGuardFailure.ExitCode
                HudReadabilityRegressionExitCode = $hudReadabilityFailure.ExitCode
                HudSmallestPhysicalTextPixels = [pscustomobject]@{
                    Authored = [double]$baseReadability.smallestPhysicalTextPixels
                    OwnerMaximized = $ownerSmallestTextPixels
                }
                CameraTransformRegressionExitCode = $cameraTransformFailure.ExitCode
                CameraInputChecks = [int]$cameraEvent.view.cameraInputChecks
                CameraTransformChecks = [int]$cameraEvent.view.cameraTransformChecks
                CameraBoundsChecks = [int]$cameraEvent.view.cameraBoundsChecks
                CameraPanChecks = [int]$cameraEvent.view.cameraPanChecks
                ViewChecksum = [string]$viewChecksum
                ViewCases = @($viewCases.Name)
                GoblinScreenPixels = [pscustomobject]@{
                    Overview = $overviewGoblinPixels
                    Base = $baseGoblinPixels
                    Detail = $detailGoblinPixels
                }
            }
        }
    }

    ui = [pscustomobject]@{
        Summary = "Golden UI state: every committed frame is captured headless and compared with tests/golden/ui."
        Body = {
            Initialize-GameHostBuild
            Initialize-EngineRuntime

            # Text before pixels: the HUD and the inspector are compared against committed
            # reference state. It runs headless, because it does not need a window to be
            # true. The HUD overflow guard itself now runs inside every entry point and at
            # the live pair plus six fixed frame/UI-scale pairs, so there is nothing
            # left here to hold it to.
            Write-VerifyDiagnostic -Text "Comparing the golden UI state..."
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

            $script:goldenUiResult = @($goldenUiFrames)
        }
    }

    screenshots = [pscustomobject]@{
        Summary = "Explicit-frame baseline captured twice byte-for-byte, plus prepared-raid sprite diagnostics."
        Body = {
            Initialize-GameHostBuild
            Initialize-EngineRuntime

            $baselineScreenshot = Join-Path $verifyRoot "baseline-t1.png"
            $baselineRepeatScreenshot = Join-Path $verifyRoot "baseline-t1-repeat.png"
            $raidScreenshot = Join-Path $verifyRoot "prepared-raid.png"
            $baselineResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--path", $gameProjectPath, "--resolution", "1280x720",
                    "--", "--fixture", "baseline",
                    "--screenshot", $baselineScreenshot, "--screenshot-ticks", "1",
                    "--tile-size", "40",
                    "--camera-zoom", "0.5",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedSuccessEvent "godot_graybox_screenshot"
            Assert-GoblinSpriteDiagnostics -OutputLines $baselineResult.Output -EventName "godot_graybox_screenshot"
            $baselineEvent = Get-GodotEvent `
                -OutputLines $baselineResult.Output `
                -EventName "godot_graybox_screenshot"
            if (-not (Test-Path -LiteralPath $baselineScreenshot -PathType Leaf)) {
                throw "Baseline visual smoke did not write its screenshot."
            }
            $baselineRepeatResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--path", $gameProjectPath, "--resolution", "1280x720",
                    "--", "--fixture", "baseline",
                    "--screenshot", $baselineRepeatScreenshot, "--screenshot-ticks", "1",
                    "--tile-size", "40",
                    "--camera-zoom", "0.5",
                    "--camera-position", "560,320",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedSuccessEvent "godot_graybox_screenshot"
            Assert-GoblinSpriteDiagnostics `
                -OutputLines $baselineRepeatResult.Output `
                -EventName "godot_graybox_screenshot"
            $baselineRepeatEvent = Get-GodotEvent `
                -OutputLines $baselineRepeatResult.Output `
                -EventName "godot_graybox_screenshot"
            Assert-FilesEqual `
                -ExpectedPath $baselineScreenshot `
                -ActualPath $baselineRepeatScreenshot `
                -Description "Repeated explicit camera screenshot"
            # Tick 1670 is twenty ticks into the *second* wave of the prepared
            # party (waves arrive at 1300, 1650, 2000 and 2350). Three reasons
            # for that tick rather than the old 1540, which now falls in the
            # quiet window between waves 1 and 2 and drew no raid at all:
            #
            # - the second wave proves the cycle repeats, which is the thing
            #   worth photographing; a frame inside wave 1 would look exactly
            #   like the single raid this replaced;
            # - twenty ticks in is structurally mid-combat and not merely
            #   empirically so: all six raiders have entered by then (entry
            #   takes ten ticks) and none can yet have walked to the larder,
            #   stolen a load and walked back out, which needs about fifty;
            # - it is the richest frame for what this stage actually guards,
            #   the goblin sprite diagnostics. Re-measured after #101 changed
            #   when defenders leave a fight: three raiders alive on the map,
            #   ten drawn including the downed of wave 1, and on the domain
            #   side five fighting, two in flight, one downed and one still
            #   mustering — every sprite state the stage guards on screen at
            #   once, and now the flight state as well.
            $raidResult = Invoke-GodotChecked `
                -GodotPath $godot `
                -Arguments @(
                    "--path", $gameProjectPath, "--resolution", "1280x720",
                    "--", "--fixture", "prepared",
                    "--screenshot", $raidScreenshot, "--screenshot-ticks", "1670",
                    "--tile-size", "40",
                    "--camera-zoom", "0.75",
                    "--camera-position", "720,400",
                    "--ui-scale", "1",
                    "--frame-size", "1280x720"
                ) `
                -ExpectedSuccessEvent "godot_graybox_screenshot"
            Assert-GoblinSpriteDiagnostics -OutputLines $raidResult.Output -EventName "godot_graybox_screenshot"
            $raidEvent = Get-GodotEvent `
                -OutputLines $raidResult.Output `
                -EventName "godot_graybox_screenshot"
            if (-not (Test-Path -LiteralPath $raidScreenshot -PathType Leaf)) {
                throw "Prepared raid smoke did not write its screenshot."
            }
            foreach ($captureEvent in @($baselineEvent, $baselineRepeatEvent, $raidEvent)) {
                if (-not [bool]$captureEvent.view.cameraSynchronizedAfterLayout) {
                    throw "A screenshot was captured before Camera2D followed deferred HUD layout."
                }
            }

            $script:screenshotResult = [pscustomobject]@{
                RaidExitCode = $raidResult.ExitCode
                Repeatable = $true
            }
        }
    }
}

$allStages = @($stageCatalog.Keys)

# The only stage whose body never calls dotnet or the Godot executable: it is
# pure PowerShell script guards (see its Summary above). Every other stage
# reaches Initialize-SolutionRestore or Initialize-SolutionBuild somewhere in
# its body - directly (build, tests, mcp), through Initialize-ScenarioAssembly
# (sim, load) or through Initialize-GameHostBuild (godot, ui, screenshots) -
# and that restore's NuGet profile is deliberately sourced from the engine's
# bundled packages rather than nuget.org, so a partial run does not silently
# check a different source than a full run does. Measured, including why
# nuget.org is not used instead, in evidence/285-stage-engine-need.json
# (Issue #285).
$engineFreeStages = @("scripts")

function Expand-StageNames {
    param(
        [AllowNull()]
        [string[]]$Names,

        [Parameter(Mandatory = $true)]
        [string]$ParameterName
    )

    $resolved = @()
    foreach ($name in @($Names)) {
        # -Stage sim,load arrives as one string through powershell -File and as an
        # array through the console host. Both spellings resolve the same way.
        foreach ($token in ([string]$name).Split(",")) {
            $trimmed = $token.Trim()
            if ($trimmed.Length -eq 0) {
                continue
            }
            if ($trimmed -eq "all") {
                $resolved += $allStages
                continue
            }
            $match = @($allStages | Where-Object { $_ -eq $trimmed })
            if ($match.Count -eq 0) {
                throw "$ParameterName '$trimmed' is not a verification stage. Available: $($allStages -join ', '). Run with -ListStages for what each one covers."
            }
            $resolved += $match[0]
        }
    }

    return @($resolved)
}

if ($ListStages) {
    [ordered]@{
        event = "verification_stages"
        stages = @($allStages | ForEach-Object {
            [ordered]@{
                name = $_
                summary = $stageCatalog[$_].Summary
            }
        })
    } | ConvertTo-Json -Depth 4 -Compress | Write-Host
    return
}

$requestedStages = if ($PSBoundParameters.ContainsKey("Stage")) {
    Expand-StageNames -Names $Stage -ParameterName "-Stage"
} else {
    $allStages
}
$excludedStages = Expand-StageNames -Names $Skip -ParameterName "-Skip"

$selectedStages = @($allStages | Where-Object {
    $_ -in $requestedStages -and $_ -notin $excludedStages
})
$notRunStages = @($allStages | Where-Object { $_ -notin $selectedStages })

if ($selectedStages.Count -eq 0) {
    throw "The selection left no stage to run. Available: $($allStages -join ', ')."
}

$scope = if ($notRunStages.Count -eq 0) { "full" } else { "partial" }
$executedStages = @()
$currentPhase = "preflight"
$currentStage = $null
$godotVersion = $null
$temporaryRootPath = $null
$temporaryRootOwned = $false

New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

# Issue #284: a full green run used to print hundreds of kilobytes - almost
# all of it the raw per-line dump of Godot process output, never read because
# nothing failed - straight into whatever called this script. The full dump
# still happens, every stage still writes everything it always did, but now
# into this file; stdout keeps only a line per stage (status and time) and
# the final verification_result with every checksum. Set here, once, before
# any stage or run-setup function that might print something runs, so the
# functions in GodotTools.ps1 and HudVerification.ps1 - dot-sourced above,
# sharing this script's scope - see it from their first call.
$stageLogPath = Join-Path $verifyRoot "stage-output.log"
New-Item -ItemType File -Force -Path $stageLogPath | Out-Null
$script:VerifyStageLogPath = $stageLogPath

try {
    Write-Host ("Verification scope: {0} ({1} of {2} stages: {3})." -f
        $scope, $selectedStages.Count, $allStages.Count, ($selectedStages -join ", "))
    if ($scope -eq "partial") {
        Write-Host ("Not running: {0}. A partial run does not replace a full one." -f
            ($notRunStages -join ", "))
    }
    [ordered]@{
        event = "verification_stage_output"
        status = "ok"
        path = $stageLogPath
    } | ConvertTo-Json -Compress | Write-Host

    # The temporary directory is proven before anything else, because it is the
    # cheapest thing to check, no stage can repair it, and every stage depends on
    # it: the Godot runtime profile and the isolated sprite-import project are
    # both created there. Issue #89 spent three sessions on a TEMP that allowed
    # creating a directory and refused to delete it, because what verification
    # reported was "'powershell' failed with exit code 1" at stage godot.
    $temporaryRootSelection = Initialize-VerificationTemporaryRoot -ExplicitPath $TemporaryRoot -RepositoryRoot $repoRoot
    $temporaryRootPath = $temporaryRootSelection.Path
    $temporaryRootOwned = $temporaryRootSelection.Owned
    [ordered]@{
        event = "verification_temporary_root"
        status = "ok"
        path = $temporaryRootSelection.Path
        source = $temporaryRootSelection.Source
        owned = $temporaryRootSelection.Owned
    } | ConvertTo-Json -Compress | Write-Host

    # The engine is resolved only when a selected stage actually needs it - see
    # $engineFreeStages above for which ones do and why. A full run's refusal is
    # unchanged: every stage but `scripts` needs the engine, so a full selection
    # always falls into the "requires it" branch below and reports the same
    # preflight failure it always has.
    $engineRequiringStages = @($selectedStages | Where-Object { $_ -notin $engineFreeStages })
    if ($engineRequiringStages.Count -gt 0) {
        try {
            $godot = Resolve-GodotExecutable -ExplicitPath $GodotPath
            $godotVersion = Assert-GodotVersion -GodotPath $godot
            $godotNuGetSource = Get-GodotNuGetSource -GodotPath $godot
            Initialize-GodotNuGetEnvironment `
                -ProfileRoot (Join-Path $artifactsRoot "tool-profile") `
                -GodotNuGetSource $godotNuGetSource
        }
        catch {
            if ($scope -eq "full") {
                # Unchanged message: a full run has always needed the engine,
                # so this is exactly the refusal it always reported.
                throw
            }
            throw (
                "Stage(s) $($engineRequiringStages -join ', ') require the Godot " +
                "engine and it could not be resolved: $($_.Exception.Message)")
        }
    }

    foreach ($stageName in $selectedStages) {
        $currentPhase = "stage"
        $currentStage = $stageName
        Write-VerifyDiagnostic -Text ""
        Write-VerifyDiagnostic -Text ("--- stage {0}: {1}" -f $stageName, $stageCatalog[$stageName].Summary)
        $stageStopwatch = [Diagnostics.Stopwatch]::StartNew()

        # Dot-sourced on purpose: a stage body is part of this script, not a
        # separate scope, so it reads the paths above and writes its result the
        # same way the single linear script did.
        $stageBody = $stageCatalog[$stageName].Body
        . $stageBody

        $stageStopwatch.Stop()
        $executedStages += $stageName
        [ordered]@{
            event = "verification_stage"
            status = "ok"
            stage = $stageName
            elapsedSeconds = [Math]::Round($stageStopwatch.Elapsed.TotalSeconds, 1)
        } | ConvertTo-Json -Compress | Write-Host
    }

    $currentPhase = "summary"
    $currentStage = $null

    $summary = [ordered]@{
        event = "verification_result"
        status = "ok"
        scope = $scope
        stagesExecuted = @($executedStages)
        stagesNotRun = @($notRunStages)
        prerequisites = @($completedPrerequisites.Keys)
        seed = $Seed
        temporaryRoot = $temporaryRootPath
    }
    if ($executedStages -contains "sim") {
        $summary["deterministicChecksum"] = $simResult.DeterministicChecksum
        $summary["changedSeedChecksum"] = $simResult.ChangedSeedChecksum
    }
    if ($executedStages -contains "load") {
        $summary["loadChecksum"] = $loadResult.Checksum
        $summary["loadElapsedMilliseconds"] = @($loadResult.ElapsedMilliseconds)
    }
    $summary["godotVersion"] = $godotVersion
    if ($executedStages -contains "godot") {
        $summary["godotExitCode"] = $godotStageResult.SmokeExitCode
        $summary["godotControlsExitCode"] = $godotStageResult.ControlsExitCode
        $summary["godotCameraExitCode"] = $godotStageResult.CameraExitCode
        $summary["invalidViewFailuresChecked"] = $godotStageResult.InvalidViewFailuresChecked
        $summary["hudGuardRegressionExitCode"] = $godotStageResult.HudGuardRegressionExitCode
        $summary["hudReadabilityRegressionExitCode"] =
            $godotStageResult.HudReadabilityRegressionExitCode
        $summary["hudSmallestPhysicalTextPixels"] =
            $godotStageResult.HudSmallestPhysicalTextPixels
        $summary["cameraTransformRegressionExitCode"] =
            $godotStageResult.CameraTransformRegressionExitCode
        $summary["cameraInputChecks"] = $godotStageResult.CameraInputChecks
        $summary["cameraTransformChecks"] = $godotStageResult.CameraTransformChecks
        $summary["cameraBoundsChecks"] = $godotStageResult.CameraBoundsChecks
        $summary["cameraPanChecks"] = $godotStageResult.CameraPanChecks
        $summary["viewInvariantChecksum"] = $godotStageResult.ViewChecksum
        $summary["viewCases"] = @($godotStageResult.ViewCases)
        $summary["goblinScreenPixels"] = $godotStageResult.GoblinScreenPixels
    }
    if ($executedStages -contains "screenshots") {
        $summary["godotRaidExitCode"] = $screenshotResult.RaidExitCode
        $summary["screenshotRepeatable"] = $screenshotResult.Repeatable
    }
    if ($executedStages -contains "ui") {
        $summary["goldenUiFrames"] = @($goldenUiResult)
    }

    $summary | ConvertTo-Json -Compress | Write-Host
}
catch {
    # Issue #284: the raw per-line dump moved into $stageLogPath, which keeps
    # a green run's stdout small, but a stage that fails partway through -
    # after some of its own Invoke-GodotChecked/Invoke-Checked/Invoke-Scenario
    # calls already succeeded and routed their output there - must not become
    # harder to diagnose than before for that reason. Those calls already
    # print loudly, unconditionally, on their own failure (still Write-Host,
    # never routed away); this recovers the rest: whatever the failed stage
    # itself had already written to the file, from its own "--- stage ...:"
    # header onward, so a check that throws directly - a checksum comparison,
    # a HUD/camera assertion - is not left with only the exception message
    # below. $currentStage is $null for a preflight failure, before any stage
    # (and its header) exists, so this is skipped for that case on purpose.
    if ($null -ne $currentStage) {
        if (Test-Path -LiteralPath $stageLogPath -PathType Leaf) {
            $failedStageLogLines = @([IO.File]::ReadAllLines($stageLogPath))
            $failedStageMarker = "--- stage " + $currentStage + ":"
            $failedStageMarkerIndex = -1
            for ($lineIndex = $failedStageLogLines.Count - 1; $lineIndex -ge 0; $lineIndex--) {
                if ($failedStageLogLines[$lineIndex].StartsWith(
                        $failedStageMarker, [StringComparison]::Ordinal)) {
                    $failedStageMarkerIndex = $lineIndex
                    break
                }
            }

            if ($failedStageMarkerIndex -ge 0) {
                $failedStageOutput = @($failedStageLogLines[
                    $failedStageMarkerIndex..($failedStageLogLines.Count - 1)])
                $failedStageErrorLines = @($failedStageOutput | Where-Object {
                    $_ -match '"status":"error"'
                })
                $failedStageTailCount = [Math]::Min(40, $failedStageOutput.Count)
                $failedStageTail = @($failedStageOutput[
                    ($failedStageOutput.Count - $failedStageTailCount)..($failedStageOutput.Count - 1)])

                Write-Host ""
                Write-Host ("--- diagnostics for the failed stage '{0}' ({1} line(s) captured in {2}) ---" -f
                    $currentStage, $failedStageOutput.Count, $stageLogPath)
                if ($failedStageErrorLines.Count -gt 0) {
                    Write-Host "Structured error event(s) reported during this stage:"
                    $failedStageErrorLines | ForEach-Object { Write-Host $_ }
                }
                Write-Host ("Last {0} line(s) of this stage's captured output:" -f $failedStageTail.Count)
                $failedStageTail | ForEach-Object { Write-Host $_ }
            }
        }
    }

    # A run that died halfway is the one most likely to be reported as a pass, so
    # it gets the same structured line as a success, with the phase and stage that
    # failed and everything that never ran. `failedPhase` separates "the machine
    # was never fit to run this" from "a check said no": a preflight failure has
    # no failed stage and nothing it could have executed.
    [ordered]@{
        event = "verification_result"
        status = "error"
        scope = $scope
        failedPhase = $currentPhase
        failedStage = $currentStage
        stagesExecuted = @($executedStages)
        stagesNotRun = @($allStages | Where-Object { $_ -notin $executedStages })
        reason = $_.Exception.Message
    } | ConvertTo-Json -Compress | Write-Host

    throw
}
finally {
    $resolvedVerifyRoot = [IO.Path]::GetFullPath($verifyRoot)
    $expectedPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if ($resolvedVerifyRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        # Best effort on purpose. A throw here replaces whatever the run was
        # about to report - including a green result - with a cleanup error, and
        # that is exactly how Issue #89 turned a passing check into a red run.
        Remove-TemporaryItemBestEffort `
            -Path $resolvedVerifyRoot `
            -Description "verification run directory" | Out-Null
    }

    # Issue #302: a run that throws partway through - whether from a failed
    # check or from Ctrl+C reaching PowerShell's own terminating-signal
    # handling of a `finally` block - must not leave its own directory behind
    # either. Complete-VerificationTemporaryRoot is the one place that decides
    # whether this run owns $temporaryRootPath at all; see it in
    # TemporaryRoot.ps1 for why an explicit -TemporaryRoot is never touched
    # here.
    Complete-VerificationTemporaryRoot -Path $temporaryRootPath -Owned $temporaryRootOwned
}
