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
