[CmdletBinding()]
param(
    [string]$Path,

    # Issue #357: DEBT_LEDGER.md carries two tables. Only "Записи" has the
    # 7-column format (Дата | Откуда | Что | Почему без последствия | Что
    # сделает её задачей | Срок с | Переформулировок) whose columns this
    # check protects. "Перечитывание и удалённые записи" has six columns by
    # construction (see DEBT_LEDGER.md non-goals) and is out of scope on
    # purpose - the section markers below narrow the scan to "Записи" only,
    # the same narrowing the Issue's own acceptance command uses.
    [string]$SectionStartPattern = '^## Записи',
    [string]$SectionEndPattern = '^## Перечитывание',
    [string]$RowPattern = '^\| 20[0-9][0-9]-',
    [int]$ExpectedFields = 9
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not $Path) {
    $repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $Path = Join-Path $repoRoot "docs\engineering\DEBT_LEDGER.md"
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "File not found: $Path"
    exit 2
}

# Splitting on a bare "|" mirrors the Issue #357 acceptance command
# (`awk -F'|'`) exactly, including its blindness to a `\|` markdown escape:
# a raw pipe byte is a delimiter to this check regardless of what precedes
# it, because that is what GitHub's own table renderer breaks on when a
# cell's content was not written to avoid the byte in the first place. The
# fix for an offending row is therefore to remove the extra raw "|" bytes
# from the cell text (rephrase, do not just prefix them with a backslash),
# not to make this check smarter than the renderer it is guarding.
$lines = Get-Content -LiteralPath $Path -Encoding UTF8

$inSection = $false
$rowsChecked = 0
$offenders = @()

for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]

    if ($line -match $SectionStartPattern) {
        $inSection = $true
    }
    if ($line -match $SectionEndPattern) {
        $inSection = $false
    }

    if ($inSection -and ($line -match $RowPattern)) {
        $rowsChecked++
        $fieldCount = ([regex]::Split($line, '\|')).Count
        if ($fieldCount -ne $ExpectedFields) {
            $offenders += [pscustomobject]@{
                line   = $i + 1
                fields = $fieldCount
            }
        }
    }
}

[ordered]@{
    event          = "check_ledger_table_columns"
    path           = $Path
    rowsChecked    = $rowsChecked
    expectedFields = $ExpectedFields
    offenderCount  = $offenders.Count
    offenders      = $offenders
} | ConvertTo-Json -Compress -Depth 4 | Write-Host

if ($offenders.Count -gt 0) {
    exit 1
}
exit 0
