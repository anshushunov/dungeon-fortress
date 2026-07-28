# Headless-контур Prototype 1

Статус: действует
Дата обновления: 2026-07-28
Источник: Issue #9, расширен #24, #26, #48 и #58

## Назначение

`DungeonFortress.Simulation` содержит чистое .NET-ядро Prototype 1 без типов
Godot. Игровой ввод использует только закрытую схему v2 из ADR 0005. Legacy
`SimulationWorld` и схема v1 остаются отдельным нагрузочным spike и не являются
игровым entry point.

Каноническое состояние schema v2 включает оставшиеся команды, следующий id
работы, именных существ и все их накопительные счётчики, зоны, состояние каждой
грядки, россыпь ресурсов, содержимое каждой клетки материального склада вместе с
её вместимостью и бронью, состояние каждой стройки вместе с доставленным на неё
камнем, обе дельты карты — выкопанное и построенное, полные работы, запасы,
экономические и трудовые счётчики, занятость кухонь и тренировочных столбов,
последние решения, журнал событий и предбоевой снимок готовности. Это позволяет сравнивать не только
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

### Момент применения команды

Команда с тиком `T` применяется **в начале тика `T`**: `Step()` сначала берёт все
команды с этим номером, затем выполняет тик и только потом увеличивает счётчик.
Поэтому прогон ровно до `T` останавливается **перед** применением, и эффект
команды виден только после `RunTicks(T + 1)`.

```powershell
# журнал stone-haul-demo размечает склад на тике 200
--ticks 200   # stockpileCapacity = 0: команда ещё не применена
--ticks 201   # клетки склада уже существуют
```

То же правило действует в graybox: команда, принятая на текущем тике, становится
активной на следующем. На этом дважды падали тесты в Issue #26, и по этой же
причине `--demo-dig` делает шаг на один тик между разметкой и снятием
обозначения.

