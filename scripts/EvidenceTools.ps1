Set-StrictMode -Version Latest

function Get-RequiredEvidenceProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Context is missing required property '$Name'."
    }

    return $property.Value
}

function Get-OptionalEvidenceProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [AllowNull()]
        [object]$DefaultValue
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $DefaultValue
    }

    return $property.Value
}

function Assert-EvidenceProperties {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string[]]$Allowed,

        [Parameter(Mandatory = $true)]
        [string]$Context
    )

    $unexpected = @($Object.PSObject.Properties.Name | Where-Object {
        $_ -notin $Allowed
    })
    if ($unexpected.Count -gt 0) {
        throw "$Context contains unexpected property/properties: $($unexpected -join ', ')."
    }
}

function ConvertTo-EvidenceInt {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [int]$Minimum = [int]::MinValue,

        [int]$Maximum = [int]::MaxValue
    )

    if ($Value -is [bool] -or $Value -is [string] -or $null -eq $Value) {
        throw "$Name must be an integer."
    }

    try {
        $numericValue = [Convert]::ToDouble(
            $Value,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Name must be an integer."
    }
    if ([double]::IsNaN($numericValue) -or
        [double]::IsInfinity($numericValue) -or
        [Math]::Truncate($numericValue) -ne $numericValue) {
        throw "$Name must be an integer."
    }
    try {
        $number = [Convert]::ToInt32(
            $numericValue,
            [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Name must be an integer."
    }

    if ($number -lt $Minimum -or $number -gt $Maximum) {
        throw "$Name must be between $Minimum and $Maximum."
    }

    return $number
}

function ConvertTo-EvidenceDouble {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Value -is [bool] -or $Value -is [string]) {
        throw "$Name must be a JSON number."
    }

    try {
        return [Convert]::ToDouble($Value, [Globalization.CultureInfo]::InvariantCulture)
    }
    catch {
        throw "$Name must be a JSON number."
    }
}

function ConvertTo-EvidenceBool {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Value -isnot [bool]) {
        throw "$Name must be true or false."
    }

    return [bool]$Value
}

