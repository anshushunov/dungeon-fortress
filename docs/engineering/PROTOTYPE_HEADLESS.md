# Headless-контур Prototype 1

Статус: действует
Дата обновления: 2026-07-27
Источник: Issue #9

## Назначение

`DungeonFortress.Simulation` содержит чистое .NET-ядро Prototype 1 без типов
Godot. Игровой ввод использует только закрытую схему v2 из ADR 0005. Legacy
`SimulationWorld` и схема v1 остаются отдельным нагрузочным spike и не являются
игровым entry point.

Каноническое состояние schema v2 включает оставшиеся команды, следующий id
работы, именных существ и все их накопительные счётчики, зоны, состояние каждой
грядки, россыпь ресурсов, полные работы, запасы, экономические и трудовые
счётчики, занятость кухонь и тренировочных столбов, последние решения, журнал
событий и предбоевой снимок готовности. Это позволяет сравнивать не только
видимый итог, но и всё состояние, способное изменить будущие тики.
Одинаковые seed, начальное состояние и журнал команд дают байт-в-байт одинаковые
state, event log и SHA-256 checksum.

## Fixtures

Версионированные журналы находятся в `scenarios/prototype1/`:

- `baseline.commands.v2.json`;
- `prepared.commands.v2.json`;
- `neglected.commands.v2.json`.

Все используют seed `20260726`, одинаковое начальное состояние и различаются
только командами непрямого управления.

Перед созданием мира весь v2-документ проходит статическую и последовательную
семантическую проверку. Поэтому ошибка даже в поздней команде (непроходимый или
запретный тайл, ворота, недопустимое значение либо удаление последней физической
кладовой из зоны `Larder`) отклоняет весь запуск до тика 0 одинаково через API,
CLI и `prototype_run`.

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

В режиме `--prototype` seed берётся только из gameplay-v2 документа, а
фиксированная популяция — из контракта Prototype 1. Явные `--seed` и `--agents`
отклоняются, чтобы CLI не создавал второй источник истины. Эти флаги остаются
доступны legacy simulation.

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
канонические state/event log, счётчики `economy`/`labor`, наблюдения по
`stations` и краткие числовые observations. Команд записи в
живую сессию пока нет: один вызов воспроизводит весь переданный журнал, что
сохраняет атомарность и облегчает review.

## Проверка

```powershell
dotnet test .\tests\DungeonFortress.Simulation.Tests
dotnet test .\tests\DungeonFortress.DomainMcp.Tests
.\scripts\verify.ps1
```

Сценарные тесты дважды запускают каждый fixture, проверяют replay, закрытую
схему и whole-document preflight, reason codes, conservation экономической
цепочки, трудовой бюджет, занятость станций, движение не более чем на один тайл,
отсутствие swap/overlap и коридоры раздела 13.4 контракта. Wall-clock время
наблюдается в CLI/MCP output, но не используется как flaky correctness gate.
Бой, налётчики, кража и outcomes появятся в Issue
#11 и не подделываются текущим ядром.
