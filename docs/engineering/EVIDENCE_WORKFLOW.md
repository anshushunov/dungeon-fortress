# Воспроизводимые evidence bundle

Статус: действует
Дата проверки: 2026-07-30

Этот workflow собирает визуальное доказательство, но не превращает картинку в
источник истины. PNG нужен человеку; manifest связывает его с явными параметрами
кадра, структурированным результатом движка, каноническим checksum и SHA-256.

## Снять bundle

Из корня репозитория:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-evidence.ps1 `
  -SpecPath .\evidence\baseline.example.json `
  -OutputRoot evidence\baseline
```

`OutputRoot` всегда относителен к `.artifacts/`. Абсолютный путь и выход за
границу `.artifacts/` отвергаются до запуска Godot. Для каждого элемента
`captures` скрипт:

Проверка границы пути лексическая: она нормализует `.` и `..`, но не разрешает
junction или symbolic link. Поэтому `.artifacts/` должен оставаться доверенным
локальным каталогом без reparse points; каноническая защита от них — отдельное
усиление, не гарантия этого workflow.

1. запускает `scripts/run-game.ps1` с явными параметрами кадра;
2. повторяет тот же capture в отдельный PNG;
3. требует одинаковые fixture, seed, tick и canonical checksum;
4. сравнивает PNG побайтово и по SHA-256;
5. пишет `manifest.json` и `manifest.md`.

Пример создаст:

```text
.artifacts/evidence/baseline/
├── baseline-t1.png
├── baseline-t1.repeat.png
├── manifest.json
└── manifest.md
```

В Git эти файлы не попадают: весь `.artifacts/` игнорируется. `manifest.json`
содержит точную команду для основного и повторного кадра, все параметры spec,
commit, признак dirty worktree, fixture/seed/tick, canonical checksum, оба
SHA-256 и структурированный `view`. `manifest.md` — сокращённый handoff для PR.
Публикуемый финальный bundle снимается после commit и должен иметь
`sourceDirty: false`.

Явный `-GodotPath` используется только для поиска исполняемого файла на машине,
где снимается кадр. В команды `command` и `repeatCommand` manifest этот
машинно-зависимый абсолютный путь не попадает: воспроизведение снова разрешает
Godot стандартным способом или принимает локальный override отдельно.

По умолчанию dirty worktree отвергается до запуска Godot. Для локальной
диагностики можно явно добавить `-AllowDirtySource`; такой manifest получит
`reproducible: false` и `publishable: false` и не прикладывается к PR.

Проверить spec без сборки и запуска движка:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\capture-evidence.ps1 `
  -SpecPath .\evidence\baseline.example.json `
  -OutputRoot evidence\baseline `
  -ValidateOnly