function Read-EvidenceSpec {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$SpecPath
    )

    if (-not (Test-Path -LiteralPath $SpecPath -PathType Leaf)) {
        throw "Evidence spec does not exist: '$SpecPath'."
    }

    try {
        $root = [IO.File]::ReadAllText(
            [IO.Path]::GetFullPath($SpecPath),
            [Text.Encoding]::UTF8) | ConvertFrom-Json
    }
    catch {
        throw "Evidence spec is not valid JSON: $($_.Exception.Message)"
    }
    if ($root -isnot [pscustomobject]) {
        throw "Evidence spec root must be a JSON object."
    }

    Assert-EvidenceProperties `
        -Object $root `
        -Allowed @("schemaVersion", "captures") `
        -Context "Evidence spec"

    $schemaVersion = ConvertTo-EvidenceInt `
        -Value (Get-RequiredEvidenceProperty -Object $root -Name "schemaVersion" -Context "Evidence spec") `
        -Name "schemaVersion" `
        -Minimum 1 `
        -Maximum 1
    $capturesProperty = $root.PSObject.Properties["captures"]
    if ($null -eq $capturesProperty) {
        throw "Evidence spec is missing required property 'captures'."
    }
    # Read the PSPropertyInfo value directly: returning a one-item JSON array
    # through a PowerShell function would otherwise enumerate it into a scalar.
    $rawCapturesValue = $capturesProperty.Value
    if ($rawCapturesValue -isnot [Array]) {
        throw "Evidence spec property 'captures' must be a JSON array."
    }
    $rawCaptures = @($rawCapturesValue)
    if ($rawCaptures.Count -eq 0) {
        throw "Evidence spec must contain at least one capture."
    }

    $allowedCaptureProperties = @(
        "name",
        "fixture",
        "screenshotTicks",
        "selectCreature",
        "selectCell",
        "demoControls",
        "demoDig",
        "demoStone",
        "demoBuild",
        "tileSize",
        "cameraZoom",
        "cameraPosition",
        "uiScale",
        "frameSize"
    )
    $captures = @()
    $names = @{}
    for ($index = 0; $index -lt $rawCaptures.Count; $index++) {
        $raw = $rawCaptures[$index]
        $context = "captures[$index]"
        if ($raw -isnot [pscustomobject]) {
            throw "$context must be a JSON object."
        }
        Assert-EvidenceProperties `
            -Object $raw `
            -Allowed $allowedCaptureProperties `
            -Context $context

        $name = [string](Get-RequiredEvidenceProperty `
            -Object $raw -Name "name" -Context $context)
        if ($name -cnotmatch '^[a-z0-9][a-z0-9._-]{0,79}$') {
            throw "$context.name must use 1-80 lower-case ASCII letters, digits, '.', '_' or '-'."
        }
        $nameKey = $name.ToLowerInvariant()
        if ($names.ContainsKey($nameKey)) {
            throw "Evidence capture name '$name' is duplicated."
        }
        $names[$nameKey] = $true

        $fixture = [string](Get-RequiredEvidenceProperty `
            -Object $raw -Name "fixture" -Context $context)
        if ($fixture -notin @("baseline", "prepared", "neglected")) {
            throw "$context.fixture must be baseline, prepared or neglected."
        }

        $screenshotTicks = ConvertTo-EvidenceInt `
            -Value (Get-RequiredEvidenceProperty `
                -Object $raw -Name "screenshotTicks" -Context $context) `
            -Name "$context.screenshotTicks" `
            -Minimum 0 `
            -Maximum 2700
        $tileSize = ConvertTo-EvidenceInt `
            -Value (Get-RequiredEvidenceProperty `
                -Object $raw -Name "tileSize" -Context $context) `
            -Name "$context.tileSize" `
            -Minimum 32 `
            -Maximum 48
        $cameraZoom = ConvertTo-EvidenceDouble `
            -Value (Get-RequiredEvidenceProperty `
                -Object $raw -Name "cameraZoom" -Context $context) `
            -Name "$context.cameraZoom"
        if ($cameraZoom -notin @(0.5, 0.75, 1.0, 1.5, 2.0)) {
            throw "$context.cameraZoom must be 0.5, 0.75, 1.0, 1.5 or 2.0."
        }
        $cameraPosition = [string](Get-RequiredEvidenceProperty `
            -Object $raw -Name "cameraPosition" -Context $context)
        if ($cameraPosition -cnotmatch '^-?\d+(\.\d+)?,-?\d+(\.\d+)?$') {
            throw "$context.cameraPosition must be x,y."
        }
        $uiScale = ConvertTo-EvidenceDouble `
            -Value (Get-RequiredEvidenceProperty `
                -Object $raw -Name "uiScale" -Context $context) `
            -Name "$context.uiScale"
        if ($uiScale -lt 0.75 -or $uiScale -gt 2.0) {
            throw "$context.uiScale must be between 0.75 and 2.0."
        }
        $frameSize = [string](Get-RequiredEvidenceProperty `
            -Object $raw -Name "frameSize" -Context $context)
        if ($frameSize -cnotmatch '^\d{3,5}x\d{3,5}$') {
            throw "$context.frameSize must be WIDTHxHEIGHT."
        }
        $frameParts = $frameSize -split "x", 2
        $frameWidth = [int]::Parse(
            $frameParts[0],
            [Globalization.CultureInfo]::InvariantCulture)
        $frameHeight = [int]::Parse(
            $frameParts[1],
            [Globalization.CultureInfo]::InvariantCulture)
        if (($frameWidth / $uiScale) -lt 1024 -or
            ($frameHeight / $uiScale) -lt 720) {
            throw (
                "$context.frameSize $frameSize at uiScale " +
                $uiScale.ToString([Globalization.CultureInfo]::InvariantCulture) +
                " provides less than the required 1024x720 logical pixels."
            )
        }

        $selectCreatureRaw = Get-OptionalEvidenceProperty `
            -Object $raw -Name "selectCreature" -DefaultValue $null
        $selectCreature = if ($null -eq $selectCreatureRaw) {
            $null
        }
        else {
            ConvertTo-EvidenceInt `
                -Value $selectCreatureRaw `
                -Name "$context.selectCreature" `
                -Minimum 0
        }
        $selectCellRaw = Get-OptionalEvidenceProperty `
            -Object $raw -Name "selectCell" -DefaultValue $null
        $selectCell = if ($null -eq $selectCellRaw) {
            $null
        }
        else {
            [string]$selectCellRaw
        }
        if ($null -ne $selectCell -and $selectCell -cnotmatch '^\d{1,2},\d{1,2}$') {
            throw "$context.selectCell must be col,row."
        }

        $captures += [pscustomobject][ordered]@{
            Name = $name
            Fixture = $fixture
            ScreenshotTicks = $screenshotTicks
            SelectCreature = $selectCreature
            SelectCell = $selectCell
            DemoControls = ConvertTo-EvidenceBool `
                -Value (Get-OptionalEvidenceProperty `
                    -Object $raw -Name "demoControls" -DefaultValue $false) `
                -Name "$context.demoControls"
            DemoDig = ConvertTo-EvidenceBool `
                -Value (Get-OptionalEvidenceProperty `
                    -Object $raw -Name "demoDig" -DefaultValue $false) `
                -Name "$context.demoDig"
            DemoStone = ConvertTo-EvidenceBool `
                -Value (Get-OptionalEvidenceProperty `
                    -Object $raw -Name "demoStone" -DefaultValue $false) `
                -Name "$context.demoStone"
            DemoBuild = ConvertTo-EvidenceBool `
                -Value (Get-OptionalEvidenceProperty `
                    -Object $raw -Name "demoBuild" -DefaultValue $false) `
                -Name "$context.demoBuild"
            TileSize = $tileSize
            CameraZoom = $cameraZoom
            CameraPosition = $cameraPosition
            UiScale = $uiScale
            FrameSize = $frameSize
        }
    }

    return [pscustomobject][ordered]@{
        SchemaVersion = $schemaVersion
        Captures = @($captures)
    }
}

function Resolve-EvidenceOutputRoot {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$RelativeOutputRoot
    )

    if ([string]::IsNullOrWhiteSpace($RelativeOutputRoot)) {
        throw "OutputRoot must be a non-empty relative path inside repository .artifacts."
    }
    if ($RelativeOutputRoot -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._ /\\-]*$') {
        throw "OutputRoot contains unsupported path characters."
    }

    $probe = Join-Path $RelativeOutputRoot "manifest.json"
    $resolvedProbe = Resolve-RepositoryArtifactPath `
        -RepositoryRoot $RepositoryRoot `
        -RelativePath $probe `
        -ParameterName "OutputRoot"
    return [IO.Path]::GetDirectoryName($resolvedProbe)
}

