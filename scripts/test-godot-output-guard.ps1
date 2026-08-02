Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$cleanOutput = @(
    "Godot Engine v4.7.1.stable.mono.official.a13da4feb",
    '{"event":"godot_visible_smoke","status":"ok"}',
    "WARNING: This line is not an engine error."
)
$cleanErrors = @(Get-GodotErrorLines -OutputLines $cleanOutput)
if ($cleanErrors.Count -ne 0) {
    throw "The Godot output guard rejected clean output."
}

$errorOutput = @(
    'ERROR: Condition "err != OK" is true.',
    "Godot_console.exe : ERROR: Failed to initialize renderer.",
    "SCRIPT ERROR: Invalid call."
)
$detectedErrors = @(Get-GodotErrorLines -OutputLines $errorOutput)
if ($detectedErrors.Count -ne $errorOutput.Count) {
    throw "The Godot output guard did not detect every ERROR signature."
}

$powershellPath = (Get-Command "powershell" -CommandType Application).Source
$exitZeroErrorRejected = $false
try {
    Invoke-GodotChecked `
        -GodotPath $powershellPath `
        -Arguments @(
            "-NoProfile",
            "-Command",
            '[Console]::Error.WriteLine("ERROR: synthetic engine failure"); exit 0'
        ) 6>$null | Out-Null
}
catch {
    if ($_.Exception.Message -match "unexpected ERROR") {
        $exitZeroErrorRejected = $true
    }
    else {
        throw
    }
}

if (-not $exitZeroErrorRejected) {
    throw "The Godot output guard accepted ERROR output with exit code 0."
}

$sameValue = Assert-SameNonEmptyValue `
    -Values @("checksum-a", "checksum-a") `
    -Description "Synthetic checksum"
if ($sameValue -ne "checksum-a") {
    throw "The non-empty equality guard changed the accepted value."
}

$emptyRejected = $false
try {
    Assert-SameNonEmptyValue `
        -Values @("", "") `
        -Description "Synthetic checksum" | Out-Null
}
catch {
    if ($_.Exception.Message -match "non-empty") {
        $emptyRejected = $true
    }
    else {
        throw
    }
}
if (-not $emptyRejected) {
    throw "The checksum guard accepted two empty values as invariant."
}

$mismatchRejected = $false
try {
    Assert-SameNonEmptyValue `
        -Values @("checksum-a", "checksum-b") `
        -Description "Synthetic checksum" | Out-Null
}
catch {
    if ($_.Exception.Message -match "differs") {
        $mismatchRejected = $true
    }
    else {
        throw
    }
}
if (-not $mismatchRejected) {
    throw "The checksum guard accepted different values as invariant."
}

# --- Issue #184: the Godot runtime profile and the GLES3 shader cache ---------
#
# Two things are held here, and they are independent. One is the arithmetic of
# the path budget, which is the measurement from evidence/184-cause.json turned
# into an assertion. The other is that every entry point which starts the engine
# picks its temporary directory the same way, which is what stopped the
# screenshots stage and its own control experiment from landing in two different
# profiles.

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))

# The staircase was measured against this exact user:// directory name, so a
# rename has to be loud rather than silently shift every number below.
$measuredUserDirectoryName = "Dungeon Fortress " + [char]0x2014 + " deterministic spike"
$declaredUserDirectoryName = Get-GodotUserDirectoryName -ProjectFile (
    Join-Path $repositoryRoot "src\DungeonFortress.Game\project.godot")
if ($declaredUserDirectoryName -ne $measuredUserDirectoryName) {
    throw (
        "The game project now declares config/name '$declaredUserDirectoryName', " +
        "but the shader cache path budget below was measured against " +
        "'$measuredUserDirectoryName'. Re-measure evidence/184-cause.json before " +
        "changing this expectation.")
}

function New-ProfileRootOfLength {
    param([int]$Length)

    # An APPDATA root of an exact length, so the assertions can be written in
    # the same units the measurement used.
    $prefix = "C:\p\"
    $suffix = "\Roaming"
    $fillerLength = $Length - $prefix.Length - $suffix.Length
    if ($fillerLength -lt 1) {
        throw "Cannot build a profile root of $Length characters."
    }
    return $prefix + ("x" * $fillerLength) + $suffix
}

# Six arms, six APPDATA lengths, six counts. Measured with one real capture per
# arm against a purged profile in evidence/184-cause.json: a warm cache emitted
# exactly this many `shader_gles3.cpp` lines and a cold one emitted none.
$measuredStaircase = @(
    [pscustomobject]@{ AppDataLength = 90; UnenterableShaderClasses = 0 },
    [pscustomobject]@{ AppDataLength = 92; UnenterableShaderClasses = 1 },
    [pscustomobject]@{ AppDataLength = 94; UnenterableShaderClasses = 3 },
    [pscustomobject]@{ AppDataLength = 99; UnenterableShaderClasses = 6 },
    [pscustomobject]@{ AppDataLength = 105; UnenterableShaderClasses = 14 },
    [pscustomobject]@{ AppDataLength = 110; UnenterableShaderClasses = 14 }
)

