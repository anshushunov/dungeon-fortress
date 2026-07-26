# ADR 0004: Dev-only Ivan-MCP для редактора прототипа

- Статус: Accepted
- Дата: 2026-07-26
- Ответственные: владелец проекта и агенты разработки

## Контекст

Issue #4 подтвердил, что project-owned domain MCP безопасно и воспроизводимо
управляет детерминированной симуляцией. Для редактора проверялся
`IvanMurzak/Godot-MCP v0.19.1` с локальным
`GameDev-MCP-Server v9.2.0`.

Ivan существенно шире минимального read-only моста: он умеет работать со
сценами, узлами, ресурсами, скриптами, логами, запуском, скриншотами и C#
reflection. Это полезно для быстрой разработки тестовой игры.

Проверка также обнаружила, что `enabled:false` влияет на feature state и MCP
advertisement, но не является authorization boundary для direct REST
`/api/tools/<name>`. Отключённый `filesystem-list` был выполнен с HTTP 200, а
отключённый reflection tool дошёл до resolver. Поэтому первоначальный hard gate
Issue #4 не пройден.

После отчёта владелец проекта явно выбрал более простой путь: принять этот риск
для тестовой игры, оставить Ivan и не строить собственный editor bridge.

## Рассмотренные варианты

### Минимальный project-owned editor bridge

Дал бы небольшую проверяемую surface для логов, дерева, скриншота и диагностики.
Недостаток — отдельная реализация и поддержка при заметно меньших возможностях.

### Ivan-MCP с прежним строгим security gate

Невозможен на pin `v0.19.1`/`v9.2.0`: direct REST не соблюдает per-tool
`enabled` state. Token и OAuth аутентифицируют caller, но не ограничивают
авторизованному caller доступ к disabled tools.

### Ivan-MCP как явно доверенный dev-only инструмент

Сохраняет широкую автоматизацию редактора и минимизирует собственную
инфраструктуру. Цена — доверие локальному агенту и отсутствие per-tool
изоляции внутри Ivan server.

## Решение

Принять третий вариант для текущего прототипа:

- Ivan-MCP используется только локально и только при явном запуске
  `scripts/ivan-mcp.ps1 -Action Open`;
- addon `v0.19.1`, server `v9.2.0` и NuGet dependencies закреплены точными
  версиями и hashes;
- cloud, token/OAuth и non-loopback bind не используются;
- полный набор Ivan tools считается доверенной dev surface; документация не
  утверждает, что `enabled:false` является защитой;
- addon, server, generated MSBuild props, process state и профиль остаются
  производными и не входят в Git;
- чистый checkout, CI и обычный production build не содержат Ivan packages или
  addon source; перед любым export локальная установка удаляется командой
  `scripts/ivan-mcp.ps1 -Action Uninstall`;
- project-owned domain MCP остаётся отдельным stdio-процессом и не зависит от
  Ivan;
- собственный editor bridge не создаётся.

## Последствия

Агент получает гораздо более широкую автоматизацию Godot и может быстрее
менять тестовую игру. Одновременно любой доверенный локальный MCP client,
имеющий доступ к loopback server, потенциально может вызвать filesystem,
source и reflection tools. Отдельный worktree и Git являются механизмом
восстановления, а не security sandbox.

Ivan нельзя использовать в репозитории с секретами, на недоверенной машине или
как production/runtime dependency. Возврат к строгой модели потребует нового
ADR: upstream fix, изоляционный proxy/sandbox либо project-owned bridge.

## Проверка решения

- install проверяет exact SHA-256 release artifacts и locked NuGet graph;
- server слушает только `127.0.0.1`/`::1`, cloud environment пуст;
- editor выполняет version handshake `0.19.1`/API `2.0.0`;
- после C# compile error допускается tracked `Stop` → `Open`: hot reload может
  не выгрузить Ivan assemblies, но restart восстанавливает handshake и tools;
- project MCP configs содержат только loopback URL и не содержат secrets;
- uninstall останавливает candidate-owned processes и удаляет только
  документированные project-local/temp targets;
- полный `scripts/verify.ps1` остаётся зелёным до и после установки;
- независимый reviewer проверяет итоговый diff и runtime evidence.