function Get-RepositoryRelativePath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path '$resolved' is outside repository '$root'."
    }

    return $resolved.Substring($prefix.Length).Replace(
        [IO.Path]::DirectorySeparatorChar, "/")
}

function New-EvidenceCaptureArguments {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Capture,

        [Parameter(Mandatory = $true)]
        [string]$ScreenshotPath,

        [string]$GodotPath
    )

    $arguments = @()
    if (-not [string]::IsNullOrWhiteSpace($GodotPath)) {
        $arguments += "-GodotPath", $GodotPath
    }
    $arguments += @(
        "-Fixture", $Capture.Fixture,
        "-ScreenshotTicks", $Capture.ScreenshotTicks.ToString(
            [Globalization.CultureInfo]::InvariantCulture),
        "-TileSize", $Capture.TileSize.ToString(
            [Globalization.CultureInfo]::InvariantCulture),
        "-CameraZoom", $Capture.CameraZoom.ToString(
            [Globalization.CultureInfo]::InvariantCulture),
        "-CameraPosition", $Capture.CameraPosition,
        "-UiScale", $Capture.UiScale.ToString(
            [Globalization.CultureInfo]::InvariantCulture),
        "-FrameSize", $Capture.FrameSize,
        "-ScreenshotPath", $ScreenshotPath
    )
    if ($null -ne $Capture.SelectCreature) {
        $arguments += "-SelectCreature", $Capture.SelectCreature.ToString(
            [Globalization.CultureInfo]::InvariantCulture)
    }
    if ($null -ne $Capture.SelectCell) {
        $arguments += "-SelectCell", $Capture.SelectCell
    }
    foreach ($flag in @(
        [pscustomobject]@{ Enabled = $Capture.DemoControls; Name = "-DemoControls" },
        [pscustomobject]@{ Enabled = $Capture.DemoDig; Name = "-DemoDig" },
        [pscustomobject]@{ Enabled = $Capture.DemoStone; Name = "-DemoStone" },
        [pscustomobject]@{ Enabled = $Capture.DemoBuild; Name = "-DemoBuild" }
    )) {
        if ($flag.Enabled) {
            $arguments += $flag.Name
        }
    }

    return @($arguments)
}

