[CmdletBinding()]
param(
    [ValidateSet("Install", "Open", "Stop", "Status", "Uninstall")]
    [string]$Action = "Status",

    [string]$GodotPath,

    [switch]$Headless
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = Join-Path $repoRoot ".artifacts\ivan-mcp"
$downloadRoot = Join-Path $artifactRoot "downloads"
$stageRoot = Join-Path $artifactRoot "stage"
$serverRoot = Join-Path $artifactRoot "server"
$gameRoot = Join-Path $repoRoot "src\DungeonFortress.Game"
$gameProject = Join-Path $gameRoot "DungeonFortress.Game.csproj"
$addonRoot = Join-Path $gameRoot "addons\godot_mcp"
$addonMarker = Join-Path $addonRoot ".dungeon-fortress-ivan-mcp.json"
$generatedProps = Join-Path $gameRoot "DungeonFortress.Game.IvanMcp.props"
$lockFile = Join-Path $repoRoot "config\ivan-mcp\packages.lock.json"
$serverExecutable = Join-Path $serverRoot "gamedev-mcp-server.exe"
$serverStateFile = Join-Path $artifactRoot "server-process.json"
$editorStateFile = Join-Path $artifactRoot "editor-process.json"
$editorLogFile = Join-Path $artifactRoot "editor.log"

$addonVersion = "0.19.1"
$serverVersion = "9.2.0"
$mcpPort = 29541
$mcpHost = "http://127.0.0.1:$mcpPort"
$addonUrl = "https://github.com/IvanMurzak/Godot-MCP/releases/download/v$addonVersion/godot-mcp-addon-$addonVersion.zip"
$serverUrl = "https://github.com/IvanMurzak/GameDev-MCP-Server/releases/download/v$serverVersion/gamedev-mcp-server-win-x64.zip"
$addonSha256 = "271b74e58631a7c07c451b205f5808ce8edc42e70f3f0e7b28e5422b82e30e03"
$serverSha256 = "e0fe86cbebed4f376737086b781445c53f4bf5cf9111c923ba98da5c0bc4b69d"

function Write-Utf8File {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its expected parent: '$resolvedPath'."
    }
}

function Remove-OwnedDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Parent,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    Assert-PathWithin -Path $Path -Parent $Parent -Description $Description
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if (-not (Test-Path -LiteralPath $Path)) {
            return
        }
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq 3) {
                if ($env:OS -eq "Windows_NT") {
                    $longPath = "\\?\" + [IO.Path]::GetFullPath($Path)
                    [IO.Directory]::Delete($longPath, $true)
                    if (-not (Test-Path -LiteralPath $Path)) {
                        return
                    }
                }
                throw
            }
            Start-Sleep -Milliseconds 200
        }
    }
}

function Get-Sha256 {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Get-PinnedDownload {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [Parameter(Mandatory = $true)]
        [string]$Destination,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        $existingHash = Get-Sha256 -Path $Destination
        if ($existingHash -eq $ExpectedSha256) {
            return
        }
        Remove-Item -LiteralPath $Destination -Force
    }

    Write-Host "Downloading pinned artifact: $Uri"
    Invoke-WebRequest -UseBasicParsing -Uri $Uri -OutFile $Destination
    $actualHash = Get-Sha256 -Path $Destination
    if ($actualHash -ne $ExpectedSha256) {
        Remove-Item -LiteralPath $Destination -Force
        throw "SHA-256 mismatch for '$Destination': expected $ExpectedSha256, got $actualHash."
    }
}

function Test-OwnedAddon {
    if (-not (Test-Path -LiteralPath $addonMarker -PathType Leaf)) {
        return $false
    }

    try {
        $marker = Get-Content -Raw -Encoding utf8 $addonMarker | ConvertFrom-Json
        return $marker.addonVersion -eq $addonVersion -and
            $marker.addonArchiveSha256 -eq $addonSha256 -and
            $marker.serverVersion -eq $serverVersion -and
            $marker.serverArchiveSha256 -eq $serverSha256
    }
    catch {
        return $false
    }
}