$staircaseChecked = 0
foreach ($step in $measuredStaircase) {
    $profileRoot = New-ProfileRootOfLength -Length $step.AppDataLength
    $paths = @(Get-GodotShaderCachePaths `
        -AppDataRoot $profileRoot `
        -UserDirectoryName $measuredUserDirectoryName)
    if ($paths.Count -ne 14) {
        throw "The shader class list no longer has fourteen entries; it has $($paths.Count)."
    }
    $over = @($paths | Where-Object { $_.Length -gt 254 }).Count
    if ($over -ne $step.UnenterableShaderClasses) {
        throw (
            "At APPDATA length $($step.AppDataLength) the budget says $over shader " +
            "classes are unenterable; the machine measured " +
            "$($step.UnenterableShaderClasses) engine error lines.")
    }

    if ($step.UnenterableShaderClasses -eq 0) {
        $measurement = Assert-GodotShaderCachePathFits `
            -AppDataRoot $profileRoot `
            -UserDirectoryName $measuredUserDirectoryName
        if ($measurement.UnenterableShaderClassCount -ne 0 -or
            $measurement.Headroom -lt 0) {
            throw "A profile the machine ran silently was refused by the budget."
        }
    }
    else {
        $refused = $false
        try {
            Assert-GodotShaderCachePathFits `
                -AppDataRoot $profileRoot `
                -UserDirectoryName $measuredUserDirectoryName | Out-Null
        }
        catch {
            if ($_.Exception.Message -match "shader classes are over the limit" -and
                $_.Exception.Message -match "shader_gles3\.cpp:802" -and
                $_.Exception.Message -match "DUNGEON_FORTRESS_TEMP") {
                $refused = $true
            }
            else {
                throw
            }
        }
        if (-not $refused) {
            throw (
                "A profile that made the engine print " +
                "$($step.UnenterableShaderClasses) shader cache errors was accepted, " +
                "or was refused without naming the count, the engine line and the fix.")
        }
    }

    $staircaseChecked++
}

# The boundary itself, in the units the failure has: 254 characters is the last
# path this engine can still enter, 255 is the first it cannot.
$boundary = @()
foreach ($longestPathLength in @(254, 255)) {
    $probeRoot = New-ProfileRootOfLength -Length 90
    $paths = @(Get-GodotShaderCachePaths `
        -AppDataRoot $probeRoot `
        -UserDirectoryName $measuredUserDirectoryName)
    $longest = @($paths | Sort-Object -Property Length -Descending)[0]
    $shift = $longestPathLength - $longest.Length
    $shiftedRoot = New-ProfileRootOfLength -Length (90 + $shift)
    $shiftedPaths = @(Get-GodotShaderCachePaths `
        -AppDataRoot $shiftedRoot `
        -UserDirectoryName $measuredUserDirectoryName)
    $shiftedLongest = @($shiftedPaths | Sort-Object -Property Length -Descending)[0]
    if ($shiftedLongest.Length -ne $longestPathLength) {
        throw "Failed to build a profile whose longest shader cache path is $longestPathLength."
    }
    $accepted = $true
    try {
        Assert-GodotShaderCachePathFits `
            -AppDataRoot $shiftedRoot `
            -UserDirectoryName $measuredUserDirectoryName | Out-Null
    }
    catch {
        $accepted = $false
    }
    $boundary += [pscustomobject]@{
        LongestPathLength = $longestPathLength
        Accepted = $accepted
    }
}
if ($boundary[0].Accepted -ne $true -or $boundary[1].Accepted -ne $false) {
    throw (
        "The shader cache path budget no longer flips between 254 and 255 " +
        "characters: 254 accepted=$($boundary[0].Accepted), " +
        "255 accepted=$($boundary[1].Accepted).")
}

# The diagnosis is attached to the engine lines and to nothing else.
if ($null -ne (Get-GodotShaderCachePathDiagnosis -OutputLines $cleanOutput)) {
    throw "Clean engine output was given a shader cache diagnosis."
}
$shaderDiagnosis = Get-GodotShaderCachePathDiagnosis -OutputLines @(
    'ERROR: Condition "err != OK" is true.',
    "   at: initialize (drivers/gles3/shader_gles3.cpp:802)"
)
if ($null -eq $shaderDiagnosis -or
    $shaderDiagnosis["engineErrorClass"] -ne "gles3_shader_cache_path" -or
    $shaderDiagnosis["explanation"] -notmatch "SetCurrentDirectoryW" -or
    $shaderDiagnosis["explanation"] -notmatch "254") {
    throw (
        "The GLES3 shader cache lines are reported without naming what they mean, " +
        "which is the state Issue #184 was opened in.")
}

