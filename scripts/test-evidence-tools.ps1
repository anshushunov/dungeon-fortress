[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "EvidenceTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("evidence-tools-test-" + [Guid]::NewGuid().ToString("N"))
$utf8 = [Text.UTF8Encoding]::new($false)

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Action,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedMessage,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -cne $ExpectedMessage) {
            throw (
                "$Message Expected error '$ExpectedMessage', got " +
                "'$($_.Exception.Message)'."
            )
        }
        return
    }
    throw $Message
}

function Invoke-ExpectedScriptFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedErrorPattern,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File (Join-Path $repoRoot "scripts\capture-evidence.ps1") `
            @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 1) {
        throw "$Message Expected exit code 1, got $exitCode."
    }
    $outputText = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($outputText -cnotmatch $ExpectedErrorPattern) {
        throw "$Message Expected error matching '$ExpectedErrorPattern', got '$outputText'."
    }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $validSpecPath = Join-Path $testRoot "valid.json"
    $validJson = @'
{
  "schemaVersion": 1,
  "captures": [
    {
      "name": "baseline-t1",
      "fixture": "baseline",
      "screenshotTicks": 1,
      "selectCell": "25,1",
      "demoStone": true,
      "tileSize": 40,
      "cameraZoom": 0.5,
      "cameraPosition": "560,320",
      "uiScale": 1.0,
      "frameSize": "1280x720"
    }
  ]
}
'@
    [IO.File]::WriteAllText($validSpecPath, $validJson, $utf8)
    $spec = Read-EvidenceSpec -SpecPath $validSpecPath
    if ($spec.SchemaVersion -ne 1 -or $spec.Captures.Count -ne 1) {
        throw "Valid evidence spec was not normalized."
    }
    $capture = $spec.Captures[0]
    $machineGodotPath = "C:\machine-specific\Godot_v4.7.1-stable_mono_win64.exe"
    $arguments = @(New-EvidenceCaptureArguments `
        -Capture $capture `
        -ScreenshotPath "evidence/test/baseline-t1.png" `
        -GodotPath $machineGodotPath)
    foreach ($required in @(
        "-ScreenshotTicks",
        "-SelectCell",
        "-DemoStone",
        "-TileSize",
        "-CameraZoom",
        "-CameraPosition",
        "-UiScale",
        "-FrameSize",
        "-ScreenshotPath"
    )) {
        if ($required -notin $arguments) {
            throw "Capture arguments omit '$required'."
        }
    }
    if ($arguments[0] -cne "-GodotPath" -or $arguments[1] -cne $machineGodotPath) {
        throw "Runtime capture arguments omit the explicit machine Godot path."
    }
    $reproductionArguments = @(New-EvidenceReproductionArguments `
        -Capture $capture `
        -ScreenshotPath "evidence/test/baseline-t1.png")
    if ("-GodotPath" -in $reproductionArguments -or
        $machineGodotPath -in $reproductionArguments) {
        throw "Published reproduction arguments leaked a machine-specific Godot path."
    }
    $command = ConvertTo-PowerShellCommand `
        -Arguments $reproductionArguments
    if ($command -notmatch "-ScreenshotTicks 1" -or
        $command -notmatch "-CameraPosition '560,320'") {
        throw "Reproduction command does not preserve explicit capture parameters."
    }

    $primaryPath = Join-Path $testRoot "primary.png"
    $repeatPath = Join-Path $testRoot "repeat.png"
    [IO.File]::WriteAllBytes($primaryPath, [byte[]]@(1, 2, 3, 4))
    [IO.File]::WriteAllBytes($repeatPath, [byte[]]@(1, 2, 3, 4))
    Assert-EvidenceFilesEqual -ExpectedPath $primaryPath -ActualPath $repeatPath
    $hash = Get-EvidenceSha256 -Path $primaryPath
    if ($hash -cne "9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a") {
        throw "Evidence SHA-256 is not stable."
    }
    [IO.File]::WriteAllBytes($repeatPath, [byte[]]@(1, 2, 3, 5))
    Assert-Throws `
        -Action {
            Assert-EvidenceFilesEqual -ExpectedPath $primaryPath -ActualPath $repeatPath
        } `
        -ExpectedMessage "Repeated evidence differs at byte 3." `
        -Message "Byte-for-byte guard accepted a changed repeat image."

    $eventLine = [ordered]@{
        event = "godot_graybox_screenshot"
        status = "ok"
        fixture = "baseline"
        seed = 424242
        tick = 1
        checksum = "canonical-a"
        path = $primaryPath
        view = [ordered]@{ requestedFrame = "1280x720" }
    } | ConvertTo-Json -Compress
    $event = Get-EvidenceCaptureEvent -OutputLines @("noise", $eventLine)
    Assert-EvidenceCaptureEvent `
        -Event $event `
        -Capture $capture `
        -ExpectedPath $primaryPath
    $changedEvent = $eventLine | ConvertFrom-Json
    $changedEvent.checksum = "canonical-b"
    Assert-Throws `
        -Action {
            Assert-EvidenceEventsEqual -Expected $event -Actual $changedEvent
        } `
        -ExpectedMessage "Repeated evidence differs in structured property 'checksum'." `
        -Message "Structured repeat guard accepted a changed checksum."

    [IO.File]::WriteAllBytes($repeatPath, [byte[]]@(1, 2, 3, 4))
    $manifestCapture = [pscustomobject][ordered]@{
        name = "baseline-t1"
        fixture = "baseline"
        seed = 424242
        tick = 1
        checksum = "canonical-a"
        imagePath = ".artifacts/evidence/test/baseline-t1.png"
        repeatImagePath = ".artifacts/evidence/test/baseline-t1.repeat.png"
        imageSha256 = $hash
        repeatImageSha256 = $hash
        byteForByteRepeat = $true
        command = $command
        repeatCommand = $command
        parameters = [ordered]@{ frameSize = "1280x720" }
        view = [ordered]@{ requestedFrame = "1280x720" }
    }
    $written = Write-EvidenceManifest `
        -RepositoryRoot $repoRoot `
        -OutputRoot $testRoot `
        -SpecPath $validSpecPath `
        -SourceCommit ("a" * 40) `
        -SourceDirty $false `
        -Captures @($manifestCapture)
    $manifest = [IO.File]::ReadAllText($written.JsonPath, [Text.Encoding]::UTF8) |
        ConvertFrom-Json
    if (-not $manifest.reproducible -or $manifest.sourceDirty -or
        -not $manifest.publishable -or
        $manifest.captures[0].imageSha256 -cne $hash -or
        -not (Test-Path -LiteralPath $written.MarkdownPath -PathType Leaf)) {
        throw "Evidence manifest omitted reproducibility data."
    }
    Assert-Throws `
        -Action {
            Assert-EvidenceSourcePublishable -SourceDirty $true
        } `
        -ExpectedMessage (
            "Source worktree is dirty. Commit the evidence-producing state first, " +
            "or use -AllowDirtySource only for a non-publishable diagnostic bundle.") `
        -Message "Dirty source was accepted as publishable by default."
    Assert-EvidenceSourcePublishable -SourceDirty $true -AllowDirtySource
    $dirtyRoot = Join-Path $testRoot "dirty"
    New-Item -ItemType Directory -Force -Path $dirtyRoot | Out-Null
    $dirtyWritten = Write-EvidenceManifest `
        -RepositoryRoot $repoRoot `
        -OutputRoot $dirtyRoot `
        -SpecPath $validSpecPath `
        -SourceCommit ("b" * 40) `
        -SourceDirty $true `
        -Captures @($manifestCapture)
    $dirtyManifest = [IO.File]::ReadAllText(
        $dirtyWritten.JsonPath,
        [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($dirtyManifest.reproducible -or $dirtyManifest.publishable) {
        throw "Dirty-source manifest claims to be reproducible or publishable."
    }
    $dirtyMarkdown = [IO.File]::ReadAllLines(
        $dirtyWritten.MarkdownPath,
        [Text.Encoding]::UTF8)
    $warningIndex = [Array]::IndexOf(
        $dirtyMarkdown,
        "> Warning: the source worktree was dirty. This diagnostic bundle is not publishable.")
    $tableHeaderIndex = [Array]::IndexOf(
        $dirtyMarkdown,
        "| Capture | Fixture / tick | Canonical checksum | PNG SHA-256 |")
    if ($warningIndex -lt 0 -or $tableHeaderIndex -le $warningIndex -or
        $dirtyMarkdown[$tableHeaderIndex + 1] -cne "|---|---:|---|---|" -or
        $dirtyMarkdown[$tableHeaderIndex + 2] -cnotmatch '^\| `baseline-t1` \|') {
        throw "Dirty-source Markdown warning interrupts or precedes no intact capture table."
    }

    $duplicateSpecPath = Join-Path $testRoot "duplicate.json"
    $duplicateJson = @'
{
  "schemaVersion": 1,
  "captures": [
    {
      "name": "same",
      "fixture": "baseline",
      "screenshotTicks": 1,
      "tileSize": 40,
      "cameraZoom": 0.5,
      "cameraPosition": "560,320",
      "uiScale": 1.0,
      "frameSize": "1280x720"
    },
    {
      "name": "same",
      "fixture": "baseline",
      "screenshotTicks": 2,
      "tileSize": 40,
      "cameraZoom": 0.5,
      "cameraPosition": "560,320",
      "uiScale": 1.0,
      "frameSize": "1280x720"
    }
  ]
}
'@
    [IO.File]::WriteAllText($duplicateSpecPath, $duplicateJson, $utf8)
    Invoke-ExpectedScriptFailure `
        -Arguments @(
            "-SpecPath", $duplicateSpecPath,
            "-OutputRoot", "evidence\invalid",
            "-ValidateOnly"
        ) `
        -ExpectedErrorPattern "Evidence capture name 'same' is duplicated\." `
        -Message "capture-evidence accepted duplicate names."

    $fractionalSpecPath = Join-Path $testRoot "fractional.json"
    [IO.File]::WriteAllText(
        $fractionalSpecPath,
        $validJson.Replace('"schemaVersion": 1', '"schemaVersion": 1.4'),
        $utf8)
    Invoke-ExpectedScriptFailure `
        -Arguments @(
            "-SpecPath", $fractionalSpecPath,
            "-OutputRoot", "evidence\invalid",
            "-ValidateOnly"
        ) `
        -ExpectedErrorPattern "schemaVersion must be an integer\." `
        -Message "capture-evidence rounded a fractional schemaVersion."

    $objectCapturesPath = Join-Path $testRoot "object-captures.json"
    $objectCapturesJson = @'
{
  "schemaVersion": 1,
  "captures": {
    "name": "not-an-array",
    "fixture": "baseline",
    "screenshotTicks": 1,
    "tileSize": 40,
    "cameraZoom": 0.5,
    "cameraPosition": "560,320",
    "uiScale": 1.0,
    "frameSize": "1280x720"
  }
}
'@
    [IO.File]::WriteAllText($objectCapturesPath, $objectCapturesJson, $utf8)
    Invoke-ExpectedScriptFailure `
        -Arguments @(
            "-SpecPath", $objectCapturesPath,
            "-OutputRoot", "evidence\invalid",
            "-ValidateOnly"
        ) `
        -ExpectedErrorPattern "Evidence spec property 'captures' must be a JSON array\." `
        -Message "capture-evidence accepted object-form captures."
    Invoke-ExpectedScriptFailure `
        -Arguments @(
            "-SpecPath", $validSpecPath,
            "-OutputRoot", "good\..\..\outside",
            "-ValidateOnly"
        ) `
        -ExpectedErrorPattern "OutputRoot resolves outside repository \.artifacts\." `
        -Message "capture-evidence accepted output traversal."

    [ordered]@{
        event = "evidence_tools_test"
        status = "ok"
        engineStarted = $false
        duplicateRejected = $true
        fractionalIntegerRejected = $true
        objectCapturesRejected = $true
        traversalRejected = $true
        byteDifferenceRejected = $true
        checksumDifferenceRejected = $true
        dirtySourceRejectedByDefault = $true
        dirtyMarkdownTableIntact = $true
        portableCommandOmitsGodotPath = $true
        manifestWritten = $true
    } | ConvertTo-Json -Compress | Write-Host
}
finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $prefix = $artifactsRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($resolvedTestRoot.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTestRoot)) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
