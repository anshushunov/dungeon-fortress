Set-StrictMode -Version Latest

# Golden UI state replaces golden screenshots. Comparing PNGs was considered and
# rejected in Issue #28: the three Issue #26 frames did reproduce byte-for-byte,
# but on one machine and one driver, so on any other machine the pixels move and
# the test becomes a source of false failures. `.artifacts/` is ignored, so the
# references would also have to be committed as binary blobs nobody can read in a
# diff. The text of the HUD is the part that actually carries meaning, it is
# deterministic, it is cross-platform, and it reads in a diff.
#
# Nothing recorded here depends on the camera. Pixel positions, the visible tile
# range and the viewport size are deliberately absent, because ADR 0008 drops the
# fixed 960x540 frame and those values stop being stable the moment a camera with
# panning and zoom lands.

$script:GoldenUiStoneFields = @(
    "stoneProduced",
    "looseStone",
    "carriedStone",
    "storedStone",
    "stockpileCapacity"
)

$script:GoldenUiTextFields = @(
    "summary",
    "inspector",
    "feedback",
    "roster",
    "controlFeedback",
    "editMode",
    "brushZone",
    "selectedCell",
    "selectedCreatureId"
)

function Get-GoldenUiFrames {
    [CmdletBinding()]
    [OutputType([object[]])]
    param()

    # The three reproducible `--demo-stone` moments already documented in
    # docs/engineering/PROTOTYPE_GRAYBOX.md: stone with nowhere to go, stone in
    # transit, and a full stockpile. `--select-cell` points the inspector at the
    # tile each frame is about, so the explanation under test is chosen instead of
    # being whatever the demo happened to select last.
    return @(
        [pscustomobject]@{
            Name       = "stone-t190-loose-no-stockpile"
            Fixture    = "baseline"
            Tick       = 190
            SelectCell = "25,3"
        },
        [pscustomobject]@{
            Name       = "stone-t336-in-transit"
            Fixture    = "baseline"
            Tick       = 336
            SelectCell = "25,1"
        },
        [pscustomobject]@{
            Name       = "stone-t950-stockpile-full"
            Fixture    = "baseline"
            Tick       = 950
            SelectCell = "23,1"
        }
    )
}

function Get-GoldenUiPath {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,

        [Parameter(Mandatory = $true)]
        [object]$Frame
    )

    return Join-Path $RepositoryRoot ("tests\golden\ui\" + $Frame.Name + ".json")
}