function Write-GeneratedProps {
    $content = @"
<Project>
  <PropertyGroup>
    <WarningsNotAsErrors>`$(WarningsNotAsErrors);CS0618</WarningsNotAsErrors>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <NuGetLockFilePath>`$(MSBuildThisFileDirectory)..\..\config\ivan-mcp\packages.lock.json</NuGetLockFilePath>
  </PropertyGroup>
  <ItemGroup>
    <Using Remove="System.IO" />
    <PackageReference Include="com.IvanMurzak.ReflectorNet" Version="[5.3.2]" />
    <PackageReference Include="com.IvanMurzak.McpPlugin" Version="[7.3.0]" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="addons\godot_mcp\extensions.catalog.json"
                      LogicalName="Godot-MCP.extensions.catalog.json" />
  </ItemGroup>
</Project>
"@
    Write-Utf8File -Path $generatedProps -Content $content
}

function Install-IvanArtifacts {
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

    $addonArchive = Join-Path $downloadRoot "godot-mcp-addon-$addonVersion.zip"
    $serverArchive = Join-Path $downloadRoot "gamedev-mcp-server-win-x64-$serverVersion.zip"
    Get-PinnedDownload -Uri $addonUrl -Destination $addonArchive -ExpectedSha256 $addonSha256
    Get-PinnedDownload -Uri $serverUrl -Destination $serverArchive -ExpectedSha256 $serverSha256

    if ((Test-Path -LiteralPath $addonRoot) -and -not (Test-OwnedAddon)) {
        throw "Refusing to replace '$addonRoot': it is not marked as a candidate-owned Ivan-MCP install."
    }

    Remove-OwnedDirectory -Path $stageRoot -Parent $artifactRoot -Description "Ivan staging directory"
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
    try {
        $addonStage = Join-Path $stageRoot "addon"
        $serverStage = Join-Path $stageRoot "server"
        Expand-Archive -LiteralPath $addonArchive -DestinationPath $addonStage
        Expand-Archive -LiteralPath $serverArchive -DestinationPath $serverStage

        $stagedAddon = Join-Path $addonStage "addons\godot_mcp"
        $stagedPlugin = Join-Path $stagedAddon "plugin.cfg"
        $stagedCatalog = Join-Path $stagedAddon "extensions.catalog.json"
        $stagedServer = Join-Path $serverStage "gamedev-mcp-server.exe"
        if (-not (Test-Path -LiteralPath $stagedPlugin -PathType Leaf) -or
            -not (Test-Path -LiteralPath $stagedCatalog -PathType Leaf) -or
            -not (Test-Path -LiteralPath $stagedServer -PathType Leaf)) {
            throw "Pinned Ivan-MCP archives do not contain the expected addon/server layout."
        }

        if (Test-OwnedAddon) {
            Remove-OwnedDirectory -Path $addonRoot `
                -Parent (Join-Path $gameRoot "addons") `
                -Description "Ivan addon directory"
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $addonRoot) | Out-Null
        Copy-Item -LiteralPath $stagedAddon -Destination $addonRoot -Recurse

        Remove-OwnedDirectory -Path $serverRoot -Parent $artifactRoot -Description "Ivan server directory"
        New-Item -ItemType Directory -Force -Path $serverRoot | Out-Null
        Copy-Item -Path (Join-Path $serverStage "*") -Destination $serverRoot -Recurse

        $marker = [ordered]@{
            addonVersion = $addonVersion
            addonArchiveSha256 = $addonSha256
            serverVersion = $serverVersion
            serverArchiveSha256 = $serverSha256
        } | ConvertTo-Json
        Write-Utf8File -Path $addonMarker -Content $marker
        Write-GeneratedProps
    }
    finally {
        Remove-OwnedDirectory -Path $stageRoot -Parent $artifactRoot -Description "Ivan staging directory"
    }
}