function New-EvidenceReproductionArguments {
    [CmdletBinding()]
    [OutputType([string[]])]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Capture,

        [Parameter(Mandatory = $true)]
        [string]$ScreenshotPath
    )

    return @(
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        ".\scripts\run-game.ps1"
    ) + @(New-EvidenceCaptureArguments `
        -Capture $Capture `
        -ScreenshotPath $ScreenshotPath)
}

function ConvertTo-PowerShellCommand {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $tokens = @($Arguments | ForEach-Object {
        $value = [string]$_
        if ($value -cmatch '^[A-Za-z0-9_./:\\-]+$') {
            $value
        }
        else {
            "'" + $value.Replace("'", "''") + "'"
        }
    })
    return $tokens -join " "
}

function Get-EvidenceCaptureEvent {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$OutputLines
    )

    $line = $OutputLines | Where-Object {
        [string]$_ -match '"event":"godot_graybox_screenshot"' -and
        [string]$_ -match '"status":"ok"'
    } | Select-Object -Last 1
    if ($null -eq $line) {
        throw "Capture output contains no successful godot_graybox_screenshot event."
    }

    try {
        return [string]$line | ConvertFrom-Json
    }
    catch {
        throw "Capture event is not valid JSON: $($_.Exception.Message)"
    }
}

function Assert-EvidenceCaptureEvent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Event,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$Capture,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedPath
    )

    if ([string]$Event.fixture -cne $Capture.Fixture) {
        throw "Capture '$($Capture.Name)' reported fixture '$($Event.fixture)', expected '$($Capture.Fixture)'."
    }
    if ([int]$Event.tick -ne $Capture.ScreenshotTicks) {
        throw "Capture '$($Capture.Name)' reported tick $($Event.tick), expected $($Capture.ScreenshotTicks)."
    }
    if ([string]::IsNullOrWhiteSpace([string]$Event.checksum)) {
        throw "Capture '$($Capture.Name)' reported an empty canonical checksum."
    }
    if ([IO.Path]::GetFullPath([string]$Event.path) -cne [IO.Path]::GetFullPath($ExpectedPath)) {
        throw "Capture '$($Capture.Name)' wrote '$($Event.path)', expected '$ExpectedPath'."
    }
    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) {
        throw "Capture '$($Capture.Name)' did not write '$ExpectedPath'."
    }
}