# Every entry point that starts the engine chooses its temporary directory the
# same way. Discovered by reading the scripts, not from a list: a new engine
# entry point is covered the day it is added.
$engineStartCommand = "Initialize-GodotRuntimeEnvironment"
$temporaryRootCommands = @(
    "Resolve-VerificationTemporaryRoot",
    "Initialize-VerificationTemporaryRoot"
)
$engineEntryPoints = @()
$orderCheckedEntryPoints = @()
foreach ($script in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1" -File)) {
    $tokens = $null
    $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        $script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        throw "Cannot read '$($script.Name)': $($parseErrors[0].Message)"
    }

    $commands = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst]
    }, $true))

    $startsEngine = @($commands | Where-Object {
        $_.GetCommandName() -eq $engineStartCommand
    }).Count -gt 0
    if (-not $startsEngine) {
        continue
    }

    $temporaryRootOffsets = @($commands | Where-Object {
        $_.GetCommandName() -in $temporaryRootCommands
    } | ForEach-Object { $_.Extent.StartOffset })

    if ($temporaryRootOffsets.Count -eq 0) {
        throw (
            "'$($script.Name)' starts the engine without choosing the temporary " +
            "directory the shared way. That is the divergence of Issue " +
            "#184: verify.ps1 honoured -TemporaryRoot and DUNGEON_FORTRESS_TEMP " +
            "and run-game.ps1 did not, so the same capture ran against two " +
            "different Godot runtime profiles and only one of them was over the " +
            "shader cache path limit. Call one of " +
            ($temporaryRootCommands -join " / ") + " before $engineStartCommand.")
    }

    # Order, not only presence. Both calls present in the wrong order is not a
    # near miss: Initialize-GodotRuntimeEnvironment builds the profile from
    # [IO.Path]::GetTempPath(), so a choice made after it is made too late and
    # the script silently goes back to ignoring DUNGEON_FORTRESS_TEMP - which is
    # Issue #184 word for word. Found by the independent review of PR #199: a
    # mutant that only swapped the two statements in run-game.ps1 survived the
    # earlier presence-only form of this check.
    #
    # verify.ps1 is the one exemption, and it is named rather than inferred. Its
    # engine start sits inside a memoised prerequisite defined above the
    # preflight, so textual order says nothing there; the execution order is
    # modelled instead by test-verify-stages.ps1, whose preflightSequence begins
    # with Initialize-VerificationTemporaryRoot. For the three linear scripts no
    # other check owns the question at all - `rg -n "run-game|update-golden|
    # goblin-sprite" scripts/test-verify-stages.ps1` finds one comment and
    # nothing else - so it is owned here.
    $orderedScripts = @()
    if ($script.Name -ne "verify.ps1") {
        $earliestEngineStart = (@($commands | Where-Object {
            $_.GetCommandName() -eq $engineStartCommand
        } | ForEach-Object { $_.Extent.StartOffset }) | Measure-Object -Minimum).Minimum
        $earliestTemporaryRoot = ($temporaryRootOffsets | Measure-Object -Minimum).Minimum

        if ($earliestTemporaryRoot -gt $earliestEngineStart) {
            throw (
                "'$($script.Name)' chooses the temporary directory only after it " +
                "starts the engine. $engineStartCommand builds the Godot runtime " +
                "profile from [IO.Path]::GetTempPath(), so a choice made later " +
                "never reaches the profile and the script is back to ignoring " +
                "-TemporaryRoot and DUNGEON_FORTRESS_TEMP - the divergence of " +
                "Issue #184 with both calls still present. Move the call to " +
                ($temporaryRootCommands -join " / ") + " above " +
                "$engineStartCommand.")
        }

        $orderedScripts += $script.Name
    }

    $engineEntryPoints += $script.Name
    $orderCheckedEntryPoints += @($orderedScripts)
}
if ($engineEntryPoints.Count -lt 4) {
    throw (
        "Only $($engineEntryPoints.Count) engine entry point(s) were found; the " +
        "check has stopped reaching the scripts it is meant to compare.")
}
# The exemption is one file, and it stays one file. Without this the order check
# could be emptied by exempting everything and would still report ok.
if ($orderCheckedEntryPoints.Count -ne ($engineEntryPoints.Count - 1)) {
    throw (
        "The order of the temporary directory and the engine start is checked " +
        "in $($orderCheckedEntryPoints.Count) of $($engineEntryPoints.Count) " +
        "entry points; every one but verify.ps1 has to be checked.")
}

[ordered]@{
    event = "godot_output_guard_test"
    status = "ok"
    cleanLines = $cleanOutput.Count
    detectedErrorLines = $detectedErrors.Count
    exitZeroErrorRejected = $exitZeroErrorRejected
    emptyInvariantRejected = $emptyRejected
    mismatchedInvariantRejected = $mismatchRejected
    shaderCacheStaircaseArms = $staircaseChecked
    shaderCacheBoundary = @($boundary | ForEach-Object {
        "$($_.LongestPathLength):$($_.Accepted)"
    })
    engineEntryPoints = @($engineEntryPoints)
    orderCheckedEntryPoints = @($orderCheckedEntryPoints)
} | ConvertTo-Json -Compress | Write-Host