Правило касается канонического состояния, а не картинки. С Issue #58 graybox
показывает разметку сразу после клика: слой представления
`DungeonFortress.Presentation.MapProjection` накладывает на снапшот команды с
текущим тиком из `pendingCommands`. Симуляция, порядок операций внутри тика и
состав снапшота при этом не меняются, поэтому checksum и replay не зависят от
того, была ли пауза. Подробности — в
[`PROTOTYPE_GRAYBOX.md`](PROTOTYPE_GRAYBOX.md#marking-while-time-is-stopped-issue-58).

### Две формы вывода

CLI печатает две разные вещи, и их не следует путать.

- `prototype_result` — одна строка JSON со **сводкой прогона**. Числа в ней
  подняты на верхний уровень (`looseStone`, `carriedStone`, `storedStone`,
  `stockpileCapacity`, `digsCompleted`), а `economy`, `labor` и `stations`
  вложены как объекты. Это отчёт о запуске, удобный для чтения и для grep.
- `--print-snapshot` и `--snapshot <файл>` выдают **канонический** JSON. Там те
  же факты лежат по своим разделам: запасы — в `stocks`, россыпь ресурсов — в
  массиве `looseItems`, карта и раскопки — в `map`, счётчики — в `economy` и
  `labor`. Именно этот документ участвует в SHA-256 checksum и в сравнении
  байт-в-байт.

Формы намеренно не выравниваются. Канонический снапшот — это контракт: к его
схеме привязаны checksum, replay и `prototype_run`, и менять её ради удобства
чтения нельзя. `prototype_result` — производная сводка, которую можно расширять,
не трогая контракт. Скрипту, которому нужно состояние мира, нужен снапшот;
скрипту, которому нужен итог прогона, — `prototype_result`.

Раскопка наблюдается тем же способом. Fixture `dig-demo` обозначает все шесть
тайлов внутреннего массива скалы на тике 0 и снимает обозначение с `(26,3)` на
тике 1. Результат содержит `digsCompleted`, `looseStone`, `excavatedTiles` и
`digDesignations` со `statusCode` каждого обозначения, поэтому состояние
раскопки читается без анализа изображения.

Короткий прогон показывает разбор намерений:

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\dig-demo.commands.v2.json `
  --ticks 5
```

Остаётся пять обозначений. `(25,1)`, `(25,2)` и `(25,3)` — `dig_reserved`, у
каждого свой `reservedBy` и свой `workTile` из колонки 24. `(26,1)` и `(26,2)` —
`dig_unreachable` с `reachable: false`: правая колонна примыкает к границе карты
и замурована, пока не выкопан сосед слева. Работы у них нет, и ни одно существо
к ним не идёт.

Полный прогон показывает результат:

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\dig-demo.commands.v2.json `
  --ticks 200
```

`digsCompleted` = 5, `looseStone` = 5, `excavatedTiles` =
`(25,1) (26,1) (25,2) (26,2) (25,3)`, `digDesignations` пуст. `(26,3)` остаётся
скалой: обозначение с него снято. Замурованные тайлы выкопаны не приказом, а
потому что раскопка соседа сделала их достижимыми.

Поведение этой fixture зафиксировано тестом
`Dig_demo_fixture_matches_the_documented_headless_walkthrough`.

### Перевозка камня на материальный склад

Fixture `stone-haul-demo` обозначает четыре тайла скалы на тике 0 и размечает
зону `MaterialStockpile` на клетках `(22,1)` и `(23,1)` только на тике 200 —
уже после того, как камень выкопан. Это делает наблюдаемыми все три состояния
ресурса по очереди.

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\stone-haul-demo.commands.v2.json `
  --ticks 200
```

`digsCompleted` = 4, `looseStone` = 4, `storedStone` = 0,
`stockpileCapacity` = 0, `materialStockpile` пуст. В журнале есть
`waiting_no_stockpile`: камень лежит, и причина этого читается без картинки.

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\stone-haul-demo.commands.v2.json `
  --ticks 210
```

Появились две клетки склада со `stockpileCapacity` = 4 и `statusCode`
`stockpile_empty`. Камень всё ещё лежит: перевозку никто не приказывал, работа
достаётся первому освободившемуся существу по обычному скорингу.

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\stone-haul-demo.commands.v2.json `
  --ticks 700
```

`looseStone` = 0, `storedStone` = 4, `stoneHaulsCompleted` = 4, обе клетки в
состоянии `stockpile_full`. Инвариант `stoneProduced = looseStone +
carriedStone + storedStone` выполняется на каждом тике этого прогона.

Поведение этой fixture зафиксировано тестом
`Stone_haul_demo_fixture_matches_the_documented_headless_walkthrough`.

### Строительство первой функциональной комнаты

Fixture `build-demo` — вся цепочка Issue #48 в одном журнале: те же четыре тайла
скалы на тике 0, склад на тике 200, а на тике 1000 — blueprint тренировочного
поста на клетке `(25,2)`, зона `TrainingGround` поверх неё и `Drill` = 3. Тик
выбран так, что весь камень уже уложен на склад, поэтому доставка обязана
достать его обратно.

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -- `
  --prototype `
  --commands .\scenarios\prototype1\build-demo.commands.v2.json `
  --ticks 1000
```

`storedStone` = 4, `siteStone` = 0, `stoneConsumed` = 0, `buildSites` пуст:
команда ещё не применена, камень лежит на складе и не участвует ни в чём.

```powershell
--ticks 1001
```

Появился один `buildSite` на `(25,2)` со `statusCode` `build_waiting_carrier`,
`delivered` 0 из `required` 2. Работы `Build` нет: пока камень не доставлен,
строить нечего, и «жду материал» отличается от «жду строителя».

```powershell
--ticks 1030
```

Тот же blueprint в состоянии `build_carrier_on_the_way` с
`incomingReserved` = 2. Никто не получал приказа: работу `Haul` выбрал первый
освободившийся по обычному скорингу, а склад выиграл у россыпи только потому,
что россыпи не осталось.

```powershell
--ticks 1150
```

`buildSites` пуст, `builtPostTiles` = `(25,2)`, `buildsCompleted` = 1,
`stoneConsumed` = 2, `storedStone` = 2, `labor.buildTicks` = 30,
`labor.drillTicks` > 0. Инвариант
`stoneProduced = looseStone + carriedStone + storedStone + siteStone +
stoneConsumed` выполняется на каждом тике этого прогона, и построенный пост
порождает работы `Drill` на клетке, которой на тике 0 не существовало.

Поведение этой fixture зафиксировано тестом
`Build_demo_fixture_matches_the_documented_headless_walkthrough`.

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
`stations`, состояние камня (`looseStone`, `carriedStone`, `storedStone`,
`siteStone`, `reservedStone`, `stockpileCapacity` и поклеточный
`materialStockpile`), состояние строительства (`buildSites`, `builtPostTiles`, а
также `buildsCompleted` и `stoneConsumed` внутри `economy`) и краткие
числовые observations. Команд записи в
живую сессию пока нет: один вызов воспроизводит весь переданный журнал, что
сохраняет атомарность и облегчает review.

## Проверка

```powershell
dotnet test .\tests\DungeonFortress.Simulation.Tests
dotnet test .\tests\DungeonFortress.Presentation.Tests
dotnet test .\tests\DungeonFortress.DomainMcp.Tests
.\scripts\verify.ps1
```

Сценарные тесты дважды запускают каждый fixture, проверяют replay, закрытую
схему и whole-document preflight, reason codes, conservation экономической
цепочки, трудовой бюджет, занятость станций, движение не более чем на один тайл,
отсутствие swap/overlap и коридоры раздела 13.4 контракта.

Отдельный набор `PrototypeBuildTests` проверяет цепочку строительства:
закрытость команд `build_designate`/`build_cancel` для любой адресации,
двухуровневую валидацию blueprint на выкопанной земле, запрет совмещения стройки
и склада, публикуемый список допустимых клеток, полный проход
`разметить → выкопать → складировать → blueprint → доставить → построить → Drill`
в одной сессии, сохранение камня на **каждом** тике полной сессии с расходом и
отменой, все десять `statusCode` стройки, детерминизм источника, цели и брони,
отсутствие переполнения стройки и неизменность трёх сценариев без строительства.

Отдельный набор
`PrototypeStoneHaulTests` проверяет цепочку камня: валидацию зоны
`MaterialStockpile`, отсутствие перевозки без склада и при `Haul` = 0,
детерминированный выбор источника, цели и брони, сохранение количества камня на
**каждом** тике полной сессии, отсутствие переполнения и двойного подъёма,
перепланирование и безопасный сброс груза при потере цели, высыпание запаса при
стирании зоны, сосуществование с пищевой перевозкой и неизменность сценариев без
камня.

`DungeonFortress.Presentation.Tests` проверяет уже не симуляцию, а её показ:
текст четырёх панелей HUD и все ветки объяснений инспектора, включая все
`statusCode` склада и раскопки. Отдельный набор `MapProjectionTests` проверяет
слой «принято, ещё не применено» из Issue #58: разметка, снятие разметки,
blueprint, клетка склада и зона попадают на карту в тот же момент, тик их
применения не меняет набор отрисованных клеток, кисть не предлагает уже
размеченную клетку, а команда, назначенная на более поздний тик, заранее не
показывается. Отдельный набор воспроизводит три эталонных кадра
`tests/golden/ui/*.json` из того же журнала команд и сравнивает текст с
эталонами **без запуска Godot**, поэтому изменение формулировки видно в CI на
pull request, а не при следующем локальном `verify.ps1`. Граница слоя описана в
[ADR 0011](../decisions/0011-presentation-layer-without-engine.md).

Wall-clock время
наблюдается в CLI/MCP output, но не используется как flaky correctness gate.
Налёт, бой, кража и `sessionResult` входят в текущий headless-срез. Для
воспроизводимой полной оценочной матрицы и её повторной проверки используйте
`scripts/evaluate-prototype.ps1`; компактный зафиксированный результат и
методика находятся в `docs/playtests/PROTOTYPE_01_EVALUATION.md`.
