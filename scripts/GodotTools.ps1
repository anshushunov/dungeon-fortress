Set-StrictMode -Version Latest

function Resolve-GodotExecutable {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return Resolve-GodotCandidate -Candidate $ExplicitPath -Source "explicit -GodotPath override"
    }

    if (-not [string]::IsNullOrWhiteSpace($env:GODOT4_CONSOLE)) {
        return Resolve-GodotCandidate -Candidate $env:GODOT4_CONSOLE -Source "GODOT4_CONSOLE"
    }

    $commandNames = @(
        "godot4_console",
        "godot4",
        "godot",
        "Godot_v4.7.1-stable_mono_win64_console",
        "Godot_v4.7.1-stable_mono_win64"
    )

    foreach ($commandName in $commandNames) {
        $command = Get-Command $commandName -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $command) {
            return [IO.Path]::GetFullPath($command.Source)
        }
    }

    throw @"
Godot 4.7.1 .NET was not found. Use one of:
  -GodotPath <console executable>
  `$env:GODOT4_CONSOLE=<console executable>
  add godot4_console, godot4, or godot to PATH
"@
}

function Assert-GodotVersion {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath
    )

    $versionLines = & $GodotPath --version 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Godot version check failed with exit code $LASTEXITCODE."
    }

    $version = ($versionLines | Out-String).Trim()
    if ($version -notmatch '^4\.7\.1(?:\.|$)') {
        throw "Godot 4.7.1 .NET is required; discovered version '$version'."
    }

    return $version
}

function Get-GodotNuGetSource {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath
    )

    $source = Join-Path (Split-Path -Parent $GodotPath) "GodotSharp\Tools\nupkgs"
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "The selected executable is not a Godot .NET build: missing '$source'."
    }

    return [IO.Path]::GetFullPath($source)
}

function Initialize-GodotNuGetEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProfileRoot,

        [Parameter(Mandatory = $true)]
        [string]$GodotNuGetSource
    )

    $resolvedProfileRoot = [IO.Path]::GetFullPath($ProfileRoot)
    $env:APPDATA = Join-Path $resolvedProfileRoot "AppData\Roaming"
    $env:LOCALAPPDATA = Join-Path $resolvedProfileRoot "AppData\Local"
    $env:NUGET_PACKAGES = Join-Path $resolvedProfileRoot "NuGetPackages"
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $resolvedProfileRoot "NuGetHttpCache"

    $nugetConfigDirectory = Join-Path $env:APPDATA "NuGet"
    $nugetConfigPath = Join-Path $nugetConfigDirectory "NuGet.Config"
    New-Item -ItemType Directory -Force -Path $nugetConfigDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA | Out-Null
    New-Item -ItemType Directory -Force -Path $env:NUGET_PACKAGES | Out-Null
    New-Item -ItemType Directory -Force -Path $env:NUGET_HTTP_CACHE_PATH | Out-Null

    $settings = [Xml.XmlWriterSettings]::new()
    $settings.Encoding = [Text.UTF8Encoding]::new($false)
    $settings.Indent = $true

    $writer = [Xml.XmlWriter]::Create($nugetConfigPath, $settings)
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement("configuration")
        $writer.WriteStartElement("packageSources")
        $writer.WriteStartElement("clear")
        $writer.WriteEndElement()
        $writer.WriteStartElement("add")
        $writer.WriteAttributeString("key", "godot-local")
        $writer.WriteAttributeString("value", $GodotNuGetSource)
        $writer.WriteEndElement()
        $writer.WriteStartElement("add")
        $writer.WriteAttributeString("key", "nuget.org")
        $writer.WriteAttributeString("value", "https://api.nuget.org/v3/index.json")
        $writer.WriteAttributeString("protocolVersion", "3")
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    }
    finally {
        $writer.Dispose()
    }
}

function Get-StablePathHash {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToLowerInvariant())
        $hash = $algorithm.ComputeHash($bytes)
        $hex = [BitConverter]::ToString($hash) -replace "-", ""
        return $hex.Substring(0, 8).ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
    }
}

# Issue #184. Godot creates a shader cache directory with CreateDirectoryW,
# which it prefixes with \\?\ and which therefore ignores MAX_PATH, and enters
# it with SetCurrentDirectoryW, which it does not prefix and which does not.
# Past the length below a directory can be created once and never entered
# again, so `if (d->change_dir(base_sha256) != OK) { Error err =
# d->make_dir(base_sha256); ERR_FAIL_COND(err != OK); }` in
# ShaderGLES3::initialize takes the make_dir branch on a directory that is
# already there, gets ERR_ALREADY_EXISTS and prints
#
#   ERROR: Condition "err != OK" is true.
#      at: initialize (drivers/gles3/shader_gles3.cpp:802)
#
# once per shader class - on the second and every later engine process, never
# on the first, which is the one that created the directories. That asymmetry is
# why the noise looked like it moved between processes in Issue #184.
#
# 254 is measured, not quoted. Six arms of one capture each, on profiles from 90
# to 110 characters of APPDATA, produced exactly as many error lines as the
# profile had shader cache directories of 255 characters or more - 0, 1, 3, 6,
# 14, 14 - and never one below. The arms and their output are
# evidence/184-cause.json. The 248-character CreateDirectory limit this
# repository documented for the same message since PR #5 was checked in the same
# sweep and does not bite on Godot 4.7.1: nine cold-cache arms from 246 to 312
# characters were silent.
#
# Do not "correct" this to 258. Direct P/Invoke probes of SetCurrentDirectoryW
# without the prefix put the raw Win32 boundary at 258 in, 259 out
# (ERROR_FILENAME_EXCED_RANGE). The engine stops four characters earlier, and
# why is not established - DirAccessWindows keeps a current directory of its own
# between ShaderGLES3::initialize and that call. So this number is calibrated on
# engine behaviour rather than derived from the API: in the 255-258 band the API
# works and the engine does not, and raising it brings Issue #184 back. Only a
# new staircase of engine runs can justify moving it.
$script:GodotMaximumEnterableDirectoryPathLength = 254

# The GLES3 shader classes this renderer initializes, read off a real profile
# with `ls <profile>\Roaming\Godot\app_userdata\<project>\shader_cache`. The
# list is what lets the refusal below say "3 of 14" rather than "possibly too
# long". A future engine with more or longer classes makes this check
# optimistic, never wrong about what it does report - and the output guard still
# fails on the lines themselves, now with the diagnosis attached.
$script:GodotGles3ShaderClasses = @(
    "CanvasOcclusionShaderGLES3",
    "CanvasSdfShaderGLES3",
    "CanvasShaderGLES3",
    "CopyShaderGLES3",
    "CubemapFilterShaderGLES3",
    "FeedShaderGLES3",
    "GlowShaderGLES3",
    "ParticlesCopyShaderGLES3",
    "ParticlesShaderGLES3",
    "PostShaderGLES3",
    "SceneShaderGLES3",
    "SkeletonShaderGLES3",
    "SkyShaderGLES3",
    "TexBlitShaderGLES3"
)

# Godot names the per-version cache directory after a SHA-256 in text form.
$script:GodotShaderCacheHashLength = 64

# The last measurement Initialize-GodotRuntimeEnvironment took, so that a run
# which still meets the engine lines can name its own numbers instead of a rule.
$script:GodotRuntimeProfileMeasurement = $null

function Get-GodotUserDirectoryName {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectFile
    )

    if (-not (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
        throw "Cannot measure the Godot runtime profile: no project.godot at '$ProjectFile'."
    }

    $text = [IO.File]::ReadAllText($ProjectFile)
    $match = [Regex]::Match($text, '(?m)^\s*config/name\s*=\s*"(?<name>[^"]*)"')
    if (-not $match.Success) {
        throw "Cannot measure the Godot runtime profile: '$ProjectFile' declares no config/name."
    }

    # Windows replaces characters it forbids in a directory name one for one
    # (OS_Windows::get_user_data_dir goes through get_safe_dir_name), so that
    # substitution cannot change the length measured here. The name is returned
    # as written, which for this project is also how it appears on disk.
    return $match.Groups["name"].Value
}

function Get-GodotShaderCachePaths {
    [CmdletBinding()]
    [OutputType([pscustomobject[]])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppDataRoot,

        [Parameter(Mandatory = $true)]
        [string]$UserDirectoryName
    )

    $cacheRoot = Join-Path $AppDataRoot (
        "Godot\app_userdata\" + $UserDirectoryName + "\shader_cache")

    return @($script:GodotGles3ShaderClasses | ForEach-Object {
        $path = Join-Path (Join-Path $cacheRoot $_) (
            "f" * $script:GodotShaderCacheHashLength)
        [pscustomobject]@{
            ShaderClass = $_
            Path = $path
            Length = $path.Length
        }
    })
}

function Assert-GodotShaderCachePathFits {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$AppDataRoot,

        [Parameter(Mandatory = $true)]
        [string]$UserDirectoryName
    )

    $paths = @(Get-GodotShaderCachePaths `
        -AppDataRoot $AppDataRoot `
        -UserDirectoryName $UserDirectoryName)
    $longest = @($paths | Sort-Object -Property Length -Descending)[0]
    $overBudget = @($paths | Where-Object {
        $_.Length -gt $script:GodotMaximumEnterableDirectoryPathLength
    })

    $measurement = [pscustomobject]@{
        AppDataRoot = $AppDataRoot
        UserDirectoryName = $UserDirectoryName
        LongestPath = $longest.Path
        LongestPathLength = $longest.Length
        MaximumEnterablePathLength = $script:GodotMaximumEnterableDirectoryPathLength
        Headroom = $script:GodotMaximumEnterableDirectoryPathLength - $longest.Length
        ShaderClassCount = $paths.Count
        UnenterableShaderClassCount = $overBudget.Count
    }

    if ($overBudget.Count -gt 0) {
        throw @"
The Godot runtime profile is too deep for this engine's shader cache.
  profile:  $AppDataRoot
  user dir: $UserDirectoryName
  longest:  $($longest.Path)
            $($longest.Length) characters; $($overBudget.Count) of $($paths.Count) shader classes are over the limit of $($script:GodotMaximumEnterableDirectoryPathLength)
Godot creates such a directory (CreateDirectoryW with the \\?\ prefix) and then
cannot enter it again (SetCurrentDirectoryW without one), so the first engine
process is silent and every later one prints $($overBudget.Count) lines of
  ERROR: Condition "err != OK" is true.
     at: initialize (drivers/gles3/shader_gles3.cpp:802)
The profile lives under the temporary directory, so shorten that one:
  -TemporaryRoot <short directory outside the worktree>
  `$env:DUNGEON_FORTRESS_TEMP=<the same directory>
Measured in evidence/184-cause.json; the rule is in
docs/engineering/ENVIRONMENT_SETUP.md.
"@
    }

    return $measurement
}

function Initialize-GodotRuntimeEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        # The project whose user:// directory sets the budget. Defaulted rather
        # than required so that every existing caller keeps its one-line call:
        # the game project's name is the longest this repository asks Godot for,
        # and the isolated sprite-import project's is shorter, so budgeting for
        # the game project covers both.
        [string]$ProjectPath
    )

    $profileName = "df-godot-" + (Get-StablePathHash -Value $RepositoryRoot)
    $profileRoot = Join-Path ([IO.Path]::GetTempPath()) $profileName
    $appDataRoot = Join-Path $profileRoot "Roaming"

    $resolvedProjectPath = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        Join-Path $RepositoryRoot "src\DungeonFortress.Game"
    }
    else {
        $ProjectPath
    }

    # Measured before anything is created, because the point is to refuse in one
    # named line instead of letting the engine print fourteen unnamed ones two
    # processes later (Issue #184).
    $measurement = Assert-GodotShaderCachePathFits `
        -AppDataRoot $appDataRoot `
        -UserDirectoryName (Get-GodotUserDirectoryName -ProjectFile (
            Join-Path $resolvedProjectPath "project.godot"))
    $script:GodotRuntimeProfileMeasurement = $measurement

    $env:APPDATA = $appDataRoot
    $env:LOCALAPPDATA = Join-Path $profileRoot "Local"

    New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
    New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA | Out-Null

    # Printed on success as well as measured. Two scripts that start the engine
    # can now be compared by their own output rather than by reading both of
    # them, which is how the comparison in Issue #184 had to be made.
    [ordered]@{
        event = "godot_runtime_profile"
        status = "ok"
        path = $env:APPDATA
        userDirectory = $measurement.UserDirectoryName
        longestShaderCachePathLength = $measurement.LongestPathLength
        maximumEnterablePathLength = $measurement.MaximumEnterablePathLength
        headroom = $measurement.Headroom
    } | ConvertTo-Json -Compress | Write-Host
}

function Import-GodotProjectAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath,

        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    $resolvedProjectPath = [IO.Path]::GetFullPath($ProjectPath)
    $projectFile = Join-Path $resolvedProjectPath "project.godot"
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Godot asset import preflight requires project.godot at '$resolvedProjectPath'."
    }

    # Godot's editor import pass is incremental: on an up-to-date project this
    # exits without rebuilding assets, while a fresh checkout gets its required
    # .godot/imported entries before ResourceLoader is used at runtime.
    Write-Host "Importing Godot project assets (incremental)..."
    $result = Invoke-GodotChecked `
        -GodotPath $GodotPath `
        -Arguments @("--headless", "--editor", "--quit", "--path", $resolvedProjectPath)

    if ($result.ExitCode -ne 0) {
        throw "Godot asset import preflight failed for '$resolvedProjectPath'. Run the command above with --editor output and fix the reported import error."
    }
}

function Resolve-RepositoryArtifactPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath,

        [ValidateSet("ScreenshotPath", "OutputRoot")]
        [string]$ParameterName = "ScreenshotPath"
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '^[A-Za-z]:') {
        throw "$ParameterName must be a non-empty relative path inside repository .artifacts."
    }

    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ".artifacts"))
    $artifactsPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $RelativePath))

    if (-not $candidate.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$ParameterName resolves outside repository .artifacts."
    }

    return $candidate
}

function Get-GodotErrorLines {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [AllowEmptyCollection()]
        [object[]]$OutputLines = @()
    )

    $errorLines = @()
    foreach ($line in $OutputLines) {
        $text = [string]$line
        if ($text -match "ERROR:") {
            $errorLines += $text
        }
    }

    return $errorLines
}

function Get-GodotShaderCachePathDiagnosis {
    [CmdletBinding()]
    [OutputType([Collections.Specialized.OrderedDictionary])]
    param(
        [AllowEmptyCollection()]
        [object[]]$OutputLines = @()
    )

    $shaderLines = @($OutputLines | Where-Object {
        [string]$_ -match "shader_gles3\.cpp"
    })
    if ($shaderLines.Count -eq 0) {
        return $null
    }

    $diagnosis = [ordered]@{
        engineErrorClass = "gles3_shader_cache_path"
        explanation = (
            "Godot created these shader cache directories once and cannot enter " +
            "them again: on Windows a directory path over " +
            "$($script:GodotMaximumEnterableDirectoryPathLength) characters can be " +
            "created (CreateDirectoryW with the \\?\ prefix) but not entered " +
            "(SetCurrentDirectoryW without one), so ShaderGLES3::initialize " +
            "reports ERR_ALREADY_EXISTS once per shader class from the second " +
            "engine process onwards. Shorten the Godot runtime profile with a " +
            "shorter -TemporaryRoot or `$env:DUNGEON_FORTRESS_TEMP. Measured in " +
            "evidence/184-cause.json; rule in docs/engineering/ENVIRONMENT_SETUP.md.")
    }

    if ($null -ne $script:GodotRuntimeProfileMeasurement) {
        $measurement = $script:GodotRuntimeProfileMeasurement
        $diagnosis["runtimeProfile"] = $measurement.AppDataRoot
        $diagnosis["longestShaderCachePathLength"] = $measurement.LongestPathLength
        $diagnosis["maximumEnterablePathLength"] = $measurement.MaximumEnterablePathLength
    }

    return $diagnosis
}

