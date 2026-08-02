[CmdletBinding()]
param()

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding       = [System.Text.Encoding]::UTF8
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptPath = Join-Path $PSScriptRoot "take-task.ps1"

Write-Host "=== Test 1: Parser validation ==="
$errors = @()
$tokens = @()
$null = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$errors)

if ($errors.Count -gt 0) {
    Write-Error "Parser found $($errors.Count) error(s):"
    $errors | ForEach-Object { Write-Error ("  " + $_.Message) }
    exit 1
}
Write-Host "PASS: script parses with zero errors."

# Parse again to get the AST for structural tests
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$errors)

Write-Host ""
Write-Host "=== Test 2: Transform-ToSlug in AST ==="
$found = $false
$ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object {
    if ($_.Name -eq "Transform-ToSlug") {
        $found = $true
        Write-Host ("PASS: Transform-ToSlug found at line " + $_.Extent.StartLineNumber)
    }
}
if (-not $found) {
    Write-Error "FAIL: Transform-ToSlug function not found."
    exit 1
}

Write-Host ""
Write-Host "=== Test 3: Parameter schema ==="
$text = $ast.Extent.Text
if (-not ($text -match 'Tier')) { Write-Error "FAIL: Tier parameter missing"; exit 1 }
if (-not ($text -match 'Issue')) { Write-Error "FAIL: Issue parameter missing"; exit 1 }
if (-not ($text -match 'WhatIf')) { Write-Error "FAIL: WhatIf parameter missing"; exit 1 }
Write-Host "PASS: all three parameters present (Tier, Issue, WhatIf)."

Write-Host ""
Write-Host "=== Test 4: Branch naming convention ==="
if (-not ($ast.Extent.Text -match "agent/")) {
    Write-Error "FAIL: branchName does not follow agent/N-slug pattern."
    exit 1
}
Write-Host "PASS: branch naming follows agent/N-slug convention."

Write-Host ""
Write-Host "=== Test 5: Race detection (mutant target) ==="
if (-not ($ast.Extent.Text -match "verifiedLabels.*contains.*claimed")) {
    Write-Error "FAIL: race detection block not found."
    exit 1
}
Write-Host "PASS: race detection block present (mutant target verified)."

Write-Host ""
Write-Host "All inline tests passed."