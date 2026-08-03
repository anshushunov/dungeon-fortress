[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-pr-claimed-output.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-pr-claimed-output-test-" + [Guid]::NewGuid().ToString("N"))
$utf8 = [Text.UTF8Encoding]::new($false)

function Invoke-ClaimChecker {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BodyText
    )

    $bodyFile = Join-Path $testRoot ("body-" + [Guid]::NewGuid().ToString("N") + ".md")
    [IO.File]::WriteAllText($bodyFile, $BodyText, $utf8)
    $output = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -BodyFile $bodyFile `
        -RepoRoot $testRoot 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    $probeScript = Join-Path $testRoot "probe.ps1"
    [IO.File]::WriteAllText($probeScript, 'Write-Host "actual count: 42"', $utf8)

    $matchBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 42
'@
    $result = Invoke-ClaimChecker -BodyText $matchBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"match"') {
        throw "Expected literal claim to match. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $mismatchBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 99
'@
    $result = Invoke-ClaimChecker -BodyText $mismatchBody
    if ($result.ExitCode -ne 1 -or $result.Output -notmatch '"status":"mismatch"') {
        throw "Expected wrong literal claim to mismatch. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $missingClaimBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
'@
    $result = Invoke-ClaimChecker -BodyText $missingClaimBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'missing Expected') {
        throw "Missing claim was not reported as not-runnable. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $rejectedBody = @'
```powershell
Remove-Item .\probe.ps1
```
Expected: removed
'@
    $result = Invoke-ClaimChecker -BodyText $rejectedBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'not allowed') {
        throw "Rejected command was not reported as not-runnable. exit=$($result.ExitCode) output=$($result.Output)"
    }
    if (-not (Test-Path -LiteralPath $probeScript)) {
        throw "Rejected command mutated the fixture file."
    }

    $unterminatedFenceBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
'@
    $result = Invoke-ClaimChecker -BodyText $unterminatedFenceBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'unterminated fenced command block') {
        throw "Unterminated fence was silently skipped. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $russianBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Заявлено: actual count
'@
    $result = Invoke-ClaimChecker -BodyText $russianBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"match"') {
        throw "Russian claim prefix did not match. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $multiBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 42

```powershell
powershell -NoProfile -Command "Write-Host 'probe.ps1'"
```
Expected: probe.ps1
'@
    $result = Invoke-ClaimChecker -BodyText $multiBody
    $matchCount = ([regex]::Matches($result.Output, '"status":"match"')).Count
    if ($result.ExitCode -ne 0 -or $matchCount -ne 2) {
        throw "Multiple blocks did not produce two matches. exit=$($result.ExitCode) output=$($result.Output)"
    }

    [ordered]@{
        event = "check_pr_claimed_output_test"
        status = "ok"
        literalMatch = $true
        mismatchFlagged = $true
        missingClaimReported = $true
        unsafeCommandRejected = $true
        unterminatedFenceReported = $true
        russianClaimPrefixAccepted = $true
        multipleBlocksReported = $true
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