function Invoke-GodotChecked {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$ExpectedSuccessEvent
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& $GodotPath @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $output | ForEach-Object { Write-Host $_ }
    $errorLines = @(Get-GodotErrorLines -OutputLines $output)

    if ($errorLines.Count -gt 0) {
        $report = [ordered]@{
            event = "godot_process_guard"
            status = "error"
            reason = "unexpected_engine_error"
            exitCode = $exitCode
            engineErrorCount = $errorLines.Count
            firstEngineError = $errorLines[0]
        }

        # The guard still fails, and on the same condition as before. What is
        # added is the one thing Issue #184 cost three sessions: these
        # particular lines are unreadable without the Godot source, so when they
        # appear the run says what they mean and what its own profile measured.
        $diagnosis = Get-GodotShaderCachePathDiagnosis -OutputLines $output
        if ($null -ne $diagnosis) {
            foreach ($key in $diagnosis.Keys) {
                $report[$key] = $diagnosis[$key]
            }
        }

        $report | ConvertTo-Json -Compress | Write-Host

        throw "Godot emitted $($errorLines.Count) unexpected ERROR line(s)."
    }

    if ($exitCode -ne 0) {
        [ordered]@{
            event = "godot_process_guard"
            status = "error"
            reason = "nonzero_exit"
            exitCode = $exitCode
            engineErrorCount = 0
        } | ConvertTo-Json -Compress | Write-Host

        throw "Godot failed with exit code $exitCode."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSuccessEvent)) {
        $escapedEvent = [Regex]::Escape($ExpectedSuccessEvent)
        $successEvent = $output | Where-Object {
            $_ -match ('"event":"' + $escapedEvent + '"') -and $_ -match '"status":"ok"'
        } | Select-Object -Last 1

        if ($null -eq $successEvent) {
            [ordered]@{
                event = "godot_process_guard"
                status = "error"
                reason = "missing_success_event"
                exitCode = $exitCode
                engineErrorCount = 0
                expectedEvent = $ExpectedSuccessEvent
            } | ConvertTo-Json -Compress | Write-Host

            throw "Godot did not emit the expected '$ExpectedSuccessEvent' success event."
        }
    }

    [ordered]@{
        event = "godot_process_guard"
        status = "ok"
        exitCode = $exitCode
        engineErrorCount = 0
        expectedEvent = $ExpectedSuccessEvent
    } | ConvertTo-Json -Compress | Write-Host

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
        EngineErrorCount = 0
    }
}