```

Имя capture уникально и состоит из lower-case ASCII, цифр, `.`, `_` и `-`.
Pixel-affecting параметры обязательны: `tileSize`, `cameraZoom`,
`cameraPosition`, `uiScale` и `frameSize`. Неизвестное поле считается ошибкой,
чтобы опечатка не превратилась в незаметный default.

## Приложить PNG к PR

GitHub загружает attachment через web Markdown editor: PNG можно перетащить в
поле комментария или выбрать через кнопку attachment. При выборе файл
загружается сразу, а редактор получает anonymized URL. Это официальный
[workflow GitHub для attachments](https://docs.github.com/en/get-started/writing-on-github/working-with-advanced-formatting/attaching-files).

REST endpoint комментария принимает только строковое поле
[`body`](https://docs.github.com/en/rest/issues/comments#create-an-issue-comment);
отдельного binary attachment parameter у него нет. Поэтому автоматизация не
придумывает несуществующий API и не коммитит PNG:

1. открыть draft PR в авторизованном браузере;
2. вставить содержимое `manifest.md` в новый комментарий;
3. загрузить основной `*.png` через attachment control;
4. дождаться появления anonymized URL и только после этого отправить комментарий;
5. открыть preview/ссылку и сверить SHA-256 с manifest.

Repeat PNG остаётся локальным доказательством воспроизводимости; прикладывать
обе одинаковые картинки не нужно. Для public repository GitHub предупреждает,
что attachment доступен публично, поэтому кадр не должен содержать секреты или
приватные данные.

## Проверка заявленных контрольных сумм

Заявленные SHA-256 сверяются с **коммиченным** состоянием дерева, а не с рабочей
копией. По умолчанию скрипт разбирает только `evidence/*.json`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-claimed-sha256.ps1
```

Строки `docs/art/*.md` сканируются **только** с `-IncludeDocs` — без флага
документы не читаются вовсе:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-claimed-sha256.ps1 `
  -IncludeDocs
```

Скрипт разбирает пары `*Path`/`*Sha256` в evidence JSON и для каждой заявленной
суммы сообщает: совпадает с блобом (`blob-match`), совпадает только с рабочей
копией (`working-copy-only` — капкан переводов строк), не совпадает ни с чем
(`mismatch`) или файл не в дереве (`untracked`). Ненулевой exit код при
`mismatch` или `working-copy-only` для отслеживаемого файла.

Причина проверять по блобу: `.gitattributes` (`* text=auto eol=lf`) нормализует
переводы строк, и хеш рабочей копии расходится с деревом. Измерено в Issue #179:
заявленный хеш скрипта, снятый до `git add`, был опровергнут review при пересчёте
по коммиту.

### Чего проверка не покрывает

Сканирование `docs/` привязано к строке: хеш и путь должны стоять в **одной**
строке Markdown. Там, где путь и сумма разнесены по соседним строкам — как в
`docs/art/PROVENANCE_VERIFIABILITY.md`, — claim не находится, и молчание скрипта
не является подтверждением суммы. Ограничение известно и разбирается отдельной
задачей: расширение поиска за пределы строки даёт ложные срабатывания, а ложное
срабатывание здесь дороже пропуска.

Скрипт также не требует, чтобы у каждого документа была заявленная сумма. Он
проверяет те суммы, которые заявлены; полнота покрытия остаётся на review.

## Проверка заявленных выводов команд в теле PR

Если тело PR приводит команду и рядом с ней заявленное число, механическую часть
проверки можно выполнить скриптом:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\check-pr-claimed-output.ps1 `
  -BodyFile .\.artifacts\pr-body.md
```

Поддерживается только узкий формат: fenced-блок с командой и ближайшая непустая
строка сразу после него в виде `Expected: <значение>` или
`Заявлено: <значение>`:

````markdown
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\some-check.ps1
```
Expected: 42
````

Значение сравнивается буквально: вывод команды должен содержать текст после
двоеточия без нормализации чисел, процентов или разделителей тысяч.

Скрипт печатает JSONL по каждому блоку:

- `match` — команда выполнена, вывод содержит заявленное значение;
- `mismatch` — команда выполнена, вывод не содержит заявленное значение;
- `not-runnable` — команда не запущена, потому что у неё нет claim или она не
  проходит узкую safety policy.

`not-runnable` не является зелёным подтверждением claim. Это честная строка для
review: инструмент увидел команду, но не имеет права или формата, чтобы её
проверить. Молчание о числах вне формата `Expected:` / `Заявлено:` тоже не
подтверждает эти числа.

## Поиск команды-источника по логам инструмента

Когда «скрипт отсутствует в репозитории» или «число не воспроизводится», команду
сначала ищут в логах прошлых сессий инструмента, а не выдумывают заново. В Issue
#179 так нашёлся отсутствовавший `remove_chroma_key.py` вместе с исходниками —
вместо круга реверс-инжиниринга.

Для Codex логи лежат в `~/.codex/sessions`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\search-codex-sessions.ps1 `
  -Query "remove_chroma_key"
```

Скрипт печатает сессию, номер строки, тип события и сводку вызова инструмента.
Неразобранная строка — например, оборванная последняя строка активной сессии —
печатается как есть с `"eventType":"unparsed"`, и сканирование продолжается.

Правило универсально, а этот способ — нет: он читает локальный каталог
конкретного инструмента на конкретной машине. Если каталога нет, скрипт выходит
с кодом 2 и ничего не утверждает; агенту другого инструмента искать надо в его
собственных логах. Поэтому способ описан здесь, а `AGENTS.md` держит только само
обязательство и ссылку.

## Диагностика GitHub authentication

Проверка не печатает raw output `gh`/Git, URL с userinfo, токены или значения
credential helper:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\github-auth.ps1
```

Structured result различает:

- отсутствующий, просроченный или рабочий `gh auth`;
- наличие `origin` и credential helper без публикации их значений;
- rejected/missing credentials, network failure;
- `sandbox_credential_unavailable`, когда Windows credential manager настроен,
  но не смонтирован в Codex sandbox.

Write-доступ проверяется `git push --dry-run` в уникальную probe-ветку,
образованную из текущего commit. Dry-run проходит GitHub receive/auth path, но
не создаёт ref. Обычный `git ls-remote` намеренно не используется как
доказательство: public repository читается анонимно.

Нормальная первичная настройка выполняется в обычном PowerShell владельца:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\github-auth.ps1 `
  -Action Setup
```

Она вызывает официальный `gh auth login` и затем `gh auth setup-git`. Внутри
Codex sandbox `-Action Setup` намеренно отвергается: копирование PAT в аргумент,
файл репозитория или лог не является исправлением. Если внешний PowerShell
успешен, а sandbox сообщает `sandbox_credential_unavailable`, API-операции
выполняются GitHub connector'ом, а `git push` — разрешённой/elevated командой.
Это граница изоляции, а не сломанный credential helper.

`gh auth login --web` может показать владельцу короткоживущий pairing code.
Агент не должен захватывать, пересылать или вставлять этот код в логи, Issue или
PR; ввод выполняет сам владелец в обычном PowerShell/браузере.

## Точный расход токенов

Оценка до запуска и фактическое измерение — разные поля. Если клиент даёт goal
usage API, основной исполнитель и каждый review-субагент создают собственный
goal в начале задачи и завершают его только после всех обязательных проверок.
Финальный handoff пишет:

```yaml
token_usage:
  status: exact
  tokens: <целое число из завершённого goal>
  estimated: 180000-300000
```

Если конкретная среда не предоставляет счётчик, используется не число «на глаз»,
а явное:

```yaml
token_usage:
  status: unavailable
  reason: <какой API/ответ отсутствует>
  estimated: 180000-300000
```

Расход субагента не складывается с основным мысленно: каждый возвращает свою
точную метрику, а ведущий отдельно показывает main, review и сумму. Значение
снимается после завершения goal; промежуточное значение допустимо только в
статусе блока и помечается как `running`.