function Invoke-GoldenUiCapture {
    [CmdletBinding()]
    [OutputType([pscustomobject])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath,

        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [object]$Frame
    )

    # Headless on purpose: the HUD text is built in C# and does not need a window,
    # a display or an imported sprite to exist. This is the whole point of the
    # Issue - the state of the UI becomes checkable without producing a picture.
    $arguments = @(
        "--headless", "--path", $ProjectPath,
        "--", "--smoke",
        "--fixture", $Frame.Fixture,
        "--demo-stone",
        "--screenshot-ticks", $Frame.Tick.ToString([Globalization.CultureInfo]::InvariantCulture),
        "--select-cell", $Frame.SelectCell
    )

    $result = Invoke-GodotChecked `
        -GodotPath $GodotPath `
        -Arguments $arguments `
        -ExpectedSuccessEvent "godot_headless_smoke"

    $resultLine = $result.Output | Where-Object {
        $_ -match '"event":"godot_headless_smoke"' -and $_ -match '"status":"ok"'
    } | Select-Object -Last 1

    $capture = ([string]$resultLine | ConvertFrom-Json)
    if ([int]$capture.tick -ne [int]$Frame.Tick) {
        throw "Golden UI frame '$($Frame.Name)' reported tick $($capture.tick) instead of $($Frame.Tick)."
    }

    return $capture
}

function ConvertTo-GoldenUiDocument {
    [CmdletBinding()]
    [OutputType([Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Frame,

        [Parameter(Mandatory = $true)]
        [object]$Capture
    )

    $stone = [ordered]@{}
    foreach ($field in $script:GoldenUiStoneFields) {
        $stone[$field] = $Capture.$field
    }

    $ui = [ordered]@{}
    foreach ($field in $script:GoldenUiTextFields) {
        $ui[$field] = $Capture.ui.$field
    }

    return [ordered]@{
        frame = [ordered]@{
            fixture    = $Frame.Fixture
            demo       = "stone"
            tick       = [int]$Capture.tick
            selectCell = $Frame.SelectCell
        }
        stone = $stone
        ui    = $ui
    }
}

function ConvertTo-GoldenUiComparable {
    [CmdletBinding()]
    [OutputType([string])]
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return "<null>"
    }

    if ($Value -is [Array]) {
        return (@($Value) | ForEach-Object { [string]$_ }) -join ","
    }

    return [string]$Value
}

function Assert-GoldenUiFrame {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedPath,

        [Parameter(Mandatory = $true)]
        [object]$Actual,

        [Parameter(Mandatory = $true)]
        [string]$FrameName
    )

    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) {
        throw "Golden UI state '$FrameName' is missing at '$ExpectedPath'. Create it with scripts\update-golden-ui.ps1."
    }

    $expected = [IO.File]::ReadAllText($ExpectedPath) | ConvertFrom-Json
    $differences = @()

    foreach ($section in @("frame", "stone", "ui")) {
        # Strict mode turns a missing property into a terminating error, so the
        # section is looked up rather than dereferenced.
        $sectionProperty = $expected.PSObject.Properties[$section]
        if ($null -eq $sectionProperty) {
            $differences += "section '$section' is missing from the golden file"
            continue
        }

        $expectedSection = $sectionProperty.Value
        $actualSection = $Actual[$section]

        $expectedKeys = @($expectedSection.PSObject.Properties.Name)
        $actualKeys = @($actualSection.Keys)
        $onlyExpected = @($expectedKeys | Where-Object { $_ -notin $actualKeys })
        $onlyActual = @($actualKeys | Where-Object { $_ -notin $expectedKeys })
        if ($onlyExpected.Count -gt 0 -or $onlyActual.Count -gt 0) {
            $differences += "$section keys differ: only in golden [$($onlyExpected -join ', ')], only in run [$($onlyActual -join ', ')]"
            continue
        }

        foreach ($key in $expectedKeys) {
            $expectedValue = ConvertTo-GoldenUiComparable -Value $expectedSection.$key
            $actualValue = ConvertTo-GoldenUiComparable -Value $actualSection[$key]
            if ($expectedValue -cne $actualValue) {
                $differences += "$section.$key`n    golden: $expectedValue`n    run:    $actualValue"
            }
        }
    }

    if ($differences.Count -gt 0) {
        [ordered]@{
            event           = "golden_ui_state"
            status          = "error"
            frame           = $FrameName
            differenceCount = $differences.Count
        } | ConvertTo-Json -Compress | Write-Host

        throw (
            "Golden UI state '$FrameName' does not match the run:`n  " +
            ($differences -join "`n  ") +
            "`nIf the change is intended, regenerate with scripts\update-golden-ui.ps1 and review the diff."
        )
    }

    Write-VerifyDiagnostic -Text (([ordered]@{
        event  = "golden_ui_state"
        status = "ok"
        frame  = $FrameName
    } | ConvertTo-Json -Compress))
}

function Write-GoldenUiDocument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Document
    )

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null

    # LF and no BOM, matching what Git stores. Regenerating on another platform
    # must produce no diff at all when nothing actually changed. The line breaks
    # inside the HUD strings are escaped as \n by ConvertTo-Json and are not
    # touched by this.
    $json = ($Document | ConvertTo-Json -Depth 6) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($Path, $json + "`n", [Text.UTF8Encoding]::new($false))
}

