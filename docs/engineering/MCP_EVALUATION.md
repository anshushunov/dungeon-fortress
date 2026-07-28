# Оценка MCP и editor bridge

Статус: Phase B завершён; принят dev-only Ivan-MCP по ADR 0004
Дата проверки: 2026-07-27
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

Отдельный `net8.0` stdio process после Issue #9 публикует ровно три инструмента:

- `bridge_status` — версия bridge, версии canonical/command schemas,
  проверенные root sentinels и точный список tools;
- `simulation_run` — bounded seed/agent/tick input, необязательный
  repository-relative command document, canonical UTF-8 JSON и SHA-256.
- `prototype_run` — gameplay schema v2, repository-relative fixture, bounded
  `0..1800` ticks, canonical state/event log и предбоевые observations.

`simulation_snapshot` не добавлен: `simulation_run` уже возвращает canonical
snapshot и checksum, поэтому отдельное имя не имело бы отличимой семантики.

Все инструменты объявлены read-only, non-destructive, idempotent и closed-world.
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
`enabled_tools = ["bridge_status", "prototype_run", "simulation_run"]` и не
передаёт секреты.
Codex загружает project config только для trusted repository.

`.mcp.json` использует project-scoped `mcpServers`, transport `stdio` и
`${CLAUDE_PROJECT_DIR:-.}` из актуальной документации Claude Code. Server-side
surface всё равно ограничена тремя tools; client allowlist Codex является
дополнительной защитой.

Обе конфигурации требуют предварительной Release-сборки:

```powershell
dotnet restore .\tests\DungeonFortress.DomainMcp.Tests\DungeonFortress.DomainMcp.Tests.csproj --locked-mode
dotnet build .\DungeonFortress.sln -c Release --no-restore
```