function Restore-AndBuildIvan {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedGodotPath
    )

    $godotNuGetSource = Get-GodotNuGetSource -GodotPath $ResolvedGodotPath
    Initialize-GodotNuGetEnvironment `
        -ProfileRoot (Join-Path $artifactRoot "tool-profile") `
        -GodotNuGetSource $godotNuGetSource

    $env:DOTNET_CLI_HOME = Join-Path $artifactRoot "dotnet-home"
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    $env:CI = "1"
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

    $restoreArguments = @("restore", $gameProject)
    if (Test-Path -LiteralPath $lockFile -PathType Leaf) {
        $restoreArguments += "--locked-mode"
    }
    $restoreOutput = @(& dotnet @restoreArguments 2>&1)
    $restoreExitCode = $LASTEXITCODE
    $restoreOutput | ForEach-Object { Write-Host $_ }
    if ($restoreExitCode -ne 0) {
        throw "Ivan-enabled Godot project restore failed with exit code $restoreExitCode."
    }

    $buildOutput = @(& dotnet build $gameProject --configuration Debug --no-restore 2>&1)
    $buildExitCode = $LASTEXITCODE
    $buildOutput | ForEach-Object { Write-Host $_ }
    if ($buildExitCode -ne 0) {
        throw "Ivan-enabled Godot project build failed with exit code $buildExitCode."
    }
}

function Get-RuntimeProfileRoot {
    $profileHash = Get-StablePathHash -Value ($repoRoot + "|ivan-mcp")
    return Join-Path ([IO.Path]::GetTempPath()) ("df-ivan-mcp-" + $profileHash)
}

function Initialize-IvanRuntimeProfile {
    $profileRoot = Get-RuntimeProfileRoot
    $roaming = Join-Path $profileRoot "Roaming"
    $local = Join-Path $profileRoot "Local"
    New-Item -ItemType Directory -Force -Path $roaming | Out-Null
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    $env:APPDATA = $roaming
    $env:LOCALAPPDATA = $local

    $projectName = "Dungeon Fortress " + [char]0x2014 + " deterministic spike"
    $configPath = Join-Path $roaming (Join-Path "Godot\app_userdata" `
        (Join-Path $projectName "godot-mcp-config.json"))
    $config = [ordered]@{
        host = $mcpHost
        token = $null
        cloudToken = $null
        connectionMode = "Custom"
        authOption = "none"
        logLevel = "Info"
        generateSkillFiles = $false
        features = [ordered]@{
            tools = @()
            prompts = @()
            resources = @()
        }
        selectedAgentId = "disabled"
    } | ConvertTo-Json -Depth 8
    Write-Utf8File -Path $configPath -Content $config
    return $profileRoot
}

function Write-ProcessState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string]$Executable
    )

    $state = [ordered]@{
        pid = $Process.Id
        executable = [IO.Path]::GetFullPath($Executable)
        startTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
    } | ConvertTo-Json
    Write-Utf8File -Path $Path -Content $state
}

function Get-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StatePath
    )

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        return $null
    }

    $state = Get-Content -Raw -Encoding utf8 $StatePath | ConvertFrom-Json
    $process = Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Remove-Item -LiteralPath $StatePath -Force
        return $null
    }

    $actualPath = [IO.Path]::GetFullPath($process.Path)
    $expectedPath = [IO.Path]::GetFullPath([string]$state.executable)
    if (-not $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Tracked PID $($process.Id) now belongs to unexpected executable '$actualPath'."
    }
    $startTimeProperty = $state.PSObject.Properties["startTimeUtcTicks"]
    if ($null -eq $startTimeProperty) {
        throw "Tracked PID $($process.Id) state is missing its process start time."
    }
    $actualStartTimeUtcTicks = $process.StartTime.ToUniversalTime().Ticks
    if ($actualStartTimeUtcTicks -ne [long]$startTimeProperty.Value) {
        throw "Tracked PID $($process.Id) was reused by another process; refusing to manage it."
    }
    return $process
}

function Stop-TrackedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StatePath,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $process = Get-TrackedProcess -StatePath $StatePath
    if ($null -ne $process) {
        Stop-Process -Id $process.Id -ErrorAction Stop
        if (-not $process.WaitForExit(10000)) {
            throw "$Name process $($process.Id) did not exit within 10 seconds."
        }
        Write-Host "Stopped $Name process $($process.Id)."
    }
    if (Test-Path -LiteralPath $StatePath) {
        Remove-Item -LiteralPath $StatePath -Force
    }
}

function Test-LoopbackPort {
    param(
        [int]$Port,
        [int]$TimeoutMilliseconds = 250
    )

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect("127.0.0.1", $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
            return $false
        }
        $client.EndConnect($connect)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Test-IvanServerIdentity {
    try {
        $body = @{
            jsonrpc = "2.0"
            id = 1
            method = "initialize"
            params = @{
                protocolVersion = "2025-03-26"
                capabilities = @{}
                clientInfo = @{
                    name = "dungeon-fortress-launcher"
                    version = "1.0"
                }
            }
        } | ConvertTo-Json -Depth 6 -Compress
        $response = Invoke-WebRequest -UseBasicParsing `
            -Uri "$mcpHost/mcp" `
            -Method Post `
            -Headers @{ Accept = "application/json, text/event-stream" } `
            -ContentType "application/json" `
            -Body $body `
            -TimeoutSec 2
        $dataLines = @($response.Content -split "`n" | Where-Object {
            $_.StartsWith("data: ", [StringComparison]::Ordinal)
        })
        if ($dataLines.Count -ne 1) {
            return $false
        }
        $payload = $dataLines[0].Substring(6) | ConvertFrom-Json
        return $payload.result.serverInfo.name -eq "gamedev-mcp-server" -and
            ([string]$payload.result.serverInfo.version).StartsWith(
                "$serverVersion.",
                [StringComparison]::Ordinal)
    }
    catch {
        return $false
    }
}

