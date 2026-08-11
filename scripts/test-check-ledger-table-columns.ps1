[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scriptPath = Join-Path $PSScriptRoot "check-ledger-table-columns.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot ".artifacts"))
$testRoot = Join-Path $artifactsRoot ("check-ledger-table-columns-test-" + [Guid]::NewGuid().ToString("N"))

function New-Fixture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ThirdRow
    )

    # Minimal fixture: a "Записи" table (7 columns, the shape this check
    # protects) followed by a "Перечитывание" table with six columns by
    # construction (DEBT_LEDGER.md non-goals) - it must never be flagged,
    # proving the section narrowing works and the check does not drift
    # into the second table even though its row count differs from 9. The
    # row under test is inserted INSIDE the "Записи" section, next to the
    # two known-good rows, not appended after the section closes.
    return @(
        "# Fixture"
        ""
        "## Записи"
        ""
        "| Дата | Откуда | Что | Почему без последствия | Что сделает её задачей | Срок с | Переформулировок |"
        "|---|---|---|---|---|---|---|"
        "| 2026-08-01 | review PR #1 | Первая находка, без символа pipe в ячейках | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
        "| 2026-08-02 | review PR #2 | Вторая находка, тоже без символа pipe | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
        $ThirdRow
        ""
        "## Перечитывание и удалённые записи"
        ""
        "| Дата | Прошло gate | Записей до | Повышено | Удалено | Осталось |"
        "|---|---|---|---|---|---|"
        "| 2026-08-01 | 0 | 1 | 0 | 0 | 1 |"
    )
}

New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
try {
    # --- Well-formed fixture (two clean rows, no third row) -> clean. ---
    # New-Fixture always inserts $ThirdRow as its own array element, and an
    # empty string would add a spurious blank line inside the table, so the
    # good fixture is built directly instead of through the helper.
    $goodLines = @(
        "# Fixture"
        ""
        "## Записи"
        ""
        "| Дата | Откуда | Что | Почему без последствия | Что сделает её задачей | Срок с | Переформулировок |"
        "|---|---|---|---|---|---|---|"
        "| 2026-08-01 | review PR #1 | Первая находка, без символа pipe в ячейках | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
        "| 2026-08-02 | review PR #2 | Вторая находка, тоже без символа pipe | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
        ""
        "## Перечитывание и удалённые записи"
        ""
        "| Дата | Прошло gate | Записей до | Повышено | Удалено | Осталось |"
        "|---|---|---|---|---|---|"
        "| 2026-08-01 | 0 | 1 | 0 | 0 | 1 |"
    )
    $fixturePath = Join-Path $testRoot "LEDGER.md"
    [IO.File]::WriteAllLines($fixturePath, $goodLines, [Text.UTF8Encoding]::new($false))

    $outputGood = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $scriptPath `
            -Path $fixturePath 2>&1)
    $goodExit = $LASTEXITCODE
    $goodText = ($outputGood | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($goodExit -ne 0 -or $goodText -notmatch '"offenderCount":0' -or $goodText -notmatch '"rowsChecked":2') {
        throw "Well-formed fixture was not reported clean. exit=$goodExit output=$goodText"
    }

    # --- Mutant: reproduce the Issue #357 defect class verbatim - a regex
    # alternation quoted with two literal, unescaped "|" bytes inside a
    # code span, exactly like the real DEBT_LEDGER.md row before its fix.
    # Inserted as the third data row, INSIDE the "Записи" section. Expected
    # field count: 11 (7 real columns + 2 extra splits from the raw pipes).
    $mutantRow = "| 2026-08-03 | review PR #3 | Паттерн ``решение владельца|независимый review|PR #[0-9]+`` не покрывает случай | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
    $mutantLines = New-Fixture -ThirdRow $mutantRow
    $mutantLineNumber = ([array]::IndexOf($mutantLines, $mutantRow)) + 1

    $mutantPath = Join-Path $testRoot "LEDGER.mutant.md"
    [IO.File]::WriteAllLines($mutantPath, $mutantLines, [Text.UTF8Encoding]::new($false))

    $outputRed = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $scriptPath `
            -Path $mutantPath 2>&1)
    $redExit = $LASTEXITCODE
    $redText = ($outputRed | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($redExit -ne 1 -or $redText -notmatch '"offenderCount":1' -or
        $redText -notmatch "`"line`":$mutantLineNumber" -or $redText -notmatch '"fields":11') {
        throw "Mutant with two raw unescaped '|' was not caught as expected. exit=$redExit output=$redText line=$mutantLineNumber"
    }

    # --- Green again: fix the mutant the way Issue #357 requires - rephrase
    # to remove the raw pipe bytes rather than just backslash-escaping them
    # (escaping alone still leaves the raw byte for this naive, awk-style
    # check, which is deliberate: it is what actually broke the real row).
    $fixedRow = "| 2026-08-03 | review PR #3 | Паттерн из трёх альтернатив ``решение владельца``, ``независимый review``, ``PR #[0-9]+`` не покрывает случай | Причина | Условие | CONTINUE, 2026-08-01 | 0 |"
    $fixedLines = New-Fixture -ThirdRow $fixedRow
    $fixedPath = Join-Path $testRoot "LEDGER.fixed.md"
    [IO.File]::WriteAllLines($fixedPath, $fixedLines, [Text.UTF8Encoding]::new($false))

    $outputGreenAgain = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $scriptPath `
            -Path $fixedPath 2>&1)
    $greenAgainExit = $LASTEXITCODE
    $greenAgainText = ($outputGreenAgain | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
    if ($greenAgainExit -ne 0 -or $greenAgainText -notmatch '"offenderCount":0' -or $greenAgainText -notmatch '"rowsChecked":3') {
        throw "Fixed mutant did not turn the check green again. exit=$greenAgainExit output=$greenAgainText"
    }

    # --- Missing file -> exit 2, distinct from "found offenders" (exit 1).
    $missingPath = Join-Path $testRoot "does-not-exist.md"
    $outputMissing = @(& powershell `
            -NoProfile `
            -ExecutionPolicy Bypass `
            -File $scriptPath `
            -Path $missingPath 2>&1)
    $missingExit = $LASTEXITCODE
    if ($missingExit -ne 2) {
        throw "Missing file was not reported with exit 2. exit=$missingExit"
    }

    [ordered]@{
        event             = "check_ledger_table_columns_test"
        status            = "ok"
        goodFixtureExit0  = $true
        mutantCaughtExit1 = $true
        fixedMutantExit0  = $true
        missingFileExit2  = $true
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