function Assert-EvidenceFilesEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPath,

        [Parameter(Mandatory = $true)]
        [string]$ActualPath
    )

    $expected = [IO.File]::ReadAllBytes($ExpectedPath)
    $actual = [IO.File]::ReadAllBytes($ActualPath)
    if ($expected.Length -ne $actual.Length) {
        throw "Repeated evidence differs in byte length."
    }
    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($expected[$index] -ne $actual[$index]) {
            throw "Repeated evidence differs at byte $index."
        }
    }
}

function Assert-EvidenceEventsEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Expected,

        [Parameter(Mandatory = $true)]
        [pscustomobject]$Actual
    )

    foreach ($property in @("fixture", "seed", "tick", "checksum")) {
        if ([string]$Expected.$property -cne [string]$Actual.$property) {
            throw "Repeated evidence differs in structured property '$property'."
        }
    }
}

function Get-EvidenceSha256 {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-EvidenceSourcePublishable {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [bool]$SourceDirty,

        [switch]$AllowDirtySource
    )

    if ($SourceDirty -and -not $AllowDirtySource) {
        throw (
            "Source worktree is dirty. Commit the evidence-producing state first, " +
            "or use -AllowDirtySource only for a non-publishable diagnostic bundle."
        )
    }
}

function Write-EvidenceManifest {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [string]$OutputRoot,

        [Parameter(Mandatory = $true)]
        [string]$SpecPath,

        [Parameter(Mandatory = $true)]
        [string]$SourceCommit,

        [Parameter(Mandatory = $true)]
        [bool]$SourceDirty,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Captures
    )

    $manifest = [ordered]@{
        schemaVersion = 1
        tool = "scripts/capture-evidence.ps1"
        sourceSpec = Get-RepositoryRelativePath `
            -RepositoryRoot $RepositoryRoot `
            -Path $SpecPath
        sourceCommit = $SourceCommit
        sourceDirty = $SourceDirty
        reproducible = -not $SourceDirty
        publishable = -not $SourceDirty
        captures = @($Captures)
    }
    $jsonPath = Join-Path $OutputRoot "manifest.json"
    $markdownPath = Join-Path $OutputRoot "manifest.md"
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText(
        $jsonPath,
        ($manifest | ConvertTo-Json -Depth 30),
        $utf8)

    $markdown = @(
        "# Visual evidence",
        "",
        ('- Source commit: `{0}`' -f $SourceCommit),
        ('- Source dirty: `{0}`' -f $SourceDirty.ToString().ToLowerInvariant()),
        ('- Source spec: `{0}`' -f $manifest.sourceSpec),
        ('- Reproducible from source commit: **{0}**' -f
            $(if ($manifest.reproducible) { "yes" } else { "no" })),
        ('- Publishable evidence: **{0}**' -f
            $(if ($manifest.publishable) { "yes" } else { "no" })),
        "- Repeated byte-for-byte: **yes**"
    )
    if ($SourceDirty) {
        $markdown += @(
            "",
            "> Warning: the source worktree was dirty. This diagnostic bundle is not publishable."
        )
    }
    $markdown += @(
        "",
        "| Capture | Fixture / tick | Canonical checksum | PNG SHA-256 |",
        "|---|---:|---|---|"
    )
    foreach ($capture in $Captures) {
        $markdown += (
            '| `{0}` | `{1}` / `{2}` | `{3}` | `{4}` |' -f
            $capture.name,
            $capture.fixture,
            $capture.tick,
            $capture.checksum,
            $capture.imageSha256
        )
    }
    foreach ($capture in $Captures) {
        $markdown += @(
            "",
            "## $($capture.name)",
            "",
            '```powershell',
            $capture.command,
            '```',
            "",
            ('Local ignored PNG: `{0}`' -f $capture.imagePath)
        )
    }
    [IO.File]::WriteAllText(
        $markdownPath,
        (($markdown -join [Environment]::NewLine) + [Environment]::NewLine),
        $utf8)

    return [pscustomobject]@{
        JsonPath = $jsonPath
        MarkdownPath = $markdownPath
        Manifest = [pscustomobject]$manifest
    }
}
