[CmdletBinding()]
param(
    [string]$OutputPath,
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$defaultOutputPath = Join-Path $repoRoot 'docs\playtests\data\prototype-01-agent-batch.json'
$expectedOutputPath = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $defaultOutputPath
} else {
    [IO.Path]::GetFullPath($OutputPath)
}
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('dungeon-fortress-evaluation-' + [Guid]::NewGuid().ToString('N'))
$scenarioProject = Join-Path $repoRoot 'tests\DungeonFortress.Scenarios\DungeonFortress.Scenarios.csproj'
$scenarioAssembly = Join-Path $repoRoot 'tests\DungeonFortress.Scenarios\bin\Release\net8.0\DungeonFortress.Scenarios.dll'

$env:DOTNET_CLI_HOME = Join-Path $runRoot 'dotnet-home'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

New-Item -ItemType Directory -Force -Path $runRoot, $env:DOTNET_CLI_HOME | Out-Null
try {
    & dotnet build $scenarioProject --configuration Release
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $scenarioAssembly -PathType Leaf)) {
        throw 'Could not build the deterministic scenario runner.'
    }

    $arguments = @(
        $scenarioAssembly,
        '--evaluate-prototype',
        '--repository-root', $repoRoot,
        '--output', $expectedOutputPath
    )
    if ($Verify) { $arguments += '--verify' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Prototype evaluation runner failed with exit code $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
