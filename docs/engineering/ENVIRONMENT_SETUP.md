# Настройка окружения Godot/.NET spike

Статус: действует для bootstrap-spike из Issue #3

Дата проверки: 2026-07-27

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

Чтобы отключить domain MCP без удаления user-scope данных:

- Codex: установить `mcp_servers.dungeon_fortress_domain.enabled = false`
  в local/user override либо удалить project config вместе с изменением;
- Claude Code: не подтверждать project server либо удалить его entry из
  `.mcp.json` вместе с изменением;
- остановить client session; закрытие stdin штатно завершает stdio process;
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