function Initialize-IsolatedProcessEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [Diagnostics.ProcessStartInfo]$StartInfo
    )

    $StartInfo.EnvironmentVariables.Clear()
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $dotnetRoot = Split-Path -Parent $dotnetCommand.Source
    $systemDirectory = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::System)
    $safePath = @($dotnetRoot, $systemDirectory, $env:SystemRoot) -join ";"
    $safeVariables = [ordered]@{
        SystemRoot = $env:SystemRoot
        WINDIR = $env:WINDIR
        TEMP = [IO.Path]::GetTempPath()
        TMP = [IO.Path]::GetTempPath()
        PATH = $safePath
        DOTNET_ROOT = $dotnetRoot
        DOTNET_CLI_HOME = Join-Path $artifactRoot "dotnet-home"
        NUGET_PACKAGES = Join-Path $artifactRoot "tool-profile\NuGetPackages"
        DOTNET_NOLOGO = "1"
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
        DOTNET_CLI_TELEMETRY_OPTOUT = "1"
    }
    foreach ($entry in $safeVariables.GetEnumerator()) {
        if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            $StartInfo.EnvironmentVariables[[string]$entry.Key] = [string]$entry.Value
        }
    }
}

function Start-IvanServer {
    $existing = Get-TrackedProcess -StatePath $serverStateFile
    if ($null -ne $existing) {
        if (-not (Test-IvanServerIdentity)) {
            throw "Tracked Ivan server is running but failed its protocol identity check; use -Action Stop."
        }
        return $existing
    }
    if (-not (Test-Path -LiteralPath $serverExecutable -PathType Leaf)) {
        throw "Ivan server is not installed. Run -Action Install first."
    }
    if (Test-LoopbackPort -Port $mcpPort) {
        throw "Loopback port $mcpPort is already in use; refusing to attach to an untracked server."
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $serverExecutable
    $startInfo.Arguments = "--client-transport streamableHttp --port $mcpPort --auth none --bind loopback"
    $startInfo.WorkingDirectory = $serverRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    Initialize-IsolatedProcessEnvironment -StartInfo $startInfo
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        Write-ProcessState -Path $serverStateFile -Process $process -Executable $serverExecutable
    }
    catch {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            $process.WaitForExit(10000) | Out-Null
        }
        throw
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) {
            break
        }
        if (Test-IvanServerIdentity) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) {
        Stop-TrackedProcess -StatePath $serverStateFile -Name "Ivan server"
        throw "Ivan server did not open loopback port $mcpPort within 10 seconds."
    }
    return $process
}