function Assert-FramePacingIndependence {
    [CmdletBinding()]
    [OutputType([Collections.Specialized.OrderedDictionary])]
    param(
        [Parameter(Mandatory = $true)]
        [string]$GodotPath,

        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [int]$TargetTick,

        [Parameter(Mandatory = $true)]
        [int[]]$FixedFps
    )

    # Interpolation is only allowed to change the picture. `--frame-pacing` drives
    # the real _Process loop to a fixed tick and reports the canonical checksum, so
    # Godot's `--fixed-fps` turns "does the frame rate reach the simulation?" into
    # an ordinary headless comparison instead of something a human judges from a
    # video.
    #
    # Four claims are checked, and each maps to one acceptance criterion of
    # Issue #36:
    #   - both frame rates end on the same tick with the same canonical checksum,
    #     and each equals the checksum of a frameless replay of the same log;
    #   - the two runs really did use different frame rates (frame counts differ);
    #   - interpolation never leads the simulation into a tile it has not reached;
    #   - no single frame moves a body by a whole tile, which is the teleporting
    #     the Issue was opened about.
    $runs = @()
    foreach ($fps in $FixedFps) {
        $result = Invoke-GodotChecked `
            -GodotPath $GodotPath `
            -Arguments @(
                "--headless", "--fixed-fps",
                $fps.ToString([Globalization.CultureInfo]::InvariantCulture),
                "--path", $ProjectPath,
                "--", "--fixture", "baseline",
                "--frame-pacing", $TargetTick.ToString([Globalization.CultureInfo]::InvariantCulture)
            ) `
            -ExpectedSuccessEvent "godot_frame_pacing"

        $resultLine = $result.Output | Where-Object {
            $_ -match '"event":"godot_frame_pacing"' -and $_ -match '"status":"ok"'
        } | Select-Object -Last 1

        $capture = ([string]$resultLine | ConvertFrom-Json)
        if ([int]$capture.tick -ne $TargetTick) {
            throw "Frame pacing run at $fps fps stopped at tick $($capture.tick) instead of $TargetTick."
        }

        if ($capture.checksum -cne $capture.replayChecksum) {
            throw (
                "Frame pacing run at $fps fps produced canonical checksum $($capture.checksum), " +
                "but replaying the same log without any frames produced $($capture.replayChecksum). " +
                "The render loop is writing to the simulation."
            )
        }

        if ([int]$capture.interpolationLeadViolations -ne 0) {
            throw (
                "Frame pacing run at $fps fps drew a creature in a tile it had not moved to " +
                "$($capture.interpolationLeadViolations) time(s). Interpolation must lag the " +
                "simulation, never lead it."
            )
        }

        if ([int]$capture.interpolatedFrames -le 0) {
            throw (
                "Frame pacing run at $fps fps never interpolated a frame, so it proves nothing " +
                "about motion. Check that the presentation layer still lerps between ticks."
            )
        }

        if ([double]$capture.maxRenderStepPixels -ge [double]$capture.tileSize) {
            throw (
                "Frame pacing run at $fps fps moved a creature $($capture.maxRenderStepPixels) px " +
                "in one frame, which is a whole $($capture.tileSize) px tile. Movement is still " +
                "teleporting between ticks."
            )
        }

        $runs += [pscustomobject]@{
            FixedFps            = $fps
            Frames              = [long]$capture.frames
            InterpolatedFrames  = [long]$capture.interpolatedFrames
            Checksum            = [string]$capture.checksum
            MaxRenderStepPixels = [double]$capture.maxRenderStepPixels
        }
    }

    $checksums = @($runs | ForEach-Object { $_.Checksum } | Sort-Object -Unique)
    if ($checksums.Count -ne 1) {
        throw (
            "Canonical state depends on the frame rate: " +
            (($runs | ForEach-Object { "$($_.FixedFps) fps -> $($_.Checksum)" }) -join ", ") + "."
        )
    }

    $frameCounts = @($runs | ForEach-Object { $_.Frames } | Sort-Object -Unique)
    if ($frameCounts.Count -lt $runs.Count) {
        throw (
            "The frame pacing runs produced the same number of frames, so they did not actually " +
            "differ in frame rate and the comparison proves nothing."
        )
    }

    $summary = [ordered]@{
        event               = "frame_pacing"
        status              = "ok"
        targetTick          = $TargetTick
        checksum            = $checksums[0]
        frames              = @($runs | ForEach-Object { $_.Frames })
        fixedFps            = @($runs | ForEach-Object { $_.FixedFps })
        interpolatedFrames  = @($runs | ForEach-Object { $_.InterpolatedFrames })
        maxRenderStepPixels = @($runs | ForEach-Object { $_.MaxRenderStepPixels })
    }

    Write-VerifyDiagnostic -Text ($summary | ConvertTo-Json -Compress)
    return $summary
}
