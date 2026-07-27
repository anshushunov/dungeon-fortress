[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "GodotTools.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$expected = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "visual\baseline.png"))
$actual = Resolve-RepositoryArtifactPath `
    -RepositoryRoot $repoRoot `
    -RelativePath "visual\baseline.png"

if ($actual -ne $expected) {
    throw "Valid nested artifact path resolved to '$actual', expected '$expected'."
}

function Assert-RejectedScreenshotPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        $null = Resolve-RepositoryArtifactPath -RepositoryRoot $repoRoot -RelativePath $Path
    }
    catch {
        return
    }

    throw "Screenshot path '$Path' was not rejected."
}

Assert-RejectedScreenshotPath -Path "C:\outside.png"
Assert-RejectedScreenshotPath -Path "..\outside.png"

[ordered]@{
    event = "screenshot_output_path_test"
    status = "ok"
    validPath = $actual
    rootedRejected = $true
    traversalRejected = $true
} | ConvertTo-Json -Compress | Write-Host
