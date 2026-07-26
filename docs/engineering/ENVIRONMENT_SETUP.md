# Настройка окружения Godot/.NET spike

Статус: действует для bootstrap-spike из Issue #3

Дата проверки: 2026-07-26

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

## Единая проверка

Из корня репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Для явного одноразового override:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1 -GodotPath "<path-to-godot-console>"
```

Команда выполняет:

1. restore и `Release` build всего `DungeonFortress.sln`;
2. `dotnet test` для детерминизма и инвариантов симуляции;
3. два независимых процесса с одинаковыми seed/commands и побайтовое сравнение
   canonical JSON;
4. запуск с другим seed и проверку изменения checksum;
5. два измерения 1 000 лёгких агентов × 10 000 fixed ticks;
6. `Debug` build Godot-host и headless smoke;
7. проверку structured success event, process exit code и отсутствие любых
   строк `ERROR:` в выводе Godot.

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

## Отдельные команды

Pure .NET тесты:

```powershell
dotnet test .\tests\DungeonFortress.Simulation.Tests\DungeonFortress.Simulation.Tests.csproj -c Release
```

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