После этого client запускает `cmd /c scripts\domain-mcp-server.cmd`. Launcher
копирует Release build output в каталог сессии `.artifacts\domain-mcp-sessions\<id>`
и исполняет копию, поэтому `dotnet build DungeonFortress.sln` не встречает
открытый файл сервера (Issue #38). Ни launcher, ни build output не пишут в
protocol stdout; диагностика идёт в stderr. Подробности — в
`ENVIRONMENT_SETUP.md`.

### Проверки и измерения блока 1

Ниже сохранён исторический снимок блока 1 до Issue #9. Указанные в таблице
число tools, размеры, checksum и результаты относятся к контракту того блока и
не описывают текущую поверхность MCP.

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

### Текущее состояние после Issue #9

Текущий server публикует три инструмента: `bridge_status`, legacy
`simulation_run` и gameplay-v2 `prototype_run`. `prototype_run` до создания мира
валидирует весь command document, включая семантику будущих команд, и возвращает
canonical snapshot вместе с `economy`, `labor` и `stations`. Исторические
результаты блоков ниже остаются point-in-time evidence и намеренно не
переписываются под текущий контракт.

Чистый verification Issue #9: solution build без warnings/errors, simulation
tests 44/44, domain MCP tests 8/8, raw protocol observations 5/5,
`toolCount=3`, deterministic/load gates и Godot 4.7.1 headless smoke — green.

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

Статус: **Trial failed** на обязательном server-side security gate; candidate
не переносится в рабочий проект, процессы остановлены.

### Pins, происхождение и лицензии

| Артефакт | Exact pin | SHA-256 | Лицензия |
|---|---|---|---|
| `IvanMurzak/Godot-MCP` source | tag `v0.19.1`, commit `34374fe8f6bb2bd1c46ba48d6004990d71718e4c`, tree `7442002b6387071f5ad6aa5b9a9ea086bdb830a5` | release zip `271b74e58631a7c07c451b205f5808ce8edc42e70f3f0e7b28e5422b82e30e03` | Apache-2.0 |
| `GameDev-MCP-Server` | `9.2.0`, tag/commit `f273adef82cbff6b79c9f057baf562e7b7581242`, tree `5dc16c346a2e34ae37cb28c888a16a56cddf1e10` | `win-x64` zip `e0fe86cbebed4f376737086b781445c53f4bf5cf9111c923ba98da5c0bc4b69d`; official `SHA256SUMS` artifact `8f793347ac6def1a5ef167b1bb6c8b635db63eef0bec4f182c11b93053ffd4ef` | Apache-2.0 |
| `com.IvanMurzak.McpPlugin` | `7.3.0`, repository commit `378cd10a5138a8cea7c2afa4cb3503b0988cb66b` | nupkg `243828788fa5804a98c10c2c6914951d6d042d1e2a1f0975659e712ea55fdc7d` | Apache-2.0 |
| `com.IvanMurzak.McpPlugin.Common` | `7.3.0`, repository commit `378cd10a5138a8cea7c2afa4cb3503b0988cb66b` | nupkg `407e5f2f690e650f6bf2b5ae5e342eac11918662239b4be2490e60d1fca8be56` | Apache-2.0 |
| `com.IvanMurzak.ReflectorNet` | `5.3.2`, repository commit `1efa1769075d7642108bb524cf81c2d64f0472e6` | nupkg `b28bcc8b047f5c400671d89e4fa361e65f8bd45cb5fa8840c35c1b68a594133a` | Apache-2.0 |
| isolated dependency lock | current project SDK `Godot.NET.Sdk/4.7.1` + exact graph | `9842e060f3a2dd4ce4c1be65dafa29a5605222e8cc8c65bb7eed710846ef3fac` | package-specific |

GitHub Releases API сообщил addon asset size `850434` bytes и server asset
size `45451457` bytes. Локальные hashes совпали одновременно с API digest и
официальным `SHA256SUMS`. Распакованное дерево
`addons/godot_mcp` byte-content-equivalent дереву точного tag
(`git diff --no-index --exit-code` вернул `0`; line-ending warnings не меняют
содержимое diff).

SHA-256 проверенных Apache-2.0 license texts: Godot-MCP
`1eb85fc97224598dad1852b5d6483bbcf0aa8608790dcc657a5a2a761ae9c8c6`,
GameDev-MCP-Server
`16a88830789f178b5e065cfd15b055a9afbae01b0b2a15b14509876649f9c08f`,
NuGet packages
`b5a48f60b0d6058185988a5124799af356544f414d2ab5bef643e40129c08237`.

Первичные источники:

- [Godot-MCP v0.19.1](https://github.com/IvanMurzak/Godot-MCP/releases/tag/v0.19.1);
- [точный Godot-MCP commit](https://github.com/IvanMurzak/Godot-MCP/commit/34374fe8f6bb2bd1c46ba48d6004990d71718e4c);
- [GameDev-MCP-Server v9.2.0](https://github.com/IvanMurzak/GameDev-MCP-Server/releases/tag/v9.2.0);
- [точный server commit](https://github.com/IvanMurzak/GameDev-MCP-Server/commit/f273adef82cbff6b79c9f057baf562e7b7581242).

### Изолированная установка

Trial создавался только под `.artifacts/ivan-eval` из `git archive` commit
`5e92250`. Release addon был распакован в копию Godot-проекта, а server — в
отдельный project-local каталог. `APPDATA`, `LOCALAPPDATA` и
`NUGET_PACKAGES` для candidate process направлялись под isolated root; global
settings, caches и user-scope MCP configs не изменялись.

До запуска был подготовлен Custom-only config для
`http://127.0.0.1:29541`, `auth=none`, без token/cloud token и с выключенной
генерацией skills. Из статически подтверждённых 39 tools разрешались только:

- `ping`;
- `console-get-logs`;
- `editor-application-get-state`;
- `scene-list-opened`;
- `scene-get-data`;
- `screenshot-viewport`;
- `script-validate`.

Остальные 32 tools явно отключались, включая все
`reflection-*`, `filesystem-*`, `script-read/create/update/delete`,
`resource-*`, изменяющие `node-*`, editor set/select, clear/delete/save/move.
Source подтверждает, что persisted map применяется через server-visible
`IToolManager.SetToolEnabled`; пустой map, напротив, оставляет все 39 tools
включёнными. После runtime handshake `GET /api/tools` вернул ровно 39 tools:
семь выше имели `enabled:true`, остальные 32 — `enabled:false`.

### Compile и runtime handshake

На принятом `Godot.NET.Sdk/4.7.1` первый rebuild дал 26 errors: 23
неоднозначности `Godot.FileAccess`/`System.IO.FileAccess` и три `CS0618`,
которые project-wide `TreatWarningsAsErrors=true` повысил до ошибок.
Два узких consumer-side свойства устранили это без изменения candidate source
и без понижения SDK:

- `<Using Remove="System.IO" />` устранил 23 collisions;
- `<WarningsNotAsErrors>$(WarningsNotAsErrors);CS0618</WarningsNotAsErrors>`
  оставил три устаревших dock-вызова предупреждениями.

Проверочная команда:

```powershell
$env:NUGET_PACKAGES = "<repo>/.artifacts/ivan-eval/nuget-packages"
dotnet build <isolated>/DungeonFortress.Game.csproj `
  -c Debug --no-restore -t:Rebuild
```

Результат: `Build succeeded`, 3 warnings `CS0618`, 0 errors. Изолированный
headless editor затем подключился к loopback server: plugin `0.19.1`, API
`2.0.0`, environment `Godot 4.7.1-stable (official)`, version handshake
`Compatible: True`, status `Connected`.

### Security hard-gate failure

Фильтрация инструментов меняет manager state и `enabled` metadata, но не
является server-side authorization boundary для официального direct REST API. При
`filesystem-list` с `enabled:false` server всё равно исполнил:

```text
POST http://127.0.0.1:29541/api/tools/filesystem-list
HTTP 200
{"status":"success","structured":{"result":{"path":"res://", ...}}}
```

Аналогичный вызов отключённого `reflection-method-call` дошёл до resolver и
вернул ошибку `Method not found`, а не запрет disabled tool. Этот API является
официальной surface candidate CLI (`run-tool` всегда POST-ит в
`/api/tools/<name>`). В pin `GameDev-MCP-Server 9.2.0` не найден документированный
project-local switch, который отключает direct REST routes или заставляет их
уважать `enabled:false`. Официальный offline token mode требует shared secret
и лишь аутентифицирует caller: после аутентификации disabled-tool bypass
остаётся. OAuth имеет ту же проблему per-tool authorization и дополнительно
требует issuer/public URL. Оба варианта выходят за no-secret/offline scope;
loopback сам по себе не устраняет доступ для другого локального процесса.

Дополнительный gap найден статически: `script-validate` прямо отвергает `.cs`
и выдаёт structured diagnostics только для GDScript. C# errors предлагается
читать из project build/console, но это не гарантирует требуемые поля
`file/line/column/code/severity` через tool contract.

### Gate matrix Ivan

| Gate | Результат | Evidence |
|---|---|---|
| exact tag/artifact/hash/license | pass | pins и hashes выше; addon tree совпал с tag |
| project-local isolated install | pass | archive/addon/server/NuGet/profile только в `.artifacts/ivan-eval` |
| cloud off, no secret, loopback only | pass for executed scope | Custom `127.0.0.1:29541`, empty credentials; listeners только `127.0.0.1`/`::1` |
| compile on accepted Godot 4.7.1 SDK | pass with narrow eval waiver | full rebuild: 3 `CS0618` warnings, 0 errors; candidate source неизменён |
| plugin/server version handshake | pass | plugin `0.19.1`, API `2.0.0`, Godot `4.7.1`, compatible/connected |
| configured REST feature-state list | pass | `GET /api/tools`: 39 total, ровно 7 `enabled:true` |
| MCP `tools/list` advertisement | not executed | успешный MCP initialize/tools-list не выполнялся до security escalation |
| server-side minimum tool surface | **fail / escalation** | disabled `filesystem-list` исполнился через direct REST с HTTP 200; disabled reflection dispatch тоже не был запрещён |
| structured C# diagnostics | **fail** | tool explicitly supports `.gd` only |
| compile-error restore/reconnect 3/3 under 120 s | not executed | остановлено сразу после dangerous-surface trigger |
| start/stop 3/3, no orphan/listener | incomplete | один успешный start/handshake; после exact-PID stop процессов и listeners нет |
| cold/recovery logs and trees | incomplete | cold connection log получен; recovery/tree gate не продолжался |
| screenshot | not executed | остановлено сразу после dangerous-surface trigger |
| no non-loopback/cloud egress | pass for executed scope | runtime connections на candidate port только loopback; cloud env пуст |
| production export surface absent | not executed | остановлено сразу после dangerous-surface trigger |

Отдельная проверка release server binary выявила опасный UX edge:
`gamedev-mcp-server.exe --help` не завершился справкой, а запустил
unauthenticated `streamableHttp` listener. Лог подтвердил explicit default
`bind: loopback`, port `8080`; процесс был остановлен по exact executable path.
После остановки: isolated processes `0`, listeners на `8080` `0`. Основной
runtime trial на `29541` также остановлен по exact PID/path; после остановки
candidate processes `0`, listeners `0` (оставались только TCP `TIME_WAIT`).

### Uninstall и rollback evidence

После независимого review удалены только четыре verified project-local target:

- `.artifacts/ivan-eval`;
- `.artifacts/downloads/godot-mcp-v0.19.1`;
- `.artifacts/vendor/godot-mcp`;
- `.artifacts/vendor/gamedev-mcp-server`.

Все четыре пути после `Remove-Item -LiteralPath ... -Recurse -Force` имеют
`exists=False`. Block 1 evidence `.artifacts/vendor/csharp-sdk`, глобальные
настройки, user-scope caches/configs и исходный проект не удалялись. Повторный
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1`
завершился `verification_result status=ok`: solution build 0 warnings/errors,
simulation tests 8/8, domain MCP tests 8/8, raw protocol observations 5/5 с
clean shutdown, deterministic/load checks и Godot 4.7.1 headless smoke прошли.

### Решение блока 2

Ivan v0.19.1 не проходит hard gates и не принимается. Причина — не
совместимость сборки, а доступная даже при `enabled:false` dangerous
reflection/filesystem/source-capable REST surface. По explicit escalation
trigger Issue #4 работа останавливается с decision brief: нельзя молча
добавлять proxy/firewall/fork/auth boundary или начинать owned editor fallback,
пока пользователь не выберет новую архитектурную границу. Domain MCP из блока
1 остаётся отдельным и не зависит от этого решения.

## Блок 3: решение владельца и dev-only adoption

После escalation владелец проекта явно изменил границу решения: для текущей
тестовой игры broad Ivan surface принимается как доверенная dev-only
автоматизация. Собственный editor bridge не создаётся. Исторический провал
server-side minimum-surface gate выше сохраняется и не переименовывается в
успех; исключение оформлено ADR 0004.

Project-local launcher `scripts/ivan-mcp.ps1` выполняет пять действий:
`Install`, `Open`, `Status`, `Stop`, `Uninstall`. Addon и server извлекаются
только в игнорируемые производные каталоги, release archives проверяются по
SHA-256, а NuGet graph закреплён в
`config/ivan-mcp/packages.lock.json` (SHA-256
`9034d794d1938e87b3831644f50a8b13da3e74d7a1d122ba42b5b91a681da49e`
для нормализованного LF-содержимого Git).
Generated props импортируется Godot project только когда локальная установка
существует. Clean checkout, CI и обычная сборка не получают Ivan packages.

Запуск использует только `http://127.0.0.1:29541`/`::1`, `--auth none`,
пустой cloud URL и отдельный temp profile. Отсутствие auth допустимо только
потому, что loopback process считается доверенным и в репозитории нет секретов.
Codex и Claude project configs содержат только loopback URL; server нужно
предварительно явно запустить.

### Проверки принятого варианта

| Gate | Результат | Evidence |
|---|---|---|
| exact pins, hashes, licenses | pass | addon `0.19.1`, server `9.2.0`, packages `7.3.0`/`5.3.2`; hashes и Apache-2.0 сохранены выше |
| locked install/build | pass | locked restore; Godot 4.7.1 compile: 3 upstream `CS0618`, 0 errors; повторный build: 0 warnings/errors |
| protocol handshake | pass | MCP initialize `2025-03-26`, server `9.2.0.0`, session id получен; `tools/list` вернул 39 tools |
| editor handshake | pass | compatible API handshake; Godot `4.7.1-stable (official)`; `[Godot-MCP] connected` |
| scene/tree/log | pass | `Main.tscn`, root `Main`/`Node2D`, `res://Main.cs`; structured editor logs получены |
| lifecycle | pass | три последовательных Open/Stop цикла завершены; exact tracked PIDs; после stop listener отсутствует, кроме ожидаемого `TIME_WAIT` |
| play control | pass | main scene перешла `false → true → false`; running scene `res://Main.tscn` |
| screenshot | pass | оконный `screenshot-viewport` вернул PNG; headless предсказуемо вернул structured error об отсутствии GPU render |
| C# compile-error recovery | pass with restart | intentional `CS1026`/`CS1002` обнаружены; hot reload не выгрузил assemblies; после исправления `Stop` → `Open` восстановил build, handshake и tool call |
| runtime error buffer | known limitation | `runtime-errors-get` доступен, но game runtime не включает `WithRuntimeErrorCapture`; доменные ошибки остаются в structured simulation/CLI/MCP output |
| server-side minimum surface | waived by owner | все 39 tools считаются доверенной dev surface; `enabled:false` не является security boundary |
| loopback/no cloud/no secret | pass | server listeners `127.0.0.1` и `::1`; editor/server connections только loopback; cloud URL/token пусты |
| uninstall | pass | addon, generated props, server artifacts, temp profile, tracked processes и listener удалены; Windows long-path shader cache обработан |
| clean full verification | pass | solution build 0 warnings/errors; simulation tests 8/8; domain MCP tests 8/8; protocol observations 5/5; deterministic/load/Godot gates green |

### Независимый review loop

Read-only reviewers сначала вернули `request changes`. Launcher был исправлен
по их замечаниям:

- занятый `29541` теперь отклоняется до запуска, а readiness проверяет MCP
  identity/version порождённого server;
- server и editor получают очищенное allowlisted environment без ambient
  tokens, cloud/Redis/non-loopback overrides; poisoned-environment runtime
  сохранил только loopback listeners/connections;
- process state хранит PID, executable path и start time, поэтому PID reuse
  приводит к fail-closed, а не к остановке чужого Godot;
- любой частичный `Open` откатывает только созданные этим вызовом editor/server;
- `Open` завершается успешно только после bounded editor tool handshake;
- Ivan MSBuild props импортируется только для `Debug`; проверенный установленный
  `Release` graph содержит только Godot `4.7.1` packages.

После исправлений повторены compile-error → `Stop` → `Open`, poisoned environment,
`Open` → `Status` → `Uninstall` и полный clean `scripts/verify.ps1`.

### Использование и ограничение

Ivan разрешён только в отдельном trusted worktree без секретов. Перед export
или передачей рабочей копии выполняется:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\ivan-mcp.ps1 -Action Uninstall
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify.ps1
```

Возврат к строгой security-модели потребует нового ADR и отдельной границы
(upstream authorization fix, sandbox/proxy или owned bridge). Такой bridge в
Phase B намеренно не реализован.