function Resolve-GodotEditorExecutable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConsolePath
    )

    if ($ConsolePath.EndsWith("_console.exe", [StringComparison]::OrdinalIgnoreCase)) {
        $guiPath = $ConsolePath.Substring(0, $ConsolePath.Length - "_console.exe".Length) + ".exe"
        if (Test-Path -LiteralPath $guiPath -PathType Leaf) {
            return [IO.Path]::GetFullPath($guiPath)
        }
    }
    return $ConsolePath
}

function Test-IvanEditorReady {
    try {
        $response = Invoke-RestMethod `
            -Uri "$mcpHost/api/tools/editor-application-get-state" `
            -Method Post `
            -ContentType "application/json" `
            -Body "{}" `
            -TimeoutSec 1
        return $response.status -eq "success" -and
            $null -ne $response.structured.result.editorVersion
    }
    catch {
        return $false
    }
}

function Start-IvanEditor {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResolvedGodotPath
    )

    $existing = Get-TrackedProcess -StatePath $editorStateFile
    if ($null -ne $existing) {
        if (-not (Test-IvanEditorReady)) {
            throw "Tracked Godot editor is running but Ivan is not ready; use -Action Stop before recovery."
        }
        return $existing
    }

    $profileRoot = Initialize-IvanRuntimeProfile
    $editorExecutable = Resolve-GodotEditorExecutable -ConsolePath $ResolvedGodotPath
    $headlessArgument = if ($Headless) { "--headless " } else { "" }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $editorExecutable
    $startInfo.Arguments = '--editor {0}--path "{1}" --log-file "{2}"' -f `
        $headlessArgument, $gameRoot, $editorLogFile
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = [bool]$Headless
    Initialize-IsolatedProcessEnvironment -StartInfo $startInfo
    $startInfo.EnvironmentVariables["APPDATA"] = Join-Path $profileRoot "Roaming"
    $startInfo.EnvironmentVariables["LOCALAPPDATA"] = Join-Path $profileRoot "Local"
    $startInfo.EnvironmentVariables["GODOT_MCP_CONNECTION_MODE"] = "Custom"
    $startInfo.EnvironmentVariables["GODOT_MCP_HOST"] = $mcpHost
    $startInfo.EnvironmentVariables["GODOT_MCP_AUTH_OPTION"] = "none"
    $startInfo.EnvironmentVariables["GODOT_MCP_TOKEN"] = ""
    $startInfo.EnvironmentVariables["GODOT_MCP_CLOUD_URL"] = ""
    $startInfo.EnvironmentVariables["GODOT_MCP_LOG_LEVEL"] = "Info"
    $startInfo.EnvironmentVariables["GODOT_MCP_DEV_CONTROL"] = "0"
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        Write-ProcessState -Path $editorStateFile -Process $process -Executable $editorExecutable
    }
    catch {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            $process.WaitForExit(10000) | Out-Null
        }
        throw
    }

    $ready = $false
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $process.Refresh()
        if ($process.HasExited) {
            break
        }
        if (Test-IvanEditorReady) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        Stop-TrackedProcess -StatePath $editorStateFile -Name "Godot editor"
        throw "Godot editor did not complete the Ivan handshake within 120 seconds."
    }
    return $process
}

function Get-IvanStatus {
    $serverProcess = Get-TrackedProcess -StatePath $serverStateFile
    $editorProcess = Get-TrackedProcess -StatePath $editorStateFile
    return [ordered]@{
        event = "ivan_mcp_status"
        status = "ok"
        addonVersion = $addonVersion
        serverVersion = $serverVersion
        addonInstalled = (Test-OwnedAddon)
        generatedPropsInstalled = (Test-Path -LiteralPath $generatedProps -PathType Leaf)
        serverRunning = $null -ne $serverProcess
        serverPid = if ($null -eq $serverProcess) { $null } else { $serverProcess.Id }
        editorRunning = $null -ne $editorProcess
        editorPid = if ($null -eq $editorProcess) { $null } else { $editorProcess.Id }
        loopbackPort = $mcpPort
        listenerReachable = (Test-LoopbackPort -Port $mcpPort)
        mcpUrl = "$mcpHost/mcp"
        cloudEnabled = $false
        trustedBroadToolSurface = $true
    }
}

