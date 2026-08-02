[CmdletBinding()]
param(
    [string]$RepoRoot,

    [switch]$IncludeDocs,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $RepoRoot) {
    $RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$evidenceRoot = Join-Path $RepoRoot "evidence"
$sha256Regex = "^[0-9a-f]{64}$"

$script:claims = [System.Collections.Generic.List[object]]::new()
$script:parseErrors = [System.Collections.Generic.List[object]]::new()

function Get-BlobHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $tempFile = Join-Path ([IO.Path]::GetTempPath()) ("df-sha-blob-" + [Guid]::NewGuid().ToString("N") + ".bin")
    try {
        & cmd /c "git -C `"$RepoRoot`" cat-file blob `"HEAD:$RelativePath`" > `"$tempFile`" 2>nul" | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $tempFile)) {
            return $null
        }
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $tempFile).Hash.ToLowerInvariant()
    }
    finally {
        if (Test-Path -LiteralPath $tempFile) {
            Remove-Item -LiteralPath $tempFile -Force
        }
    }
}

function Test-TrackedInGit {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $true)]
        [string]$RelativePath
    )

    $trackedOutput = & git -C $RepoRoot ls-files --cached -- "$RelativePath" 2>$null
    if ($null -eq $trackedOutput) {
        return $false
    }
    $trackedList = @($trackedOutput | ForEach-Object { [string]$_ })
    return ($trackedList.Count -gt 0)
}

function Get-WorkingHash {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function Add-Claim {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ClaimedFrom,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ClaimedSha256
    )

    $resolved = Join-Path $RepoRoot $Path
    $relative = $Path.Replace("/", [IO.Path]::DirectorySeparatorChar)
    $tracked = Test-TrackedInGit -RepoRoot $RepoRoot -RelativePath $relative
    $workingHash = $null
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        $workingHash = Get-WorkingHash -Path $resolved
    }
    $blobHash = $null
    if ($tracked) {
        $blobHash = Get-BlobHash -RepoRoot $RepoRoot -RelativePath $relative
    }

    $status = if ($tracked -and $ClaimedSha256 -eq $blobHash) {
        "blob-match"
    }
    elseif ($tracked -and $ClaimedSha256 -eq $workingHash) {
        "working-copy-only"
    }
    elseif ($tracked) {
        "mismatch"
    }
    elseif ($ClaimedSha256 -eq $workingHash) {
        "untracked-working-match"
    }
    else {
        "untracked"
    }

    $script:claims.Add([pscustomobject]@{
        claimedFrom = $ClaimedFrom
        path = $Path
        claimedSha256 = $ClaimedSha256
        blobSha256 = $blobHash
        workingSha256 = $workingHash
        status = $status
    })
}

