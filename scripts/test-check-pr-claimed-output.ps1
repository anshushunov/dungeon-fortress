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
        [string]$BodyText,

        [string]$RepoRootOverride
    )

    $bodyFile = Join-Path $testRoot ("body-" + [Guid]::NewGuid().ToString("N") + ".md")
    [IO.File]::WriteAllText($bodyFile, $BodyText, $utf8)
    $useRoot = if ($RepoRootOverride) { $RepoRootOverride } else { $testRoot }
    $output = @(& powershell `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $scriptPath `
        -BodyFile $bodyFile `
        -RepoRoot $useRoot 2>&1)
    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    }
}

function Assert-JsonLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Output,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedStatus
    )

    if ($Output -notmatch ('"status":"' + $ExpectedStatus + '"')) {
        throw "Expected status '$ExpectedStatus' in output: $Output"
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

    $probeCyrillic = Join-Path $testRoot "probe-cyrillic.ps1"
    [IO.File]::WriteAllText($probeCyrillic, @'
$s = -join @([char]0x0411, [char]0x043b, [char]0x043e, [char]0x043a)
$bytes = [Text.Encoding]::UTF8.GetBytes($s)
[Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)
'@, $utf8)

    $cyrillicMatchBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe-cyrillic.ps1
```
Expected: Блок
'@
    $result = Invoke-ClaimChecker -BodyText $cyrillicMatchBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"match"') {
        throw "Cyrillic claim present in output did not match. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $cyrillicMismatchBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe-cyrillic.ps1
```
Expected: Отсутствует
'@
    $result = Invoke-ClaimChecker -BodyText $cyrillicMismatchBody
    if ($result.ExitCode -ne 1 -or $result.Output -notmatch '"status":"mismatch"') {
        throw "Cyrillic claim absent from output did not mismatch. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $noLanguageBody = @'
```
git log --oneline -1
```
Expected: anything
'@
    $result = Invoke-ClaimChecker -BodyText $noLanguageBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'fence language is missing') {
        throw "Language-less fenced block was not reported as not-runnable. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $multiBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 42

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 42
'@
    $result = Invoke-ClaimChecker -BodyText $multiBody
    $matchCount = ([regex]::Matches($result.Output, '"status":"match"')).Count
    if ($result.ExitCode -ne 0 -or $matchCount -ne 2) {
        throw "Multiple blocks did not produce two matches. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $inlineCommandBody = @'
```powershell
powershell -NoProfile -Command "Write-Host 'probe.ps1'"
```
Expected: probe.ps1
'@
    $result = Invoke-ClaimChecker -BodyText $inlineCommandBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'inline execution switch') {
        throw "Inline -Command execution was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $removeAliasBody = @'
```powershell
rm .\probe.ps1
```
Expected: removed
'@
    $result = Invoke-ClaimChecker -BodyText $removeAliasBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'not on the allow-list') {
        throw "Alias command was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
    }
    if (-not (Test-Path -LiteralPath $probeScript)) {
        throw "Alias command mutated the fixture file."
    }

    $reflectionBody = @'
```powershell
[Environment]::SetEnvironmentVariable("PR_CLAIM_TEST", "1", "User")
```
Expected: 1
'@
    $result = Invoke-ClaimChecker -BodyText $reflectionBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'not on the allow-list') {
        throw ".NET reflection command was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
    }

    $restMethodBody = @'
```powershell
powershell -NoProfile -Command "Invoke-RestMethod http://127.0.0.1:1/unreachable"
```
Expected: ok
'@
    $result = Invoke-ClaimChecker -BodyText $restMethodBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'inline execution switch') {
        throw "Network command was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
    }

    if (Get-Command git -ErrorAction SilentlyContinue) {
        $gitInit = @(& git init -q $testRoot 2>&1)
        $guardBranch = "protect-claim-test"
        [void](& git -C $testRoot symbolic-ref HEAD "refs/heads/main" 2>$null)
        [void](& git -C $testRoot commit --allow-empty -q -m init 2>$null)
        [void](& git -C $testRoot branch $guardBranch 2>&1)
        $gitDeleteBody = @'
```powershell
git branch -D protect-claim-test
```
Expected: deleted
'@
        $result = Invoke-ClaimChecker -BodyText $gitDeleteBody -RepoRootOverride $testRoot
        if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
            $result.Output -notmatch 'not on the read-only allow-list') {
            throw "Mutating git branch -D was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
        }
        $branches = @(& git -C $testRoot branch --format="%(refname:short)")
        if ($branches -notcontains $guardBranch) {
            throw "git branch -D was executed despite the allow-list: branch '$guardBranch' is gone."
        }
    }

    $crashBody = @'
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\missing-script-xyz.ps1
```
Expected: 1

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\probe.ps1
```
Expected: 42
'@
    $result = Invoke-ClaimChecker -BodyText $crashBody
    $lineCount = ([regex]::Matches($result.Output, '"claimedFrom"')).Count
    if ($result.ExitCode -ne 0 -or $lineCount -ne 2 -or
        $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch '"status":"match"') {
        throw "Crash on first pair leaked into the second. exit=$($result.ExitCode) lines=$lineCount output=$($result.Output)"
    }
    if ($result.Output -notmatch 'command exited with code') {
        throw "Failing command was not reported as not-runnable with a reason. output=$($result.Output)"
    }

    $encodedCommandBody = @'
```powershell
powershell -EncodedCommand RQB4AGkAdAA=
```
Expected: 0
'@
    $result = Invoke-ClaimChecker -BodyText $encodedCommandBody
    if ($result.ExitCode -ne 0 -or $result.Output -notmatch '"status":"not-runnable"' -or
        $result.Output -notmatch 'inline execution switch') {
        throw "EncodedCommand was not rejected. exit=$($result.ExitCode) output=$($result.Output)"
    }

    [ordered]@{
        event = "check_pr_claimed_output_test"
        status = "ok"
        literalMatch = $true
        mismatchFlagged = $true
        missingClaimReported = $true
        unterminatedFenceReported = $true
        russianClaimPrefixAccepted = $true
        cyrillicClaimMatch = $true
        cyrillicClaimMismatch = $true
        noLanguageFenceReported = $true
        multipleBlocksReported = $true
        inlineCommandRejected = $true
        aliasRejected = $true
        reflectionRejected = $true
        networkRejected = $true
        gitMutateRejected = $true
        crashIsolatedPerPair = $true
        encodedCommandRejected = $true
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
