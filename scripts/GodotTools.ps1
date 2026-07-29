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

function Initialize-GodotRuntimeEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $profileName = "df-godot-" + (Get-StablePathHash -Value $RepositoryRoot)
    $profileRoot = Join-Path ([IO.Path]::GetTempPath()) $profileName
    $env:APPDATA = Join-Path $profileRoot "Roaming"
    $env:LOCALAPPDATA = Join-Path $profileRoot "Local"

    New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null
    New-Item -ItemType Directory -Force -Path $env:LOCALAPPDATA | Out-Null
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
        [string]$RelativePath
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '^[A-Za-z]:') {
        throw "ScreenshotPath must be a non-empty relative path inside repository .artifacts."
    }

    $artifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot ".artifacts"))
    $artifactsPrefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $RelativePath))

    if (-not $candidate.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ScreenshotPath resolves outside repository .artifacts."
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
        [ordered]@{
            event = "godot_process_guard"
            status = "error"
            reason = "unexpected_engine_error"
            exitCode = $exitCode
            engineErrorCount = $errorLines.Count
            firstEngineError = $errorLines[0]
        } | ConvertTo-Json -Compress | Write-Host

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
    $required = @("idle", "work", "combat", "downed")
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
