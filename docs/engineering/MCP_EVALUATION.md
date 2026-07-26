# Оценка MCP и editor bridge

Статус: Phase B, блок 1 завершён
Дата проверки: 2026-07-26
Issue: [#4](https://github.com/anshushunov/dungeon-fortress/issues/4)

## Цель и границы

Phase B проверяет два независимых контура:

1. проектный domain MCP поверх детерминированного контракта симуляции из Issue
   #3;
2. development-only адаптер редактора Godot для процесса, диагностики, логов,
   деревьев и скриншота.

Domain MCP не содержит правил игры и не управляет редактором. Editor bridge не
получает доменные инструменты. Cloud, секреты, non-loopback transport,
произвольные shell/eval/reflection/source/filesystem операции не входят в
эксперимент.

## Сохранённый результат Phase A

Read-only сравнение дало следующий порядок:

1. **Trial** официального C# SDK
   `modelcontextprotocol/csharp-sdk v1.4.1`,
   commit `2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01`, для постоянного
   project-owned domain MCP.
2. **Trial** `IvanMurzak/Godot-MCP v0.19.1`, commit
   `34374fe8f6bb2bd1c46ba48d6004990d71718e4c`, только для editor/process
   операций и только с выключенным cloud.
3. **Assess** `slangwald/godot-mcp`, commit
   `95532c4eb632d61c9a3c7a281f1a8a2925042345`, без установки по умолчанию.
4. Owned editor fallback создаётся только после наблюдаемого провала Ivan и
   покрывает только проваленные требования.

Главные риски Ivan перед Trial: восстановление после ошибки компиляции C# и
возможность убрать broad reflection/script/filesystem/delete surface именно на
сервере, а не только client-side allowlist.

## Технологический радар

| Кандидат | Зрелость | Польза | Риск и откат | Действие |
|---|---|---|---|---|
| C# SDK v1.4.1 | stable release официального SDK | стандартный stdio MCP без нового языка/runtime | NuGet dependencies; удалить два проекта MCP, configs и lock files | Adopt для domain adapter |
| Ivan v0.19.1 | release внешнего Godot add-on | готовые editor/process/log/tree/screenshot операции | broad surface, lifecycle и compile recovery пока не доказаны; удалить candidate-owned add-on/config | Trial в блоке 2 |
| slangwald pinned commit | сравниваемый open-source bridge | возможный источник объяснения провала Ivan | отдельный Python/TCP контур расширяет стек | Assess, не устанавливать без доказательной необходимости |

Версии не обновлялись ради новизны: C# SDK и кандидаты закреплены brief Issue
#4. Перед изменением pins процедура из `TECH_RADAR.md` выполняется заново.

## Блок 1: project-owned domain MCP

### Контракт

Отдельный `net8.0` stdio process публикует ровно два инструмента:

- `bridge_status` — версия bridge, версии canonical/command schemas,
  проверенные root sentinels и точный список tools;
- `simulation_run` — bounded seed/agent/tick input, необязательный
  repository-relative command document, canonical UTF-8 JSON и SHA-256.

`simulation_snapshot` не добавлен: `simulation_run` уже возвращает canonical
snapshot и checksum, поэтому отдельное имя не имело бы отличимой семантики.

Оба инструмента объявлены read-only, non-destructive, idempotent и closed-world.
Server не принимает command line, имя метода, source или arbitrary path через
MCP. Допустимы:

- `1..5000` агентов;
- `0..100000` fixed ticks;
- command document до 1 MiB и до 10 000 команд;
- только relative `.json` path внутри validated repository root.

Root подтверждается `AGENTS.md`, `DungeonFortress.sln` и simulation `.csproj`.
Absolute path, traversal, отсутствующий файл, другая extension и
symlink/junction отвергаются. Parser command document перенесён в
`DungeonFortress.Simulation` и одинаков для CLI и MCP; правила симуляции и
canonical serializer не дублировались.

Stdout зарезервирован под MCP JSON-RPC. `Microsoft.Extensions.Logging.Console`
настроен с `LogToStandardErrorThreshold = Trace`, поэтому все host/SDK logs идут
в stderr. Закрытие stdin завершает process без listener.

### Dependencies, лицензии и hashes

| Артефакт | Pin | Лицензия | Проверка |
|---|---|---|---|
| `ModelContextProtocol` NuGet | `1.4.1`, repository commit `2b7fd35f...` | Apache-2.0 в `.nuspec` | SHA-256 `a15e95fdc480bf44d78e2fd56017531c23e464fb61d6163cddc626ac4f965ec8`; NuGet repository signature valid |
| `Microsoft.Extensions.Hosting` | `10.0.7` | MIT | direct exact `PackageReference`; transitive graph in lock |
| server lock | committed `packages.lock.json` | package-specific | SHA-256 `c5d2a3a2afcd73ab3e7bdb235dc65e6116bdbabfb1ccae83e58347a6f489fa99` |
| protocol-test lock | committed `packages.lock.json` | package-specific | SHA-256 `c28a2c02aa8fff4f0fba545ae796bd1d2fa92c2d12ce07f3fc0f864598411581` |

NuGet lock содержит `resolved` и SHA-512 `contentHash` для каждой прямой и
транзитивной зависимости. `dotnet restore --locked-mode` входит в локальную и
CI-проверку. Upstream repository `LICENSE` описывает переход исходного проекта
с MIT на Apache-2.0 и CC-BY-4.0 для документации; опубликованный package
`ModelContextProtocol 1.4.1` однозначно объявляет Apache-2.0.

Первичные источники:

- [C# SDK v1.4.1](https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v1.4.1);
- [точный upstream commit](https://github.com/modelcontextprotocol/csharp-sdk/commit/2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01);
- [package ModelContextProtocol 1.4.1](https://www.nuget.org/packages/ModelContextProtocol/1.4.1);
- [Codex MCP configuration](https://developers.openai.com/codex/mcp/);
- [Claude Code project-scoped MCP](https://code.claude.com/docs/en/mcp).

### Project configs

`.codex/config.toml` использует текущую stdio-форму
`[mcp_servers.<name>]`, project working directory, exact
`enabled_tools = ["bridge_status", "simulation_run"]` и не передаёт секреты.
Codex загружает project config только для trusted repository.

`.mcp.json` использует project-scoped `mcpServers`, transport `stdio` и
`${CLAUDE_PROJECT_DIR:-.}` из актуальной документации Claude Code. Server-side
surface всё равно ограничена двумя tools; client allowlist Codex является
дополнительной защитой.

Обе конфигурации требуют предварительной Release-сборки:

```powershell
dotnet restore .\tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj --locked-mode
dotnet build .\DungeonFortress.sln -c Release --no-restore
```

После этого client запускает `dotnet run --no-build --no-restore`; build output
не попадает в protocol stdout.

### Проверки и измерения блока 1

Сценарий: seed `424242`, 32 агента, 256 ticks,
`scenarios/smoke.commands.json`.

| Контур | Наблюдение | Результат |
|---|---|---|
| direct CLI | canonical snapshot | 1839 bytes, checksum `e65273aa102f4db01d2cf64ecc48b1556700544f5da0fe7c19378d1d089b6f6f` |
| raw JSON-RPC stdio | changed seed + 5 последовательных `tools/call` | both seeds byte-identical CLI; changed seed differs; repeated seed 5/5, median 1.966 ms, maximum 7.417 ms, failures 0 |
| official SDK client | process + `ListToolsAsync`/`CallToolAsync` | tools ровно 2; structured canonical bytes совпали с simulation API |
| Codex CLI 0.142.5 | fresh ephemeral read-only session, model `gpt-5.4` | project MCP вызван ровно один раз; checksum и полный canonical JSON совпали |
| Claude Code 2.1.219 | fresh `--strict-mcp-config` session | project MCP вызван ровно один раз; checksum и полный canonical JSON совпали |

Для пяти наблюдений сообщаются median, maximum и failure count; p95 не
утверждается. Первый Codex client probe с пользовательским default model
`gpt-5.6-sol` не дошёл до tool call: установленный CLI 0.142.5 потребовал
обновление. Повтор на поддерживаемом `gpt-5.4` прошёл; это client-version
ограничение, а не отказ MCP server.

Автоматические проверки:

```powershell
dotnet test .\tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-domain-mcp.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Проверяются exact closed-world schemas и bounds, runtime-отказ неизвестных
аргументов, safe error, sentinels, absolute/traversal/case-sensitive paths,
file symlink и directory link/junction, server-side tool list, official SDK
client, raw protocol, CLI/MCP bytes, пять измерений и clean shutdown. Полный
`verify.ps1` также оставляет действующими simulation и Godot 4.7.1 gates.

Независимый critical reviewer первоначально вернул `request-fixes`: нашёл
case-insensitive containment на Linux и permissive binding неизвестных полей.
Containment теперь учитывает семантику платформы, а protocol binding выполняет
явную server-side проверку перед simulation. Регрессионный набор расширен с 5
до 8 тестов; после исправлений полный verify прошёл повторно.

### Откат блока 1

1. Удалить project entries из `.codex/config.toml` и `.mcp.json`.
2. Удалить `tools/DungeonFortress.DomainMcp` и
   `tests/DungeonFortress.DomainMcp.Tests` из solution/CI.
3. Вернуть CLI к прежнему private parser, если domain bridge полностью
   откатывается.
4. Удалить `.artifacts/`; user-scope Codex/Claude data и approvals не
   изменяются и автоматически не удаляются.

## Блок 2: Ivan v0.19.1

Статус: ожидает изолированной установки и измерения. Результат будет добавлен
без изменения Phase A evidence и блока 1.
