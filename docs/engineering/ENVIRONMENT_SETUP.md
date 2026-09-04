# Настройка окружения

## Версии

- [.NET SDK 8.0.423](https://dotnet.microsoft.com/en-us/download/dotnet/8.0);
  `global.json` разрешает более свежий patch в той же feature band.
- [Godot 4.7.1 stable, .NET edition](https://godotengine.org/download/archive/4.7.1-stable/).
  Godot-проект использует `Godot.NET.Sdk/4.7.1`; NuGet-пакеты движка берутся
  из `GodotSharp/Tools/nupkgs` распакованной сборки.
- PowerShell 5.1 или новее для скриптов.

## Установка

1. Установить .NET SDK, проверить `dotnet --version`.
2. Распаковать Godot 4.7.1 .NET в каталог вне репозитория. Export templates не
   нужны.
3. Сделать console executable движка доступным одним из способов, в порядке
   приоритета:
   - `-GodotPath <путь>` при вызове скрипта;
   - переменная `GODOT4_CONSOLE`;
   - каталог в `PATH` с именем `godot4_console`, `godot4` или `godot`;
   - без настройки: положить движок в
     `<родитель репозитория>\Godot_v4.7.1-stable_mono_win64\`.

Скрипты проверяют версию `4.7.1`. Пути конкретной машины в репозиторий не
попадают.

## Команды

```powershell
# Тесты без движка
dotnet test .\tests\DungeonFortress.Simulation.Tests -c Release
dotnet test .\tests\DungeonFortress.Presentation.Tests -c Release

# Детерминированный сценарий с записью снапшота
dotnet run --project .\tests\DungeonFortress.Scenarios -c Release -- --seed 424242 --agents 32 --ticks 256 --commands .\scenarios\smoke.commands.json --snapshot .\.artifacts\snapshot.json

# Игра
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1
# Видимый smoke, закрывается сам
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\run-game.ps1 -VisibleSmoke

# Полная проверка перед PR
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
# Только часть стадий
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -Stage tests,sim
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -ListStages

# Обновить золотые кадры HUD после правки интерфейса
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\update-golden-ui.ps1
```

Стадии `verify.ps1`: `build`, `tests`, `sim` (детерминизм байт в байт), `load`
(1000 агентов × 10000 тиков дважды), `godot` (headless smoke, камера,
читаемость HUD), `ui` (золотые кадры), `screenshots` (повторяемые скриншоты).
Результат последнего прогона лежит в `.artifacts/verify-results/`.

Прогон ничего не оставляет в среде машины: `DOTNET_CLI_HOME` и профиль Godot
живут в `.artifacts/` и во временном каталоге, который удаляется после прогона.
`DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0` обязателен рядом с изолированным
`DOTNET_CLI_HOME`, иначе .NET CLI дописывает пользовательский `PATH`.

## Производные файлы Godot

`.godot/`, `*.import` и `*.uid` не отслеживаются. После импорта проекта
`git status` остаётся пустым. Новый `.cs` или ресурс добавляется без `.uid`.

## CI

`.github/workflows/dotnet.yml` собирает и тестирует проекты без движка на
Ubuntu и прогоняет детерминированный сценарий. Godot остаётся локальной
проверкой, поэтому всё, что должно проверяться на каждом PR, живёт в сборке
без зависимости от движка.

## Откат

Удалить распакованный Godot, переменную `GODOT4_CONSOLE` и `.artifacts/`.
Репозиторий не ставит глобальных workloads и не меняет глобальный NuGet.Config.
