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
        [string]$Message
    )

    try {
        & $Action
    }
    catch {
        return
    }
    throw $Message
}

function Invoke-ExpectedScriptFailure {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Message
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $null = & powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File (Join-Path $repoRoot "scripts\capture-evidence.ps1") `
            @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 1) {
        throw "$Message Expected exit code 1, got $exitCode."
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
    $arguments = @(New-EvidenceCaptureArguments `
        -Capture $capture `
        -ScreenshotPath "evidence/test/baseline-t1.png")
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
    $command = ConvertTo-PowerShellCommand `
        -Arguments (@("powershell", "-File", ".\scripts\run-game.ps1") + $arguments)
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
        -Message "capture-evidence accepted object-form captures."
    Invoke-ExpectedScriptFailure `
        -Arguments @(
            "-SpecPath", $validSpecPath,
            "-OutputRoot", "..\outside",
            "-ValidateOnly"
        ) `
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
