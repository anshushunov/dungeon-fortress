# Headless-контур Prototype 1

Статус: действует
Дата обновления: 2026-07-26
Источник: Issue #9

## Назначение

`DungeonFortress.Simulation` содержит чистое .NET-ядро Prototype 1 без типов
Godot. Игровой ввод использует только закрытую схему v2 из ADR 0005. Legacy
`SimulationWorld` и схема v1 остаются отдельным нагрузочным spike и не являются
игровым entry point.

Каноническое состояние включает именных существ, зоны, запасы, работы,
потребности, последние решения, журнал событий и предбоевой снимок готовности.
Одинаковые seed, начальное состояние и журнал команд дают байт-в-байт одинаковые
state, event log и SHA-256 checksum.

## Fixtures

Версионированные журналы находятся в `scenarios/prototype1/`:

- `baseline.commands.v2.json`;
- `prepared.commands.v2.json`;
- `neglected.commands.v2.json`.

Все используют seed `20260726`, одинаковое начальное состояние и различаются
только командами непрямого управления.

## CLI

Прогоны fixtures до тика набега:

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\baseline.commands.v2.json `
  --ticks 1501

dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\prepared.commands.v2.json `
  --ticks 1501

dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\neglected.commands.v2.json `
  --ticks 1501
```

Добавьте `--print-snapshot`, чтобы вывести канонический JSON, или
`--snapshot .\.artifacts\prepared.json`, чтобы записать его в файл. Параметры
скорости воспроизведения не входят в игровой журнал.

Для тестов ядро также предоставляет `PrototypeWorld.Step()` и
`PrototypeWorld.RunTicks(n)`: разбивка одного и того же числа фиксированных
тиков на шаги не меняет каноническое состояние.

## Domain MCP

Безопасная read-only операция `prototype_run` принимает:

```json
{
  "commandsPath": "scenarios/prototype1/prepared.commands.v2.json",
  "ticks": 1501
}
```

`commandsPath` обязан быть относительным путём внутри проверенного корня
репозитория и вести к `.json` без symlink/junction. Ответ содержит checksum,
канонические state/event log и краткие числовые observations. Команд записи в
живую сессию пока нет: один вызов воспроизводит весь переданный журнал, что
сохраняет атомарность и облегчает review.

## Проверка

```powershell
dotnet test .\tests\DungeonFortress.Simulation.Tests
dotnet test .\tests\DungeonFortress.DomainMcp.Tests
.\scripts\verify.ps1
```

Сценарный тест дважды запускает fixtures, проверяет replay, закрытую схему,
атомарное отклонение, reason codes, коридоры раздела 13.4 контракта и небольшой
performance sanity budget. Бой, налётчики, кража и outcomes появятся в Issue
#11 и не подделываются текущим ядром.
