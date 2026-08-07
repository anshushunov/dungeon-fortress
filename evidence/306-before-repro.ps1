[CmdletBinding()]
param(
    [string]$GodotPath = "C:/gamedev/Godot_v4.7.1-stable_mono_win64/Godot_v4.7.1-stable_mono_win64_console.exe"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\scripts\GodotTools.ps1")
. (Join-Path $PSScriptRoot "..\scripts\TemporaryRoot.ps1")

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$solutionPath = Join-Path $repoRoot "DungeonFortress.sln"
$domainMcpTestProject = Join-Path $repoRoot "tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj"

$env:DOTNET_CLI_HOME = Join-Path $artifactsRoot "dotnet-home"
$env:DOTNET_NOLOGO = "1"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = "0"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME | Out-Null

$temporaryRootSelection = Initialize-VerificationTemporaryRoot -RepositoryRoot $repoRoot -OwnDirectorySuffix "306before"
$temporaryRootPath = $temporaryRootSelection.Path
Write-Host "temporaryRoot: $temporaryRootPath (owned=$($temporaryRootSelection.Owned))"

$godot = Resolve-GodotExecutable -ExplicitPath $GodotPath
$godotVersion = Assert-GodotVersion -GodotPath $godot
$godotNuGetSource = Get-GodotNuGetSource -GodotPath $godot
Initialize-GodotNuGetEnvironment -ProfileRoot (Join-Path $artifactsRoot "tool-profile") -GodotNuGetSource $godotNuGetSource
Write-Host "godotVersion: $godotVersion"

$baselineProcesses = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' or Name='VBCSCompiler.exe' or Name='MSBuild.exe'" |
    Select-Object -ExpandProperty ProcessId)
Write-Host ("baseline dotnet/VBCSCompiler/MSBuild PIDs: {0}" -f ($baselineProcesses -join ","))

Write-Host "--- dotnet restore (solution) ---"
& dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) { throw "restore failed with $LASTEXITCODE" }

Write-Host "--- dotnet restore (domain mcp tests, locked-mode) ---"
& dotnet restore $domainMcpTestProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw "domain mcp restore failed with $LASTEXITCODE" }

Write-Host "--- dotnet build (solution, Release) ---"
& dotnet build $solutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw "build failed with $LASTEXITCODE" }

Write-Host "--- post-build process snapshot ---"
$afterProcesses = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' or Name='VBCSCompiler.exe' or Name='MSBuild.exe'")
$newProcesses = @($afterProcesses | Where-Object { $_.ProcessId -notin $baselineProcesses })
foreach ($p in $newProcesses) {
    Write-Host ("NEW PID={0} Name={1} Created={2}" -f $p.ProcessId, $p.Name, $p.CreationDate)
    Write-Host ("  CommandLine: {0}" -f $p.CommandLine)
}
if ($newProcesses.Count -eq 0) {
    Write-Host "No new dotnet/VBCSCompiler/MSBuild processes survived the build."
}

Write-Host "--- attempting cleanup of temporary root ---"
try {
    Remove-Item -LiteralPath $temporaryRootPath -Recurse -Force -ErrorAction Stop
    Write-Host "CLEANUP SUCCEEDED (no lock encountered this run)."
}
catch {
    Write-Host "CLEANUP FAILED:"
    Write-Host $_.Exception.Message

    Write-Host "--- identifying holding process by loaded module match ---"
    $remainingFiles = @()
    if (Test-Path -LiteralPath $temporaryRootPath) {
        $remainingFiles = @(Get-ChildItem -LiteralPath $temporaryRootPath -Recurse -File -ErrorAction SilentlyContinue |
            Select-Object -ExpandProperty FullName)
    }
    Write-Host ("remaining file count: {0}" -f $remainingFiles.Count)

    $candidates = @(Get-Process -Name dotnet, VBCSCompiler, MSBuild -ErrorAction SilentlyContinue)
    foreach ($proc in $candidates) {
        try {
            $matchingModules = @($proc.Modules | Where-Object {
                $remainingFiles -contains $_.FileName
            })
        }
        catch {
            $matchingModules = @()
        }
        if ($matchingModules.Count -gt 0) {
            Write-Host ("HOLDER: PID={0} Name={1} StartTime={2}" -f $proc.Id, $proc.ProcessName, $proc.StartTime)
            foreach ($m in $matchingModules) {
                Write-Host ("  locked module: {0}" -f $m.FileName)
            }
        }
    }
}

Write-Host "--- final state ---"
if (Test-Path -LiteralPath $temporaryRootPath) {
    Write-Host "temporaryRoot STILL EXISTS: $temporaryRootPath"
    Get-ChildItem -LiteralPath $temporaryRootPath -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
}
else {
    Write-Host "temporaryRoot fully removed."
}
