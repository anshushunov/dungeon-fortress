$targetPids = @(144688,78008,97660,94468,97696,82992,92352,136732)
foreach ($id in $targetPids) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($null -eq $p) {
        Write-Host ("PID {0}: gone" -f $id)
        continue
    }
    Write-Host ("PID {0}: alive, StartTime={1}" -f $id, $p.StartTime)
    $modules = @($p.Modules | Where-Object { $_.FileName -match "df-verify-306before|dotnet-home|SourceGeneration" })
    foreach ($m in $modules) {
        Write-Host ("  module: {0}" -f $m.FileName)
    }
}