function Invoke-Install {
    if ($null -ne (Get-TrackedProcess -StatePath $editorStateFile) -or
        $null -ne (Get-TrackedProcess -StatePath $serverStateFile)) {
        throw "Ivan is running. Use -Action Stop before reinstalling artifacts."
    }
    $resolvedGodot = Resolve-GodotExecutable -ExplicitPath $GodotPath
    $null = Assert-GodotVersion -GodotPath $resolvedGodot
    Install-IvanArtifacts
    Restore-AndBuildIvan -ResolvedGodotPath $resolvedGodot
    return $resolvedGodot
}

function Invoke-EnsureInstalled {
    $resolvedGodot = Resolve-GodotExecutable -ExplicitPath $GodotPath
    $null = Assert-GodotVersion -GodotPath $resolvedGodot
    $completeInstall = (Test-OwnedAddon) -and
        (Test-Path -LiteralPath $generatedProps -PathType Leaf) -and
        (Test-Path -LiteralPath $serverExecutable -PathType Leaf)
    if (-not $completeInstall) {
        Install-IvanArtifacts
    }
    Restore-AndBuildIvan -ResolvedGodotPath $resolvedGodot
    return $resolvedGodot
}

function Invoke-Uninstall {
    Stop-TrackedProcess -StatePath $editorStateFile -Name "Godot editor"
    Stop-TrackedProcess -StatePath $serverStateFile -Name "Ivan server"

    if (Test-Path -LiteralPath $addonRoot) {
        if (-not (Test-OwnedAddon)) {
            throw "Refusing to remove '$addonRoot': candidate ownership marker is missing or invalid."
        }
        Remove-OwnedDirectory -Path $addonRoot `
            -Parent (Join-Path $gameRoot "addons") `
            -Description "Ivan addon directory"
    }
    if (Test-Path -LiteralPath $generatedProps) {
        $propsText = Get-Content -Raw -Encoding utf8 $generatedProps
        if ($propsText -notmatch "com\.IvanMurzak\.McpPlugin") {
            throw "Refusing to remove unrecognized generated props '$generatedProps'."
        }
        Remove-Item -LiteralPath $generatedProps -Force
    }

    $profileRoot = Get-RuntimeProfileRoot
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    Remove-OwnedDirectory -Path $profileRoot -Parent $tempRoot -Description "Ivan runtime profile"
    Remove-OwnedDirectory -Path $artifactRoot `
        -Parent (Join-Path $repoRoot ".artifacts") `
        -Description "Ivan artifact directory"
}

switch ($Action) {
    "Install" {
        $null = Invoke-Install
        Get-IvanStatus | ConvertTo-Json -Compress
    }
    "Open" {
        $resolvedGodot = Invoke-EnsureInstalled
        $serverWasRunning = $null -ne (Get-TrackedProcess -StatePath $serverStateFile)
        $editorWasRunning = $null -ne (Get-TrackedProcess -StatePath $editorStateFile)
        $server = Start-IvanServer
        try {
            $editor = Start-IvanEditor -ResolvedGodotPath $resolvedGodot
            $status = Get-IvanStatus
            if (-not $status.serverRunning -or -not $status.editorRunning) {
                throw "Ivan Open completed without both tracked processes running."
            }
        }
        catch {
            if (-not $editorWasRunning) {
                Stop-TrackedProcess -StatePath $editorStateFile -Name "Godot editor"
            }
            if (-not $serverWasRunning) {
                Stop-TrackedProcess -StatePath $serverStateFile -Name "Ivan server"
            }
            throw
        }
        $status | ConvertTo-Json -Compress
    }
    "Stop" {
        Stop-TrackedProcess -StatePath $editorStateFile -Name "Godot editor"
        Stop-TrackedProcess -StatePath $serverStateFile -Name "Ivan server"
        Get-IvanStatus | ConvertTo-Json -Compress
    }
    "Status" {
        Get-IvanStatus | ConvertTo-Json -Compress
    }
    "Uninstall" {
        Invoke-Uninstall
        Get-IvanStatus | ConvertTo-Json -Compress
    }
}
