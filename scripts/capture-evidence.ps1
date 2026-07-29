[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SpecPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputRoot,

    [string]$GodotPath,

    [switch]$AllowDirtySource,

    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")
. (Join-Path $PSScriptRoot "EvidenceTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedSpecPath = [IO.Path]::GetFullPath($SpecPath)

function Invoke-EvidenceRunGame {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RunGamePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $RunGamePath `
            @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    $output | ForEach-Object { Write-Host $_ }
    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = @($output)
    }
}

function Get-EvidenceSourceState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $commitOutput = @(& git `
            -c ("safe.directory=" + $RepositoryRoot) `
            -C $RepositoryRoot `
            rev-parse HEAD 2>&1)
        $commitExitCode = $LASTEXITCODE
        $statusOutput = @(& git `
            -c ("safe.directory=" + $RepositoryRoot) `
            -C $RepositoryRoot `
            status --porcelain 2>&1)
        $statusExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($commitExitCode -ne 0 -or $statusExitCode -ne 0) {
        throw "Cannot resolve source Git state for evidence manifest."
    }
    $commit = (($commitOutput | ForEach-Object { [string]$_ }) -join "").Trim()
    if ([string]::IsNullOrWhiteSpace($commit)) {
        throw "Cannot resolve source Git commit for evidence manifest."
    }
    $dirty = @($statusOutput | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_)
    }).Count -gt 0
    return [pscustomobject]@{
        Commit = $commit
        Dirty = $dirty
    }
}

try {
    $null = Get-RepositoryRelativePath `
        -RepositoryRoot $repoRoot `
        -Path $resolvedSpecPath
    $resolvedOutputRoot = Resolve-EvidenceOutputRoot `
        -RepositoryRoot $repoRoot `
        -RelativeOutputRoot $OutputRoot
    $spec = Read-EvidenceSpec -SpecPath $resolvedSpecPath
    if ($ValidateOnly) {
        [ordered]@{
            event = "evidence_spec_validation"
            status = "ok"
            captures = $spec.Captures.Count
            outputRoot = Get-RepositoryRelativePath `
                -RepositoryRoot $repoRoot `
                -Path $resolvedOutputRoot
        } | ConvertTo-Json -Compress | Write-Host
        exit 0
    }

    $sourceState = Get-EvidenceSourceState -RepositoryRoot $repoRoot
    Assert-EvidenceSourcePublishable `
        -SourceDirty $sourceState.Dirty `
        -AllowDirtySource:$AllowDirtySource
    New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null
    $runGamePath = Join-Path $repoRoot "scripts\run-game.ps1"
    $manifestCaptures = @()
    foreach ($capture in $spec.Captures) {
        $relativePrimaryPath = (
            Join-Path $OutputRoot ($capture.Name + ".png")).Replace("\", "/")
        $relativeRepeatPath = (
            Join-Path $OutputRoot ($capture.Name + ".repeat.png")).Replace("\", "/")
        $primaryPath = Resolve-RepositoryArtifactPath `
            -RepositoryRoot $repoRoot `
            -RelativePath $relativePrimaryPath
        $repeatPath = Resolve-RepositoryArtifactPath `
            -RepositoryRoot $repoRoot `
            -RelativePath $relativeRepeatPath
        $primaryArguments = @(New-EvidenceCaptureArguments `
            -Capture $capture `
            -ScreenshotPath $relativePrimaryPath `
            -GodotPath $GodotPath)
        $repeatArguments = @(New-EvidenceCaptureArguments `
            -Capture $capture `
            -ScreenshotPath $relativeRepeatPath `
            -GodotPath $GodotPath)
        $reproductionArguments = @(New-EvidenceReproductionArguments `
            -Capture $capture `
            -ScreenshotPath $relativePrimaryPath)
        $repeatReproductionArguments = @(New-EvidenceReproductionArguments `
            -Capture $capture `
            -ScreenshotPath $relativeRepeatPath)

        Write-Host "Capturing evidence '$($capture.Name)' (primary)..."
        $primaryResult = Invoke-EvidenceRunGame `
            -RunGamePath $runGamePath `
            -Arguments $primaryArguments
        if ($primaryResult.ExitCode -ne 0) {
            throw "Primary capture '$($capture.Name)' failed with exit code $($primaryResult.ExitCode)."
        }
        $primaryEvent = Get-EvidenceCaptureEvent -OutputLines $primaryResult.Output
        Assert-EvidenceCaptureEvent `
            -Event $primaryEvent `
            -Capture $capture `
            -ExpectedPath $primaryPath

        Write-Host "Capturing evidence '$($capture.Name)' (repeat)..."
        $repeatResult = Invoke-EvidenceRunGame `
            -RunGamePath $runGamePath `
            -Arguments $repeatArguments
        if ($repeatResult.ExitCode -ne 0) {
            throw "Repeat capture '$($capture.Name)' failed with exit code $($repeatResult.ExitCode)."
        }
        $repeatEvent = Get-EvidenceCaptureEvent -OutputLines $repeatResult.Output
        Assert-EvidenceCaptureEvent `
            -Event $repeatEvent `
            -Capture $capture `
            -ExpectedPath $repeatPath
        Assert-EvidenceEventsEqual -Expected $primaryEvent -Actual $repeatEvent
        Assert-EvidenceFilesEqual -ExpectedPath $primaryPath -ActualPath $repeatPath

        $primaryHash = Get-EvidenceSha256 -Path $primaryPath
        $repeatHash = Get-EvidenceSha256 -Path $repeatPath
        if ($primaryHash -cne $repeatHash) {
            throw "Repeated evidence '$($capture.Name)' has different SHA-256 values."
        }

        $manifestCaptures += [pscustomobject][ordered]@{
            name = $capture.Name
            fixture = [string]$primaryEvent.fixture
            seed = [UInt64]$primaryEvent.seed
            tick = [int]$primaryEvent.tick
            checksum = [string]$primaryEvent.checksum
            imagePath = Get-RepositoryRelativePath `
                -RepositoryRoot $repoRoot `
                -Path $primaryPath
            repeatImagePath = Get-RepositoryRelativePath `
                -RepositoryRoot $repoRoot `
                -Path $repeatPath
            imageSha256 = $primaryHash
            repeatImageSha256 = $repeatHash
            byteForByteRepeat = $true
            command = ConvertTo-PowerShellCommand -Arguments $reproductionArguments
            repeatCommand = ConvertTo-PowerShellCommand `
                -Arguments $repeatReproductionArguments
            parameters = [ordered]@{
                screenshotTicks = $capture.ScreenshotTicks
                selectCreature = $capture.SelectCreature
                selectCell = $capture.SelectCell
                demoControls = $capture.DemoControls
                demoDig = $capture.DemoDig
                demoStone = $capture.DemoStone
                demoBuild = $capture.DemoBuild
                tileSize = $capture.TileSize
                cameraZoom = $capture.CameraZoom
                cameraPosition = $capture.CameraPosition
                uiScale = $capture.UiScale
                frameSize = $capture.FrameSize
            }
            view = $primaryEvent.view
        }
    }

    $written = Write-EvidenceManifest `
        -RepositoryRoot $repoRoot `
        -OutputRoot $resolvedOutputRoot `
        -SpecPath $resolvedSpecPath `
        -SourceCommit $sourceState.Commit `
        -SourceDirty $sourceState.Dirty `
        -Captures $manifestCaptures

    [ordered]@{
        event = "evidence_capture"
        status = "ok"
        captures = $manifestCaptures.Count
        manifestJson = Get-RepositoryRelativePath `
            -RepositoryRoot $repoRoot `
            -Path $written.JsonPath
        manifestMarkdown = Get-RepositoryRelativePath `
            -RepositoryRoot $repoRoot `
            -Path $written.MarkdownPath
        reproducible = [bool]$written.Manifest.reproducible
        publishable = [bool]$written.Manifest.publishable
    } | ConvertTo-Json -Compress | Write-Host
}
catch {
    [ordered]@{
        event = "evidence_capture"
        status = "error"
        reason = $_.Exception.Message
    } | ConvertTo-Json -Compress | Write-Host
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
