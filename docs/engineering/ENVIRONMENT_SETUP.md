# Настройка окружения Godot/.NET spike

Статус: действует для bootstrap-spike из Issue #3

Дата проверки: 2026-07-29

## Закреплённые версии

- [.NET SDK 8.0.423](https://dotnet.microsoft.com/en-us/download/dotnet/8.0);
- [Godot 4.7.1 stable, .NET edition](https://godotengine.org/download/archive/4.7.1-stable/);
- PowerShell 5.1 или новее для локальных Windows-скриптов.

`global.json` закрепляет SDK 8.0.423 с переходом только на более свежий patch в
той же feature band. Godot-проект использует `Godot.NET.Sdk/4.7.1`; локальный
NuGet source берётся из `GodotSharp/Tools/nupkgs` выбранной .NET-сборки движка.

## Обязательные компоненты

1. Установить .NET SDK и проверить:

   ```powershell
   dotnet --version
   ```

   Ожидается `8.0.423` или совместимый patch, разрешённый `global.json`.

2. Скачать и распаковать Godot 4.7.1 .NET edition в пользовательский каталог вне
   репозитория. Установка export templates для этого spike не требуется.

3. Сделать console executable доступным одним из трёх способов, в порядке
   приоритета:

   - передать `-GodotPath <path-to-godot-console>`;
   - установить `GODOT4_CONSOLE`;
   - добавить каталог в `PATH` под именем `godot4_console`, `godot4` или `godot`.

   Пример настройки только для текущего PowerShell-процесса:

   ```powershell
   $env:GODOT4_CONSOLE = "<path-to-godot-console>"
   ```

Скрипты проверяют строку версии `4.7.1` и наличие bundled NuGet packages. Путь
конкретной машины не записывается в проект.

## Временный каталог

Проверке нужен временный каталог, в котором она может создавать, писать **и
удалять**. Там живут две вещи: короткий runtime profile Godot и изолированный
проект теста импорта спрайтов. Обе создаются во время прогона и убираются после.

Каталог выбирается в порядке:

1. `-TemporaryRoot <path>` — явный одноразовый override;
2. `$env:DUNGEON_FORTRESS_TEMP` — override для всей сессии;
3. системные `TMP`/`TEMP`.

Выбранный каталог печатается до первой стадии и попадает в итоговую строку:

```json
{"event":"verification_temporary_root","status":"ok","path":"C:\\Users\\<user>\\AppData\\Local\\Temp","source":"TMP/TEMP"}
```

Пригодность проверяется **до** запуска стадий: скрипт создаёт пробный подкаталог
с файлом и удаляет его — тем же вызовом `Remove-Item -Recurse -Force`, которым
пользуется настоящая уборка. Непригодный каталог отвергается с указанием
каталога, причины и способа исправления, а `verification_result` получает
`"failedPhase":"preflight"` и пустой `stagesExecuted`.

Пример из сессии, в которой `TEMP` указывал в `C:\WINDOWS\TEMP`:

```text
The temporary directory this run would use is not usable.
  directory: C:\WINDOWS\TEMP
  chosen by: TMP/TEMP
  failure:   'C:\WINDOWS\TEMP\verify-temp-probe-...' was created and then could
             not be deleted: Отказано в доступе.
Use one of:
  -TemporaryRoot <directory this account can create and delete in>
  $env:DUNGEON_FORTRESS_TEMP=<the same directory>
  point TMP and TEMP at such a directory
```

Так выглядит Issue #89. До проверки пригодности та же сессия доходила до стадии
`godot`, тест импорта печатал `status: ok`, и только его уборка падала на
`Remove-Item`. Прогон сообщал `{"failedStage":"godot","reason":"'powershell'
failed with exit code 1."}` — сообщение не называло ни каталог, ни право, и три
разные сессии искали дефект в проверяемом изменении.

Тонкость, из-за которой проверка делает удаление именно через `Remove-Item`: в
`C:\WINDOWS\TEMP` вызов `[IO.Directory]::Delete` на собственном подкаталоге
проходит, а `Remove-Item -Recurse -Force` на нём же падает с
`Access is denied`. Проба более дешёвым вызовом признала бы каталог пригодным, а
уборка всё равно упала бы.

Каталог **не** подбирается автоматически. Он обязан быть коротким и вне
worktree: в нём создаётся runtime profile Godot, а длинный путь возвращает
ограничение `CreateDirectory` и ошибку shader cache из раздела «Проверка вывода
Godot». Подставить `.artifacts/` вместо системного temp — значит вернуть ту
ошибку молча, поэтому прогон отказывается угадывать и просит выбрать каталог
явно.

Неудача уборки — предупреждение, а не провал. Если каталог перестал удаляться
уже после проверки пригодности (антивирус, открытый файл, редактор), в выводе
появляется строка

```json
{"event":"temporary_cleanup","status":"warning","description":"...","path":"...","reason":"..."}
```

а прогон сохраняет тот статус, который заслужили сами проверки. Выполненная
проверка не становится красной из-за того, что каталог не удалось стереть.

Обе половины правила проверяются отдельным dependency-free тестом из стадии
`scripts`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-temporary-root.ps1
```

Он подтверждает порядок выбора каталога, отвергает файл вместо каталога и
каталог, из которого не удаляется содержимое, требует, чтобы пригодный каталог
принимался и не оставлял мусора, и запускает настоящий `verify.ps1` с заведомо
непригодным `-TemporaryRoot`, ожидая отказ с пустым `stagesExecuted`. Удаление
блокируется настоящим открытым файлом, а не заглушкой.

## Единая проверка

Из корня репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Для явного одноразового override:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -GodotPath "<path-to-godot-console>"
```

Без параметров выполняются все стадии. Это полный прогон, и его состав с
введением стадий не изменился.

### Стадии

Проверка разделена на девять стадий. Стадия — группа проверок, которая падает по
одной причине и нужна одному виду изменений. Полный прогон выполняет их в
порядке таблицы; `-Stage` оставляет подмножество, `-Skip` исключает названные.

Таблица ниже — источник, с которым `test-verify-stages.ps1` сверяет `-ListStages`.
Маркеры вокруг неё не декоративные: без них сверка не знает, какую таблицу читать,
и падает.

<!-- stage-table:begin -->

| Стадия | Что проверяет | Время\* |
|---|---|---|
| `scripts` | восемь dependency-free гвардов: контракт стадий, пригодность временного каталога, вывод Godot, пути screenshot/evidence, manifest, GitHub auth diagnostic, конфиги Ivan-MCP и domain MCP | 25 с |
| `build` | `restore` решения, `--locked-mode` restore тестов domain MCP, `Release` build всего решения | 3 с |
| `tests` | `dotnet test` для Simulation, Presentation и DomainMcp | 31 с |
| `mcp` | реальный запуск launcher domain MCP и stdio-контракт `verify-domain-mcp.ps1` | 4 с |
| `sim` | детерминизм: 32 агента × 256 тиков дважды на одном seed и один раз на другом | 0,4 с |
| `load` | 1 000 агентов × 10 000 fixed ticks дважды с побайтовым сравнением | 0,6 с |
| `godot` | `Debug` build Godot-host, изолированный импорт спрайтов, smoke, camera/input smoke, независимость checksum от камеры/кадра/UI scale и канонического состояния от частоты кадров | 30 с |
| `ui` | эталонные кадры `tests/golden/ui/*.json`, снятые headless и сравнённые с коммитом | 4 с |
| `screenshots` | baseline-кадр дважды с побайтовым сравнением и prepared-raid кадр; все параметры кадра явные | 12 с |

<!-- stage-table:end -->

\* Замер на машине владельца при прогретых сборках; полный прогон в тех же
условиях — около 90 с. Первый прогон в свежем worktree дольше за счёт restore.

Что покрывает каждая стадия, печатает сам скрипт:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -ListStages
```

Примеры частичного прогона:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -Stage ui
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -Stage tests,sim
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -Skip load,screenshots
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -Stage all
```

`-Stage all` — явный псевдоним полного прогона: он раскрывается в весь список
стадий и даёт `scope: full`, то есть равен запуску без `-Stage`. Пригоден, когда
имя стадии подставляется скриптом или переменной и «все» нужно выразить
значением, а не отсутствием параметра. Имя `all` занято псевдонимом и не может
быть именем стадии.

Любая стадия, включая `scripts`, требует установленного Godot. Движок
разрешается один раз до цикла стадий, потому что NuGet-профиль для .NET-сборок
пишется из bundled packages выбранной сборки движка. Обход сделал бы частичный
прогон проверкой не того же, что проверяет полный: сборка пошла бы с
machine-wide NuGet state. Поэтому «dependency-free» в описании стадии `scripts`
означает «без сборки решения и без запуска движка», а не «без установленного
движка».

### Какую стадию выбирать

| Что изменено | Минимальный набор |
|---|---|
| `scripts/**`, evidence/auth tooling, конфигурация MCP-клиентов | `scripts`; плюс `mcp` при правке launcher или `verify-domain-mcp.ps1` |
| `src/DungeonFortress.Simulation/**` | `tests,sim`; плюс `load` при изменении стоимости тика или структур данных; плюс `mcp` при изменении ответа `prototype_run` |
| `src/DungeonFortress.Presentation/**` | `tests,ui` |
| `src/DungeonFortress.Game/**`, сцены, ввод | `godot,ui`; плюс `screenshots` при изменении кадра или визуального слоя |
| спрайты и другие ассеты | `godot,screenshots` |
| `.csproj`, `global.json`, версии зависимостей | полный прогон |
| только документация | ничего |

Стадия сама доводит рабочую копию до нужного состояния: `-Stage ui` выполнит
restore, `Debug` build Godot-host и импорт ассетов, `-Stage sim` соберёт scenario
runner. Поэтому частичный прогон не проверяет вчерашний бинарник, а выполненные
подготовительные шаги перечислены в поле `prerequisites`.

Сам механизм стадий тоже проверяется, и без сборки:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-verify-stages.ps1
```

Тест разбирает `verify.ps1` как AST и держит три инварианта, ни один из которых
не виден в зелёном прогоне.

**Ни одна проверка не живёт вне стадии.** Правило задано списком **мест**, а не
списком известных имён проверок. Тело стадии и тело любой функции, достижимой из
стадии, могут содержать что угодно — для этого стадия и нужна. Всё остальное —
верхний уровень и тело функции, до которой не дотягивается ни одна стадия, —
может вызывать только имена из `$allowedOutsideStages`. Поэтому проверка,
добавленная под именем, которого guard никогда не видел, падает по умолчанию, а
добавление имени в список — отдельная строка в diff с обоснованием рядом.

Прежняя версия перечисляла известные имена проверок, и review PR #70 прошёл
сквозь неё: `Assert-SomeNewInvariant` внутри `try` перед циклом стадий возвращал
`status: ok`.

**Всё, что вызывает `dotnet`, идёт до переключения `APPDATA`.**
`Initialize-GodotRuntimeEnvironment` переписывает `APPDATA` на короткий runtime
profile Godot, в котором нет NuGet-конфигурации. Guard моделирует порядок
выполнения по AST с учётом мемоизации подготовительных шагов и проверяет каждую
одиночную стадию и каждую упорядоченную пару стадий — то есть любой выбор,
который можно попросить через `-Stage`. Раньше инвариант держался комментарием.
Заодно фиксируется порядок подготовки до цикла стадий: временный каталог,
разрешение движка, версия, NuGet source, NuGet-профиль.

**Таблица стадий совпадает со скриптом.** Сверка читает таблицу между маркерами
`<!-- stage-table:begin -->` и `<!-- stage-table:end -->`, а не «первую ячейку в
обратных кавычках» — иначе будущая посторонняя таблица со строкой вида
``| `assets` | … |`` дала бы ложное «documented but absent».

Кроме того, тест требует, чтобы неизвестное имя стадии и пустой выбор
отвергались, и проверяет сам себя: шесть мутаций `verify.ps1` во временной копии
(проверка вне стадии под новым именем и через переменную, `dotnet` после
переключения профиля, переставленные подготовительные шаги, недостижимый
prerequisite, удалённый preflight временного каталога), две мутации документа
(посторонняя таблица игнорируется, удалённая строка ловится) и контрольный
прогон на неизменённой копии, без которого негативные случаи не доказывали бы
ничего. Тест входит в стадию `scripts`.

### Когда полный прогон обязателен

Частичный прогон — инструмент итерации, а не замена проверки. Полный прогон
обязателен:

- перед handoff, PR и merge: `green-auto-merge` из
  [ADR 0014](../decisions/0014-parallel-agents-and-autonomy.md) требует зелёный
  `scripts/verify.ps1` целиком;
- после изменения зависимостей, версии SDK или движка;
- когда изменение затрагивает больше одной области из партиции ADR 0014;
- когда неясно, что именно затронуто.

Отличать прогоны нужно по выводу, а не по памяти. Итоговая строка
`verification_result` содержит `scope` (`full` или `partial`), `stagesExecuted` и
`stagesNotRun`:

```json
{"event":"verification_result","status":"ok","scope":"partial","stagesExecuted":["ui"],"stagesNotRun":["scripts","build","tests","mcp","sim","load","godot","screenshots"]}
```

Прогон, упавший на середине, печатает ту же строку со `"status":"error"`, именем
упавшей стадии и списком всего, что не выполнялось. Молча усечённой проверки не
бывает: частичный прогон всегда называет то, что он не проверял.

Проверку можно запускать с подключённым domain MCP: клиентская сессия исполняет
собственную копию сервера и не держит открытым build output. Останавливать
сессию перед `verify.ps1` не нужно — см. «Project-owned domain MCP».

Временные NuGet-настройки, пакеты и результаты создаются под `.artifacts/`,
который игнорируется Git. При первом запуске нужен доступ к NuGet.org для
тестовых пакетов; Godot packages берутся из выбранного движка. Runtime Godot
получает отдельный короткий профиль под системным temporary directory, чтобы
NuGet tool-profile из длинного worktree path не становился его `APPDATA`.

### Проверка вывода Godot

`run-game.ps1` и `verify.ps1` не считают exit code 0 достаточным условием успеха.
Общий guard завершает команду с code 1 при любой строке `ERROR:`, при ненулевом
exit code или при отсутствии ожидаемого structured success event. Широкого
whitelist нет.

Guard имеет отдельный dependency-free тест:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-godot-output-guard.ps1
```

Во время review PR #5 исходный `run-game.ps1` воспроизводимо давал:

```text
ERROR: Condition "err != OK" is true.
   at: initialize (drivers/gles3/shader_gles3.cpp:802)
```

Причиной оказался не shader проекта и не NVIDIA driver. NuGet bootstrap временно
переназначал `APPDATA` внутрь worktree и оставлял его таким для Godot runtime.
Путь cache entry `CanvasOcclusionShaderGLES3/<sha256>` достигал 255 символов.
В [Godot 4.7.1 строка 802](https://github.com/godotengine/godot/blob/4.7.1-stable/drivers/gles3/shader_gles3.cpp#L800-L803)
проверяет ошибку создания именно каталога SHA-256. Windows документирует
ограничение `CreateDirectory` в 248 символов без long-path opt-in:
[Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createdirectory).

Исправление разделяет длинный NuGet tool-profile и короткий Godot runtime
profile. Контрольный verbose-запуск после этого успешно инициализирует
`CanvasOcclusionShaderGLES3` без `ERROR:`.

## Производные файлы Godot и Git

Godot создаёт рядом с исходниками файлы, которых нет в коммитах: каталог кэша
`**/.godot/`, `*.import` рядом с каждым ассетом и `*.uid` рядом с каждым
скриптом. Все три восстанавливает тот же incremental-импорт, который
`verify.ps1` и `run-game.ps1` выполняют перед запуском движка, поэтому чистый
`git clone` полноценен без них.

### `*.uid` не отслеживаются — правило без исключений

Ни один `*.uid` не коммитится. Правило закреплено строкой `*.uid` в
`.gitignore` рядом с `*.import` и действует на весь Godot-проект.

До Issue #64 правила не было ни одного: `Main.cs.uid` отслеживался, а
`HudButton.cs.uid` из PR #57 — нет. Каждый агент в свежем worktree получал после
первого импорта грязное дерево и тратил внимание на вопрос «моё ли это
изменение». Это независимо произошло у исполнителей #58 и #38.

**Почему игнорируем.** Ни один отслеживаемый файл проекта не ссылается на
`uid://`. `Main.tscn` подключает скрипт по пути —
`[ext_resource type="Script" path="res://Main.cs"]`, — спрайты и иконки
загружаются `GD.Load<Texture2D>("res://assets/…")` тоже по пути, а
`project.godot` называет главную сцену как `res://Main.tscn`. Проверка ищет
только по содержимому Git и должна ничего не находить:

```powershell
git grep -n 'uid://' -- src/DungeonFortress.Game
```

Идентификаторы в проекте есть, но живут они ровно в производных файлах:
`uid="uid://…"` внутри каждого `*.png.import` и строка в каждом `*.uid`. Оба
класса игнорируются и восстанавливаются одним и тем же импортом, так что
одинаковое обращение с ними — не новая политика, а распространение уже
действующей.

Что идентификатор ни на что не влияет, проверено экспериментом, а не выведено из
этого поиска. После удаления `Main.cs.uid` вместе с локальным кэшем `.godot/`
импорт выдал скрипту **другой** случайный идентификатор
(`uid://du80nvw2ghu16` → `uid://cw3gvuoooy3xo`), `Main.tscn` при этом не
изменился, а полный `verify.ps1` остался зелёным — включая headless smoke,
`--smoke-controls`, camera/input smoke с независимой проверкой live Camera2D,
отрицательные startup/HUD/camera проверки, четыре camera/frame/UI варианта, три
golden UI frame, сравнение checksum на 20 и 60 fps и три screenshot-прогона,
которые загружают сцену и все PNG.

**Почему не коммитим.** Вариант «коммитить все `*.uid`» проверен тем же
способом и технически работает: уже закоммиченный `.uid` импорт сохраняет и не
переписывает даже при удалённом кэше `.godot/`. Отвергнут он не за это. Он
требует, чтобы автор каждого нового `.cs` вручную добавил в коммит второй файл,
который ничего не значит для читателя diff. Ровно этот шаг пропущен в PR #57 и
породил Issue #64, и ни одна проверка его не ловит. Игнорирование не требует
дисциплины вообще и одинаково ведёт себя при любом числе новых скриптов.

### При добавлении нового `.cs` или ресурса

Ничего делать не нужно. Новый `*.uid` появится после первого импорта, будет
проигнорирован и в `git status` не попадёт. Добавлять его через `git add -f` не
следует: отслеживаемый `.uid` вернёт ровно то расхождение, которое закрыл
Issue #64.

### Когда решение придётся пересмотреть

Триггер один: в `.tscn`, `.tres` или `project.godot` появилась ссылка `uid://`.
Признак виден прямо в diff — у `[ext_resource]` или у заголовка `[gd_scene]`
возникает атрибут `uid=`.

Механизм, которым это происходит, ровно один: **сцену сохранил редактор Godot.**
Пока сцены правятся текстом, как просит комментарий в первых строках
`project.godot`, атрибут `uid=` не появляется — импорт его не дописывает, что
подтверждает неизменённый `Main.tscn` в эксперименте выше. Но редактор в этом
репозитории не экзотика: `project.godot` держит включённым плагин
`res://addons/godot_mcp/plugin.cfg`, а `scripts/ivan-mcp.ps1 -Action Open`
открывает настоящий редактор по [ADR 0004](../decisions/0004-dev-only-ivan-mcp.md).
Поэтому связь стоит помнить целиком: **редактор сохранил сцену → в сцене
появился `uid=` → правило игнорирования пересматривается отдельным Issue.**

Важно знать заранее, **как именно** такая ссылка ломается, потому что тихо.
Проверено на отдельном одноразовом проекте под `.artifacts/`: сцена с
`uid="uid://ck7jxbdtppw5b"` рядом с `path="res://Probe.gd"`, а локально
сгенерированный `.uid` содержит другой идентификатор. Godot 4.7.1 в этом случае:

```text
WARNING: res://Probe.tscn:3 - ext_resource, invalid UID: uid://ck7jxbdtppw5b
 - using text path instead: res://Probe.gd
```

Дальше сцена грузится по пути, скрипт подключается, процесс выходит с кодом 0, и
сам файл сцены Godot не переписывает. Это `WARNING`, а не `ERROR:`, поэтому
guard из `run-game.ps1` и `verify.ps1` его не поймает — он падает только на
`ERROR:` и на ненулевом exit code. Пока ссылок `uid://` нет, ловить нечего; как
только они появятся, расхождение придётся ловить не проверкой вывода, а этим
правилом.

## Отдельные команды

Pure .NET тесты:

```powershell
dotnet test .\tests\DungeonFortress.Simulation.Tests\DungeonFortress.Simulation.Tests.csproj -c Release
dotnet test .\tests\DungeonFortress.Presentation.Tests\DungeonFortress.Presentation.Tests.csproj -c Release
```

`DungeonFortress.Presentation.Tests` проверяет текст HUD и объяснения инспектора
из `src\DungeonFortress.Presentation`. Эта сборка не ссылается на Godot, поэтому
тесты не требуют движка — см. [ADR 0011](../decisions/0011-presentation-layer-without-engine.md).

Запись canonical snapshot:

```powershell
dotnet run --project .\tests\DungeonFortress.Scenarios -c Release -- --seed 424242 --agents 32 --ticks 256 --commands .\scenarios\smoke.commands.json --snapshot .\.artifacts\snapshot.json
```

Видимый Godot-host:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1
```

Автоматически закрывающийся видимый smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 -VisibleSmoke
```

Видимый узел только проецирует данные `DungeonFortress.Simulation`; fixed ticks
выполняются до отрисовки и не зависят от frame delta.

## CI

`.github/workflows/dotnet.yml` восстанавливает, собирает и тестирует только
engine-independent .NET projects на Ubuntu. Godot остаётся локальной проверкой:
загрузка и pinning engine binary в CI расширили бы этот spike без новой пользы.

Поэтому всё, что должно проверяться на каждом pull request, обязано жить в
сборке без зависимости от движка. Текст HUD и инспектора вынесен в
`DungeonFortress.Presentation` именно ради этого: до Issue #39 эталоны
`tests/golden/ui/*.json` сравнивались только локально на машине владельца.

## Откат и удаление

- удалить распакованный каталог Godot, если он больше не нужен;
- удалить пользовательскую переменную `GODOT4_CONSOLE` или запись из `PATH`;
- удалить `.artifacts/`, чтобы очистить только локальные производные этого
  репозитория;
- удалить обычным способом .NET SDK 8, только если он не используется другими
  проектами.

Репозиторий не устанавливает глобальные workloads, не изменяет глобальный
NuGet.Config и не хранит абсолютные пути. MCP и Agent Bridge в Issue #3 не
устанавливаются и не настраиваются; это отдельный decision gate для ADR 0003.

## Project-owned domain MCP

Issue #4 добавляет engine-independent stdio adapter над тем же
`DungeonFortress.Simulation` и command document contract, что использует
scenario CLI. Перед первой client-сессией:

```powershell
dotnet restore .\tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj --locked-mode
dotnet build .\DungeonFortress.sln -c Release --no-restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-domain-mcp.ps1 -NoBuild
```

После Release build trusted Codex project читает `.codex/config.toml`, а Claude
Code предлагает одноразово подтвердить project-scoped `.mcp.json`. Эти файлы не
содержат секретов и абсолютных путей. Server публикует только
`bridge_status`, legacy `simulation_run` и gameplay-v2 `prototype_run`; запуск
fixtures Prototype 1 описан в `PROTOTYPE_HEADLESS.md`, а подробный контракт,
pins, hashes и security
guards и rollback находятся в `MCP_EVALUATION.md`.

Для `prototype_run` fixture является единственным источником seed, состава
существ и command document: флаги legacy CLI `--seed` и `--agents` с prototype
fixture отвергаются, а не переопределяют сценарий. Command document проходит
полную preflight-валидацию до создания мира, поэтому ошибка в будущей команде не
может оставить частично выполненный прогон. Ответ инструмента содержит canonical
snapshot и явные секции `economy`, `labor`, `stations`, пригодные для проверки
агентом без анализа изображения.

### Запуск сессии отделён от цели сборки

Оба клиента поднимают сервер одной командой
`cmd /c scripts\domain-mcp-server.cmd`. Launcher копирует
`tools\DungeonFortress.DomainMcp\bin\Release\net8.0` в собственный каталог
сессии `.artifacts\domain-mcp-sessions\<id>` и запускает копию. Отдельного шага
подготовки нет, а при завершении сессии каталог копии удаляется.

Так сделано потому, что раньше сессия исполняла сам build output. Файл
`bin\Release\net8.0\DungeonFortress.DomainMcp.exe` оставался открытым на всё
время сессии, и шаг `dotnet build DungeonFortress.sln -c Release` в
`verify.ps1` падал с `MSB3027`, не дойдя ни до одного теста (Issue #38). Теперь
цель сборки и исполняемая копия — разные файлы, поэтому проверка проходит при
любом числе подключённых клиентов и не требует ручной остановки сессии.

Что важно знать про копию:

- копия снимается один раз, в момент старта сессии. Живая сессия продолжает
  исполнять её и после пересборки решения — новую сборку подхватит только
  следующая сессия. Раньше действовало то же правило (`--no-build` исполнял
  последний build output), но теперь у копии есть собственный момент фиксации,
  и его стоит помнить при проверке свежих правок домена через MCP;
- каталог копии создаётся рядом под именем `<id>.partial` и переименовывается
  целиком, поэтому исполняемый каталог никогда не бывает наполовину скопирован.
  На всё время сессии launcher держит открытым `<id>.lock`, и уборка чужих
  копий пропускает такую сессию, даже если копирование ещё идёт;
- копии, оставшиеся после аварийно снятых сессий, удаляются при следующем
  старте: их lock свободен и исполняемый файл никем не открыт;
- старт сессии одновременно со сборкой теоретически может скопировать смесь
  файлов: robocopy читает каталог, который в этот момент переписывает MSBuild.
  Окно — доли секунды, MSBuild повторяет копирование до десяти раз, поэтому на
  практике такого не наблюдалось. Если сессия ведёт себя странно после старта
  во время сборки, перезапустите её.

Launcher написан на `.cmd`, а не на PowerShell: `cmd` запускает сервер ровно с
теми дескрипторами stdin/stdout, которые клиент дал launcher, без промежуточного
слоя. Прямой запуск из PowerShell (`&`, `Start-Process`) так не работает — его
native command processor превращает вывод дочернего процесса в объекты конвейера
и переписывает его через host, поэтому JSON-RPC пришлось бы перекладывать между
процессами вручную. Это лишняя копия, лишняя буферизация и риск перекодировки на
пути протокола без выигрыша. `scripts\verify-domain-mcp.ps1` поднимает тот же
сервер из PowerShell с явным перенаправлением, но там PowerShell сам является
клиентом, а не прозрачным передатчиком.

`cmd` и `robocopy` делают запуск domain MCP Windows-only. Раньше `dotnet run`
был формально кроссплатформенным, но контур проверки репозитория и так
Windows/PowerShell (ADR 0003, ADR 0004), а CI на Ubuntu MCP не поднимает.

Обе стороны проверяются автоматически:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-domain-mcp-config.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-domain-mcp-launcher.ps1
```

Первый тест читает конфигурацию и текст launcher: оба project config должны
поднимать сервер через launcher, не содержать машинных абсолютных путей и не
возвращаться к исполнению build output; первая строка launcher обязана быть
`@echo off`, а каждая его диагностика и вывод `robocopy` — уходить не в stdout.
Второй запускает launcher по-настоящему после сборки: проверяет ответ на
`initialize`, что сервер исполняет свою копию, что build output в это время
остаётся доступен на запись и что после закрытия stdin launcher вышел с кодом 0
и убрал за собой. Оба входят в `verify.ps1`; текстовый тест не заменяет запуск,
потому что опечатка в batch не видна в тексте.

Чтобы отключить domain MCP без удаления user-scope данных:

- Codex: установить `mcp_servers.dungeon_fortress_domain.enabled = false`
  в local/user override либо удалить project config вместе с изменением;
- Claude Code: не подтверждать project server либо удалить его entry из
  `.mcp.json` вместе с изменением;
- остановить client session; закрытие stdin штатно завершает stdio process, а
  launcher удаляет каталог копии этой сессии;
- удалить `.artifacts/` и обычные `bin/obj`, если нужна очистка производных
  файлов.

Конфигурация не создаёт listener, credential или внешнее соединение. Editor
adapter оценивается отдельно и не входит в production/runtime dependency graph
domain MCP.

## Dev-only Ivan-MCP для редактора

ADR 0004 принимает Ivan-MCP как доверенный локальный инструмент для тестовой
игры. Это не production dependency и не security sandbox: локальный MCP client
получает широкие editor/source/filesystem/reflection возможности. Не запускайте
его в worktree с секретами или недоверенным кодом.

Установка и запуск с Godot 4.7.1 .NET:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 `
  -Action Install `
  -GodotPath C:\path\to\Godot_v4.7.1-stable_mono_win64_console.exe

powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 `
  -Action Open `
  -GodotPath C:\path\to\Godot_v4.7.1-stable_mono_win64_console.exe
```

`Open -Headless` подходит для handshake/tree/log/play проверок, но screenshot
требует обычного оконного редактора. После исправления C# compile error
перезапустите tracked процессы: hot reload Godot может не выгрузить Ivan
assemblies.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 -Action Status
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 -Action Stop
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 -Action Open -Headless
```

Server слушает только `127.0.0.1:29541`/`::1`; cloud и credentials не
используются. Project configs Codex и Claude содержат
`http://127.0.0.1:29541/mcp`, но не запускают server автоматически.

Перед export и для полного отката удалите только candidate-owned локальную
установку:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 -Action Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

`Uninstall` не меняет глобальные настройки и user-scope конфиги. Exact pins,
hashes, лицензии, измерения и принятое исключение security gate находятся в
`MCP_EVALUATION.md` и ADR 0004.