function Invoke-GodotExpectedFailure {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedErrorEvent,

        [string]$MessagePattern,

        [ValidateRange(1, 120)]
        [int]$TimeoutSeconds = 20
    )

    # Direct invocation cannot enforce a deadline. Use a no-window process with
    # redirected streams so a regression that hangs after its startup exception
    # fails verification instead of hanging verification too.
    $quotedArguments = @($Arguments | ForEach-Object {
        $argument = [string]$_
        if ($argument -match '[\s"]') {
            '"' + $argument.Replace('"', '\"') + '"'
        }
        else {
            $argument
        }
    })
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $GodotPath
    $startInfo.Arguments = $quotedArguments -join " "
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo

    try {
        if (-not $process.Start()) {
            throw "Godot failure process did not start."
        }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "Godot did not exit within $TimeoutSeconds second(s) after the expected failure."
        }
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        $stdout = @($stdoutTask.Result -split "\r?\n" | Where-Object { $_ -ne "" })
        $stderr = @($stderrTask.Result -split "\r?\n" | Where-Object { $_ -ne "" })
        $output = @($stdout) + @($stderr)
        $output | ForEach-Object { Write-Host $_ }

        if ($exitCode -ne 1) {
            throw "Godot failure exited with code $exitCode; expected exactly 1."
        }

        $eventPattern = '"event":"' + [Regex]::Escape($ExpectedErrorEvent) + '"'
        $eventLine = $output | Where-Object {
            $_ -match $eventPattern -and $_ -match '"status":"error"'
        } | Select-Object -Last 1
        if ($null -eq $eventLine) {
            throw "Godot did not emit the expected '$ExpectedErrorEvent' error event."
        }

        $event = $eventLine | ConvertFrom-Json
        if (-not [string]::IsNullOrWhiteSpace($MessagePattern) -and
            [string]$event.message -notmatch $MessagePattern) {
            throw (
                "Godot error event message '$($event.message)' did not match " +
                "'$MessagePattern'."
            )
        }

        [ordered]@{
            event = "godot_expected_failure_guard"
            status = "ok"
            exitCode = $exitCode
            expectedEvent = $ExpectedErrorEvent
            message = $event.message
        } | ConvertTo-Json -Compress | Write-Host

        return [pscustomobject]@{
            ExitCode = $exitCode
            Output = $output
            Event = $event
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-SameNonEmptyValue {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Values,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $texts = @($Values | ForEach-Object { [string]$_ })
    if ($texts.Count -eq 0 -or
        @($texts | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "$Description must contain one non-empty value from every case."
    }

    $distinct = @($texts | Select-Object -Unique)
    if ($distinct.Count -ne 1) {
        throw "$Description differs between cases."
    }

    return $distinct[0]
}

function Assert-GoblinSpriteDiagnostics {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$OutputLines,

        [Parameter(Mandatory = $true)]
        [string]$EventName
    )

    $eventPattern = '"event":"' + [Regex]::Escape($EventName) + '"'
    $resultLine = $OutputLines | Where-Object {
        $_ -match $eventPattern -and $_ -match '"status":"ok"'
    } | Select-Object -Last 1
    if ($null -eq $resultLine) {
        throw "Cannot validate goblin sprite diagnostics: '$EventName' success output is missing."
    }

    $result = ([string]$resultLine | ConvertFrom-Json)
    $loaded = @($result.loadedSpriteStates)
    $missing = @($result.missingSpriteStates)
    $fallbacks = [int]$result.fallbackSpriteDraws
    # Six since Issue #77 connected the v2 pack. The list is the one
    # DungeonFortress.Presentation.BodySprites.States declares; a pose the
    # adapter can choose but did not load is a missing texture in a frame, so
    # this is the check that both ends agree.
    $required = @("idle", "work", "combat", "windup", "flinch", "downed")
    $missingRequired = @($required | Where-Object { $_ -notin $loaded })
    if ($missingRequired.Count -gt 0 -or $missing.Count -gt 0 -or $fallbacks -ne 0) {
        throw "Goblin sprite diagnostics failed: loaded=[$($loaded -join ',')], missing=[$($missing -join ',')], fallbackSpriteDraws=$fallbacks."
    }
}

function Resolve-GodotCandidate {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Candidate,

        [Parameter(Mandatory = $true)]
        [string]$Source
    )

    if (Test-Path -LiteralPath $Candidate -PathType Leaf) {
        return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Candidate).Path)
    }

    $command = Get-Command $Candidate -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        return [IO.Path]::GetFullPath($command.Source)
    }

    throw "Godot path from $Source does not resolve to an executable: '$Candidate'."
}