function Add-EvidenceClaims {
    $jsonFiles = Get-ChildItem -LiteralPath $evidenceRoot -Filter "*.json" -File -ErrorAction SilentlyContinue
    foreach ($file in $jsonFiles) {
        $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
        # A malformed evidence file is a finding to report, not a reason to die
        # mid-scan with a stack trace and leave the remaining files unchecked.
        # It still fails the run: see the exit condition below.
        $node = $null
        try {
            $node = $text | ConvertFrom-Json
        }
        catch {
            $script:parseErrors.Add([pscustomobject]@{
                claimedFrom = "evidence/" + $file.Name
                error = $_.Exception.Message
            })
            continue
        }

        $stack = [System.Collections.Generic.Stack[object]]::new()
        $stack.Push($node)
        while ($stack.Count -gt 0) {
            $current = $stack.Pop()
            if ($null -eq $current) {
                continue
            }
            if ($current -is [System.Management.Automation.PSCustomObject]) {
                $shaKeys = @($current.PSObject.Properties |
                    Where-Object { $_.Name -match "Sha256$" -and $_.Value -is [string] -and $_.Value -match $sha256Regex })
                $pathKeys = @($current.PSObject.Properties |
                    Where-Object { $_.Name -match "(Path|path)$" -and $_.Value -is [string] })
                if ($shaKeys.Count -gt 0 -and $pathKeys.Count -gt 0) {
                    foreach ($shaProp in $shaKeys) {
                        Add-Claim `
                            -ClaimedFrom ("evidence/" + $file.Name + " :: " + $shaProp.Name) `
                            -Path $pathKeys[0].Value `
                            -ClaimedSha256 $shaProp.Value
                    }
                }
                foreach ($p in $current.PSObject.Properties) {
                    if ($p.Value -is [System.Management.Automation.PSCustomObject] -or
                        $p.Value -is [System.Collections.IEnumerable]) {
                        $stack.Push($p.Value)
                    }
                }
            }
            elseif ($current -is [System.Collections.IEnumerable] -and -not ($current -is [string])) {
                foreach ($item in $current) {
                    $stack.Push($item)
                }
            }
        }
    }
}

function Add-DocClaims {
    if (-not $IncludeDocs) {
        return
    }
    $mdFiles = @(
        Get-ChildItem -LiteralPath (Join-Path $RepoRoot "docs\art") -Filter "*.md" -File -ErrorAction SilentlyContinue
    )
    foreach ($file in $mdFiles) {
        $lines = [IO.File]::ReadAllLines($file.FullName, [Text.Encoding]::UTF8)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = $lines[$i]
            $hashMatch = [regex]::Match($line, "\b[0-9a-f]{64}\b")
            if (-not $hashMatch.Success) {
                continue
            }
            $pathMatch = [regex]::Match($line, '`([A-Za-z0-9_./\\-]+\.[A-Za-z0-9]{1,5})`')
            if (-not $pathMatch.Success) {
                continue
            }
            $claimSource = $file.FullName.Replace($RepoRoot, "")
            $claimSource = $claimSource.TrimStart([char]92, [char]47)
            Add-Claim `
                -ClaimedFrom ($claimSource + ":" + ($i + 1)) `
                -Path $pathMatch.Groups[1].Value `
                -ClaimedSha256 $hashMatch.Value
        }
    }
}

Add-EvidenceClaims
Add-DocClaims

if (-not $Quiet) {
    foreach ($e in $script:parseErrors) {
        [ordered]@{
            claimedFrom = $e.claimedFrom
            status = "unparsed"
            error = $e.error
        } | ConvertTo-Json -Compress | Write-Host
    }
    foreach ($c in $script:claims) {
        $line = [ordered]@{
            claimedFrom = $c.claimedFrom
            path = $c.path
            claimedSha256 = $c.claimedSha256
            status = $c.status
            blobSha256 = $c.blobSha256
            workingSha256 = $c.workingSha256
        } | ConvertTo-Json -Compress
        Write-Host $line
    }
}

$mismatches = @($script:claims | Where-Object { $_.status -eq "mismatch" })
$workingOnly = @($script:claims | Where-Object { $_.status -eq "working-copy-only" })
$blobMatches = @($script:claims | Where-Object { $_.status -eq "blob-match" })
$untracked = @($script:claims | Where-Object { $_.status -in @("untracked", "untracked-working-match") })

if ($mismatches.Count -gt 0 -or $workingOnly.Count -gt 0 -or $script:parseErrors.Count -gt 0) {
    if (-not $Quiet) {
        Write-Host ""
        Write-Host ("MISMATCH: {0}, working-copy-only (CRLF trap): {1}, unparsed evidence files: {2}" -f `
            $mismatches.Count, $workingOnly.Count, $script:parseErrors.Count)
    }
    exit 1
}

if (-not $Quiet) {
    Write-Host ("OK: {0} blob-match, {1} untracked/working-copy, {2} claims total" -f `
        $blobMatches.Count, $untracked.Count, $script:claims.Count)
}
exit 0
